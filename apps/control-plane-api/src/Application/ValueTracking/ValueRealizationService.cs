using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Azure;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.Json;

namespace Atlas.ControlPlane.Application.ValueTracking;

/// <summary>
/// Feature #1: ROI & Value Tracking Dashboard
/// Tracks actual vs estimated savings, payback periods, and value attribution
/// </summary>
public class ValueRealizationService
{
    private readonly AtlasDbContext _db;
    private readonly IAzureMcpValueEvidenceClient? _azureMcpValueEvidenceClient;
    private readonly IAzureCostManagementClient? _costManagementClient;
    private readonly ILogger<ValueRealizationService> _logger;

    public ValueRealizationService(
        AtlasDbContext db,
        IAzureMcpValueEvidenceClient? azureMcpValueEvidenceClient,
        IAzureCostManagementClient? costManagementClient,
        ILogger<ValueRealizationService> logger)
    {
        _db = db;
        _azureMcpValueEvidenceClient = azureMcpValueEvidenceClient;
        _costManagementClient = costManagementClient;
        _logger = logger;
    }

    /// <summary>
    /// Initialize value tracking for a recommendation when it's approved
    /// </summary>
    public async Task<ValueRealizationTracking> InitializeTrackingAsync(
        Guid recommendationId,
        Guid? changeSetId,
        decimal estimatedMonthlySavings,
        decimal estimatedImplementationCost,
        CancellationToken cancellationToken = default)
    {
        var paybackMonths = estimatedMonthlySavings > 0
            ? (int)Math.Ceiling(estimatedImplementationCost / estimatedMonthlySavings)
            : 12;

        var tracking = new ValueRealizationTracking
        {
            Id = Guid.NewGuid(),
            RecommendationId = recommendationId,
            ChangeSetId = changeSetId,
            EstimatedMonthlySavings = estimatedMonthlySavings,
            ActualMonthlySavings = 0,
            EstimatedImplementationCost = estimatedImplementationCost,
            ActualImplementationCost = 0,
            EstimatedPaybackDate = DateTime.UtcNow.AddMonths(paybackMonths),
            Status = "pending",
            BaselineRecordedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.ValueRealizations.Add(tracking);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Initialized value tracking for recommendation {RecommendationId}: estimated ${EstimatedMonthlySavings}/mo",
            recommendationId,
            estimatedMonthlySavings);

        return tracking;
    }

    /// <summary>
    /// Record actual costs and savings (called monthly or on-demand)
    /// </summary>
    public async Task<ValueRealizationTracking?> RecordActualValueAsync(
        Guid recommendationId,
        decimal actualMonthlySavings,
        decimal? actualImplementationCost = null,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        var tracking = await _db.ValueRealizations
            .FirstOrDefaultAsync(t => t.RecommendationId == recommendationId, cancellationToken);

        if (tracking == null)
        {
            _logger.LogWarning("No value tracking found for recommendation {RecommendationId}", recommendationId);
            return null;
        }

        tracking.ActualMonthlySavings = actualMonthlySavings;
        if (actualImplementationCost.HasValue)
        {
            tracking.ActualImplementationCost = actualImplementationCost.Value;
        }

        if (tracking.FirstMeasurementAt == null)
        {
            tracking.FirstMeasurementAt = DateTime.UtcNow;
            tracking.Status = "measuring";
        }

        // Check if payback achieved
        var totalSavings = actualMonthlySavings * (DateTime.UtcNow - tracking.BaselineRecordedAt).Days / 30.0m;
        if (totalSavings >= tracking.ActualImplementationCost && tracking.ActualPaybackDate == null)
        {
            tracking.ActualPaybackDate = DateTime.UtcNow;
            tracking.PaybackAchievedAt = DateTime.UtcNow;
            tracking.Status = "realized";
            _logger.LogInformation(
                "Payback achieved for recommendation {RecommendationId}: ${TotalSavings} >= ${Cost}",
                recommendationId,
                totalSavings,
                tracking.ActualImplementationCost);
        }

        if (notes != null)
        {
            tracking.MeasurementNotes = notes;
        }

        tracking.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return tracking;
    }

    /// <summary>
    /// Get aggregated ROI dashboard data
    /// </summary>
    public async Task<RoiDashboardData> GetDashboardDataAsync(
        Guid? serviceGroupId = null,
        CancellationToken cancellationToken = default)
    {
        var query = _db.ValueRealizations.AsQueryable();

        if (serviceGroupId.HasValue)
        {
            query = query.Where(v => v.Recommendation.ServiceGroupId == serviceGroupId.Value);
        }

        var allTracking = await query
            .Include(v => v.Recommendation)
            .ToListAsync(cancellationToken);

        if (allTracking.Count == 0)
        {
            var fallback = await BuildRecommendationEstimateFallbackAsync(serviceGroupId, cancellationToken);
            return await AppendBillingEvidenceAsync(fallback, serviceGroupId, cancellationToken);
        }

        var totalEstimatedSavings = allTracking.Sum(t => t.EstimatedMonthlySavings * 12);
        var totalActualSavings = allTracking.Sum(t => t.ActualMonthlySavings * 12);
        var totalEstimatedCost = allTracking.Sum(t => t.EstimatedImplementationCost);
        var totalActualCost = allTracking.Sum(t => t.ActualImplementationCost);

        var paybackAchieved = allTracking.Count(t => t.PaybackAchievedAt.HasValue);

        var dashboard = new RoiDashboardData
        {
            TotalEstimatedAnnualSavings = totalEstimatedSavings,
            TotalActualAnnualSavings = totalActualSavings,
            TotalEstimatedCost = totalEstimatedCost,
            TotalActualCost = totalActualCost,
            SavingsAccuracy = CalculateSavingsAccuracy(totalEstimatedSavings, totalActualSavings),
            AveragePaybackDays = allTracking
                .Where(t => t.PaybackAchievedAt.HasValue)
                .Select(t => (t.PaybackAchievedAt!.Value - t.BaselineRecordedAt).TotalDays)
                .DefaultIfEmpty(0)
                .Average(),
            TotalRecommendations = allTracking.Count,
            PaybackAchievedCount = paybackAchieved,
            TopSavers = allTracking
                .OrderByDescending(t => t.ActualMonthlySavings)
                .Take(10)
                .Select(t => new TopSaverItem
                {
                    RecommendationId = t.RecommendationId,
                    Title = t.Recommendation.Title,
                    MonthlySavings = t.ActualMonthlySavings,
                    Category = t.Recommendation.Category,
                    SavingsReason = "Measured savings from realized value tracking data."
                })
                .ToList()
        };

        return await AppendBillingEvidenceAsync(dashboard, serviceGroupId, cancellationToken);
    }

    private async Task<RoiDashboardData> BuildRecommendationEstimateFallbackAsync(
        Guid? serviceGroupId,
        CancellationToken cancellationToken)
    {
        var recommendations = _db.Recommendations.AsNoTracking().AsQueryable();

        if (serviceGroupId.HasValue)
        {
            recommendations = recommendations.Where(r => r.ServiceGroupId == serviceGroupId.Value);
        }

        var estimateCandidates = await recommendations
            .Select(r => new
            {
                r.Id,
                r.Title,
                r.Category,
                r.EstimatedImpact
            })
            .ToListAsync(cancellationToken);

        var estimatedSavings = estimateCandidates
            .Select(candidate => new
            {
                candidate.Id,
                candidate.Title,
                candidate.Category,
                ParsedSavings = ParseEstimatedMonthlySavings(
                    candidate.EstimatedImpact,
                    candidate.Category,
                    candidate.Title)
            })
            .Where(x => x.ParsedSavings is not null)
            .Select(x => new
            {
                x.Id,
                x.Title,
                x.Category,
                EstimatedMonthlySavings = x.ParsedSavings!.Value.MonthlySavings,
                SavingsReason = x.ParsedSavings!.Value.Reason
            })
            .Where(x => x.EstimatedMonthlySavings > 0)
            .ToList();

        var topSavers = estimatedSavings
            .OrderByDescending(x => x.EstimatedMonthlySavings)
            .Take(10)
            .ToList();

        var totalEstimatedAnnualSavings = estimatedSavings.Sum(x => x.EstimatedMonthlySavings) * 12;

        return new RoiDashboardData
        {
            TotalEstimatedAnnualSavings = totalEstimatedAnnualSavings,
            TotalActualAnnualSavings = 0,
            TotalEstimatedCost = 0,
            TotalActualCost = 0,
            SavingsAccuracy = 0,
            AveragePaybackDays = 0,
            TotalRecommendations = estimateCandidates.Count,
            PaybackAchievedCount = 0,
            TopSavers = topSavers
                .Select(x => new TopSaverItem
                {
                    RecommendationId = x.Id,
                    Title = x.Title,
                    MonthlySavings = x.EstimatedMonthlySavings,
                    Category = x.Category,
                    SavingsReason = x.SavingsReason
                })
                .ToList()
        };
    }

    private static ParsedSavings? ParseEstimatedMonthlySavings(
        string? estimatedImpactJson,
        string category,
        string title)
    {
        if (string.IsNullOrWhiteSpace(estimatedImpactJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(estimatedImpactJson);
            var root = doc.RootElement;

            // Try monthlySavings field first
            if (root.TryGetProperty("monthlySavings", out var monthlySavings) &&
                TryReadDecimal(monthlySavings, out var monthlySavingsValue) &&
                monthlySavingsValue > 0)
            {
                return new ParsedSavings(
                    monthlySavingsValue,
                    "Estimated from recommendation monthlySavings field.");
            }

            // Try estimatedMonthlySavings field
            if (root.TryGetProperty("estimatedMonthlySavings", out var estimatedMonthlySavings) &&
                TryReadDecimal(estimatedMonthlySavings, out var estimatedMonthlyValue) &&
                estimatedMonthlyValue > 0)
            {
                return new ParsedSavings(
                    estimatedMonthlyValue,
                    "Estimated from recommendation estimatedMonthlySavings field.");
            }

            // Try estimatedAnnualSavings field (divide by 12)
            if (root.TryGetProperty("estimatedAnnualSavings", out var estimatedAnnualSavings) &&
                TryReadDecimal(estimatedAnnualSavings, out var estimatedAnnualValue) &&
                estimatedAnnualValue > 0)
            {
                var annualToMonthly = estimatedAnnualValue / 12m;
                return new ParsedSavings(
                    annualToMonthly,
                    "Estimated from recommendation estimatedAnnualSavings field (annualized).");
            }

            // Try costDelta field (negative value indicates savings)
            if (root.TryGetProperty("costDelta", out var costDelta) &&
                TryReadDecimal(costDelta, out var costDeltaValue) &&
                costDeltaValue < 0 &&
                IsCostFocusedRecommendation(category, title))
            {
                return new ParsedSavings(
                    -costDeltaValue,
                    "Estimated from recommendation costDelta field.");
            }

            // Try estimatedCostReduction field
            if (root.TryGetProperty("estimatedCostReduction", out var costReduction) &&
                TryReadDecimal(costReduction, out var costReductionValue) &&
                costReductionValue > 0)
            {
                var annualToMonthly2 = costReductionValue / 12m;
                return new ParsedSavings(
                    annualToMonthly2,
                    "Estimated from recommendation estimatedCostReduction field (annualized).");
            }

            // Last resort: try to extract any numeric value from impact field
            if (IsCostFocusedRecommendation(category, title) && root.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name.Contains("savings", StringComparison.OrdinalIgnoreCase) ||
                        prop.Name.Contains("cost", StringComparison.OrdinalIgnoreCase) ||
                        prop.Name.Contains("impact", StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryReadDecimal(prop.Value, out var value) && value > 0)
                        {
                            // Assume annual value if large, monthly if small
                            var monthlyValue = value > 10000 ? value / 12m : value;
                            return new ParsedSavings(
                                monthlyValue,
                                $"Estimated from recommendation {prop.Name} field.");
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        return null;
    }

    private static bool TryReadDecimal(JsonElement value, out decimal parsed)
    {
        parsed = default;

        if (value.ValueKind == JsonValueKind.Number)
        {
            try
            {
                return value.TryGetDecimal(out parsed);
            }
            catch
            {
                // Fallback: try int/double conversion
                if (value.TryGetInt64(out var longVal))
                {
                    parsed = longVal;
                    return true;
                }
                if (value.TryGetDouble(out var doubleVal))
                {
                    parsed = (decimal)doubleVal;
                    return true;
                }
            }
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var raw = value.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            // Try direct decimal parse
            if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed))
            {
                return true;
            }

            // Try removing common currency symbols and trying again
            var cleaned = raw.Trim('$', '€', '£', '¥', ' ');
            if (decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed))
            {
                return true;
            }
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        return false;
    }

    private static bool IsCostFocusedRecommendation(string? category, string? title)
    {
        var normalizedCategory = category?.ToLowerInvariant() ?? string.Empty;
        var normalizedTitle = title?.ToLowerInvariant() ?? string.Empty;

        if (normalizedCategory.Contains("cost") || normalizedCategory.Contains("finops"))
        {
            return true;
        }

        return normalizedTitle.Contains("cost") || normalizedTitle.Contains("savings");
    }

    private async Task<RoiDashboardData> AppendBillingEvidenceAsync(
        RoiDashboardData dashboard,
        Guid? serviceGroupId,
        CancellationToken cancellationToken)
    {
        if (!serviceGroupId.HasValue)
        {
            return dashboard with
            {
                BillingEvidenceStatus = "Select a service group to load Billing API evidence.",
                CostEvidence = []
            };
        }

        if (_costManagementClient is null && _azureMcpValueEvidenceClient is null)
        {
            return dashboard with
            {
                BillingEvidenceStatus = "No billing evidence client is configured in this environment.",
                CostEvidence = []
            };
        }

        var scopes = await _db.ServiceGroupScopes
            .AsNoTracking()
            .Where(scope => scope.ServiceGroupId == serviceGroupId.Value)
            .Select(scope => new { scope.SubscriptionId, scope.ResourceGroup })
            .ToListAsync(cancellationToken);

        var distinctScopes = scopes
            .DistinctBy(scope => new
            {
                SubscriptionId = scope.SubscriptionId.Trim(),
                ResourceGroup = (scope.ResourceGroup ?? string.Empty).Trim()
            })
            .ToList();

        if (distinctScopes.Count == 0)
        {
            return dashboard with
            {
                BillingEvidenceStatus = "No subscription scopes found for this service group.",
                CostEvidence = []
            };
        }

        var utcNow = DateTime.UtcNow;
        var currentMonthStart = new DateTime(utcNow.Year, utcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var previousMonthStart = currentMonthStart.AddMonths(-1);
        var previousMonthEnd = currentMonthStart.AddTicks(-1);

        var elapsedDaysInCurrentMonth = Math.Max((utcNow.Date - currentMonthStart.Date).Days + 1, 1);
        var previousMonthDays = DateTime.DaysInMonth(previousMonthStart.Year, previousMonthStart.Month);

        var evidence = new List<CostEvidenceItem>(distinctScopes.Count);
        foreach (var scope in distinctScopes)
        {
            var mcpEvidence = _azureMcpValueEvidenceClient is null
                ? null
                : await _azureMcpValueEvidenceClient.TryGetCostEvidenceAsync(
                    scope.SubscriptionId,
                    scope.ResourceGroup,
                    currentMonthStart,
                    utcNow,
                    elapsedDaysInCurrentMonth,
                    previousMonthDays,
                    cancellationToken);

            if (mcpEvidence is not null)
            {
                evidence.Add(new CostEvidenceItem
                {
                    SubscriptionId = scope.SubscriptionId,
                    ResourceGroup = scope.ResourceGroup,
                    MonthToDateCostUsd = mcpEvidence.MonthToDateCostUsd,
                    BaselineMonthToDateCostUsd = mcpEvidence.BaselineMonthToDateCostUsd,
                    EstimatedMonthlySavingsUsd = mcpEvidence.EstimatedMonthlySavingsUsd,
                    AnomalyCount = mcpEvidence.AnomalyCount,
                    AdvisorRecommendationLinks = mcpEvidence.AdvisorRecommendationLinks,
                    ActivityLogCorrelationEvents = mcpEvidence.ActivityLogCorrelationEvents,
                    EvidenceSource = mcpEvidence.Source,
                    LastQueriedAt = mcpEvidence.LastQueriedAtUtc
                });

                continue;
            }

            if (_costManagementClient is null)
            {
                continue;
            }

            var monthToDateCost = await _costManagementClient.QueryCostsAsync(
                scope.SubscriptionId,
                currentMonthStart,
                utcNow,
                scope.ResourceGroup,
                cancellationToken);

            var previousMonthCost = await _costManagementClient.QueryCostsAsync(
                scope.SubscriptionId,
                previousMonthStart,
                previousMonthEnd,
                scope.ResourceGroup,
                cancellationToken);

            var baselineMonthToDateCost = previousMonthDays > 0
                ? Math.Round((previousMonthCost / previousMonthDays) * elapsedDaysInCurrentMonth, 2)
                : 0m;

            var estimatedMonthlySavings = Math.Round(baselineMonthToDateCost - monthToDateCost, 2);

            var anomalies = await _costManagementClient.GetAnomaliesAsync(scope.SubscriptionId, cancellationToken);

            evidence.Add(new CostEvidenceItem
            {
                SubscriptionId = scope.SubscriptionId,
                ResourceGroup = scope.ResourceGroup,
                MonthToDateCostUsd = monthToDateCost,
                BaselineMonthToDateCostUsd = baselineMonthToDateCost,
                EstimatedMonthlySavingsUsd = estimatedMonthlySavings,
                AnomalyCount = anomalies.Count,
                AdvisorRecommendationLinks = 0,
                ActivityLogCorrelationEvents = 0,
                EvidenceSource = "Azure Cost Management API",
                LastQueriedAt = utcNow
            });
        }

        var elapsedDays = elapsedDaysInCurrentMonth;
        var currentAnnualRunRate = elapsedDays > 0
            ? evidence.Sum(item => Math.Round(item.MonthToDateCostUsd / elapsedDays * 365m, 2))
            : 0m;
        var optimisedAnnualRunRate = Math.Max(0m, currentAnnualRunRate - dashboard.TotalEstimatedAnnualSavings);

        var derivedActualAnnualSavings = evidence.Sum(item => item.EstimatedMonthlySavingsUsd) * 12m;
        var adjustedActualAnnualSavings = dashboard.TotalActualAnnualSavings;
        if (adjustedActualAnnualSavings <= 0m && derivedActualAnnualSavings != 0m)
        {
            adjustedActualAnnualSavings = derivedActualAnnualSavings;
        }
        else if (derivedActualAnnualSavings > 0m)
        {
            adjustedActualAnnualSavings = Math.Max(adjustedActualAnnualSavings, derivedActualAnnualSavings);
        }

        var adjustedSavingsAccuracy = CalculateSavingsAccuracy(
            dashboard.TotalEstimatedAnnualSavings,
            adjustedActualAnnualSavings);

        var evidenceSources = evidence
            .Select(item => item.EvidenceSource)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var azureMcpEvidenceCount = evidence.Count(item =>
            string.Equals(item.EvidenceSource, "Azure MCP", StringComparison.OrdinalIgnoreCase));
        var totalScopeCount = distinctScopes.Count;

        var hasAzureMcpEvidence = evidenceSources
            .Any(source => string.Equals(source, "Azure MCP", StringComparison.OrdinalIgnoreCase));
        var sourceLabel = evidenceSources.Count == 0
            ? "no source"
            : string.Join(", ", evidenceSources);

        var billingEvidenceStatus = hasAzureMcpEvidence
            ? $"Azure MCP evidence loaded from {sourceLabel} for {distinctScopes.Count} scoped subscription/resource-group target(s)."
            : $"Azure MCP evidence was unavailable for the selected scope; using {sourceLabel} fallback for {distinctScopes.Count} scoped subscription/resource-group target(s).";

        var azureMcpToolCallStatus = hasAzureMcpEvidence
            ? $"Azure MCP tool-call evidence resolved for {azureMcpEvidenceCount}/{totalScopeCount} scoped subscription/resource-group target(s)."
            : $"Azure MCP tool-call evidence was not resolved for the selected scope ({azureMcpEvidenceCount}/{totalScopeCount}); using fallback billing data where available.";

        return dashboard with
        {
            TotalActualAnnualSavings = adjustedActualAnnualSavings,
            SavingsAccuracy = adjustedSavingsAccuracy,
            CurrentAnnualRunRate = currentAnnualRunRate,
            OptimisedAnnualRunRate = optimisedAnnualRunRate,
            BillingEvidenceStatus = billingEvidenceStatus,
            AzureMcpToolCallStatus = azureMcpToolCallStatus,
            CostEvidence = evidence
        };
    }

    /// <summary>
    /// Get all tracking records, optionally filtered by status, with a result limit.
    /// </summary>
    public async Task<List<ValueRealizationTracking>> GetTrackingRecordsAsync(
        string? status,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var query = _db.ValueRealizations
            .Include(v => v.Recommendation)
            .AsNoTracking();

        if (!string.IsNullOrEmpty(status))
        {
            query = query.Where(t => t.Status == status);
        }

        return await query.Take(limit).ToListAsync(cancellationToken);
    }

    private static decimal CalculateSavingsAccuracy(decimal estimatedAnnualSavings, decimal actualAnnualSavings)
    {
        if (estimatedAnnualSavings <= 0m)
        {
            return 0m;
        }

        var ratio = (actualAnnualSavings / estimatedAnnualSavings) * 100m;
        return Math.Round(Math.Max(0m, ratio), 2);
    }
}

public record RoiDashboardData
{
    public decimal TotalEstimatedAnnualSavings { get; init; }
    public decimal TotalActualAnnualSavings { get; init; }
    public decimal TotalEstimatedCost { get; init; }
    public decimal TotalActualCost { get; init; }
    public decimal SavingsAccuracy { get; init; } // percentage
    public double AveragePaybackDays { get; init; }
    public int TotalRecommendations { get; init; }
    public int PaybackAchievedCount { get; init; }
    public List<TopSaverItem> TopSavers { get; init; } = new();
    public string BillingEvidenceStatus { get; init; } = "";
    public string AzureMcpToolCallStatus { get; init; } = "";
    public List<CostEvidenceItem> CostEvidence { get; init; } = new();
    /// <summary>Annualised run rate extrapolated from month-to-date billing evidence.</summary>
    public decimal CurrentAnnualRunRate { get; init; }
    /// <summary>Run rate after applying all estimated savings opportunities (floored at zero).</summary>
    public decimal OptimisedAnnualRunRate { get; init; }
}

public record TopSaverItem
{
    public Guid RecommendationId { get; init; }
    public string Title { get; init; } = string.Empty;
    public decimal MonthlySavings { get; init; }
    public string Category { get; init; } = string.Empty;
    public string SavingsReason { get; init; } = string.Empty;
}

public readonly record struct ParsedSavings(decimal MonthlySavings, string Reason);

public record CostEvidenceItem
{
    public string SubscriptionId { get; init; } = string.Empty;
    public string? ResourceGroup { get; init; }
    public decimal MonthToDateCostUsd { get; init; }
    public decimal BaselineMonthToDateCostUsd { get; init; }
    public decimal EstimatedMonthlySavingsUsd { get; init; }
    public int AnomalyCount { get; init; }
    public int AdvisorRecommendationLinks { get; init; }
    public int ActivityLogCorrelationEvents { get; init; }
    public string EvidenceSource { get; init; } = string.Empty;
    public DateTime LastQueriedAt { get; init; }
}
