using Atlas.AgentOrchestrator.Integrations.Auth;
using Azure.Core;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atlas.AgentOrchestrator.Orchestration;

/// <summary>
/// Queries Azure Cost Management via the REST API using managed identity to return
/// actual month-to-date spend for a subscription.
/// Requires: Billing Reader or Cost Management Reader on the subscription scope.
/// </summary>
public class AzureCostManagementClient
{
    private const string ArmResourceUri = "https://management.azure.com/";
    private const string CostMgmtApiVersion = "2023-11-01";
    private static readonly ActivitySource ActivitySource = new("Atlas.AgentOrchestrator.CostManagement");

    private readonly HttpClient _httpClient;
    private readonly ManagedIdentityCredentialProvider _credentialProvider;
    private readonly ILogger<AzureCostManagementClient> _logger;

    public AzureCostManagementClient(
        HttpClient httpClient,
        ManagedIdentityCredentialProvider credentialProvider,
        ILogger<AzureCostManagementClient> logger)
    {
        _httpClient = httpClient;
        _credentialProvider = credentialProvider;
        _logger = logger;
        _httpClient.BaseAddress = new Uri(ArmResourceUri);
    }

    /// <summary>
    /// Returns the month-to-date actual cost (USD) for the given subscription.
    /// Returns 0 if the managed identity lacks billing permissions — logged as a warning.
    /// </summary>
    public async Task<decimal> GetMonthToDateCostAsync(
        string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("GetMonthToDateCost");
        activity?.SetTag("subscription.id", subscriptionId);

        try
        {
            var credential = _credentialProvider.GetSystemAssignedCredential();
            var tokenContext = new TokenRequestContext(new[] { $"{ArmResourceUri}.default" });
            var accessToken = await credential.GetTokenAsync(tokenContext, cancellationToken);
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Token);

            var requestBody = new
            {
                type = "ActualCost",
                timeframe = "MonthToDate",
                dataset = new
                {
                    granularity = "None",
                    aggregation = new
                    {
                        totalCost = new { name = "Cost", function = "Sum" }
                    }
                }
            };

            var url = $"subscriptions/{subscriptionId}/providers/Microsoft.CostManagement/query?api-version={CostMgmtApiVersion}";
            var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(url, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Cost Management query returned {Status} for subscription {SubscriptionId} — FinOps analysis will use 0 as cost baseline",
                    response.StatusCode, subscriptionId);
                return 0m;
            }

            var result = await response.Content.ReadFromJsonAsync<CostQueryResponse>(cancellationToken: cancellationToken);
            var totalCost = result?.Properties?.Rows?.FirstOrDefault()?.FirstOrDefault();

            if (totalCost is JsonElement costElement && costElement.ValueKind == JsonValueKind.Number)
            {
                var amount = costElement.GetDecimal();
                activity?.SetTag("cost.monthToDate", amount);
                return amount;
            }

            return 0m;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to retrieve month-to-date cost for subscription {SubscriptionId}. " +
                "Ensure the managed identity has Cost Management Reader role. Using 0 as fallback.",
                subscriptionId);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return 0m;
        }
    }

    // ─── Response DTOs ─────────────────────────────────────────────────────────

    private sealed class CostQueryResponse
    {
        [JsonPropertyName("properties")]
        public CostQueryProperties? Properties { get; set; }
    }

    private sealed class CostQueryProperties
    {
        [JsonPropertyName("rows")]
        public List<List<JsonElement>>? Rows { get; set; }
    }
}
