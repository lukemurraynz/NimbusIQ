using Atlas.AgentOrchestrator.Integrations.Auth;
using Atlas.AgentOrchestrator.Integrations.Azure;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;

namespace Atlas.AgentOrchestrator.Orchestration;

/// <summary>
/// Queries Azure Resource Graph via the ARM REST API using the system-assigned managed identity.
/// The system-assigned MI holds subscription-scope Reader, giving it access to cross-subscription resource queries.
/// Implements IResourceGraphClient so OrphanDetectionService can use it without a separate adapter.
/// </summary>
public class AzureResourceGraphClient : IResourceGraphClient
{
    private const string ResourceGraphApiVersion = "2021-03-01";
    private const string ArmResource = "https://management.azure.com/";

    private readonly HttpClient _httpClient;
    private readonly ManagedIdentityCredentialProvider _credentialProvider;
    private readonly ILogger<AzureResourceGraphClient> _logger;
    private readonly string? _defaultSubscriptionId;

    public AzureResourceGraphClient(
        HttpClient httpClient,
        ManagedIdentityCredentialProvider credentialProvider,
        IConfiguration configuration,
        ILogger<AzureResourceGraphClient> logger)
    {
        _httpClient = httpClient;
        _credentialProvider = credentialProvider;
        _logger = logger;
        _defaultSubscriptionId = configuration["AzureResourceGraph:SubscriptionId"];
        _httpClient.BaseAddress = new Uri(ArmResource);
    }

    /// <summary>
    /// Executes a KQL Resource Graph query and returns a typed result.
    /// Uses caller-supplied subscriptions when provided, otherwise falls back to the configured default.
    /// </summary>
    public async Task<ResourceGraphResult> QueryAsync(
        string query,
        IEnumerable<string>? subscriptions = null,
        CancellationToken cancellationToken = default)
    {
        var doc = await QueryResourcesAsync(query, subscriptions, cancellationToken);
        return ResourceGraphResult.FromJsonDocument(doc);
    }

    /// <summary>
    /// Low-level query method returning raw JSON.
    /// </summary>
    public async Task<JsonDocument> QueryResourcesAsync(
        string query,
        IEnumerable<string>? subscriptions = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return JsonDocument.Parse("{}");
        }

        // Resolve subscription list: prefer caller-supplied, fall back to configured default
        var subscriptionList = subscriptions?.ToList();
        if (subscriptionList is null or { Count: 0 })
        {
            subscriptionList = _defaultSubscriptionId is not null
                ? [_defaultSubscriptionId]
                : [];
        }

        try
        {
            var credential = _credentialProvider.GetSystemAssignedCredential();
            var tokenContext = new TokenRequestContext([ArmResource + ".default"]);
            var accessToken = await credential.GetTokenAsync(tokenContext, cancellationToken);

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"providers/Microsoft.ResourceGraph/resources?api-version={ResourceGraphApiVersion}");

            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Token);

            var body = new { subscriptions = subscriptionList, query };
            request.Content = JsonContent.Create(body);

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "Resource Graph query failed with {StatusCode}: {Error}",
                    (int)response.StatusCode,
                    errorBody.Length > 500 ? errorBody[..500] : errorBody);
                return JsonDocument.Parse("{}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonDocument.Parse(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Resource Graph query execution failed; returning empty result");
            return JsonDocument.Parse("{}");
        }
    }
}

