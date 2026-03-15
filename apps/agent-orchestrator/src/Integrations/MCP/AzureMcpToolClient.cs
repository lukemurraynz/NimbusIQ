using System.Diagnostics;
using System.Text.Json;
using Atlas.AgentOrchestrator.Integrations.Auth;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Atlas.AgentOrchestrator.Integrations.MCP;

/// <summary>
/// Azure MCP tool client built on the official <c>ModelContextProtocol</c> SDK.
/// Connects to an Azure MCP server (https://github.com/Azure/azure-mcp) using
/// stdio transport by default (Learn .NET guidance) and optionally SSE for
/// explicitly configured remote MCP endpoints.
/// and exposes typed wrappers for the Azure cost-management and resource tools.
/// </summary>
public class AzureMcpToolClient : IAsyncDisposable
{
    private readonly AzureMcpOptions _options;
    private readonly ILogger<AzureMcpToolClient> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TokenCredential _credential;
    private readonly IMcpToolCallAuditor? _toolCallAuditor;
    private static readonly ActivitySource ActivitySource = new("Atlas.AgentOrchestrator.MCP.Azure");

    // Lazy McpClient so we only connect when first used.
    private readonly Lazy<Task<McpClient>> _clientFactory;
    private readonly Lazy<Task<ToolCatalog>> _toolCatalogFactory;

    public AzureMcpToolClient(
        IOptions<AzureMcpOptions> options,
        ILogger<AzureMcpToolClient> logger,
        ILoggerFactory loggerFactory,
        ManagedIdentityCredentialProvider credentialProvider,
        IMcpToolCallAuditor? toolCallAuditor = null)
    {
        _options = options.Value;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _toolCallAuditor = toolCallAuditor;

        _credential = credentialProvider.GetCredential();

        _clientFactory = new Lazy<Task<McpClient>>(CreateClientAsync);
        _toolCatalogFactory = new Lazy<Task<ToolCatalog>>(LoadToolCatalogAsync);

        _logger.LogInformation(
            "AzureMcpToolClient initialised. Transport: {TransportMode}, ServerUrl: {ServerUrl}",
            string.IsNullOrWhiteSpace(_options.ServerUrl) ? "stdio" : "http",
            string.IsNullOrWhiteSpace(_options.ServerUrl) ? "(stdio)" : _options.ServerUrl);

        if (_options.EnableToolDiscovery)
        {
            _ = WarmToolCatalogAsync();
        }
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Calls the Azure MCP <c>cost_query</c> tool to retrieve cost data for the
    /// given subscription and time range.
    /// </summary>
    public async Task<Dictionary<string, object>> QueryCostAsync(
        string subscriptionId,
        DateTime startDate,
        DateTime endDate,
        CancellationToken cancellationToken = default,
        ToolCallContext? toolCallContext = null)
    {
        using var activity = ActivitySource.StartActivity("AzureMcp.QueryCost");
        activity?.SetTag("subscription.id", subscriptionId);
        activity?.SetTag("date.start", startDate.ToString("yyyy-MM-dd"));
        activity?.SetTag("date.end", endDate.ToString("yyyy-MM-dd"));

        try
        {
            if (_options.EnableToolDiscovery)
            {
                var catalog = await GetToolCatalogAsync(cancellationToken);
                if (!catalog.Tools.ContainsKey(_options.CostQueryToolName))
                {
                    _logger.LogInformation(
                        "Azure MCP cost tool {ToolName} not found in catalog. Skipping cost query.",
                        _options.CostQueryToolName);

                    return new Dictionary<string, object>
                    {
                        ["status"] = "unavailable",
                        ["message"] = $"Azure MCP tool '{_options.CostQueryToolName}' is not available on the connected server.",
                        ["subscriptionId"] = subscriptionId
                    };
                }
            }

            var arguments = new Dictionary<string, object>
            {
                ["subscriptionId"] = subscriptionId,
                ["startDate"] = startDate.ToString("yyyy-MM-dd"),
                ["endDate"] = endDate.ToString("yyyy-MM-dd"),
                ["granularity"] = "Monthly",
                ["groupBy"] = "ResourceType"
            };

            var result = await CallToolAsync(
                _options.CostQueryToolName,
                arguments,
                cancellationToken,
                toolCallContext);

            activity?.SetTag("result.status", "success");
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogWarning(ex,
                "Azure MCP cost_query failed for subscription {SubscriptionId}; returning empty result",
                subscriptionId);

            return new Dictionary<string, object>
            {
                ["status"] = "unavailable",
                ["message"] = $"Azure MCP server unavailable: {ex.Message}",
                ["subscriptionId"] = subscriptionId
            };
        }
    }

    /// <summary>
    /// Lists available Azure MCP tools on the configured server.
    /// </summary>
    public async Task<IList<McpClientTool>> ListToolsAsync(
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("AzureMcp.ListTools");

        try
        {
            var catalog = await GetToolCatalogAsync(cancellationToken);
            activity?.SetTag("tool.count", catalog.Tools.Count);
            _logger.LogInformation("Listed {ToolCount} tools from Azure MCP server", catalog.Tools.Count);
            return catalog.Tools.Values.ToList();
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogWarning(ex, "Failed to list tools from Azure MCP server");
            return Array.Empty<McpClientTool>();
        }
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private sealed record ToolCatalog(IReadOnlyDictionary<string, McpClientTool> Tools, DateTimeOffset RetrievedAt);

    private async Task WarmToolCatalogAsync()
    {
        try
        {
            await GetToolCatalogAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure MCP tool discovery failed during warm-up");
        }
    }

    private async Task<ToolCatalog> GetToolCatalogAsync(CancellationToken cancellationToken)
    {
        return await _toolCatalogFactory.Value;
    }

    private async Task<ToolCatalog> LoadToolCatalogAsync()
    {
        using var activity = ActivitySource.StartActivity("AzureMcp.ToolDiscovery");

        var client = await _clientFactory.Value;
        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var toolMap = tools
            .Where(tool => !string.IsNullOrWhiteSpace(tool.Name))
            .ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);

        activity?.SetTag("tool.count", toolMap.Count);
        _logger.LogInformation("Azure MCP tool catalog loaded ({Count} tools)", toolMap.Count);

        return new ToolCatalog(toolMap, DateTimeOffset.UtcNow);
    }

    private async Task<McpClientTool?> EnsureToolAsync(string toolName, CancellationToken cancellationToken)
    {
        var catalog = await GetToolCatalogAsync(cancellationToken);
        if (catalog.Tools.TryGetValue(toolName, out var tool))
        {
            return tool;
        }

        _logger.LogError(
            "Azure MCP tool {ToolName} not found in catalog (available: {ToolCount})",
            toolName,
            catalog.Tools.Count);

        if (_options.FailClosedOnMissingTools)
        {
            throw new InvalidOperationException(
                $"Azure MCP tool '{toolName}' not registered on server {_options.ServerUrl}.");
        }

        return null;
    }

    private async Task<Dictionary<string, object>> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object> arguments,
        CancellationToken cancellationToken,
        ToolCallContext? toolCallContext)
    {
        var client = await _clientFactory.Value;
        var tool = _options.EnableToolDiscovery
            ? await EnsureToolAsync(toolName, cancellationToken)
            : null;

        // Use the typed MCP client to invoke tool calls.
        var nullableArgs = arguments.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value);

        var callStart = Stopwatch.GetTimestamp();
        var callResult = await client.CallToolAsync(
            toolName,
            nullableArgs,
            cancellationToken: cancellationToken);
        var durationMs = Stopwatch.GetElapsedTime(callStart).TotalMilliseconds;

        // Convert the MCP content items back to a plain dictionary
        var result = new Dictionary<string, object>();
        if (callResult?.Content != null)
        {
            var parts = new List<string>();
            foreach (var item in callResult.Content)
            {
                if (item is TextContentBlock textItem && !string.IsNullOrWhiteSpace(textItem.Text))
                    parts.Add(textItem.Text);
            }

            if (parts.Count > 0)
                result["text"] = string.Join("\n", parts);
        }

        result["toolName"] = toolName;
        result["status"] = callResult?.IsError == true ? "error" : "success";

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
                Result = result,
                Success = callResult?.IsError != true,
                Error = callResult?.IsError == true && result.TryGetValue("text", out var errorText)
                    ? errorText?.ToString()
                    : null,
                TraceId = traceId,
                SpanId = spanId,
                ActorId = context?.ActorId ?? "azure-mcp-tool-client",
                DurationMs = durationMs
            }, cancellationToken);
        }

        return result;
    }

    private async Task<McpClient> CreateClientAsync()
    {
        if (string.IsNullOrWhiteSpace(_options.ServerUrl))
        {
            _logger.LogInformation(
                "Connecting to Azure MCP server via stdio. Command: {Command} Args: {Args}",
                _options.StdioCommand,
                JsonSerializer.Serialize(_options.StdioArguments));

            var stdioTransport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = _options.StdioCommand,
                Arguments = _options.StdioArguments,
                Name = "Azure MCP"
            });

            var stdioClientOptions = new ModelContextProtocol.Client.McpClientOptions
            {
                ClientInfo = new ModelContextProtocol.Protocol.Implementation
                {
                    Name = "Atlas.AgentOrchestrator",
                    Version = "1.0"
                }
            };

            return await McpClient.CreateAsync(stdioTransport, stdioClientOptions, loggerFactory: _loggerFactory);
        }

        _logger.LogInformation("Connecting to Azure MCP server at {ServerUrl}", _options.ServerUrl);

        // Obtain a bearer token for the Azure MCP server
        var tokenContext = new TokenRequestContext(
            ["https://management.azure.com/.default"]);
        var tokenResult = await _credential.GetTokenAsync(tokenContext, CancellationToken.None);

        // Build an HttpClient with auth header injected
        var httpClient = new System.Net.Http.HttpClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenResult.Token);

        // Use SSE transport — the Azure MCP server exposes an SSE endpoint
        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = new Uri(_options.ServerUrl)
        };

        var transport = new HttpClientTransport(transportOptions, httpClient, loggerFactory: _loggerFactory, ownsHttpClient: true);
        var clientOptions = new ModelContextProtocol.Client.McpClientOptions
        {
            ClientInfo = new ModelContextProtocol.Protocol.Implementation
            {
                Name = "Atlas.AgentOrchestrator",
                Version = "1.0"
            }
        };

        return await McpClient.CreateAsync(transport, clientOptions, loggerFactory: _loggerFactory);
    }

    // -------------------------------------------------------------------------
    // IAsyncDisposable
    // -------------------------------------------------------------------------

    public async ValueTask DisposeAsync()
    {
        if (_clientFactory.IsValueCreated)
        {
            try
            {
                var client = await _clientFactory.Value;
                if (client is IAsyncDisposable disposable)
                    await disposable.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing Azure MCP client");
            }
        }
    }
}

/// <summary>
/// Configuration for the Azure MCP tool client.
/// </summary>
public class AzureMcpOptions
{
    public const string SectionName = "AzureMcp";

    /// <summary>
    /// Base URL of the Azure MCP server (SSE endpoint).
    /// When empty, the client uses stdio transport and starts Azure MCP via
    /// <see cref="StdioCommand"/> and <see cref="StdioArguments"/>.
    /// </summary>
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>
    /// Whether to enable Azure MCP tool integration through the native
    /// ModelContextProtocol SDK client.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Command used to start Azure MCP when <see cref="ServerUrl"/> is empty.
    /// </summary>
    public string StdioCommand { get; set; } = "azmcp";

    /// <summary>
    /// Command arguments used to start Azure MCP in stdio mode.
    /// Starts the Azure MCP server using the official wrapper command.
    /// </summary>
    public string[] StdioArguments { get; set; } = ["server", "start"];

    /// <summary>
    /// Enable MCP tool discovery and catalog caching on startup.
    /// </summary>
    public bool EnableToolDiscovery { get; set; } = true;

    /// <summary>
    /// When true, fail closed if required tools are missing from discovery.
    /// </summary>
    public bool FailClosedOnMissingTools { get; set; } = false;

    /// <summary>
    /// Tool name to use for cost queries.
    /// </summary>
    public string CostQueryToolName { get; set; } = "cost_query";

    /// <summary>
    /// Number of days of historical cost data to request when calling the
    /// Azure MCP <c>cost_query</c> tool.  Defaults to 90 days (3 months).
    /// </summary>
    public int CostLookbackDays { get; set; } = 90;
}
