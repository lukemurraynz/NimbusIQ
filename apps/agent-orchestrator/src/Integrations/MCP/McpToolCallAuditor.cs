using System.Collections;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Atlas.AgentOrchestrator.Integrations.MCP;

public interface IMcpToolCallAuditor
{
    Task RecordAsync(McpToolCallAudit audit, CancellationToken cancellationToken = default);
}

public sealed record McpToolCallAudit
{
    public Guid CorrelationId { get; init; }
    public Guid? AnalysisRunId { get; init; }
    public Guid? ServiceGroupId { get; init; }
    public string ToolServer { get; init; } = string.Empty;
    public string ToolName { get; init; } = string.Empty;
    public object? ToolDefinition { get; init; }
    public object? Arguments { get; init; }
    public object? Result { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? TraceId { get; init; }
    public string? SpanId { get; init; }
    public string ActorId { get; init; } = "agent-orchestrator";
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public double? DurationMs { get; init; }
}

/// <summary>
/// Persists MCP tool calls into the shared audit_events table for compliance review.
/// </summary>
public sealed class McpToolCallAuditor : IMcpToolCallAuditor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxSerializedPayloadChars = 64_000;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<McpToolCallAuditor> _logger;

    public McpToolCallAuditor(NpgsqlDataSource dataSource, ILogger<McpToolCallAuditor> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task RecordAsync(McpToolCallAudit audit, CancellationToken cancellationToken = default)
    {
        var correlationId = audit.CorrelationId == Guid.Empty ? Guid.NewGuid() : audit.CorrelationId;
        var createdAt = audit.Timestamp.UtcDateTime;
        var payload = JsonSerializer.Serialize(new
        {
            audit.ToolServer,
            audit.ToolName,
            audit.ToolDefinition,
            Arguments = McpAuditPayloadRedactor.Sanitize(audit.Arguments),
            Result = McpAuditPayloadRedactor.Sanitize(audit.Result),
            audit.Success,
            Error = McpAuditPayloadRedactor.SanitizeText(audit.Error),
            audit.AnalysisRunId,
            audit.ServiceGroupId,
            audit.TraceId,
            audit.SpanId,
            audit.DurationMs
        }, JsonOptions);

        if (payload.Length > MaxSerializedPayloadChars)
        {
            payload = payload[..MaxSerializedPayloadChars] + "...(truncated)";
        }

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO audit_events
                  ("Id", "CorrelationId", "ActorType", "ActorId", "EventName", "EventPayload", "TraceId", "CreatedAt", "EventType", "EntityType", "EntityId", "Timestamp")
                VALUES
                  (@id, @correlationId, @actorType, @actorId, @eventName, @eventPayload, @traceId, @createdAt, @eventType, @entityType, @entityId, @timestamp)
                """;
            cmd.Parameters.AddWithValue("id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("correlationId", correlationId);
            cmd.Parameters.AddWithValue("actorType", "agent");
            cmd.Parameters.AddWithValue("actorId", audit.ActorId);
            cmd.Parameters.AddWithValue("eventName", "mcp.tool_invocation");
            cmd.Parameters.AddWithValue("eventPayload", payload);
            cmd.Parameters.AddWithValue("traceId", (object?)audit.TraceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("createdAt", createdAt);
            cmd.Parameters.AddWithValue("eventType", "mcp.tool_invocation");
            object entityType = audit.AnalysisRunId.HasValue ? "analysis_run" : DBNull.Value;
            object entityId = audit.AnalysisRunId.HasValue ? audit.AnalysisRunId.ToString()! : DBNull.Value;
            cmd.Parameters.AddWithValue("entityType", entityType);
            cmd.Parameters.AddWithValue("entityId", entityId);
            cmd.Parameters.AddWithValue("timestamp", createdAt);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist MCP tool audit event for {ToolName}", audit.ToolName);
        }
    }
}

/// <summary>
/// Sanitizes MCP audit payloads to reduce sensitive-data disclosure risk and control payload size growth.
/// </summary>
public static class McpAuditPayloadRedactor
{
    public const string RedactedValue = "[REDACTED]";
    public const string MaxDepthValue = "[MAX_DEPTH_REACHED]";
    public const string TruncatedItemsValue = "[TRUNCATED_ITEMS]";

    private const int MaxDepth = 5;
    private const int MaxCollectionItems = 25;
    private const int MaxStringLength = 512;

    private static readonly string[] SensitiveNameFragments =
    [
        "authorization",
        "token",
        "secret",
        "password",
        "passwd",
        "apikey",
        "api_key",
        "clientsecret",
        "connectionstring",
        "cookie",
        "setcookie",
        "sas",
        "signature"
    ];

    public static object? Sanitize(object? value)
        => SanitizeCore(value, null, 0);

    public static string? SanitizeText(string? value)
        => value is null ? null : Truncate(value);

    private static object? SanitizeCore(object? value, string? propertyName, int depth)
    {
        if (value is null)
        {
            return null;
        }

        if (IsSensitivePropertyName(propertyName))
        {
            return RedactedValue;
        }

        if (depth >= MaxDepth)
        {
            return MaxDepthValue;
        }

        return value switch
        {
            string text => Truncate(text),
            JsonElement json => SanitizeJsonElement(json, propertyName, depth + 1),
            IReadOnlyDictionary<string, object?> roDictionary => SanitizeDictionary(roDictionary, depth + 1),
            IDictionary<string, object?> dictionary => SanitizeDictionary(dictionary, depth + 1),
            IEnumerable<KeyValuePair<string, object?>> kvpEnumerable => SanitizeDictionary(kvpEnumerable, depth + 1),
            IEnumerable enumerable when value is not string => SanitizeEnumerable(enumerable, depth + 1),
            _ => SanitizeObject(value, depth + 1)
        };
    }

    private static Dictionary<string, object?> SanitizeDictionary(
        IEnumerable<KeyValuePair<string, object?>> entries,
        int depth)
    {
        var sanitized = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var count = 0;

        foreach (var (key, value) in entries)
        {
            if (count >= MaxCollectionItems)
            {
                sanitized["__truncatedItems"] = true;
                break;
            }

            sanitized[key] = SanitizeCore(value, key, depth);
            count++;
        }

        return sanitized;
    }

    private static List<object?> SanitizeEnumerable(IEnumerable values, int depth)
    {
        var sanitized = new List<object?>();
        var count = 0;

        foreach (var value in values)
        {
            if (count >= MaxCollectionItems)
            {
                sanitized.Add(TruncatedItemsValue);
                break;
            }

            sanitized.Add(SanitizeCore(value, null, depth));
            count++;
        }

        return sanitized;
    }

    private static object? SanitizeObject(object value, int depth)
    {
        try
        {
            var element = JsonSerializer.SerializeToElement(value);
            return SanitizeJsonElement(element, null, depth);
        }
        catch
        {
            return Truncate(value.ToString() ?? value.GetType().Name);
        }
    }

    private static object? SanitizeJsonElement(JsonElement element, string? propertyName, int depth)
    {
        if (IsSensitivePropertyName(propertyName))
        {
            return RedactedValue;
        }

        if (depth >= MaxDepth)
        {
            return MaxDepthValue;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Object => SanitizeJsonObject(element, depth + 1),
            JsonValueKind.Array => SanitizeJsonArray(element, depth + 1),
            JsonValueKind.String => Truncate(element.GetString() ?? string.Empty),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => Truncate(element.GetRawText())
        };
    }

    private static Dictionary<string, object?> SanitizeJsonObject(JsonElement element, int depth)
    {
        var sanitized = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var count = 0;

        foreach (var property in element.EnumerateObject())
        {
            if (count >= MaxCollectionItems)
            {
                sanitized["__truncatedItems"] = true;
                break;
            }

            sanitized[property.Name] = SanitizeJsonElement(property.Value, property.Name, depth);
            count++;
        }

        return sanitized;
    }

    private static List<object?> SanitizeJsonArray(JsonElement element, int depth)
    {
        var sanitized = new List<object?>();
        var count = 0;

        foreach (var item in element.EnumerateArray())
        {
            if (count >= MaxCollectionItems)
            {
                sanitized.Add(TruncatedItemsValue);
                break;
            }

            sanitized.Add(SanitizeJsonElement(item, null, depth));
            count++;
        }

        return sanitized;
    }

    private static bool IsSensitivePropertyName(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        var normalized = propertyName
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return SensitiveNameFragments.Any(fragment => normalized.Contains(fragment, StringComparison.Ordinal));
    }

    private static string Truncate(string text)
    {
        if (text.Length <= MaxStringLength)
        {
            return text;
        }

        return text[..MaxStringLength] + "...(truncated)";
    }
}
