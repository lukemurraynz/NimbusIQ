using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Atlas.AgentOrchestrator.Integrations.MCP;

public interface IMcpToolInvoker
{
  Task<IReadOnlyList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken = default);
  Task<CallToolResult?> CallToolAsync(
      string toolName,
      IReadOnlyDictionary<string, object?> arguments,
      CancellationToken cancellationToken = default);
}

/// <summary>
/// MCP client for Microsoft Learn documentation lookup. Provides grounding
/// for best-practice rules and agent recommendations with current WAF guidance.
/// Uses the official ModelContextProtocol SDK with response caching (24h TTL).
/// </summary>
public class LearnMcpClient : IAsyncDisposable
{
  private readonly LearnMcpOptions _options;
  private readonly ILogger<LearnMcpClient> _logger;
  private readonly ILoggerFactory _loggerFactory;
  private readonly IMcpToolCallAuditor? _toolCallAuditor;
  private readonly IMcpToolInvoker _toolInvoker;
  private static readonly ActivitySource ActivitySource = new("Atlas.AgentOrchestrator.MCP.Learn");
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
  {
    PropertyNameCaseInsensitive = true
  };

  // Thread-safe cache: (query+scope) → (result, expiry)
  private readonly ConcurrentDictionary<string, (List<LearnDocResult> Results, DateTimeOffset Expiry)> _cache = new();

  // Lazy MCP client so we only connect when first used.
  private readonly Lazy<Task<McpClient>>? _clientFactory;
  private readonly Lazy<Task<ToolCatalog>> _toolCatalogFactory;

  public LearnMcpClient(
      IOptions<LearnMcpOptions> options,
      ILogger<LearnMcpClient> logger,
      ILoggerFactory loggerFactory,
      IMcpToolCallAuditor? toolCallAuditor = null,
      IMcpToolInvoker? toolInvoker = null)
  {
    _options = options.Value;
    _logger = logger;
    _loggerFactory = loggerFactory;
    _toolCallAuditor = toolCallAuditor;

    if (toolInvoker is null)
    {
      _clientFactory = new Lazy<Task<McpClient>>(CreateClientAsync);
      _toolInvoker = new McpClientToolInvoker(_clientFactory);
    }
    else
    {
      _toolInvoker = toolInvoker;
    }

    _toolCatalogFactory = new Lazy<Task<ToolCatalog>>(LoadToolCatalogAsync);

    if (_options.EnableToolDiscovery)
    {
      _ = WarmToolCatalogAsync();
    }
  }

  /// <summary>
  /// Searches Microsoft Learn documentation for relevant guidance.
  /// Results are cached for the configured TTL to avoid rate limiting.
  /// </summary>
  public async Task<List<LearnDocResult>> SearchDocsAsync(
      string query,
      string? scope = null,
      string? resourceType = null,
      CancellationToken cancellationToken = default,
      ToolCallContext? toolCallContext = null)
  {
    using var activity = ActivitySource.StartActivity("LearnMcp.SearchDocs");
    activity?.SetTag("query", query);
    activity?.SetTag("scope", scope ?? "all");

    var cacheKey = $"{query}|{scope}|{resourceType}";
    if (_cache.TryGetValue(cacheKey, out var cached) && cached.Expiry > DateTimeOffset.UtcNow)
    {
      activity?.SetTag("cache.hit", true);
      return cached.Results;
    }

    try
    {
      var toolName = await ResolveToolNameAsync(_options.SearchToolName, "search", cancellationToken);
      if (string.IsNullOrWhiteSpace(toolName))
      {
        return [];
      }

      var args = new Dictionary<string, object>
      {
        ["query"] = query,
        ["scope"] = scope ?? "waf",
        ["resourceType"] = resourceType ?? string.Empty,
        ["maxResults"] = _options.MaxResults
      };

      var callResult = await CallToolAsync(toolName, args, cancellationToken, toolCallContext);
      var results = ParseSearchResults(callResult);

      _cache[cacheKey] = (results, DateTimeOffset.UtcNow.AddHours(_options.CacheTtlHours));

      activity?.SetTag("result.count", results.Count);
      _logger.LogInformation(
          "Learn MCP returned {Count} results for query: {Query}",
          results.Count,
          query);

      return results;
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
      activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
      _logger.LogWarning(ex,
          "Learn MCP search failed for query: {Query}; returning empty results",
          query);
      return [];
    }
  }

  /// <summary>
  /// Looks up guidance for a specific Azure resource type and WAF pillar.
  /// Convenience wrapper combining resource type and pillar into a targeted query.
  /// </summary>
  public async Task<List<LearnDocResult>> GetResourceGuidanceAsync(
      string resourceType,
      string pillar,
      CancellationToken cancellationToken = default,
      ToolCallContext? toolCallContext = null)
  {
    var query = $"Azure {resourceType} {pillar} best practices Well-Architected Framework";
    return await SearchDocsAsync(query, "waf", resourceType, cancellationToken, toolCallContext);
  }

  /// <summary>
  /// Fetches architecture pattern guidance for a specific scenario.
  /// </summary>
  public async Task<List<LearnDocResult>> GetArchitectureGuidanceAsync(
      string scenario,
      CancellationToken cancellationToken = default,
      ToolCallContext? toolCallContext = null)
  {
    return await SearchDocsAsync(scenario, "architecture-center", cancellationToken: cancellationToken, toolCallContext: toolCallContext);
  }

  /// <summary>
  /// Verifies that a documentation URL is current and returns updated metadata.
  /// Agents call this to replace hardcoded URLs with verified references.
  /// </summary>
  public async Task<LearnDocResult?> VerifyReferenceAsync(
      string url,
      CancellationToken cancellationToken = default,
      ToolCallContext? toolCallContext = null)
  {
    using var activity = ActivitySource.StartActivity("LearnMcp.VerifyReference");
    activity?.SetTag("url", url);

    try
    {
      var toolName = await ResolveToolNameAsync(_options.VerifyToolName, "verify", cancellationToken);
      if (string.IsNullOrWhiteSpace(toolName))
      {
        return null;
      }

      var args = new Dictionary<string, object>
      {
        ["url"] = url
      };

      var callResult = await CallToolAsync(toolName, args, cancellationToken, toolCallContext);
      return ParseVerifyResult(callResult);
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Learn MCP reference verification failed for URL: {Url}", url);
      return null;
    }
  }

  private sealed record ToolCatalog(IReadOnlyDictionary<string, McpClientTool> Tools, DateTimeOffset RetrievedAt);

  private sealed class McpClientToolInvoker : IMcpToolInvoker
  {
    private readonly Lazy<Task<McpClient>> _clientFactory;

    public McpClientToolInvoker(Lazy<Task<McpClient>> clientFactory)
    {
      _clientFactory = clientFactory;
    }

    public async Task<IReadOnlyList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
      var client = await _clientFactory.Value;
      var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
      return tools.ToList();
    }

    public async Task<CallToolResult?> CallToolAsync(
      string toolName,
      IReadOnlyDictionary<string, object?> arguments,
      CancellationToken cancellationToken = default)
    {
      var client = await _clientFactory.Value;
      return await client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);
    }
  }

  private async Task WarmToolCatalogAsync()
  {
    try
    {
      await GetToolCatalogAsync(CancellationToken.None);
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Learn MCP tool discovery failed during warm-up");
    }
  }

  private async Task<ToolCatalog> GetToolCatalogAsync(CancellationToken cancellationToken)
  {
    return await _toolCatalogFactory.Value;
  }

  private async Task<ToolCatalog> LoadToolCatalogAsync()
  {
    using var activity = ActivitySource.StartActivity("LearnMcp.ToolDiscovery");

    var tools = await _toolInvoker.ListToolsAsync(CancellationToken.None);
    var toolMap = tools
      .Where(tool => !string.IsNullOrWhiteSpace(tool.Name))
      .ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);

    activity?.SetTag("tool.count", toolMap.Count);
    _logger.LogInformation("Learn MCP tool catalog loaded ({Count} tools)", toolMap.Count);

    return new ToolCatalog(toolMap, DateTimeOffset.UtcNow);
  }

  private async Task<string?> ResolveToolNameAsync(string configuredName, string fallbackHint, CancellationToken cancellationToken)
  {
    var toolName = configuredName;

    if (!_options.EnableToolDiscovery)
    {
      return toolName;
    }

    var catalog = await GetToolCatalogAsync(cancellationToken);
    if (catalog.Tools.ContainsKey(toolName))
    {
      return toolName;
    }

    var fallback = catalog.Tools.Keys
      .FirstOrDefault(name => name.Contains(fallbackHint, StringComparison.OrdinalIgnoreCase));

    if (!string.IsNullOrWhiteSpace(fallback))
    {
      _logger.LogInformation("Learn MCP tool name resolved from discovery: {Requested} -> {Resolved}", toolName, fallback);
      return fallback;
    }

    _logger.LogError("Learn MCP tool {ToolName} not found in catalog", toolName);

    if (_options.FailClosedOnMissingTools)
    {
      throw new InvalidOperationException(
        $"Learn MCP tool '{toolName}' not registered on server {_options.ServerUrl}.");
    }

    return null;
  }

  private async Task<CallToolResult?> CallToolAsync(
      string toolName,
      IReadOnlyDictionary<string, object> arguments,
      CancellationToken cancellationToken,
      ToolCallContext? toolCallContext)
  {
    McpClientTool? tool = null;
    if (_options.EnableToolDiscovery)
    {
      var catalog = await GetToolCatalogAsync(cancellationToken);
      catalog.Tools.TryGetValue(toolName, out tool);
    }

    var nullableArgs = arguments.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value);
    var callStart = Stopwatch.GetTimestamp();
    var callResult = await _toolInvoker.CallToolAsync(toolName, nullableArgs, cancellationToken);
    var durationMs = Stopwatch.GetElapsedTime(callStart).TotalMilliseconds;

    if (_toolCallAuditor is not null)
    {
      var context = toolCallContext is null
        ? null
        : ToolCallContext.FromActivity(toolCallContext, Activity.Current);
      var traceId = context?.TraceId ?? Activity.Current?.TraceId.ToString();
      var spanId = context?.SpanId ?? Activity.Current?.SpanId.ToString();
      var correlationId = context?.CorrelationId ?? Guid.Empty;

      await _toolCallAuditor.RecordAsync(new McpToolCallAudit
      {
        CorrelationId = correlationId,
        AnalysisRunId = context?.AnalysisRunId,
        ServiceGroupId = context?.ServiceGroupId,
        ToolServer = _options.ServerUrl,
        ToolName = toolName,
        ToolDefinition = tool?.ProtocolTool,
        Arguments = arguments,
        Result = callResult?.StructuredContent ?? (object?)ExtractText(callResult),
        Success = callResult?.IsError != true,
        Error = callResult?.IsError == true ? ExtractText(callResult) : null,
        TraceId = traceId,
        SpanId = spanId,
        ActorId = context?.ActorId ?? "learn-mcp-client",
        DurationMs = durationMs
      }, cancellationToken);
    }

    return callResult;
  }

  private async Task<McpClient> CreateClientAsync()
  {
    _logger.LogInformation("Connecting to Learn MCP server at {ServerUrl}", _options.ServerUrl);

    var transportOptions = new HttpClientTransportOptions
    {
      Endpoint = new Uri(_options.ServerUrl)
    };

    var transport = new HttpClientTransport(transportOptions, loggerFactory: _loggerFactory);
    var clientOptions = new ModelContextProtocol.Client.McpClientOptions
    {
      ClientInfo = new Implementation
      {
        Name = "Atlas.AgentOrchestrator",
        Version = "1.0"
      }
    };

    return await McpClient.CreateAsync(transport, clientOptions, loggerFactory: _loggerFactory);
  }

  private static List<LearnDocResult> ParseSearchResults(CallToolResult? callResult)
  {
    if (callResult is null)
    {
      return [];
    }

    if (callResult.StructuredContent is not null)
    {
      var json = JsonSerializer.Serialize(callResult.StructuredContent, JsonOptions);
      return DeserializeResults(json);
    }

    var text = ExtractText(callResult);
    return string.IsNullOrWhiteSpace(text) ? [] : DeserializeResults(text);
  }

  private static LearnDocResult? ParseVerifyResult(CallToolResult? callResult)
  {
    if (callResult is null)
    {
      return null;
    }

    if (callResult.StructuredContent is not null)
    {
      var json = JsonSerializer.Serialize(callResult.StructuredContent, JsonOptions);
      return DeserializeSingleResult(json);
    }

    var text = ExtractText(callResult);
    return string.IsNullOrWhiteSpace(text) ? null : DeserializeSingleResult(text);
  }

  private static List<LearnDocResult> DeserializeResults(string json)
  {
    try
    {
      using var doc = JsonDocument.Parse(json);
      if (doc.RootElement.ValueKind == JsonValueKind.Array)
      {
        return JsonSerializer.Deserialize<List<LearnDocResult>>(doc.RootElement.GetRawText(), JsonOptions) ?? [];
      }

      if (doc.RootElement.ValueKind == JsonValueKind.Object &&
          doc.RootElement.TryGetProperty("results", out var resultsEl) &&
          resultsEl.ValueKind == JsonValueKind.Array)
      {
        return JsonSerializer.Deserialize<List<LearnDocResult>>(resultsEl.GetRawText(), JsonOptions) ?? [];
      }
    }
    catch
    {
      // Swallow parse errors and fall through.
    }

    return [];
  }

  private static LearnDocResult? DeserializeSingleResult(string json)
  {
    try
    {
      using var doc = JsonDocument.Parse(json);
      if (doc.RootElement.ValueKind == JsonValueKind.Object)
      {
        return JsonSerializer.Deserialize<LearnDocResult>(doc.RootElement.GetRawText(), JsonOptions);
      }

      if (doc.RootElement.ValueKind == JsonValueKind.Object &&
          doc.RootElement.TryGetProperty("result", out var resultEl) &&
          resultEl.ValueKind == JsonValueKind.Object)
      {
        return JsonSerializer.Deserialize<LearnDocResult>(resultEl.GetRawText(), JsonOptions);
      }
    }
    catch
    {
      // Ignore parse failures.
    }

    return null;
  }

  private static string? ExtractText(CallToolResult? callResult)
  {
    if (callResult?.Content is null)
    {
      return null;
    }

    var parts = new List<string>();
    foreach (var item in callResult.Content)
    {
      if (item is TextContentBlock textItem && !string.IsNullOrWhiteSpace(textItem.Text))
      {
        parts.Add(textItem.Text);
      }
    }

    return parts.Count > 0 ? string.Join("\n", parts) : null;
  }

  public async ValueTask DisposeAsync()
  {
    if (_clientFactory is not null && _clientFactory.IsValueCreated)
    {
      try
      {
        var client = await _clientFactory.Value;
        if (client is IAsyncDisposable disposable)
          await disposable.DisposeAsync();
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Error disposing Learn MCP client");
      }
    }
    else if (_toolInvoker is IAsyncDisposable disposableInvoker)
    {
      try
      {
        await disposableInvoker.DisposeAsync();
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Error disposing Learn MCP tool invoker");
      }
    }
  }
}

/// <summary>
/// Result from a Learn MCP documentation search.
/// </summary>
public class LearnDocResult
{
  public string Title { get; set; } = string.Empty;
  public string Url { get; set; } = string.Empty;
  public string Summary { get; set; } = string.Empty;
  public DateTimeOffset? LastUpdated { get; set; }
  public List<string> RelevantExcerpts { get; set; } = [];
  public string? Scope { get; set; }
  public string? ResourceType { get; set; }
}

/// <summary>
/// Configuration for the Learn MCP client.
/// </summary>
public class LearnMcpOptions
{
  public const string SectionName = "LearnMcp";

  public string ServerUrl { get; set; } = "https://learn.microsoft.com/mcp";
  public bool Enabled { get; set; } = true;
  public int MaxResults { get; set; } = 5;
  public int CacheTtlHours { get; set; } = 24;

  /// <summary>
  /// Enable MCP tool discovery and catalog caching on startup.
  /// </summary>
  public bool EnableToolDiscovery { get; set; } = true;

  /// <summary>
  /// When true, fail closed if required tools are missing from discovery.
  /// </summary>
  public bool FailClosedOnMissingTools { get; set; } = true;

  /// <summary>
  /// Tool name to use for Learn MCP searches.
  /// </summary>
  public string SearchToolName { get; set; } = "search";

  /// <summary>
  /// Tool name to use for Learn MCP reference verification.
  /// </summary>
  public string VerifyToolName { get; set; } = "verify";
}
