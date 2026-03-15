using Atlas.AgentOrchestrator.Agents;
using Atlas.AgentOrchestrator.Integrations.MCP;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.AgentOrchestrator.Tests.Unit.Integrations.MCP;

/// <summary>
/// Unit tests for <see cref="AzureMcpToolClient"/> option validation and graceful
/// fallback behaviour — exercisable without a live Azure MCP server.
/// </summary>
public class AzureMcpToolClientTests
{
    // ─────────────────────────────────────────────────────────────
    // AzureMcpOptions defaults
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void AzureMcpOptions_DefaultValues_AreSet()
    {
        var options = new AzureMcpOptions();

        Assert.NotNull(options.ServerUrl);
        Assert.True(string.IsNullOrWhiteSpace(options.ServerUrl),
            "ServerUrl should default to empty so stdio mode is used by default");
        Assert.True(options.Enabled,
            "Enabled should default to true so Azure MCP capabilities are available by default");
    }

    [Fact]
    public void AzureMcpOptions_SectionName_IsNonEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(AzureMcpOptions.SectionName),
            "SectionName must be non-empty so config binding works");
    }

    // ─────────────────────────────────────────────────────────────
    // FinOpsOptimizerAgent — MCP not configured
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task FinOpsOptimizerAgent_WithNullMcpClient_SkipsMcpEnrichment()
    {
        // Arrange: agent without MCP client
        var agent = new FinOpsOptimizerAgent(
            NullLogger<FinOpsOptimizerAgent>.Instance,
            foundryClient: null,
            orphanDetectionService: null,
            mcpToolClient: null);

        var context = new FinOpsContext
        {
            ServiceGroupId = Guid.NewGuid(),
            CurrentMonthlyCost = 500m,
            SubscriptionId = "test-sub-id"
        };

        // Act
        await agent.AnalyzeAsync(context);

        // Assert: no MCP enrichment — McpCostQueryResult stays null
        Assert.Null(context.McpCostQueryResult);
    }

    [Fact]
    public async Task FinOpsOptimizerAgent_WithNullSubscriptionId_SkipsMcpEnrichment()
    {
        var agent = new FinOpsOptimizerAgent(
            NullLogger<FinOpsOptimizerAgent>.Instance,
            foundryClient: null,
            orphanDetectionService: null,
            mcpToolClient: null);

        var context = new FinOpsContext
        {
            ServiceGroupId = Guid.NewGuid(),
            CurrentMonthlyCost = 500m,
            SubscriptionId = null  // no subscription → MCP skipped
        };

        await agent.AnalyzeAsync(context);

        Assert.Null(context.McpCostQueryResult);
    }
}
