using Atlas.AgentOrchestrator.Agents;
using Atlas.AgentOrchestrator.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.AgentOrchestrator.Tests.Unit.Agents;

public class GovernanceMediatorAgentTests
{
  private readonly GovernanceMediatorAgent _agent = new(
      NullLogger<GovernanceMediatorAgent>.Instance,
      foundryClient: null,
      learnMcpClient: null);

  [Fact]
  public async Task MediateAsync_NoConflicts_ReturnsApprovedStatus()
  {
    var request = new MediationRequestPayload
    {
      Constraint = "optimize_within_policy",
      Objectives = ["reduce_cost"],
      AgentEvaluations =
        [
            new ConcurrentEvaluationPayload
                {
                    AgentName = "FinOps",
                    Pillar = "CostOptimization",
                    Score = 80,
                    Position = "Cost posture acceptable",
                    EstimatedCostDelta = -200m,
                    EstimatedRiskDelta = 0,
                    SlaImpact = 0
                },
                new ConcurrentEvaluationPayload
                {
                    AgentName = "Reliability",
                    Pillar = "Reliability",
                    Score = 85,
                    Position = "Reliability posture stable",
                    EstimatedCostDelta = 0m,
                    EstimatedRiskDelta = 0,
                    SlaImpact = 0
                }
        ],
      DetectedConflicts = []
    };

    var outcome = await _agent.MediateAsync(request);

    Assert.Equal("approved", outcome.Status);
    Assert.False(outcome.RequiresDualApproval);
    Assert.NotEmpty(outcome.ResolutionNarrative);
  }

  [Fact]
  public async Task MediateAsync_CostVsReliabilityConflict_ReturnsConditionalWithDualApproval()
  {
    var request = new MediationRequestPayload
    {
      Constraint = "reduce_cost",
      Objectives = ["reduce_cost", "maintain_sla"],
      AgentEvaluations =
        [
            new ConcurrentEvaluationPayload
                {
                    AgentName = "FinOps",
                    Pillar = "FinOps",
                    Score = 70,
                    Position = "Need aggressive cost cuts",
                    EstimatedCostDelta = -1000m,
                    EstimatedRiskDelta = 0.05,
                    SlaImpact = -0.01
                },
                new ConcurrentEvaluationPayload
                {
                    AgentName = "Reliability",
                    Pillar = "Reliability",
                    Score = 60,
                    Position = "Cannot reduce resources without degrading SLA",
                    EstimatedCostDelta = 200m,
                    EstimatedRiskDelta = 0.15,
                    SlaImpact = 0.005
                }
        ],
      DetectedConflicts = []
    };

    var outcome = await _agent.MediateAsync(request);

    Assert.Equal("conditional", outcome.Status);
    Assert.True(outcome.RequiresDualApproval);
  }

  [Fact]
  public void DetectConflicts_OpposingCostAndReliability_DetectsCostVsReliability()
  {
    var evaluations = new List<ConcurrentEvaluationPayload>
        {
            new()
            {
                AgentName = "FinOps",
                Pillar = "FinOps",
                Score = 70,
                Position = "Reduce cost",
                EstimatedCostDelta = -500m,
                EstimatedRiskDelta = 0,
                SlaImpact = 0
            },
            new()
            {
                AgentName = "Reliability",
                Pillar = "Reliability",
                Score = 60,
                Position = "Need more resources",
                EstimatedCostDelta = 200m,
                EstimatedRiskDelta = 0.10,
                SlaImpact = 0.005
            }
        };

    var conflicts = GovernanceMediatorAgent.DetectConflicts(evaluations);

    Assert.Contains(conflicts, c => c.ConflictType == "cost_vs_reliability");
  }

  [Fact]
  public void DetectConflicts_SecurityVsCost_DetectsSecurityConflict()
  {
    var evaluations = new List<ConcurrentEvaluationPayload>
        {
            new()
            {
                AgentName = "FinOps",
                Pillar = "CostOptimization",
                Score = 70,
                Position = "Reduce spend",
                EstimatedCostDelta = -500m,
                EstimatedRiskDelta = 0,
                SlaImpact = 0
            },
            new()
            {
                AgentName = "Security",
                Pillar = "Security",
                Score = 55,
                Position = "Security hardening required",
                EstimatedCostDelta = 300m,
                EstimatedRiskDelta = -0.10,
                SlaImpact = 0
            }
        };

    var conflicts = GovernanceMediatorAgent.DetectConflicts(evaluations);

    Assert.Contains(conflicts, c => c.ConflictType == "security_vs_cost");
  }

  [Fact]
  public void DetectConflicts_NoOpposingPositions_ReturnsEmpty()
  {
    var evaluations = new List<ConcurrentEvaluationPayload>
        {
            new()
            {
                AgentName = "FinOps",
                Pillar = "CostOptimization",
                Score = 85,
                Position = "Cost posture good",
                EstimatedCostDelta = 0m,
                EstimatedRiskDelta = 0,
                SlaImpact = 0
            },
            new()
            {
                AgentName = "Reliability",
                Pillar = "Reliability",
                Score = 90,
                Position = "Reliability is strong",
                EstimatedCostDelta = 0m,
                EstimatedRiskDelta = 0,
                SlaImpact = 0
            }
        };

    var conflicts = GovernanceMediatorAgent.DetectConflicts(evaluations);

    Assert.Empty(conflicts);
  }

  [Fact]
  public async Task MediateAsync_WithSecurityConflict_IncludesUnresolvedConflicts()
  {
    var request = new MediationRequestPayload
    {
      Constraint = "balance_security_and_cost",
      Objectives = ["enforce_security", "reduce_cost"],
      AgentEvaluations =
        [
            new ConcurrentEvaluationPayload
                {
                    AgentName = "FinOps",
                    Pillar = "CostOptimization",
                    Score = 70,
                    Position = "Reduce spend aggressively",
                    EstimatedCostDelta = -800m,
                    EstimatedRiskDelta = 0,
                    SlaImpact = 0
                },
                new ConcurrentEvaluationPayload
                {
                    AgentName = "Security",
                    Pillar = "Security",
                    Score = 50,
                    Position = "Security hardening is non-negotiable",
                    EstimatedCostDelta = 400m,
                    EstimatedRiskDelta = -0.15,
                    SlaImpact = 0
                }
        ],
      DetectedConflicts = []
    };

    var outcome = await _agent.MediateAsync(request);

    Assert.NotEmpty(outcome.UnresolvedConflicts);
    Assert.Contains(outcome.UnresolvedConflicts, c => c.ConflictType == "security_vs_cost");
  }

  [Fact]
  public async Task MediateAsync_ProducesScoreImpactProjection()
  {
    var request = new MediationRequestPayload
    {
      Constraint = "optimize",
      Objectives = ["reduce_cost"],
      AgentEvaluations =
        [
            new ConcurrentEvaluationPayload
                {
                    AgentName = "FinOps",
                    Pillar = "CostOptimization",
                    Score = 75,
                    Position = "Optimize",
                    EstimatedCostDelta = -300m,
                    EstimatedRiskDelta = 0,
                    SlaImpact = 0
                }
        ],
      DetectedConflicts = []
    };

    var outcome = await _agent.MediateAsync(request);

    Assert.NotNull(outcome.ScoreImpact);
    Assert.True(outcome.ScoreImpact.CurrentOverall > 0);
    Assert.True(outcome.ScoreImpact.ProjectedOverall > 0);
  }
}
