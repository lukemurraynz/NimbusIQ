using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace Atlas.AgentOrchestrator.Agents;

/// <summary>
/// T1.3: Drift Detection Agent - Continuous compliance monitoring and drift tracking
/// Compares current state vs desired state, tracks drift over time with historical trending
/// </summary>
public class DriftDetectionAgent
{
    private readonly ILogger<DriftDetectionAgent> _logger;
    private readonly BestPracticeEngine _bestPracticeEngine;

    public DriftDetectionAgent(
        ILogger<DriftDetectionAgent> logger,
        BestPracticeEngine bestPracticeEngine)
    {
        _logger = logger;
        _bestPracticeEngine = bestPracticeEngine;
    }

    /// <summary>
    /// Detect drift for a service group
    /// </summary>
    public async Task<DriftAnalysisResult> AnalyzeDriftAsync(
        DiscoverySnapshot currentSnapshot,
        DriftSnapshot? previousSnapshot,
        CancellationToken cancellationToken = default)
    {
        var activity = Activity.Current;
        activity?.SetTag("drift.serviceGroupId", currentSnapshot.ServiceGroupId);
        activity?.SetTag("drift.hasPreviousSnapshot", previousSnapshot != null);

        try
        {
            // Evaluate current state against best practices
            var evaluation = await _bestPracticeEngine.EvaluateAsync(currentSnapshot, cancellationToken);

            // Calculate drift score (0-100, lower is better)
            var driftScore = CalculateDriftScore(evaluation);

            // Compare with previous snapshot if available
            var trend = previousSnapshot != null
                ? CalculateTrend(evaluation, previousSnapshot)
                : new DriftTrend { Status = "baseline", Message = "First drift analysis" };

            // Generate prioritized remediation paths
            var remediationPaths = GenerateRemediationPaths(evaluation);

            var result = new DriftAnalysisResult
            {
                ServiceGroupId = currentSnapshot.ServiceGroupId,
                AnalysisRunId = currentSnapshot.AnalysisRunId,
                AnalyzedAt = DateTime.UtcNow,
                DriftScore = driftScore,
                TotalViolations = evaluation.TotalViolations,
                CriticalViolations = evaluation.CriticalViolations,
                HighViolations = evaluation.HighViolations,
                MediumViolations = evaluation.MediumViolations,
                LowViolations = evaluation.LowViolations,
                CategoryBreakdown = CalculateCategoryBreakdown(evaluation),
                Trend = trend,
                RemediationPaths = remediationPaths,
                Violations = evaluation.Violations
            };

            _logger.LogInformation(
                "Drift analysis complete for service group {ServiceGroupId}: Drift score={DriftScore:F2}, Violations={TotalViolations} ({Critical} critical), Trend={Trend}",
                currentSnapshot.ServiceGroupId,
                driftScore,
                evaluation.TotalViolations,
                evaluation.CriticalViolations,
                trend.Status);

            activity?.SetTag("drift.score", driftScore);
            activity?.SetTag("drift.trend", trend.Status);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze drift for service group {ServiceGroupId}", currentSnapshot.ServiceGroupId);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Calculate drift score (0-100, lower is better)
    /// Weighted by severity: Critical=10, High=5, Medium=2, Low=1
    /// </summary>
    private decimal CalculateDriftScore(BestPracticeEvaluationResult evaluation)
    {
        if (evaluation.TotalViolations == 0)
            return 0;

        // Weight violations by severity
        var weightedScore =
            (evaluation.CriticalViolations * 10) +
            (evaluation.HighViolations * 5) +
            (evaluation.MediumViolations * 2) +
            (evaluation.LowViolations * 1);

        // Normalize to 0-100 scale (cap at 100)
        var normalizedScore = Math.Min(weightedScore, 100);

        return normalizedScore;
    }

    /// <summary>
    /// Calculate trend by comparing with previous snapshot
    /// </summary>
    private DriftTrend CalculateTrend(BestPracticeEvaluationResult current, DriftSnapshot previous)
    {
        var currentScore = CalculateDriftScore(current);
        var previousScore = previous.DriftScore;

        var scoreDelta = currentScore - previousScore;
        var violationDelta = current.TotalViolations - previous.TotalViolations;

        string status;
        string message;

        if (scoreDelta < -5) // Improved by more than 5 points
        {
            status = "improving";
            message = $"Drift reduced by {Math.Abs(scoreDelta):F1} points ({violationDelta} fewer violations)";
        }
        else if (scoreDelta > 5) // Degraded by more than 5 points
        {
            status = "degrading";
            message = $"Drift increased by {scoreDelta:F1} points ({Math.Abs(violationDelta)} new violations)";
        }
        else
        {
            status = "stable";
            message = $"Drift score stable at {currentScore:F1} ({current.TotalViolations} violations)";
        }

        return new DriftTrend
        {
            Status = status,
            Message = message,
            ScoreDelta = scoreDelta,
            ViolationDelta = violationDelta,
            PreviousScore = previousScore,
            CurrentScore = currentScore
        };
    }

    /// <summary>
    /// Calculate violations by category
    /// </summary>
    private Dictionary<string, int> CalculateCategoryBreakdown(BestPracticeEvaluationResult evaluation)
    {
        var breakdown = new Dictionary<string, int>();

        foreach (var violation in evaluation.Violations)
        {
            var category = string.IsNullOrWhiteSpace(violation.Category) ? "General" : violation.Category;

            if (!breakdown.ContainsKey(category))
                breakdown[category] = 0;

            breakdown[category]++;
        }

        return breakdown;
    }

    /// <summary>
    /// Generate prioritized remediation paths
    /// Groups violations by common remediation actions
    /// </summary>
    private List<RemediationPath> GenerateRemediationPaths(BestPracticeEvaluationResult evaluation)
    {
        var paths = new List<RemediationPath>();

        // Group by resource type and remediation action
        var violationGroups = evaluation.Violations
            .GroupBy(v => new { v.ResourceType, RuleId = v.RuleId })
            .OrderByDescending(g => g.Count())
            .Take(10); // Top 10 most impactful remediation paths

        foreach (var group in violationGroups)
        {
            var sample = group.First();
            var impact = group.Sum(v => GetImpactScore(v.Severity));

            paths.Add(new RemediationPath
            {
                Title = $"Remediate {group.Key.ResourceType} violations",
                Description = $"Fix {group.Count()} resources affected by rule {group.Key.RuleId}",
                AffectedResources = group.Count(),
                EstimatedImpact = impact,
                Priority = GetPriority(sample.Severity, group.Count()),
                Resources = group.Select(v => v.ResourceId).ToList()
            });
        }

        return paths;
    }

    private int GetImpactScore(string severity)
    {
        return severity switch
        {
            "Critical" => 10,
            "High" => 5,
            "Medium" => 2,
            "Low" => 1,
            _ => 0
        };
    }

    private string GetPriority(string severity, int count)
    {
        var impact = GetImpactScore(severity) * count;

        return impact switch
        {
            >= 50 => "critical",
            >= 20 => "high",
            >= 10 => "medium",
            _ => "low"
        };
    }
}

// DTOs
public class DriftAnalysisResult
{
    public Guid ServiceGroupId { get; set; }
    public Guid? AnalysisRunId { get; set; }
    public DateTime AnalyzedAt { get; set; }
    public decimal DriftScore { get; set; }
    public int TotalViolations { get; set; }
    public int CriticalViolations { get; set; }
    public int HighViolations { get; set; }
    public int MediumViolations { get; set; }
    public int LowViolations { get; set; }
    public Dictionary<string, int> CategoryBreakdown { get; set; } = new();
    public DriftTrend Trend { get; set; } = new() { Status = "baseline", Message = "No previous data" };
    public List<RemediationPath> RemediationPaths { get; set; } = new();
    public List<BestPracticeViolation> Violations { get; set; } = new();
}

public class DriftTrend
{
    public required string Status { get; set; } // improving|degrading|stable|baseline
    public required string Message { get; set; }
    public decimal ScoreDelta { get; set; }
    public int ViolationDelta { get; set; }
    public decimal PreviousScore { get; set; }
    public decimal CurrentScore { get; set; }
}

public class RemediationPath
{
    public required string Title { get; set; }
    public required string Description { get; set; }
    public int AffectedResources { get; set; }
    public int EstimatedImpact { get; set; }
    public required string Priority { get; set; } // critical|high|medium|low
    public List<string> Resources { get; set; } = new();
}

public class DriftSnapshot
{
    public Guid Id { get; set; }
    public Guid ServiceGroupId { get; set; }
    public DateTime SnapshotTime { get; set; }
    public int TotalViolations { get; set; }
    public int CriticalViolations { get; set; }
    public int HighViolations { get; set; }
    public int MediumViolations { get; set; }
    public int LowViolations { get; set; }
    public decimal DriftScore { get; set; }
    public string? CategoryBreakdown { get; set; }
    public string? TrendAnalysis { get; set; }
    public DateTime CreatedAt { get; set; }
}
