using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Atlas.AgentOrchestrator.Agents;

/// <summary>
/// Technical Debt Calculator - Aggregates findings from all agents to calculate technical debt index
/// </summary>
public class TechnicalDebtCalculator
{
    private readonly ILogger<TechnicalDebtCalculator> _logger;

    public TechnicalDebtCalculator(ILogger<TechnicalDebtCalculator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Calculate technical debt index based on all agent analyses
    /// </summary>
    public TechnicalDebtIndex CalculateTechnicalDebt(
        AgentAnalysisResult architectureAnalysis,
        AgentAnalysisResult finOpsAnalysis,
        AgentAnalysisResult reliabilityAnalysis,
        AgentAnalysisResult sustainabilityAnalysis)
    {
        var activity = Activity.Current;
        activity?.SetTag("calculation.type", "technical_debt");

        // Aggregate findings by severity
        var allFindings = new List<Finding>();
        allFindings.AddRange(architectureAnalysis.Findings);
        allFindings.AddRange(finOpsAnalysis.Findings);
        allFindings.AddRange(reliabilityAnalysis.Findings);
        allFindings.AddRange(sustainabilityAnalysis.Findings);

        var criticalCount = allFindings.Count(f => f.Severity.Equals("critical", StringComparison.OrdinalIgnoreCase));
        var highCount = allFindings.Count(f => f.Severity.Equals("high", StringComparison.OrdinalIgnoreCase));
        var mediumCount = allFindings.Count(f => f.Severity.Equals("medium", StringComparison.OrdinalIgnoreCase));
        var lowCount = allFindings.Count(f => f.Severity.Equals("low", StringComparison.OrdinalIgnoreCase));

        // Calculate weighted debt score (0-100, higher is worse)
        var debtScore = (criticalCount * 25.0) + (highCount * 15.0) + (mediumCount * 7.0) + (lowCount * 3.0);
        debtScore = Math.Min(100, debtScore); // Cap at 100

        // Calculate debt level
        var debtLevel = DetermineDebtLevel(debtScore);

        // Calculate aggregate scores
        var avgScore = (architectureAnalysis.Score + finOpsAnalysis.Score + 
                       reliabilityAnalysis.Score + sustainabilityAnalysis.Score) / 4.0;
        
        var avgConfidence = (architectureAnalysis.Confidence + finOpsAnalysis.Confidence + 
                            reliabilityAnalysis.Confidence + sustainabilityAnalysis.Confidence) / 4.0;

        // Aggregate recommendations
        var allRecommendations = new List<Recommendation>();
        allRecommendations.AddRange(architectureAnalysis.Recommendations);
        allRecommendations.AddRange(finOpsAnalysis.Recommendations);
        allRecommendations.AddRange(reliabilityAnalysis.Recommendations);
        allRecommendations.AddRange(sustainabilityAnalysis.Recommendations);

        // Prioritize recommendations
        var prioritizedRecommendations = allRecommendations
            .OrderByDescending(r => GetPriorityWeight(r.Priority))
            .ThenByDescending(r => GetEffortValue(r.EstimatedEffort))
            .ToList();

        // Calculate estimated remediation effort
        var totalEffort = CalculateTotalEffort(prioritizedRecommendations);

        _logger.LogInformation(
            "Technical debt calculated: Score={Score:F2}, Level={Level}, Findings={Findings} (Critical={Critical}, High={High})",
            debtScore,
            debtLevel,
            allFindings.Count,
            criticalCount,
            highCount);

        return new TechnicalDebtIndex
        {
            DebtScore = Math.Round(debtScore, 2),
            DebtLevel = debtLevel,
            OverallMaturityScore = Math.Round(avgScore, 2),
            Confidence = Math.Round(avgConfidence, 2),
            FindingsByCategory = new FindingsSummary
            {
                Critical = criticalCount,
                High = highCount,
                Medium = mediumCount,
                Low = lowCount,
                Total = allFindings.Count
            },
            DomainScores = new DomainScores
            {
                Architecture = Math.Round(architectureAnalysis.Score, 2),
                FinOps = Math.Round(finOpsAnalysis.Score, 2),
                Reliability = Math.Round(reliabilityAnalysis.Score, 2),
                Sustainability = Math.Round(sustainabilityAnalysis.Score, 2)
            },
            PrioritizedRecommendations = prioritizedRecommendations.Take(10).ToList(), // Top 10
            EstimatedRemediationEffort = totalEffort,
            CalculatedAt = DateTime.UtcNow
        };
    }

    private string DetermineDebtLevel(double debtScore)
    {
        return debtScore switch
        {
            >= 75 => "critical",
            >= 50 => "high",
            >= 25 => "medium",
            >= 10 => "low",
            _ => "minimal"
        };
    }

    private int GetPriorityWeight(string priority)
    {
        return priority.ToLowerInvariant() switch
        {
            "critical" => 4,
            "high" => 3,
            "medium" => 2,
            "low" => 1,
            _ => 0
        };
    }

    private int GetEffortValue(string effort)
    {
        // Invert effort - prefer low effort recommendations
        return effort.ToLowerInvariant() switch
        {
            "low" => 3,
            "medium" => 2,
            "high" => 1,
            _ => 0
        };
    }

    private string CalculateTotalEffort(List<Recommendation> recommendations)
    {
        var lowCount = recommendations.Count(r => r.EstimatedEffort.Equals("low", StringComparison.OrdinalIgnoreCase));
        var mediumCount = recommendations.Count(r => r.EstimatedEffort.Equals("medium", StringComparison.OrdinalIgnoreCase));
        var highCount = recommendations.Count(r => r.EstimatedEffort.Equals("high", StringComparison.OrdinalIgnoreCase));

        var totalPoints = (lowCount * 1) + (mediumCount * 3) + (highCount * 8);

        return totalPoints switch
        {
            >= 30 => "high (3-6 months)",
            >= 15 => "medium (1-3 months)",
            >= 5 => "low (< 1 month)",
            _ => "minimal (< 1 week)"
        };
    }
}

/// <summary>
/// Technical debt index result
/// </summary>
public class TechnicalDebtIndex
{
    public double DebtScore { get; set; }
    public string DebtLevel { get; set; } = string.Empty;
    public double OverallMaturityScore { get; set; }
    public double Confidence { get; set; }
    public FindingsSummary FindingsByCategory { get; set; } = new();
    public DomainScores DomainScores { get; set; } = new();
    public List<Recommendation> PrioritizedRecommendations { get; set; } = new();
    public string EstimatedRemediationEffort { get; set; } = string.Empty;
    public DateTime CalculatedAt { get; set; }
}

public class FindingsSummary
{
    public int Critical { get; set; }
    public int High { get; set; }
    public int Medium { get; set; }
    public int Low { get; set; }
    public int Total { get; set; }
}

public class DomainScores
{
    public double Architecture { get; set; }
    public double FinOps { get; set; }
    public double Reliability { get; set; }
    public double Sustainability { get; set; }
}
