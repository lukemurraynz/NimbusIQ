using System.Text.Json;
using Atlas.ControlPlane.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Atlas.ControlPlane.Application.Services;

/// <summary>
/// Generates intelligent, actionable insights about top impact factors that affect pillar scores.
/// Uses AI Foundry Agent Framework when available, with rule-based fallback for local development.
/// </summary>
public interface IImpactFactorInsightService
{
  /// <summary>
  /// Generate enhanced insights for a top impact factor.
  /// </summary>
  Task<EnhancedImpactFactorInsight> GenerateInsightAsync(
      string category,
      string factorName,
      int affectedResourceCount,
      string severity,
      ScoreResult scores,
      IReadOnlyList<BestPracticeViolation> relatedViolations,
      CancellationToken cancellationToken = default);

  /// <summary>
  /// Batch generate insights for multiple factors (optimized for AI throughput).
  /// </summary>
  Task<IReadOnlyList<EnhancedImpactFactorInsight>> GenerateInsightsAsync(
      string category,
      IReadOnlyList<TopImpactFactorInput> factors,
      ScoreResult scores,
      IReadOnlyList<BestPracticeViolation> allViolations,
      CancellationToken cancellationToken = default);
}

/// <summary>
/// Input structure for generating insights on a factor.
/// </summary>
public record TopImpactFactorInput(
    string FactorName,
    int AffectedResourceCount,
    string Severity,
    IReadOnlyList<string>? RelatedResourceTypes = null,
    string? RuleId = null);

/// <summary>
/// Enhanced impact factor with AI-generated insight and remediation advice.
/// </summary>
public record EnhancedImpactFactorInsight(
    string FactorName,
    int AffectedCount,
    string Severity,
    string ShortDescription,
    string ActionableLongDescription,
    IReadOnlyList<string> RemediationSteps,
    string ConfidenceLevel,
    string GeneratedBy = "rule_engine",
    IReadOnlyList<string>? AffectedResourceTypes = null)
{
  /// <summary>
  /// Export as JSON for frontend consumption.
  /// </summary>
  public string ToJson() => JsonSerializer.Serialize(this);
}

/// <summary>
/// Default implementation using rule-based insights with optional AI enhancement.
/// </summary>
public class ImpactFactorInsightService : IImpactFactorInsightService
{
  private readonly AIChatService? _aiChatService;
  private readonly ILogger<ImpactFactorInsightService> _logger;

  // Default rule-based insight templates by category and pattern
  private static readonly Dictionary<string, Dictionary<string, InsightTemplate>> InsightTemplates = new()
    {
        {
            "Reliability", new()
            {
                {
                    "availability", new(
                        shortDescription: "Availability gaps affecting uptime",
                        longDescription: "Your infrastructure has availability gaps in critical zones. {affectedCount} resources lack redundancy or failover mechanisms. High availability is essential for SLA targets.",
                        remediationSteps: new[]
                        {
                            "Review resource distribution across zones",
                            "Use Azure regions with zone-redundant deployment options",
                            "Enable Health Probes and Auto-Healing for critical services",
                            "Configure Azure Traffic Manager for geographic failover",
                            "Implement Circuit Breaker patterns in application code"
                        })
                },
                {
                    "security", new(
                        shortDescription: "Security controls limiting reliability posture",
                        longDescription: "Security misconfigurations are limiting your reliability posture. {affectedCount} resources have weak isolation or missing authentication controls. Critical services need strong identity and access controls.",
                        remediationSteps: new[]
                        {
                            "Review and strengthen network segmentation (NSGs, UDRs)",
                            "Enable private endpoints for data plane services",
                            "Use Managed Identities instead of connection strings",
                            "Implement Azure Policy to enforce security controls",
                            "Configure Key Vault for secrets rotation"
                        })
                },
                {
                    "resiliency", new(
                        shortDescription: "Resource resiliency gaps detected",
                        longDescription: "Resiliency patterns are missing from {affectedCount} resources. Without graceful degradation, failures cascade quickly. Implement circuit breakers, retries, and bulkheads.",
                        remediationSteps: new[]
                        {
                            "Implement exponential backoff and circuit breakers (Polly.NET)",
                            "Use Azure Service Bus Dead Letter Queues",
                            "Enable auto-scaling based on load and health metrics",
                            "Configure timeouts and health checks",
                            "Use Chaos Engineering (Azure Chaos Studio) to validate resilience"
                        })
                }
            }
        },
        {
            "FinOps", new()
            {
                {
                    "costEfficiency", new(
                        shortDescription: "Cost optimization opportunities identified",
                        longDescription: "Your cost efficiency is {percentile}. {affectedCount} resources are either oversized, underutilized, or running in expensive SKUs. Right-sizing these resources can reduce costs by 20-40%.",
                        remediationSteps: new[]
                        {
                            "Use Azure Advisor cost recommendations",
                            "Resize VMs based on CPU/memory utilization metrics",
                            "Move to Spot VM instances for non-critical workloads",
                            "Review and consolidate storage accounts",
                            "Migrate to Reserved Instances for predictable workloads",
                            "Use Azure Autoscale to match capacity to demand"
                        })
                },
                {
                    "taggingCoverage", new(
                        shortDescription: "Resource tagging gaps prevent cost allocation",
                        longDescription: "{affectedCount} resources lack proper cost center or business unit tags. Without tagging, chargeback is impossible and cost visibility is lost.",
                        remediationSteps: new[]
                        {
                            "Define tagging strategy (cost center, environment, owner)",
                            "Use Azure Policy to enforce tags on creation",
                            "Use bulk tagging via Azure CLI or Azure Resource Graph",
                            "Set up cost alerts in Cost Management using tag dimensions",
                            "Implement governance via Policy initiatives"
                        })
                },
                {
                    "resourceUtilization", new(
                        shortDescription: "Underutilized resources detected",
                        longDescription: "{affectedCount} resources show low utilization (CPU <5%, network <10%). These are candidates for decommissioning or downsizing.",
                        remediationSteps: new[]
                        {
                            "Export resource utilization from Azure Monitor",
                            "Set cleanup schedules for resources with no recent compute",
                            "Use Azure Autoscale to shut down unused non-prod resources",
                            "Consolidate low-traffic workloads onto smaller SKUs",
                            "Enable Auto-shutdown on non-production Dev/Test VMs"
                        })
                }
            }
        },
        {
            "Architecture", new()
            {
                {
                    "completeness", new(
                        shortDescription: "Metadata completeness gaps affecting governance",
                        longDescription: "{affectedCount} resources lack required metadata fields (tags, SKU, region). This prevents accurate reporting and governance validation. Completeness is {percentile}.",
                        remediationSteps: new[]
                        {
                            "Run Azure Resource Graph queries to identify missing metadata",
                            "Use Azure Policy to enforce tagging on resource creation",
                            "Use Resource Graph bulk tagging to fill gaps",
                            "Audit SKU metadata capture in resource definitions",
                            "Schedule monthly completeness reports"
                        })
                }
            }
        },
        {
            "Sustainability", new()
            {
                {
                    "resourceUtilization", new(
                        shortDescription: "Unused resources driving unnecessary carbon footprint",
                        longDescription: "{affectedCount} resources are underutilized, consuming energy without proportional value. Rightsizing reduces both costs and carbon emissions.",
                        remediationSteps: new[]
                        {
                            "Monitor CPU and memory utilization via Azure Monitor",
                            "Right-size or consolidate underutilized VMs",
                            "Enable power-off schedules for non-production resources",
                            "Use Azure Autoscale to match resources to demand",
                            "Track carbon impact via Azure Carbon Optimization Report"
                        })
                },
                {
                    "carbonSignal", new(
                        shortDescription: "Carbon-intensive workload placement detected",
                        longDescription: "Your workloads are running in regions with high carbon intensity. {affectedCount} resources could benefit from region migration. Carbon intensity varies 10x across regions.",
                        remediationSteps: new[]
                        {
                            "Check Azure's carbon intensity map by region",
                            "Migrate non-latency-critical workloads to green regions",
                            "Use Azure Sustainability Calculator to estimate impact",
                            "Consider multi-region for resilience + carbon optimization",
                            "Schedule workloads during low-carbon-intensity windows"
                        })
                }
            }
        },
        {
            "Security", new()
            {
                {
                    "security", new(
                        shortDescription: "Critical security controls missing",
                        longDescription: "{affectedCount} resources have security gaps: missing identities, weak encryption, or unpatched software. These require immediate remediation.",
                        remediationSteps: new[]
                        {
                            "Enable Defender for Cloud to assess compliance",
                            "Use Azure Policy to enforce resource locks and encryption",
                            "Implement Managed Identity on all application services",
                            "Enable automatic patching for VMs (Update Management)",
                            "Configure Network Security Groups with least-privilege rules",
                            "Enable Azure Policy for PCI-DSS, CIS, or SOC2 benchmarks"
                        })
                }
            }
        }
    };

  public ImpactFactorInsightService(
      ILogger<ImpactFactorInsightService> logger,
      AIChatService? aiChatService = null)
  {
    _logger = logger;
    _aiChatService = aiChatService;
  }

  public async Task<EnhancedImpactFactorInsight> GenerateInsightAsync(
      string category,
      string factorName,
      int affectedResourceCount,
      string severity,
      ScoreResult scores,
      IReadOnlyList<BestPracticeViolation> relatedViolations,
      CancellationToken cancellationToken = default)
  {
    var insights = await GenerateInsightsAsync(
        category,
        new[] { new TopImpactFactorInput(factorName, affectedResourceCount, severity) },
        scores,
        relatedViolations,
        cancellationToken);

    return insights.FirstOrDefault()
        ?? throw new InvalidOperationException($"Failed to generate insight for factor '{factorName}'");
  }

  public async Task<IReadOnlyList<EnhancedImpactFactorInsight>> GenerateInsightsAsync(
      string category,
      IReadOnlyList<TopImpactFactorInput> factors,
      ScoreResult scores,
      IReadOnlyList<BestPracticeViolation> allViolations,
      CancellationToken cancellationToken = default)
  {
    if (factors.Count == 0)
      return Array.Empty<EnhancedImpactFactorInsight>();

    // Try AI-enhanced insights first if available
    if (_aiChatService?.IsAIAvailable == true)
    {
      try
      {
        return await GenerateAIInsightsAsync(category, factors, scores, allViolations, cancellationToken);
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "AI insight generation failed; falling back to rule-based insights");
      }
    }

    // Rule-based fallback
    return GenerateRuleBasedInsights(category, factors, scores);
  }

  private async Task<IReadOnlyList<EnhancedImpactFactorInsight>> GenerateAIInsightsAsync(
      string category,
      IReadOnlyList<TopImpactFactorInput> factors,
      ScoreResult scores,
      IReadOnlyList<BestPracticeViolation> allViolations,
      CancellationToken cancellationToken)
  {
    var prompt = BuildInsightPrompt(category, factors, scores, allViolations);
    var context = new InfrastructureContext
    {
      ServiceGroupCount = 1,
      ServiceGroupNames = ["impact-factor-analysis"],
      RecentRunCount = 1,
      CompletedRunCount = 1,
      PendingRunCount = 0,
      Findings = factors.Select(f => new FindingSummary(f.FactorName, category)).ToList(),
      DetailedDataJson = JsonSerializer.Serialize(new
      {
        category,
        factors = factors.Select(f => new
        {
          f.FactorName,
          f.AffectedResourceCount,
          f.Severity,
          f.RelatedResourceTypes
        }),
        scores = new { scores.Availability, scores.CostEfficiency, scores.Utilization, scores.Completeness },
        violationCount = allViolations.Count
      })
    };

    var aiResponse = await _aiChatService!.GenerateResponseAsync(prompt, context, cancellationToken);

    // Parse AI response into structured insights
    return ParseAIResponse(aiResponse.Text, factors, category)
        ?? GenerateRuleBasedInsights(category, factors, scores);
  }

  private IReadOnlyList<EnhancedImpactFactorInsight> GenerateRuleBasedInsights(
      string category,
      IReadOnlyList<TopImpactFactorInput> factors,
      ScoreResult scores)
  {
    var insights = new List<EnhancedImpactFactorInsight>();

    foreach (var factor in factors)
    {
      var template = GetTemplate(category, factor.FactorName);
      var shortDesc = template?.ShortDescription ?? $"{factor.FactorName} (affects {factor.AffectedResourceCount} resources)";
      var longDesc = template?.LongDescription ?? BuildGenericDescription(category, factor, scores);
      var steps = template?.RemediationSteps ?? GetGenericRemediationSteps(category, factor);

      insights.Add(new EnhancedImpactFactorInsight(
          FactorName: factor.FactorName,
          AffectedCount: factor.AffectedResourceCount,
          Severity: factor.Severity,
          ShortDescription: shortDesc,
          ActionableLongDescription: longDesc,
          RemediationSteps: steps.ToList().AsReadOnly(),
          ConfidenceLevel: "medium",
          GeneratedBy: "rule_engine",
          AffectedResourceTypes: factor.RelatedResourceTypes
      ));
    }

    return insights.AsReadOnly();
  }

  private InsightTemplate? GetTemplate(string category, string factorName)
  {
    if (!InsightTemplates.TryGetValue(category, out var categoryTemplates))
      return null;

    var normalized = factorName.ToLowerInvariant()
        .Replace("primary ", "")
        .Replace(" driver", "")
        .Replace(" (", "")
        .Replace(")", "");

    foreach (var key in categoryTemplates.Keys)
    {
      if (normalized.Contains(key.ToLowerInvariant()))
        return categoryTemplates[key];
    }

    return null;
  }

  private InsightTemplate? TryGetTemplate(string category, string factorName)
  {
    if (!InsightTemplates.TryGetValue(category, out var categoryTemplates))
      return null;

    var normalized = factorName.ToLowerInvariant()
        .Replace("primary ", "")
        .Replace(" driver", "")
        .Replace(" (", "")
        .Replace(")", "");

    foreach (var kvp in categoryTemplates)
    {
      if (normalized.Contains(kvp.Key.ToLowerInvariant()))
        return kvp.Value;
    }

    return null;
  }

  private string BuildGenericDescription(string category, TopImpactFactorInput factor, ScoreResult scores)
  {
    var percentile = category switch
    {
      "Reliability" => $"{Math.Round(scores.Availability * 100)}%",
      "FinOps" => $"{Math.Round(scores.CostEfficiency * 100)}%",
      "Architecture" => $"{Math.Round(scores.Completeness * 100)}%",
      "Sustainability" => $"{Math.Round(scores.Utilization * 100)}%",
      _ => "unknown"
    };

    return $"{factor.FactorName} is affecting {factor.AffectedResourceCount} resources. Current {category} score: {percentile}. " +
           $"This factor has {factor.Severity} severity and should be addressed to improve overall posture.";
  }

  private string[] GetGenericRemediationSteps(string category, TopImpactFactorInput factor)
  {
    return new[]
    {
            $"Identify the root causes of the {factor.FactorName} issue",
            $"Prioritize the {factor.AffectedResourceCount} affected resources by impact",
            "Apply best practice remediation based on your architecture",
            "Validate fixes with compliance scanning tools",
            "Monitor for regressions in the coming weeks"
        };
  }

  private string BuildInsightPrompt(
      string category,
      IReadOnlyList<TopImpactFactorInput> factors,
      ScoreResult scores,
      IReadOnlyList<BestPracticeViolation> violations)
  {
    return $@"
You are an Azure Well-Architected Expert. Generate actionable, specific insights about these impact factors affecting {category} score.

Category: {category}
Factors: {string.Join(", ", factors.Select(f => $"{f.FactorName} ({f.AffectedResourceCount} resources, {f.Severity})"))}
Current Score: {Math.Round(GetScoreForCategory(scores, category) * 100)}%
Total Violations: {violations.Count}

For each factor, provide:
1. A short (1 line) description
2. A longer explanation (2-3 sentences) of WHY this matters
3. A list of specific remediation steps (5-7 items, Azure-specific)

Format your response as JSON array with objects: [{{""factorName"": ""..."", ""shortDescription"": ""..."", ""actionableLongDescription"": ""..."", ""remediationSteps"": [""...""]}}]
";
  }

  private double GetScoreForCategory(ScoreResult scores, string category)
      => category switch
      {
        "Reliability" => scores.Availability,
        "FinOps" => scores.CostEfficiency,
        "Architecture" => scores.Completeness,
        "Sustainability" => scores.Utilization,
        "Security" => scores.Security,
        _ => scores.GetAverageScore()
      };

  private List<EnhancedImpactFactorInsight>? ParseAIResponse(
      string aiText,
      IReadOnlyList<TopImpactFactorInput> factors,
      string category)
  {
    try
    {
      if (string.IsNullOrWhiteSpace(aiText))
        return null;

      using var doc = JsonDocument.Parse(aiText);
      var insights = new List<EnhancedImpactFactorInsight>();

      if (doc.RootElement.ValueKind == JsonValueKind.Array)
      {
        foreach (var elem in doc.RootElement.EnumerateArray())
        {
          var factorName = elem.GetProperty("factorName").GetString() ?? "";
          var factor = factors.FirstOrDefault(f => f.FactorName.Equals(factorName, StringComparison.OrdinalIgnoreCase));

          if (factor == null)
            continue;

          var shortDesc = elem.GetProperty("shortDescription").GetString() ?? "";
          var longDesc = elem.GetProperty("actionableLongDescription").GetString() ?? "";
          var steps = elem.GetProperty("remediationSteps")
              .EnumerateArray()
              .Select(e => e.GetString() ?? "")
              .Where(s => !string.IsNullOrWhiteSpace(s))
              .ToList();

          insights.Add(new EnhancedImpactFactorInsight(
              FactorName: factor.FactorName,
              AffectedCount: factor.AffectedResourceCount,
              Severity: factor.Severity,
              ShortDescription: shortDesc,
              ActionableLongDescription: longDesc,
              RemediationSteps: steps.AsReadOnly(),
              ConfidenceLevel: "high",
              GeneratedBy: "ai_foundry",
              AffectedResourceTypes: factor.RelatedResourceTypes
          ));
        }

        return insights.Count > 0 ? insights : null;
      }
    }
    catch (Exception)
    {
      return null;
    }

    return null;
  }

  private class InsightTemplate
  {
    public string ShortDescription { get; }
    public string LongDescription { get; }
    public string[] RemediationSteps { get; }

    public InsightTemplate(string shortDescription, string longDescription, string[] remediationSteps)
    {
      ShortDescription = shortDescription;
      LongDescription = longDescription;
      RemediationSteps = remediationSteps;
    }
  }

}
