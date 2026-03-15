namespace Atlas.AgentOrchestrator.Orchestration;

/// <summary>
/// Adapts the existing AzureCostManagementClient (single-subscription) to the multi-subscription
/// IAzureCostManagementClient interface required by DiscoveryWorkflow.
/// </summary>
internal sealed class AzureCostManagementClientAdapter : IAzureCostManagementClient
{
    private readonly AzureCostManagementClient _inner;

    public AzureCostManagementClientAdapter(AzureCostManagementClient inner)
    {
        _inner = inner;
    }

    public async Task<List<CostRecord>> GetCostsAsync(
        List<string> subscriptions,
        CancellationToken cancellationToken)
    {
        var records = new List<CostRecord>();
        foreach (var subscriptionId in subscriptions)
        {
            var cost = await _inner.GetMonthToDateCostAsync(subscriptionId, cancellationToken);
            if (cost > 0)
            {
                records.Add(new CostRecord
                {
                    ResourceGroup = subscriptionId,
                    ResourceType = "subscription",
                    Cost = cost
                });
            }
        }
        return records;
    }
}

/// <summary>
/// No-op IAzureMonitorClient used until a full Monitor integration is built.
/// Returns false for all resources so DiscoveryWorkflow proceeds without Monitor data.
/// </summary>
internal sealed class NoOpAzureMonitorClient : IAzureMonitorClient
{
    public Task<bool> HasMetricsAsync(string resourceId, CancellationToken cancellationToken)
        => Task.FromResult(false);
}
