using System.Text.Json;

namespace Atlas.AgentOrchestrator.Integrations.Azure;

/// <summary>
/// Abstraction over Azure Resource Graph to allow testable orphan detection.
/// Typed wrapper avoids dynamic dispatch on JsonElement, which fails at runtime.
/// </summary>
public interface IResourceGraphClient
{
    Task<ResourceGraphResult> QueryAsync(
        string query,
        IEnumerable<string>? subscriptions = null,
        CancellationToken cancellationToken = default);
}

public sealed class ResourceGraphResult
{
    public static readonly ResourceGraphResult Empty = new();

    public IReadOnlyList<ResourceGraphRow> Data { get; init; } = [];
    public int TotalRecords { get; init; }

    public static ResourceGraphResult FromJsonDocument(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("data", out var dataElement))
            return Empty;

        var rows = dataElement.EnumerateArray()
            .Select(e => new ResourceGraphRow(e))
            .ToList();

        var totalRecords = doc.RootElement.TryGetProperty("totalRecords", out var total)
            ? total.GetInt32()
            : rows.Count;

        return new ResourceGraphResult { Data = rows, TotalRecords = totalRecords };
    }
}

/// <summary>
/// Strongly-typed row from a Resource Graph query result.
/// Property access via named helpers avoids dynamic dispatch surprises.
/// </summary>
public sealed class ResourceGraphRow
{
    private readonly JsonElement _element;

    public ResourceGraphRow(JsonElement element) => _element = element;

    public string? GetString(string property)
    {
        if (!_element.TryGetProperty(property, out var val))
            return null;
        return val.ValueKind == JsonValueKind.Null ? null : val.ToString();
    }

    public decimal GetDecimal(string property)
    {
        if (!_element.TryGetProperty(property, out var val))
            return 0m;
        return val.TryGetDecimal(out var d) ? d : 0m;
    }

    public bool GetBool(string property)
    {
        if (!_element.TryGetProperty(property, out var val))
            return false;
        return val.ValueKind == JsonValueKind.True;
    }

    public string GetRawJson() => _element.GetRawText();
}
