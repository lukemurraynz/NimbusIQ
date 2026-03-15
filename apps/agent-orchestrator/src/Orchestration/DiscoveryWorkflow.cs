using Atlas.AgentOrchestrator.Integrations.Azure;
using Atlas.AgentOrchestrator.Integrations.MCP;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace Atlas.AgentOrchestrator.Orchestration;

/// <summary>
/// T023: Discovery orchestration - Resource Graph, Monitor, Cost ingestion + dependency mapping
/// </summary>
public class DiscoveryWorkflow
{
    private readonly ILogger<DiscoveryWorkflow> _logger;
    private readonly IResourceGraphClient _resourceGraphClient;
    private readonly IAzureMonitorClient _monitorClient;
    private readonly IAzureCostManagementClient _costClient;
    private readonly AzureMcpToolClient? _azureMcpToolClient;

    public DiscoveryWorkflow(
        ILogger<DiscoveryWorkflow> logger,
        IResourceGraphClient resourceGraphClient,
        IAzureMonitorClient monitorClient,
        IAzureCostManagementClient costClient,
        AzureMcpToolClient? azureMcpToolClient = null)
    {
        _logger = logger;
        _resourceGraphClient = resourceGraphClient;
        _monitorClient = monitorClient;
        _costClient = costClient;
        _azureMcpToolClient = azureMcpToolClient;
    }

    /// <summary>
    /// Execute discovery for a service group scope
    /// </summary>
    public async Task<DiscoveryResult> ExecuteAsync(
        Guid analysisRunId,
        ServiceGroupScope scope,
        CancellationToken cancellationToken)
    {
        var activity = Activity.Current;
        activity?.SetTag("analysisRunId", analysisRunId);
        activity?.SetTag("serviceGroupId", scope.ServiceGroupId);

        _logger.LogInformation(
            "Starting discovery for service group {ServiceGroupId}, analysis run {AnalysisRunId}",
            scope.ServiceGroupId,
            analysisRunId);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // T023.1: Resource Graph query (inventory)
            var resources = await DiscoverResourcesAsync(scope, cancellationToken);
            _logger.LogInformation("Discovered {ResourceCount} resources", resources.Count);

            // T023.2: Build dependency map
            var dependencyMap = await BuildDependencyMapAsync(resources, cancellationToken);
            _logger.LogInformation("Mapped {DependencyCount} dependencies", dependencyMap.Count);

            // T023.3: Fetch telemetry (SLO context)
            var telemetryContext = await FetchTelemetryContextAsync(resources, cancellationToken);

            // T023.4: Fetch cost data
            var costContext = await FetchCostContextAsync(scope, cancellationToken);

            // T023.5: Detect anomalies
            var anomalies = DetectAnomalies(resources, telemetryContext);

            // Optional MCP enrichment: expose available Azure MCP capabilities used
            // during discovery for traceability and future-state planning.
            var mcpContext = await BuildMcpDiscoveryContextAsync(scope, cancellationToken);

            stopwatch.Stop();

            var result = new DiscoveryResult
            {
                AnalysisRunId = analysisRunId,
                ServiceGroupId = scope.ServiceGroupId,
                Resources = resources,
                DependencyMap = dependencyMap,
                TelemetryContext = telemetryContext,
                CostContext = costContext,
                Anomalies = anomalies,
                McpDiscoveryContext = mcpContext,
                DiscoveryDurationMs = stopwatch.ElapsedMilliseconds,
                CapturedAt = DateTime.UtcNow
            };

            _logger.LogInformation(
                "Discovery completed in {DurationMs}ms: {ResourceCount} resources, {DependencyCount} dependencies, {AnomalyCount} anomalies",
                result.DiscoveryDurationMs,
                result.Resources.Count,
                result.DependencyMap.Count,
                result.Anomalies.Count);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discovery failed for analysis run {AnalysisRunId}", analysisRunId);
            throw;
        }
    }

    private async Task<List<DiscoveredResource>> DiscoverResourcesAsync(
        ServiceGroupScope scope,
        CancellationToken cancellationToken)
    {
        // T023.1: Query Azure Resource Graph
        var query = BuildResourceGraphQuery(scope);
        var result = await _resourceGraphClient.QueryAsync(query, scope.SubscriptionIds, cancellationToken);

        return result.Data.Select(MapToDiscoveredResource).ToList();
    }

    private string BuildResourceGraphQuery(ServiceGroupScope scope)
    {
        // Base query for all resources
        var query = "Resources";

        // Apply scope filters
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
            query += " | where " + string.Join(" and ", filters);
        }

        // Project relevant fields
        query += @"
            | project
                id,
                name,
                type,
                resourceGroup,
                subscriptionId,
                location,
                tags,
                sku,
                kind,
                properties,
                identity";

        return query;
    }

    private async Task<List<ResourceDependency>> BuildDependencyMapAsync(
        List<DiscoveredResource> resources,
        CancellationToken cancellationToken)
    {
        // T023.2: Build dependency graph from resource properties
        var dependencies = new List<ResourceDependency>();

        foreach (var resource in resources)
        {
            // Extract dependencies from resource properties
            var deps = await ExtractDependenciesAsync(resource, resources, cancellationToken);
            dependencies.AddRange(deps);
        }

        return dependencies;
    }

    private async Task<List<ResourceDependency>> ExtractDependenciesAsync(
        DiscoveredResource resource,
        List<DiscoveredResource> allResources,
        CancellationToken cancellationToken)
    {
        var dependencies = new List<ResourceDependency>();

        // Parse resource properties for references to other resources
        // Common patterns:
        // - "resourceId": "/subscriptions/.../resourceGroups/..."
        // - "subnet": "/subscriptions/.../virtualNetworks/.../subnets/..."
        // - "storageAccountId", "keyVaultId", etc.

        var propertyJson = resource.Properties?.ToString() ?? "{}";

        // Extract resource ID references (simplified - production would use proper JSON parsing)
        var resourceIdPattern = @"/subscriptions/[a-f0-9-]+/resourceGroups/[^/]+/providers/[^""]+";
        var matches = System.Text.RegularExpressions.Regex.Matches(propertyJson, resourceIdPattern);

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var targetId = match.Value;
            var targetResource = allResources.FirstOrDefault(r => r.Id == targetId);

            if (targetResource != null)
            {
                dependencies.Add(new ResourceDependency
                {
                    SourceResourceId = resource.Id,
                    TargetResourceId = targetId,
                    DependencyType = InferDependencyType(resource.Type, targetResource.Type)
                });
            }
        }

        return await Task.FromResult(dependencies);
    }

    private string InferDependencyType(string sourceType, string targetType)
    {
        // Infer dependency type based on resource types
        return (sourceType, targetType) switch
        {
            (_, "Microsoft.Network/virtualNetworks") => "network",
            (_, "Microsoft.Storage/storageAccounts") => "storage",
            (_, "Microsoft.KeyVault/vaults") => "secrets",
            (_, "Microsoft.Sql/servers") => "database",
            (_, "Microsoft.DBforPostgreSQL/flexibleServers") => "database",
            (_, "Microsoft.Insights/components") => "monitoring",
            _ => "reference"
        };
    }

    private async Task<TelemetryContext> FetchTelemetryContextAsync(
        List<DiscoveredResource> resources,
        CancellationToken cancellationToken)
    {
        // T023.3: Fetch metrics and logs for SLO context
        var metricsAvailable = new List<string>();
        var logsAvailable = new List<string>();
        var missingTelemetry = new List<string>();

        foreach (var resource in resources)
        {
            try
            {
                // Check if metrics are available
                var hasMetrics = await _monitorClient.HasMetricsAsync(resource.Id, cancellationToken);
                if (hasMetrics)
                {
                    metricsAvailable.Add(resource.Id);
                }
                else
                {
                    missingTelemetry.Add(resource.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check telemetry for resource {ResourceId}", resource.Id);
                missingTelemetry.Add(resource.Id);
            }
        }

        return new TelemetryContext
        {
            MetricsAvailable = metricsAvailable,
            LogsAvailable = logsAvailable,
            MissingTelemetry = missingTelemetry,
            ConfidenceImpact = CalculateConfidenceImpact(missingTelemetry.Count, resources.Count)
        };
    }

    private decimal CalculateConfidenceImpact(int missingCount, int totalCount)
    {
        if (totalCount == 0) return 1.0m;

        var coverage = (decimal)(totalCount - missingCount) / totalCount;
        return coverage; // 1.0 = full coverage, 0.0 = no coverage
    }

    private async Task<CostContext> FetchCostContextAsync(
        ServiceGroupScope scope,
        CancellationToken cancellationToken)
    {
        // T023.4: Fetch cost data for the service group
        try
        {
            var costs = await _costClient.GetCostsAsync(scope.SubscriptionIds, cancellationToken);

            return new CostContext
            {
                TotalMonthlyCost = costs.Sum(c => c.Cost),
                CostByResourceGroup = costs.GroupBy(c => c.ResourceGroup)
                    .ToDictionary(g => g.Key, g => g.Sum(c => c.Cost)),
                CostByResourceType = costs.GroupBy(c => c.ResourceType)
                    .ToDictionary(g => g.Key, g => g.Sum(c => c.Cost))
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch cost data, continuing without cost context");

            // Optional MCP fallback for environments where direct cost client access
            // is constrained but Azure MCP is available.
            if (_azureMcpToolClient is not null && scope.SubscriptionIds.Count > 0)
            {
                try
                {
                    var endDate = DateTime.UtcNow;
                    var startDate = endDate.AddDays(-30);
                    var mcpResult = await _azureMcpToolClient.QueryCostAsync(
                        scope.SubscriptionIds[0],
                        startDate,
                        endDate,
                        cancellationToken);

                    var mcpMonthlyCost = TryExtractTotalCostFromMcpResult(mcpResult);
                    if (mcpMonthlyCost.HasValue)
                    {
                        return new CostContext
                        {
                            TotalMonthlyCost = mcpMonthlyCost.Value,
                            CostByResourceGroup = new Dictionary<string, decimal>(),
                            CostByResourceType = new Dictionary<string, decimal>(),
                            Source = "azure-mcp"
                        };
                    }
                }
                catch (Exception mcpEx)
                {
                    _logger.LogWarning(mcpEx, "Azure MCP cost fallback failed");
                }
            }

            return new CostContext
            {
                TotalMonthlyCost = 0,
                CostByResourceGroup = new Dictionary<string, decimal>(),
                CostByResourceType = new Dictionary<string, decimal>(),
                Source = "none"
            };
        }
    }

    private async Task<McpDiscoveryContext?> BuildMcpDiscoveryContextAsync(
        ServiceGroupScope scope,
        CancellationToken cancellationToken)
    {
        if (_azureMcpToolClient is null)
        {
            return null;
        }

        try
        {
            var tools = await _azureMcpToolClient.ListToolsAsync(cancellationToken);
            return new McpDiscoveryContext
            {
                Enabled = true,
                ToolCount = tools.Count,
                ToolNames = tools
                    .Select(static tool => tool.Name)
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .Take(25)
                    .ToList(),
                SubscriptionScope = scope.SubscriptionIds
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure MCP discovery enrichment failed");
            return new McpDiscoveryContext
            {
                Enabled = false,
                Error = ex.Message,
                SubscriptionScope = scope.SubscriptionIds
            };
        }
    }

    private static decimal? TryExtractTotalCostFromMcpResult(Dictionary<string, object> mcpResult)
    {
        if (!mcpResult.TryGetValue("text", out var rawText) || rawText is null)
        {
            return null;
        }

        var text = rawText.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("totalCost", out var totalCostElement) &&
                totalCostElement.TryGetDecimal(out var totalCost))
            {
                return totalCost;
            }
        }
        catch
        {
            // Ignore parse errors and continue gracefully.
        }

        return null;
    }

    private List<ResourceAnomaly> DetectAnomalies(
        List<DiscoveredResource> resources,
        TelemetryContext telemetryContext)
    {
        // T023.5: Detect configuration anomalies
        var anomalies = new List<ResourceAnomaly>();

        foreach (var resource in resources)
        {
            // Check for common issues
            if (telemetryContext.MissingTelemetry.Contains(resource.Id))
            {
                anomalies.Add(new ResourceAnomaly
                {
                    ResourceId = resource.Id,
                    AnomalyType = "missing-telemetry",
                    Severity = "warning",
                    Description = "Resource has no telemetry data available"
                });
            }

            // Check for public network access
            if (IsPubliclyAccessible(resource))
            {
                anomalies.Add(new ResourceAnomaly
                {
                    ResourceId = resource.Id,
                    AnomalyType = "public-access",
                    Severity = "info",
                    Description = "Resource has public network access enabled"
                });
            }
        }

        return anomalies;
    }

    private bool IsPubliclyAccessible(DiscoveredResource resource)
    {
        // Check resource properties for public access indicators
        var props = resource.Properties?.ToString() ?? "{}";

        return props.Contains("\"publicNetworkAccess\":\"Enabled\"", StringComparison.OrdinalIgnoreCase) ||
               props.Contains("\"allowPublicAccess\":true", StringComparison.OrdinalIgnoreCase);
    }

    private static DiscoveredResource MapToDiscoveredResource(ResourceGraphRow row)
    {
        return new DiscoveredResource
        {
            Id = row.GetString("id") ?? string.Empty,
            Name = row.GetString("name") ?? string.Empty,
            Type = row.GetString("type") ?? string.Empty,
            ResourceGroup = row.GetString("resourceGroup") ?? string.Empty,
            SubscriptionId = row.GetString("subscriptionId") ?? string.Empty,
            Location = row.GetString("location") ?? string.Empty,
            Tags = row.GetString("tags"),
            Sku = row.GetString("sku"),
            Kind = row.GetString("kind"),
            Properties = row.GetRawJson()
        };
    }
}

// Supporting types
public record ServiceGroupScope(
    Guid ServiceGroupId,
    List<string> SubscriptionIds,
    List<string>? ResourceGroups,
    Dictionary<string, string>? TagFilters);

public class DiscoveryResult
{
    public Guid AnalysisRunId { get; set; }
    public Guid ServiceGroupId { get; set; }
    public List<DiscoveredResource> Resources { get; set; } = new();
    public List<ResourceDependency> DependencyMap { get; set; } = new();
    public TelemetryContext TelemetryContext { get; set; } = new();
    public CostContext CostContext { get; set; } = new();
    public List<ResourceAnomaly> Anomalies { get; set; } = new();
    public McpDiscoveryContext? McpDiscoveryContext { get; set; }
    public long DiscoveryDurationMs { get; set; }
    public DateTime CapturedAt { get; set; }
}

public class DiscoveredResource
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string ResourceGroup { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public object? Tags { get; set; }
    public object? Sku { get; set; }
    public object? Kind { get; set; }
    public object? Properties { get; set; }
}

public class ResourceDependency
{
    public string SourceResourceId { get; set; } = string.Empty;
    public string TargetResourceId { get; set; } = string.Empty;
    public string DependencyType { get; set; } = string.Empty;
}

public class TelemetryContext
{
    public List<string> MetricsAvailable { get; set; } = new();
    public List<string> LogsAvailable { get; set; } = new();
    public List<string> MissingTelemetry { get; set; } = new();
    public decimal ConfidenceImpact { get; set; }
}

public class CostContext
{
    public decimal TotalMonthlyCost { get; set; }
    public Dictionary<string, decimal> CostByResourceGroup { get; set; } = new();
    public Dictionary<string, decimal> CostByResourceType { get; set; } = new();
    public string Source { get; set; } = "cost-management-api";
}

public class McpDiscoveryContext
{
    public bool Enabled { get; set; }
    public int ToolCount { get; set; }
    public List<string> ToolNames { get; set; } = new();
    public List<string> SubscriptionScope { get; set; } = new();
    public string? Error { get; set; }
}

public class ResourceAnomaly
{
    public string ResourceId { get; set; } = string.Empty;
    public string AnomalyType { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

// Azure client interfaces (to be implemented in Infrastructure layer)
public interface IAzureMonitorClient
{
    Task<bool> HasMetricsAsync(string resourceId, CancellationToken cancellationToken);
}

public interface IAzureCostManagementClient
{
    Task<List<CostRecord>> GetCostsAsync(List<string> subscriptions, CancellationToken cancellationToken);
}

public class CostRecord
{
    public string ResourceGroup { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public decimal Cost { get; set; }
}
