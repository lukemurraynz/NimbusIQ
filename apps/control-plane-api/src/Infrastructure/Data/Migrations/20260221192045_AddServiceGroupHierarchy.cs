using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.ControlPlane.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceGroupHierarchy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ConfidenceScore",
                table: "recommendations",
                newName: "Confidence");

            migrationBuilder.AddColumn<int>(
                name: "HierarchyLevel",
                table: "service_groups",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "HierarchyPath",
                table: "service_groups",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentServiceGroupId",
                table: "service_groups",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Rationale",
                table: "recommendations",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "recommendations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Impact",
                table: "recommendations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ProposedChanges",
                table: "recommendations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "ServiceGroupId",
                table: "recommendations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<List<string>>(
                name: "Warnings",
                table: "recommendations",
                type: "text[]",
                nullable: false);

            migrationBuilder.CreateTable(
                name: "BestPracticeRule",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleId = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    Pillar = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Rationale = table.Column<string>(type: "text", nullable: true),
                    Severity = table.Column<string>(type: "text", nullable: false),
                    ApplicabilityScope = table.Column<string>(type: "text", nullable: false),
                    ApplicabilityCriteria = table.Column<string>(type: "text", nullable: true),
                    EvaluationQuery = table.Column<string>(type: "text", nullable: false),
                    RemediationGuidance = table.Column<string>(type: "text", nullable: true),
                    RemediationIac = table.Column<string>(type: "text", nullable: true),
                    References = table.Column<string>(type: "text", nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeprecatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BestPracticeRule", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ComplianceAssessment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnalysisRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    FrameworkType = table.Column<string>(type: "text", nullable: false),
                    FrameworkVersion = table.Column<string>(type: "text", nullable: false),
                    AssessmentDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OverallScore = table.Column<decimal>(type: "numeric", nullable: false),
                    CompliancePercentage = table.Column<decimal>(type: "numeric", nullable: false),
                    TotalRules = table.Column<int>(type: "integer", nullable: false),
                    PassedRules = table.Column<int>(type: "integer", nullable: false),
                    FailedRules = table.Column<int>(type: "integer", nullable: false),
                    WaivedRules = table.Column<int>(type: "integer", nullable: false),
                    PillarScores = table.Column<string>(type: "text", nullable: true),
                    AssessmentSummary = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComplianceAssessment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComplianceAssessment_analysis_runs_AnalysisRunId",
                        column: x => x.AnalysisRunId,
                        principalTable: "analysis_runs",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ComplianceAssessment_service_groups_ServiceGroupId",
                        column: x => x.ServiceGroupId,
                        principalTable: "service_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DriftSnapshot",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TotalViolations = table.Column<int>(type: "integer", nullable: false),
                    CriticalViolations = table.Column<int>(type: "integer", nullable: false),
                    HighViolations = table.Column<int>(type: "integer", nullable: false),
                    MediumViolations = table.Column<int>(type: "integer", nullable: false),
                    LowViolations = table.Column<int>(type: "integer", nullable: false),
                    DriftScore = table.Column<decimal>(type: "numeric", nullable: false),
                    CategoryBreakdown = table.Column<string>(type: "text", nullable: true),
                    TrendAnalysis = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriftSnapshot", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DriftSnapshot_service_groups_ServiceGroupId",
                        column: x => x.ServiceGroupId,
                        principalTable: "service_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecommendationDecision",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecommendationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Decision = table.Column<string>(type: "text", nullable: false),
                    Rationale = table.Column<string>(type: "text", nullable: false),
                    SubmittedBy = table.Column<string>(type: "text", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecommendationDecision", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecommendationDecision_recommendations_RecommendationId",
                        column: x => x.RecommendationId,
                        principalTable: "recommendations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "service_group_hierarchy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentServiceGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChildServiceGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelationshipType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_group_hierarchy", x => x.Id);
                    table.ForeignKey(
                        name: "FK_service_group_hierarchy_service_groups_ChildServiceGroupId",
                        column: x => x.ChildServiceGroupId,
                        principalTable: "service_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_service_group_hierarchy_service_groups_ParentServiceGroupId",
                        column: x => x.ParentServiceGroupId,
                        principalTable: "service_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BestPracticeViolation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnalysisRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResourceId = table.Column<string>(type: "text", nullable: false),
                    ResourceType = table.Column<string>(type: "text", nullable: false),
                    ViolationType = table.Column<string>(type: "text", nullable: false),
                    Severity = table.Column<string>(type: "text", nullable: false),
                    CurrentState = table.Column<string>(type: "text", nullable: false),
                    ExpectedState = table.Column<string>(type: "text", nullable: false),
                    DriftDetails = table.Column<string>(type: "text", nullable: true),
                    DetectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolutionNotes = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BestPracticeViolation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BestPracticeViolation_BestPracticeRule_RuleId",
                        column: x => x.RuleId,
                        principalTable: "BestPracticeRule",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BestPracticeViolation_analysis_runs_AnalysisRunId",
                        column: x => x.AnalysisRunId,
                        principalTable: "analysis_runs",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_BestPracticeViolation_service_groups_ServiceGroupId",
                        column: x => x.ServiceGroupId,
                        principalTable: "service_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_service_groups_ParentServiceGroupId",
                table: "service_groups",
                column: "ParentServiceGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_recommendations_ServiceGroupId",
                table: "recommendations",
                column: "ServiceGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_BestPracticeViolation_AnalysisRunId",
                table: "BestPracticeViolation",
                column: "AnalysisRunId");

            migrationBuilder.CreateIndex(
                name: "IX_BestPracticeViolation_RuleId",
                table: "BestPracticeViolation",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_BestPracticeViolation_ServiceGroupId",
                table: "BestPracticeViolation",
                column: "ServiceGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_ComplianceAssessment_AnalysisRunId",
                table: "ComplianceAssessment",
                column: "AnalysisRunId");

            migrationBuilder.CreateIndex(
                name: "IX_ComplianceAssessment_ServiceGroupId",
                table: "ComplianceAssessment",
                column: "ServiceGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_DriftSnapshot_ServiceGroupId",
                table: "DriftSnapshot",
                column: "ServiceGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_RecommendationDecision_RecommendationId",
                table: "RecommendationDecision",
                column: "RecommendationId");

            migrationBuilder.CreateIndex(
                name: "IX_service_group_hierarchy_ChildServiceGroupId",
                table: "service_group_hierarchy",
                column: "ChildServiceGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_service_group_hierarchy_ParentServiceGroupId_ChildServiceGr~",
                table: "service_group_hierarchy",
                columns: new[] { "ParentServiceGroupId", "ChildServiceGroupId", "RelationshipType" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_recommendations_service_groups_ServiceGroupId",
                table: "recommendations",
                column: "ServiceGroupId",
                principalTable: "service_groups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_service_groups_service_groups_ParentServiceGroupId",
                table: "service_groups",
                column: "ParentServiceGroupId",
                principalTable: "service_groups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_recommendations_service_groups_ServiceGroupId",
                table: "recommendations");

            migrationBuilder.DropForeignKey(
                name: "FK_service_groups_service_groups_ParentServiceGroupId",
                table: "service_groups");

            migrationBuilder.DropTable(
                name: "BestPracticeViolation");

            migrationBuilder.DropTable(
                name: "ComplianceAssessment");

            migrationBuilder.DropTable(
                name: "DriftSnapshot");

            migrationBuilder.DropTable(
                name: "RecommendationDecision");

            migrationBuilder.DropTable(
                name: "service_group_hierarchy");

            migrationBuilder.DropTable(
                name: "BestPracticeRule");

            migrationBuilder.DropIndex(
                name: "IX_service_groups_ParentServiceGroupId",
                table: "service_groups");

            migrationBuilder.DropIndex(
                name: "IX_recommendations_ServiceGroupId",
                table: "recommendations");

            migrationBuilder.DropColumn(
                name: "HierarchyLevel",
                table: "service_groups");

            migrationBuilder.DropColumn(
                name: "HierarchyPath",
                table: "service_groups");

            migrationBuilder.DropColumn(
                name: "ParentServiceGroupId",
                table: "service_groups");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "recommendations");

            migrationBuilder.DropColumn(
                name: "Impact",
                table: "recommendations");

            migrationBuilder.DropColumn(
                name: "ProposedChanges",
                table: "recommendations");

            migrationBuilder.DropColumn(
                name: "ServiceGroupId",
                table: "recommendations");

            migrationBuilder.DropColumn(
                name: "Warnings",
                table: "recommendations");

            migrationBuilder.RenameColumn(
                name: "Confidence",
                table: "recommendations",
                newName: "ConfidenceScore");

            migrationBuilder.AlterColumn<string>(
                name: "Rationale",
                table: "recommendations",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");
        }
    }
}
