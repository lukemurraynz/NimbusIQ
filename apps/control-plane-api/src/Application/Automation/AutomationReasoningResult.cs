namespace Atlas.ControlPlane.Application.Automation;

/// <summary>
/// Result of agent-driven evaluation of automation rule applicability.
/// Augments basic rule matching with multi-agent reasoning and conflict detection.
/// </summary>
public record AutomationReasoningResult(
    /// <summary>
    /// Whether the recommendation should be auto-approved per agent reasoning.
    /// </summary>
    bool ShouldAutoApprove,

    /// <summary>
    /// The matched automation rule, if applicable.
    /// </summary>
    AutomationRuleReasoningDto? MatchedRule,

    /// <summary>
    /// Agent-derived confidence in the auto-approval decision (0.0-1.0).
    /// Represents consensus across cost, reliability, and governance agents.
    /// </summary>
    decimal AgentConfidence,

    /// <summary>
    /// Summary of agent reasoning (why this rule matched or why it was rejected).
    /// </summary>
    string ReasoningSummary,

    /// <summary>
    /// Names of agents that contributed to this evaluation.
    /// Example: ["FinOpsOptimizerAgent", "ReliabilityAgent", "GovernanceMediatorAgent"]
    /// </summary>
    IReadOnlyList<string> ContributingAgents,

    /// <summary>
    /// Estimated impact of applying this rule across WAF pillars (0-100 scale).
    /// Dictionary key is pillar name (e.g., "FinOps", "Reliability", "Architecture", "Sustainability").
    /// </summary>
    IReadOnlyDictionary<string, int>? PredictedImpactByPillar,

    /// <summary>
    /// Other rules that matched but were deprioritized by agent reasoning.
    /// Useful for conflict detection and multi-rule scenarios.
    /// </summary>
    IReadOnlyList<AutomationRuleConflict>? ConflictingRules,

    /// <summary>
    /// Source of reasoning: "heuristic", "agent", or "fallback".
    /// Helps client understand confidence level.
    /// </summary>
    string Source
);

/// <summary>
/// Automation rule with agent reasoning context.
/// </summary>
public record AutomationRuleReasoningDto(
    Guid Id,
    string RuleName,
    string Trigger,
    string ActionType,
    decimal MaxRiskThreshold,
    decimal MinConfidenceThreshold,
    bool IsEnabled,
    // Agent reasoning fields
    decimal AgentValidationConfidence,
    string AgentValidationRationale
);

/// <summary>
/// When multiple automation rules match, conflicting rules are recorded here.
/// Helps with mediation and prioritization decisions.
/// </summary>
public record AutomationRuleConflict(
    Guid RuleId,
    string RuleName,
    string Reason,
    decimal NormalizedPriority
);
