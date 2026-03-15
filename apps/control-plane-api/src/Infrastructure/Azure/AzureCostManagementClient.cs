using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atlas.ControlPlane.Infrastructure.Auth;
using Azure.Core;
using Microsoft.Extensions.Logging;

namespace Atlas.ControlPlane.Infrastructure.Azure;

public interface IAzureCostManagementClient
{
    Task<decimal> QueryCostsAsync(string subscriptionId, CancellationToken cancellationToken = default);

    Task<decimal> QueryCostsAsync(
        string subscriptionId,
        DateTime fromUtc,
        DateTime toUtc,
        string? resourceGroup = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CostAlert>> GetAnomaliesAsync(
        string subscriptionId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Client for querying Azure Cost Management via the ARM REST API using managed identity.
/// Requires Cost Management Reader on the subscription scope.
/// </summary>
public class AzureCostManagementClient : IAzureCostManagementClient
{
    private const string ArmResourceUri = "https://management.azure.com/";
    private const string CostMgmtApiVersion = "2023-11-01";
    private static readonly ActivitySource ActivitySource = new("Atlas.ControlPlane.Azure.CostManagement");

    private readonly HttpClient _httpClient;
    private readonly TokenCredential _credential;
    private readonly ILogger<AzureCostManagementClient> _logger;

    public AzureCostManagementClient(
        IHttpClientFactory httpClientFactory,
        ManagedIdentityCredentialProvider credentialProvider,
        ILogger<AzureCostManagementClient> logger)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient(nameof(AzureCostManagementClient));
        _httpClient.BaseAddress = new Uri(ArmResourceUri);
        // Use the shared user-assigned identity (AZURE_CLIENT_ID), which receives
        // subscription-scope Cost Management Reader during azd provision.
        _credential = credentialProvider.GetCredential();
    }

    /// <summary>
    /// Returns the month-to-date actual cost (USD) for the given subscription.
    /// Returns 0 if the managed identity lacks billing permissions — logged as a warning.
    /// </summary>
    public async Task<decimal> QueryCostsAsync(
        string subscriptionId,
        CancellationToken cancellationToken = default)
        => await ExecuteCostQueryAsync(subscriptionId, null, null, null, cancellationToken);

    /// <summary>
    /// Returns actual cost (USD) for a custom UTC time window, optionally filtered to a resource group.
    /// </summary>
    public async Task<decimal> QueryCostsAsync(
        string subscriptionId,
        DateTime fromUtc,
        DateTime toUtc,
        string? resourceGroup = null,
        CancellationToken cancellationToken = default)
        => await ExecuteCostQueryAsync(subscriptionId, fromUtc, toUtc, resourceGroup, cancellationToken);

    private async Task<decimal> ExecuteCostQueryAsync(
        string subscriptionId,
        DateTime? fromUtc,
        DateTime? toUtc,
        string? resourceGroup,
        CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("CostManagement.QueryCosts");
        activity?.SetTag("subscription.id", subscriptionId);
        if (!string.IsNullOrWhiteSpace(resourceGroup))
            activity?.SetTag("scope.resource_group", resourceGroup);

        try
        {
            var tokenContext = new TokenRequestContext(new[] { $"{ArmResourceUri}.default" });
            var accessToken = await _credential.GetTokenAsync(tokenContext, cancellationToken);
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Token);

            object? filter = null;
            if (!string.IsNullOrWhiteSpace(resourceGroup))
            {
                filter = new
                {
                    dimensions = new
                    {
                        name = "ResourceGroupName",
                        @operator = "In",
                        values = new[] { resourceGroup }
                    }
                };
            }

            object requestBody;
            if (fromUtc.HasValue && toUtc.HasValue)
            {
                requestBody = new
                {
                    type = "ActualCost",
                    timeframe = "Custom",
                    timePeriod = new
                    {
                        from = fromUtc.Value.ToUniversalTime().ToString("O"),
                        to = toUtc.Value.ToUniversalTime().ToString("O")
                    },
                    dataset = new
                    {
                        granularity = "None",
                        aggregation = new
                        {
                            totalCost = new { name = "Cost", function = "Sum" }
                        },
                        filter
                    }
                };
            }
            else
            {
                requestBody = new
                {
                    type = "ActualCost",
                    timeframe = "MonthToDate",
                    dataset = new
                    {
                        granularity = "None",
                        aggregation = new
                        {
                            totalCost = new { name = "Cost", function = "Sum" }
                        },
                        filter
                    }
                };
            }

            var url = $"subscriptions/{subscriptionId}/providers/Microsoft.CostManagement/query?api-version={CostMgmtApiVersion}";
            using var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(url, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Cost Management query returned {Status} for subscription {SubscriptionId} — FinOps analysis will use 0 as cost baseline",
                    response.StatusCode, subscriptionId);
                return 0m;
            }

            var result = await response.Content.ReadFromJsonAsync<CostQueryResponse>(cancellationToken: cancellationToken);
            var firstRow = result?.Properties?.Rows?.FirstOrDefault()?.FirstOrDefault();

            if (firstRow is JsonElement el && el.ValueKind == JsonValueKind.Number)
            {
                var amount = el.GetDecimal();
                activity?.SetTag("cost.monthToDate", amount);
                _logger.LogInformation(
                    "Month-to-date cost for subscription {SubscriptionId}: {Amount:F2} USD",
                    subscriptionId, amount);
                return amount;
            }

            return 0m;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogWarning(ex,
                "Failed to retrieve month-to-date cost for subscription {SubscriptionId}. " +
                "Ensure the managed identity has Cost Management Reader role. Using 0 as fallback.",
                subscriptionId);
            return 0m;
        }
    }

    /// <summary>
    /// Gets cost anomaly alerts from Azure Cost Management for the given subscription.
    /// Returns an empty list if the managed identity lacks billing permissions.
    /// </summary>
    public async Task<IReadOnlyList<CostAlert>> GetAnomaliesAsync(
        string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("CostManagement.GetAnomalies");
        activity?.SetTag("subscription.id", subscriptionId);

        try
        {
            var tokenContext = new TokenRequestContext(new[] { $"{ArmResourceUri}.default" });
            var accessToken = await _credential.GetTokenAsync(tokenContext, cancellationToken);
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Token);

            var url = $"subscriptions/{subscriptionId}/providers/Microsoft.CostManagement/alerts?api-version={CostMgmtApiVersion}";
            using var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Cost Management alerts query returned {Status} for subscription {SubscriptionId}",
                    response.StatusCode, subscriptionId);
                return [];
            }

            var alertsResponse = await response.Content.ReadFromJsonAsync<CostAlertsResponse>(cancellationToken: cancellationToken);
            var alerts = alertsResponse?.Value ?? [];

            activity?.SetTag("result.alert_count", alerts.Count);
            _logger.LogInformation(
                "Retrieved {AlertCount} cost anomaly alert(s) for subscription {SubscriptionId}",
                alerts.Count, subscriptionId);

            return alerts;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogWarning(ex,
                "Failed to retrieve cost alerts for subscription {SubscriptionId}. Using empty list as fallback.",
                subscriptionId);
            return [];
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

    private sealed class CostAlertsResponse
    {
        [JsonPropertyName("value")]
        public List<CostAlert>? Value { get; set; }
    }
}

/// <summary>
/// A cost anomaly alert returned by Azure Cost Management.
/// </summary>
public sealed class CostAlert
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("properties")]
    public CostAlertProperties? Properties { get; set; }
}

/// <summary>
/// Properties of a cost anomaly alert.
/// </summary>
public sealed class CostAlertProperties
{
    [JsonPropertyName("alertType")]
    public string? AlertType { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}
