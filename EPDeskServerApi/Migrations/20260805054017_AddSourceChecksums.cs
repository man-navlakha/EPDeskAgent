using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EPDeskServerApi.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceChecksums : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Sha256",
                table: "OldUserDataFiles",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Sha256",
                table: "AutomaticFileUploads",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Sha256",
                table: "OldUserDataFiles");

            migrationBuilder.DropColumn(
                name: "Sha256",
                table: "AutomaticFileUploads");
        }
    }
}
