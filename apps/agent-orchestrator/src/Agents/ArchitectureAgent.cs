using Atlas.AgentOrchestrator.Integrations.Azure;
using Atlas.AgentOrchestrator.Integrations.Prompts;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Atlas.AgentOrchestrator.Agents;

/// <summary>
/// Architecture Agent - Evaluates architecture maturity and technical patterns.
/// Uses Azure AI Foundry (GPT-4) to produce a structured narrative that explains
/// the trade-offs behind each architectural finding and recommends remediation priority.
/// </summary>
public class ArchitectureAgent
{
    private readonly ILogger<ArchitectureAgent> _logger;
    private readonly IAzureAIFoundryClient? _foundryClient;
    private readonly IPromptProvider? _promptProvider;

    public ArchitectureAgent(
        ILogger<ArchitectureAgent> logger,
        IAzureAIFoundryClient? foundryClient = null,
        IPromptProvider? promptProvider = null)
    {
        _logger = logger;
        _foundryClient = foundryClient;
        _promptProvider = promptProvider;
    }

    /// <summary>
    /// Analyze architecture maturity based on service knowledge graph.
    /// When <see cref="AzureAIFoundryClient"/> is configured, appends a GPT-4 generated
    /// narrative that reasons about coupling, boundary quality and resilience trade-offs.
    /// </summary>
    public async Task<AgentAnalysisResult> AnalyzeArchitectureAsync(
        ServiceGraphContext graphContext,
        CancellationToken cancellationToken = default)
    {
        var activity = Activity.Current;
        activity?.SetTag("agent.type", "architecture");
        activity?.SetTag("service.group.id", graphContext.ServiceGroupId);

        var findings = new List<Finding>();
        var recommendations = new List<Recommendation>();
        var evidence = new List<string>();

        // Analyze service boundaries
        var boundaryScore = AnalyzeServiceBoundaries(graphContext, findings, recommendations, evidence);

        // Analyze coupling and cohesion
        var couplingScore = AnalyzeCoupling(graphContext, findings, recommendations, evidence);

        // Analyze resilience patterns
        var resilienceScore = AnalyzeResiliencePatterns(graphContext, findings, recommendations, evidence);

        // Analyze scalability patterns
        var scalabilityScore = AnalyzeScalability(graphContext, findings, recommendations, evidence);

        // Calculate overall architecture maturity score (0-100)
        var overallScore = (boundaryScore + couplingScore + resilienceScore + scalabilityScore) / 4.0;

        // Calculate confidence based on graph completeness
        var confidence = CalculateConfidence(graphContext);

        _logger.LogInformation(
            "Architecture analysis complete for service group {ServiceGroupId}: Score={Score:F2}, Confidence={Confidence:F2}",
            graphContext.ServiceGroupId,
            overallScore,
            confidence);

        var result = new AgentAnalysisResult
        {
            Score = Math.Round(overallScore, 2),
            Confidence = Math.Round(confidence, 2),
            Findings = findings,
            Recommendations = recommendations,
            EvidenceReferences = evidence
        };

        // Enrich with AI-generated architecture narrative when Foundry is available
        if (_foundryClient != null)
        {
            result.AINarrativeSummary = await GenerateAINarrativeAsync(
                result, graphContext, cancellationToken);
        }

        return result;
    }

    private async Task<string?> GenerateAINarrativeAsync(
        AgentAnalysisResult result,
        ServiceGraphContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var criticalFindings = result.Findings
                .Where(f => f.Severity is "critical" or "high")
                .Select(f => $"- {f.Category}: {f.Description}")
                .Take(5)
                .ToList();

            var topRecs = result.Recommendations
                .Take(3)
                .Select(r => $"- {r.Title}")
                .ToList();

            if (_promptProvider is null)
            {
                throw new InvalidOperationException("Prompt provider is required for architecture narrative generation.");
            }

            var prompt = _promptProvider.Render(
                "architecture-narrative",
                new Dictionary<string, string>
                {
                    ["OverallScore"] = result.Score.ToString("F0"),
                    ["ServiceCount"] = context.Nodes.Count.ToString(),
                    ["DomainCount"] = context.Domains.Count.ToString(),
                    ["DependencyCount"] = context.Edges.Count.ToString(),
                    ["CriticalFindings"] = criticalFindings.Any() ? string.Join("\n", criticalFindings) : "No critical architecture findings",
                    ["TopRecommendations"] = topRecs.Any() ? string.Join("\n", topRecs) : "No specific actions required"
                });

            return await _foundryClient!.SendPromptAsync(prompt, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "AI narrative generation failed for architecture analysis of service group {ServiceGroupId}",
                context.ServiceGroupId);
            return $"AI narrative unavailable (score: {result.Score:F0}/100). Review architecture findings for details.";
        }
    }

    private double AnalyzeServiceBoundaries(
        ServiceGraphContext context,
        List<Finding> findings,
        List<Recommendation> recommendations,
        List<string> evidence)
    {
        var score = 100.0;

        // Check if services have clear domain boundaries
        if (context.Domains.Count == 0)
        {
            score -= 30;
            findings.Add(new Finding
            {
                Severity = "high",
                Category = "service_boundaries",
                Description = "No service domains identified - unclear service boundaries",
                Impact = "Difficult to understand system architecture and responsibilities"
            });

            recommendations.Add(new Recommendation
            {
                Priority = "high",
                Category = "architecture",
                Title = "Define service domain boundaries",
                Description = "Group related resources into logical service domains using tags and naming conventions",
                EstimatedEffort = "medium"
            });
        }
        else
        {
            evidence.Add($"service_domains_count:{context.Domains.Count}");

            // Check for overly large domains (>20 resources)
            var largeDomains = context.Domains.Where(d => d.ResourceCount > 20).ToList();
            if (largeDomains.Any())
            {
                score -= 15;
                findings.Add(new Finding
                {
                    Severity = "medium",
                    Category = "service_boundaries",
                    Description = $"{largeDomains.Count} service domain(s) contain >20 resources",
                    Impact = "Large domains may indicate insufficient decomposition"
                });

                recommendations.Add(new Recommendation
                {
                    Priority = "medium",
                    Category = "architecture",
                    Title = "Consider decomposing large service domains",
                    Description = "Review large domains and split into smaller, more focused services",
                    EstimatedEffort = "high"
                });
            }
        }

        return Math.Max(0, score);
    }

    private double AnalyzeCoupling(
        ServiceGraphContext context,
        List<Finding> findings,
        List<Recommendation> recommendations,
        List<string> evidence)
    {
        var score = 100.0;

        if (context.Nodes.Count == 0)
        {
            return score;
        }

        // Calculate average dependencies per node
        var avgDependencies = context.Edges.Count / (double)context.Nodes.Count;
        evidence.Add($"avg_dependencies_per_node:{avgDependencies:F2}");

        // High coupling if avg > 5
        if (avgDependencies > 5)
        {
            score -= 25;
            findings.Add(new Finding
            {
                Severity = "high",
                Category = "coupling",
                Description = $"High average coupling detected ({avgDependencies:F1} dependencies per service)",
                Impact = "Changes may cascade across multiple services"
            });

            recommendations.Add(new Recommendation
            {
                Priority = "high",
                Category = "architecture",
                Title = "Reduce service coupling",
                Description = "Introduce service facades or event-driven patterns to decouple services",
                EstimatedEffort = "high"
            });
        }
        else if (avgDependencies > 3)
        {
            score -= 10;
            findings.Add(new Finding
            {
                Severity = "medium",
                Category = "coupling",
                Description = $"Moderate coupling detected ({avgDependencies:F1} dependencies per service)",
                Impact = "Consider reducing dependencies for better modularity"
            });
        }

        // Check for circular dependencies
        var circularDeps = DetectCircularDependencies(context);
        if (circularDeps.Any())
        {
            score -= 30;
            findings.Add(new Finding
            {
                Severity = "critical",
                Category = "coupling",
                Description = $"Circular dependencies detected between {circularDeps.Count} service pairs",
                Impact = "Circular dependencies prevent independent deployment and testing"
            });

            recommendations.Add(new Recommendation
            {
                Priority = "critical",
                Category = "architecture",
                Title = "Break circular dependencies",
                Description = "Refactor to remove circular references using dependency inversion or event patterns",
                EstimatedEffort = "high"
            });
        }

        return Math.Max(0, score);
    }

    private double AnalyzeResiliencePatterns(
        ServiceGraphContext context,
        List<Finding> findings,
        List<Recommendation> recommendations,
        List<string> evidence)
    {
        var score = 100.0;

        // Check for load balancer presence
        var hasLoadBalancer = context.Nodes.Any(n =>
            n.NodeType.Contains("LoadBalancer", StringComparison.OrdinalIgnoreCase) ||
            n.NodeType.Contains("ApplicationGateway", StringComparison.OrdinalIgnoreCase) ||
            n.NodeType.Contains("FrontDoor", StringComparison.OrdinalIgnoreCase));

        if (!hasLoadBalancer)
        {
            score -= 20;
            findings.Add(new Finding
            {
                Severity = "medium",
                Category = "resilience",
                Description = "No load balancer detected",
                Impact = "Single point of failure for traffic distribution"
            });

            recommendations.Add(new Recommendation
            {
                Priority = "medium",
                Category = "reliability",
                Title = "Add load balancing layer",
                Description = "Implement Azure Load Balancer or Application Gateway for traffic distribution",
                EstimatedEffort = "medium"
            });
        }
        else
        {
            evidence.Add("has_load_balancer:true");
        }

        // Check for multi-region setup
        var regions = context.Nodes
            .Select(n => n.Metadata?.GetValueOrDefault("location"))
            .Where(l => !string.IsNullOrEmpty(l))
            .Distinct()
            .Count();

        evidence.Add($"regions_count:{regions}");

        if (regions < 2)
        {
            score -= 15;
            findings.Add(new Finding
            {
                Severity = "medium",
                Category = "resilience",
                Description = "Single-region deployment detected",
                Impact = "No geographic redundancy - vulnerable to regional outages"
            });

            recommendations.Add(new Recommendation
            {
                Priority = "low",
                Category = "reliability",
                Title = "Consider multi-region deployment",
                Description = "Deploy to multiple Azure regions for geographic redundancy",
                EstimatedEffort = "high"
            });
        }

        return Math.Max(0, score);
    }

    private double AnalyzeScalability(
        ServiceGraphContext context,
        List<Finding> findings,
        List<Recommendation> recommendations,
        List<string> evidence)
    {
        var score = 100.0;

        // Check for auto-scaling capabilities
        var hasAutoScale = context.Nodes.Any(n =>
            n.NodeType.Contains("ScaleSet", StringComparison.OrdinalIgnoreCase) ||
            n.NodeType.Contains("AppService", StringComparison.OrdinalIgnoreCase) ||
            n.NodeType.Contains("ContainerApp", StringComparison.OrdinalIgnoreCase) ||
            n.NodeType.Contains("Function", StringComparison.OrdinalIgnoreCase));

        if (!hasAutoScale)
        {
            score -= 25;
            findings.Add(new Finding
            {
                Severity = "medium",
                Category = "scalability",
                Description = "No auto-scaling resources detected",
                Impact = "Manual intervention required for load changes"
            });

            recommendations.Add(new Recommendation
            {
                Priority = "medium",
                Category = "scalability",
                Title = "Implement auto-scaling",
                Description = "Use Azure App Service, Container Apps, or VMSS with auto-scale rules",
                EstimatedEffort = "medium"
            });
        }
        else
        {
            evidence.Add("has_autoscale:true");
        }

        // Check for caching layer
        var hasCaching = context.Nodes.Any(n =>
            n.NodeType.Contains("Redis", StringComparison.OrdinalIgnoreCase) ||
            n.NodeType.Contains("Cache", StringComparison.OrdinalIgnoreCase));

        if (!hasCaching)
        {
            score -= 15;
            findings.Add(new Finding
            {
                Severity = "low",
                Category = "scalability",
                Description = "No caching layer detected",
                Impact = "Database or backend services may become bottleneck"
            });

            recommendations.Add(new Recommendation
            {
                Priority = "low",
                Category = "performance",
                Title = "Add caching layer",
                Description = "Implement Azure Cache for Redis for frequently accessed data",
                EstimatedEffort = "medium"
            });
        }
        else
        {
            evidence.Add("has_caching:true");
        }

        return Math.Max(0, score);
    }

    private double CalculateConfidence(ServiceGraphContext context)
    {
        var confidence = 1.0;

        // Reduce confidence if graph is incomplete
        if (context.Nodes.Count == 0)
        {
            confidence *= 0.3;
        }
        else if (context.Nodes.Count < 5)
        {
            confidence *= 0.7;
        }

        if (context.Edges.Count == 0)
        {
            confidence *= 0.5;
        }

        if (context.Domains.Count == 0)
        {
            confidence *= 0.8;
        }

        return confidence;
    }

    private List<string> DetectCircularDependencies(ServiceGraphContext context)
    {
        // Simplified circular dependency detection
        // In production, use proper graph traversal algorithm
        var circular = new List<string>();

        var adjacencyList = context.Edges
            .GroupBy(e => e.SourceNodeId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.TargetNodeId).ToList());

        foreach (var edge in context.Edges)
        {
            // Check if target has edge back to source
            if (adjacencyList.TryGetValue(edge.TargetNodeId, out var targets) &&
                targets.Contains(edge.SourceNodeId))
            {
                circular.Add($"{edge.SourceNodeId}<->{edge.TargetNodeId}");
            }
        }

        return circular;
    }
}

/// <summary>
/// Service graph context passed to agents for analysis
/// </summary>
public class ServiceGraphContext
{
    public Guid ServiceGroupId { get; set; }
    public List<ServiceNodeDto> Nodes { get; set; } = new();
    public List<ServiceEdgeDto> Edges { get; set; } = new();
    public List<ServiceDomainDto> Domains { get; set; } = new();
}

public class ServiceNodeDto
{
    public Guid Id { get; set; }
    public string NodeType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? AzureResourceId { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
    /// <summary>Azure region/location of the resource, e.g. "uksouth".</summary>
    public string? Region { get; set; }
}

public class ServiceEdgeDto
{
    public Guid SourceNodeId { get; set; }
    public Guid TargetNodeId { get; set; }
    public string EdgeType { get; set; } = string.Empty;
}

public class ServiceDomainDto
{
    public Guid Id { get; set; }
    public string DomainType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int ResourceCount { get; set; }
}

/// <summary>
/// Standardized agent analysis output
/// </summary>
public class AgentAnalysisResult
{
    public double Score { get; set; }
    public double Confidence { get; set; }
    public List<Finding> Findings { get; set; } = new();
    public List<Recommendation> Recommendations { get; set; } = new();
    public List<string> EvidenceReferences { get; set; } = new();
    /// <summary>AI-generated narrative summary enriched by Azure AI Foundry (null when AI is not configured)</summary>
    public string? AINarrativeSummary { get; set; }
}

public class Finding
{
    public string Severity { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Impact { get; set; } = string.Empty;
}

public class Recommendation
{
    public string Priority { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string EstimatedEffort { get; set; } = string.Empty;
    public string? Action { get; set; }
    public string? AffectedResource { get; set; }

    /// <summary>JSON: { costDelta, performanceDelta, availabilityDelta, securityDelta }</summary>
    public string? EstimatedImpactJson { get; set; }

    /// <summary>JSON: { improves: [...], degrades: [...], neutral: [...] }</summary>
    public string? TradeoffProfileJson { get; set; }

    /// <summary>JSON: { currentRisk, mitigatedRisk, residualRisk, riskCategory }</summary>
    public string? RiskProfileJson { get; set; }

    /// <summary>Human-readable "Why now?" context</summary>
    public string? TriggerReason { get; set; }

    /// <summary>JSON: what changed that triggered this recommendation</summary>
    public string? ChangeContextJson { get; set; }

    /// <summary>JSON: array of affected service node IDs or resource IDs</summary>
    public string? ImpactedServicesJson { get; set; }
}
