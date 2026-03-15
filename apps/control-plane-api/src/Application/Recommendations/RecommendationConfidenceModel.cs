using System.Text.Json;
using Atlas.ControlPlane.Domain.Entities;

namespace Atlas.ControlPlane.Application.Recommendations;

public static class RecommendationConfidenceModel
{
    public static decimal CalculateWeightedConfidence(
        Atlas.ControlPlane.Domain.Entities.Recommendation recommendation,
        GroundingProvenance? grounding,
        CostEvidenceSignals? costSignals)
    {
        var baseConfidence = Clamp((double)recommendation.Confidence);
        var scopeCoverage = Clamp(costSignals?.ScopeCoverage ?? 0.0);
        var freshness = Clamp(costSignals?.CostEvidenceFreshnessScore ?? 0.0);
        var anomalySeverity = Clamp(costSignals?.AnomalySeverityScore ?? 0.0);
        var groundingRecency = Clamp(grounding?.GroundingRecencyScore ?? 0.0);
        var replayStability = Clamp(EstimateReplayStability(recommendation));

        var blended =
            (0.30 * baseConfidence) +
            (0.20 * freshness) +
            (0.10 * anomalySeverity) +
            (0.15 * scopeCoverage) +
            (0.15 * groundingRecency) +
            (0.10 * replayStability);

        return (decimal)Math.Round(Clamp(blended), 4);
    }

    public static double EstimateReplayStability(Atlas.ControlPlane.Domain.Entities.Recommendation recommendation)
    {
        var hasStructuredTradeoffs = HasJsonObject(recommendation.TradeoffProfile);
        var hasStructuredRisk = HasJsonObject(recommendation.RiskProfile);
        var hasEvidence = CountEvidence(recommendation.EvidenceReferences) > 0;

        var score = 0.2;
        if (hasStructuredTradeoffs) score += 0.3;
        if (hasStructuredRisk) score += 0.3;
        if (hasEvidence) score += 0.2;

        return Clamp(score);
    }

    private static bool HasJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }

    private static int CountEvidence(string? evidenceReferences)
    {
        if (string.IsNullOrWhiteSpace(evidenceReferences))
        {
            return 0;
        }

        try
        {
            using var doc = JsonDocument.Parse(evidenceReferences);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return doc.RootElement.GetArrayLength();
            }
        }
        catch
        {
            // ignored intentionally, fallback below
        }

        return evidenceReferences.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    }

    private static double Clamp(double value) => Math.Max(0.0, Math.Min(1.0, value));
}

public sealed record CostEvidenceSignals(
    double ScopeCoverage,
    double CostEvidenceFreshnessScore,
    double AnomalySeverityScore);
