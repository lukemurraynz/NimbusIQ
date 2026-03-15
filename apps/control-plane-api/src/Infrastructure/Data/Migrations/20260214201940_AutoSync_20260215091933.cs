using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.ControlPlane.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AutoSync_20260215091933 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "EvidenceRefs",
                table: "recommendations",
                newName: "RejectionReason");

            migrationBuilder.RenameColumn(
                name: "Confidence",
                table: "recommendations",
                newName: "ConfidenceScore");

            migrationBuilder.AddColumn<string>(
                name: "ComponentName",
                table: "release_attestations",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ComponentVersion",
                table: "release_attestations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MockDetectionDetails",
                table: "release_attestations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReleaseId",
                table: "release_attestations",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "ValidationPassed",
                table: "release_attestations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ActionType",
                table: "recommendations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ApprovalComments",
                table: "recommendations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovedAt",
                table: "recommendations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedBy",
                table: "recommendations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CorrelationId",
                table: "recommendations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "recommendations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EstimatedImpact",
                table: "recommendations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvidenceReferences",
                table: "recommendations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAt",
                table: "recommendations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Priority",
                table: "recommendations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Rationale",
                table: "recommendations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RejectedAt",
                table: "recommendations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectedBy",
                table: "recommendations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResourceId",
                table: "recommendations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "ValidUntil",
                table: "recommendations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AnalysisRunId",
                table: "discovery_snapshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AnomalyCount",
                table: "discovery_snapshots",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CapturedBy",
                table: "discovery_snapshots",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DependencyCount",
                table: "discovery_snapshots",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ResourceCount",
                table: "discovery_snapshots",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ResourceInventory",
                table: "discovery_snapshots",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntityId",
                table: "audit_events",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntityType",
                table: "audit_events",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EventType",
                table: "audit_events",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "Timestamp",
                table: "audit_events",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "audit_events",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_release_attestations_ReleaseId",
                table: "release_attestations",
                column: "ReleaseId");

            migrationBuilder.CreateIndex(
                name: "IX_discovery_snapshots_AnalysisRunId",
                table: "discovery_snapshots",
                column: "AnalysisRunId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_EntityType_EntityId",
                table: "audit_events",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_Timestamp",
                table: "audit_events",
                column: "Timestamp");

            migrationBuilder.AddForeignKey(
                name: "FK_discovery_snapshots_analysis_runs_AnalysisRunId",
                table: "discovery_snapshots",
                column: "AnalysisRunId",
                principalTable: "analysis_runs",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_discovery_snapshots_analysis_runs_AnalysisRunId",
                table: "discovery_snapshots");

            migrationBuilder.DropIndex(
                name: "IX_release_attestations_ReleaseId",
                table: "release_attestations");

            migrationBuilder.DropIndex(
                name: "IX_discovery_snapshots_AnalysisRunId",
                table: "discovery_snapshots");

            migrationBuilder.DropIndex(
                name: "IX_audit_events_EntityType_EntityId",
                table: "audit_events");

            migrationBuilder.DropIndex(
                name: "IX_audit_events_Timestamp",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "ComponentName",
                table: "release_attestations");

            migrationBuilder.DropColumn(
                name: "ComponentVersion",
                table: "release_attestations");

            migrationBuilder.DropColumn(
                name: "MockDetectionDetails",
                table: "release_attestations");

            migrationBuilder.DropColumn(
                name: "ReleaseId",
                table: "release_attestations");

            migrationBuilder.DropColumn(
                name: "ValidationPassed",
                table: "release_attestations");

            migrationBuilder.DropColumn(
                name: "ActionType",
                table: "recommendations");

            migrationBuilder.DropColumn(
                name: "ApprovalComments",
                table: "recommendations");

            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "recommendations");

            migrationBuilder.DropColumn(
                name: "ApprovedBy",
                table: "recommendations");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "recommendations");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "recommendations");

            migrationBuilder.DropColumn(
                name: "EstimatedImpact",
                table: "recommendations");

            migrationBuilder.DropColumn(
                name: "EvidenceReferences",
                table: "recommendations");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "recommendations");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "recommendations");

            migrationBuilder.DropColumn(
                name: "Rationale",
                table: "recommendations");

            migrationBuilder.DropColumn(
                name: "RejectedAt",
                table: "recommendations");

            migrationBuilder.DropColumn(
                name: "RejectedBy",
                table: "recommendations");

            migrationBuilder.DropColumn(
                name: "ResourceId",
                table: "recommendations");

            migrationBuilder.DropColumn(
                name: "ValidUntil",
                table: "recommendations");

            migrationBuilder.DropColumn(
                name: "AnalysisRunId",
                table: "discovery_snapshots");

            migrationBuilder.DropColumn(
                name: "AnomalyCount",
                table: "discovery_snapshots");

            migrationBuilder.DropColumn(
                name: "CapturedBy",
                table: "discovery_snapshots");

            migrationBuilder.DropColumn(
                name: "DependencyCount",
                table: "discovery_snapshots");

            migrationBuilder.DropColumn(
                name: "ResourceCount",
                table: "discovery_snapshots");

            migrationBuilder.DropColumn(
                name: "ResourceInventory",
                table: "discovery_snapshots");

            migrationBuilder.DropColumn(
                name: "EntityId",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "EntityType",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "EventType",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "Timestamp",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "audit_events");

            migrationBuilder.RenameColumn(
                name: "RejectionReason",
                table: "recommendations",
                newName: "EvidenceRefs");

            migrationBuilder.RenameColumn(
                name: "ConfidenceScore",
                table: "recommendations",
                newName: "Confidence");
        }
    }
}
