using System.Text.Json;
using Atlas.ControlPlane.Infrastructure.Azure;

namespace Atlas.ControlPlane.Application.Services;

/// <summary>
/// Builds a serialized service knowledge graph from a discovered resource set.
/// The resulting JSON is stored as a <c>serviceGraphContext</c> agent message so the
/// agent-orchestrator can deserialise it into its own <c>ServiceGraphContext</c> type.
/// </summary>
public static class ServiceGraphContextBuilder
{
    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Builds a <see cref="ServiceGraphContextDto"/> from the resources in a discovery result
    /// and returns it serialised to JSON.
    /// </summary>
    public static string Build(Guid serviceGroupId, IReadOnlyList<DiscoveredAzureResource> resources)
    {
        var nodes = BuildNodes(resources);
        var nodeIndex = nodes.ToDictionary(n => n.AzureResourceId!, n => n.Id);
        var edges = BuildEdges(resources, nodeIndex);
        var domains = BuildDomains(resources, nodes);

        var graph = new ServiceGraphContextDto
        {
            ServiceGroupId = serviceGroupId,
            Nodes = nodes,
            Edges = edges,
            Domains = domains,
        };

        return JsonSerializer.Serialize(graph, _options);
    }

    private static List<ServiceNodeDtoInternal> BuildNodes(IReadOnlyList<DiscoveredAzureResource> resources)
    {
        return resources.Select(r => new ServiceNodeDtoInternal
        {
            Id = Guid.NewGuid(),
            NodeType = NormalizeResourceType(r.ResourceType),
            Name = r.Name,
            AzureResourceId = r.ArmId,
            Metadata = BuildNodeMetadata(r),
        }).ToList();
    }

    private static List<ServiceEdgeDtoInternal> BuildEdges(
        IReadOnlyList<DiscoveredAzureResource> resources,
        Dictionary<string, Guid> nodeIndex)
    {
        var edges = new List<ServiceEdgeDtoInternal>();
        foreach (var dep in AzureDiscoveryService.InferDependencyEdges(resources))
        {
            if (nodeIndex.TryGetValue(dep.SourceId, out var srcId) &&
                nodeIndex.TryGetValue(dep.TargetId, out var tgtId))
            {
                edges.Add(new ServiceEdgeDtoInternal
                {
                    SourceNodeId = srcId,
                    TargetNodeId = tgtId,
                    EdgeType = dep.Type,
                });
            }
        }
        return edges;
    }

    private static List<ServiceDomainDtoInternal> BuildDomains(
        IReadOnlyList<DiscoveredAzureResource> resources,
        List<ServiceNodeDtoInternal> nodes)
    {
        // Group resources by resource group — each RG maps to one domain
        var nodeByArmId = nodes.ToDictionary(n => n.AzureResourceId!, n => n.Id);

        var rgGroups = resources
            .Where(r => !string.IsNullOrWhiteSpace(r.ResourceGroup))
            .GroupBy(r => r.ResourceGroup!, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return rgGroups.Select(g => new ServiceDomainDtoInternal
        {
            Id = Guid.NewGuid(),
            DomainType = "resource_group",
            Name = g.Key,
            ResourceCount = g.Count(),
        }).ToList();
    }

    private static string NormalizeResourceType(string resourceType)
    {
        // Return the leaf segment for readability: "microsoft.web/sites" → "sites"
        var slash = resourceType.LastIndexOf('/');
        return slash >= 0 ? resourceType[(slash + 1)..] : resourceType;
    }

    private static Dictionary<string, string> BuildNodeMetadata(DiscoveredAzureResource r)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(r.Location)) meta["location"] = r.Location;
        if (!string.IsNullOrWhiteSpace(r.ResourceGroup)) meta["resourceGroup"] = r.ResourceGroup;
        if (!string.IsNullOrWhiteSpace(r.SubscriptionId)) meta["subscriptionId"] = r.SubscriptionId;
        if (!string.IsNullOrWhiteSpace(r.Sku)) meta["sku"] = r.Sku;
        if (!string.IsNullOrWhiteSpace(r.Kind)) meta["kind"] = r.Kind;
        return meta;
    }
}

// Internal DTOs — mirror the agent-orchestrator ServiceGraphContext shape
// so the orchestrator can deserialise without conversion.

internal sealed class ServiceGraphContextDto
{
    public Guid ServiceGroupId { get; set; }
    public List<ServiceNodeDtoInternal> Nodes { get; set; } = new();
    public List<ServiceEdgeDtoInternal> Edges { get; set; } = new();
    public List<ServiceDomainDtoInternal> Domains { get; set; } = new();
}

internal sealed class ServiceNodeDtoInternal
{
    public Guid Id { get; set; }
    public string NodeType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? AzureResourceId { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}

internal sealed class ServiceEdgeDtoInternal
{
    public Guid SourceNodeId { get; set; }
    public Guid TargetNodeId { get; set; }
    public string EdgeType { get; set; } = string.Empty;
}

internal sealed class ServiceDomainDtoInternal
{
    public Guid Id { get; set; }
    public string DomainType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int ResourceCount { get; set; }
}
