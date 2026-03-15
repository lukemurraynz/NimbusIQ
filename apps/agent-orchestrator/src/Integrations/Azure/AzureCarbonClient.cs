using Atlas.AgentOrchestrator.Integrations.Auth;
using Azure.Core;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Atlas.AgentOrchestrator.Integrations.Azure;

/// <summary>
/// Queries Azure Carbon Optimization API (preview) for carbon emission data.
/// Gracefully falls back to empty results when the preview API is unavailable or permissions are missing.
/// Requires: Carbon Optimization Reader role on subscription scope.
/// </summary>
public class AzureCarbonClient
{
    private const string ArmBaseUri = "https://management.azure.com/";
    private const string CarbonApiVersion = "2023-04-01-preview";
    private static readonly Regex AvailableDateRangeRegex = new(
        @"StartDate:\s*(?<start>[^,]+),\s*EndDate:\s*(?<end>[^.\s]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly ActivitySource ActivitySource = new("Atlas.AgentOrchestrator.Carbon");

    private readonly HttpClient _httpClient;
    private readonly ManagedIdentityCredentialProvider _credentialProvider;
    private readonly ILogger<AzureCarbonClient> _logger;

    public AzureCarbonClient(
        HttpClient httpClient,
        ManagedIdentityCredentialProvider credentialProvider,
        ILogger<AzureCarbonClient> logger)
    {
        _httpClient = httpClient;
        _credentialProvider = credentialProvider;
        _logger = logger;
        _httpClient.BaseAddress = new Uri(ArmBaseUri);
    }

    /// <summary>
    /// Queries total carbon emissions (kg CO₂e) for the given subscriptions over the last 30 days.
    /// Returns 0 if the API is unavailable (preview) or credentials lack permission.
    /// </summary>
    public async Task<CarbonEmissionSummary> GetMonthlyCarbonEmissionsAsync(
        IReadOnlyList<string> subscriptionIds,
        CancellationToken cancellationToken = default)
    {
        if (subscriptionIds.Count == 0)
            return CarbonEmissionSummary.Empty;

        using var activity = ActivitySource.StartActivity("GetMonthlyCarbonEmissions");
        activity?.SetTag("subscription.count", subscriptionIds.Count);

        try
        {
            // Carbon Optimization Reader is granted to the UAMI (via Grant-RuntimeSubscriptionRoles.ps1),
            // not the system-assigned identity — use GetCredential() so AZURE_CLIENT_ID picks up the UAMI.
            var credential = _credentialProvider.GetCredential();
            var tokenContext = new TokenRequestContext(new[] { $"{ArmBaseUri}.default" });
            var accessToken = await credential.GetTokenAsync(tokenContext, cancellationToken);

            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"providers/Microsoft.Carbon/carbonEmissionReports?api-version={CarbonApiVersion}");
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Token);

            var endDate = DateTime.UtcNow.Date;
            var startDate = endDate.AddDays(-30);
            request.Content = BuildRequestContent(subscriptionIds, startDate, endDate);

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                var badRequestBody = await response.Content.ReadAsStringAsync(cancellationToken);
                if (TryBuildFallbackDateRange(badRequestBody, out var fallbackStart, out var fallbackEnd))
                {
                    _logger.LogInformation(
                        "Retrying Carbon Optimization API request with available date window {Start}..{End}",
                        fallbackStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        fallbackEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

                    response.Dispose();

                    using var retryRequest = new HttpRequestMessage(HttpMethod.Post,
                        $"providers/Microsoft.Carbon/carbonEmissionReports?api-version={CarbonApiVersion}");
                    retryRequest.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Token);
                    retryRequest.Content = BuildRequestContent(subscriptionIds, fallbackStart, fallbackEnd);

                    response = await _httpClient.SendAsync(retryRequest, cancellationToken);
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "Carbon Optimization API returned {Status} for {Count} subscription(s) — sustainability analysis will use fallback estimates. Response: {Response}",
                    response.StatusCode, subscriptionIds.Count, errorBody);
                return CarbonEmissionSummary.Empty;
            }

            using var payload = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);

            if (!payload.RootElement.TryGetProperty("value", out var valueElement) ||
                valueElement.ValueKind != JsonValueKind.Array ||
                valueElement.GetArrayLength() == 0)
            {
                return CarbonEmissionSummary.Empty;
            }

            double totalKg = 0;
            var emissionFieldsFound = false;
            var regionEmissions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in valueElement.EnumerateArray())
            {
                if (!TryGetEmissionValue(item, out var emissionKg))
                {
                    continue;
                }

                emissionFieldsFound = true;
                totalKg += emissionKg;

                var region = TryGetRegionLabel(item);
                if (!string.IsNullOrEmpty(region))
                {
                    regionEmissions.TryGetValue(region, out var existing);
                    regionEmissions[region] = existing + emissionKg;
                }
            }

            if (!emissionFieldsFound)
            {
                _logger.LogWarning(
                    "Carbon Optimization API response did not contain recognized emission fields. Expected one of: totalCarbonEmission, latestMonthEmissions, latest_month_emissions.");
                return CarbonEmissionSummary.Empty;
            }

            activity?.SetTag("carbon.totalKg", totalKg);

            _logger.LogInformation(
                "Carbon emissions retrieved: {TotalKg:F2} kg CO₂e across {Regions} region(s) for {Subscriptions} subscription(s)",
                totalKg, regionEmissions.Count, subscriptionIds.Count);

            return new CarbonEmissionSummary
            {
                TotalEmissionsKg = totalKg,
                RegionEmissions = regionEmissions,
                HasRealData = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to retrieve carbon emissions — Carbon Optimization API may not be available " +
                "(preview feature). Sustainability analysis will use fallback estimates.");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return CarbonEmissionSummary.Empty;
        }
    }

    private static bool TryGetEmissionValue(JsonElement item, out double emissionKg)
    {
        // Legacy field (older preview docs)
        if (TryGetDouble(item, "totalCarbonEmission", out emissionKg))
        {
            return true;
        }

        // Current field (newer Carbon Optimization API + SDKs)
        if (TryGetDouble(item, "latestMonthEmissions", out emissionKg) ||
            TryGetDouble(item, "latest_month_emissions", out emissionKg))
        {
            return true;
        }

        // Some payloads nest emission metrics under a data/properties object.
        if (item.TryGetProperty("carbonEmissionData", out var nested) ||
            item.TryGetProperty("properties", out nested))
        {
            if (TryGetDouble(nested, "totalCarbonEmission", out emissionKg) ||
                TryGetDouble(nested, "latestMonthEmissions", out emissionKg) ||
                TryGetDouble(nested, "latest_month_emissions", out emissionKg))
            {
                return true;
            }
        }

        emissionKg = 0;
        return false;
    }

    private static bool TryGetDouble(JsonElement obj, string propertyName, out double value)
    {
        if (obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty(propertyName, out var val) &&
            val.ValueKind == JsonValueKind.Number &&
            val.TryGetDouble(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static string? TryGetRegionLabel(JsonElement item)
    {
        // Keep this tolerant: Carbon API report shapes differ by report type.
        return TryGetString(item, "dataCenter")
            ?? TryGetString(item, "location")
            ?? TryGetString(item, "resourceLocation")
            ?? TryGetString(item, "categoryValue");
    }

    private static string? TryGetString(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind != JsonValueKind.Object ||
            !obj.TryGetProperty(propertyName, out var val))
        {
            return null;
        }

        return val.ValueKind == JsonValueKind.String ? val.GetString() : null;
    }

    private static StringContent BuildRequestContent(
        IReadOnlyList<string> subscriptionIds,
        DateTime startDate,
        DateTime endDate)
    {
        var body = new
        {
            reportType = "OverallSummaryReport",
            subscriptionList = subscriptionIds,
            carbonScopeList = new[] { "Scope1", "Scope2", "Scope3" },
            dateRange = new
            {
                start = startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                end = endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            }
        };

        return new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");
    }

    internal static bool TryBuildFallbackDateRange(
        string? responseBody,
        out DateTime fallbackStart,
        out DateTime fallbackEnd)
    {
        fallbackStart = default;
        fallbackEnd = default;

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        var match = AvailableDateRangeRegex.Match(responseBody);
        if (!match.Success)
        {
            return false;
        }

        var startRaw = match.Groups["start"].Value.Trim();
        var endRaw = match.Groups["end"].Value.Trim();

        if (!TryParseCarbonDate(startRaw, out var availableStart) ||
            !TryParseCarbonDate(endRaw, out var availableEnd))
        {
            return false;
        }

        availableStart = availableStart.Date;
        availableEnd = availableEnd.Date;

        if (availableEnd <= availableStart)
        {
            return false;
        }

        var proposedStart = availableEnd.AddDays(-30);
        fallbackStart = proposedStart < availableStart ? availableStart : proposedStart;
        fallbackEnd = availableEnd;

        return fallbackEnd > fallbackStart;
    }

    private static bool TryParseCarbonDate(string value, out DateTime parsed)
    {
        var formats = new[] { "yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy" };
        return DateTime.TryParseExact(
            value,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out parsed);
    }
}

public sealed class CarbonEmissionSummary
{
    public static readonly CarbonEmissionSummary Empty = new();

    public double TotalEmissionsKg { get; init; }
    public Dictionary<string, double> RegionEmissions { get; init; } = new();
    public bool HasRealData { get; init; }
}
