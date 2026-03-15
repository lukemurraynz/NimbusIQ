using Atlas.ControlPlane.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Atlas.ControlPlane.Infrastructure.Data;

public class AtlasDbContext : DbContext
{
    public AtlasDbContext(DbContextOptions<AtlasDbContext> options) : base(options)
    {
    }

    public DbSet<ServiceGroup> ServiceGroups { get; set; }
    public DbSet<ServiceGroupScope> ServiceGroupScopes { get; set; }
    public DbSet<DiscoverySnapshot> DiscoverySnapshots { get; set; }
    public DbSet<DiscoveredResource> DiscoveredResources { get; set; }
    public DbSet<AnalysisRun> AnalysisRuns { get; set; }
    public DbSet<AgentMessage> AgentMessages { get; set; }
    public DbSet<Recommendation> Recommendations { get; set; }
    public DbSet<ApprovalDecision> ApprovalDecisions { get; set; }
    public DbSet<IacChangeSet> IacChangeSets { get; set; }
    public DbSet<ReleaseAttestation> ReleaseAttestations { get; set; }
    public DbSet<RollbackPlan> RollbackPlans { get; set; }
    public DbSet<TimelineEvent> TimelineEvents { get; set; }
    public DbSet<AuditEvent> AuditEvents { get; set; }

    // Service Knowledge Graph
    public DbSet<ServiceNode> ServiceNodes { get; set; }
    public DbSet<ServiceEdge> ServiceEdges { get; set; }
    public DbSet<ServiceDomain> ServiceDomains { get; set; }
    public DbSet<ServiceDomainMembership> ServiceDomainMemberships { get; set; }
    public DbSet<ServiceGraphSnapshot> ServiceGraphSnapshots { get; set; }

    // Drift and best-practice tracking
    public DbSet<DriftSnapshot> DriftSnapshots { get; set; }
    public DbSet<BestPracticeRule> BestPracticeRules { get; set; }
    public DbSet<BestPracticeViolation> BestPracticeViolations { get; set; }

    // Score history time-series
    public DbSet<ScoreSnapshot> ScoreSnapshots { get; set; }

    // ROI & Value Tracking (Feature #1)
    public DbSet<ValueRealizationTracking> ValueRealizations { get; set; }

    // Automated Remediation (Feature #2)
    public DbSet<AutomationRule> AutomationRules { get; set; }
    public DbSet<AutomationExecution> AutomationExecutions { get; set; }

    // Recommendation Templates (Feature #4)
    public DbSet<RecommendationTemplate> RecommendationTemplates { get; set; }
    public DbSet<TemplateUsage> TemplateUsages { get; set; }

    // GitOps Auto-PR Integration (Feature #5)
    public DbSet<GitOpsPullRequest> GitOpsPullRequests { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ServiceGroup
        modelBuilder.Entity<ServiceGroup>(entity =>
        {
            entity.ToTable("service_groups");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ExternalKey).IsRequired().HasMaxLength(255);
            entity.HasIndex(e => e.ExternalKey).IsUnique();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();

            // Self-referencing hierarchy (ParentServiceGroupId)
            entity.HasOne(e => e.ParentServiceGroup)
                .WithMany(e => e.ChildServiceGroups)
                .HasForeignKey(e => e.ParentServiceGroupId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ServiceGroupScope
        modelBuilder.Entity<ServiceGroupScope>(entity =>
        {
            entity.ToTable("service_group_scopes");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SubscriptionId).IsRequired().HasMaxLength(255);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasOne(e => e.ServiceGroup)
                .WithMany(sg => sg.Scopes)
                .HasForeignKey(e => e.ServiceGroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ServiceGroupHierarchy (T2.1) - explicit mapping required due to dual ServiceGroup navigations
        modelBuilder.Entity<ServiceGroupHierarchy>(entity =>
        {
            entity.ToTable("service_group_hierarchy");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RelationshipType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.CreatedAt).IsRequired();

            entity.HasIndex(e => new { e.ParentServiceGroupId, e.ChildServiceGroupId, e.RelationshipType })
                .IsUnique();

            // Parent -> children
            entity.HasOne(e => e.ParentServiceGroup)
                .WithMany(sg => sg.ChildRelationships)
                .HasForeignKey(e => e.ParentServiceGroupId)
                .OnDelete(DeleteBehavior.Restrict);

            // Child -> parents
            entity.HasOne(e => e.ChildServiceGroup)
                .WithMany(sg => sg.ParentRelationships)
                .HasForeignKey(e => e.ChildServiceGroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // DiscoverySnapshot
        modelBuilder.Entity<DiscoverySnapshot>(entity =>
        {
            entity.ToTable("discovery_snapshots");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CorrelationId).IsRequired();
            entity.Property(e => e.SnapshotTime).IsRequired();
            entity.Property(e => e.InventoryHash).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasOne(e => e.ServiceGroup)
                .WithMany(sg => sg.Snapshots)
                .HasForeignKey(e => e.ServiceGroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // DiscoveredResource
        modelBuilder.Entity<DiscoveredResource>(entity =>
        {
            entity.ToTable("discovered_resources");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.AzureResourceId).IsRequired().HasMaxLength(1024);
            entity.Property(e => e.ResourceType).IsRequired().HasMaxLength(255);
            entity.Property(e => e.ResourceName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.TelemetryState).IsRequired().HasMaxLength(50);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasOne(e => e.Snapshot)
                .WithMany(s => s.Resources)
                .HasForeignKey(e => e.SnapshotId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AnalysisRun
        modelBuilder.Entity<AnalysisRun>(entity =>
        {
            entity.ToTable("analysis_runs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CorrelationId).IsRequired();
            entity.HasIndex(e => e.CorrelationId).IsUnique();
            entity.Property(e => e.TriggeredBy).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasOne(e => e.ServiceGroup)
                .WithMany(sg => sg.AnalysisRuns)
                .HasForeignKey(e => e.ServiceGroupId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Snapshot)
                .WithMany()
                .HasForeignKey(e => e.SnapshotId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // AgentMessage
        modelBuilder.Entity<AgentMessage>(entity =>
        {
            entity.ToTable("agent_messages");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.MessageId).IsRequired();
            entity.HasIndex(e => e.MessageId).IsUnique();
            entity.Property(e => e.AgentName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.AgentRole).IsRequired().HasMaxLength(50);
            entity.Property(e => e.MessageType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Confidence).HasPrecision(5, 4);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasOne(e => e.AnalysisRun)
                .WithMany(ar => ar.Messages)
                .HasForeignKey(e => e.AnalysisRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Recommendation
        modelBuilder.Entity<Recommendation>(entity =>
        {
            entity.ToTable("recommendations");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RecommendationType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.TargetEnvironment).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Summary).IsRequired();
            entity.Property(e => e.Confidence).IsRequired().HasPrecision(5, 4);
            entity.Property(e => e.ConfidenceSource).HasMaxLength(50);
            entity.Property(e => e.ApprovalMode).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.HasOne(e => e.AnalysisRun)
                .WithMany(ar => ar.Recommendations)
                .HasForeignKey(e => e.AnalysisRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ApprovalDecision
        modelBuilder.Entity<ApprovalDecision>(entity =>
        {
            entity.ToTable("approval_decisions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DecisionSetId).IsRequired();
            entity.Property(e => e.Decision).IsRequired().HasMaxLength(50);
            entity.Property(e => e.DecidedBy).IsRequired().HasMaxLength(255);
            entity.Property(e => e.DecidedAt).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasOne(e => e.Recommendation)
                .WithMany(r => r.Approvals)
                .HasForeignKey(e => e.RecommendationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // IacChangeSet
        modelBuilder.Entity<IacChangeSet>(entity =>
        {
            entity.ToTable("iac_change_sets");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Format).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ArtifactUri).IsRequired();
            entity.Property(e => e.PrTitle).IsRequired().HasMaxLength(500);
            entity.Property(e => e.PrDescription).IsRequired();
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasOne(e => e.Recommendation)
                .WithMany(r => r.ChangeSets)
                .HasForeignKey(e => e.RecommendationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ReleaseAttestation
        modelBuilder.Entity<ReleaseAttestation>(entity =>
        {
            entity.ToTable("release_attestations");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ReleaseId).IsRequired().HasMaxLength(255);
            entity.Property(e => e.ComponentName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.ComponentVersion).IsRequired().HasMaxLength(100);
            entity.Property(e => e.AttestationType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ValidationMode).IsRequired().HasMaxLength(50);
            entity.Property(e => e.MockDetectionResult).IsRequired().HasMaxLength(50);
            entity.Property(e => e.AttestedBy).IsRequired().HasMaxLength(255);
            entity.Property(e => e.AttestedAt).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasIndex(e => e.ReleaseId);
            entity.HasOne(e => e.ChangeSet)
                .WithMany(cs => cs.Attestations)
                .HasForeignKey(e => e.IacChangeSetId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // RollbackPlan
        modelBuilder.Entity<RollbackPlan>(entity =>
        {
            entity.ToTable("rollback_plans");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasOne(e => e.ChangeSet)
                .WithOne(cs => cs.RollbackPlan)
                .HasForeignKey<RollbackPlan>(e => e.IacChangeSetId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // TimelineEvent
        modelBuilder.Entity<TimelineEvent>(entity =>
        {
            entity.ToTable("timeline_events");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.EventTime).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasOne(e => e.ServiceGroup)
                .WithMany()
                .HasForeignKey(e => e.ServiceGroupId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.AnalysisRun)
                .WithMany()
                .HasForeignKey(e => e.AnalysisRunId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // AuditEvent
        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.ToTable("audit_events");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CorrelationId).IsRequired();
            entity.HasIndex(e => e.CorrelationId);
            entity.Property(e => e.ActorType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ActorId).IsRequired().HasMaxLength(255);
            entity.Property(e => e.EventName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasIndex(e => e.CreatedAt);

            // Query support fields
            entity.Property(e => e.EventType).HasMaxLength(255);
            entity.Property(e => e.EntityType).HasMaxLength(100);
            entity.Property(e => e.EntityId).HasMaxLength(255);
            entity.Property(e => e.UserId).HasMaxLength(255);
            entity.Property(e => e.Timestamp).IsRequired();
            entity.HasIndex(e => new { e.EntityType, e.EntityId });
            entity.HasIndex(e => e.Timestamp);
        });

        // ServiceNode
        modelBuilder.Entity<ServiceNode>(entity =>
        {
            entity.ToTable("service_nodes");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.NodeType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(500);
            entity.Property(e => e.AzureResourceId).HasMaxLength(1024);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.HasIndex(e => new { e.ServiceGroupId, e.NodeType });
            entity.HasIndex(e => e.AzureResourceId);
            entity.HasOne(e => e.ServiceGroup)
                .WithMany()
                .HasForeignKey(e => e.ServiceGroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ServiceEdge
        modelBuilder.Entity<ServiceEdge>(entity =>
        {
            entity.ToTable("service_edges");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EdgeType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Protocol).HasMaxLength(50);
            entity.Property(e => e.Direction).HasMaxLength(50);
            entity.Property(e => e.ConfidenceScore).HasPrecision(5, 4);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasIndex(e => new { e.ServiceGroupId, e.SourceNodeId });
            entity.HasIndex(e => new { e.ServiceGroupId, e.TargetNodeId });
            entity.HasOne(e => e.ServiceGroup)
                .WithMany()
                .HasForeignKey(e => e.ServiceGroupId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.SourceNode)
                .WithMany(n => n.OutgoingEdges)
                .HasForeignKey(e => e.SourceNodeId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.TargetNode)
                .WithMany(n => n.IncomingEdges)
                .HasForeignKey(e => e.TargetNodeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ServiceDomain
        modelBuilder.Entity<ServiceDomain>(entity =>
        {
            entity.ToTable("service_domains");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DomainType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasIndex(e => new { e.ServiceGroupId, e.DomainType });
            entity.HasOne(e => e.ServiceGroup)
                .WithMany()
                .HasForeignKey(e => e.ServiceGroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ServiceDomainMembership
        modelBuilder.Entity<ServiceDomainMembership>(entity =>
        {
            entity.ToTable("service_domain_memberships");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasIndex(e => new { e.DomainId, e.NodeId }).IsUnique();
            entity.HasOne(e => e.Domain)
                .WithMany(d => d.Memberships)
                .HasForeignKey(e => e.DomainId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Node)
                .WithMany()
                .HasForeignKey(e => e.NodeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ServiceGraphSnapshot
        modelBuilder.Entity<ServiceGraphSnapshot>(entity =>
        {
            entity.ToTable("service_graph_snapshots");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Version).IsRequired().HasMaxLength(50);
            entity.Property(e => e.SnapshotTime).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasIndex(e => new { e.ServiceGroupId, e.SnapshotTime });
            entity.HasOne(e => e.ServiceGroup)
                .WithMany()
                .HasForeignKey(e => e.ServiceGroupId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.AnalysisRun)
                .WithMany()
                .HasForeignKey(e => e.AnalysisRunId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // DriftSnapshot
        modelBuilder.Entity<DriftSnapshot>(entity =>
        {
            entity.ToTable("drift_snapshots");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SnapshotTime).IsRequired();
            entity.Property(e => e.DriftScore).HasPrecision(5, 2);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasIndex(e => new { e.ServiceGroupId, e.SnapshotTime });
            entity.HasOne(e => e.ServiceGroup)
                .WithMany(sg => sg.DriftSnapshots)
                .HasForeignKey(e => e.ServiceGroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // BestPracticeRule
        modelBuilder.Entity<BestPracticeRule>(entity =>
        {
            entity.ToTable("best_practice_rules");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RuleId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Source).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Category).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Pillar).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).IsRequired();
            entity.Property(e => e.Severity).IsRequired().HasMaxLength(20);
            entity.Property(e => e.ApplicabilityScope).IsRequired();
            entity.Property(e => e.EvaluationQuery).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.HasIndex(e => e.RuleId).IsUnique();
        });

        // BestPracticeViolation
        modelBuilder.Entity<BestPracticeViolation>(entity =>
        {
            entity.ToTable("best_practice_violations");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ResourceId).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.ResourceType).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ViolationType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Severity).IsRequired().HasMaxLength(20);
            entity.Property(e => e.CurrentState).IsRequired();
            entity.Property(e => e.ExpectedState).IsRequired();
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.Property(e => e.DetectedAt).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasIndex(e => new { e.ServiceGroupId, e.Status });
            entity.HasOne(e => e.Rule)
                .WithMany(r => r.Violations)
                .HasForeignKey(e => e.RuleId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.ServiceGroup)
                .WithMany(sg => sg.Violations)
                .HasForeignKey(e => e.ServiceGroupId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.AnalysisRun)
                .WithMany()
                .HasForeignKey(e => e.AnalysisRunId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ScoreSnapshot
        modelBuilder.Entity<ScoreSnapshot>(entity =>
        {
            entity.ToTable("score_snapshots");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Category).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Score).IsRequired();
            entity.Property(e => e.Confidence).IsRequired();
            entity.Property(e => e.RecordedAt).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasIndex(e => new { e.ServiceGroupId, e.Category, e.RecordedAt });
            entity.HasOne(e => e.ServiceGroup)
                .WithMany()
                .HasForeignKey(e => e.ServiceGroupId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.AnalysisRun)
                .WithMany()
                .HasForeignKey(e => e.AnalysisRunId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
