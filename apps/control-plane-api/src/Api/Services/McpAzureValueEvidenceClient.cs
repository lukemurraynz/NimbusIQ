using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using Atlas.ControlPlane.Application.ValueTracking;
using Atlas.ControlPlane.Infrastructure.Auth;
using Azure.Core;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Atlas.ControlPlane.Api.Services;

public sealed class McpAzureValueEvidenceClient : IAzureMcpValueEvidenceClient, IAsyncDisposable
{
    private readonly McpAzureValueEvidenceOptions _options;
    private readonly TokenCredential _credential;
    private readonly ILogger<McpAzureValueEvidenceClient> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Lazy<Task<McpClient>> _clientFactory;
    private readonly ConcurrentDictionary<string, string> _toolNameCache = new(StringComparer.OrdinalIgnoreCase);

    public McpAzureValueEvidenceClient(
        IOptions<McpAzureValueEvidenceOptions> options,
        ManagedIdentityCredentialProvider credentialProvider,
        ILogger<McpAzureValueEvidenceClient> logger,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _credential = credentialProvider.GetCredential();
        _logger = logger;
        _loggerFactory = loggerFactory;
        _clientFactory = new Lazy<Task<McpClient>>(CreateClientAsync);
    }

    public async Task<McpCostEvidence?> TryGetCostEvidenceAsync(
        string subscriptionId,
        string? resourceGroup,
        DateTime monthStartUtc,
        DateTime utcNow,
        int elapsedDaysInCurrentMonth,
        int previousMonthDays,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return null;
        }

        try
        {
            var client = await _clientFactory.Value;

            var currentCost = await QueryCostAsync(client, subscriptionId, monthStartUtc, utcNow, resourceGroup, cancellationToken);
            var previousMonthStart = monthStartUtc.AddMonths(-1);
            var previousMonthEnd = monthStartUtc.AddTicks(-1);
            var previousCost = await QueryCostAsync(client, subscriptionId, previousMonthStart, previousMonthEnd, resourceGroup, cancellationToken);

            var baselineMtd = previousMonthDays > 0
                ? Math.Round((previousCost / previousMonthDays) * elapsedDaysInCurrentMonth, 2)
                : 0m;

            var estimatedSavings = Math.Round(baselineMtd - currentCost, 2);
            var anomalyCount = await TryGetAnomalyCountAsync(client, subscriptionId, cancellationToken);
            var advisorLinks = await TryGetAdvisorCorrelationCountAsync(client, subscriptionId, resourceGroup, cancellationToken);
            var activityCorrelation = await TryGetActivityCorrelationCountAsync(client, subscriptionId, resourceGroup, cancellationToken);

            return new McpCostEvidence(
                MonthToDateCostUsd: currentCost,
                BaselineMonthToDateCostUsd: baselineMtd,
                EstimatedMonthlySavingsUsd: estimatedSavings,
                AnomalyCount: anomalyCount,
                AdvisorRecommendationLinks: advisorLinks,
                ActivityLogCorrelationEvents: activityCorrelation,
                LastQueriedAtUtc: utcNow,
                Source: "Azure MCP");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure MCP value evidence failed for subscription {SubscriptionId}", subscriptionId);
            return null;
        }
    }

    private async Task<decimal> QueryCostAsync(
        McpClient client,
        string subscriptionId,
        DateTime fromUtc,
        DateTime toUtc,
        string? resourceGroup,
        CancellationToken cancellationToken)
    {
        var args = new Dictionary<string, object?>
        {
            ["subscriptionId"] = subscriptionId,
            ["startDate"] = fromUtc.ToString("yyyy-MM-dd"),
            ["endDate"] = toUtc.ToString("yyyy-MM-dd"),
            ["granularity"] = "Monthly",
            ["groupBy"] = "ResourceType"
        };

        if (!string.IsNullOrWhiteSpace(resourceGroup))
        {
            args["resourceGroup"] = resourceGroup;
        }

        var toolName = await ResolveToolNameAsync(
            client,
            _options.CostQueryToolName,
            ["cost_query", "cost", "billing"],
            cancellationToken);
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return 0m;
        }

        var result = await client.CallToolAsync(toolName, args, cancellationToken: cancellationToken);
        var text = ExtractTextResult(result);
        return ExtractCostAmount(text);
    }

    // Known Azure Cost Management JSON field names that represent a cost total.
    private static readonly HashSet<string> KnownCostFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "totalCost", "pretaxCost", "costInBillingCurrency", "cost", "amount", "total"
    };

    private static decimal ExtractCostAmount(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return 0m;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawText);
            // Prefer a well-known named cost field over the first arbitrary number.
            var named = ExtractNamedCostField(doc.RootElement);
            if (named.HasValue)
            {
                return Math.Max(0m, Math.Round(named.Value, 2));
            }

            var candidate = ExtractFirstNumber(doc.RootElement);
            if (candidate.HasValue)
            {
                return Math.Max(0m, Math.Round(candidate.Value, 2));
            }
        }
        catch
        {
            // best-effort text parsing below
        }

        var matches = Regex.Matches(rawText, @"-?\d+(?:\.\d+)?");
        foreach (Match match in matches)
        {
            if (decimal.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            {
                return Math.Round(parsed, 2);
            }
        }

        return 0m;
    }

    /// <summary>
    /// Searches the JSON element for a property whose name is a known Azure Cost Management
    /// cost field (e.g. "totalCost", "pretaxCost"). Returns the first matching numeric value,
    /// or <c>null</c> if none found.
    /// </summary>
    private static decimal? ExtractNamedCostField(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            // Check direct properties first.
            foreach (var prop in element.EnumerateObject())
            {
                if (KnownCostFieldNames.Contains(prop.Name) &&
                    prop.Value.TryGetDecimal(out var val) && val > 0)
                {
                    return val;
                }
            }

            // Recurse into nested objects.
            foreach (var prop in element.EnumerateObject())
            {
                var nested = ExtractNamedCostField(prop.Value);
                if (nested.HasValue)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            // Sum named cost fields across array items (handles breakdown rows).
            var sum = 0m;
            var found = false;
            foreach (var item in element.EnumerateArray())
            {
                var itemCost = ExtractNamedCostField(item);
                if (itemCost.HasValue)
                {
                    sum += itemCost.Value;
                    found = true;
                }
            }

            return found ? sum : null;
        }

        return null;
    }

    private static decimal? ExtractFirstNumber(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                if (element.TryGetDecimal(out var value))
                {
                    return value;
                }
                return null;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var valueInArray = ExtractFirstNumber(item);
                    if (valueInArray.HasValue)
                    {
                        return valueInArray;
                    }
                }
                return null;

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var valueInObject = ExtractFirstNumber(property.Value);
                    if (valueInObject.HasValue)
                    {
                        return valueInObject;
                    }
                }
                return null;

            default:
                return null;
        }
    }

    private async Task<int> TryGetAnomalyCountAsync(McpClient client, string subscriptionId, CancellationToken cancellationToken)
    {
        try
        {
            var args = new Dictionary<string, object?>
            {
                ["subscriptionId"] = subscriptionId,
                ["query"] = "cost anomalies"
            };

            var toolName = await ResolveToolNameAsync(
                client,
                _options.CostAnomalyToolName,
                ["anomal", "cost"],
                cancellationToken);
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return 0;
            }

            var result = await client.CallToolAsync(toolName, args, cancellationToken: cancellationToken);
            var text = ExtractTextResult(result);
            var matches = Regex.Matches(text, @"\d+");
            foreach (Match match in matches)
            {
                if (int.TryParse(match.Value, out var parsed) && parsed >= 0)
                {
                    return parsed;
                }
            }
        }
        catch
        {
            // ignore, best effort
        }

        return 0;
    }

    private async Task<int> TryGetAdvisorCorrelationCountAsync(
        McpClient client,
        string subscriptionId,
        string? resourceGroup,
        CancellationToken cancellationToken)
    {
        try
        {
            var args = new Dictionary<string, object?>
            {
                ["subscriptionId"] = subscriptionId
            };

            if (!string.IsNullOrWhiteSpace(resourceGroup))
            {
                args["resourceGroup"] = resourceGroup;
            }

            var toolName = await ResolveToolNameAsync(
                client,
                _options.AdvisorRecommendationsToolName,
                ["advisor", "recommend"],
                cancellationToken);
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return 0;
            }

            var result = await client.CallToolAsync(toolName, args, cancellationToken: cancellationToken);
            var text = ExtractTextResult(result);

            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            var idMatches = Regex.Matches(text, @"/providers/microsoft\.advisor/recommendations/[A-Za-z0-9\-]+", RegexOptions.IgnoreCase);
            if (idMatches.Count > 0)
            {
                return idMatches
                    .Select(m => m.Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
            }

            var genericMatches = Regex.Matches(text, @"\badvisor\b", RegexOptions.IgnoreCase);
            return genericMatches.Count;
        }
        catch
        {
            return 0;
        }
    }

    private async Task<int> TryGetActivityCorrelationCountAsync(
        McpClient client,
        string subscriptionId,
        string? resourceGroup,
        CancellationToken cancellationToken)
    {
        try
        {
            var args = new Dictionary<string, object?>
            {
                ["subscriptionId"] = subscriptionId
            };

            if (!string.IsNullOrWhiteSpace(resourceGroup))
            {
                args["resourceGroup"] = resourceGroup;
            }

            var toolName = await ResolveToolNameAsync(
                client,
                _options.ActivityLogToolName,
                ["activity", "log"],
                cancellationToken);
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return 0;
            }

            var result = await client.CallToolAsync(toolName, args, cancellationToken: cancellationToken);
            var text = ExtractTextResult(result);
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            var deploymentMatches = Regex.Matches(
                text,
                @"(microsoft\.resources/deployments/write|/write|/delete|deploy)",
                RegexOptions.IgnoreCase);

            if (deploymentMatches.Count == 0)
            {
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(resourceGroup) &&
                !text.Contains(resourceGroup, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (!text.Contains(subscriptionId, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            return deploymentMatches.Count;
        }
        catch
        {
            // ignore, best effort
        }

        return 0;
    }

    private static string ExtractTextResult(CallToolResult? result)
    {
        if (result?.StructuredContent is not null)
        {
            return JsonSerializer.Serialize(result.StructuredContent);
        }

        return string.Join("\n", result?.Content?.OfType<TextContentBlock>().Select(t => t.Text) ?? []);
    }

    private async Task<string?> ResolveToolNameAsync(
        McpClient client,
        string configuredToolName,
        string[] fallbackHints,
        CancellationToken cancellationToken)
    {
        if (_toolNameCache.TryGetValue(configuredToolName, out var cachedToolName))
        {
            return cachedToolName;
        }

        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            var toolNames = tools
                .Where(t => !string.IsNullOrWhiteSpace(t.Name))
                .Select(t => t.Name)
                .ToList();

            var resolved = toolNames.FirstOrDefault(n => string.Equals(n, configuredToolName, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(resolved))
            {
                resolved = toolNames.FirstOrDefault(n => fallbackHints.Any(h => n.Contains(h, StringComparison.OrdinalIgnoreCase)));
            }

            if (!string.IsNullOrWhiteSpace(resolved))
            {
                _toolNameCache[configuredToolName] = resolved;
                if (!string.Equals(configuredToolName, resolved, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "Azure MCP tool name resolved from discovery: requested '{RequestedTool}' -> using '{ResolvedTool}'.",
                        configuredToolName,
                        resolved);
                }

                return resolved;
            }

            _logger.LogWarning(
                "Azure MCP tool discovery found no compatible tool for '{RequestedTool}'. Available tools: {Tools}",
                configuredToolName,
                string.Join(", ", toolNames));
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure MCP tool discovery failed while resolving '{RequestedTool}'.", configuredToolName);
            return configuredToolName;
        }
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
                    Name = "Atlas.ControlPlane.ValueEvidence",
                    Version = "1.0"
                }
            };

            return await McpClient.CreateAsync(stdioTransport, stdioClientOptions, loggerFactory: _loggerFactory);
        }

        _logger.LogInformation("Connecting to Azure MCP server at {ServerUrl}", _options.ServerUrl);

        // Use a token-refreshing handler so the Bearer token is renewed on every request,
        // preventing 401s after the initial token expires (~1 hour for managed identity).
        var httpClient = new HttpClient(new TokenRefreshingHandler(_credential));

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(_options.ServerUrl) },
            httpClient,
            loggerFactory: _loggerFactory,
            ownsHttpClient: true);

        var clientOptions = new ModelContextProtocol.Client.McpClientOptions
        {
            ClientInfo = new Implementation
            {
                Name = "Atlas.ControlPlane.ValueEvidence",
                Version = "1.0"
            }
        };

        return await McpClient.CreateAsync(transport, clientOptions, loggerFactory: _loggerFactory);
    }

    /// <summary>
    /// Obtains a fresh managed identity token on every HTTP request, preventing
    /// 401 errors after the initial token expires (~1 hour).
    /// </summary>
    private sealed class TokenRefreshingHandler(TokenCredential credential) : DelegatingHandler(new HttpClientHandler())
    {
        private static readonly TokenRequestContext TokenContext =
            new(["https://management.azure.com/.default"]);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var token = await credential.GetTokenAsync(TokenContext, cancellationToken);
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_clientFactory.IsValueCreated)
        {
            var client = await _clientFactory.Value;
            if (client is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
        }
    }
}

public sealed class McpAzureValueEvidenceOptions
{
    public const string SectionName = "AzureMcp";

    public bool Enabled { get; set; } = true;
    public string ServerUrl { get; set; } = string.Empty;
    public string StdioCommand { get; set; } = "azmcp";
    public string[] StdioArguments { get; set; } = ["server", "start", "--mode", "all"];
    public string CostQueryToolName { get; set; } = "cost_query";
    public string CostAnomalyToolName { get; set; } = "cost_anomaly";
    public string AdvisorRecommendationsToolName { get; set; } = "advisor_recommendations_list";
    public string ActivityLogToolName { get; set; } = "azureResources_getAzureActivityLog";
}
