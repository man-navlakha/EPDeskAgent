using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EPDeskServerApi.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceObjectVersionMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "B2VersionId",
                table: "OldUserDataFiles",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ObjectETag",
                table: "OldUserDataFiles",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "B2VersionId",
                table: "AutomaticFileUploads",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ObjectETag",
                table: "AutomaticFileUploads",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "B2VersionId",
                table: "OldUserDataFiles");

            migrationBuilder.DropColumn(
                name: "ObjectETag",
                table: "OldUserDataFiles");

            migrationBuilder.DropColumn(
                name: "B2VersionId",
                table: "AutomaticFileUploads");

            migrationBuilder.DropColumn(
                name: "ObjectETag",
                table: "AutomaticFileUploads");
        }
    }
}
