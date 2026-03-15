using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.ControlPlane.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFeatures1245_ValueTracking_Automation_Templates_GitOps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AutomationRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleName = table.Column<string>(type: "text", nullable: false),
                    Trigger = table.Column<string>(type: "text", nullable: false),
                    TriggerCriteria = table.Column<string>(type: "text", nullable: true),
                    MaxRiskThreshold = table.Column<decimal>(type: "numeric", nullable: false),
                    MinConfidenceThreshold = table.Column<decimal>(type: "numeric", nullable: false),
                    ActionType = table.Column<string>(type: "text", nullable: false),
                    ImplementationSchedule = table.Column<string>(type: "text", nullable: true),
                    ApprovalBypass = table.Column<string>(type: "text", nullable: true),
                    RequiresAttestation = table.Column<bool>(type: "boolean", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ExecutionCount = table.Column<int>(type: "integer", nullable: false),
                    LastExecutedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GitOpsPullRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecommendationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangeSetId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryUrl = table.Column<string>(type: "text", nullable: false),
                    PullRequestUrl = table.Column<string>(type: "text", nullable: false),
                    PullRequestNumber = table.Column<int>(type: "integer", nullable: false),
                    BranchName = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    TargetBranch = table.Column<string>(type: "text", nullable: false),
                    AutoMergeEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Reviewers = table.Column<string>(type: "text", nullable: true),
                    Labels = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MergedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MergeCommitSha = table.Column<string>(type: "text", nullable: true),
                    CiCheckStatus = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitOpsPullRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GitOpsPullRequests_iac_change_sets_ChangeSetId",
                        column: x => x.ChangeSetId,
                        principalTable: "iac_change_sets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GitOpsPullRequests_recommendations_RecommendationId",
                        column: x => x.RecommendationId,
                        principalTable: "recommendations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecommendationTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateName = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    ProblemPattern = table.Column<string>(type: "text", nullable: false),
                    SolutionPattern = table.Column<string>(type: "text", nullable: false),
                    ApplicabilityCriteria = table.Column<string>(type: "text", nullable: true),
                    IacTemplate = table.Column<string>(type: "text", nullable: false),
                    ParameterSchema = table.Column<string>(type: "text", nullable: true),
                    PreConditions = table.Column<string>(type: "text", nullable: true),
                    PostConditions = table.Column<string>(type: "text", nullable: true),
                    EstimatedSavingsRange = table.Column<decimal>(type: "numeric", nullable: false),
                    TypicalRiskScore = table.Column<decimal>(type: "numeric", nullable: false),
                    UsageCount = table.Column<int>(type: "integer", nullable: false),
                    AverageSuccessRate = table.Column<decimal>(type: "numeric", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecommendationTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ValueRealizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecommendationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangeSetId = table.Column<Guid>(type: "uuid", nullable: true),
                    EstimatedMonthlySavings = table.Column<decimal>(type: "numeric", nullable: false),
                    ActualMonthlySavings = table.Column<decimal>(type: "numeric", nullable: false),
                    EstimatedImplementationCost = table.Column<decimal>(type: "numeric", nullable: false),
                    ActualImplementationCost = table.Column<decimal>(type: "numeric", nullable: false),
                    EstimatedPaybackDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActualPaybackDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    BaselineRecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FirstMeasurementAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PaybackAchievedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MeasurementNotes = table.Column<string>(type: "text", nullable: true),
                    VarianceAnalysis = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ValueRealizations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ValueRealizations_iac_change_sets_ChangeSetId",
                        column: x => x.ChangeSetId,
                        principalTable: "iac_change_sets",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ValueRealizations_recommendations_RecommendationId",
                        column: x => x.RecommendationId,
                        principalTable: "recommendations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AutomationExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AutomationRuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecommendationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    TriggeredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExecutionLog = table.Column<string>(type: "text", nullable: true),
                    ErrorDetails = table.Column<string>(type: "text", nullable: true),
                    RollbackTriggered = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomationExecutions_AutomationRules_AutomationRuleId",
                        column: x => x.AutomationRuleId,
                        principalTable: "AutomationRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AutomationExecutions_recommendations_RecommendationId",
                        column: x => x.RecommendationId,
                        principalTable: "recommendations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TemplateUsages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecommendationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppliedBy = table.Column<string>(type: "text", nullable: false),
                    AppliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ParameterValues = table.Column<string>(type: "text", nullable: true),
                    Outcome = table.Column<string>(type: "text", nullable: false),
                    FeedbackNotes = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TemplateUsages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TemplateUsages_RecommendationTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "RecommendationTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TemplateUsages_recommendations_RecommendationId",
                        column: x => x.RecommendationId,
                        principalTable: "recommendations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationExecutions_AutomationRuleId",
                table: "AutomationExecutions",
                column: "AutomationRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationExecutions_RecommendationId",
                table: "AutomationExecutions",
                column: "RecommendationId");

            migrationBuilder.CreateIndex(
                name: "IX_GitOpsPullRequests_ChangeSetId",
                table: "GitOpsPullRequests",
                column: "ChangeSetId");

            migrationBuilder.CreateIndex(
                name: "IX_GitOpsPullRequests_RecommendationId",
                table: "GitOpsPullRequests",
                column: "RecommendationId");

            migrationBuilder.CreateIndex(
                name: "IX_TemplateUsages_RecommendationId",
                table: "TemplateUsages",
                column: "RecommendationId");

            migrationBuilder.CreateIndex(
                name: "IX_TemplateUsages_TemplateId",
                table: "TemplateUsages",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_ValueRealizations_ChangeSetId",
                table: "ValueRealizations",
                column: "ChangeSetId");

            migrationBuilder.CreateIndex(
                name: "IX_ValueRealizations_RecommendationId",
                table: "ValueRealizations",
                column: "RecommendationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutomationExecutions");

            migrationBuilder.DropTable(
                name: "GitOpsPullRequests");

            migrationBuilder.DropTable(
                name: "TemplateUsages");

            migrationBuilder.DropTable(
                name: "ValueRealizations");

            migrationBuilder.DropTable(
                name: "AutomationRules");

            migrationBuilder.DropTable(
                name: "RecommendationTemplates");
        }
    }
}
