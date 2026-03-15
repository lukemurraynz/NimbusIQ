using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atlas.ControlPlane.Application.Recommendations;
using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Auth;
using Azure.Core;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Atlas.ControlPlane.Api.Services;

public sealed class McpLearnGroundingClient : IRecommendationGroundingClient, IAsyncDisposable
{
    private readonly McpLearnGroundingOptions _options;
    private readonly ILogger<McpLearnGroundingClient> _logger;
    private readonly TokenCredential _credential;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lazy<Task<McpClient>> _clientFactory;
    private static readonly ActivitySource ActivitySource = new("Atlas.ControlPlane.MCP.LearnGrounding");

    public McpLearnGroundingClient(
        IOptions<McpLearnGroundingOptions> options,
        ManagedIdentityCredentialProvider credentialProvider,
        ILogger<McpLearnGroundingClient> logger,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _credential = credentialProvider.GetCredential();
        _logger = logger;
        _loggerFactory = loggerFactory;
        _clientFactory = new Lazy<Task<McpClient>>(CreateClientAsync);
    }

    public async Task<GroundingEnrichmentResult?> TryGroundAsync(
        Atlas.ControlPlane.Domain.Entities.Recommendation recommendation,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return null;
        }

        var resourceType = TryExtractResourceType(recommendation.ResourceId);
        var query = BuildGroundingQuery(recommendation.Category, resourceType);
        var cacheKey = $"{recommendation.Category}|{resourceType}|{query}";

        if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            return cached.Result;
        }

        using var activity = ActivitySource.StartActivity("LearnMcp.GroundRecommendation");
        activity?.SetTag("recommendation.category", recommendation.Category);
        activity?.SetTag("recommendation.resourceType", resourceType);

        try
        {
            var client = await _clientFactory.Value;
            var searchToolName = await ResolveSearchToolNameAsync(client, cancellationToken);
            if (string.IsNullOrWhiteSpace(searchToolName))
            {
                _logger.LogWarning("Learn MCP grounding skipped because no compatible search tool was discovered.");
                return null;
            }

            var args = new Dictionary<string, object?>
            {
                ["query"] = query,
                ["scope"] = "waf",
                ["resourceType"] = resourceType ?? string.Empty,
                ["maxResults"] = _options.MaxResults
            };

            var callResult = await client.CallToolAsync(searchToolName, args, cancellationToken: cancellationToken);
            var citations = ParseCitations(callResult, query);
            if (citations.Count == 0)
            {
                _logger.LogWarning("Learn MCP grounding returned no citations for query '{Query}' using tool '{ToolName}'.", query, searchToolName);
                return null;
            }

            var recency = CalculateRecencyScore(citations);
            var quality = CalculateGroundingQuality(citations, resourceType);

            var result = new GroundingEnrichmentResult(
                Citations: citations,
                Provenance: new GroundingProvenance(
                    GroundingSource: "learn_mcp",
                    GroundingQuery: query,
                    GroundingTimestampUtc: DateTime.UtcNow,
                    GroundingToolRunId: ExtractToolRunId(callResult),
                    GroundingQuality: quality,
                    GroundingRecencyScore: recency),
                EvidenceUrls: citations.Select(c => c.Url).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

            _cache[cacheKey] = new CacheEntry(result, DateTime.UtcNow.AddHours(_options.CacheTtlHours));

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Learn MCP grounding failed for recommendation {RecommendationId}", recommendation.Id);
            return null;
        }
    }

    private async Task<string?> ResolveSearchToolNameAsync(McpClient client, CancellationToken cancellationToken)
    {
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            var toolNames = tools
                .Where(t => !string.IsNullOrWhiteSpace(t.Name))
                .Select(t => t.Name)
                .ToList();

            if (toolNames.Any(n => string.Equals(n, _options.SearchToolName, StringComparison.OrdinalIgnoreCase)))
            {
                return _options.SearchToolName;
            }

            var fallback = toolNames.FirstOrDefault(n =>
                string.Equals(n, "microsoft_docs_search", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("docs_search", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("search", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(fallback))
            {
                _logger.LogInformation(
                    "Learn MCP tool name resolved from discovery: requested '{RequestedTool}' -> using '{ResolvedTool}'.",
                    _options.SearchToolName,
                    fallback);
                return fallback;
            }

            _logger.LogWarning("Learn MCP tool discovery found no compatible search tool. Available tools: {Tools}", string.Join(", ", toolNames));
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Learn MCP tool discovery failed; proceeding with configured tool name '{ToolName}'.", _options.SearchToolName);
            return _options.SearchToolName;
        }
    }

    private static string BuildGroundingQuery(string category, string? resourceType)
    {
        if (!string.IsNullOrWhiteSpace(resourceType))
        {
            return $"Azure Well-Architected {category} guidance for {resourceType}";
        }

        return $"Azure Well-Architected {category} guidance";
    }

    private static List<GroundedCitation> ParseCitations(CallToolResult? callResult, string query)
    {
        if (callResult?.Content is null)
        {
            return [];
        }

        string? rawText;
        if (callResult.StructuredContent is not null)
        {
            rawText = JsonSerializer.Serialize(callResult.StructuredContent);
        }
        else
        {
            rawText = string.Join("\n", callResult.Content
                .OfType<TextContentBlock>()
                .Select(t => t.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t)));
        }

        if (string.IsNullOrWhiteSpace(rawText))
        {
            return [];
        }

        var citations = new List<GroundedCitation>();

        try
        {
            using var doc = JsonDocument.Parse(rawText);
            IEnumerable<JsonElement> items = [];
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                items = doc.RootElement.EnumerateArray();
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                     doc.RootElement.TryGetProperty("results", out var resultsElement) &&
                     resultsElement.ValueKind == JsonValueKind.Array)
            {
                items = resultsElement.EnumerateArray();
            }

            foreach (var item in items)
            {
                var url = item.TryGetProperty("url", out var u)
                    ? u.GetString()
                    : item.TryGetProperty("contentUrl", out var cu)
                        ? cu.GetString()
                        : null;
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var title = item.TryGetProperty("title", out var t) ? t.GetString() : null;
                var summary = item.TryGetProperty("summary", out var s)
                    ? s.GetString()
                    : item.TryGetProperty("content", out var c)
                        ? c.GetString()
                        : null;
                DateTime? lastUpdated = null;
                if (item.TryGetProperty("lastUpdated", out var lu) && DateTime.TryParse(lu.GetString(), out var parsed))
                {
                    lastUpdated = parsed.ToUniversalTime();
                }

                var snippet = string.IsNullOrWhiteSpace(summary) ? title ?? url : summary;
                if (snippet.Length > 500)
                {
                    snippet = snippet[..500];
                }

                citations.Add(new GroundedCitation(
                    Url: url,
                    Title: title ?? "Microsoft Learn guidance",
                    SnippetHash: HashSnippet(snippet),
                    RetrievedAtUtc: DateTime.UtcNow,
                    Source: "learn_mcp",
                    Query: query,
                    ToolRunId: null,
                    SourceLastUpdatedUtc: lastUpdated));
            }
        }
        catch
        {
            var urls = rawText.Split(['\n', ',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Where(token => token.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || token.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5);

            foreach (var url in urls)
            {
                citations.Add(new GroundedCitation(
                    Url: url,
                    Title: "Microsoft Learn guidance",
                    SnippetHash: HashSnippet(url),
                    RetrievedAtUtc: DateTime.UtcNow,
                    Source: "learn_mcp",
                    Query: query,
                    ToolRunId: null,
                    SourceLastUpdatedUtc: null));
            }
        }

        return citations;
    }

    private static string HashSnippet(string snippet)
    {
        var bytes = Encoding.UTF8.GetBytes(snippet);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? ExtractToolRunId(CallToolResult? callResult)
    {
        _ = callResult;
        return null;
    }

    private static double CalculateRecencyScore(IReadOnlyList<GroundedCitation> citations)
    {
        if (citations.Count == 0)
        {
            return 0;
        }

        var withDates = citations.Where(c => c.SourceLastUpdatedUtc.HasValue).ToList();
        if (withDates.Count == 0)
        {
            return 0.65;
        }

        var avgDays = withDates
            .Select(c => (DateTime.UtcNow - c.SourceLastUpdatedUtc!.Value).TotalDays)
            .Average();

        if (avgDays <= 30) return 1.0;
        if (avgDays <= 90) return 0.85;
        if (avgDays <= 180) return 0.7;
        return 0.5;
    }

    private static double CalculateGroundingQuality(IReadOnlyList<GroundedCitation> citations, string? resourceType)
    {
        if (citations.Count == 0)
        {
            return 0.0;
        }

        var urlQuality = citations.Count(c => c.Url.Contains("learn.microsoft.com", StringComparison.OrdinalIgnoreCase)) / (double)citations.Count;
        var titleQuality = citations.Count(c => !string.IsNullOrWhiteSpace(c.Title)) / (double)citations.Count;
        var resourceSpecific = string.IsNullOrWhiteSpace(resourceType)
            ? 0.7
            : citations.Any(c => c.Query.Contains(resourceType, StringComparison.OrdinalIgnoreCase)) ? 1.0 : 0.6;

        return Math.Round(Math.Max(0.0, Math.Min(1.0, (urlQuality * 0.4) + (titleQuality * 0.3) + (resourceSpecific * 0.3))), 3);
    }

    private async Task<McpClient> CreateClientAsync()
    {
        var httpClient = new HttpClient();
        if (_options.UseManagedIdentityAuth)
        {
            var tokenContext = new TokenRequestContext(["https://management.azure.com/.default"]);
            var token = await _credential.GetTokenAsync(tokenContext, CancellationToken.None);
            httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
        }

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(_options.ServerUrl) },
            httpClient,
            loggerFactory: _loggerFactory,
            ownsHttpClient: true);

        var clientOptions = new ModelContextProtocol.Client.McpClientOptions
        {
            ClientInfo = new Implementation
            {
                Name = "Atlas.ControlPlane",
                Version = "1.0"
            }
        };

        return await McpClient.CreateAsync(transport, clientOptions, loggerFactory: _loggerFactory);
    }

    private static string? TryExtractResourceType(string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return null;
        }

        var segments = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var providerIndex = Array.FindIndex(segments, s => s.Equals("providers", StringComparison.OrdinalIgnoreCase));
        if (providerIndex < 0 || providerIndex + 2 >= segments.Length)
        {
            return null;
        }

        return $"{segments[providerIndex + 1]}/{segments[providerIndex + 2]}";
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

    private sealed record CacheEntry(GroundingEnrichmentResult Result, DateTime ExpiresAtUtc);
}

public sealed class McpLearnGroundingOptions
{
    public const string SectionName = "LearnMcp";

    public bool Enabled { get; set; } = true;
    public string ServerUrl { get; set; } = "https://learn.microsoft.com/api/mcp";
    public string SearchToolName { get; set; } = "microsoft_docs_search";
    public int MaxResults { get; set; } = 5;
    public int CacheTtlHours { get; set; } = 24;
    public bool UseManagedIdentityAuth { get; set; } = false;
}
