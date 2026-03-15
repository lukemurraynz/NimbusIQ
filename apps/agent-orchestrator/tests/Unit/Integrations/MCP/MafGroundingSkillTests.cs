using Atlas.AgentOrchestrator.Integrations.MCP;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Atlas.AgentOrchestrator.Tests.Unit.Integrations.MCP;

public class MafGroundingSkillTests
{
    [Fact]
    public async Task BuildGroundingContextAsync_NoMcpClients_ReturnsWarningsAndDefaults()
    {
        var skill = new MafGroundingSkill(NullLogger<MafGroundingSkill>.Instance);

        var result = await skill.BuildGroundingContextAsync(new MafGroundingRequest
        {
            ServiceGroupId = Guid.NewGuid()
        });

        Assert.NotNull(result);
        Assert.Equal("optimize_within_policy", result.SuggestedConstraint);
        Assert.Contains("reduce_cost", result.SuggestedObjectives);
        Assert.Contains(result.Warnings, static warning => warning.Contains("not configured", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildGroundingContextAsync_WithLearnClient_ReturnsLearnReferences()
    {
        var learnClient = CreateLearnClient(new FakeToolInvoker(onCall: () => new CallToolResult
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = """[{"title":"WAF Guidance","url":"https://learn.microsoft.com/test","summary":"Use reliability practices"}]"""
                }
            ]
        }));

        var skill = new MafGroundingSkill(
            NullLogger<MafGroundingSkill>.Instance,
            learnMcpClient: learnClient,
            azureMcpToolClient: null);

        var result = await skill.BuildGroundingContextAsync(new MafGroundingRequest
        {
            ServiceGroupId = Guid.NewGuid(),
            Constraint = "cost reliability tradeoffs",
            ResourceTypes = ["Microsoft.Compute/virtualMachines"]
        });

        Assert.NotEmpty(result.LearnReferences);
        Assert.Contains(result.LearnReferences, static reference =>
            reference.Title.Contains("WAF", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildGroundingContextAsync_WithRequestingAgentId_RecordsProvenanceInReferences()
    {
        var learnClient = CreateLearnClient(new FakeToolInvoker(onCall: () => new CallToolResult
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = """[{"title":"Security Best Practices","url":"https://learn.microsoft.com/test","summary":"Follow zero-trust"}]"""
                }
            ]
        }));

        var skill = new MafGroundingSkill(
            NullLogger<MafGroundingSkill>.Instance,
            learnMcpClient: learnClient,
            azureMcpToolClient: null);

        const string securityAgentId = "SecurityAnalysis";
        var result = await skill.BuildGroundingContextAsync(
            new MafGroundingRequest
            {
                ServiceGroupId = Guid.NewGuid(),
                Constraint = "security zero-trust",
                ResourceTypes = ["Microsoft.KeyVault/vaults"]
            },
            requestingAgentId: securityAgentId);

        Assert.Equal(securityAgentId, result.RequestingAgentId);
        Assert.All(result.LearnReferences, reference =>
        {
            Assert.Equal(securityAgentId, reference.RequestedByAgent);
            Assert.NotNull(reference.Pillar);
            Assert.NotEqual(default, reference.RetrievedAt);
        });
    }

    [Fact]
    public async Task BuildGroundingContextAsync_PolicyViolation_ReliabilityMissingLearnReferences()
    {
        var skill = new MafGroundingSkill(NullLogger<MafGroundingSkill>.Instance);

        const string reliabilityAgentId = "ReliabilityAnalysis";
        var result = await skill.BuildGroundingContextAsync(
            new MafGroundingRequest
            {
                ServiceGroupId = Guid.NewGuid()
            },
            requestingAgentId: reliabilityAgentId);

        // Reliability agent requires Learn MCP which is null
        Assert.False(result.PolicyCompliant);
        Assert.NotEmpty(result.PolicyViolations);
        Assert.Contains(result.PolicyViolations, violation =>
            violation.Contains(reliabilityAgentId, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildGroundingContextAsync_CostOptimizationAgent_CapabilityRouting()
    {
        // Cost optimization agent should have capability routing applied
        var skill = new MafGroundingSkill(
            NullLogger<MafGroundingSkill>.Instance,
            learnMcpClient: CreateLearnClient(new FakeToolInvoker()),
            azureMcpToolClient: null);

        const string costAgentId = "CostOptimization";
        var result = await skill.BuildGroundingContextAsync(
            new MafGroundingRequest
            {
                ServiceGroupId = Guid.NewGuid()
            },
            requestingAgentId: costAgentId);

        // Cost agent should have tool capability filtering applied (even though no tools retrieved without client)
        Assert.Equal(costAgentId, result.RequestingAgentId);
    }

    [Fact]
    public async Task BuildGroundingContextAsync_SecurityAgent_RoutesSecuritySpecificTools()
    {
        // Security agent should have security-specific capability routing
        var skill = new MafGroundingSkill(
            NullLogger<MafGroundingSkill>.Instance,
            learnMcpClient: CreateLearnClient(new FakeToolInvoker()),
            azureMcpToolClient: null);

        const string securityAgentId = "SecurityAnalysis";
        var result = await skill.BuildGroundingContextAsync(
            new MafGroundingRequest
            {
                ServiceGroupId = Guid.NewGuid()
            },
            requestingAgentId: securityAgentId);

        Assert.Equal(securityAgentId, result.RequestingAgentId);
    }

    [Fact]
    public void MafSkillPolicy_ReliabilityAgentValidation_RequiresLearnReferences()
    {
        var result = new MafGroundingResult
        {
            LearnMcpEnabled = false,
            LearnReferences = [],
            AzureMcpEnabled = true,
            AzureToolNames = ["tool1", "tool2"]
        };

        var validation = MafSkillPolicy.ValidateGroundingPolicy("ReliabilityAnalysis", result);

        Assert.False(validation.IsValid);
        Assert.NotEmpty(validation.Violations);
        Assert.Contains(validation.Violations, v =>
            v.Contains("Learn MCP", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MafSkillPolicy_CostOptimizationAgent_RequiresAzureTools()
    {
        var result = new MafGroundingResult
        {
            LearnMcpEnabled = true,
            LearnReferences = [new MafGroundingReference { Title = "Cost Guide" }],
            AzureMcpEnabled = false,
            AzureToolNames = []
        };

        var validation = MafSkillPolicy.ValidateGroundingPolicy("CostOptimization", result);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Violations, v =>
            v.Contains("Azure MCP", StringComparison.OrdinalIgnoreCase));
    }

    private static LearnMcpClient CreateLearnClient(IMcpToolInvoker toolInvoker)
    {
        var options = Options.Create(new LearnMcpOptions
        {
            ServerUrl = "https://learn.test/mcp",
            Enabled = true,
            EnableToolDiscovery = false
        });

        return new LearnMcpClient(
            options,
            NullLogger<LearnMcpClient>.Instance,
            NullLoggerFactory.Instance,
            toolInvoker: toolInvoker);
    }

    private sealed class FakeToolInvoker : IMcpToolInvoker
    {
        private readonly Func<CallToolResult?>? _onCall;

        public FakeToolInvoker(Func<CallToolResult?>? onCall = null)
        {
            _onCall = onCall;
        }

        public Task<IReadOnlyList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult((IReadOnlyList<McpClientTool>)Array.Empty<McpClientTool>());

        public Task<CallToolResult?> CallToolAsync(
            string toolName,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_onCall?.Invoke());
        }
    }
}
