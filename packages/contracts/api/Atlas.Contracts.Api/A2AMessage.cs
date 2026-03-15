using System.Text.Json.Serialization;

namespace Atlas.AgentOrchestrator.Contracts;

/// <summary>
/// Typed Agent-to-Agent (A2A) message contract following Microsoft Agent Framework.
/// Schema: specs/001-service-group-scoped/contracts/a2a-message.schema.json
/// </summary>
public class A2AMessage
{
    [JsonPropertyName("message_id")]
    public required string MessageId { get; init; }

    [JsonPropertyName("correlation_id")]
    public required string CorrelationId { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("sender_agent")]
    public required string SenderAgent { get; init; }

    [JsonPropertyName("recipient_agent")]
    public string? RecipientAgent { get; init; }

    [JsonPropertyName("message_type")]
    public required string MessageType { get; init; }

    [JsonPropertyName("payload")]
    public required object Payload { get; init; }

    [JsonPropertyName("lineage")]
    public required LineageMetadata Lineage { get; init; }

    [JsonPropertyName("priority")]
    public string Priority { get; init; } = "normal";

    [JsonPropertyName("ttl_seconds")]
    public int? TtlSeconds { get; init; }
}

public class LineageMetadata
{
    [JsonPropertyName("origin_agent")]
    public required string OriginAgent { get; init; }

    [JsonPropertyName("contributing_agents")]
    public required List<string> ContributingAgents { get; init; }

    [JsonPropertyName("evidence_references")]
    public required List<string> EvidenceReferences { get; init; }

    [JsonPropertyName("decision_path")]
    public required List<string> DecisionPath { get; init; }

    [JsonPropertyName("confidence_score")]
    public decimal? ConfidenceScore { get; init; }

    [JsonPropertyName("trace_id")]
    public string? TraceId { get; init; }

    [JsonPropertyName("span_id")]
    public string? SpanId { get; init; }
}

/// <summary>
/// Message types for A2A communication
/// </summary>
public static class A2AMessageTypes
{
    public const string Discovery = "discovery";
    public const string Analysis = "analysis";
    public const string Recommendation = "recommendation";
    public const string Negotiation = "negotiation";
    public const string Decision = "decision";
    public const string Status = "status";
    public const string Error = "error";
    public const string ConcurrentResult = "concurrent_result";
    public const string MediationRequest = "mediation_request";
    public const string MediationOutcome = "mediation_outcome";
    public const string Conflict = "conflict";
    public const string Escalation = "escalation";
}

/// <summary>
/// Payload for concurrent agent evaluation results sent to the mediator.
/// </summary>
public class ConcurrentEvaluationPayload
{
    [JsonPropertyName("agent_name")]
    public required string AgentName { get; init; }

    [JsonPropertyName("pillar")]
    public required string Pillar { get; init; }

    [JsonPropertyName("score")]
    public double Score { get; init; }

    [JsonPropertyName("position")]
    public required string Position { get; init; }

    [JsonPropertyName("suggested_actions")]
    public List<string> SuggestedActions { get; init; } = [];

    [JsonPropertyName("estimated_cost_delta")]
    public decimal EstimatedCostDelta { get; init; }

    [JsonPropertyName("estimated_risk_delta")]
    public double EstimatedRiskDelta { get; init; }

    [JsonPropertyName("sla_impact")]
    public double SlaImpact { get; init; }
}

/// <summary>
/// Payload for mediation requests combining multiple concurrent evaluation results.
/// </summary>
public class MediationRequestPayload
{
    [JsonPropertyName("constraint")]
    public required string Constraint { get; init; }

    [JsonPropertyName("objectives")]
    public List<string> Objectives { get; init; } = [];

    [JsonPropertyName("agent_evaluations")]
    public List<ConcurrentEvaluationPayload> AgentEvaluations { get; init; } = [];

    [JsonPropertyName("detected_conflicts")]
    public List<MediationConflict> DetectedConflicts { get; init; } = [];
}

/// <summary>
/// Conflict detected between concurrent agent positions.
/// </summary>
public class MediationConflict
{
    [JsonPropertyName("conflict_type")]
    public required string ConflictType { get; init; }

    [JsonPropertyName("conflicting_agents")]
    public List<string> ConflictingAgents { get; init; } = [];

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("positions")]
    public Dictionary<string, string> Positions { get; init; } = [];
}

/// <summary>
/// Payload for mediation outcomes produced by the GovernanceMediatorAgent.
/// </summary>
public class MediationOutcomePayload
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("resolution_narrative")]
    public required string ResolutionNarrative { get; init; }

    [JsonPropertyName("suggested_changes")]
    public List<SuggestedChange> SuggestedChanges { get; init; } = [];

    [JsonPropertyName("score_impact")]
    public ScoreImpactProjection? ScoreImpact { get; init; }

    [JsonPropertyName("requires_dual_approval")]
    public bool RequiresDualApproval { get; init; }

    [JsonPropertyName("agents_that_agreed")]
    public List<string> AgentsThatAgreed { get; init; } = [];

    [JsonPropertyName("unresolved_conflicts")]
    public List<MediationConflict> UnresolvedConflicts { get; init; } = [];
}

/// <summary>
/// A suggested change from governance negotiation with impact metrics.
/// </summary>
public class SuggestedChange
{
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("pillar")]
    public required string Pillar { get; init; }

    [JsonPropertyName("cost_delta")]
    public decimal CostDelta { get; init; }

    [JsonPropertyName("risk_delta")]
    public double RiskDelta { get; init; }

    [JsonPropertyName("sla_delta")]
    public double SlaDelta { get; init; }

    [JsonPropertyName("source_agent")]
    public required string SourceAgent { get; init; }
}

/// <summary>
/// Projected score impact from governance mediation.
/// </summary>
public class ScoreImpactProjection
{
    [JsonPropertyName("current_overall")]
    public double CurrentOverall { get; init; }

    [JsonPropertyName("projected_overall")]
    public double ProjectedOverall { get; init; }

    [JsonPropertyName("pillar_deltas")]
    public Dictionary<string, double> PillarDeltas { get; init; } = [];
}

/// <summary>
/// Priority levels for A2A messages
/// </summary>
public static class A2APriority
{
    public const string Low = "low";
    public const string Normal = "normal";
    public const string High = "high";
    public const string Critical = "critical";
}
