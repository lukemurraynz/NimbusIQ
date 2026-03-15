using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Atlas.AgentOrchestrator.Agents;

/// <summary>
/// T025: Service intelligence agent - score calculation + confidence output
/// </summary>
public class ServiceIntelligenceAgent
{
    private readonly ILogger<ServiceIntelligenceAgent> _logger;

    public ServiceIntelligenceAgent(ILogger<ServiceIntelligenceAgent> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Calculate comprehensive scores for all categories (T025)
    /// </summary>
    public ServiceGroupAssessment CalculateScores(DiscoverySnapshot snapshot)
    {
        var activity = Activity.Current;
        activity?.SetTag("assessment.resourceCount", snapshot.ResourceCount);

        // Calculate confidence based on resource count (simple heuristic)
        var confidence = new ConfidenceScore
        {
            Value = snapshot.ResourceCount > 10 ? 0.9m : 0.7m,
            Level = snapshot.ResourceCount > 10 ? "High" : "Medium",
            DegradationFactors = new(),
            CanProceed = true
        };

        // Calculate scores for each category
        var architectureScore = CalculateArchitectureScore(snapshot, confidence.Value);
        var finOpsScore = CalculateFinOpsScore(snapshot, confidence.Value);
        var reliabilityScore = CalculateReliabilityScore(snapshot, confidence.Value);
        var sustainabilityScore = CalculateSustainabilityScore(snapshot, confidence.Value);

        var assessment = new ServiceGroupAssessment
        {
            ServiceGroupId = snapshot.ServiceGroupId,
            OverallConfidence = confidence,
            Architecture = architectureScore,
            FinOps = finOpsScore,
            Reliability = reliabilityScore,
            Sustainability = sustainabilityScore,
            AssessedAt = DateTime.UtcNow
        };

        _logger.LogInformation(
            "Calculated scores for service group {ServiceGroupId}: Architecture={ArchScore}, FinOps={FinScore}, Reliability={RelScore}, Sustainability={SusScore} (Confidence={Confidence})",
            snapshot.ServiceGroupId,
            architectureScore.Score,
            finOpsScore.Score,
            reliabilityScore.Score,
            sustainabilityScore.Score,
            confidence.Value);

        return assessment;
    }

    /// <summary>
    /// Calculate Architecture score (0-100)
    /// </summary>
    private CategoryScore CalculateArchitectureScore(DiscoverySnapshot snapshot, decimal confidence)
    {
        var score = 100;
        var findings = new List<string>();

        // Parse resource inventory
        var resources = ParseResourceInventory(snapshot.ResourceInventory);

        // Check for modern architecture patterns
        var hasModernCompute = resources.Any(r =>
            r.Contains("Microsoft.App/containerApps") ||
            r.Contains("Microsoft.Web/sites/config/container"));

        if (!hasModernCompute)
        {
            score -= 20;
            findings.Add("No modern container-based compute detected");
        }

        // Check for managed services ratio
        var managedServiceTypes = new[] { "flexibleServers", "managedClusters", "containerApps", "appServices" };
        var managedServiceCount = resources.Count(r => managedServiceTypes.Any(t => r.Contains(t, StringComparison.OrdinalIgnoreCase)));
        var managedRatio = resources.Count > 0 ? (double)managedServiceCount / resources.Count : 0;

        if (managedRatio < 0.5)
        {
            score -= 15;
            findings.Add($"Low managed services ratio: {managedRatio:P0}");
        }

        // Check resource count
        if (snapshot.ResourceCount < 5)
        {
            score -= 10;
            findings.Add("Limited resources for comprehensive analysis");
        }

        return new CategoryScore
        {
            Category = "Architecture",
            Score = Math.Max(0, score),
            Confidence = confidence,
            Findings = findings
        };
    }

    /// <summary>
    /// Calculate FinOps score (0-100)
    /// </summary>
    private CategoryScore CalculateFinOpsScore(DiscoverySnapshot snapshot, decimal confidence)
    {
        var score = 100;
        var findings = new List<string>();
        var resources = ParseResourceInventory(snapshot.ResourceInventory);

        // Penalise if more than half the resources use a premium/expensive SKU without clear need
        var expensiveResources = resources
            .Count(r => r.Contains("Premium", StringComparison.OrdinalIgnoreCase));

        if (resources.Count > 0 && expensiveResources > resources.Count * 0.5)
        {
            score -= 15;
            findings.Add($"{expensiveResources} resources using premium SKUs - review for right-sizing");
        }

        // Flag missing cost-allocation tags only if resource inventory provides no tag evidence
        if (snapshot.ResourceCount > 5 &&
            (string.IsNullOrEmpty(snapshot.ResourceInventory) ||
             !snapshot.ResourceInventory.Contains("\"environment\"", StringComparison.OrdinalIgnoreCase)))
        {
            score -= 10;
            findings.Add("No cost-allocation tags detected - tag resources for accurate attribution");
        }

        return new CategoryScore
        {
            Category = "FinOps",
            Score = Math.Max(0, score),
            Confidence = confidence * 0.8m, // Reduced confidence without full cost data
            Findings = findings
        };
    }

    /// <summary>
    /// Calculate Reliability score (0-100)
    /// </summary>
    private CategoryScore CalculateReliabilityScore(DiscoverySnapshot snapshot, decimal confidence)
    {
        var score = 100;
        var findings = new List<string>();
        var resources = ParseResourceInventory(snapshot.ResourceInventory);

        // Check for monitoring/observability resources (App Insights, Log Analytics)
        var hasMonitoring = resources.Any(r =>
            r.Contains("microsoft.insights", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("microsoft.operationalinsights", StringComparison.OrdinalIgnoreCase));

        if (!hasMonitoring)
        {
            score -= 15;
            findings.Add("No observability resources detected (App Insights, Log Analytics)");
        }

        // Check for multi-zone/redundant resources as a HA proxy
        var hasRedundancy = resources.Any(r =>
            r.Contains("ZoneRedundant", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("geo-redundant", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("Standard_ZRS", StringComparison.OrdinalIgnoreCase));

        if (!hasRedundancy && snapshot.ResourceCount > 3)
        {
            score -= 10;
            findings.Add("No zone-redundant or geo-redundant resources detected");
        }

        return new CategoryScore
        {
            Category = "Reliability",
            Score = Math.Max(0, score),
            Confidence = confidence,
            Findings = findings
        };
    }

    /// <summary>
    /// Calculate Sustainability score (0-100)
    /// </summary>
    private CategoryScore CalculateSustainabilityScore(DiscoverySnapshot snapshot, decimal confidence)
    {
        var score = 100;
        var findings = new List<string>();
        var resources = ParseResourceInventory(snapshot.ResourceInventory);

        // Check for green regions (Azure regions with high renewable energy commitment)
        var greenRegions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "westeurope", "northeurope", "swedencentral", "francecentral", "norwayeast",
            "australiaeast", "australiasoutheast",
            "japaneast", "japanwest",
            "canadacentral", "canadaeast",
            "brazilsouth"
        };
        var resourcesInGreenRegions = resources.Count(r =>
            greenRegions.Any(gr => r.Contains(gr, StringComparison.OrdinalIgnoreCase)));

        var greenRatio = resources.Count > 0 ? (double)resourcesInGreenRegions / resources.Count : 0;

        if (greenRatio < 0.5)
        {
            score -= 20;
            findings.Add($"Low green region usage: {greenRatio:P0}");
        }

        // Check for legacy SKUs
        var legacySkus = resources.Count(r =>
            r.Contains("Basic", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("_V1", StringComparison.OrdinalIgnoreCase));

        if (legacySkus > 0)
        {
            score -= Math.Min(15, legacySkus * 3);
            findings.Add($"{legacySkus} resources using legacy/inefficient SKUs");
        }

        return new CategoryScore
        {
            Category = "Sustainability",
            Score = Math.Max(0, score),
            Confidence = confidence * 0.9m, // Slightly lower confidence for sustainability metrics
            Findings = findings
        };
    }

    /// <summary>
    /// Calculate confidence score based on telemetry availability
    /// </summary>
    private ConfidenceScore CalculateConfidence(TelemetryContext telemetryContext)
    {
        var activity = Activity.Current;
        var coverage = (double)telemetryContext.ConfidenceImpact;

        activity?.SetTag("telemetry.coverage", coverage);

        var factors = new List<string>();

        if (telemetryContext.MissingTelemetry.Count > 0)
        {
            factors.Add($"{telemetryContext.MissingTelemetry.Count} resources missing telemetry");
        }

        var confidence = new ConfidenceScore
        {
            Value = Math.Round((decimal)coverage, 4),
            Level = DetermineLevel((decimal)coverage),
            DegradationFactors = factors,
            CanProceed = coverage >= 0.5
        };

        _logger.LogInformation(
            "Calculated confidence score {Score} ({Level}) with coverage {Coverage:P0}",
            confidence.Value,
            confidence.Level,
            coverage);

        return confidence;
    }

    private static string DetermineLevel(decimal score)
    {
        return score switch
        {
            >= 0.9m => "high",
            >= 0.7m => "medium",
            >= 0.5m => "low",
            _ => "very_low"
        };
    }

    private List<string> ParseResourceInventory(string? resourceInventoryJson)
    {
        // Parse JSON array of resources (simplified)
        if (string.IsNullOrWhiteSpace(resourceInventoryJson))
        {
            return new List<string>();
        }

        try
        {
            // Simple heuristic: count resource type mentions
            return resourceInventoryJson
                .Split(new[] { "Microsoft.", "microsoft." }, StringSplitOptions.RemoveEmptyEntries)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private TelemetryContext ParseTelemetryHealth(string? telemetryHealthJson)
    {
        // Parse telemetry context JSON
        if (string.IsNullOrWhiteSpace(telemetryHealthJson))
        {
            return new TelemetryContext { ConfidenceImpact = 0.5m };
        }

        try
        {
            // Simple parsing - in production would use System.Text.Json
            var hasMissing = telemetryHealthJson.Contains("MissingTelemetry", StringComparison.OrdinalIgnoreCase);
            var coverage = hasMissing ? 0.7m : 1.0m;

            return new TelemetryContext
            {
                ConfidenceImpact = coverage,
                MissingTelemetry = hasMissing ? new List<string> { "partial" } : new List<string>()
            };
        }
        catch
        {
            return new TelemetryContext { ConfidenceImpact = 0.5m };
        }
    }
}

// Supporting types for T025
public class ServiceGroupAssessment
{
    public Guid ServiceGroupId { get; set; }
    public ConfidenceScore OverallConfidence { get; set; } = new();
    public CategoryScore Architecture { get; set; } = null!;
    public CategoryScore FinOps { get; set; } = null!;
    public CategoryScore Reliability { get; set; } = null!;
    public CategoryScore Sustainability { get; set; } = null!;
    public DateTime AssessedAt { get; set; }
}

public class CategoryScore
{
    public required string Category { get; init; }
    public int Score { get; init; } // 0-100
    public decimal Confidence { get; init; } // 0.0-1.0
    public List<string> Findings { get; init; } = new();
}

public class TelemetryAvailability
{
    public bool HasResourceGraph { get; set; }
    public bool HasMonitorMetrics { get; set; }
    public bool HasCostData { get; set; }
    public bool HasDependencyGraph { get; set; }
}

public class ConfidenceScore
{
    public decimal Value { get; set; }
    public string Level { get; set; } = string.Empty;
    public List<string> DegradationFactors { get; set; } = new();
    public bool CanProceed { get; set; }
}

// Re-use types from DiscoveryWorkflow
public class TelemetryContext
{
    public List<string> MetricsAvailable { get; set; } = new();
    public List<string> LogsAvailable { get; set; } = new();
    public List<string> MissingTelemetry { get; set; } = new();
    public decimal ConfidenceImpact { get; set; }
}
