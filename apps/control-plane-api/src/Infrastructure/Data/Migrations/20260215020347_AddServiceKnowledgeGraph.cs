using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.ControlPlane.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceKnowledgeGraph : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "service_domains",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    DomainType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Policies = table.Column<string>(type: "text", nullable: true),
                    Owners = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_domains", x => x.Id);
                    table.ForeignKey(
                        name: "FK_service_domains_service_groups_ServiceGroupId",
                        column: x => x.ServiceGroupId,
                        principalTable: "service_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "service_graph_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnalysisRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SnapshotTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NodeCount = table.Column<int>(type: "integer", nullable: false),
                    EdgeCount = table.Column<int>(type: "integer", nullable: false),
                    DomainCount = table.Column<int>(type: "integer", nullable: false),
                    GraphMetrics = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_graph_snapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_service_graph_snapshots_analysis_runs_AnalysisRunId",
                        column: x => x.AnalysisRunId,
                        principalTable: "analysis_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_service_graph_snapshots_service_groups_ServiceGroupId",
                        column: x => x.ServiceGroupId,
                        principalTable: "service_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "service_nodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    AzureResourceId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Properties = table.Column<string>(type: "text", nullable: true),
                    Tags = table.Column<string>(type: "text", nullable: true),
                    MonthlyCost = table.Column<decimal>(type: "numeric", nullable: true),
                    ReliabilityScore = table.Column<string>(type: "text", nullable: true),
                    PerformanceMetrics = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_nodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_service_nodes_service_groups_ServiceGroupId",
                        column: x => x.ServiceGroupId,
                        principalTable: "service_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "service_domain_memberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DomainId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_domain_memberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_service_domain_memberships_service_domains_DomainId",
                        column: x => x.DomainId,
                        principalTable: "service_domains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_service_domain_memberships_service_nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "service_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "service_edges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceNodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetNodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    EdgeType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Protocol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Direction = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ConfidenceScore = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    Properties = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_edges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_service_edges_service_groups_ServiceGroupId",
                        column: x => x.ServiceGroupId,
                        principalTable: "service_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_service_edges_service_nodes_SourceNodeId",
                        column: x => x.SourceNodeId,
                        principalTable: "service_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_service_edges_service_nodes_TargetNodeId",
                        column: x => x.TargetNodeId,
                        principalTable: "service_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_service_domain_memberships_DomainId_NodeId",
                table: "service_domain_memberships",
                columns: new[] { "DomainId", "NodeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_service_domain_memberships_NodeId",
                table: "service_domain_memberships",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_service_domains_ServiceGroupId_DomainType",
                table: "service_domains",
                columns: new[] { "ServiceGroupId", "DomainType" });

            migrationBuilder.CreateIndex(
                name: "IX_service_edges_ServiceGroupId_SourceNodeId",
                table: "service_edges",
                columns: new[] { "ServiceGroupId", "SourceNodeId" });

            migrationBuilder.CreateIndex(
                name: "IX_service_edges_ServiceGroupId_TargetNodeId",
                table: "service_edges",
                columns: new[] { "ServiceGroupId", "TargetNodeId" });

            migrationBuilder.CreateIndex(
                name: "IX_service_edges_SourceNodeId",
                table: "service_edges",
                column: "SourceNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_service_edges_TargetNodeId",
                table: "service_edges",
                column: "TargetNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_service_graph_snapshots_AnalysisRunId",
                table: "service_graph_snapshots",
                column: "AnalysisRunId");

            migrationBuilder.CreateIndex(
                name: "IX_service_graph_snapshots_ServiceGroupId_SnapshotTime",
                table: "service_graph_snapshots",
                columns: new[] { "ServiceGroupId", "SnapshotTime" });

            migrationBuilder.CreateIndex(
                name: "IX_service_nodes_AzureResourceId",
                table: "service_nodes",
                column: "AzureResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_service_nodes_ServiceGroupId_NodeType",
                table: "service_nodes",
                columns: new[] { "ServiceGroupId", "NodeType" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "service_domain_memberships");

            migrationBuilder.DropTable(
                name: "service_edges");

            migrationBuilder.DropTable(
                name: "service_graph_snapshots");

            migrationBuilder.DropTable(
                name: "service_domains");

            migrationBuilder.DropTable(
                name: "service_nodes");
        }
    }
}
