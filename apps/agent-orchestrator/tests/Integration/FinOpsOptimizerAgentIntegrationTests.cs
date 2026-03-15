using Atlas.AgentOrchestrator.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.AgentOrchestrator.Tests.Integration;

/// <summary>
/// Integration-level tests for <see cref="FinOpsOptimizerAgent"/> covering MCP
/// context enrichment logic and the broader analysis pipeline.
/// These tests use the agent with no AI/MCP dependencies so they run without
/// any external infrastructure.
/// </summary>
public class FinOpsOptimizerAgentIntegrationTests
{
    private static FinOpsOptimizerAgent CreateAgent() =>
        new(NullLogger<FinOpsOptimizerAgent>.Instance,
            foundryClient: null,
            orphanDetectionService: null,
            mcpToolClient: null);

    // ─────────────────────────────────────────────────────────────
    // Context enrichment — no MCP client configured
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeAsync_WithNullMcpClient_CompletesWithoutException()
    {
        var agent = CreateAgent();
        var context = new FinOpsContext
        {
            ServiceGroupId = Guid.NewGuid(),
            CurrentMonthlyCost = 200m,
            SubscriptionId = null // MCP skipped when null
        };

        var result = await agent.AnalyzeAsync(context);

        // Should produce a result even without MCP or AI Foundry
        Assert.NotNull(result);
    }

    [Fact]
    public async Task AnalyzeAsync_WithSubscriptionIdAndNullMcpClient_DoesNotChangeCurrentCost()
    {
        // When no MCP client is configured the context cost stays unchanged
        var agent = CreateAgent();
        const decimal originalCost = 300m;
        var context = new FinOpsContext
        {
            ServiceGroupId = Guid.NewGuid(),
            CurrentMonthlyCost = originalCost,
            SubscriptionId = "sub-001"
        };

        await agent.AnalyzeAsync(context);

        Assert.Equal(originalCost, context.CurrentMonthlyCost);
    }

    // ─────────────────────────────────────────────────────────────
    // MCP result property initialised only via enrichment
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeAsync_WithNullMcpClient_McpCostQueryResultRemainsNull()
    {
        var agent = CreateAgent();
        var context = new FinOpsContext
        {
            ServiceGroupId = Guid.NewGuid(),
            CurrentMonthlyCost = 100m,
            SubscriptionId = "sub-no-mcp"
        };

        await agent.AnalyzeAsync(context);

        Assert.Null(context.McpCostQueryResult);
    }

    // ─────────────────────────────────────────────────────────────
    // Anomaly detection with various cost profiles
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeAsync_WithHighCostResources_DetectsOptimisationOpportunities()
    {
        var agent = CreateAgent();
        var context = new FinOpsContext
        {
            ServiceGroupId = Guid.NewGuid(),
            CurrentMonthlyCost = 10_000m,
            Resources =
            [
                new FinOpsResourceInfo
                {
                    ResourceId = "/subscriptions/s1/resourceGroups/rg1/providers/Microsoft.Compute/virtualMachines/vm-oversized",
                    ResourceType = "Microsoft.Compute/virtualMachines",
                    Sku = "Standard_D64s_v3",
                    MonthlyCost = 8_000m,
                    CpuUtilization = 3m,    // Massively under-utilised
                    MemoryUtilization = 5m,
                }
            ]
        };

        var result = await agent.AnalyzeAsync(context);

        Assert.NotNull(result);
        // An oversized VM with <5 % utilisation should produce at least one rightsizing opportunity
        Assert.NotEmpty(result.RightsizingOpportunities);
    }

    [Fact]
    public async Task AnalyzeAsync_WithZeroCostContext_ReturnsResultWithoutExploding()
    {
        var agent = CreateAgent();
        var context = new FinOpsContext
        {
            ServiceGroupId = Guid.NewGuid(),
            CurrentMonthlyCost = 0m,
        };

        var result = await agent.AnalyzeAsync(context);

        Assert.NotNull(result);
        Assert.True(result.PotentialMonthlySavings >= 0m);
    }
}
