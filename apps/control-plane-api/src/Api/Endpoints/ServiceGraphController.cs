using Atlas.ControlPlane.Api.Middleware;
using Atlas.ControlPlane.Application.ServiceGraph;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Atlas.ControlPlane.Api.Endpoints;

[Authorize(Policy = "AnalysisRead")]
[ApiController]
[Route("api/v1/service-groups")]
public class ServiceGraphController : ControllerBase
{
    private readonly AtlasDbContext _dbContext;
    private readonly ServiceGraphBuilder _graphBuilder;
    private readonly ILogger<ServiceGraphController> _logger;

    public ServiceGraphController(
        AtlasDbContext dbContext,
        ServiceGraphBuilder graphBuilder,
        ILogger<ServiceGraphController> logger)
    {
        _dbContext = dbContext;
        _graphBuilder = graphBuilder;
        _logger = logger;
    }

    /// <summary>
    /// Get the service knowledge graph for a service group
    /// </summary>
    [HttpGet("{serviceGroupId:guid}/graph")]
    public async Task<IActionResult> GetServiceGraph(
        Guid serviceGroupId,
        [FromQuery] Guid? snapshotId = null,
        CancellationToken cancellationToken = default)
    {
        var serviceGroup = await _dbContext.ServiceGroups
            .FirstOrDefaultAsync(sg => sg.Id == serviceGroupId, cancellationToken);

        if (serviceGroup == null)
        {
            return this.ProblemNotFound("ServiceGroupNotFound", $"Service group {serviceGroupId} not found");
        }

        // Get nodes and edges for the service group
        var nodesQuery = _dbContext.ServiceNodes
            .Where(n => n.ServiceGroupId == serviceGroupId)
            .AsNoTracking();

        var edgesQuery = _dbContext.ServiceEdges
            .Where(e => e.ServiceGroupId == serviceGroupId)
            .AsNoTracking();

        var domainsQuery = _dbContext.ServiceDomains
            .Where(d => d.ServiceGroupId == serviceGroupId)
            .Include(d => d.Memberships)
            .AsNoTracking();

        // If snapshot ID is provided, filter by snapshot
        if (snapshotId.HasValue)
        {
            var snapshot = await _dbContext.ServiceGraphSnapshots
                .FirstOrDefaultAsync(s => s.Id == snapshotId.Value, cancellationToken);

            if (snapshot == null)
            {
                return this.ProblemNotFound("SnapshotNotFound", $"Graph snapshot {snapshotId.Value} not found");
            }
        }

        var nodes = await nodesQuery.ToListAsync(cancellationToken);
        var edges = await edgesQuery.ToListAsync(cancellationToken);
        var domains = await domainsQuery.ToListAsync(cancellationToken);

        // Map entities to DTOs expected by the frontend client
        var nodeDtos = nodes
            .Select(n => new
            {
                id = n.Id,
                name = n.Name,
                type = n.NodeType,
                metadata = n.Properties
            })
            .ToList();

        var edgeDtos = edges
            .Select(e => new
            {
                sourceId = e.SourceNodeId,
                targetId = e.TargetNodeId,
                type = e.EdgeType
            })
            .ToList();

        var domainDtos = domains
            .Select(d => new
            {
                id = d.Id,
                name = d.Name,
                type = d.DomainType,
                nodeIds = d.Memberships.Select(m => m.NodeId).ToList()
            })
            .ToList();

        var result = new
        {
            serviceGroupId,
            snapshotId,
            nodeCount = nodeDtos.Count,
            edgeCount = edgeDtos.Count,
            domainCount = domainDtos.Count,
            nodes = nodeDtos,
            edges = edgeDtos,
            domains = domainDtos
        };

        return Ok(result);
    }

    /// <summary>
    /// Get topology view of service graph (simplified for visualization)
    /// </summary>
    [HttpGet("{serviceGroupId:guid}/topology")]
    public async Task<IActionResult> GetTopology(
        Guid serviceGroupId,
        CancellationToken cancellationToken = default)
    {
        var serviceGroup = await _dbContext.ServiceGroups
            .FirstOrDefaultAsync(sg => sg.Id == serviceGroupId, cancellationToken);

        if (serviceGroup == null)
        {
            return this.ProblemNotFound("ServiceGroupNotFound", $"Service group {serviceGroupId} not found");
        }

        var nodes = await _dbContext.ServiceNodes
            .Where(n => n.ServiceGroupId == serviceGroupId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var edges = await _dbContext.ServiceEdges
            .Where(e => e.ServiceGroupId == serviceGroupId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // Build simplified topology for visualization
        var topology = new
        {
            Nodes = nodes.Select(n => new
            {
                n.Id,
                n.NodeType,
                n.Name,
                n.DisplayName,
                n.Description,
                n.AzureResourceId,
                Properties = n.Properties
            }).ToList(),
            Edges = edges.Select(e => new
            {
                e.Id,
                Source = e.SourceNodeId,
                Target = e.TargetNodeId,
                Type = e.EdgeType,
                e.Direction,
                Confidence = e.ConfidenceScore
            }).ToList()
        };

        return Ok(topology);
    }

    /// <summary>
    /// Build or rebuild the service graph from latest discovery snapshot
    /// </summary>
    [HttpPost("{serviceGroupId:guid}/graph/build")]
    [Authorize(Policy = "AnalysisWrite")]
    public async Task<IActionResult> BuildGraph(
        Guid serviceGroupId,
        CancellationToken cancellationToken = default)
    {
        var serviceGroup = await _dbContext.ServiceGroups
            .FirstOrDefaultAsync(sg => sg.Id == serviceGroupId, cancellationToken);

        if (serviceGroup == null)
        {
            return this.ProblemNotFound("ServiceGroupNotFound", $"Service group {serviceGroupId} not found");
        }

        // Get the latest snapshot
        var snapshot = await _dbContext.DiscoverySnapshots
            .Where(s => s.ServiceGroupId == serviceGroupId)
            .OrderByDescending(s => s.SnapshotTime)
            .Include(s => s.Resources)
            .FirstOrDefaultAsync(cancellationToken);

        if (snapshot == null)
        {
            return this.ProblemBadRequest("NoSnapshotAvailable", "No discovery snapshot available for this service group");
        }

        // Build the graph
        var graphResult = await _graphBuilder.BuildGraphAsync(serviceGroupId, snapshot, cancellationToken);

        // Save to database
        await _dbContext.ServiceNodes.AddRangeAsync(graphResult.Nodes, cancellationToken);
        await _dbContext.ServiceEdges.AddRangeAsync(graphResult.Edges, cancellationToken);
        await _dbContext.ServiceDomains.AddRangeAsync(graphResult.Domains, cancellationToken);

        // Create snapshot record
        var graphSnapshot = new Domain.Entities.ServiceGraphSnapshot
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = serviceGroupId,
            Version = "1.0",
            SnapshotTime = DateTime.UtcNow,
            NodeCount = graphResult.Nodes.Count,
            EdgeCount = graphResult.Edges.Count,
            DomainCount = graphResult.Domains.Count,
            CreatedAt = DateTime.UtcNow
        };

        await _dbContext.ServiceGraphSnapshots.AddAsync(graphSnapshot, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Built service graph for service group {ServiceGroupId}: {NodeCount} nodes, {EdgeCount} edges, {DomainCount} domains",
            serviceGroupId,
            graphResult.Nodes.Count,
            graphResult.Edges.Count,
            graphResult.Domains.Count);

        return Ok(new
        {
            message = "Service graph built successfully.",
            nodesCreated = graphResult.Nodes.Count,
            edgesCreated = graphResult.Edges.Count
        });
    }

    /// <summary>
    /// Calculate blast radius for a set of resources or a recommendation.
    /// Phase 3.3: Service graph and blast radius analysis
    /// Returns dependency-driven impact: affected applications, identities/secrets/networks, recommendations with shared blast radius.
    /// </summary>
    [HttpGet("{serviceGroupId:guid}/blast-radius")]
    public async Task<IActionResult> GetBlastRadius(
        Guid serviceGroupId,
        [FromQuery] string? resourceIds = null,
        [FromQuery] Guid? recommendationId = null,
        [FromQuery(Name = "api-version")] string? apiVersion = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return this.ProblemBadRequest("MissingApiVersionParameter", "api-version query parameter is required");

        var serviceGroup = await _dbContext.ServiceGroups
            .FirstOrDefaultAsync(sg => sg.Id == serviceGroupId, cancellationToken);

        if (serviceGroup == null)
            return this.ProblemNotFound("ServiceGroupNotFound", $"Service group {serviceGroupId} not found");

        // Parse resource IDs from comma-separated query parameter
        var targetResourceIds = new List<string>();
        if (!string.IsNullOrWhiteSpace(resourceIds))
        {
            targetResourceIds = resourceIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        // If recommendation ID provided, get affected resources from recommendation
        if (recommendationId.HasValue)
        {
            var recommendation = await _dbContext.Recommendations
                .FirstOrDefaultAsync(r => r.Id == recommendationId.Value, cancellationToken);

            if (recommendation != null && !string.IsNullOrWhiteSpace(recommendation.ImpactedServices))
            {
                try
                {
                    var impactedServices = System.Text.Json.JsonSerializer.Deserialize<List<string>>(recommendation.ImpactedServices);
                    if (impactedServices != null)
                        targetResourceIds.AddRange(impactedServices);
                }
                catch
                {
                    // Continue with empty list if parsing fails
                }
            }
        }

        if (targetResourceIds.Count == 0)
            return this.ProblemBadRequest("NoResourcesSpecified", "Either resourceIds or recommendationId must be provided");

        // Get all nodes and edges for the service group
        var nodes = await _dbContext.ServiceNodes
            .Where(n => n.ServiceGroupId == serviceGroupId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var edges = await _dbContext.ServiceEdges
            .Where(e => e.ServiceGroupId == serviceGroupId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // Find target nodes matching resource IDs (partial match on Azure Resource ID)
        var targetNodes = nodes
            .Where(n => targetResourceIds.Any(rid => n.AzureResourceId != null && n.AzureResourceId.Contains(rid, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (targetNodes.Count == 0)
            return Ok(new BlastRadiusResponse { ResourceCount = 0, AffectedResources = [], AffectedIdentities = [], SharedRecommendations = [] });

        // Calculate blast radius using graph traversal
        var affectedNodeIds = new HashSet<Guid>();
        var affectedApplications = new List<AffectedResourceDto>();
        var affectedIdentities = new List<AffectedIdentityDto>();

        // BFS traversal from target nodes to find dependent resources
        var queue = new Queue<Guid>();
        var visited = new HashSet<Guid>();

        foreach (var targetNode in targetNodes)
        {
            queue.Enqueue(targetNode.Id);
            visited.Add(targetNode.Id);
        }

        while (queue.Count > 0)
        {
            var currentNodeId = queue.Dequeue();
            affectedNodeIds.Add(currentNodeId);

            // Find outbound edges (resources that depend on current node)
            var dependentEdges = edges.Where(e => e.TargetNodeId == currentNodeId && e.Direction == "outbound").ToList();

            foreach (var edge in dependentEdges)
            {
                if (!visited.Contains(edge.SourceNodeId))
                {
                    visited.Add(edge.SourceNodeId);
                    queue.Enqueue(edge.SourceNodeId);
                }
            }
        }

        // Categorize affected resources
        foreach (var nodeId in affectedNodeIds)
        {
            var node = nodes.FirstOrDefault(n => n.Id == nodeId);
            if (node == null) continue;

            if (node.NodeType.Contains("Identity", StringComparison.OrdinalIgnoreCase) ||
                node.NodeType.Contains("ServicePrincipal", StringComparison.OrdinalIgnoreCase) ||
                node.NodeType.Contains("ManagedIdentity", StringComparison.OrdinalIgnoreCase))
            {
                affectedIdentities.Add(new AffectedIdentityDto
                {
                    ResourceId = node.AzureResourceId ?? "",
                    Name = node.DisplayName ?? node.Name,
                    Type = node.NodeType,
                    ImpactType = "IdentityAccess"
                });
            }
            else
            {
                affectedApplications.Add(new AffectedResourceDto
                {
                    ResourceId = node.AzureResourceId ?? "",
                    Name = node.DisplayName ?? node.Name,
                    Type = node.NodeType,
                    ImpactType = DetermineImpactType(node.NodeType)
                });
            }
        }

        // Find recommendations with shared blast radius
        var allRecommendations = await _dbContext.Recommendations
            .Where(r => r.ServiceGroupId == serviceGroupId && r.Status != "Implemented")
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var sharedRecommendations = new List<SharedRecommendationDto>();
        foreach (var rec in allRecommendations)
        {
            if (string.IsNullOrWhiteSpace(rec.ImpactedServices)) continue;

            try
            {
                var recImpactedServices = System.Text.Json.JsonSerializer.Deserialize<List<string>>(rec.ImpactedServices);
                if (recImpactedServices != null)
                {
                    var overlap = recImpactedServices.Intersect(affectedApplications.Select(a => a.ResourceId), StringComparer.OrdinalIgnoreCase).Count();
                    if (overlap > 0)
                    {
                        sharedRecommendations.Add(new SharedRecommendationDto
                        {
                            RecommendationId = rec.Id,
                            Title = rec.Title,
                            Category = rec.Category,
                            Priority = rec.Priority,
                            OverlapCount = overlap
                        });
                    }
                }
            }
            catch
            {
                // Skip if parsing fails
            }
        }

        return Ok(new BlastRadiusResponse
        {
            ResourceCount = affectedApplications.Count,
            IdentityCount = affectedIdentities.Count,
            AffectedResources = affectedApplications,
            AffectedIdentities = affectedIdentities,
            SharedRecommendations = sharedRecommendations.OrderByDescending(r => r.OverlapCount).Take(10).ToList()
        });
    }

    private static string DetermineImpactType(string nodeType)
    {
        if (nodeType.Contains("App", StringComparison.OrdinalIgnoreCase) ||
            nodeType.Contains("Function", StringComparison.OrdinalIgnoreCase) ||
            nodeType.Contains("Container", StringComparison.OrdinalIgnoreCase))
            return "Application";

        if (nodeType.Contains("Database", StringComparison.OrdinalIgnoreCase) ||
            nodeType.Contains("Storage", StringComparison.OrdinalIgnoreCase) ||
            nodeType.Contains("Cosmos", StringComparison.OrdinalIgnoreCase))
            return "DataStore";

        if (nodeType.Contains("Network", StringComparison.OrdinalIgnoreCase) ||
            nodeType.Contains("Vnet", StringComparison.OrdinalIgnoreCase) ||
            nodeType.Contains("Gateway", StringComparison.OrdinalIgnoreCase))
            return "Network";

        if (nodeType.Contains("KeyVault", StringComparison.OrdinalIgnoreCase) ||
            nodeType.Contains("Secret", StringComparison.OrdinalIgnoreCase))
            return "Secret";

        return "Infrastructure";
    }
}

// Phase 3.3: Blast Radius DTOs
public class BlastRadiusResponse
{
    public int ResourceCount { get; set; }
    public int IdentityCount { get; set; }
    public List<AffectedResourceDto> AffectedResources { get; set; } = [];
    public List<AffectedIdentityDto> AffectedIdentities { get; set; } = [];
    public List<SharedRecommendationDto> SharedRecommendations { get; set; } = [];
}

public class AffectedResourceDto
{
    public string ResourceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string ImpactType { get; set; } = "";
}

public class AffectedIdentityDto
{
    public string ResourceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string ImpactType { get; set; } = "";
}

public class SharedRecommendationDto
{
    public Guid RecommendationId { get; set; }
    public string Title { get; set; } = "";
    public string Category { get; set; } = "";
    public string Priority { get; set; } = "";
    public int OverlapCount { get; set; }
}
