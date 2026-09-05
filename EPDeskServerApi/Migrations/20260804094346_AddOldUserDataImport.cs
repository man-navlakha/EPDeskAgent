using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EPDeskServerApi.Migrations
{
    /// <inheritdoc />
    public partial class AddOldUserDataImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OldUserDataImportJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RootPath = table.Column<string>(type: "text", nullable: false),
                    RootPathIdentity = table.Column<string>(type: "text", nullable: false),
                    SourceLabel = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    IndexedFileCount = table.Column<long>(type: "bigint", nullable: false),
                    IndexedSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    UploadedFileCount = table.Column<long>(type: "bigint", nullable: false),
                    UploadedSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    FailedFileCount = table.Column<long>(type: "bigint", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ScanCompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OldUserDataImportJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OldUserDataFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ImportJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserFolder = table.Column<string>(type: "text", nullable: false),
                    DeviceCode = table.Column<string>(type: "text", nullable: false),
                    FullPath = table.Column<string>(type: "text", nullable: false),
                    RelativePath = table.Column<string>(type: "text", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    Extension = table.Column<string>(type: "text", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ContentType = table.Column<string>(type: "text", nullable: false),
                    ObjectKey = table.Column<string>(type: "text", nullable: false),
                    MultipartUploadId = table.Column<string>(type: "text", nullable: false),
                    PartSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    UploadedBytes = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: false),
                    IndexedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UploadStartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OldUserDataFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OldUserDataFiles_OldUserDataImportJobs_ImportJobId",
                        column: x => x.ImportJobId,
                        principalTable: "OldUserDataImportJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OldUserDataFiles_ImportJobId_DeviceCode",
                table: "OldUserDataFiles",
                columns: new[] { "ImportJobId", "DeviceCode" });

            migrationBuilder.CreateIndex(
                name: "IX_OldUserDataFiles_ImportJobId_FullPath",
                table: "OldUserDataFiles",
                columns: new[] { "ImportJobId", "FullPath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OldUserDataFiles_ImportJobId_Status",
                table: "OldUserDataFiles",
                columns: new[] { "ImportJobId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_OldUserDataImportJobs_RootPathIdentity",
                table: "OldUserDataImportJobs",
                column: "RootPathIdentity",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OldUserDataImportJobs_Status",
                table: "OldUserDataImportJobs",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OldUserDataFiles");

            migrationBuilder.DropTable(
                name: "OldUserDataImportJobs");
        }
    }
}
