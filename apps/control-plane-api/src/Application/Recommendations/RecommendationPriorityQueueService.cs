namespace Atlas.ControlPlane.Application.Recommendations;

/// <summary>
/// Computes a risk-weighted prioritization queue for recommendations.
/// Higher score means the item should be triaged sooner.
/// </summary>
public static class RecommendationPriorityQueueService
{
    public static double CalculateRiskWeightedScore(Domain.Entities.Recommendation recommendation, DateTime utcNow)
    {
        var lens = RecommendationLensCalculator.Calculate(recommendation, utcNow);
        var normalizedPriority = recommendation.Priority?.ToLowerInvariant() ?? string.Empty;

        decimal priorityWeight = normalizedPriority switch
        {
            "critical" => 1.00m,
            "high" => 0.80m,
            "medium" => 0.55m,
            "low" => 0.30m,
            _ => 0.45m
        };

        var daysToExpiry = recommendation.ValidUntil.HasValue
            ? (recommendation.ValidUntil.Value - utcNow).TotalDays
            : 30;

        decimal urgency = daysToExpiry switch
        {
            <= 0 => 1.00m,
            <= 2 => 0.90m,
            <= 7 => 0.75m,
            <= 14 => 0.55m,
            <= 30 => 0.35m,
            _ => 0.20m
        };

        var trustPenalty = 1.0m - Decimal.Clamp(lens.TrustScore, 0.0m, 1.0m);
        var risk = Decimal.Clamp(lens.RiskScore, 0.0m, 1.0m);

        var score = (risk * 0.50m) +
                    (urgency * 0.25m) +
                    (priorityWeight * 0.15m) +
                    (trustPenalty * 0.10m);

        return Math.Round((double)Decimal.Clamp(score, 0.0m, 1.0m), 4);
    }

    public static string BuildReason(Domain.Entities.Recommendation recommendation, DateTime utcNow)
    {
        var lens = RecommendationLensCalculator.Calculate(recommendation, utcNow);
        var parts = new List<string>();

        if (lens.RiskScore >= 0.75m)
        {
            parts.Add("high risk exposure");
        }

        if (recommendation.ValidUntil.HasValue)
        {
            var remaining = Math.Ceiling((recommendation.ValidUntil.Value - utcNow).TotalDays);
            if (remaining <= 7)
            {
                parts.Add($"expires in {Math.Max(0, (int)remaining)}d");
            }
        }

        if (lens.TrustScore < 0.55m)
        {
            parts.Add("requires trust review");
        }

        if (parts.Count == 0)
        {
            parts.Add("balanced default priority");
        }

        return string.Join(", ", parts);
    }
}
