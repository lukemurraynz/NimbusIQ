using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.ControlPlane.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConfidenceSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConfidenceSource",
                table: "recommendations",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConfidenceSource",
                table: "recommendations");
        }
    }
}
