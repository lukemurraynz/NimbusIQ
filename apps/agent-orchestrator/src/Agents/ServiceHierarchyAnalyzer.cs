using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace Atlas.AgentOrchestrator.Agents;

/// <summary>
/// T2.2: Service Hierarchy Analyzer - Analyzes parent-child service relationships
/// Cascades recommendations up/down hierarchy, aggregates scores at parent level
/// Identifies cross-cutting concerns affecting multiple levels
/// </summary>
public class ServiceHierarchyAnalyzer
{
    private readonly ILogger<ServiceHierarchyAnalyzer> _logger;

    public ServiceHierarchyAnalyzer(ILogger<ServiceHierarchyAnalyzer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Analyze hierarchical relationships and aggregate insights
    /// </summary>
    public async Task<HierarchyAnalysisResult> AnalyzeHierarchyAsync(
        ServiceGroupHierarchyContext context,
        CancellationToken cancellationToken = default)
    {
        var activity = Activity.Current;
        activity?.SetTag("hierarchy.rootGroupId", context.RootServiceGroupId);
        activity?.SetTag("hierarchy.levels", context.MaxDepth);

        try
        {
            // Build hierarchy tree
            var hierarchyTree = BuildHierarchyTree(context);

            // Aggregate scores from children to parents
            var aggregatedScores = AggregateScoresUpward(hierarchyTree);

            // Identify cross-cutting concerns
            var crossCuttingConcerns = IdentifyCrossCuttingConcerns(hierarchyTree);

            // Cascade recommendations based on hierarchy
            var cascadedRecommendations = CascadeRecommendations(hierarchyTree);

            // Calculate impact blast radius
            var impactAnalysis = CalculateImpactBlastRadius(hierarchyTree);

            var result = new HierarchyAnalysisResult
            {
                RootServiceGroupId = context.RootServiceGroupId,
                AnalyzedAt = DateTime.UtcNow,
                TotalLevels = context.MaxDepth,
                TotalServiceGroups = context.ServiceGroups.Count,
                HierarchyTree = hierarchyTree,
                AggregatedScores = aggregatedScores,
                CrossCuttingConcerns = crossCuttingConcerns,
                CascadedRecommendations = cascadedRecommendations,
                ImpactAnalysis = impactAnalysis
            };

            _logger.LogInformation(
                "Hierarchy analysis complete for root group {RootGroupId}: {TotalLevels} levels, {TotalGroups} service groups, {CrossCuttingCount} cross-cutting concerns",
                context.RootServiceGroupId,
                context.MaxDepth,
                context.ServiceGroups.Count,
                crossCuttingConcerns.Count);

            activity?.SetTag("hierarchy.crossCuttingConcerns", crossCuttingConcerns.Count);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze hierarchy for root group {RootGroupId}", context.RootServiceGroupId);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Build hierarchical tree structure
    /// </summary>
    private HierarchyNode BuildHierarchyTree(ServiceGroupHierarchyContext context)
    {
        var nodeMap = new Dictionary<Guid, HierarchyNode>();

        // Create nodes for all service groups
        foreach (var group in context.ServiceGroups)
        {
            nodeMap[group.Id] = new HierarchyNode
            {
                ServiceGroupId = group.Id,
                Name = group.Name,
                Level = group.HierarchyLevel,
                Assessment = context.Assessments.FirstOrDefault(a => a.ServiceGroupId == group.Id),
                DriftScore = context.DriftScores.FirstOrDefault(d => d.ServiceGroupId == group.Id),
                Children = new List<HierarchyNode>()
            };
        }

        // Build parent-child relationships
        HierarchyNode? root = null;
        foreach (var group in context.ServiceGroups)
        {
            var node = nodeMap[group.Id];

            if (group.ParentServiceGroupId == null)
            {
                root = node;
            }
            else if (nodeMap.ContainsKey(group.ParentServiceGroupId.Value))
            {
                var parent = nodeMap[group.ParentServiceGroupId.Value];
                parent.Children.Add(node);
                node.Parent = parent;
            }
        }

        return root ?? throw new InvalidOperationException("No root node found in hierarchy");
    }

    /// <summary>
    /// Aggregate scores from children to parent nodes
    /// Uses weighted average based on resource count
    /// </summary>
    private Dictionary<Guid, AggregatedScore> AggregateScoresUpward(HierarchyNode root)
    {
        var aggregated = new Dictionary<Guid, AggregatedScore>();

        void AggregateNode(HierarchyNode node)
        {
            // First, aggregate all children
            foreach (var child in node.Children)
            {
                AggregateNode(child);
            }

            // Then aggregate this node
            var score = new AggregatedScore
            {
                ServiceGroupId = node.ServiceGroupId,
                Level = node.Level
            };

            if (node.Assessment != null)
            {
                // Direct scores from assessment
                score.ArchitectureScore = node.Assessment.ArchitectureScore;
                score.FinOpsScore = node.Assessment.FinOpsScore;
                score.ReliabilityScore = node.Assessment.ReliabilityScore;
                score.SustainabilityScore = node.Assessment.SustainabilityScore;
                score.ResourceCount = node.Assessment.ResourceCount;
            }

            // Aggregate from children if any exist
            if (node.Children.Any())
            {
                var childScores = node.Children
                    .Select(c => aggregated[c.ServiceGroupId])
                    .Where(s => s != null)
                    .ToList();

                if (childScores.Any())
                {
                    var totalResources = childScores.Sum(s => s.ResourceCount);

                    if (totalResources > 0)
                    {
                        // Weighted average by resource count
                        score.AggregatedArchitectureScore = childScores.Sum(s => s.ArchitectureScore * s.ResourceCount) / totalResources;
                        score.AggregatedFinOpsScore = childScores.Sum(s => s.FinOpsScore * s.ResourceCount) / totalResources;
                        score.AggregatedReliabilityScore = childScores.Sum(s => s.ReliabilityScore * s.ResourceCount) / totalResources;
                        score.AggregatedSustainabilityScore = childScores.Sum(s => s.SustainabilityScore * s.ResourceCount) / totalResources;
                        score.TotalChildResourceCount = totalResources;
                    }
                    else
                    {
                        // Fallback: unweighted averages when all child resource counts are zero
                        score.AggregatedArchitectureScore = childScores.Average(s => s.ArchitectureScore);
                        score.AggregatedFinOpsScore = childScores.Average(s => s.FinOpsScore);
                        score.AggregatedReliabilityScore = childScores.Average(s => s.ReliabilityScore);
                        score.AggregatedSustainabilityScore = childScores.Average(s => s.SustainabilityScore);
                        score.TotalChildResourceCount = 0;
                    }
                }
            }

            aggregated[node.ServiceGroupId] = score;
        }

        AggregateNode(root);
        return aggregated;
    }

    /// <summary>
    /// Identify cross-cutting concerns affecting multiple levels
    /// </summary>
    private List<CrossCuttingConcern> IdentifyCrossCuttingConcerns(HierarchyNode root)
    {
        var concerns = new List<CrossCuttingConcern>();

        // Collect all violations across hierarchy using the real per-category counts from drift data
        var allViolations = new List<(Guid ServiceGroupId, int Level, string Category, int Count)>();

        void CollectViolations(HierarchyNode node)
        {
            if (node.DriftScore != null && node.DriftScore.ViolationsByCategory.Count > 0)
            {
                foreach (var (category, count) in node.DriftScore.ViolationsByCategory)
                {
                    if (count > 0)
                    {
                        allViolations.Add((node.ServiceGroupId, node.Level, category, count));
                    }
                }
            }

            foreach (var child in node.Children)
            {
                CollectViolations(child);
            }
        }

        CollectViolations(root);

        // Find patterns that span multiple levels
        var categoryGroups = allViolations
            .GroupBy(v => v.Category)
            .Where(g => g.Select(v => v.Level).Distinct().Count() > 1); // Must span multiple levels

        foreach (var group in categoryGroups)
        {
            var levels = group.Select(v => v.Level).Distinct().OrderBy(l => l).ToList();
            var affectedGroups = group.Select(v => v.ServiceGroupId).Distinct().ToList();

            concerns.Add(new CrossCuttingConcern
            {
                Category = group.Key,
                Description = $"{group.Key} issues detected across {levels.Count} hierarchy levels",
                AffectedLevels = levels,
                AffectedServiceGroups = affectedGroups,
                Severity = DetermineSeverity(group.Sum(v => v.Count), affectedGroups.Count),
                RecommendedAction = $"Implement organization-wide {group.Key.ToLower()} policy"
            });
        }

        return concerns;
    }

    /// <summary>
    /// Cascade recommendations based on hierarchy
    /// Parent recommendations may apply to children, child issues may need parent-level fixes
    /// </summary>
    private List<CascadedRecommendation> CascadeRecommendations(HierarchyNode root)
    {
        var cascaded = new List<CascadedRecommendation>();

        // Example: Security issues at child level might require parent-level network policies
        // Example: Cost optimization at parent level might cascade to all children

        void AnalyzeNode(HierarchyNode node)
        {
            // Downward cascade: Parent changes affecting children
            if (node.Assessment != null && node.Children.Any())
            {
                if (node.Assessment.ArchitectureScore < 60)
                {
                    cascaded.Add(new CascadedRecommendation
                    {
                        SourceServiceGroupId = node.ServiceGroupId,
                        TargetServiceGroupIds = node.Children.Select(c => c.ServiceGroupId).ToList(),
                        Direction = "downward",
                        Category = "Architecture",
                        Title = $"Improve architecture patterns across {node.Name} workload",
                        Description = "Low architecture score at parent level suggests systemic issues",
                        Impact = "Affects all child services"
                    });
                }
            }

            // Upward cascade: Child issues needing parent-level intervention
            if (node.Parent != null && node.DriftScore != null)
            {
                if (node.DriftScore.CriticalViolations > 0)
                {
                    cascaded.Add(new CascadedRecommendation
                    {
                        SourceServiceGroupId = node.ServiceGroupId,
                        TargetServiceGroupIds = new List<Guid> { node.Parent.ServiceGroupId },
                        Direction = "upward",
                        Category = "Security",
                        Title = $"Address critical security issues in {node.Name}",
                        Description = "Critical violations detected that may require parent-level policy",
                        Impact = "May expose entire workload to risk"
                    });
                }
            }

            foreach (var child in node.Children)
            {
                AnalyzeNode(child);
            }
        }

        AnalyzeNode(root);
        return cascaded;
    }

    /// <summary>
    /// Calculate impact blast radius for changes
    /// </summary>
    private Dictionary<Guid, ImpactRadius> CalculateImpactBlastRadius(HierarchyNode root)
    {
        var impacts = new Dictionary<Guid, ImpactRadius>();

        void CalculateNodeImpact(HierarchyNode node)
        {
            var impact = new ImpactRadius
            {
                ServiceGroupId = node.ServiceGroupId,
                Level = node.Level,
                DirectChildren = node.Children.Count,
                TotalDescendants = CountDescendants(node),
                ImpactedResourceCount = CalculateImpactedResources(node),
                BlastRadiusScore = CalculateBlastRadius(node)
            };

            impacts[node.ServiceGroupId] = impact;

            foreach (var child in node.Children)
            {
                CalculateNodeImpact(child);
            }
        }

        CalculateNodeImpact(root);
        return impacts;
    }

    private int CountDescendants(HierarchyNode node)
    {
        var count = node.Children.Count;
        foreach (var child in node.Children)
        {
            count += CountDescendants(child);
        }
        return count;
    }

    private int CalculateImpactedResources(HierarchyNode node)
    {
        var count = node.Assessment?.ResourceCount ?? 0;
        foreach (var child in node.Children)
        {
            count += CalculateImpactedResources(child);
        }
        return count;
    }

    private decimal CalculateBlastRadius(HierarchyNode node)
    {
        // Higher scores indicate broader impact
        var descendants = CountDescendants(node);
        var resources = CalculateImpactedResources(node);

        return (descendants * 10) + (resources * 0.1m);
    }

    private string DetermineSeverity(int violationCount, int affectedGroupCount)
    {
        var score = violationCount * affectedGroupCount;

        return score switch
        {
            >= 20 => "Critical",
            >= 10 => "High",
            >= 5 => "Medium",
            _ => "Low"
        };
    }
}

// DTOs
public class ServiceGroupHierarchyContext
{
    public Guid RootServiceGroupId { get; set; }
    public int MaxDepth { get; set; }
    public List<ServiceGroupInfo> ServiceGroups { get; set; } = new();
    public List<ServiceAssessment> Assessments { get; set; } = new();
    public List<DriftScoreInfo> DriftScores { get; set; } = new();
}

public class ServiceGroupInfo
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public Guid? ParentServiceGroupId { get; set; }
    public int HierarchyLevel { get; set; }
}

public class ServiceAssessment
{
    public Guid ServiceGroupId { get; set; }
    public decimal ArchitectureScore { get; set; }
    public decimal FinOpsScore { get; set; }
    public decimal ReliabilityScore { get; set; }
    public decimal SustainabilityScore { get; set; }
    public int ResourceCount { get; set; }
}

public class DriftScoreInfo
{
    public Guid ServiceGroupId { get; set; }
    public int CriticalViolations { get; set; }
    public int HighViolations { get; set; }
    public decimal DriftScore { get; set; }
    public Dictionary<string, int> ViolationsByCategory { get; set; } = new();
}

public class HierarchyAnalysisResult
{
    public Guid RootServiceGroupId { get; set; }
    public DateTime AnalyzedAt { get; set; }
    public int TotalLevels { get; set; }
    public int TotalServiceGroups { get; set; }
    public HierarchyNode HierarchyTree { get; set; } = null!;
    public Dictionary<Guid, AggregatedScore> AggregatedScores { get; set; } = new();
    public List<CrossCuttingConcern> CrossCuttingConcerns { get; set; } = new();
    public List<CascadedRecommendation> CascadedRecommendations { get; set; } = new();
    public Dictionary<Guid, ImpactRadius> ImpactAnalysis { get; set; } = new();
}

public class HierarchyNode
{
    public Guid ServiceGroupId { get; set; }
    public required string Name { get; set; }
    public int Level { get; set; }
    public HierarchyNode? Parent { get; set; }
    public List<HierarchyNode> Children { get; set; } = new();
    public ServiceAssessment? Assessment { get; set; }
    public DriftScoreInfo? DriftScore { get; set; }
}

public class AggregatedScore
{
    public Guid ServiceGroupId { get; set; }
    public int Level { get; set; }
    public decimal ArchitectureScore { get; set; }
    public decimal FinOpsScore { get; set; }
    public decimal ReliabilityScore { get; set; }
    public decimal SustainabilityScore { get; set; }
    public int ResourceCount { get; set; }
    public decimal AggregatedArchitectureScore { get; set; }
    public decimal AggregatedFinOpsScore { get; set; }
    public decimal AggregatedReliabilityScore { get; set; }
    public decimal AggregatedSustainabilityScore { get; set; }
    public int TotalChildResourceCount { get; set; }
}

public class CrossCuttingConcern
{
    public required string Category { get; set; }
    public required string Description { get; set; }
    public List<int> AffectedLevels { get; set; } = new();
    public List<Guid> AffectedServiceGroups { get; set; } = new();
    public required string Severity { get; set; }
    public required string RecommendedAction { get; set; }
}

public class CascadedRecommendation
{
    public Guid SourceServiceGroupId { get; set; }
    public List<Guid> TargetServiceGroupIds { get; set; } = new();
    public required string Direction { get; set; } // upward|downward
    public required string Category { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
    public required string Impact { get; set; }
}

public class ImpactRadius
{
    public Guid ServiceGroupId { get; set; }
    public int Level { get; set; }
    public int DirectChildren { get; set; }
    public int TotalDescendants { get; set; }
    public int ImpactedResourceCount { get; set; }
    public decimal BlastRadiusScore { get; set; }
}
