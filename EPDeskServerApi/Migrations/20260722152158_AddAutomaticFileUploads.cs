using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EPDeskServerApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomaticFileUploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AutomaticFileUploads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceCode = table.Column<string>(type: "text", nullable: false),
                    FullPath = table.Column<string>(type: "text", nullable: false),
                    PathIdentity = table.Column<string>(type: "text", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    Extension = table.Column<string>(type: "text", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    LastModifiedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ContentType = table.Column<string>(type: "text", nullable: false),
                    ObjectKey = table.Column<string>(type: "text", nullable: false),
                    MultipartUploadId = table.Column<string>(type: "text", nullable: false),
                    PartSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomaticFileUploads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FileUploadPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ExtensionsJson = table.Column<string>(type: "text", nullable: false),
                    MaxFileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileUploadPolicies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutomaticFileUploads_DeviceCode_PathIdentity",
                table: "AutomaticFileUploads",
                columns: new[] { "DeviceCode", "PathIdentity" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutomaticFileUploads_Status",
                table: "AutomaticFileUploads",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AutomaticFileUploads_UpdatedAtUtc",
                table: "AutomaticFileUploads",
                column: "UpdatedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutomaticFileUploads");

            migrationBuilder.DropTable(
                name: "FileUploadPolicies");
        }
    }
}
