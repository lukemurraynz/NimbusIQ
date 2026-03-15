namespace Atlas.ControlPlane.Domain.Entities;

public class ServiceGroup
{
    public Guid Id { get; set; }
    public required string ExternalKey { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? BusinessOwner { get; set; }
    public string? SloProfile { get; set; } // JSON
    public string? Tags { get; set; } // JSON
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // T2.1: Hierarchy support
    public Guid? ParentServiceGroupId { get; set; }
    public int HierarchyLevel { get; set; } = 0; // 0 = root, 1 = child, 2 = grandchild, etc.
    public string? HierarchyPath { get; set; } // e.g., "root/app/service" for efficient querying

    // Navigation properties
    public ServiceGroup? ParentServiceGroup { get; set; }
    public ICollection<ServiceGroup> ChildServiceGroups { get; set; } = new List<ServiceGroup>();
    public ICollection<ServiceGroupHierarchy> ParentRelationships { get; set; } = new List<ServiceGroupHierarchy>();
    public ICollection<ServiceGroupHierarchy> ChildRelationships { get; set; } = new List<ServiceGroupHierarchy>();
    public ICollection<ServiceGroupScope> Scopes { get; set; } = new List<ServiceGroupScope>();
    public ICollection<DiscoverySnapshot> Snapshots { get; set; } = new List<DiscoverySnapshot>();
    public ICollection<AnalysisRun> AnalysisRuns { get; set; } = new List<AnalysisRun>();
    public ICollection<BestPracticeViolation> Violations { get; set; } = new List<BestPracticeViolation>();
    public ICollection<ComplianceAssessment> ComplianceAssessments { get; set; } = new List<ComplianceAssessment>();
    public ICollection<DriftSnapshot> DriftSnapshots { get; set; } = new List<DriftSnapshot>();
}

public class ServiceGroupScope
{
    public Guid Id { get; set; }
    public Guid ServiceGroupId { get; set; }
    public required string SubscriptionId { get; set; }
    public string? ResourceGroup { get; set; }
    public string? ScopeFilter { get; set; } // JSON
    public DateTime CreatedAt { get; set; }

    public ServiceGroup ServiceGroup { get; set; } = null!;
}

public class DiscoverySnapshot
{
    public Guid Id { get; set; }
    public Guid ServiceGroupId { get; set; }
    public Guid? AnalysisRunId { get; set; }
    public Guid CorrelationId { get; set; }
    public DateTimeOffset SnapshotTime { get; set; }
    public required string InventoryHash { get; set; }
    public string? ResourceInventory { get; set; } // JSON
    public string? TelemetryHealth { get; set; } // JSON
    public string? DependencyGraph { get; set; } // JSON
    public string? SlaContext { get; set; } // JSON
    public required string Status { get; set; } // completed|partial|failed
    public int ResourceCount { get; set; }
    public int DependencyCount { get; set; }
    public int AnomalyCount { get; set; }
    public string? CapturedBy { get; set; }
    public DateTime CreatedAt { get; set; }

    public ServiceGroup ServiceGroup { get; set; } = null!;
    public AnalysisRun? AnalysisRun { get; set; }
    public ICollection<DiscoveredResource> Resources { get; set; } = new List<DiscoveredResource>();
}

public class DiscoveredResource
{
    public Guid Id { get; set; }
    public Guid SnapshotId { get; set; }
    public required string AzureResourceId { get; set; }
    public required string ResourceType { get; set; }
    public required string ResourceName { get; set; }
    public string? Region { get; set; }
    public string? Sku { get; set; }
    public string? Metadata { get; set; } // JSON
    public required string TelemetryState { get; set; } // healthy|degraded|missing
    public DateTime CreatedAt { get; set; }

    public DiscoverySnapshot Snapshot { get; set; } = null!;
}

public class AnalysisRun
{
    public Guid Id { get; set; }
    public Guid ServiceGroupId { get; set; }
    public Guid? SnapshotId { get; set; }
    public Guid CorrelationId { get; set; }
    public required string TriggeredBy { get; set; }
    public required string Status { get; set; } // queued|running|completed|partial|failed|cancelled
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public ServiceGroup ServiceGroup { get; set; } = null!;
    public DiscoverySnapshot? Snapshot { get; set; }
    public ICollection<AgentMessage> Messages { get; set; } = new List<AgentMessage>();
    public ICollection<Recommendation> Recommendations { get; set; } = new List<Recommendation>();
}

public class AgentMessage
{
    public Guid Id { get; set; }
    public Guid AnalysisRunId { get; set; }
    public Guid MessageId { get; set; }
    public Guid? ParentMessageId { get; set; }
    public required string AgentName { get; set; }
    public required string AgentRole { get; set; } // observer|proposer|mediator|executor
    public required string MessageType { get; set; }
    public string? Payload { get; set; } // JSON
    public string? EvidenceRefs { get; set; } // JSON
    public decimal? Confidence { get; set; }
    public DateTime CreatedAt { get; set; }

    public AnalysisRun AnalysisRun { get; set; } = null!;
}

public class Recommendation
{
    public Guid Id { get; set; }
    public Guid ServiceGroupId { get; set; } // T028-T030: Direct reference to ServiceGroup
    public Guid CorrelationId { get; set; }
    public Guid AnalysisRunId { get; set; }
    public required string ResourceId { get; set; }
    public required string Category { get; set; } // T028: Architecture|FinOps|Reliability|Sustainability
    public required string RecommendationType { get; set; } // modernize|cost|sustainability|reliability|governance
    public required string ActionType { get; set; } // upgrade|migrate|optimize|deprecate
    public required string TargetEnvironment { get; set; } // prod
    public required string Title { get; set; }
    public required string Description { get; set; }
    public required string Rationale { get; set; } // T028: Why this recommendation
    public required string Impact { get; set; } // T028: Expected impact
    public required string ProposedChanges { get; set; } // T028: What will change
    public string? TriggerReason { get; set; } // rule_violation|cost_anomaly|drift_detected|advisor|policy
    public string? ChangeContext { get; set; } // JSON — what triggered this recommendation
    public required string Summary { get; set; }
    public decimal Confidence { get; set; } // T028: 0.0-1.0 confidence score
    public string? ConfidenceSource { get; set; } // ai_foundry|rule_engine|heuristic|composite
    public string? EstimatedImpact { get; set; } // JSON
    public string? TradeoffProfile { get; set; } // JSON
    public string? RiskProfile { get; set; } // JSON
    public string? ImpactedServices { get; set; } // JSON
    public string? EvidenceReferences { get; set; } // JSON
    public required string ApprovalMode { get; set; } // single|dual
    public short RequiredApprovals { get; set; }
    public short ReceivedApprovals { get; set; }
    public required string Status { get; set; } // T029: pending|pending_second_approval|approved|rejected
    public required string Priority { get; set; } // low|medium|high|critical
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ValidUntil { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public List<string> Warnings { get; set; } = new List<string>(); // T030: Medium confidence warnings

    // Approval tracking
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovalComments { get; set; }

    // Rejection tracking
    public string? RejectedBy { get; set; }
    public DateTime? RejectedAt { get; set; }
    public string? RejectionReason { get; set; }

    public ServiceGroup ServiceGroup { get; set; } = null!; // T028: Navigation to ServiceGroup
    public AnalysisRun AnalysisRun { get; set; } = null!;
    public ICollection<ApprovalDecision> Approvals { get; set; } = new List<ApprovalDecision>();
    public ICollection<RecommendationDecision> Decisions { get; set; } = new List<RecommendationDecision>(); // T029-T030: Dual-control decisions
    public ICollection<IacChangeSet> ChangeSets { get; set; } = new List<IacChangeSet>();
}

public class ApprovalDecision
{
    public Guid Id { get; set; }
    public Guid RecommendationId { get; set; }
    public Guid DecisionSetId { get; set; }
    public required string Decision { get; set; } // approve|modify|defer|escalate|reject
    public string? DecisionNotes { get; set; }
    public string? OverridePayload { get; set; } // JSON
    public required string DecidedBy { get; set; }
    public string? ApproverRole { get; set; }
    public DateTime DecidedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public Recommendation Recommendation { get; set; } = null!;
}

public class IacChangeSet
{
    public Guid Id { get; set; }
    public Guid RecommendationId { get; set; }
    public required string Format { get; set; } // terraform|bicep|mixed
    public required string ArtifactUri { get; set; }
    public required string PrTitle { get; set; }
    public required string PrDescription { get; set; }
    public string? ValidationResult { get; set; } // JSON
    public required string Status { get; set; } // generated|validated|failed|published
    public DateTime CreatedAt { get; set; }

    public Recommendation Recommendation { get; set; } = null!;
    public ICollection<ReleaseAttestation> Attestations { get; set; } = new List<ReleaseAttestation>();
    public RollbackPlan? RollbackPlan { get; set; }
}

public class ReleaseAttestation
{
    public Guid Id { get; set; }
    public Guid IacChangeSetId { get; set; }
    public required string ReleaseId { get; set; }
    public required string ComponentName { get; set; }
    public required string ComponentVersion { get; set; }
    public required string AttestationType { get; set; } // real_dependency_validation|policy_gate|canary_health|rollback_event
    public required string ValidationMode { get; set; } // real_only
    public required string MockDetectionResult { get; set; } // passed|failed
    public string? MockDetectionDetails { get; set; }
    public bool ValidationPassed { get; set; }
    public string? PromotionBlockReason { get; set; }
    public string? ValidationScopeId { get; set; }
    public required string AttestedBy { get; set; }
    public DateTime AttestedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public IacChangeSet ChangeSet { get; set; } = null!;
}

public class RollbackPlan
{
    public Guid Id { get; set; }
    public Guid IacChangeSetId { get; set; }
    public string? RollbackSteps { get; set; } // JSON
    public string? Preconditions { get; set; } // JSON
    public string? ValidationSteps { get; set; } // JSON
    public DateTime CreatedAt { get; set; }

    public IacChangeSet ChangeSet { get; set; } = null!;
}

public class TimelineEvent
{
    public Guid Id { get; set; }
    public Guid ServiceGroupId { get; set; }
    public Guid? AnalysisRunId { get; set; }
    public required string EventType { get; set; }
    public string? EventCategory { get; set; } // analysis|drift|recommendation|governance|deployment
    public DateTime EventTime { get; set; }
    public string? EventPayload { get; set; } // JSON
    public double? ScoreImpact { get; set; } // delta on overall WAF score
    public string? DeltaSummary { get; set; } // human-readable change summary
    public DateTime CreatedAt { get; set; }

    public ServiceGroup ServiceGroup { get; set; } = null!;
    public AnalysisRun? AnalysisRun { get; set; }
}

public class AuditEvent
{
    public Guid Id { get; set; }
    public Guid CorrelationId { get; set; }
    public required string ActorType { get; set; } // user|system|agent
    public required string ActorId { get; set; }
    public required string EventName { get; set; }
    public string? EventPayload { get; set; } // JSON
    public string? TraceId { get; set; }
    public DateTime CreatedAt { get; set; }

    // Query support properties (align with application usage)
    public string? EventType { get; set; } // Alias for EventName for backward compatibility
    public string? EntityType { get; set; } // Extracted from EventPayload or set explicitly
    public string? EntityId { get; set; } // Extracted from EventPayload or set explicitly
    public string? UserId { get; set; } // Alias for ActorId when ActorType is user
    public DateTime Timestamp { get; set; } // Alias for CreatedAt for query compatibility
}

// Service Knowledge Graph entities
public class ServiceNode
{
    public Guid Id { get; set; }
    public Guid ServiceGroupId { get; set; }
    public required string NodeType { get; set; } // logical_service|resource|workload|data_store|network_boundary
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public string? AzureResourceId { get; set; }
    public string? Properties { get; set; } // JSON - flexible metadata
    public string? Tags { get; set; } // JSON
    public decimal? MonthlyCost { get; set; }
    public string? ReliabilityScore { get; set; } // JSON with {score, confidence}
    public string? PerformanceMetrics { get; set; } // JSON
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ServiceGroup ServiceGroup { get; set; } = null!;
    public ICollection<ServiceEdge> OutgoingEdges { get; set; } = new List<ServiceEdge>();
    public ICollection<ServiceEdge> IncomingEdges { get; set; } = new List<ServiceEdge>();
}

public class ServiceEdge
{
    public Guid Id { get; set; }
    public Guid ServiceGroupId { get; set; }
    public Guid SourceNodeId { get; set; }
    public Guid TargetNodeId { get; set; }
    public required string EdgeType { get; set; } // depends_on|network_flow|data_flow|owns|contains
    public string? Protocol { get; set; }
    public string? Direction { get; set; } // inbound|outbound|bidirectional
    public decimal? ConfidenceScore { get; set; }
    public string? Properties { get; set; } // JSON - flexible metadata
    public DateTime CreatedAt { get; set; }

    public ServiceGroup ServiceGroup { get; set; } = null!;
    public ServiceNode SourceNode { get; set; } = null!;
    public ServiceNode TargetNode { get; set; } = null!;
}

public class ServiceDomain
{
    public Guid Id { get; set; }
    public Guid ServiceGroupId { get; set; }
    public required string DomainType { get; set; } // cost_center|reliability_zone|compliance_boundary|ownership
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? Policies { get; set; } // JSON
    public string? Owners { get; set; } // JSON - list of owners
    public DateTime CreatedAt { get; set; }

    public ServiceGroup ServiceGroup { get; set; } = null!;
    public ICollection<ServiceDomainMembership> Memberships { get; set; } = new List<ServiceDomainMembership>();
}

public class ServiceDomainMembership
{
    public Guid Id { get; set; }
    public Guid DomainId { get; set; }
    public Guid NodeId { get; set; }
    public DateTime CreatedAt { get; set; }

    public ServiceDomain Domain { get; set; } = null!;
    public ServiceNode Node { get; set; } = null!;
}

public class ServiceGraphSnapshot
{
    public Guid Id { get; set; }
    public Guid ServiceGroupId { get; set; }
    public Guid? AnalysisRunId { get; set; }
    public required string Version { get; set; }
    public DateTime SnapshotTime { get; set; }
    public int NodeCount { get; set; }
    public int EdgeCount { get; set; }
    public int DomainCount { get; set; }
    public string? GraphMetrics { get; set; } // JSON - graph analysis metrics
    public DateTime CreatedAt { get; set; }

    public ServiceGroup ServiceGroup { get; set; } = null!;
    public AnalysisRun? AnalysisRun { get; set; }
}

// T029-T030: Recommendation decision tracking for dual-control approval
public class RecommendationDecision
{
    public Guid Id { get; set; }
    public Guid RecommendationId { get; set; }
    public required string Decision { get; set; } // approved|rejected
    public required string Rationale { get; set; }
    public required string SubmittedBy { get; set; }
    public DateTime SubmittedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public Recommendation Recommendation { get; set; } = null!;
}

// Best Practice Framework - T1.1: Rule Definition with Multi-Source Support
public class BestPracticeRule
{
    public Guid Id { get; set; }
    public required string RuleId { get; set; } // Unique identifier (e.g., WAF-SEC-001, PSRULE-AKS-001)
    public required string Source { get; set; } // WAF|PSRule|AzureQuickReview|ArchitectureCenter|Custom
    public required string Category { get; set; } // Security|Reliability|Performance|Cost|Operations|Sustainability
    public required string Pillar { get; set; } // WAF pillar: Security|Reliability|PerformanceEfficiency|CostOptimization|OperationalExcellence
    public required string Name { get; set; }
    public required string Description { get; set; }
    public string? Rationale { get; set; }
    public required string Severity { get; set; } // Critical|High|Medium|Low|Informational
    public required string ApplicabilityScope { get; set; } // ResourceType filter (JSON array)
    public string? ApplicabilityCriteria { get; set; } // KQL or JSON logic for when rule applies
    public required string EvaluationQuery { get; set; } // KQL query or evaluation logic
    public string? RemediationGuidance { get; set; } // Detailed remediation steps
    public string? RemediationIac { get; set; } // Sample Bicep/Terraform code
    public string? References { get; set; } // JSON array of documentation URLs
    public bool IsEnabled { get; set; } = true;
    public DateTime? EffectiveFrom { get; set; }
    public DateTime? DeprecatedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<BestPracticeViolation> Violations { get; set; } = new List<BestPracticeViolation>();
}

// Best Practice Violation - T1.3: Drift Detection
public class BestPracticeViolation
{
    public Guid Id { get; set; }
    public Guid RuleId { get; set; }
    public Guid ServiceGroupId { get; set; }
    public Guid? AnalysisRunId { get; set; }
    public required string ResourceId { get; set; } // Azure Resource ID
    public required string ResourceType { get; set; }
    public required string ViolationType { get; set; } // drift|non_compliance|warning
    public string? DriftCategory { get; set; } // ConfigurationDrift|CostDrift|ComplianceDrift|PerformanceDrift|SecurityDrift
    public required string Severity { get; set; } // Inherited from rule but can be overridden
    public required string CurrentState { get; set; } // JSON - actual state
    public required string ExpectedState { get; set; } // JSON - desired state
    public string? DriftDetails { get; set; } // JSON - detailed drift analysis
    public DateTime DetectedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolutionNotes { get; set; }
    public required string Status { get; set; } // active|resolved|acknowledged|waived
    public DateTime CreatedAt { get; set; }

    public BestPracticeRule Rule { get; set; } = null!;
    public ServiceGroup ServiceGroup { get; set; } = null!;
    public AnalysisRun? AnalysisRun { get; set; }
}

// Service Hierarchy - T2.1: Parent-Child Relationships
public class ServiceGroupHierarchy
{
    public Guid Id { get; set; }
    public Guid ParentServiceGroupId { get; set; }
    public Guid ChildServiceGroupId { get; set; }
    public required string RelationshipType { get; set; } // contains|depends_on|part_of|consumes
    public int Level { get; set; } // Depth in hierarchy (0 = root, 1 = child, 2 = grandchild, etc.)
    public DateTime CreatedAt { get; set; }

    public ServiceGroup ParentServiceGroup { get; set; } = null!;
    public ServiceGroup ChildServiceGroup { get; set; } = null!;
}

// Compliance Assessment - T3.1: Well-Architected Framework Assessment
public class ComplianceAssessment
{
    public Guid Id { get; set; }
    public Guid ServiceGroupId { get; set; }
    public Guid? AnalysisRunId { get; set; }
    public required string FrameworkType { get; set; } // WAF|ISO27001|SOC2|HIPAA|PCI-DSS|Custom
    public required string FrameworkVersion { get; set; }
    public DateTime AssessmentDate { get; set; }
    public decimal OverallScore { get; set; } // 0-100
    public decimal CompliancePercentage { get; set; } // % of rules passing
    public int TotalRules { get; set; }
    public int PassedRules { get; set; }
    public int FailedRules { get; set; }
    public int WaivedRules { get; set; }
    public string? PillarScores { get; set; } // JSON - scores per pillar
    public string? AssessmentSummary { get; set; } // JSON - detailed findings
    public DateTime CreatedAt { get; set; }

    public ServiceGroup ServiceGroup { get; set; } = null!;
    public AnalysisRun? AnalysisRun { get; set; }
}

// Drift Timeline - T1.3: Historical Drift Tracking
public class DriftSnapshot
{
    public Guid Id { get; set; }
    public Guid ServiceGroupId { get; set; }
    public DateTime SnapshotTime { get; set; }
    public int TotalViolations { get; set; }
    public int CriticalViolations { get; set; }
    public int HighViolations { get; set; }
    public int MediumViolations { get; set; }
    public int LowViolations { get; set; }
    public decimal DriftScore { get; set; } // 0-100, lower is better (less drift)
    public string? CategoryBreakdown { get; set; } // JSON - violations by category
    public string? TrendAnalysis { get; set; } // JSON - comparison to previous snapshot
    public DateTime CreatedAt { get; set; }

    public ServiceGroup ServiceGroup { get; set; } = null!;
}

// Agent Collaboration - T4.1: Multi-Agent Orchestration
public class AgentCollaborationSession
{
    public Guid Id { get; set; }
    public Guid AnalysisRunId { get; set; }
    public required string SessionType { get; set; } // analysis|recommendation|remediation
    public required string PrimaryAgent { get; set; }
    public string? ParticipatingAgents { get; set; } // JSON array
    public string? CollaborationProtocol { get; set; } // concurrent-mediator|leader|sequential
    public required string Status { get; set; } // active|completed|failed
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Outcome { get; set; } // JSON - collaboration results
    public DateTime CreatedAt { get; set; }

    public AnalysisRun AnalysisRun { get; set; } = null!;
    public ICollection<AgentMessage> Messages { get; set; } = new List<AgentMessage>();
}

// Cloud Native Maturity - T5.1: Modern Architecture Assessment
public class CloudNativeMaturityAssessment
{
    public Guid Id { get; set; }
    public Guid ServiceGroupId { get; set; }
    public Guid? AnalysisRunId { get; set; }
    public decimal ContainerizationScore { get; set; } // 0-100
    public decimal MicroservicesScore { get; set; } // 0-100
    public decimal ObservabilityScore { get; set; } // 0-100
    public decimal ServiceMeshReadiness { get; set; } // 0-100
    public decimal OverallMaturityScore { get; set; } // 0-100
    public required string MaturityLevel { get; set; } // Level1|Level2|Level3|Level4|Level5
    public string? Findings { get; set; } // JSON - detailed findings per dimension
    public string? Recommendations { get; set; } // JSON - improvement recommendations
    public DateTime AssessedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public ServiceGroup ServiceGroup { get; set; } = null!;
    public AnalysisRun? AnalysisRun { get; set; }
}

// Sustainability Assessment - T5.2: GreenOps
public class SustainabilityAssessment
{
    public Guid Id { get; set; }
    public Guid ServiceGroupId { get; set; }
    public Guid? AnalysisRunId { get; set; }
    public decimal EstimatedCarbonFootprint { get; set; } // kgCO2e per month
    public decimal GreenRegionPercentage { get; set; } // % of resources in green regions
    public decimal EnergyEfficiencyScore { get; set; } // 0-100
    public required string PrimaryDatacenterRegion { get; set; }
    public bool UsesGreenEnergy { get; set; }
    public string? OptimizationOpportunities { get; set; } // JSON - green SKU suggestions
    public string? CarbonReductionPotential { get; set; } // JSON - potential savings
    public DateTime AssessedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public ServiceGroup ServiceGroup { get; set; } = null!;
    public AnalysisRun? AnalysisRun { get; set; }
}

// Score History — time-series snapshots for score explainability and trend analysis
public class ScoreSnapshot
{
    public Guid Id { get; set; }
    public Guid ServiceGroupId { get; set; }
    public Guid? AnalysisRunId { get; set; }
    public required string Category { get; set; } // Architecture|FinOps|Reliability|Sustainability
    public double Score { get; set; } // 0-100
    public double Confidence { get; set; } // 0.0-1.0
    public string? Dimensions { get; set; } // JSON — sub-dimension breakdown
    public string? DeltaFromPrevious { get; set; } // JSON — {scoreDelta, dimensionDeltas}
    public int ResourceCount { get; set; }
    public DateTime RecordedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public ServiceGroup ServiceGroup { get; set; } = null!;
    public AnalysisRun? AnalysisRun { get; set; }
}

// Feature #1: ROI & Value Tracking — Track actual vs estimated impact
public class ValueRealizationTracking
{
    public Guid Id { get; set; }
    public Guid RecommendationId { get; set; }
    public Guid? ChangeSetId { get; set; }
    public decimal EstimatedMonthlySavings { get; set; }
    public decimal ActualMonthlySavings { get; set; }
    public decimal EstimatedImplementationCost { get; set; }
    public decimal ActualImplementationCost { get; set; }
    public DateTime EstimatedPaybackDate { get; set; }
    public DateTime? ActualPaybackDate { get; set; }
    public required string Status { get; set; } // pending|measuring|realized|failed
    public DateTime BaselineRecordedAt { get; set; }
    public DateTime? FirstMeasurementAt { get; set; }
    public DateTime? PaybackAchievedAt { get; set; }
    public string? MeasurementNotes { get; set; } // JSON — monthly snapshots
    public string? VarianceAnalysis { get; set; } // Why actual differs from estimated
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Recommendation Recommendation { get; set; } = null!;
    public IacChangeSet? ChangeSet { get; set; }
}

// Feature #2: Automated Remediation — Auto-execute low-risk changes
public class AutomationRule
{
    public Guid Id { get; set; }
    public required string RuleName { get; set; }
    public required string Trigger { get; set; } // recommendation_created|score_threshold|schedule
    public string? TriggerCriteria { get; set; } // JSON — conditions to match
    public decimal MaxRiskThreshold { get; set; } // Auto-approve only if risk <= this
    public decimal MinConfidenceThreshold { get; set; } // And confidence >= this
    public required string ActionType { get; set; } // auto_approve|auto_implement|notify
    public string? ImplementationSchedule { get; set; } // cron expression for deferred execution
    public string? ApprovalBypass { get; set; } // JSON — bypass conditions
    public bool RequiresAttestation { get; set; }
    public bool IsEnabled { get; set; }
    public int ExecutionCount { get; set; }
    public DateTime? LastExecutedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<AutomationExecution> Executions { get; set; } = new List<AutomationExecution>();
}

public class AutomationExecution
{
    public Guid Id { get; set; }
    public Guid AutomationRuleId { get; set; }
    public Guid RecommendationId { get; set; }
    public required string Status { get; set; } // queued|running|succeeded|failed|rolled_back
    public DateTime TriggeredAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ExecutionLog { get; set; } // JSON — detailed execution trace
    public string? ErrorDetails { get; set; }
    public bool RollbackTriggered { get; set; }
    public DateTime CreatedAt { get; set; }

    public AutomationRule AutomationRule { get; set; } = null!;
    public Recommendation Recommendation { get; set; } = null!;
}

// Feature #4: Recommendation Templates — Reusable solution patterns
public class RecommendationTemplate
{
    public Guid Id { get; set; }
    public required string TemplateName { get; set; }
    public required string Category { get; set; } // Architecture|FinOps|Reliability|Sustainability
    public required string ProblemPattern { get; set; } // Description of common problem
    public required string SolutionPattern { get; set; } // Standard solution approach
    public string? ApplicabilityCriteria { get; set; } // JSON — when to use this template
    public required string IacTemplate { get; set; } // Bicep/Terraform template with placeholders
    public string? ParameterSchema { get; set; } // JSON schema for required parameters
    public string? PreConditions { get; set; } // JSON — what must be true before applying
    public string? PostConditions { get; set; } // JSON — what to verify after applying
    public decimal EstimatedSavingsRange { get; set; } // Typical savings ($)
    public decimal TypicalRiskScore { get; set; } // Historical risk level
    public int UsageCount { get; set; }
    public decimal AverageSuccessRate { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<TemplateUsage> Usages { get; set; } = new List<TemplateUsage>();
}

public class TemplateUsage
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public Guid RecommendationId { get; set; }
    public required string AppliedBy { get; set; }
    public DateTime AppliedAt { get; set; }
    public string? ParameterValues { get; set; } // JSON — actual parameters used
    public required string Outcome { get; set; } // pending|succeeded|failed|rolled_back
    public string? FeedbackNotes { get; set; }
    public DateTime CreatedAt { get; set; }

    public RecommendationTemplate Template { get; set; } = null!;
    public Recommendation Recommendation { get; set; } = null!;
}

// Feature #5: GitOps Auto-PR Integration — Auto-create PRs for approved changes
public class GitOpsPullRequest
{
    public Guid Id { get; set; }
    public Guid RecommendationId { get; set; }
    public Guid ChangeSetId { get; set; }
    public required string RepositoryUrl { get; set; }
    public required string PullRequestUrl { get; set; }
    public int PullRequestNumber { get; set; }
    public required string BranchName { get; set; }
    public required string Status { get; set; } // created|open|merged|closed|failed
    public required string TargetBranch { get; set; }
    public bool AutoMergeEnabled { get; set; }
    public string? Reviewers { get; set; } // JSON array of reviewer IDs
    public string? Labels { get; set; } // JSON array of PR labels
    public DateTime CreatedAt { get; set; }
    public DateTime? MergedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string? MergeCommitSha { get; set; }
    public string? CiCheckStatus { get; set; } // pending|passed|failed
    public DateTime UpdatedAt { get; set; }

    public Recommendation Recommendation { get; set; } = null!;
    public IacChangeSet ChangeSet { get; set; } = null!;
}
