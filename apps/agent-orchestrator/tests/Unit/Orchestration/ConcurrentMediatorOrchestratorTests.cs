using Atlas.AgentOrchestrator.Agents;
using Atlas.AgentOrchestrator.Contracts;
using Atlas.AgentOrchestrator.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.AgentOrchestrator.Tests.Unit.Orchestration;

public class ConcurrentMediatorOrchestratorTests
{
  private readonly ConcurrentMediatorOrchestrator _orchestrator;

  public ConcurrentMediatorOrchestratorTests()
  {
    var mediatorAgent = new GovernanceMediatorAgent(
        NullLogger<GovernanceMediatorAgent>.Instance,
        foundryClient: null,
        learnMcpClient: null);

    _orchestrator = new ConcurrentMediatorOrchestrator(
        NullLogger<ConcurrentMediatorOrchestrator>.Instance,
        mediatorAgent);
  }

  [Fact]
  public async Task EvaluateAndMediateAsync_ProducesThreeEvaluations_ReturnsMediationOutcome()
  {
    var agentResults = BuildTypicalAgentResults();
    var context = BuildContext();

    var result = await _orchestrator.EvaluateAndMediateAsync(
        "optimize_within_policy",
        ["reduce_cost", "maintain_sla", "enforce_security"],
        agentResults,
        context);

    Assert.NotNull(result);
    Assert.NotEmpty(result.Outcome.Status);
    Assert.NotNull(result.Outcome.ScoreImpact);
    Assert.Contains(result.Messages, message => message.MessageType == A2AMessageTypes.MediationOutcome);
  }

  [Fact]
  public async Task EvaluateAndMediateAsync_WithCostObjective_ProducesCostReduction()
  {
    var agentResults = BuildTypicalAgentResults();
    var context = BuildContext();

    var result = await _orchestrator.EvaluateAndMediateAsync(
        "reduce_monthly_cost",
        ["reduce_cost"],
        agentResults,
        context);

    Assert.NotNull(result);
    Assert.NotEmpty(result.Outcome.ResolutionNarrative);
  }

  [Fact]
  public async Task EvaluateAndMediateAsync_EmptyAgentResults_StillProducesOutcome()
  {
    var agentResults = new Dictionary<string, object>();
    var context = BuildContext();

    var result = await _orchestrator.EvaluateAndMediateAsync(
        "optimize_within_policy",
        ["reduce_cost"],
        agentResults,
        context);

    Assert.NotNull(result);
    Assert.NotEmpty(result.Outcome.Status);
  }

  [Fact]
  public async Task EvaluateAndMediateAsync_WithSecurityFindings_IncludesSecurityPerspective()
  {
    var agentResults = new Dictionary<string, object>
    {
      ["WellArchitected"] = new AgentAnalysisResult
      {
        Score = 55,
        Confidence = 0.8,
        Findings =
            [
                new Finding { Category = "Security", Severity = "High", Description = "No encryption at rest" },
                    new Finding { Category = "Security", Severity = "High", Description = "No NSG on subnet" },
                    new Finding { Category = "Security", Severity = "Medium", Description = "No MFA enforced" },
                    new Finding { Category = "Security", Severity = "Medium", Description = "Local auth enabled" }
            ],
        Recommendations =
            [
                new Recommendation { Category = "Security", Title = "Enable encryption at rest", Priority = "High" },
                    new Recommendation { Category = "Security", Title = "Add NSG rules", Priority = "High" }
            ]
      }
    };

    var context = BuildContext();

    var result = await _orchestrator.EvaluateAndMediateAsync(
        "audit_security",
        ["enforce_security"],
        agentResults,
        context);

    Assert.NotNull(result);
    Assert.NotEmpty(result.Outcome.SuggestedChanges);
  }

  [Fact]
  public async Task EvaluateAndMediateAsync_EmitsConcurrentResultsAndMediationRequest()
  {
    var agentResults = BuildTypicalAgentResults();
    var context = BuildContext();

    var result = await _orchestrator.EvaluateAndMediateAsync(
        "optimize_within_policy",
        ["reduce_cost", "maintain_sla", "enforce_security"],
        agentResults,
        context);

    Assert.Equal(5, result.Messages.Count);
    Assert.Equal(3, result.Messages.Count(message => message.MessageType == A2AMessageTypes.ConcurrentResult));
    Assert.Contains(result.Messages, message => message.MessageType == A2AMessageTypes.MediationRequest);
    Assert.Contains(result.Messages, message => message.MessageType == A2AMessageTypes.MediationOutcome);
  }

  private static Dictionary<string, object> BuildTypicalAgentResults()
  {
    return new Dictionary<string, object>
    {
      ["FinOps"] = new AgentAnalysisResult
      {
        Score = 72,
        Confidence = 0.85,
        Recommendations =
            [
                new Recommendation { Title = "Right-size VMs", Priority = "High", Category = "Cost" },
                    new Recommendation { Title = "Reserved Instances", Priority = "Medium", Category = "Cost" }
            ]
      },
      ["Reliability"] = new AgentAnalysisResult
      {
        Score = 68,
        Confidence = 0.80,
        Recommendations =
            [
                new Recommendation { Title = "Enable availability zones", Priority = "High", Category = "Reliability" }
            ]
      },
      ["WellArchitected"] = new AgentAnalysisResult
      {
        Score = 70,
        Confidence = 0.75,
        Findings =
            [
                new Finding { Category = "Security", Severity = "Medium", Description = "No encryption" }
            ],
        Recommendations =
            [
                new Recommendation { Category = "Security", Title = "Enable TDE", Priority = "High" }
            ]
      }
    };
  }

  private static AnalysisContext BuildContext()
  {
    return new AnalysisContext
    {
      ServiceGroupId = Guid.NewGuid(),
      AnalysisRunId = Guid.NewGuid(),
      CorrelationId = Guid.NewGuid(),
      Snapshot = new DiscoverySnapshot
      {
        Id = Guid.NewGuid(),
        ServiceGroupId = Guid.NewGuid(),
        ResourceCount = 15
      }
    };
  }
}
