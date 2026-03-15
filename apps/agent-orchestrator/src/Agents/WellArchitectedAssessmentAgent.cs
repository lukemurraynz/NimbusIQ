using Atlas.AgentOrchestrator.Integrations.Azure;
using Atlas.AgentOrchestrator.Integrations.Prompts;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace Atlas.AgentOrchestrator.Agents;

/// <summary>
/// T3.1: Well-Architected Framework Assessment Agent
/// Maps existing scores to WAF pillars and generates comprehensive assessment reports
/// Aligns with Azure Well-Architected Framework guidance
/// </summary>
public class WellArchitectedAssessmentAgent
{
    private readonly ILogger<WellArchitectedAssessmentAgent> _logger;
    private readonly BestPracticeEngine _bestPracticeEngine;
    private readonly DriftDetectionAgent _driftDetectionAgent;
    private readonly IAzureAIFoundryClient? _foundryClient;
    private readonly IPromptProvider? _promptProvider;

    public WellArchitectedAssessmentAgent(
        ILogger<WellArchitectedAssessmentAgent> logger,
        BestPracticeEngine bestPracticeEngine,
        DriftDetectionAgent driftDetectionAgent,
        IAzureAIFoundryClient? foundryClient = null,
        IPromptProvider? promptProvider = null)
    {
        _logger = logger;
        _bestPracticeEngine = bestPracticeEngine;
        _driftDetectionAgent = driftDetectionAgent;
        _foundryClient = foundryClient;
        _promptProvider = promptProvider;
    }

    /// <summary>
    /// Conduct a full Well-Architected Framework assessment
    /// </summary>
    public async Task<WafAssessmentResult> ConductAssessmentAsync(
        DiscoverySnapshot snapshot,
        Dictionary<string, decimal>? existingScores = null,
        CancellationToken cancellationToken = default)
    {
        var activity = Activity.Current;
        activity?.SetTag("waf.serviceGroupId", snapshot.ServiceGroupId);

        try
        {
            // Map existing scores to WAF pillars
            var pillarScores = MapToWafPillars(existingScores);

            // Evaluate WAF-specific rules
            var wafRules = await _bestPracticeEngine.EvaluateAsync(snapshot, cancellationToken);
            var wafViolations = wafRules.Violations.ToList();

            // Calculate compliance per pillar (use RuleId to filter later if needed)
            var pillarCompliance = CalculatePillarCompliance(wafViolations, pillarScores);

            // Generate assessment summary
            var assessmentSummary = GenerateAssessmentSummary(pillarCompliance, wafViolations);

            // Create recommendations per pillar
            var pillarRecommendations = GeneratePillarRecommendations(pillarCompliance, wafViolations);

            var result = new WafAssessmentResult
            {
                ServiceGroupId = snapshot.ServiceGroupId,
                AnalysisRunId = snapshot.AnalysisRunId,
                AssessmentDate = DateTime.UtcNow,
                FrameworkVersion = "2024",
                OverallScore = CalculateOverallWafScore(pillarCompliance),
                PillarScores = pillarCompliance,
                TotalRules = wafRules.TotalViolations + (wafRules.Violations.Count - wafRules.TotalViolations),
                PassedRules = wafRules.Violations.Count - wafRules.TotalViolations,
                FailedRules = wafRules.TotalViolations,
                CompliancePercentage = CalculateCompliancePercentage(wafRules),
                AssessmentSummary = assessmentSummary,
                PillarRecommendations = pillarRecommendations,
                Violations = wafViolations
            };

            _logger.LogInformation(
                "WAF assessment complete for service group {ServiceGroupId}: Overall score={OverallScore:F2}, Compliance={Compliance:F1}%",
                snapshot.ServiceGroupId,
                result.OverallScore,
                result.CompliancePercentage);

            activity?.SetTag("waf.overallScore", result.OverallScore);
            activity?.SetTag("waf.compliance", result.CompliancePercentage);

            // Enrich with AI narrative for pillars scoring below threshold
            if (_foundryClient != null)
            {
                result.AINarrativeSummary = await GenerateAINarrativeAsync(result, cancellationToken);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to conduct WAF assessment for service group {ServiceGroupId}", snapshot.ServiceGroupId);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private async Task<string?> GenerateAINarrativeAsync(WafAssessmentResult result, CancellationToken cancellationToken)
    {
        try
        {
            var atRiskPillars = result.PillarScores
                .Where(kvp => kvp.Value.Score < 70)
                .OrderBy(kvp => kvp.Value.Score)
                .Select(kvp => $"{kvp.Key}: {kvp.Value.Score:F0}/100 ({kvp.Value.ViolationCount} violations, {kvp.Value.CriticalIssues} critical)")
                .ToList();

            if (_promptProvider is null)
            {
                throw new InvalidOperationException("Prompt provider is required for WAF narrative generation.");
            }

            var prompt = _promptProvider.Render(
                "waf-narrative",
                new Dictionary<string, string>
                {
                    ["OverallScore"] = result.OverallScore.ToString("F0"),
                    ["CompliancePercentage"] = result.CompliancePercentage.ToString("F0"),
                    ["AtRiskPillars"] = atRiskPillars.Any() ? string.Join("\n", atRiskPillars) : "All pillars meeting baseline"
                });

            return await _foundryClient!.SendPromptAsync(prompt, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI narrative generation failed for WAF assessment; continuing without narrative");
            return $"AI narrative unavailable (score: {result.OverallScore:F0}/100, compliance: {result.CompliancePercentage:F0}%). Review pillar scores for details.";
        }
    }

    /// <summary>
    /// Map existing analysis scores to WAF pillars
    /// </summary>
    private Dictionary<string, PillarScore> MapToWafPillars(Dictionary<string, decimal>? scores)
    {
        return new Dictionary<string, PillarScore>
        {
            ["Security"] = new PillarScore
            {
                Name = "Security",
                Score = scores?.GetValueOrDefault("Security", 0) ?? 0,
                Weight = 1.0m,
                Status = GetPillarStatus(scores?.GetValueOrDefault("Security", 0) ?? 0),
                KeyFindings = new List<string>
                {
                    "Identity and access management evaluation",
                    "Network security posture",
                    "Data protection compliance"
                }
            },
            ["Reliability"] = new PillarScore
            {
                Name = "Reliability",
                Score = scores?.GetValueOrDefault("Reliability", 0) ?? 0,
                Weight = 1.0m,
                Status = GetPillarStatus(scores?.GetValueOrDefault("Reliability", 0) ?? 0),
                KeyFindings = new List<string>
                {
                    "Availability and resilience design",
                    "Disaster recovery readiness",
                    "Monitoring and telemetry coverage"
                }
            },
            ["PerformanceEfficiency"] = new PillarScore
            {
                Name = "Performance Efficiency",
                Score = scores?.GetValueOrDefault("Architecture", 0) ?? 0,
                Weight = 0.8m,
                Status = GetPillarStatus(scores?.GetValueOrDefault("Architecture", 0) ?? 0),
                KeyFindings = new List<string>
                {
                    "Compute and storage optimization",
                    "Scaling strategy alignment",
                    "Modern architecture patterns adoption"
                }
            },
            ["CostOptimization"] = new PillarScore
            {
                Name = "Cost Optimization",
                Score = scores?.GetValueOrDefault("FinOps", 0) ?? 0,
                Weight = 1.0m,
                Status = GetPillarStatus(scores?.GetValueOrDefault("FinOps", 0) ?? 0),
                KeyFindings = new List<string>
                {
                    "Resource right-sizing opportunities",
                    "Commitment-based discount adoption",
                    "Cost allocation and tagging"
                }
            },
            ["OperationalExcellence"] = new PillarScore
            {
                Name = "Operational Excellence",
                Score = ((scores?.GetValueOrDefault("Reliability", 0) ?? 0) + (scores?.GetValueOrDefault("Architecture", 0) ?? 0)) / 2,
                Weight = 0.9m,
                Status = GetPillarStatus(((scores?.GetValueOrDefault("Reliability", 0) ?? 0) + (scores?.GetValueOrDefault("Architecture", 0) ?? 0)) / 2),
                KeyFindings = new List<string>
                {
                    "DevOps maturity and automation",
                    "Observability and incident response",
                    "Infrastructure as Code adoption"
                }
            }
        };
    }

    private string GetPillarStatus(decimal score)
    {
        return score switch
        {
            >= 80 => "Excellent",
            >= 60 => "Good",
            >= 40 => "NeedsImprovement",
            _ => "AtRisk"
        };
    }

    private Dictionary<string, PillarScore> CalculatePillarCompliance(
        List<BestPracticeViolation> violations,
        Dictionary<string, PillarScore> baselineScores)
    {
        // Route each violation to its primary WAF pillar via the violation's Category field,
        // which is populated from BestPracticeRule.Category by BestPracticeEngine.EvaluateRuleAsync.
        // Cross-cutting violations (no clear pillar) apply a reduced penalty to every pillar.
        var crossCutting = violations
            .Where(v => (v.Severity == "Critical" || v.Severity == "High") &&
                        string.IsNullOrEmpty(MapCategoryToPillar(v.Category)))
            .ToList();

        foreach (var (pillar, score) in baselineScores)
        {
            var primary = violations
                .Where(v => (v.Severity == "Critical" || v.Severity == "High") &&
                            MapCategoryToPillar(v.Category) == pillar)
                .ToList();

            var reduction = primary.Take(10).Sum(v => v.Severity switch
            {
                "Critical" => 15m,
                "High" => 10m,
                _ => 0m
            });

            // Cross-cutting violations apply 1/3 weight to each pillar
            reduction += crossCutting.Take(5).Sum(v => v.Severity switch
            {
                "Critical" => 5m,
                "High" => 3m,
                _ => 0m
            });

            if (reduction > 0)
            {
                score.Score = Math.Max(0, score.Score - reduction);
                score.ViolationCount = primary.Count + crossCutting.Count;
                score.CriticalIssues = primary.Count(v => v.Severity == "Critical");
            }
        }

        return baselineScores;
    }

    /// <summary>Maps a violation's Category string to its WAF pillar dictionary key.</summary>
    private static string MapCategoryToPillar(string? category)
    {
        return (category ?? "").ToLowerInvariant() switch
        {
            var c when c.Contains("security") || c.Contains("identity") || c.Contains("auth") => "Security",
            var c when c.Contains("reliab") || c.Contains("availab") => "Reliability",
            var c when c.Contains("cost") || c.Contains("finops") || c.Contains("budget") => "CostOptimization",
            var c when c.Contains("performance") || c.Contains("scalab") || c.Contains("architecture") => "PerformanceEfficiency",
            var c when c.Contains("operation") || c.Contains("devops") || c.Contains("monitor") => "OperationalExcellence",
            _ => ""
        };
    }

    private decimal CalculateOverallWafScore(Dictionary<string, PillarScore> pillarScores)
    {
        var totalWeight = pillarScores.Values.Sum(p => p.Weight);
        var weightedSum = pillarScores.Values.Sum(p => p.Score * p.Weight);
        return totalWeight > 0 ? weightedSum / totalWeight : 0;
    }

    private decimal CalculateCompliancePercentage(BestPracticeEvaluationResult evaluation)
    {
        var totalRules = evaluation.TotalViolations + (evaluation.Violations.Count - evaluation.TotalViolations);
        return totalRules > 0 ? ((decimal)(totalRules - evaluation.TotalViolations) / totalRules) * 100 : 100;
    }

    private string GenerateAssessmentSummary(
        Dictionary<string, PillarScore> pillarScores,
        List<BestPracticeViolation> violations)
    {
        var summary = new
        {
            OverallHealth = GetOverallHealth(pillarScores),
            PillarSummary = pillarScores.Select(kvp => new
            {
                Pillar = kvp.Key,
                Score = kvp.Value.Score,
                Status = kvp.Value.Status,
                Violations = kvp.Value.ViolationCount,
                Critical = kvp.Value.CriticalIssues
            }).ToList(),
            TopIssues = violations
                .OrderByDescending(v => v.Severity == "Critical" ? 4 : v.Severity == "High" ? 3 : v.Severity == "Medium" ? 2 : 1)
                .Take(10)
                .Select(v => new
                {
                    ViolationType = v.ViolationType,
                    Severity = v.Severity,
                    RuleId = v.RuleId,
                    Resource = v.ResourceId
                })
                .ToList(),
            RecommendedActions = GetRecommendedActions(pillarScores)
        };

        return JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
    }

    private string GetOverallHealth(Dictionary<string, PillarScore> pillarScores)
    {
        var avgScore = pillarScores.Values.Average(p => p.Score);
        var criticalCount = pillarScores.Values.Sum(p => p.CriticalIssues);

        if (criticalCount > 5) return "Critical";
        if (avgScore >= 80) return "Excellent";
        if (avgScore >= 60) return "Good";
        if (avgScore >= 40) return "NeedsAttention";
        return "AtRisk";
    }

    private List<string> GetRecommendedActions(Dictionary<string, PillarScore> pillarScores)
    {
        var actions = new List<string>();

        foreach (var (pillar, score) in pillarScores.OrderBy(kvp => kvp.Value.Score))
        {
            if (score.Score < 60)
            {
                actions.Add($"Priority: Address {pillar} issues (Score: {score.Score:F0}/100)");
            }

            if (score.CriticalIssues > 0)
            {
                actions.Add($"Urgent: Remediate {score.CriticalIssues} critical {pillar} issues");
            }
        }

        if (!actions.Any())
        {
            actions.Add("Continue monitoring and maintain current standards");
        }

        return actions;
    }

    private Dictionary<string, List<string>> GeneratePillarRecommendations(
        Dictionary<string, PillarScore> pillarScores,
        List<BestPracticeViolation> violations)
    {
        var recommendations = new Dictionary<string, List<string>>();

        foreach (var (pillar, score) in pillarScores)
        {
            var pillarRecs = new List<string>();

            // Add general remediation guidance based on score
            if (score.Score < 70)
            {
                pillarRecs.Add($"Review and address {pillar} violations");
                pillarRecs.Add($"Implement {pillar} best practices from WAF guidance");
            }

            // Add generic recommendations based on score
            if (score.Score < 60)
            {
                pillarRecs.Add($"Conduct focused review of {pillar} practices");
                pillarRecs.Add($"Establish baseline monitoring for {pillar} metrics");
            }

            recommendations[pillar] = pillarRecs;
        }

        return recommendations;
    }
}

// Result models
public class WafAssessmentResult
{
    public Guid ServiceGroupId { get; set; }
    public Guid? AnalysisRunId { get; set; }
    public DateTime AssessmentDate { get; set; }
    public required string FrameworkVersion { get; set; }
    public decimal OverallScore { get; set; }
    public required Dictionary<string, PillarScore> PillarScores { get; set; }
    public int TotalRules { get; set; }
    public int PassedRules { get; set; }
    public int FailedRules { get; set; }
    public decimal CompliancePercentage { get; set; }
    public required string AssessmentSummary { get; set; }
    public required Dictionary<string, List<string>> PillarRecommendations { get; set; }
    public required List<BestPracticeViolation> Violations { get; set; }
    /// <summary>AI-generated narrative summary enriched by Azure AI Foundry (null when AI is not configured)</summary>
    public string? AINarrativeSummary { get; set; }
}

public class PillarScore
{
    public required string Name { get; set; }
    public decimal Score { get; set; }
    public decimal Weight { get; set; } = 1.0m;
    public required string Status { get; set; }
    public required List<string> KeyFindings { get; set; }
    public int ViolationCount { get; set; }
    public int CriticalIssues { get; set; }
}


