using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.ControlPlane.Infrastructure.Data.Migrations
{
  /// <inheritdoc />
  public partial class ExpandIacChangeSetArtifactUri : Migration
  {
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
      migrationBuilder.AlterColumn<string>(
          name: "ArtifactUri",
          table: "iac_change_sets",
          type: "text",
          nullable: false,
          oldClrType: typeof(string),
          oldType: "character varying(2048)",
          oldMaxLength: 2048);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
      migrationBuilder.AlterColumn<string>(
          name: "ArtifactUri",
          table: "iac_change_sets",
          type: "character varying(2048)",
          maxLength: 2048,
          nullable: false,
          oldClrType: typeof(string),
          oldType: "text");
    }
  }
}
