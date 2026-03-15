using System.Text.Json;
using Atlas.ControlPlane.Domain.Entities;

namespace Atlas.ControlPlane.Application.Recommendations;

public static class RecommendationLensCalculator
{
    private const decimal MaxRiskScore = 1.0m;
    private const decimal MinRiskScore = 0.0m;

    public static RecommendationLens Calculate(Domain.Entities.Recommendation recommendation, DateTime utcNow)
    {
        var confidence = Clamp(recommendation.Confidence, 0.0m, 1.0m);
        var evidenceCompleteness = CalculateEvidenceCompleteness(recommendation);
        var freshnessDays = Math.Max(0, (utcNow - recommendation.CreatedAt).Days);
        var freshnessScore = 1.0m - Math.Min(freshnessDays, 60) / 60.0m;

        var trustScore = Clamp(
            (0.5m * confidence) + (0.3m * evidenceCompleteness) + (0.2m * freshnessScore),
            0.0m,
            1.0m);

        var trustLevel = trustScore >= 0.75m
            ? "high"
            : trustScore >= 0.55m
                ? "medium"
                : "low";

        var riskScore = Clamp(
            CalculateRiskScore(recommendation, confidence),
            MinRiskScore,
            MaxRiskScore);

        return new RecommendationLens(
            RiskScore: Decimal.Round(riskScore, 3),
            TrustScore: Decimal.Round(trustScore, 3),
            TrustLevel: trustLevel,
            EvidenceCompleteness: Decimal.Round(evidenceCompleteness, 3),
            FreshnessDays: freshnessDays);
    }

    private static decimal CalculateRiskScore(Domain.Entities.Recommendation recommendation, decimal confidence)
    {
        var priorityWeight = recommendation.Priority?.ToLowerInvariant() switch
        {
            "critical" => 1.0m,
            "high" => 0.8m,
            "medium" => 0.5m,
            "low" => 0.2m,
            _ => 0.4m
        };

        var impactedServicesCount = CountJsonItems(recommendation.ImpactedServices);
        var impactMultiplier = 1.0m + Math.Min(impactedServicesCount, 10) * 0.02m; // up to +20%

        return priorityWeight * (0.6m + 0.4m * confidence) * impactMultiplier;
    }

    private static decimal CalculateEvidenceCompleteness(Domain.Entities.Recommendation recommendation)
    {
        var signals = new[]
        {
            recommendation.EvidenceReferences,
            recommendation.TradeoffProfile,
            recommendation.RiskProfile,
            recommendation.EstimatedImpact,
            recommendation.ImpactedServices,
            recommendation.ChangeContext
        };

        var available = signals.Count(s => CountJsonItems(s) > 0 || !string.IsNullOrWhiteSpace(s));
        return available / (decimal)signals.Length;
    }

    private static int CountJsonItems(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return 0;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.Array => doc.RootElement.GetArrayLength(),
                JsonValueKind.Object => doc.RootElement.EnumerateObject().Count(),
                JsonValueKind.String => string.IsNullOrWhiteSpace(doc.RootElement.GetString()) ? 0 : 1,
                _ => 1
            };
        }
        catch
        {
            return 0;
        }
    }

    private static decimal Clamp(decimal value, decimal min, decimal max) =>
        value < min ? min : value > max ? max : value;
}

public sealed record RecommendationLens(
    decimal RiskScore,
    decimal TrustScore,
    string TrustLevel,
    decimal EvidenceCompleteness,
    int FreshnessDays);
