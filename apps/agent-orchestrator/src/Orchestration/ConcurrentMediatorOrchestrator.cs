using System.Diagnostics;
using System.Text.Json;
using Atlas.AgentOrchestrator.Agents;
using Atlas.AgentOrchestrator.Contracts;
using Atlas.AgentOrchestrator.Integrations.MCP;
using Microsoft.Extensions.Logging;

namespace Atlas.AgentOrchestrator.Orchestration;

/// <summary>
/// Implements the Concurrent+Mediator MAF pattern for governance negotiation.
/// Fan-out: FinOps, Reliability, and Security agents evaluate a constraint in parallel.
/// Fan-in: GovernanceMediatorAgent reconciles positions and produces a unified outcome.
/// Context is transferred explicitly per MAF skill rule #3 (no shared session assumptions).
/// </summary>
public class ConcurrentMediatorOrchestrator
{
  private readonly ILogger<ConcurrentMediatorOrchestrator> _logger;
  private readonly GovernanceMediatorAgent _mediatorAgent;
  private static readonly ActivitySource ActivitySource = new("Atlas.AgentOrchestrator.ConcurrentMediator");

  public ConcurrentMediatorOrchestrator(
      ILogger<ConcurrentMediatorOrchestrator> logger,
      GovernanceMediatorAgent mediatorAgent)
  {
    _logger = logger;
    _mediatorAgent = mediatorAgent;
  }

  /// <summary>
  /// Executes concurrent agent evaluations and mediates the results.
  /// Each agent receives an independent copy of the context (no shared state).
  /// </summary>
  public async Task<ConcurrentMediationResult> EvaluateAndMediateAsync(
      string constraint,
      List<string> objectives,
      Dictionary<string, object> agentResults,
      AnalysisContext context,
      CancellationToken cancellationToken = default)
  {
    using var activity = ActivitySource.StartActivity("ConcurrentMediator.EvaluateAndMediate");
    activity?.SetTag("constraint", constraint);
    activity?.SetTag("objectives", string.Join(",", objectives));

    _logger.LogInformation(
        "Starting concurrent governance evaluation for constraint: {Constraint}",
        constraint);

    // Fan-out: evaluate constraint from each pillar perspective concurrently
    var evaluationTasks = new List<Task<ConcurrentEvaluationPayload>>
        {
            EvaluateFinOpsPerspective(constraint, objectives, agentResults, context, cancellationToken),
            EvaluateReliabilityPerspective(constraint, objectives, agentResults, context, cancellationToken),
            EvaluateSecurityPerspective(constraint, objectives, agentResults, context, cancellationToken)
        };

    var evaluations = await Task.WhenAll(evaluationTasks);
    var correlationId = context.CorrelationId.ToString("D");
    var a2aMessages = evaluations
      .Select(evaluation => CreateA2AMessage(
        context,
        correlationId,
        evaluation.AgentName,
        "GovernanceMediator",
        A2AMessageTypes.ConcurrentResult,
        evaluation,
        A2APriority.Normal,
        evaluation.Score >= 80 ? 900 : 600))
      .ToList();

    _logger.LogInformation(
        "Concurrent evaluation complete: {Count} agents responded",
        evaluations.Length);

    // Fan-in: create mediation request and delegate to mediator
    var mediationRequest = new MediationRequestPayload
    {
      Constraint = constraint,
      Objectives = objectives,
      AgentEvaluations = [.. evaluations],
      DetectedConflicts = []
    };

    a2aMessages.Add(CreateA2AMessage(
        context,
        correlationId,
        "ConcurrentMediatorOrchestrator",
        "GovernanceMediator",
        A2AMessageTypes.MediationRequest,
        mediationRequest,
        A2APriority.High,
        900));

    var toolCallContext = new ToolCallContext
    {
      AnalysisRunId = context.AnalysisRunId,
      ServiceGroupId = context.ServiceGroupId,
      CorrelationId = context.CorrelationId,
      ActorId = "governance-mediator-agent",
      TraceId = Activity.Current?.TraceId.ToString(),
      SpanId = Activity.Current?.SpanId.ToString(),
      TraceParent = Activity.Current?.Id
    };

    var outcome = await _mediatorAgent.MediateAsync(mediationRequest, toolCallContext, cancellationToken);

    a2aMessages.Add(CreateA2AMessage(
        context,
        correlationId,
        "GovernanceMediator",
        "MultiAgentOrchestrator",
        A2AMessageTypes.MediationOutcome,
        outcome,
        A2APriority.High,
        900));

    activity?.SetTag("outcome.status", outcome.Status);
    activity?.SetTag("outcome.changes", outcome.SuggestedChanges.Count);

    return new ConcurrentMediationResult(outcome, a2aMessages);
  }

  private static A2AMessage CreateA2AMessage(
      AnalysisContext context,
      string correlationId,
      string senderAgent,
      string recipientAgent,
      string messageType,
      object payload,
      string priority,
      int ttlSeconds)
  {
    return new A2AMessage
    {
      MessageId = Guid.NewGuid().ToString("D"),
      CorrelationId = correlationId,
      Timestamp = DateTimeOffset.UtcNow,
      SenderAgent = senderAgent,
      RecipientAgent = recipientAgent,
      MessageType = messageType,
      Payload = payload,
      Priority = priority,
      TtlSeconds = ttlSeconds,
      Lineage = new LineageMetadata
      {
        OriginAgent = senderAgent,
        ContributingAgents = [senderAgent],
        EvidenceReferences = [$"analysis_run:{context.AnalysisRunId:D}", $"service_group:{context.ServiceGroupId:D}"],
        DecisionPath = ["concurrent_mediator", messageType],
        ConfidenceScore = null,
        TraceId = Activity.Current?.TraceId.ToString(),
        SpanId = Activity.Current?.SpanId.ToString()
      }
    };
  }

  private Task<ConcurrentEvaluationPayload> EvaluateFinOpsPerspective(
      string constraint,
      List<string> objectives,
      Dictionary<string, object> agentResults,
      AnalysisContext context,
      CancellationToken cancellationToken)
  {
    return Task.Run(() =>
    {
      var finOpsResult = agentResults.GetValueOrDefault("FinOps") as AgentAnalysisResult;
      var score = finOpsResult?.Score ?? 75.0;

      var wantsCostReduction = objectives.Contains("reduce_cost") ||
                                   constraint.Contains("cost", StringComparison.OrdinalIgnoreCase);

      var costDelta = wantsCostReduction ? -500m : 0m;
      if (finOpsResult?.Recommendations is { Count: > 0 })
      {
        costDelta = -(decimal)(finOpsResult.Recommendations.Count * 200);
      }

      var suggestedActions = finOpsResult?.Recommendations
              .Take(3)
              .Select(r => r.Title ?? r.Description ?? "Optimize resource spend")
              .ToList() ?? ["Review resource utilization"];

      return new ConcurrentEvaluationPayload
      {
        AgentName = "FinOps",
        Pillar = "CostOptimization",
        Score = score,
        Position = wantsCostReduction
                  ? $"Reduce monthly spend by targeting {finOpsResult?.Recommendations.Count ?? 0} optimization opportunities"
                  : "Current cost posture is acceptable",
        SuggestedActions = suggestedActions,
        EstimatedCostDelta = costDelta,
        EstimatedRiskDelta = wantsCostReduction ? 0.05 : 0,
        SlaImpact = wantsCostReduction ? -0.001 : 0
      };
    }, cancellationToken);
  }

  private Task<ConcurrentEvaluationPayload> EvaluateReliabilityPerspective(
      string constraint,
      List<string> objectives,
      Dictionary<string, object> agentResults,
      AnalysisContext context,
      CancellationToken cancellationToken)
  {
    return Task.Run(() =>
    {
      var reliabilityResult = agentResults.GetValueOrDefault("Reliability") as AgentAnalysisResult;
      var score = reliabilityResult?.Score ?? 75.0;

      var wantsSlaPreservation = objectives.Contains("maintain_sla") ||
                                     constraint.Contains("SLA", StringComparison.OrdinalIgnoreCase);

      var riskDelta = 0.0;
      if (wantsSlaPreservation && reliabilityResult?.Score < 80)
      {
        riskDelta = 0.10;
      }

      var suggestedActions = reliabilityResult?.Recommendations
              .Take(3)
              .Select(r => r.Title ?? r.Description ?? "Improve availability design")
              .ToList() ?? ["Review reliability posture"];

      return new ConcurrentEvaluationPayload
      {
        AgentName = "Reliability",
        Pillar = "Reliability",
        Score = score,
        Position = wantsSlaPreservation
                  ? "SLA targets must be preserved; any cost optimization must not degrade availability"
                  : "Reliability posture can be adjusted proportionally",
        SuggestedActions = suggestedActions,
        EstimatedCostDelta = wantsSlaPreservation ? 200m : 0m,
        EstimatedRiskDelta = riskDelta,
        SlaImpact = wantsSlaPreservation ? 0.005 : 0
      };
    }, cancellationToken);
  }

  private Task<ConcurrentEvaluationPayload> EvaluateSecurityPerspective(
      string constraint,
      List<string> objectives,
      Dictionary<string, object> agentResults,
      AnalysisContext context,
      CancellationToken cancellationToken)
  {
    return Task.Run(() =>
    {
      var wafResult = agentResults.GetValueOrDefault("WellArchitected") as AgentAnalysisResult;
      var bestPracticeResult = agentResults.GetValueOrDefault("BestPractice");
      var score = wafResult?.Score ?? 75.0;

      var securityFindings = wafResult?.Findings
              .Count(f => f.Category?.Contains("security", StringComparison.OrdinalIgnoreCase) == true) ?? 0;

      var suggestedActions = wafResult?.Recommendations
              .Where(r => r.Category?.Contains("security", StringComparison.OrdinalIgnoreCase) == true)
              .Take(3)
              .Select(r => r.Title ?? r.Description ?? "Harden security posture")
              .ToList() ?? ["Review security configuration"];

      return new ConcurrentEvaluationPayload
      {
        AgentName = "Security",
        Pillar = "Security",
        Score = score,
        Position = securityFindings > 0
                  ? $"{securityFindings} security findings require remediation before cost optimization"
                  : "Security posture is acceptable; cost optimization can proceed",
        SuggestedActions = suggestedActions,
        EstimatedCostDelta = securityFindings > 3 ? 300m : 0m,
        EstimatedRiskDelta = securityFindings > 0 ? -0.03 * securityFindings : 0,
        SlaImpact = 0
      };
    }, cancellationToken);
  }
}

public sealed record ConcurrentMediationResult(
  MediationOutcomePayload Outcome,
  IReadOnlyList<A2AMessage> Messages);
