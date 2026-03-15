using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Atlas.AgentOrchestrator.Integrations.Azure;

/// <summary>
/// Detects orphaned Azure resources using specific Resource Graph queries.
/// Based on patterns from https://github.com/dolevshor/azure-orphan-resources
/// Each detection method targets a specific resource type with known orphan patterns.
/// </summary>
public class OrphanDetectionService
{
    private readonly IResourceGraphClient _resourceGraphClient;
    private readonly LogAnalyticsWasteAnalyzer? _logAnalyticsAnalyzer;
    private readonly ILogger<OrphanDetectionService> _logger;
    private static readonly ActivitySource ActivitySource = new("Atlas.AgentOrchestrator.OrphanDetection");

    public OrphanDetectionService(
        IResourceGraphClient resourceGraphClient,
        ILogger<OrphanDetectionService> logger,
        LogAnalyticsWasteAnalyzer? logAnalyticsAnalyzer = null)
    {
        _resourceGraphClient = resourceGraphClient;
        _logger = logger;
        _logAnalyticsAnalyzer = logAnalyticsAnalyzer;
    }

    /// <summary>
    /// Execute all orphan detection checks and return aggregate results.
    /// </summary>
    public async Task<OrphanDetectionResult> DetectAllOrphansAsync(
        ServiceGroupScope scope,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("DetectAllOrphans");
        var stopwatch = Stopwatch.StartNew();

        var result = new OrphanDetectionResult { Scope = scope };

        try
        {
            // Run detections in parallel where possible
            var detectionTasks = new List<Task<List<OrphanedResource>>>
            {
                DetectUnattachedDisksAsync(scope, cancellationToken),
                DetectUnassociatedPublicIpsAsync(scope, cancellationToken),
                DetectUnusedNetworkInterfacesAsync(scope, cancellationToken),
                DetectOrphanedSnapshotsAsync(scope, cancellationToken),
                DetectEmptyAppServicePlansAsync(scope, cancellationToken),
                DetectUnusedAvailabilitySetsAsync(scope, cancellationToken),
                DetectOrphanedRouteTablesAsync(scope, cancellationToken),
                DetectOrphanedNetworkSecurityGroupsAsync(scope, cancellationToken),
                DetectUnusedLoadBalancersAsync(scope, cancellationToken),
                DetectOrphanedApplicationGatewaysAsync(scope, cancellationToken),
                DetectUnusedDiagnosticSettingsAsync(scope, cancellationToken)
            };

            var detectionResults = await Task.WhenAll(detectionTasks);

            // Aggregate all orphaned resources
            result.OrphanedResources = detectionResults.SelectMany(r => r).ToList();
            result.TotalOrphans = result.OrphanedResources.Count;
            result.TotalEstimatedMonthlyCost = result.OrphanedResources.Sum(r => r.EstimatedMonthlyCost);

            // Group by type
            result.OrphansByType = result.OrphanedResources
                .GroupBy(r => r.OrphanType)
                .ToDictionary(g => g.Key, g => g.Count());

            stopwatch.Stop();
            result.DetectionDurationMs = stopwatch.ElapsedMilliseconds;

            _logger.LogInformation(
                "Orphan detection completed: {TotalOrphans} resources found, estimated cost ${EstimatedCost:F2}/month ({DurationMs}ms)",
                result.TotalOrphans,
                result.TotalEstimatedMonthlyCost,
                result.DetectionDurationMs);

            activity?.SetTag("orphans.total", result.TotalOrphans);
            activity?.SetTag("orphans.cost", result.TotalEstimatedMonthlyCost);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Orphan detection failed");
            result.Error = ex.Message;
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        }

        return result;
    }

    /// <summary>
    /// Detect unattached managed disks (not attached to any VM).
    /// Cost: ~$0.048/GB/month for Standard HDD, ~$0.12/GB/month for Premium SSD
    /// </summary>
    private async Task<List<OrphanedResource>> DetectUnattachedDisksAsync(
        ServiceGroupScope scope,
        CancellationToken cancellationToken)
    {
        var query = BuildScopedQuery(scope, @"
Resources
| where type == 'microsoft.compute/disks'
| where properties.diskState == 'Unattached'
| extend diskSizeGB = toreal(properties.diskSizeGB)
| extend sku = tostring(sku.name)
| extend monthlyCost = case(
    sku contains 'Premium', diskSizeGB * 0.12,
    sku contains 'StandardSSD', diskSizeGB * 0.075,
    diskSizeGB * 0.048
)
| project id, name, resourceGroup, location, subscriptionId, diskSizeGB, sku, monthlyCost, tags");

        var response = await _resourceGraphClient.QueryAsync(query, scope.SubscriptionIds, cancellationToken);

        return MapQueryResults(response.Data, "UnattachedDisk",
            "Managed disk not attached to any virtual machine");
    }

    /// <summary>
    /// Detect unassociated public IP addresses (not associated with any resource).
    /// Cost: ~$3.65/month per IP (Basic) or ~$4.38/month (Standard)
    /// </summary>
    private async Task<List<OrphanedResource>> DetectUnassociatedPublicIpsAsync(
        ServiceGroupScope scope,
        CancellationToken cancellationToken)
    {
        var query = BuildScopedQuery(scope, @"
Resources
| where type == 'microsoft.network/publicipaddresses'
| where properties.ipConfiguration == '' or isnull(properties.ipConfiguration)
| where properties.natGateway == '' or isnull(properties.natGateway)
| extend sku = tostring(sku.name)
| extend monthlyCost = case(sku == 'Standard', 4.38, 3.65)
| project id, name, resourceGroup, location, subscriptionId, sku, monthlyCost, tags");

        var response = await _resourceGraphClient.QueryAsync(query, scope.SubscriptionIds, cancellationToken);

        return MapQueryResults(response.Data, "UnassociatedPublicIP",
            "Public IP address not associated with any resource");
    }

    /// <summary>
    /// Detect unused network interfaces (not attached to any VM).
    /// Cost: ~$4/month per NIC with public IP, ~$0 without
    /// </summary>
    private async Task<List<OrphanedResource>> DetectUnusedNetworkInterfacesAsync(
        ServiceGroupScope scope,
        CancellationToken cancellationToken)
    {
        var query = BuildScopedQuery(scope, @"
Resources
| where type == 'microsoft.network/networkinterfaces'
| where properties.virtualMachine == '' or isnull(properties.virtualMachine)
| where properties.privateEndpoint == '' or isnull(properties.privateEndpoint)
| extend hasPublicIp = isnotnull(properties.ipConfigurations[0].properties.publicIPAddress)
| extend monthlyCost = case(hasPublicIp, 4.0, 0.5)
| project id, name, resourceGroup, location, subscriptionId, hasPublicIp, monthlyCost, tags");

        var response = await _resourceGraphClient.QueryAsync(query, scope.SubscriptionIds, cancellationToken);

        return MapQueryResults(response.Data, "UnusedNetworkInterface",
            "Network interface not attached to any virtual machine or private endpoint");
    }

    /// <summary>
    /// Detect orphaned disk snapshots (disk no longer exists or is detached).
    /// Cost: ~$0.05/GB/month
    /// </summary>
    private async Task<List<OrphanedResource>> DetectOrphanedSnapshotsAsync(
        ServiceGroupScope scope,
        CancellationToken cancellationToken)
    {
        var query = BuildScopedQuery(scope, @"
Resources
| where type == 'microsoft.compute/snapshots'
| where properties.diskState == 'Unattached'
| extend diskSizeGB = toreal(properties.diskSizeGB)
| extend monthlyCost = diskSizeGB * 0.05
| project id, name, resourceGroup, location, subscriptionId, diskSizeGB, monthlyCost, tags");

        var response = await _resourceGraphClient.QueryAsync(query, scope.SubscriptionIds, cancellationToken);

        return MapQueryResults(response.Data, "OrphanedSnapshot",
            "Disk snapshot with no associated active disk");
    }

    /// <summary>
    /// Detect empty App Service Plans (no apps hosted).
    /// Cost: Varies by SKU, ~$54/month for Basic B1, ~$405/month for Standard S1
    /// </summary>
    private async Task<List<OrphanedResource>> DetectEmptyAppServicePlansAsync(
        ServiceGroupScope scope,
        CancellationToken cancellationToken)
    {
        var query = BuildScopedQuery(scope, @"
Resources
| where type == 'microsoft.web/serverfarms'
| where properties.numberOfSites == 0
| extend sku = tostring(sku.name)
| extend monthlyCost = case(
    sku startswith 'P', 405.0,
    sku startswith 'S', 405.0,
    sku startswith 'B', 54.0,
    sku startswith 'F', 0.0,
    100.0
)
| project id, name, resourceGroup, location, subscriptionId, sku, monthlyCost, tags");

        var response = await _resourceGraphClient.QueryAsync(query, scope.SubscriptionIds, cancellationToken);

        return MapQueryResults(response.Data, "EmptyAppServicePlan",
            "App Service Plan with no hosted applications");
    }

    /// <summary>
    /// Detect unused availability sets (no VMs assigned).
    /// Cost: ~$0/month (but indicates abandoned infrastructure)
    /// </summary>
    private async Task<List<OrphanedResource>> DetectUnusedAvailabilitySetsAsync(
        ServiceGroupScope scope,
        CancellationToken cancellationToken)
    {
        var query = BuildScopedQuery(scope, @"
Resources
| where type == 'microsoft.compute/availabilitysets'
| where array_length(properties.virtualMachines) == 0
| extend monthlyCost = 0.0
| project id, name, resourceGroup, location, subscriptionId, monthlyCost, tags");

        var response = await _resourceGraphClient.QueryAsync(query, scope.SubscriptionIds, cancellationToken);

        return MapQueryResults(response.Data, "UnusedAvailabilitySet",
            "Availability set with no assigned virtual machines");
    }

    /// <summary>
    /// Detect orphaned route tables (not associated with any subnet).
    /// Cost: ~$0/month (but indicates configuration drift)
    /// </summary>
    private async Task<List<OrphanedResource>> DetectOrphanedRouteTablesAsync(
        ServiceGroupScope scope,
        CancellationToken cancellationToken)
    {
        var query = BuildScopedQuery(scope, @"
Resources
| where type == 'microsoft.network/routetables'
| where properties.subnets == '' or isnull(properties.subnets) or array_length(properties.subnets) == 0
| extend monthlyCost = 0.0
| project id, name, resourceGroup, location, subscriptionId, monthlyCost, tags");

        var response = await _resourceGraphClient.QueryAsync(query, scope.SubscriptionIds, cancellationToken);

        return MapQueryResults(response.Data, "OrphanedRouteTable",
            "Route table not associated with any subnet");
    }

    /// <summary>
    /// Detect orphaned network security groups (not associated with any subnet or NIC).
    /// Cost: ~$0/month (but indicates configuration drift)
    /// </summary>
    private async Task<List<OrphanedResource>> DetectOrphanedNetworkSecurityGroupsAsync(
        ServiceGroupScope scope,
        CancellationToken cancellationToken)
    {
        var query = BuildScopedQuery(scope, @"
Resources
| where type == 'microsoft.network/networksecuritygroups'
| where (properties.subnets == '' or isnull(properties.subnets) or array_length(properties.subnets) == 0)
    and (properties.networkInterfaces == '' or isnull(properties.networkInterfaces) or array_length(properties.networkInterfaces) == 0)
| extend monthlyCost = 0.0
| project id, name, resourceGroup, location, subscriptionId, monthlyCost, tags");

        var response = await _resourceGraphClient.QueryAsync(query, scope.SubscriptionIds, cancellationToken);

        return MapQueryResults(response.Data, "OrphanedNetworkSecurityGroup",
            "Network security group not associated with any subnet or network interface");
    }

    /// <summary>
    /// Detect unused load balancers (no backend pools or rules configured).
    /// Cost: ~$18.25/month for Basic, ~$25/month for Standard
    /// </summary>
    private async Task<List<OrphanedResource>> DetectUnusedLoadBalancersAsync(
        ServiceGroupScope scope,
        CancellationToken cancellationToken)
    {
        var query = BuildScopedQuery(scope, @"
Resources
| where type == 'microsoft.network/loadbalancers'
| where array_length(properties.backendAddressPools) == 0 or array_length(properties.loadBalancingRules) == 0
| extend sku = tostring(sku.name)
| extend monthlyCost = case(sku == 'Standard', 25.0, 18.25)
| project id, name, resourceGroup, location, subscriptionId, sku, monthlyCost, tags");

        var response = await _resourceGraphClient.QueryAsync(query, scope.SubscriptionIds, cancellationToken);

        return MapQueryResults(response.Data, "UnusedLoadBalancer",
            "Load balancer with no backend pools or load balancing rules");
    }

    /// <summary>
    /// Detect orphaned application gateways (no backend pools or HTTP listeners).
    /// Cost: ~$125-$730/month depending on SKU
    /// </summary>
    private async Task<List<OrphanedResource>> DetectOrphanedApplicationGatewaysAsync(
        ServiceGroupScope scope,
        CancellationToken cancellationToken)
    {
        var query = BuildScopedQuery(scope, @"
Resources
| where type == 'microsoft.network/applicationgateways'
| where array_length(properties.backendAddressPools) == 0 or array_length(properties.httpListeners) == 0
| extend sku = tostring(sku.name)
| extend monthlyCost = case(
    sku startswith 'WAF', 730.0,
    sku startswith 'Standard_v2', 328.0,
    125.0
)
| project id, name, resourceGroup, location, subscriptionId, sku, monthlyCost, tags");

        var response = await _resourceGraphClient.QueryAsync(query, scope.SubscriptionIds, cancellationToken);

        return MapQueryResults(response.Data, "OrphanedApplicationGateway",
            "Application gateway with no backend pools or HTTP listeners");
    }

    /// <summary>    /// Detect unused diagnostic settings (sending logs to Log Analytics but never queried).
    /// Cost: ~$2.30/GB ingested + ~$0.10/GB/month retention
    /// HIGH WASTE POTENTIAL: Often 20-40% of Log Analytics costs are never-queried logs
    /// </summary>
    private async Task<List<OrphanedResource>> DetectUnusedDiagnosticSettingsAsync(
        ServiceGroupScope scope,
        CancellationToken cancellationToken)
    {
        // Step 1: Find all Log Analytics workspaces in scope
        var workspacesQuery = BuildScopedQuery(scope, @"
Resources
| where type == 'microsoft.operationalinsights/workspaces'
| project workspaceId = id, workspaceName = name, resourceGroup, location, subscriptionId, tags");

        var workspacesResponse = await _resourceGraphClient.QueryAsync(workspacesQuery, scope.SubscriptionIds, cancellationToken);
        var workspaces = workspacesResponse.Data;

        if (!workspaces.Any())
        {
            _logger.LogInformation("No Log Analytics workspaces found in scope");
            return new List<OrphanedResource>();
        }

        // Step 2: Find all diagnostic settings pointing to these workspaces
        var diagnosticSettingsQuery = BuildScopedQuery(scope, @"
Resources
| where type == 'microsoft.insights/diagnosticsettings'
| extend workspaceId = tostring(properties.workspaceId)
| where isnotempty(workspaceId)
| extend targetResourceId = tostring(properties.resourceId)
| project id, name, resourceGroup, subscriptionId, workspaceId, targetResourceId, tags");

        var diagnosticResponse = await _resourceGraphClient.QueryAsync(diagnosticSettingsQuery, scope.SubscriptionIds, cancellationToken);

        var orphanedResources = new List<OrphanedResource>();

        // Step 3: For each diagnostic setting, check if the workspace still exists and estimate cost
        // Note: Full table usage analysis requires querying Log Analytics API directly
        // This basic detection identifies diagnostic settings for resources that:
        // - Send to workspaces that no longer exist
        // - Are enabled but resource is deallocated/stopped
        // - Could be candidates for usage analysis

        foreach (var diagnostic in diagnosticResponse.Data)
        {
            var targetWorkspaceId = diagnostic.GetString("workspaceId");
            var targetResourceId = diagnostic.GetString("targetResourceId");

            // Check if workspace still exists
            var workspaceExists = workspaces.Any(ws => ws.GetString("workspaceId") == targetWorkspaceId);

            if (!workspaceExists)
            {
                orphanedResources.Add(new OrphanedResource
                {
                    ResourceId = diagnostic.GetString("id") ?? string.Empty,
                    ResourceName = diagnostic.GetString("name") ?? string.Empty,
                    ResourceGroup = diagnostic.GetString("resourceGroup") ?? string.Empty,
                    Location = "global",
                    SubscriptionId = diagnostic.GetString("subscriptionId") ?? string.Empty,
                    OrphanType = "UnusedDiagnosticSetting",
                    Description = $"Diagnostic setting sending logs to deleted/non-existent workspace (target: {targetResourceId})",
                    EstimatedMonthlyCost = 50m,
                    DetectedAt = DateTimeOffset.UtcNow,
                    Tags = diagnostic.GetString("tags") ?? "{}"
                });
            }
        }

        // Step 4: Query for stopped/deallocated VMs with diagnostic settings still active
        var stoppedVmsQuery = BuildScopedQuery(scope, @"
Resources
| where type == 'microsoft.compute/virtualmachines'
| where properties.extended.instanceView.powerState.code == 'PowerState/deallocated'
| project vmId = id, vmName = name, resourceGroup, subscriptionId
| join kind=inner (
    Resources
    | where type == 'microsoft.insights/diagnosticsettings'
    | extend targetResourceId = tostring(properties.resourceId)
    | project diagnosticId = id, diagnosticName = name, targetResourceId, diagnosticRg = resourceGroup
) on $left.vmId == $right.targetResourceId
| project diagnosticId, diagnosticName, diagnosticRg, vmName, resourceGroup, subscriptionId");

        try
        {
            var stoppedVmsResponse = await _resourceGraphClient.QueryAsync(stoppedVmsQuery, scope.SubscriptionIds, cancellationToken);

            foreach (var row in stoppedVmsResponse.Data)
            {
                orphanedResources.Add(new OrphanedResource
                {
                    ResourceId = row.GetString("diagnosticId") ?? string.Empty,
                    ResourceName = row.GetString("diagnosticName") ?? string.Empty,
                    ResourceGroup = row.GetString("diagnosticRg") ?? string.Empty,
                    Location = "global",
                    SubscriptionId = row.GetString("subscriptionId") ?? string.Empty,
                    OrphanType = "UnusedDiagnosticSetting",
                    Description = $"Diagnostic setting active for deallocated VM '{row.GetString("vmName")}' — no logs being generated",
                    EstimatedMonthlyCost = 0m,
                    DetectedAt = DateTimeOffset.UtcNow,
                    Tags = "{}"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query stopped VMs with diagnostic settings (non-critical)");
        }

        _logger.LogInformation(
            "Found {Count} unused diagnostic settings (deleted workspaces + deallocated resources)",
            orphanedResources.Count);

        return orphanedResources;
    }

    /// <summary>
    /// Analyze Log Analytics table usage to identify never-queried or rarely-queried tables.
    /// This method requires direct Log Analytics API access (not Resource Graph).
    /// Returns recommendations for reducing log verbosity or disabling unused log categories.
    /// </summary>
    /// <remarks>
    /// COST BREAKDOWN:
    /// - Data Ingestion: ~$2.30/GB (first 5 GB/month free per workspace)
    /// - Data Retention: ~$0.10/GB/month beyond 31 days
    /// - Archive Storage: ~$0.026/GB/month for 4+ years
    /// 
    /// COMMON WASTE PATTERNS:
    /// - AzureActivity logs retained 730 days (only need 90)
    /// - AppServiceHTTPLogs at verbose level (only need errors)
    /// - AzureDiagnostics from non-prod resources (should be Basic tier)
    /// - Tables never queried in 90+ days
    /// - Resources sending duplicate logs to multiple workspaces
    /// 
    /// DETECTION STRATEGY (requires Log Analytics API):
    /// 1. Query workspace usage stats: `Usage | where TimeGenerated > ago(90d) | summarize GB=sum(Quantity)/1000 by DataType`
    /// 2. Query table access logs: `LAQueryLogs | summarize LastQuery=max(TimeGenerated) by TableName`
    /// 3. Calculate cost per table: GB * $2.30 + (RetentionDays - 31) * GB * $0.10
    /// 4. Flag tables with: LastQuery > 90 days ago OR LastQuery == null
    /// 5. Estimate savings: (UnusedGB * $2.30) + (ExcessRetentionGB * $0.10)
    /// </remarks>
    public async Task<LogAnalyticsWasteReport> AnalyzeLogAnalyticsWasteAsync(
        string workspaceId,
        int unusedThresholdDays = 90,
        CancellationToken cancellationToken = default)
    {
        if (_logAnalyticsAnalyzer is null)
        {
            _logger.LogWarning(
                "LogAnalyticsWasteAnalyzer not configured — workspace {WorkspaceId} skipped. " +
                "Register LogAnalyticsWasteAnalyzer in DI with a TokenCredential to enable this analysis.",
                workspaceId);

            return new LogAnalyticsWasteReport
            {
                WorkspaceId = workspaceId,
                AnalyzedAt = DateTimeOffset.UtcNow,
                Message = "LogAnalyticsWasteAnalyzer not registered in DI — configure TokenCredential to enable"
            };
        }

        return await _logAnalyticsAnalyzer.AnalyzeWorkspaceAsync(
            workspaceId, unusedThresholdDays, cancellationToken);
    }

    /// <summary>    /// Build a scoped KQL query by applying resource group and tag filters.
    /// </summary>
    private string BuildScopedQuery(ServiceGroupScope scope, string baseQuery)
    {
        var filters = new List<string>();

        if (scope.ResourceGroups?.Any() == true)
        {
            // Escape single quotes to prevent KQL injection (consistent with AzureResourceGraphClient)
            var rgFilter = string.Join(" or ", scope.ResourceGroups.Select(rg => $"resourceGroup =~ '{rg.Replace("'", "''")}'"));
            filters.Add($"({rgFilter})");
        }

        if (scope.TagFilters?.Any() == true)
        {
            foreach (var (key, value) in scope.TagFilters)
            {
                // Escape single quotes in both key and value to prevent KQL injection
                var safeKey = key.Replace("'", "''");
                var safeValue = value.Replace("'", "''");
                filters.Add($"tags['{safeKey}'] =~ '{safeValue}'");
            }
        }

        if (filters.Any())
        {
            // Insert WHERE clause after the first line (Resources | where type == '...')
            var lines = baseQuery.Split('\n');
            var firstLine = lines[0];
            var additionalFilters = " and " + string.Join(" and ", filters);
            lines[0] = firstLine + additionalFilters;
            return string.Join('\n', lines);
        }

        return baseQuery;
    }

    /// <summary>
    /// Map Resource Graph query results to OrphanedResource objects.
    /// </summary>
    private List<OrphanedResource> MapQueryResults(
        IReadOnlyList<ResourceGraphRow> data,
        string orphanType,
        string description)
    {
        var resources = new List<OrphanedResource>(data.Count);

        try
        {
            foreach (var row in data)
            {
                resources.Add(new OrphanedResource
                {
                    ResourceId = row.GetString("id") ?? string.Empty,
                    ResourceName = row.GetString("name") ?? string.Empty,
                    ResourceGroup = row.GetString("resourceGroup") ?? string.Empty,
                    Location = row.GetString("location") ?? string.Empty,
                    SubscriptionId = row.GetString("subscriptionId") ?? string.Empty,
                    OrphanType = orphanType,
                    Description = description,
                    EstimatedMonthlyCost = row.GetDecimal("monthlyCost"),
                    DetectedAt = DateTimeOffset.UtcNow,
                    Tags = row.GetString("tags") ?? "{}"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to map query results for orphan type: {OrphanType}", orphanType);
        }

        return resources;
    }
}

// DTOs

public class OrphanDetectionResult
{
    public ServiceGroupScope Scope { get; set; } = new();
    public List<OrphanedResource> OrphanedResources { get; set; } = new();
    public int TotalOrphans { get; set; }
    public decimal TotalEstimatedMonthlyCost { get; set; }
    public Dictionary<string, int> OrphansByType { get; set; } = new();
    public long DetectionDurationMs { get; set; }
    public string? Error { get; set; }
}

public class OrphanedResource
{
    public required string ResourceId { get; set; }
    public required string ResourceName { get; set; }
    public required string ResourceGroup { get; set; }
    public required string Location { get; set; }
    public required string SubscriptionId { get; set; }
    public required string OrphanType { get; set; }
    public required string Description { get; set; }
    public decimal EstimatedMonthlyCost { get; set; }
    public DateTimeOffset DetectedAt { get; set; }
    public string Tags { get; set; } = "{}";

    /// <summary>
    /// Generate Azure CLI command to delete this resource.
    /// </summary>
    public string GetDeletionCommand()
    {
        return $"az resource delete --ids \"{ResourceId}\"";
    }

    /// <summary>
    /// Generate PowerShell command to delete this resource.
    /// </summary>
    public string GetPowerShellDeletionCommand()
    {
        return $"Remove-AzResource -ResourceId \"{ResourceId}\" -Force";
    }
}

public class ServiceGroupScope
{
    public List<string> SubscriptionIds { get; set; } = new();
    public List<string>? ResourceGroups { get; set; }
    public Dictionary<string, string>? TagFilters { get; set; }
}

/// <summary>
/// Report on Log Analytics workspace waste (unused tables, excessive retention, etc.)
/// </summary>
public class LogAnalyticsWasteReport
{
    public required string WorkspaceId { get; set; }
    public DateTimeOffset AnalyzedAt { get; set; }
    public List<UnusedLogTable> UnusedTables { get; set; } = new();
    public List<ExcessiveRetentionTable> ExcessiveRetentionTables { get; set; } = new();
    public decimal TotalMonthlyWaste { get; set; }
    public decimal UnusedIngestionCost { get; set; }
    public decimal ExcessRetentionCost { get; set; }
    public string? Message { get; set; }
}

public class UnusedLogTable
{
    public required string TableName { get; set; }
    public decimal MonthlyIngestionGB { get; set; }
    public DateTimeOffset? LastQueried { get; set; }
    public int DaysSinceLastQuery { get; set; }
    public decimal EstimatedMonthlyCost { get; set; }
    public string RecommendedAction { get; set; } = string.Empty;
}

public class ExcessiveRetentionTable
{
    public required string TableName { get; set; }
    public int CurrentRetentionDays { get; set; }
    public int RecommendedRetentionDays { get; set; }
    public decimal TotalStorageGB { get; set; }
    public decimal MonthlySavings { get; set; }
    public string Justification { get; set; } = string.Empty;
}
