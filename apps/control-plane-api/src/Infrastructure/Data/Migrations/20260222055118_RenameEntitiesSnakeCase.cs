using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.ControlPlane.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameEntitiesSnakeCase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rename tables from PascalCase (created by AddServiceGroupHierarchy migration) to snake_case
            migrationBuilder.RenameTable(
                name: "BestPracticeRule",
                newName: "best_practice_rules");

            migrationBuilder.RenameTable(
                name: "DriftSnapshot",
                newName: "drift_snapshots");

            migrationBuilder.RenameTable(
                name: "BestPracticeViolation",
                newName: "best_practice_violations");

            // Drop shadow-state FK/index/column artefacts introduced when navigations used WithMany().
            // Use raw SQL with IF EXISTS so this migration is idempotent: objects may or may not
            // exist depending on whether the database was freshly created or upgraded from an
            // earlier state. PostgreSQL supports IF EXISTS for constraints, indexes, and columns.
            migrationBuilder.Sql("""
                ALTER TABLE best_practice_violations DROP CONSTRAINT IF EXISTS "FK_best_practice_violations_best_practice_rules_BestPracticeRu~";
                ALTER TABLE best_practice_violations DROP CONSTRAINT IF EXISTS "FK_best_practice_violations_service_groups_ServiceGroupId1";
                ALTER TABLE drift_snapshots          DROP CONSTRAINT IF EXISTS "FK_drift_snapshots_service_groups_ServiceGroupId1";
                DROP INDEX IF EXISTS "IX_drift_snapshots_ServiceGroupId1";
                DROP INDEX IF EXISTS "IX_best_practice_violations_BestPracticeRuleId";
                DROP INDEX IF EXISTS "IX_best_practice_violations_ServiceGroupId1";
                ALTER TABLE drift_snapshots          DROP COLUMN IF EXISTS "ServiceGroupId1";
                ALTER TABLE best_practice_violations DROP COLUMN IF EXISTS "BestPracticeRuleId";
                ALTER TABLE best_practice_violations DROP COLUMN IF EXISTS "ServiceGroupId1";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ServiceGroupId1",
                table: "drift_snapshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BestPracticeRuleId",
                table: "best_practice_violations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ServiceGroupId1",
                table: "best_practice_violations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_drift_snapshots_ServiceGroupId1",
                table: "drift_snapshots",
                column: "ServiceGroupId1");

            migrationBuilder.CreateIndex(
                name: "IX_best_practice_violations_BestPracticeRuleId",
                table: "best_practice_violations",
                column: "BestPracticeRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_best_practice_violations_ServiceGroupId1",
                table: "best_practice_violations",
                column: "ServiceGroupId1");

            migrationBuilder.AddForeignKey(
                name: "FK_best_practice_violations_best_practice_rules_BestPracticeRu~",
                table: "best_practice_violations",
                column: "BestPracticeRuleId",
                principalTable: "best_practice_rules",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_best_practice_violations_service_groups_ServiceGroupId1",
                table: "best_practice_violations",
                column: "ServiceGroupId1",
                principalTable: "service_groups",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_drift_snapshots_service_groups_ServiceGroupId1",
                table: "drift_snapshots",
                column: "ServiceGroupId1",
                principalTable: "service_groups",
                principalColumn: "Id");

            // Reverse the table renames
            migrationBuilder.RenameTable(
                name: "best_practice_violations",
                newName: "BestPracticeViolation");

            migrationBuilder.RenameTable(
                name: "drift_snapshots",
                newName: "DriftSnapshot");

            migrationBuilder.RenameTable(
                name: "best_practice_rules",
                newName: "BestPracticeRule");
        }
    }
}
