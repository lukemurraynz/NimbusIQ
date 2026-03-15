using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.ControlPlane.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    EventName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    EventPayload = table.Column<string>(type: "text", nullable: true),
                    TraceId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "service_groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalKey = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    BusinessOwner = table.Column<string>(type: "text", nullable: true),
                    SloProfile = table.Column<string>(type: "text", nullable: true),
                    Tags = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_groups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "discovery_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    InventoryHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TelemetryHealth = table.Column<string>(type: "text", nullable: true),
                    DependencyGraph = table.Column<string>(type: "text", nullable: true),
                    SlaContext = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discovery_snapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_discovery_snapshots_service_groups_ServiceGroupId",
                        column: x => x.ServiceGroupId,
                        principalTable: "service_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "service_group_scopes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubscriptionId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ResourceGroup = table.Column<string>(type: "text", nullable: true),
                    ScopeFilter = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_group_scopes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_service_group_scopes_service_groups_ServiceGroupId",
                        column: x => x.ServiceGroupId,
                        principalTable: "service_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "analysis_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TriggeredBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analysis_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_analysis_runs_discovery_snapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "discovery_snapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_analysis_runs_service_groups_ServiceGroupId",
                        column: x => x.ServiceGroupId,
                        principalTable: "service_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "discovered_resources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    AzureResourceId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ResourceType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ResourceName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Region = table.Column<string>(type: "text", nullable: true),
                    Sku = table.Column<string>(type: "text", nullable: true),
                    Metadata = table.Column<string>(type: "text", nullable: true),
                    TelemetryState = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discovered_resources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_discovered_resources_discovery_snapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "discovery_snapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AnalysisRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    AgentName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    AgentRole = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    MessageType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: true),
                    EvidenceRefs = table.Column<string>(type: "text", nullable: true),
                    Confidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_messages_analysis_runs_AnalysisRunId",
                        column: x => x.AnalysisRunId,
                        principalTable: "analysis_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "recommendations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AnalysisRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecommendationType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TargetEnvironment = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    TradeoffProfile = table.Column<string>(type: "text", nullable: true),
                    RiskProfile = table.Column<string>(type: "text", nullable: true),
                    ImpactedServices = table.Column<string>(type: "text", nullable: true),
                    Confidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    EvidenceRefs = table.Column<string>(type: "text", nullable: true),
                    ApprovalMode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    RequiredApprovals = table.Column<short>(type: "smallint", nullable: false),
                    ReceivedApprovals = table.Column<short>(type: "smallint", nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recommendations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_recommendations_analysis_runs_AnalysisRunId",
                        column: x => x.AnalysisRunId,
                        principalTable: "analysis_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "timeline_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnalysisRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EventTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EventPayload = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_timeline_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_timeline_events_analysis_runs_AnalysisRunId",
                        column: x => x.AnalysisRunId,
                        principalTable: "analysis_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_timeline_events_service_groups_ServiceGroupId",
                        column: x => x.ServiceGroupId,
                        principalTable: "service_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "approval_decisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecommendationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DecisionSetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Decision = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DecisionNotes = table.Column<string>(type: "text", nullable: true),
                    OverridePayload = table.Column<string>(type: "text", nullable: true),
                    DecidedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ApproverRole = table.Column<string>(type: "text", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_decisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_approval_decisions_recommendations_RecommendationId",
                        column: x => x.RecommendationId,
                        principalTable: "recommendations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "iac_change_sets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecommendationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Format = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ArtifactUri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    PrTitle = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    PrDescription = table.Column<string>(type: "text", nullable: false),
                    ValidationResult = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_iac_change_sets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_iac_change_sets_recommendations_RecommendationId",
                        column: x => x.RecommendationId,
                        principalTable: "recommendations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "release_attestations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IacChangeSetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttestationType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ValidationMode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    MockDetectionResult = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PromotionBlockReason = table.Column<string>(type: "text", nullable: true),
                    ValidationScopeId = table.Column<string>(type: "text", nullable: true),
                    AttestedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    AttestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_attestations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_release_attestations_iac_change_sets_IacChangeSetId",
                        column: x => x.IacChangeSetId,
                        principalTable: "iac_change_sets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rollback_plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IacChangeSetId = table.Column<Guid>(type: "uuid", nullable: false),
                    RollbackSteps = table.Column<string>(type: "text", nullable: true),
                    Preconditions = table.Column<string>(type: "text", nullable: true),
                    ValidationSteps = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rollback_plans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_rollback_plans_iac_change_sets_IacChangeSetId",
                        column: x => x.IacChangeSetId,
                        principalTable: "iac_change_sets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_messages_AnalysisRunId",
                table: "agent_messages",
                column: "AnalysisRunId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_messages_MessageId",
                table: "agent_messages",
                column: "MessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_analysis_runs_CorrelationId",
                table: "analysis_runs",
                column: "CorrelationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_analysis_runs_ServiceGroupId",
                table: "analysis_runs",
                column: "ServiceGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_analysis_runs_SnapshotId",
                table: "analysis_runs",
                column: "SnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_approval_decisions_RecommendationId",
                table: "approval_decisions",
                column: "RecommendationId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_CorrelationId",
                table: "audit_events",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_CreatedAt",
                table: "audit_events",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_discovered_resources_SnapshotId",
                table: "discovered_resources",
                column: "SnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_discovery_snapshots_ServiceGroupId",
                table: "discovery_snapshots",
                column: "ServiceGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_iac_change_sets_RecommendationId",
                table: "iac_change_sets",
                column: "RecommendationId");

            migrationBuilder.CreateIndex(
                name: "IX_recommendations_AnalysisRunId",
                table: "recommendations",
                column: "AnalysisRunId");

            migrationBuilder.CreateIndex(
                name: "IX_release_attestations_IacChangeSetId",
                table: "release_attestations",
                column: "IacChangeSetId");

            migrationBuilder.CreateIndex(
                name: "IX_rollback_plans_IacChangeSetId",
                table: "rollback_plans",
                column: "IacChangeSetId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_service_group_scopes_ServiceGroupId",
                table: "service_group_scopes",
                column: "ServiceGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_service_groups_ExternalKey",
                table: "service_groups",
                column: "ExternalKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_timeline_events_AnalysisRunId",
                table: "timeline_events",
                column: "AnalysisRunId");

            migrationBuilder.CreateIndex(
                name: "IX_timeline_events_ServiceGroupId",
                table: "timeline_events",
                column: "ServiceGroupId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_messages");

            migrationBuilder.DropTable(
                name: "approval_decisions");

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "discovered_resources");

            migrationBuilder.DropTable(
                name: "release_attestations");

            migrationBuilder.DropTable(
                name: "rollback_plans");

            migrationBuilder.DropTable(
                name: "service_group_scopes");

            migrationBuilder.DropTable(
                name: "timeline_events");

            migrationBuilder.DropTable(
                name: "iac_change_sets");

            migrationBuilder.DropTable(
                name: "recommendations");

            migrationBuilder.DropTable(
                name: "analysis_runs");

            migrationBuilder.DropTable(
                name: "discovery_snapshots");

            migrationBuilder.DropTable(
                name: "service_groups");
        }
    }
}
