using Atlas.ControlPlane.Domain.Entities;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Atlas.ControlPlane.Application.ServiceGraph;

/// <summary>
/// Builds and populates the Service Knowledge Graph from discovery data
/// Phase 1.4: Populate graph from discovery outputs (resource naming, tags, telemetry, cost grouping)
/// </summary>
public class ServiceGraphBuilder
{
    private readonly ILogger<ServiceGraphBuilder> _logger;
    private readonly ComponentTypeDetector _componentTypeDetector;

    public ServiceGraphBuilder(ILogger<ServiceGraphBuilder> logger, ComponentTypeDetector componentTypeDetector)
    {
        _logger = logger;
        _componentTypeDetector = componentTypeDetector;
    }

    /// <summary>
    /// Build a service knowledge graph from discovery snapshot
    /// </summary>
    public async Task<ServiceGraphResult> BuildGraphAsync(
        Guid serviceGroupId,
        DiscoverySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Building service graph for service group {ServiceGroupId} from snapshot {SnapshotId}",
            serviceGroupId,
            snapshot.Id);

        var nodes = new List<ServiceNode>();
        var edges = new List<ServiceEdge>();
        var domains = new List<ServiceDomain>();

        // Phase 1.6: Resource-to-service mapping using naming conventions
        var serviceGroups = GroupResourcesByService(snapshot.Resources.ToList());

        foreach (var (serviceName, resources) in serviceGroups)
        {
            var serviceNode = CreateServiceNode(serviceGroupId, serviceName, resources);
            nodes.Add(serviceNode);

            // Create resource nodes for each resource
            foreach (var resource in resources)
            {
                var resourceNode = CreateResourceNode(serviceGroupId, resource);
                nodes.Add(resourceNode);

                // Create containment edge from service to resource
                edges.Add(new ServiceEdge
                {
                    Id = Guid.NewGuid(),
                    ServiceGroupId = serviceGroupId,
                    SourceNodeId = serviceNode.Id,
                    TargetNodeId = resourceNode.Id,
                    EdgeType = "contains",
                    ConfidenceScore = 0.9m,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        // Phase 1.7: Tag-based service grouping
        var tagDomains = CreateTagBasedDomains(serviceGroupId, nodes);
        domains.AddRange(tagDomains);

        // Phase 1.8: Infer relationships from telemetry and dependency data
        if (!string.IsNullOrEmpty(snapshot.DependencyGraph))
        {
            var dependencyEdges = ParseDependencyGraph(serviceGroupId, snapshot.DependencyGraph, nodes);
            edges.AddRange(dependencyEdges);
        }

        // Phase 1.9: Cost domain grouping
        var costDomains = CreateCostDomains(serviceGroupId, nodes, snapshot);
        domains.AddRange(costDomains);

        _logger.LogInformation(
            "Built service graph: {NodeCount} nodes, {EdgeCount} edges, {DomainCount} domains",
            nodes.Count,
            edges.Count,
            domains.Count);

        return await Task.FromResult(new ServiceGraphResult
        {
            Nodes = nodes,
            Edges = edges,
            Domains = domains
        });
    }

    /// <summary>
    /// Phase 1.6: Group resources into logical services using naming conventions
    /// </summary>
    private Dictionary<string, List<DiscoveredResource>> GroupResourcesByService(List<DiscoveredResource> resources)
    {
        var serviceGroups = new Dictionary<string, List<DiscoveredResource>>();

        foreach (var resource in resources)
        {
            var serviceName = InferServiceName(resource);

            if (!serviceGroups.ContainsKey(serviceName))
            {
                serviceGroups[serviceName] = new List<DiscoveredResource>();
            }

            serviceGroups[serviceName].Add(resource);
        }

        return serviceGroups;
    }

    /// <summary>
    /// Infer logical service name from resource naming patterns
    /// </summary>
    private string InferServiceName(DiscoveredResource resource)
    {
        var parts = resource.ResourceName.Split('-', '_');

        if (parts.Length >= 2)
        {
            return parts[0];
        }

        var componentType = _componentTypeDetector.Detect(resource.ResourceType, resource.ResourceName);
        return componentType is "unknown" ? resource.ResourceType : componentType;
    }

    /// <summary>
    /// Create a logical service node
    /// </summary>
    private ServiceNode CreateServiceNode(Guid serviceGroupId, string serviceName, List<DiscoveredResource> resources)
    {
        var properties = new
        {
            ResourceCount = resources.Count,
            ResourceTypes = resources.Select(r => r.ResourceType).Distinct().ToList(),
            Regions = resources.Select(r => r.Region).Distinct().ToList()
        };

        return new ServiceNode
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = serviceGroupId,
            NodeType = "logical_service",
            Name = serviceName,
            DisplayName = serviceName,
            Description = $"Logical service containing {resources.Count} resources",
            Properties = JsonSerializer.Serialize(properties),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Create a resource node from discovered resource
    /// </summary>
    private ServiceNode CreateResourceNode(Guid serviceGroupId, DiscoveredResource resource)
    {
        var componentType = _componentTypeDetector.Detect(resource.ResourceType, resource.ResourceName);

        var properties = new
        {
            Sku = resource.Sku,
            Region = resource.Region,
            TelemetryState = resource.TelemetryState,
            ComponentType = componentType
        };

        return new ServiceNode
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = serviceGroupId,
            NodeType = "resource",
            Name = resource.ResourceName,
            DisplayName = resource.ResourceName,
            Description = resource.ResourceType,
            AzureResourceId = resource.AzureResourceId,
            Properties = JsonSerializer.Serialize(properties),
            Tags = resource.Metadata,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private List<ServiceDomain> CreateTagBasedDomains(Guid serviceGroupId, List<ServiceNode> nodes)
    {
        var domains = new List<ServiceDomain>();
        var ownershipPatterns = new[] { "owner", "team", "department", "business-unit" };
        var environmentPatterns = new[] { "environment", "env", "stage" };

        foreach (var pattern in ownershipPatterns)
        {
            var ownershipDomain = CreateDomainFromTag(serviceGroupId, nodes, pattern, "ownership");
            if (ownershipDomain != null) domains.Add(ownershipDomain);
        }

        foreach (var pattern in environmentPatterns)
        {
            var envDomain = CreateDomainFromTag(serviceGroupId, nodes, pattern, "compliance_boundary");
            if (envDomain != null) domains.Add(envDomain);
        }

        return domains;
    }

    private ServiceDomain? CreateDomainFromTag(Guid serviceGroupId, List<ServiceNode> nodes, string tagKey, string domainType)
    {
        var nodesByTagValue = new Dictionary<string, List<ServiceNode>>();

        foreach (var node in nodes)
        {
            if (string.IsNullOrEmpty(node.Tags)) continue;

            try
            {
                var tags = JsonSerializer.Deserialize<Dictionary<string, string>>(node.Tags);
                if (tags != null && tags.TryGetValue(tagKey, out var tagValue))
                {
                    if (!nodesByTagValue.ContainsKey(tagValue))
                    {
                        nodesByTagValue[tagValue] = new List<ServiceNode>();
                    }
                    nodesByTagValue[tagValue].Add(node);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize tags for node {NodeId} (tag key: {TagKey})", node.Id, tagKey);
            }
        }

        if (nodesByTagValue.Count == 0) return null;

        return new ServiceDomain
        {
            Id = Guid.NewGuid(),
            ServiceGroupId = serviceGroupId,
            DomainType = domainType,
            Name = $"{tagKey}_domain",
            Description = $"Domain based on {tagKey} tag",
            Policies = JsonSerializer.Serialize(new { TagKey = tagKey, Values = nodesByTagValue.Keys }),
            CreatedAt = DateTime.UtcNow
        };
    }

    private List<ServiceEdge> ParseDependencyGraph(Guid serviceGroupId, string dependencyGraphJson, List<ServiceNode> nodes)
    {
        var edges = new List<ServiceEdge>();
        try
        {
            var dependencies = JsonSerializer.Deserialize<List<DependencyRelation>>(dependencyGraphJson);
            if (dependencies == null) return edges;

            foreach (var dep in dependencies)
            {
                var sourceNode = nodes.FirstOrDefault(n => n.AzureResourceId == dep.SourceResourceId);
                var targetNode = nodes.FirstOrDefault(n => n.AzureResourceId == dep.TargetResourceId);

                if (sourceNode != null && targetNode != null)
                {
                    edges.Add(new ServiceEdge
                    {
                        Id = Guid.NewGuid(),
                        ServiceGroupId = serviceGroupId,
                        SourceNodeId = sourceNode.Id,
                        TargetNodeId = targetNode.Id,
                        EdgeType = "depends_on",
                        Direction = "outbound",
                        ConfidenceScore = 0.8m,
                        Properties = JsonSerializer.Serialize(new { OriginalType = dep.DependencyType }),
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse dependency graph");
        }
        return edges;
    }

    private List<ServiceDomain> CreateCostDomains(Guid serviceGroupId, List<ServiceNode> nodes, DiscoverySnapshot snapshot)
    {
        var domains = new List<ServiceDomain>();
        if (snapshot.ResourceCount > 0)
        {
            domains.Add(new ServiceDomain
            {
                Id = Guid.NewGuid(),
                ServiceGroupId = serviceGroupId,
                DomainType = "cost_center",
                Name = "primary_cost_center",
                Description = "Cost tracking domain for service group",
                Policies = JsonSerializer.Serialize(new { BillingScope = "service_group", TrackingEnabled = true }),
                CreatedAt = DateTime.UtcNow
            });
        }
        return domains;
    }

    private class DependencyRelation
    {
        public string SourceResourceId { get; set; } = string.Empty;
        public string TargetResourceId { get; set; } = string.Empty;
        public string DependencyType { get; set; } = string.Empty;
    }
}

public class ServiceGraphResult
{
    public List<ServiceNode> Nodes { get; set; } = new();
    public List<ServiceEdge> Edges { get; set; } = new();
    public List<ServiceDomain> Domains { get; set; } = new();
}
