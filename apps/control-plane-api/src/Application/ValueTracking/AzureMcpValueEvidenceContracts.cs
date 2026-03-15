namespace Atlas.ControlPlane.Application.ValueTracking;

public sealed record McpCostEvidence(
    decimal MonthToDateCostUsd,
    decimal BaselineMonthToDateCostUsd,
    decimal EstimatedMonthlySavingsUsd,
    int AnomalyCount,
    int AdvisorRecommendationLinks,
    int ActivityLogCorrelationEvents,
    DateTime LastQueriedAtUtc,
    string Source);

public interface IAzureMcpValueEvidenceClient
{
    Task<McpCostEvidence?> TryGetCostEvidenceAsync(
        string subscriptionId,
        string? resourceGroup,
        DateTime monthStartUtc,
        DateTime utcNow,
        int elapsedDaysInCurrentMonth,
        int previousMonthDays,
        CancellationToken cancellationToken = default);
}
