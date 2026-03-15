using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.ControlPlane.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SyncPendingModelChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeltaSummary",
                table: "timeline_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EventCategory",
                table: "timeline_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ScoreImpact",
                table: "timeline_events",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChangeContext",
                table: "recommendations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TriggerReason",
                table: "recommendations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DriftCategory",
                table: "best_practice_violations",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "score_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnalysisRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Score = table.Column<double>(type: "double precision", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    Dimensions = table.Column<string>(type: "text", nullable: true),
                    DeltaFromPrevious = table.Column<string>(type: "text", nullable: true),
                    ResourceCount = table.Column<int>(type: "integer", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_score_snapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_score_snapshots_analysis_runs_AnalysisRunId",
                        column: x => x.AnalysisRunId,
                        principalTable: "analysis_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_score_snapshots_service_groups_ServiceGroupId",
                        column: x => x.ServiceGroupId,
                        principalTable: "service_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_score_snapshots_AnalysisRunId",
                table: "score_snapshots",
                column: "AnalysisRunId");

            migrationBuilder.CreateIndex(
                name: "IX_score_snapshots_ServiceGroupId_Category_RecordedAt",
                table: "score_snapshots",
                columns: new[] { "ServiceGroupId", "Category", "RecordedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "score_snapshots");

            migrationBuilder.DropColumn(
                name: "DeltaSummary",
                table: "timeline_events");

            migrationBuilder.DropColumn(
                name: "EventCategory",
                table: "timeline_events");

            migrationBuilder.DropColumn(
                name: "ScoreImpact",
                table: "timeline_events");

            migrationBuilder.DropColumn(
                name: "ChangeContext",
                table: "recommendations");

            migrationBuilder.DropColumn(
                name: "TriggerReason",
                table: "recommendations");

            migrationBuilder.DropColumn(
                name: "DriftCategory",
                table: "best_practice_violations");
        }
    }
}
