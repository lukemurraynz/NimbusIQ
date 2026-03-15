using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Azure;
using Microsoft.Extensions.Logging;

namespace Atlas.ControlPlane.Application.Services;

/// <summary>
/// Orchestrates Azure resource discovery for a service group by querying Azure Resource Graph.
/// Returns partial results when some scopes fail so the overall analysis is not blocked.
/// When the <see cref="AzureResourceGraphClient"/> is not available (null), returns an empty
/// result with <see cref="DiscoveryResult.IsPartial"/> set to true.
///
/// For Azure Service Groups (groups imported via the /discover endpoint whose ExternalKey is an
/// ARM resource ID under /providers/Microsoft.Management/serviceGroups/), membership is managed
/// via Microsoft.Relationships/ServiceGroupMember extension resources on each member scope.
/// The member scope can be a subscription, resource group, or individual resource.
/// These groups use <see cref="AzureResourceGraphClient.DiscoverServiceGroupMembersAsync"/> to
/// find their members across the full hierarchy of child service groups.
///
/// For manually configured groups, discovery falls back to the subscription/resource-group
/// <see cref="ServiceGroupScope"/> rows associated with the group.
/// </summary>
public class AzureDiscoveryService
{
    private readonly AzureResourceGraphClient? _resourceGraphClient;
    private readonly ILogger<AzureDiscoveryService> _logger;

    public AzureDiscoveryService(
        AzureResourceGraphClient? resourceGraphClient,
        ILogger<AzureDiscoveryService> logger)
    {
        _resourceGraphClient = resourceGraphClient;
        _logger = logger;
    }

    /// <summary>
    /// Discovers all Azure resources covered by the scopes of the given service group.
    /// </summary>
    /// <param name="serviceGroup">Service group with its Scopes navigation property loaded.</param>
    /// <param name="allHierarchyArmIds">
    /// All Azure Service Group ARM IDs in the hierarchy: the group itself plus every descendant.
    /// When non-empty and the service group was imported from Azure Service Groups, the
    /// <c>Microsoft.Relationship/ServiceGroupMember</c> path is used instead of subscription scopes.
    /// Pass an empty list (or null) to fall back to subscription-scope discovery.
    /// </param>
    /// <param name="correlationId">Correlation ID forwarded to the Resource Graph client.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="DiscoveryResult"/> containing the discovered resources plus a status flag
    /// indicating whether all scopes were queried successfully.
    /// </returns>
    public async Task<DiscoveryResult> DiscoverAsync(
        ServiceGroup serviceGroup,
        IReadOnlyList<string>? allHierarchyArmIds,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        if (_resourceGraphClient is null)
        {
            _logger.LogWarning(
                "AzureResourceGraphClient is not configured; returning empty discovery result [correlation={CorrelationId}]",
                correlationId);
            return new DiscoveryResult([], IsPartial: true, ErrorMessage: "Azure Resource Graph client is not configured.");
        }

        // Use Service Group member discovery when the group was imported from Azure Service Groups.
        // Azure SG membership is tracked via Microsoft.Relationship/ServiceGroupMember on resources,
        // not via subscription/resource-group scopes, so the member path is both more accurate
        // and includes resources across all child service groups in the hierarchy.
        var isAzureServiceGroup = serviceGroup.ExternalKey.Contains(
            "/providers/microsoft.management/servicegroups/", StringComparison.OrdinalIgnoreCase);

        if (isAzureServiceGroup)
        {
            var hierarchyArmIds = allHierarchyArmIds is { Count: > 0 }
                ? allHierarchyArmIds
                : [serviceGroup.ExternalKey];

            _logger.LogInformation(
                "Service group {ServiceGroupId} is an Azure Service Group; using Service Group member discovery " +
                "across {GroupCount} group(s) in the hierarchy [correlation={CorrelationId}]",
                serviceGroup.Id, hierarchyArmIds.Count, correlationId);

            try
            {
                var members = await _resourceGraphClient.DiscoverServiceGroupMembersAsync(
                    hierarchyArmIds, correlationId, cancellationToken);

                if (members.Count == 0 && serviceGroup.Scopes.Count > 0)
                {
                    _logger.LogWarning(
                        "Azure Service Group member discovery returned zero resources for {ServiceGroupId}; " +
                        "falling back to explicit ServiceGroupScope discovery across {ScopeCount} scope(s) [correlation={CorrelationId}]",
                        serviceGroup.Id,
                        serviceGroup.Scopes.Count,
                        correlationId);
                }
                else
                {
                    var advisor = await DiscoverAdvisorRecommendationsAsync(
                        members, correlationId, cancellationToken);
                    var policy = await DiscoverPolicyFindingsAsync(
                        members, correlationId, cancellationToken);
                    var defender = await DiscoverDefenderAssessmentsAsync(
                        members, correlationId, cancellationToken);

                    _logger.LogInformation(
                        "Service Group member discovery complete: {Count} resource(s) found [correlation={CorrelationId}]",
                        members.Count, correlationId);

                    return new DiscoveryResult(
                        members,
                        IsPartial: false,
                        ErrorMessage: null,
                        AdvisorRecommendations: advisor,
                        PolicyFindings: policy,
                        DefenderAssessments: defender);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (serviceGroup.Scopes.Count > 0)
                {
                    _logger.LogWarning(ex,
                        "Service Group member discovery failed for {ServiceGroupId}; " +
                        "falling back to explicit ServiceGroupScope discovery across {ScopeCount} scope(s) [correlation={CorrelationId}]",
                        serviceGroup.Id,
                        serviceGroup.Scopes.Count,
                        correlationId);
                }
                else
                {
                    _logger.LogError(ex,
                        "Service Group member discovery failed for {ServiceGroupId} [correlation={CorrelationId}]",
                        serviceGroup.Id, correlationId);
                    return new DiscoveryResult([], IsPartial: true,
                        ErrorMessage: $"Service Group member discovery failed: {ex.Message}");
                }
            }
        }

        // --- Subscription/resource-group scope path (manually configured groups) ---
        var scopes = serviceGroup.Scopes.ToList();
        if (scopes.Count == 0)
        {
            _logger.LogWarning(
                "Service group {ServiceGroupId} has no scopes defined; returning empty discovery result [correlation={CorrelationId}]",
                serviceGroup.Id, correlationId);
            return new DiscoveryResult([], IsPartial: false, ErrorMessage: null);
        }

        // Group scopes by subscription so we can make one query per subscription
        // and optionally apply resource-group filters.
        var bySubscription = scopes
            .GroupBy(s => s.SubscriptionId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allResources = new List<DiscoveredAzureResource>();
        var errors = new List<string>();

        foreach (var group in bySubscription)
        {
            var subscriptionId = group.Key;
            var rgFilters = group
                .Where(s => !string.IsNullOrWhiteSpace(s.ResourceGroup))
                .Select(s => s.ResourceGroup!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // When all scopes for a subscription have no RG filter, query all resources.
            var hasUnfilteredScope = group.Any(s => string.IsNullOrWhiteSpace(s.ResourceGroup));
            IEnumerable<string>? rgParam = (hasUnfilteredScope || rgFilters.Count == 0) ? null : rgFilters;

            _logger.LogInformation(
                "Discovering resources in subscription {SubscriptionId} (rgFilters={RgFilters}) [correlation={CorrelationId}]",
                subscriptionId, rgParam == null ? "none" : string.Join(",", rgFilters), correlationId);

            try
            {
                var resources = await _resourceGraphClient.DiscoverResourcesAsync(
                    subscriptionIds: [subscriptionId],
                    resourceGroupFilters: rgParam,
                    correlationId: correlationId,
                    cancellationToken: cancellationToken);

                allResources.AddRange(resources);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var msg = $"Discovery failed for subscription {subscriptionId}: {ex.Message}";
                _logger.LogError(ex, "{Message} [correlation={CorrelationId}]", msg, correlationId);
                errors.Add(msg);
            }
        }

        var isPartial = errors.Count > 0;
        var errorMessage = isPartial ? string.Join("; ", errors) : null;
        var advisorRecommendations = await DiscoverAdvisorRecommendationsAsync(
            allResources, correlationId, cancellationToken);
        var policyFindings = await DiscoverPolicyFindingsAsync(
            allResources, correlationId, cancellationToken);
        var defenderAssessments = await DiscoverDefenderAssessmentsAsync(
            allResources, correlationId, cancellationToken);

        _logger.LogInformation(
            "Discovery complete: {Count} resources found, partial={IsPartial} [correlation={CorrelationId}]",
            allResources.Count, isPartial, correlationId);

        return new DiscoveryResult(
            allResources,
            isPartial,
            errorMessage,
            AdvisorRecommendations: advisorRecommendations,
            PolicyFindings: policyFindings,
            DefenderAssessments: defenderAssessments);
    }

    private async Task<IReadOnlyList<DiscoveredAdvisorRecommendation>> DiscoverAdvisorRecommendationsAsync(
        IReadOnlyList<DiscoveredAzureResource> resources,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (_resourceGraphClient is null || resources.Count == 0)
            return [];

        var subscriptionIds = resources
            .Select(r => r.SubscriptionId)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (subscriptionIds.Count == 0)
            return [];

        var advisor = await _resourceGraphClient.DiscoverAdvisorRecommendationsAsync(
            subscriptionIds,
            correlationId,
            cancellationToken);

        if (advisor.Count == 0)
            return advisor;

        // Keep recommendations tied to resources in this discovery set, but retain
        // subscription-scoped advisor entries that may not have a resourceId.
        var discoveredIds = resources
            .Select(r => r.ArmId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return advisor
            .Where(a =>
                string.IsNullOrWhiteSpace(a.ResourceId) ||
                discoveredIds.Contains(a.ResourceId!.ToLowerInvariant()))
            .Take(200)
            .ToList();
    }

    private async Task<IReadOnlyList<DiscoveredPolicyFinding>> DiscoverPolicyFindingsAsync(
        IReadOnlyList<DiscoveredAzureResource> resources,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (_resourceGraphClient is null || resources.Count == 0)
            return [];

        var subscriptionIds = resources
            .Select(r => r.SubscriptionId)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (subscriptionIds.Count == 0)
            return [];

        var findings = await _resourceGraphClient.DiscoverPolicyNonComplianceAsync(
            subscriptionIds,
            correlationId,
            cancellationToken);

        if (findings.Count == 0)
            return findings;

        var discoveredIds = resources
            .Select(r => r.ArmId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return findings
            .Where(f =>
                string.IsNullOrWhiteSpace(f.ResourceId) ||
                discoveredIds.Contains(f.ResourceId!.ToLowerInvariant()))
            .Take(250)
            .ToList();
    }

    private async Task<IReadOnlyList<DiscoveredDefenderAssessment>> DiscoverDefenderAssessmentsAsync(
        IReadOnlyList<DiscoveredAzureResource> resources,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (_resourceGraphClient is null || resources.Count == 0)
            return [];

        var subscriptionIds = resources
            .Select(r => r.SubscriptionId)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (subscriptionIds.Count == 0)
            return [];

        var assessments = await _resourceGraphClient.DiscoverDefenderAssessmentsAsync(
            subscriptionIds,
            correlationId,
            cancellationToken);

        if (assessments.Count == 0)
            return assessments;

        var discoveredIds = resources
            .Select(r => r.ArmId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return assessments
            .Where(a =>
                string.IsNullOrWhiteSpace(a.ResourceId) ||
                discoveredIds.Contains(a.ResourceId!.ToLowerInvariant()))
            .Take(250)
            .ToList();
    }

    /// <summary>
    /// Infers dependency edges from discovered resource metadata using heuristics:
    /// <list type="bullet">
    ///   <item>All resources sharing the same RG and subscription → <c>depends_on</c> edges (co-location)</item>
    ///   <item>App Services and Function Apps → Storage Accounts in same RG → <c>data_flow</c> edge</item>
    ///   <item>AKS clusters → ACR registries in same subscription → <c>depends_on</c> edge</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<DependencyEdge> InferDependencyEdges(IReadOnlyList<DiscoveredAzureResource> resources)
    {
        var edges = new List<DependencyEdge>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Group by (subscription, resource group) for co-location edges
        var byRg = resources
            .Where(r => !string.IsNullOrWhiteSpace(r.ResourceGroup) && !string.IsNullOrWhiteSpace(r.SubscriptionId))
            .GroupBy(r => $"{r.SubscriptionId}/{r.ResourceGroup}", StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in byRg)
        {
            var members = group.ToList();
            // Star topology: each resource depends_on the first (avoid N² explosion for large RGs)
            var anchor = members[0];
            for (var i = 1; i < members.Count; i++)
            {
                var key = $"{members[i].ArmId}>{anchor.ArmId}";
                if (seen.Add(key))
                    edges.Add(new DependencyEdge(members[i].ArmId, anchor.ArmId, "depends_on"));
            }
        }

        // App Services / Function Apps → Storage Accounts in same RG → data_flow
        var appResources = resources.Where(r =>
            r.ResourceType.Contains("microsoft.web/sites", StringComparison.OrdinalIgnoreCase) ||
            r.ResourceType.Contains("microsoft.web/functionapps", StringComparison.OrdinalIgnoreCase)).ToList();

        var storageByRg = resources
            .Where(r => r.ResourceType.Contains("microsoft.storage/storageaccounts", StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => $"{r.SubscriptionId}/{r.ResourceGroup}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var app in appResources)
        {
            var rgKey = $"{app.SubscriptionId}/{app.ResourceGroup}";
            if (storageByRg.TryGetValue(rgKey, out var storage))
            {
                var key = $"{app.ArmId}>{storage.ArmId}:data_flow";
                if (seen.Add(key))
                    edges.Add(new DependencyEdge(app.ArmId, storage.ArmId, "data_flow"));
            }
        }

        // AKS clusters → ACR registries in same subscription → depends_on
        var aksBySubscription = resources
            .Where(r => r.ResourceType.Contains("microsoft.containerservice/managedclusters", StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => r.SubscriptionId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var acrBySubscription = resources
            .Where(r => r.ResourceType.Contains("microsoft.containerregistry/registries", StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => r.SubscriptionId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var (subscriptionId, aksClusters) in aksBySubscription)
        {
            if (acrBySubscription.TryGetValue(subscriptionId, out var acr))
            {
                foreach (var aks in aksClusters)
                {
                    var key = $"{aks.ArmId}>{acr.ArmId}";
                    if (seen.Add(key))
                        edges.Add(new DependencyEdge(aks.ArmId, acr.ArmId, "depends_on"));
                }
            }
        }

        return edges;
    }
}

/// <summary>Result of an Azure resource discovery run.</summary>
/// <param name="Resources">All resources discovered across all scopes.</param>
/// <param name="IsPartial">True when at least one scope failed.</param>
/// <param name="ErrorMessage">Aggregated error messages from failed scopes. Null when fully successful.</param>
public record DiscoveryResult(
    IReadOnlyList<DiscoveredAzureResource> Resources,
    bool IsPartial,
    string? ErrorMessage,
    IReadOnlyList<DiscoveredAdvisorRecommendation>? AdvisorRecommendations = null,
    IReadOnlyList<DiscoveredPolicyFinding>? PolicyFindings = null,
    IReadOnlyList<DiscoveredDefenderAssessment>? DefenderAssessments = null);

/// <summary>A directed dependency edge inferred between two discovered resources.</summary>
/// <param name="SourceId">ARM ID of the upstream resource.</param>
/// <param name="TargetId">ARM ID of the downstream resource.</param>
/// <param name="Type">Edge semantics: <c>depends_on</c> or <c>data_flow</c>.</param>
public record DependencyEdge(string SourceId, string TargetId, string Type);
