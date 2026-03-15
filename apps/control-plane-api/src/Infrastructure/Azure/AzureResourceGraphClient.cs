using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.ResourceGraph;
using Azure.ResourceManager.ResourceGraph.Models;
using Atlas.ControlPlane.Infrastructure.Auth;
using Microsoft.Extensions.Logging;

namespace Atlas.ControlPlane.Infrastructure.Azure;

/// <summary>
/// Represents an Azure Service Group (microsoft.management/servicegroups) discovered from
/// Azure Resource Graph. Service Groups are a tenant-level governance construct for flexible
/// cross-boundary grouping of resources.
/// See: https://learn.microsoft.com/azure/governance/service-groups/overview
/// </summary>
/// <param name="ArmId">
/// Full ARM resource ID, e.g. /providers/Microsoft.Management/serviceGroups/{name}
/// </param>
/// <param name="Name">The resource name (short unique identifier within the tenant).</param>
/// <param name="DisplayName">
/// Human-readable display name from properties.displayName. May be null if unset.
/// </param>
/// <param name="ParentArmId">
/// Full ARM ID of the parent service group from properties.parent.resourceId.
/// Null for top-level groups (whose parent is the root/tenant service group).
/// </param>
public record DiscoveredAzureServiceGroup(
    string ArmId,
    string Name,
    string? DisplayName,
    string? ParentArmId);

/// <summary>
/// Represents an Azure resource discovered via Resource Graph.
/// </summary>
/// <param name="ArmId">Full ARM resource ID.</param>
/// <param name="Name">Resource name.</param>
/// <param name="ResourceType">ARM resource type, e.g. microsoft.compute/virtualmachines.</param>
/// <param name="Location">Azure region, e.g. australiaeast.</param>
/// <param name="ResourceGroup">Resource group name. Null for tenant-scoped resources.</param>
/// <param name="SubscriptionId">Subscription GUID. Null for tenant-scoped resources.</param>
/// <param name="Sku">JSON string of the SKU object if present.</param>
/// <param name="Tags">JSON string of tags object if present.</param>
/// <param name="Kind">Resource kind if present.</param>
public record DiscoveredAzureResource(
    string ArmId,
    string Name,
    string ResourceType,
    string Location,
    string? ResourceGroup,
    string? SubscriptionId,
    string? Sku,
    string? Tags,
    string? Kind,
    string? Properties);

/// <summary>
/// Represents an Azure Advisor recommendation discovered via Resource Graph.
/// </summary>
public record DiscoveredAdvisorRecommendation(
    string RecommendationId,
    string Name,
    string? SubscriptionId,
    string? Category,
    string? Impact,
    string? Risk,
    string? Description,
    string? Remediation,
    string? ResourceId,
    string? RecommendationTypeId,
    string? LearnMoreLink);

/// <summary>
/// Represents a non-compliant Azure Policy state record discovered via Resource Graph.
/// </summary>
public record DiscoveredPolicyFinding(
    string FindingId,
    string? SubscriptionId,
    string? ResourceId,
    string? PolicyAssignmentId,
    string? PolicyAssignmentName,
    string? PolicyDefinitionId,
    string? PolicyDefinitionName,
    string? ComplianceState,
    string? Description);

/// <summary>
/// Represents a Defender for Cloud assessment discovered via Resource Graph.
/// </summary>
public record DiscoveredDefenderAssessment(
    string AssessmentId,
    string Name,
    string? SubscriptionId,
    string? ResourceId,
    string? StatusCode,
    string? Severity,
    string? Description,
    string? Remediation,
    string? LearnMoreLink);

/// <summary>
/// Client for querying Azure Resource Graph using KQL.
/// Uses the user-assigned managed identity (<c>atlas-acr-pull-prod</c>) which holds:
///   - Reader at subscription scope (for resource discovery via Resource Graph)
///   - Service Group Reader at root service group scope (for service group discovery)
/// </summary>
public class AzureResourceGraphClient
{
    private readonly ArmClient _armClient;
    private readonly TokenCredential _credential;
    private readonly ILogger<AzureResourceGraphClient> _logger;
    private static readonly ActivitySource ActivitySource = new("Atlas.ControlPlane.Azure.ResourceGraph");

    public AzureResourceGraphClient(
        ManagedIdentityCredentialProvider credentialProvider,
        ILogger<AzureResourceGraphClient> logger)
    {
        _logger = logger;
        // Use the user-assigned MI (AZURE_CLIENT_ID). These container apps have no
        // system-assigned identity; Reader + Service Group Reader are on the user-assigned MI.
        _credential = credentialProvider.GetCredential();
        _armClient = new ArmClient(_credential);
    }

    /// <summary>
    /// Executes a KQL query against Azure Resource Graph.
    /// </summary>
    /// <param name="query">KQL query string</param>
    /// <param name="subscriptions">Optional list of subscription IDs to query. If null, queries all accessible subscriptions.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Query results as dynamic objects</returns>
    public async Task<object> QueryAsync(
        string query,
        IEnumerable<string>? subscriptions = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("ResourceGraph.Query");
        activity?.SetTag("query.type", "resource-graph");
        activity?.SetTag("query.length", query.Length);

        try
        {
            var queryRequest = new ResourceQueryContent(query);

            if (subscriptions != null)
            {
                foreach (var sub in subscriptions)
                {
                    queryRequest.Subscriptions.Add(sub);
                }
            }

            // Use Azure SDK's native resource graph client
            var tenant = _armClient.GetTenants().FirstOrDefault();
            if (tenant == null)
            {
                throw new InvalidOperationException("No accessible tenants found for managed identity");
            }

            var result = await tenant.GetResourcesAsync(queryRequest, cancellationToken);

            activity?.SetTag("result.count", result.Value.TotalRecords);
            activity?.SetTag("result.has_more", !string.IsNullOrEmpty(result.Value.SkipToken));

            _logger.LogInformation(
                "Resource Graph query executed successfully. Records: {TotalRecords}, HasMore: {HasSkipToken}",
                result.Value.TotalRecords,
                !string.IsNullOrEmpty(result.Value.SkipToken));

            return result.Value;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Resource Graph query failed");
            throw;
        }
    }

    /// <summary>
    /// Executes a query with pagination support.
    /// </summary>
    public async IAsyncEnumerable<object> QueryWithPaginationAsync(
        string query,
        IEnumerable<string>? subscriptions = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? skipToken = null;
        var queryRequest = new ResourceQueryContent(query);

        if (subscriptions != null)
        {
            foreach (var sub in subscriptions)
            {
                queryRequest.Subscriptions.Add(sub);
            }
        }

        var tenant = _armClient.GetTenants().FirstOrDefault();
        if (tenant == null)
        {
            throw new InvalidOperationException("No accessible tenants found for managed identity");
        }

        do
        {
            if (!string.IsNullOrEmpty(skipToken))
            {
                queryRequest.Options = new ResourceQueryRequestOptions { SkipToken = skipToken };
            }

            var result = await tenant.GetResourcesAsync(queryRequest, cancellationToken);
            yield return result.Value;

            skipToken = result.Value.SkipToken;
        }
        while (!string.IsNullOrEmpty(skipToken) && !cancellationToken.IsCancellationRequested);
    }

    /// <summary>
    /// Returns the IDs of all Azure subscriptions accessible to the managed identity.
    /// Used as a fallback scope when a service group has no explicit subscription scopes defined
    /// (e.g. groups imported from Azure Service Groups, which use resource membership rather than
    /// subscription/resource-group scopes).
    /// </summary>
    public virtual async Task<IReadOnlyList<string>> GetAccessibleSubscriptionIdsAsync(
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("ResourceGraph.GetAccessibleSubscriptions");
        var subscriptionIds = new List<string>();

        try
        {
            await foreach (var sub in _armClient.GetSubscriptions().GetAllAsync(cancellationToken))
            {
                if (!string.IsNullOrEmpty(sub.Data.SubscriptionId))
                    subscriptionIds.Add(sub.Data.SubscriptionId);
            }

            activity?.SetTag("result.count", subscriptionIds.Count);
            _logger.LogInformation("Found {Count} accessible subscription(s) for managed identity", subscriptionIds.Count);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Failed to enumerate accessible subscriptions");
            throw;
        }

        return subscriptionIds;
    }

    /// <summary>
    /// Fallback discovery: enumerates accessible Azure subscriptions and returns them as synthetic
    /// <see cref="DiscoveredAzureServiceGroup"/> records. Used when Azure Service Groups
    /// (microsoft.management/servicegroups) are not available in the tenant.
    /// Each subscription becomes a top-level service group.
    /// </summary>
    public virtual async Task<IReadOnlyList<DiscoveredAzureServiceGroup>> DiscoverSubscriptionsAsServiceGroupsAsync(
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("ResourceGraph.DiscoverSubscriptionsAsServiceGroups");

        var results = new List<DiscoveredAzureServiceGroup>();

        try
        {
            await foreach (var sub in _armClient.GetSubscriptions().GetAllAsync(cancellationToken))
            {
                if (string.IsNullOrEmpty(sub.Data.SubscriptionId))
                    continue;

                results.Add(new DiscoveredAzureServiceGroup(
                    ArmId: $"/subscriptions/{sub.Data.SubscriptionId}",
                    Name: sub.Data.SubscriptionId,
                    DisplayName: sub.Data.DisplayName,
                    ParentArmId: null));
            }

            activity?.SetTag("result.count", results.Count);
            _logger.LogInformation(
                "Fallback discovery: found {Count} accessible subscription(s) to use as service groups",
                results.Count);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Fallback subscription discovery failed");
            throw;
        }

        return results;
    }

    /// <summary>
    /// Discovers all Azure resources within a set of subscription/resource-group scopes.
    /// Returns each resource's ARM ID, type, name, location, SKU, and a subset of tags.
    /// Handles pagination automatically and tolerates individual scope failures gracefully,
    /// returning whatever partial results were collected.
    /// </summary>
    /// <param name="subscriptionIds">Subscription IDs to query. Must not be empty.</param>
    /// <param name="resourceGroupFilters">
    /// Optional resource-group names. When provided, only resources inside those groups are returned.
    /// </param>
    /// <param name="correlationId">Correlation ID for tracing.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of discovered Azure resources.</returns>
    public virtual async Task<IReadOnlyList<DiscoveredAzureResource>> DiscoverResourcesAsync(
        IEnumerable<string> subscriptionIds,
        IEnumerable<string>? resourceGroupFilters = null,
        Guid? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("ResourceGraph.DiscoverResources");
        activity?.SetTag("correlation.id", correlationId?.ToString());

        var results = new List<DiscoveredAzureResource>();
        var subList = subscriptionIds.ToList();

        if (subList.Count == 0)
        {
            _logger.LogWarning("DiscoverResourcesAsync called with no subscription IDs; returning empty list");
            return results;
        }

        var rgFilter = resourceGroupFilters?.ToList();
        string rgCondition;
        if (rgFilter is { Count: > 0 })
        {
            var sanitizedResourceGroups = rgFilter
                .Where(rg => !string.IsNullOrWhiteSpace(rg))
                .Select(rg => rg.Replace("'", "''"))
                .ToList();

            if (sanitizedResourceGroups.Count > 0)
            {
                var inList = string.Join(", ", sanitizedResourceGroups.Select(rg => $"'{rg}'"));
                rgCondition = $"| where resourceGroup in~ ({inList})";
            }
            else
            {
                rgCondition = string.Empty;
            }
        }
        else
        {
            rgCondition = string.Empty;
        }

        // Query all resources in the given subscriptions, with optional RG filter.
        var kql = $"""
            Resources
            | where type !startswith 'microsoft.advisor/'
              and type !startswith 'microsoft.security/policies'
            {rgCondition}
            | project id, name, type, location, resourceGroup, subscriptionId,
                      sku, tags, kind, properties
            | order by type asc
            """;

        try
        {
            var tenant = _armClient.GetTenants().FirstOrDefault();
            if (tenant == null)
            {
                _logger.LogWarning("No accessible tenant found; cannot discover resources");
                return results;
            }

            string? skipToken = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                var queryRequest = new ResourceQueryContent(kql);
                foreach (var sub in subList)
                    queryRequest.Subscriptions.Add(sub);

                if (!string.IsNullOrEmpty(skipToken))
                    queryRequest.Options = new ResourceQueryRequestOptions { SkipToken = skipToken };

                var response = await tenant.GetResourcesAsync(queryRequest, cancellationToken);
                var data = response.Value;

                ParseResourceRows(data.Data, results);
                skipToken = data.SkipToken;
            }
            while (!string.IsNullOrEmpty(skipToken));

            activity?.SetTag("result.count", results.Count);
            _logger.LogInformation(
                "Discovered {Count} Azure resources across {SubCount} subscription(s) [correlation={CorrelationId}]",
                results.Count, subList.Count, correlationId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex,
                "Azure resource discovery failed for subscriptions [{Subs}] [correlation={CorrelationId}]",
                string.Join(", ", subList), correlationId);
            // Graceful degradation: return whatever was collected before the failure.
        }

        return results;
    }

    private static void ParseResourceRows(BinaryData data, List<DiscoveredAzureResource> results)
    {
        using var doc = JsonDocument.Parse(data.ToStream());
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            // ObjectArray format: array of JSON objects with column names as keys.
            foreach (var obj in root.EnumerateArray())
            {
                string? Prop(string key) => obj.TryGetProperty(key, out var el)
                    ? GetJsonStringFromElement(el)
                    : null;

                var armId = Prop("id") ?? "";
                var name = Prop("name") ?? "";
                var type = Prop("type") ?? "";

                if (string.IsNullOrEmpty(armId) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(type))
                    continue;

                results.Add(new DiscoveredAzureResource(
                    ArmId: armId,
                    Name: name,
                    ResourceType: type,
                    Location: Prop("location") ?? string.Empty,
                    ResourceGroup: Prop("resourceGroup"),
                    SubscriptionId: Prop("subscriptionId"),
                    Sku: Prop("sku"),
                    Tags: Prop("tags"),
                    Kind: Prop("kind"),
                    Properties: Prop("properties")));
            }
            return;
        }

        // Table format: object with "columns" and "rows" arrays.
        if (!root.TryGetProperty("columns", out var columnsEl) ||
            !root.TryGetProperty("rows", out var rowsEl))
            return;

        var columns = columnsEl.EnumerateArray()
            .Select(c => c.GetProperty("name").GetString() ?? string.Empty)
            .ToList();

        int Col(string name) => columns.IndexOf(name);
        var idIdx = Col("id"); var nameIdx = Col("name"); var typeIdx = Col("type");
        var locIdx = Col("location"); var rgIdx = Col("resourceGroup");
        var subIdx = Col("subscriptionId"); var skuIdx = Col("sku");
        var tagsIdx = Col("tags"); var kindIdx = Col("kind"); var propsIdx = Col("properties");

        if (idIdx < 0 || nameIdx < 0 || typeIdx < 0)
            return;

        foreach (var row in rowsEl.EnumerateArray())
        {
            var cells = row.EnumerateArray().ToList();

            string? Get(int idx) =>
                idx >= 0 && idx < cells.Count && cells[idx].ValueKind != JsonValueKind.Null
                    ? cells[idx].ToString()
                    : null;

            var armId = Get(idIdx);
            var name = Get(nameIdx);
            var type = Get(typeIdx);

            if (string.IsNullOrEmpty(armId) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(type))
                continue;

            results.Add(new DiscoveredAzureResource(
                ArmId: armId,
                Name: name,
                ResourceType: type,
                Location: Get(locIdx) ?? string.Empty,
                ResourceGroup: Get(rgIdx),
                SubscriptionId: Get(subIdx),
                Sku: Get(skuIdx),
                Tags: Get(tagsIdx),
                Kind: Get(kindIdx),
                Properties: Get(propsIdx)));
        }
    }

    /// <summary>
    /// Discovers Azure Advisor recommendations for the specified subscriptions.
    /// Results can later be filtered to a specific discovered resource set.
    /// </summary>
    public virtual async Task<IReadOnlyList<DiscoveredAdvisorRecommendation>> DiscoverAdvisorRecommendationsAsync(
        IEnumerable<string> subscriptionIds,
        Guid? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("ResourceGraph.DiscoverAdvisorRecommendations");
        activity?.SetTag("correlation.id", correlationId?.ToString());

        var results = new List<DiscoveredAdvisorRecommendation>();
        var subList = subscriptionIds
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (subList.Count == 0)
            return results;

        var kql = """
            Resources
            | where type =~ 'microsoft.advisor/recommendations'
            | project
                id,
                name,
                subscriptionId,
                category = tostring(properties.category),
                impact = tostring(properties.impact),
                risk = tostring(properties.risk),
                description = tostring(properties.shortDescription.problem),
                remediation = tostring(properties.shortDescription.solution),
                resourceId = tostring(properties.resourceMetadata.resourceId),
                recommendationTypeId = tostring(properties.recommendationTypeId),
                learnMoreLink = tostring(properties.learnMoreLink)
            """;

        try
        {
            var tenant = _armClient.GetTenants().FirstOrDefault();
            if (tenant == null)
                return results;

            string? skipToken = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                var request = new ResourceQueryContent(kql);
                foreach (var sub in subList)
                    request.Subscriptions.Add(sub);

                if (!string.IsNullOrEmpty(skipToken))
                    request.Options = new ResourceQueryRequestOptions { SkipToken = skipToken };

                var response = await tenant.GetResourcesAsync(request, cancellationToken);
                ParseAdvisorRows(response.Value.Data, results);
                skipToken = response.Value.SkipToken;
            }
            while (!string.IsNullOrEmpty(skipToken));

            activity?.SetTag("result.count", results.Count);
            _logger.LogInformation(
                "Discovered {Count} Azure Advisor recommendation(s) across {SubCount} subscription(s) [correlation={CorrelationId}]",
                results.Count, subList.Count, correlationId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogWarning(ex,
                "Azure Advisor recommendation discovery failed [correlation={CorrelationId}]",
                correlationId);
        }

        return results;
    }

    /// <summary>
    /// Discovers non-compliant Azure Policy states for the specified subscriptions.
    /// </summary>
    public virtual async Task<IReadOnlyList<DiscoveredPolicyFinding>> DiscoverPolicyNonComplianceAsync(
        IEnumerable<string> subscriptionIds,
        Guid? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("ResourceGraph.DiscoverPolicyNonCompliance");
        activity?.SetTag("correlation.id", correlationId?.ToString());

        var results = new List<DiscoveredPolicyFinding>();
        var subList = subscriptionIds
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (subList.Count == 0)
            return results;

        var kql = """
            PolicyResources
            | where type =~ 'microsoft.policyinsights/policystates'
            | where tostring(properties.complianceState) =~ 'NonCompliant'
            | summarize arg_max(todatetime(properties.timestamp), *) by
                resourceId = tostring(properties.resourceId),
                policyAssignmentId = tostring(properties.policyAssignmentId),
                policyDefinitionId = tostring(properties.policyDefinitionId)
            | project
                findingId = id,
                subscriptionId = tostring(split(tostring(properties.resourceId), '/')[2]),
                resourceId = tostring(properties.resourceId),
                policyAssignmentId = tostring(properties.policyAssignmentId),
                policyAssignmentName = tostring(properties.policyAssignmentName),
                policyDefinitionId = tostring(properties.policyDefinitionId),
                policyDefinitionName = tostring(properties.policyDefinitionName),
                complianceState = tostring(properties.complianceState),
                description = tostring(properties.policyDefinitionAction)
            """;

        try
        {
            var tenant = _armClient.GetTenants().FirstOrDefault();
            if (tenant == null)
                return results;

            string? skipToken = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                var request = new ResourceQueryContent(kql);
                foreach (var sub in subList)
                    request.Subscriptions.Add(sub);

                if (!string.IsNullOrEmpty(skipToken))
                    request.Options = new ResourceQueryRequestOptions { SkipToken = skipToken };

                var response = await tenant.GetResourcesAsync(request, cancellationToken);
                ParsePolicyRows(response.Value.Data, results);
                skipToken = response.Value.SkipToken;
            }
            while (!string.IsNullOrEmpty(skipToken));

            activity?.SetTag("result.count", results.Count);
            _logger.LogInformation(
                "Discovered {Count} non-compliant Azure Policy finding(s) across {SubCount} subscription(s) [correlation={CorrelationId}]",
                results.Count, subList.Count, correlationId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogWarning(ex,
                "Azure Policy non-compliance discovery failed [correlation={CorrelationId}]",
                correlationId);
        }

        return results;
    }

    /// <summary>
    /// Discovers Defender for Cloud assessments for the specified subscriptions.
    /// </summary>
    public virtual async Task<IReadOnlyList<DiscoveredDefenderAssessment>> DiscoverDefenderAssessmentsAsync(
        IEnumerable<string> subscriptionIds,
        Guid? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("ResourceGraph.DiscoverDefenderAssessments");
        activity?.SetTag("correlation.id", correlationId?.ToString());

        var results = new List<DiscoveredDefenderAssessment>();
        var subList = subscriptionIds
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (subList.Count == 0)
            return results;

        var kql = """
            SecurityResources
            | where type =~ 'microsoft.security/assessments'
            | project
                assessmentId = id,
                name = tostring(properties.displayName),
                subscriptionId,
                resourceId = tostring(properties.resourceDetails.id),
                statusCode = tostring(properties.status.code),
                severity = tostring(properties.metadata.severity),
                description = tostring(properties.metadata.description),
                remediation = tostring(properties.metadata.remediationDescription),
                learnMoreLink = tostring(properties.links.azurePortal)
            """;

        try
        {
            var tenant = _armClient.GetTenants().FirstOrDefault();
            if (tenant == null)
                return results;

            string? skipToken = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                var request = new ResourceQueryContent(kql);
                foreach (var sub in subList)
                    request.Subscriptions.Add(sub);

                if (!string.IsNullOrEmpty(skipToken))
                    request.Options = new ResourceQueryRequestOptions { SkipToken = skipToken };

                var response = await tenant.GetResourcesAsync(request, cancellationToken);
                ParseDefenderRows(response.Value.Data, results);
                skipToken = response.Value.SkipToken;
            }
            while (!string.IsNullOrEmpty(skipToken));

            activity?.SetTag("result.count", results.Count);
            _logger.LogInformation(
                "Discovered {Count} Defender assessment(s) across {SubCount} subscription(s) [correlation={CorrelationId}]",
                results.Count, subList.Count, correlationId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogWarning(ex,
                "Defender assessment discovery failed [correlation={CorrelationId}]",
                correlationId);
        }

        return results;
    }

    private static void ParseAdvisorRows(BinaryData data, List<DiscoveredAdvisorRecommendation> results)
    {
        using var doc = JsonDocument.Parse(data.ToStream());
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var obj in root.EnumerateArray())
            {
                string? Prop(string key) => obj.TryGetProperty(key, out var el)
                    ? GetJsonStringFromElement(el)
                    : null;

                var id = Prop("id");
                var name = Prop("name");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                    continue;

                results.Add(new DiscoveredAdvisorRecommendation(
                    RecommendationId: id,
                    Name: name,
                    SubscriptionId: Prop("subscriptionId"),
                    Category: Prop("category"),
                    Impact: Prop("impact"),
                    Risk: Prop("risk"),
                    Description: Prop("description"),
                    Remediation: Prop("remediation"),
                    ResourceId: Prop("resourceId"),
                    RecommendationTypeId: Prop("recommendationTypeId"),
                    LearnMoreLink: Prop("learnMoreLink")));
            }
            return;
        }

        if (!root.TryGetProperty("columns", out var columnsEl) ||
            !root.TryGetProperty("rows", out var rowsEl))
            return;

        var columns = columnsEl.EnumerateArray()
            .Select(c => c.GetProperty("name").GetString() ?? string.Empty)
            .ToList();

        int Col(string name) => columns.IndexOf(name);
        var idIdx = Col("id");
        var nameIdx = Col("name");
        if (idIdx < 0 || nameIdx < 0)
            return;

        var subIdx = Col("subscriptionId");
        var categoryIdx = Col("category");
        var impactIdx = Col("impact");
        var riskIdx = Col("risk");
        var descriptionIdx = Col("description");
        var remediationIdx = Col("remediation");
        var resourceIdIdx = Col("resourceId");
        var typeIdIdx = Col("recommendationTypeId");
        var learnIdx = Col("learnMoreLink");

        foreach (var row in rowsEl.EnumerateArray())
        {
            var cells = row.EnumerateArray().ToList();
            var id = GetJsonString(cells, idIdx);
            var name = GetJsonString(cells, nameIdx);
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                continue;

            results.Add(new DiscoveredAdvisorRecommendation(
                RecommendationId: id,
                Name: name,
                SubscriptionId: GetJsonString(cells, subIdx),
                Category: GetJsonString(cells, categoryIdx),
                Impact: GetJsonString(cells, impactIdx),
                Risk: GetJsonString(cells, riskIdx),
                Description: GetJsonString(cells, descriptionIdx),
                Remediation: GetJsonString(cells, remediationIdx),
                ResourceId: GetJsonString(cells, resourceIdIdx),
                RecommendationTypeId: GetJsonString(cells, typeIdIdx),
                LearnMoreLink: GetJsonString(cells, learnIdx)));
        }
    }

    private static void ParsePolicyRows(BinaryData data, List<DiscoveredPolicyFinding> results)
    {
        using var doc = JsonDocument.Parse(data.ToStream());
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var obj in root.EnumerateArray())
            {
                string? Prop(string key) => obj.TryGetProperty(key, out var el)
                    ? GetJsonStringFromElement(el)
                    : null;

                var findingId = Prop("findingId") ?? Prop("id");
                if (string.IsNullOrWhiteSpace(findingId))
                    continue;

                results.Add(new DiscoveredPolicyFinding(
                    FindingId: findingId,
                    SubscriptionId: Prop("subscriptionId"),
                    ResourceId: Prop("resourceId"),
                    PolicyAssignmentId: Prop("policyAssignmentId"),
                    PolicyAssignmentName: Prop("policyAssignmentName"),
                    PolicyDefinitionId: Prop("policyDefinitionId"),
                    PolicyDefinitionName: Prop("policyDefinitionName"),
                    ComplianceState: Prop("complianceState"),
                    Description: Prop("description")));
            }

            return;
        }

        if (!root.TryGetProperty("columns", out var columnsEl) ||
            !root.TryGetProperty("rows", out var rowsEl))
            return;

        var columns = columnsEl.EnumerateArray()
            .Select(c => c.GetProperty("name").GetString() ?? string.Empty)
            .ToList();

        int Col(string name) => columns.IndexOf(name);
        var findingIdIdx = Col("findingId");
        var idIdx = Col("id");
        var subIdx = Col("subscriptionId");
        var resourceIdIdx = Col("resourceId");
        var assignmentIdIdx = Col("policyAssignmentId");
        var assignmentNameIdx = Col("policyAssignmentName");
        var defIdIdx = Col("policyDefinitionId");
        var defNameIdx = Col("policyDefinitionName");
        var complianceIdx = Col("complianceState");
        var descriptionIdx = Col("description");

        foreach (var row in rowsEl.EnumerateArray())
        {
            var cells = row.EnumerateArray().ToList();
            var findingId = GetJsonString(cells, findingIdIdx) ?? GetJsonString(cells, idIdx);
            if (string.IsNullOrWhiteSpace(findingId))
                continue;

            results.Add(new DiscoveredPolicyFinding(
                FindingId: findingId,
                SubscriptionId: GetJsonString(cells, subIdx),
                ResourceId: GetJsonString(cells, resourceIdIdx),
                PolicyAssignmentId: GetJsonString(cells, assignmentIdIdx),
                PolicyAssignmentName: GetJsonString(cells, assignmentNameIdx),
                PolicyDefinitionId: GetJsonString(cells, defIdIdx),
                PolicyDefinitionName: GetJsonString(cells, defNameIdx),
                ComplianceState: GetJsonString(cells, complianceIdx),
                Description: GetJsonString(cells, descriptionIdx)));
        }
    }

    private static void ParseDefenderRows(BinaryData data, List<DiscoveredDefenderAssessment> results)
    {
        using var doc = JsonDocument.Parse(data.ToStream());
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var obj in root.EnumerateArray())
            {
                string? Prop(string key) => obj.TryGetProperty(key, out var el)
                    ? GetJsonStringFromElement(el)
                    : null;

                var id = Prop("assessmentId") ?? Prop("id");
                var name = Prop("name");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                    continue;

                results.Add(new DiscoveredDefenderAssessment(
                    AssessmentId: id,
                    Name: name,
                    SubscriptionId: Prop("subscriptionId"),
                    ResourceId: Prop("resourceId"),
                    StatusCode: Prop("statusCode"),
                    Severity: Prop("severity"),
                    Description: Prop("description"),
                    Remediation: Prop("remediation"),
                    LearnMoreLink: Prop("learnMoreLink")));
            }

            return;
        }

        if (!root.TryGetProperty("columns", out var columnsEl) ||
            !root.TryGetProperty("rows", out var rowsEl))
            return;

        var columns = columnsEl.EnumerateArray()
            .Select(c => c.GetProperty("name").GetString() ?? string.Empty)
            .ToList();

        int Col(string name) => columns.IndexOf(name);
        var idIdx = Col("assessmentId");
        var fallbackIdIdx = Col("id");
        var nameIdx = Col("name");
        if ((idIdx < 0 && fallbackIdIdx < 0) || nameIdx < 0)
            return;

        var subIdx = Col("subscriptionId");
        var resourceIdIdx = Col("resourceId");
        var statusIdx = Col("statusCode");
        var severityIdx = Col("severity");
        var descriptionIdx = Col("description");
        var remediationIdx = Col("remediation");
        var learnIdx = Col("learnMoreLink");

        foreach (var row in rowsEl.EnumerateArray())
        {
            var cells = row.EnumerateArray().ToList();
            var id = GetJsonString(cells, idIdx) ?? GetJsonString(cells, fallbackIdIdx);
            var name = GetJsonString(cells, nameIdx);
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                continue;

            results.Add(new DiscoveredDefenderAssessment(
                AssessmentId: id,
                Name: name,
                SubscriptionId: GetJsonString(cells, subIdx),
                ResourceId: GetJsonString(cells, resourceIdIdx),
                StatusCode: GetJsonString(cells, statusIdx),
                Severity: GetJsonString(cells, severityIdx),
                Description: GetJsonString(cells, descriptionIdx),
                Remediation: GetJsonString(cells, remediationIdx),
                LearnMoreLink: GetJsonString(cells, learnIdx)));
        }
    }

    /// <summary>
    /// Discovers Azure Service Groups (microsoft.management/servicegroups) accessible to
    /// the managed identity by querying membership relationships and then fetching details.
    ///
    /// Service Groups (preview) are NOT indexed in Azure Resource Graph's ResourceContainers
    /// table, so we discover them through a two-stage approach:
    ///   1. Query RelationshipResources for microsoft.relationships/servicegroupmember records
    ///      to find which Service Group ARM IDs exist.
    ///   2. GET each Service Group via the ARM REST API to retrieve displayName and parent hierarchy.
    ///
    /// Requires: Service Group Reader role on the root service group
    ///   (/providers/Microsoft.Management/serviceGroups/{tenantId}) for the user-assigned MI.
    ///
    /// See: https://learn.microsoft.com/azure/governance/service-groups/overview
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>All accessible Azure Service Groups ordered by name.</returns>
    public virtual async Task<IReadOnlyList<DiscoveredAzureServiceGroup>> DiscoverAzureServiceGroupsAsync(
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("ResourceGraph.DiscoverAzureServiceGroups");

        var results = new List<DiscoveredAzureServiceGroup>();

        try
        {
            var tenant = _armClient.GetTenants().FirstOrDefault();
            if (tenant == null)
            {
                _logger.LogWarning("No accessible tenants for Azure Service Group discovery");
                return results;
            }

            // Stage 1: Discover SG ARM IDs from RelationshipResources.
            // Service Groups aren't in ResourceContainers (preview limitation),
            // so we find them through their subscription/resource membership.
            var allSubscriptionIds = await GetAccessibleSubscriptionIdsAsync(cancellationToken);
            if (allSubscriptionIds.Count == 0)
            {
                _logger.LogWarning("No accessible subscriptions; cannot query RelationshipResources for service group discovery");
                return results;
            }

            const string memberQuery = """
                RelationshipResources
                | where type =~ 'microsoft.relationships/servicegroupmember'
                | project targetId = tostring(properties.TargetId)
                | distinct targetId
                """;

            var sgArmIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? skipToken = null;

            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = new ResourceQueryContent(memberQuery);
                foreach (var sub in allSubscriptionIds)
                    request.Subscriptions.Add(sub);

                if (!string.IsNullOrEmpty(skipToken))
                    request.Options = new ResourceQueryRequestOptions { SkipToken = skipToken };

                var response = await tenant.GetResourcesAsync(request, cancellationToken);
                ParseDistinctTargetIds(response.Value.Data, sgArmIds);
                skipToken = response.Value.SkipToken;
            }
            while (!string.IsNullOrEmpty(skipToken) && !cancellationToken.IsCancellationRequested);

            _logger.LogInformation(
                "Found {Count} Service Group ARM ID(s) from RelationshipResources", sgArmIds.Count);

            if (sgArmIds.Count == 0)
                return results;

            // Stage 2: GET each Service Group by ARM ID to retrieve displayName and parent.
            // If REST GET fails (e.g. 403 before RBAC propagates), fall back to parsing the name from the ARM ID.
            foreach (var armId in sgArmIds)
            {
                var sg = await GetServiceGroupByArmIdAsync(armId, cancellationToken)
                         ?? ParseServiceGroupFromArmId(armId);
                if (sg != null)
                    results.Add(sg);
            }

            // Stage 3: Supplement with a direct list from the management API so that
            // service groups with no member resources are also discovered.
            var listedIds = await ListServiceGroupArmIdsAsync(cancellationToken);
            var newFromList = listedIds.Where(id => !sgArmIds.Contains(id)).ToList();
            if (newFromList.Count > 0)
            {
                _logger.LogInformation(
                    "Direct list found {Count} additional Service Group(s) not visible via RelationshipResources",
                    newFromList.Count);
                foreach (var armId in newFromList)
                {
                    var sg = await GetServiceGroupByArmIdAsync(armId, cancellationToken)
                             ?? ParseServiceGroupFromArmId(armId);
                    if (sg != null)
                        results.Add(sg);
                }
            }

            results.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            activity?.SetTag("result.count", results.Count);
            _logger.LogInformation(
                "Discovered {Count} Azure Service Group(s) total (membership + direct list)", results.Count);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Azure Service Group discovery failed");
            throw;
        }

        return results;
    }

    /// <summary>
    /// Lists all Azure Service Group ARM IDs visible to the managed identity by calling
    /// the management REST API list endpoint directly. Returns an empty collection if the
    /// endpoint is unavailable or returns an error (non-throwing; logs a warning).
    /// </summary>
    private async Task<IReadOnlyList<string>> ListServiceGroupArmIdsAsync(
        CancellationToken cancellationToken)
    {
        const string apiVersion = "2024-02-01-preview";
        const string url = $"https://management.azure.com/providers/Microsoft.Management/serviceGroups?api-version={apiVersion}";

        var armIds = new List<string>();
        try
        {
            var tokenRequest = new TokenRequestContext(["https://management.azure.com/.default"]);
            var token = await _credential.GetTokenAsync(tokenRequest, cancellationToken);

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token.Token);

            string? nextLink = url;
            while (!string.IsNullOrEmpty(nextLink))
            {
                using var response = await httpClient.GetAsync(nextLink, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Could not list Service Groups via management API: HTTP {StatusCode}",
                        (int)response.StatusCode);
                    break;
                }

                using var doc = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);
                var root = doc.RootElement;

                if (root.TryGetProperty("value", out var valueEl))
                {
                    foreach (var item in valueEl.EnumerateArray())
                    {
                        var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                        if (!string.IsNullOrEmpty(id))
                            armIds.Add(id);
                    }
                }

                nextLink = root.TryGetProperty("nextLink", out var nlEl) ? nlEl.GetString() : null;
            }

            _logger.LogInformation("Direct management API list returned {Count} Service Group(s)", armIds.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list Service Groups via management API; membership-only results will be used");
        }

        return armIds;
    }

    /// <summary>
    /// GETs a single Azure Service Group by ARM ID using the REST API directly.
    /// Returns null if the SG is not accessible or does not exist.
    /// </summary>
    private async Task<DiscoveredAzureServiceGroup?> GetServiceGroupByArmIdAsync(
        string armId, CancellationToken cancellationToken)
    {
        const string apiVersion = "2024-02-01-preview";
        var url = $"https://management.azure.com{armId}?api-version={apiVersion}";

        try
        {
            var tokenRequest = new TokenRequestContext(["https://management.azure.com/.default"]);
            var token = await _credential.GetTokenAsync(tokenRequest, cancellationToken);

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token.Token);

            using var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Failed to GET Service Group {ArmId}: HTTP {StatusCode}",
                    armId, (int)response.StatusCode);
                return null;
            }

            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);

            var root = doc.RootElement;
            var name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";

            string? displayName = null;
            string? parentArmId = null;

            if (root.TryGetProperty("properties", out var propsEl))
            {
                if (propsEl.TryGetProperty("displayName", out var dnEl))
                    displayName = dnEl.GetString();
                if (propsEl.TryGetProperty("parent", out var parentEl) &&
                    parentEl.TryGetProperty("resourceId", out var parentIdEl))
                    parentArmId = parentIdEl.GetString();
            }

            return new DiscoveredAzureServiceGroup(
                ArmId: armId,
                Name: name,
                DisplayName: !string.IsNullOrEmpty(displayName) ? displayName : name,
                ParentArmId: parentArmId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to GET Service Group details for {ArmId}; skipping", armId);
            return null;
        }
    }

    /// <summary>
    /// Parses distinct targetId values from a RelationshipResources query result.
    /// Supports both ObjectArray and Table wire formats.
    /// </summary>
    private static void ParseDistinctTargetIds(BinaryData data, HashSet<string> targetIds)
    {
        using var doc = JsonDocument.Parse(data.ToStream());
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var obj in root.EnumerateArray())
            {
                var targetId = obj.TryGetProperty("targetId", out var el) ? el.GetString() : null;
                if (!string.IsNullOrEmpty(targetId))
                    targetIds.Add(targetId);
            }
            return;
        }

        if (!root.TryGetProperty("columns", out var columnsEl) ||
            !root.TryGetProperty("rows", out var rowsEl))
            return;

        var columns = columnsEl.EnumerateArray()
            .Select(c => c.GetProperty("name").GetString() ?? string.Empty)
            .ToList();

        var targetIdIdx = columns.IndexOf("targetId");
        if (targetIdIdx < 0) return;

        foreach (var row in rowsEl.EnumerateArray())
        {
            var cells = row.EnumerateArray().ToList();
            var targetId = GetJsonString(cells, targetIdIdx);
            if (!string.IsNullOrEmpty(targetId))
                targetIds.Add(targetId);
        }
    }

    /// <summary>
    /// Extracts the Service Group name from its ARM ID when the REST API is inaccessible.
    /// ARM ID format: /providers/Microsoft.Management/serviceGroups/{name}
    /// </summary>
    private static DiscoveredAzureServiceGroup? ParseServiceGroupFromArmId(string armId)
    {
        const string prefix = "/providers/Microsoft.Management/serviceGroups/";
        var idx = armId.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var name = armId[(idx + prefix.Length)..].TrimEnd('/');
        if (string.IsNullOrEmpty(name)) return null;

        return new DiscoveredAzureServiceGroup(
            ArmId: armId,
            Name: name,
            DisplayName: name,
            ParentArmId: null);
    }

    /// <summary>
    /// Parses the columns/rows table returned by Resource Graph into typed records.
    /// Handles two formats returned by the Azure SDK depending on the API version and result format:
    ///   Table format:       { "columns": [{"name":"...","type":"..."},...], "rows": [[val,...],...] }
    ///   ObjectArray format: [ {"id":"...", "name":"...", "displayName":..., "parentId":...}, ... ]
    /// </summary>
    private static void ParseServiceGroupRows(BinaryData data, List<DiscoveredAzureServiceGroup> results)
    {
        using var doc = JsonDocument.Parse(data.ToStream());
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            // ObjectArray format: array of JSON objects with column names as keys.
            foreach (var obj in root.EnumerateArray())
            {
                var armId = obj.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                var name = obj.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                var displayName = obj.TryGetProperty("displayName", out var displayNameEl)
                    ? GetJsonStringFromElement(displayNameEl) : null;
                var parentId = obj.TryGetProperty("parentId", out var parentIdEl)
                    ? GetJsonStringFromElement(parentIdEl) : null;

                if (!string.IsNullOrEmpty(armId) && !string.IsNullOrEmpty(name))
                {
                    results.Add(new DiscoveredAzureServiceGroup(
                        ArmId: armId,
                        Name: name,
                        DisplayName: string.IsNullOrEmpty(displayName) ? null : displayName,
                        ParentArmId: string.IsNullOrEmpty(parentId) ? null : parentId));
                }
            }
            return;
        }

        // Table format: object with "columns" and "rows" arrays.
        if (!root.TryGetProperty("columns", out var columnsEl) ||
            !root.TryGetProperty("rows", out var rowsEl))
            return;

        var columns = columnsEl.EnumerateArray()
            .Select(c => c.GetProperty("name").GetString() ?? string.Empty)
            .ToList();

        var idIdx = columns.IndexOf("id");
        var nameIdx = columns.IndexOf("name");
        var displayNameIdx = columns.IndexOf("displayName");
        var parentIdIdx = columns.IndexOf("parentId");

        // id and name are required; displayName and parentId are optional
        if (idIdx < 0 || nameIdx < 0)
            return;

        foreach (var row in rowsEl.EnumerateArray())
        {
            var cells = row.EnumerateArray().ToList();
            var requiredMax = Math.Max(idIdx, nameIdx);
            if (cells.Count <= requiredMax)
                continue;

            var armId = cells[idIdx].GetString() ?? string.Empty;
            var name = cells[nameIdx].GetString() ?? string.Empty;
            var displayName = GetJsonString(cells, displayNameIdx);
            var parentId = GetJsonString(cells, parentIdIdx);

            if (!string.IsNullOrEmpty(armId) && !string.IsNullOrEmpty(name))
            {
                results.Add(new DiscoveredAzureServiceGroup(
                    ArmId: armId,
                    Name: name,
                    DisplayName: string.IsNullOrEmpty(displayName) ? null : displayName,
                    ParentArmId: string.IsNullOrEmpty(parentId) ? null : parentId));
            }
        }
    }

    /// <summary>
    /// Safely coerces a <see cref="JsonElement"/> to a nullable string.
    /// Used when parsing ObjectArray format rows where dynamic properties may not be plain strings.
    /// </summary>
    private static string? GetJsonStringFromElement(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Object when el.GetRawText() == "{}" => null,
            _ => el.GetRawText()
        };

    /// <summary>
    /// Safely extracts a string from a Resource Graph table-format row cell by index.
    /// KQL dynamic property projections can serialize as <see cref="JsonValueKind.Object"/>
    /// (<c>{}</c>) when the property is absent rather than as <c>null</c>.
    /// </summary>
    private static string? GetJsonString(List<JsonElement> cells, int idx)
    {
        if (idx < 0 || idx >= cells.Count) return null;
        return GetJsonStringFromElement(cells[idx]);
    }

    /// <summary>
    /// Discovers all Azure resources that are members of the specified Azure Service Groups
    /// via <c>Microsoft.Relationships/ServiceGroupMember</c> extension resources in Resource Graph.
    ///
    /// Member scopes can be:
    ///   - A full subscription  → all resources in that subscription are included
    ///   - A resource group     → all resources in that RG are included
    ///   - A specific resource  → that individual resource is included
    ///
    /// The method uses a two-stage approach:
    ///   1. Query <c>RelationshipResources</c> with all accessible subscription IDs to find member
    ///      source scopes (the table is subscription-scoped and returns empty without them).
    ///   2. Build a targeted <c>Resources</c> query that covers all resolved scopes.
    /// </summary>
    /// <param name="serviceGroupArmIds">ARM IDs of the service groups to resolve members for (the group itself plus all descendants).</param>
    /// <param name="correlationId">Correlation ID for tracing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public virtual async Task<IReadOnlyList<DiscoveredAzureResource>> DiscoverServiceGroupMembersAsync(
        IEnumerable<string> serviceGroupArmIds,
        Guid? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var armIdList = serviceGroupArmIds.ToList();
        if (armIdList.Count == 0)
        {
            _logger.LogWarning("DiscoverServiceGroupMembersAsync called with no service group IDs [correlation={CorrelationId}]", correlationId);
            return [];
        }

        using var activity = ActivitySource.StartActivity("ResourceGraph.DiscoverServiceGroupMembers");
        activity?.SetTag("correlation.id", correlationId?.ToString());
        activity?.SetTag("service_group.count", armIdList.Count);

        var results = new List<DiscoveredAzureResource>();

        try
        {
            var tenant = _armClient.GetTenants().FirstOrDefault();
            if (tenant == null)
            {
                _logger.LogWarning("No accessible tenant found; cannot discover service group members [correlation={CorrelationId}]", correlationId);
                return results;
            }

            // RelationshipResources is subscription-scoped: queries return empty without explicit
            // subscription IDs, so we must enumerate accessible subscriptions up front.
            var allSubscriptionIds = await GetAccessibleSubscriptionIdsAsync(cancellationToken);
            if (allSubscriptionIds.Count == 0)
            {
                _logger.LogWarning("No accessible subscriptions; cannot query RelationshipResources [correlation={CorrelationId}]", correlationId);
                return results;
            }

            // Stage 1: resolve service group membership from RelationshipResources.
            // Actual resource type is "microsoft.relationships/servicegroupmember" with:
            //   properties.TargetId  = service group ARM ID
            //   properties.SourceId  = member scope (subscription / resource group / specific resource)
            //   properties.Metadata.SourceType = Microsoft.Resources/subscriptions | .../resourceGroups | other
            var sanitisedIds = armIdList
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Replace("'", "''"))
                .ToList();
            var sgInList = string.Join(", ", sanitisedIds.Select(id => $"'{id}'"));

            var memberQuery = $"""
                RelationshipResources
                | where type =~ "microsoft.relationships/servicegroupmember"
                | where properties.TargetId in~ ({sgInList})
                | project
                    sourceId   = tostring(properties.SourceId),
                    sourceType = tostring(properties.Metadata.SourceType)
                | distinct sourceId, sourceType
                """;

            var memberSources = new List<(string SourceId, string SourceType)>();
            string? skipToken = null;

            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var memberRequest = new ResourceQueryContent(memberQuery);
                foreach (var sub in allSubscriptionIds)
                    memberRequest.Subscriptions.Add(sub);

                if (!string.IsNullOrEmpty(skipToken))
                    memberRequest.Options = new ResourceQueryRequestOptions { SkipToken = skipToken };

                var memberResponse = await tenant.GetResourcesAsync(memberRequest, cancellationToken);
                ParseMemberSourceRows(memberResponse.Value.Data, memberSources);
                skipToken = memberResponse.Value.SkipToken;
            }
            while (!string.IsNullOrEmpty(skipToken));

            _logger.LogInformation(
                "Found {MemberCount} Service Group member scope(s) across {GroupCount} group(s) [correlation={CorrelationId}]",
                memberSources.Count, armIdList.Count, correlationId);

            if (memberSources.Count == 0)
                return results;

            // Stage 2: categorise member scopes and build a targeted Resources query.
            var subscriptionScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rgScopes = new List<(string SubscriptionId, string ResourceGroup)>();
            var specificIds = new List<string>();

            foreach (var (sourceId, sourceType) in memberSources)
            {
                if (sourceType.Equals("Microsoft.Resources/subscriptions", StringComparison.OrdinalIgnoreCase))
                {
                    var subId = ExtractSubscriptionIdFromArmId(sourceId);
                    if (subId != null) subscriptionScopes.Add(subId);
                }
                else if (sourceType.Equals("Microsoft.Resources/subscriptions/resourceGroups", StringComparison.OrdinalIgnoreCase))
                {
                    var (subId, rgName) = ExtractSubscriptionAndRgFromArmId(sourceId);
                    if (subId != null && rgName != null) rgScopes.Add((subId, rgName));
                }
                else
                {
                    specificIds.Add(sourceId.ToLowerInvariant());
                }
            }

            // Determine which subscriptions to pass to the Resources query.
            var querySubscriptions = new HashSet<string>(subscriptionScopes, StringComparer.OrdinalIgnoreCase);
            foreach (var (subId, _) in rgScopes) querySubscriptions.Add(subId);
            foreach (var id in specificIds)
            {
                var subId = ExtractSubscriptionIdFromArmId(id);
                if (subId != null) querySubscriptions.Add(subId);
            }

            if (querySubscriptions.Count == 0)
                return results;

            // Build WHERE clause combining all member scope types.
            var conditions = new List<string>();

            if (subscriptionScopes.Count > 0)
            {
                var subInList = string.Join(", ", subscriptionScopes.Select(s => $"'{s.Replace("'", "''")}'"));
                conditions.Add($"subscriptionId in~ ({subInList})");
            }

            foreach (var (subId, rgName) in rgScopes)
            {
                var safeSubId = subId.Replace("'", "''");
                var safeRg = rgName.Replace("'", "''");
                conditions.Add($"(subscriptionId =~ '{safeSubId}' and resourceGroup =~ '{safeRg}')");
            }

            // Batch specific resource IDs into chunks to stay within KQL query size limits.
            const int idBatchSize = 500;
            for (int offset = 0; offset < specificIds.Count; offset += idBatchSize)
            {
                var batch = specificIds.GetRange(offset, Math.Min(idBatchSize, specificIds.Count - offset));
                var idInList = string.Join(", ", batch.Select(id => $"'{id.Replace("'", "''")}'"));
                conditions.Add($"tolower(id) in~ ({idInList})");
            }

            // If all conditions fit in one query, execute as a single request.
            // If specific-ID batching produced multiple conditions, they're all ORd together.
            var whereClause = string.Join(" or ", conditions);
            var resourceQuery = $"""
                Resources
                | where type !startswith 'microsoft.advisor/'
                  and type !startswith 'microsoft.security/policies'
                | where {whereClause}
                | project id, name, type, location, resourceGroup, subscriptionId, sku, tags, kind, properties
                """;

            string? resourceSkipToken = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resourceRequest = new ResourceQueryContent(resourceQuery);
                foreach (var sub in querySubscriptions)
                    resourceRequest.Subscriptions.Add(sub);

                if (!string.IsNullOrEmpty(resourceSkipToken))
                    resourceRequest.Options = new ResourceQueryRequestOptions { SkipToken = resourceSkipToken };

                var resourceResponse = await tenant.GetResourcesAsync(resourceRequest, cancellationToken);
                ParseResourceRows(resourceResponse.Value.Data, results);
                resourceSkipToken = resourceResponse.Value.SkipToken;
            }
            while (!string.IsNullOrEmpty(resourceSkipToken));

            activity?.SetTag("result.count", results.Count);
            _logger.LogInformation(
                "Discovered {Count} Azure resource(s) via Service Group membership for {GroupCount} group(s) [correlation={CorrelationId}]",
                results.Count, armIdList.Count, correlationId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex,
                "Azure Service Group member discovery failed [correlation={CorrelationId}]",
                correlationId);
        }

        return results;
    }

    /// <summary>
    /// Parses (sourceId, sourceType) pairs from a RelationshipResources query result,
    /// supporting both ObjectArray and Table wire formats.
    /// </summary>
    private static void ParseMemberSourceRows(BinaryData data, List<(string SourceId, string SourceType)> results)
    {
        using var doc = JsonDocument.Parse(data.ToStream());
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var obj in root.EnumerateArray())
            {
                var sourceId = obj.TryGetProperty("sourceId", out var sid) ? GetJsonStringFromElement(sid) : null;
                var sourceType = obj.TryGetProperty("sourceType", out var stype) ? GetJsonStringFromElement(stype) : null;
                if (!string.IsNullOrEmpty(sourceId))
                    results.Add((sourceId, sourceType ?? string.Empty));
            }
            return;
        }

        if (!root.TryGetProperty("columns", out var columnsEl) ||
            !root.TryGetProperty("rows", out var rowsEl))
            return;

        var columns = columnsEl.EnumerateArray()
            .Select(c => c.GetProperty("name").GetString() ?? string.Empty)
            .ToList();

        var sourceIdIdx = columns.IndexOf("sourceId");
        var sourceTypeIdx = columns.IndexOf("sourceType");
        if (sourceIdIdx < 0) return;

        foreach (var row in rowsEl.EnumerateArray())
        {
            var cells = row.EnumerateArray().ToList();
            var sourceId = GetJsonString(cells, sourceIdIdx);
            var sourceType = sourceTypeIdx >= 0 ? GetJsonString(cells, sourceTypeIdx) : null;
            if (!string.IsNullOrEmpty(sourceId))
                results.Add((sourceId, sourceType ?? string.Empty));
        }
    }

    private static string? ExtractSubscriptionIdFromArmId(string armId)
    {
        // /subscriptions/{guid}/... or /subscriptions/{guid}
        var span = armId.AsSpan();
        const string prefix = "/subscriptions/";
        var idx = span.IndexOf(prefix.AsSpan(), StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var after = span[(idx + prefix.Length)..];
        var slashIdx = after.IndexOf('/');
        return slashIdx < 0 ? after.ToString() : after[..slashIdx].ToString();
    }

    private static (string? SubscriptionId, string? ResourceGroup) ExtractSubscriptionAndRgFromArmId(string armId)
    {
        // /subscriptions/{guid}/resourceGroups/{name}/...
        var subId = ExtractSubscriptionIdFromArmId(armId);
        if (subId == null) return (null, null);

        const string rgSegment = "/resourceGroups/";
        var idx = armId.IndexOf(rgSegment, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return (subId, null);

        var after = armId[(idx + rgSegment.Length)..];
        var slashIdx = after.IndexOf('/');
        var rgName = slashIdx < 0 ? after : after[..slashIdx];
        return (subId, rgName);
    }

    /// <summary>
    /// Queries RelationshipResources to find which subscriptions are members of each service group.
    /// Returns a dictionary mapping service group ARM IDs to the set of member subscription IDs.
    /// </summary>
    public virtual async Task<Dictionary<string, HashSet<string>>> DiscoverServiceGroupSubscriptionMembersAsync(
        IEnumerable<string> serviceGroupArmIds,
        CancellationToken cancellationToken = default)
    {
        var armIdList = serviceGroupArmIds.ToList();
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        if (armIdList.Count == 0)
            return result;

        var tenant = _armClient.GetTenants().FirstOrDefault();
        if (tenant == null)
            return result;

        var allSubscriptionIds = await GetAccessibleSubscriptionIdsAsync(cancellationToken);
        if (allSubscriptionIds.Count == 0)
            return result;

        var sanitisedIds = armIdList
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Replace("'", "''"))
            .ToList();
        var sgInList = string.Join(", ", sanitisedIds.Select(id => $"'{id}'"));

        var memberQuery = $"""
            RelationshipResources
            | where type =~ "microsoft.relationships/servicegroupmember"
            | where properties.TargetId in~ ({sgInList})
            | project
                targetId = tostring(properties.TargetId),
                sourceId = tostring(properties.SourceId)
            | distinct targetId, sourceId
            """;

        string? skipToken = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new ResourceQueryContent(memberQuery);
            foreach (var sub in allSubscriptionIds)
                request.Subscriptions.Add(sub);

            if (!string.IsNullOrEmpty(skipToken))
                request.Options = new ResourceQueryRequestOptions { SkipToken = skipToken };

            var response = await tenant.GetResourcesAsync(request, cancellationToken);
            ParseMembershipRows(response.Value.Data, result);
            skipToken = response.Value.SkipToken;
        }
        while (!string.IsNullOrEmpty(skipToken));

        _logger.LogInformation(
            "Discovered subscription membership for {GroupCount} service group(s): {TotalMembers} total member subscription(s)",
            result.Count, result.Values.Sum(s => s.Count));

        return result;
    }

    private static void ParseMembershipRows(BinaryData data, Dictionary<string, HashSet<string>> result)
    {
        using var doc = JsonDocument.Parse(data.ToStream());
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var obj in root.EnumerateArray())
            {
                var targetId = obj.TryGetProperty("targetId", out var tid) ? GetJsonStringFromElement(tid) : null;
                var sourceId = obj.TryGetProperty("sourceId", out var sid) ? GetJsonStringFromElement(sid) : null;
                if (string.IsNullOrEmpty(targetId) || string.IsNullOrEmpty(sourceId)) continue;

                var subId = ExtractSubscriptionIdFromArmId(sourceId);
                if (subId == null) continue;

                if (!result.TryGetValue(targetId, out var subs))
                {
                    subs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    result[targetId] = subs;
                }
                subs.Add(subId);
            }
            return;
        }

        if (!root.TryGetProperty("columns", out var columnsEl) ||
            !root.TryGetProperty("rows", out var rowsEl))
            return;

        var columns = columnsEl.EnumerateArray()
            .Select(c => c.GetProperty("name").GetString() ?? string.Empty)
            .ToList();

        var targetIdIdx = columns.IndexOf("targetId");
        var sourceIdIdx = columns.IndexOf("sourceId");
        if (targetIdIdx < 0 || sourceIdIdx < 0) return;

        foreach (var row in rowsEl.EnumerateArray())
        {
            var cells = row.EnumerateArray().ToList();
            var targetId = GetJsonString(cells, targetIdIdx);
            var sourceId = GetJsonString(cells, sourceIdIdx);
            if (string.IsNullOrEmpty(targetId) || string.IsNullOrEmpty(sourceId)) continue;

            var subId = ExtractSubscriptionIdFromArmId(sourceId);
            if (subId == null) continue;

            if (!result.TryGetValue(targetId, out var subs))
            {
                subs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                result[targetId] = subs;
            }
            subs.Add(subId);
        }
    }
}
