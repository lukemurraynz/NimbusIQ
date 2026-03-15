using Atlas.AgentOrchestrator.Contracts;
using Atlas.AgentOrchestrator.Integrations.Azure;
using Atlas.AgentOrchestrator.Integrations.MCP;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace Atlas.AgentOrchestrator.Agents;

/// <summary>
/// Mediator agent that reconciles conflicting evaluations from concurrent FinOps,
/// Reliability, and Security agents. Uses AI Foundry for multi-perspective tradeoff
/// reasoning and Learn MCP for grounding in current WAF guidance.
/// </summary>
public class GovernanceMediatorAgent
{
  private readonly ILogger<GovernanceMediatorAgent> _logger;
  private readonly IAzureAIFoundryClient? _foundryClient;
  private readonly LearnMcpClient? _learnMcpClient;
  private static readonly ActivitySource ActivitySource = new("Atlas.AgentOrchestrator.GovernanceMediator");

  public GovernanceMediatorAgent(
      ILogger<GovernanceMediatorAgent> logger,
      IAzureAIFoundryClient? foundryClient = null,
      LearnMcpClient? learnMcpClient = null)
  {
    _logger = logger;
    _foundryClient = foundryClient;
    _learnMcpClient = learnMcpClient;
  }

  /// <summary>
  /// Mediates concurrent agent evaluation results, resolves conflicts,
  /// and produces a unified negotiation outcome with tradeoff matrix.
  /// </summary>
  public Task<MediationOutcomePayload> MediateAsync(
      MediationRequestPayload request,
      CancellationToken cancellationToken = default)
      => MediateAsync(request, null, cancellationToken);

  /// <summary>
  /// Mediates concurrent agent evaluation results with optional MCP audit context.
  /// </summary>
  public async Task<MediationOutcomePayload> MediateAsync(
      MediationRequestPayload request,
      ToolCallContext? toolCallContext,
      CancellationToken cancellationToken = default)
  {
    using var activity = ActivitySource.StartActivity("GovernanceMediator.Mediate");
    activity?.SetTag("constraint", request.Constraint);
    activity?.SetTag("agent.count", request.AgentEvaluations.Count);

    _logger.LogInformation(
        "Mediating {Count} concurrent agent evaluations for constraint: {Constraint}",
        request.AgentEvaluations.Count,
        request.Constraint);

    var conflicts = DetectConflicts(request.AgentEvaluations);
    var allConflicts = new List<MediationConflict>([.. request.DetectedConflicts, .. conflicts]);
    request = new MediationRequestPayload
    {
      Constraint = request.Constraint,
      Objectives = request.Objectives,
      AgentEvaluations = request.AgentEvaluations,
      DetectedConflicts = allConflicts
    };

    string? wafGuidance = null;
    if (_learnMcpClient is not null)
    {
      wafGuidance = await FetchWafGuidanceAsync(request.Constraint, toolCallContext, cancellationToken);
    }

    var resolutionNarrative = await GenerateResolutionAsync(request, wafGuidance, cancellationToken);

    var suggestedChanges = SynthesizeSuggestedChanges(request.AgentEvaluations, request.DetectedConflicts);
    var scoreImpact = ProjectScoreImpact(request.AgentEvaluations);

    var hasBlockingConflicts = request.DetectedConflicts.Any(c =>
        c.ConflictType is "cost_vs_reliability" or "security_vs_cost");

    var outcome = new MediationOutcomePayload
    {
      Status = hasBlockingConflicts ? "conditional" : "approved",
      ResolutionNarrative = resolutionNarrative,
      SuggestedChanges = suggestedChanges,
      ScoreImpact = scoreImpact,
      RequiresDualApproval = hasBlockingConflicts,
      AgentsThatAgreed = request.AgentEvaluations
            .Where(e => !request.DetectedConflicts
                .SelectMany(c => c.ConflictingAgents)
                .Contains(e.AgentName))
            .Select(e => e.AgentName)
            .ToList(),
      UnresolvedConflicts = request.DetectedConflicts
            .Where(c => c.ConflictType is "security_vs_cost")
            .ToList()
    };

    _logger.LogInformation(
        "Mediation complete: status={Status}, suggestedChanges={ChangeCount}, conflicts={ConflictCount}",
        outcome.Status,
        outcome.SuggestedChanges.Count,
        outcome.UnresolvedConflicts.Count);

    return outcome;
  }

  /// <summary>
  /// Detects conflicts between concurrent agent positions by comparing
  /// cost/risk/SLA deltas for opposing directions.
  /// </summary>
  public static List<MediationConflict> DetectConflicts(
      List<ConcurrentEvaluationPayload> evaluations)
  {
    var conflicts = new List<MediationConflict>();

    var finOps = evaluations.FirstOrDefault(e => e.Pillar is "FinOps" or "CostOptimization");
    var reliability = evaluations.FirstOrDefault(e => e.Pillar is "Reliability");
    var security = evaluations.FirstOrDefault(e => e.Pillar is "Security");

    // FinOps wants to reduce cost vs Reliability wants to maintain/increase resources
    if (finOps is not null && reliability is not null
        && finOps.EstimatedCostDelta < 0
        && reliability.EstimatedRiskDelta > 0)
    {
      conflicts.Add(new MediationConflict
      {
        ConflictType = "cost_vs_reliability",
        ConflictingAgents = [finOps.AgentName, reliability.AgentName],
        Description = $"FinOps recommends cost reduction (${finOps.EstimatedCostDelta}/mo) " +
                        $"but Reliability flags increased risk ({reliability.EstimatedRiskDelta:P0})",
        Positions = new Dictionary<string, string>
        {
          [finOps.AgentName] = finOps.Position,
          [reliability.AgentName] = reliability.Position
        }
      });
    }

    // Security hardening vs cost reduction
    if (security is not null && finOps is not null
        && security.EstimatedCostDelta > 0
        && finOps.EstimatedCostDelta < 0)
    {
      conflicts.Add(new MediationConflict
      {
        ConflictType = "security_vs_cost",
        ConflictingAgents = [security.AgentName, finOps.AgentName],
        Description = $"Security requires additional spend (${security.EstimatedCostDelta}/mo) " +
                        $"conflicting with FinOps cost reduction target (${finOps.EstimatedCostDelta}/mo)",
        Positions = new Dictionary<string, string>
        {
          [security.AgentName] = security.Position,
          [finOps.AgentName] = finOps.Position
        }
      });
    }

    // Reliability SLA increase vs cost
    if (reliability is not null && finOps is not null
        && reliability.SlaImpact > 0
        && finOps.EstimatedCostDelta < 0)
    {
      conflicts.Add(new MediationConflict
      {
        ConflictType = "sla_vs_cost",
        ConflictingAgents = [reliability.AgentName, finOps.AgentName],
        Description = $"Reliability targets SLA improvement ({reliability.SlaImpact:P2}) " +
                        $"which conflicts with cost reduction goal (${finOps.EstimatedCostDelta}/mo)",
        Positions = new Dictionary<string, string>
        {
          [reliability.AgentName] = reliability.Position,
          [finOps.AgentName] = finOps.Position
        }
      });
    }

    return conflicts;
  }

  private async Task<string?> FetchWafGuidanceAsync(
      string constraint,
      ToolCallContext? toolCallContext,
      CancellationToken cancellationToken)
  {
    try
    {
      var results = await _learnMcpClient!.SearchDocsAsync(
          $"Azure Well-Architected Framework tradeoffs {constraint}",
          "waf",
          cancellationToken: cancellationToken,
          toolCallContext: toolCallContext);

      if (results.Count > 0)
      {
        return string.Join("\n", results.Take(3).Select(r =>
            $"- [{r.Title}]({r.Url}): {r.Summary}"));
      }
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Learn MCP lookup failed for governance mediation; proceeding without WAF grounding");
    }

    return null;
  }

  private async Task<string> GenerateResolutionAsync(
      MediationRequestPayload request,
      string? wafGuidance,
      CancellationToken cancellationToken)
  {
    if (_foundryClient is null)
      return GenerateRuleBasedResolution(request);

    try
    {
      var agentPositionsSummary = string.Join("\n", request.AgentEvaluations.Select(e =>
          $"  - {e.AgentName} ({e.Pillar}): score={e.Score:F1}, position=\"{e.Position}\", " +
          $"costDelta=${e.EstimatedCostDelta}, riskDelta={e.EstimatedRiskDelta:P0}, slaDelta={e.SlaImpact:P2}"));

      var conflictsSummary = request.DetectedConflicts.Count > 0
          ? string.Join("\n", request.DetectedConflicts.Select(c =>
              $"  - {c.ConflictType}: {c.Description}"))
          : "  None detected";

      var guidanceSection = wafGuidance is not null
          ? $"\nRelevant Azure WAF guidance:\n{wafGuidance}"
          : "";

      var prompt =
          $"""
                You are a cloud governance mediator reconciling concurrent agent evaluations.

                User constraint: "{request.Constraint}"
                Objectives: {string.Join(", ", request.Objectives)}

                Agent positions:
                {agentPositionsSummary}

                Detected conflicts:
                {conflictsSummary}
                {guidanceSection}

                Synthesize a 3-5 sentence governance resolution that:
                1. Addresses the user's constraint
                2. Reconciles agent conflicts with specific tradeoff recommendations
                3. Quantifies the impact (cost delta, risk change, SLA impact)
                4. Specifies which changes require dual approval
                Be concise and actionable. Reference WAF principles where applicable.
                """;

      var threadId = $"governance-mediation:{request.Constraint}";
      var narrative = await _foundryClient.SendPromptAsync(prompt, threadId, cancellationToken);

      _logger.LogInformation("AI-generated mediation resolution for constraint: {Constraint}", request.Constraint);
      return narrative;
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "AI mediation resolution failed; falling back to rule-based");
      return GenerateRuleBasedResolution(request);
    }
  }

  private static string GenerateRuleBasedResolution(MediationRequestPayload request)
  {
    var totalCostDelta = request.AgentEvaluations.Sum(e => e.EstimatedCostDelta);
    var avgRiskDelta = request.AgentEvaluations.Average(e => e.EstimatedRiskDelta);
    var conflictCount = request.DetectedConflicts.Count;

    var resolution = conflictCount switch
    {
      0 => $"All agents agree. Net cost impact: ${totalCostDelta}/month. " +
           $"Average risk change: {avgRiskDelta:P0}. Recommend approval.",
      1 => $"One conflict detected between agents. Recommend conditional approval with " +
           $"phased rollout. Net cost impact: ${totalCostDelta}/month.",
      _ => $"{conflictCount} conflicts detected. Recommend escalation to architecture review board. " +
           $"Net cost impact: ${totalCostDelta}/month, average risk change: {avgRiskDelta:P0}."
    };

    return $"{resolution} [Basic analysis — enable AI Foundry for cross-pillar tradeoff reasoning and WAF-grounded resolution narratives]";
  }

  private static List<SuggestedChange> SynthesizeSuggestedChanges(
      List<ConcurrentEvaluationPayload> evaluations,
      List<MediationConflict> conflicts)
  {
    var conflictingAgents = conflicts
        .SelectMany(c => c.ConflictingAgents)
        .ToHashSet();

    return evaluations
        .SelectMany(e => e.SuggestedActions.Select(action => new SuggestedChange
        {
          Title = action,
          Description = $"Suggested by {e.AgentName} ({e.Pillar} perspective)",
          Pillar = e.Pillar,
          CostDelta = e.EstimatedCostDelta / Math.Max(e.SuggestedActions.Count, 1),
          RiskDelta = e.EstimatedRiskDelta / Math.Max(e.SuggestedActions.Count, 1),
          SlaDelta = e.SlaImpact / Math.Max(e.SuggestedActions.Count, 1),
          SourceAgent = e.AgentName
        }))
        .ToList();
  }

  private static ScoreImpactProjection ProjectScoreImpact(
      List<ConcurrentEvaluationPayload> evaluations)
  {
    var currentOverall = evaluations.Average(e => e.Score);
    var avgCostBenefit = evaluations.Average(e => (double)e.EstimatedCostDelta);
    var projectedDelta = avgCostBenefit < 0 ? 2.5 : -1.0;

    return new ScoreImpactProjection
    {
      CurrentOverall = currentOverall,
      ProjectedOverall = Math.Clamp(currentOverall + projectedDelta, 0, 100),
      PillarDeltas = evaluations.ToDictionary(
            e => e.Pillar,
            e => e.EstimatedRiskDelta < 0 ? 3.0 : -1.5)
    };
  }
}
