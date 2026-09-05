using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace EPDeskServerApi.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentExtractionSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Classification = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Department = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DocumentVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    SourceVersionKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    FileName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    FileExtension = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BucketName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ObjectKey = table.Column<string>(type: "text", nullable: false),
                    B2VersionId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ObjectETag = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    SourceModifiedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeclaredContentType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    DetectedContentType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExtractionStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PageCount = table.Column<int>(type: "integer", nullable: true),
                    SectionCount = table.Column<int>(type: "integer", nullable: false),
                    ExtractionPipelineVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExtractionMetadataJson = table.Column<string>(type: "jsonb", nullable: false),
                    DerivativePrefix = table.Column<string>(type: "text", nullable: false),
                    ExtractionErrorCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ExtractionError = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExtractedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentVersions", x => x.Id);
                    table.CheckConstraint("CK_DocumentVersions_SectionCount", "\"SectionCount\" >= 0");
                    table.CheckConstraint("CK_DocumentVersions_SizeBytes", "\"SizeBytes\" >= 0");
                    table.CheckConstraint("CK_DocumentVersions_VersionNumber", "\"VersionNumber\" > 0");
                    table.ForeignKey(
                        name: "FK_DocumentVersions_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DocumentDerivatives",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PipelineVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    BucketName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ObjectKey = table.Column<string>(type: "text", nullable: false),
                    B2VersionId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ObjectETag = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentDerivatives", x => x.Id);
                    table.CheckConstraint("CK_DocumentDerivatives_Ordinal", "\"Ordinal\" >= 0");
                    table.CheckConstraint("CK_DocumentDerivatives_SizeBytes", "\"SizeBytes\" >= 0");
                    table.ForeignKey(
                        name: "FK_DocumentDerivatives_DocumentVersions_DocumentVersionId",
                        column: x => x.DocumentVersionId,
                        principalTable: "DocumentVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DocumentSections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PipelineVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    SectionType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SectionNumber = table.Column<int>(type: "integer", nullable: true),
                    Heading = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    OcrContent = table.Column<string>(type: "text", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CharacterCount = table.Column<int>(type: "integer", nullable: false),
                    TokenCount = table.Column<int>(type: "integer", nullable: true),
                    Language = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LocatorJson = table.Column<string>(type: "jsonb", nullable: false),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: false),
                    SearchVector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false)
                        .Annotation("Npgsql:TsVectorConfig", "simple")
                        .Annotation("Npgsql:TsVectorProperties", new[] { "Heading", "Content", "OcrContent" }),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentSections", x => x.Id);
                    table.CheckConstraint("CK_DocumentSections_CharacterCount", "\"CharacterCount\" >= 0");
                    table.CheckConstraint("CK_DocumentSections_Ordinal", "\"Ordinal\" >= 0");
                    table.ForeignKey(
                        name: "FK_DocumentSections_DocumentVersions_DocumentVersionId",
                        column: x => x.DocumentVersionId,
                        principalTable: "DocumentVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExtractionJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PipelineVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    LeaseOwner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LeaseToken = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LeaseUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastHeartbeatAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtractionJobs", x => x.Id);
                    table.CheckConstraint("CK_ExtractionJobs_AttemptCount", "\"AttemptCount\" >= 0");
                    table.CheckConstraint("CK_ExtractionJobs_MaxAttempts", "\"MaxAttempts\" > 0");
                    table.ForeignKey(
                        name: "FK_ExtractionJobs_DocumentVersions_DocumentVersionId",
                        column: x => x.DocumentVersionId,
                        principalTable: "DocumentVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentDerivatives_DocumentVersionId_PipelineVersion_Kind_~",
                table: "DocumentDerivatives",
                columns: new[] { "DocumentVersionId", "PipelineVersion", "Kind", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Documents_Department_Classification",
                table: "Documents",
                columns: new[] { "Department", "Classification" });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_DeviceCode",
                table: "Documents",
                column: "DeviceCode");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_SourceType_SourceRecordId",
                table: "Documents",
                columns: new[] { "SourceType", "SourceRecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentSections_DocumentVersionId_PipelineVersion_Ordinal",
                table: "DocumentSections",
                columns: new[] { "DocumentVersionId", "PipelineVersion", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentSections_DocumentVersionId_PipelineVersion_SectionT~",
                table: "DocumentSections",
                columns: new[] { "DocumentVersionId", "PipelineVersion", "SectionType", "SectionNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentSections_SearchVector",
                table: "DocumentSections",
                column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentVersions_DocumentId_SourceVersionKey",
                table: "DocumentVersions",
                columns: new[] { "DocumentId", "SourceVersionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentVersions_DocumentId_VersionNumber",
                table: "DocumentVersions",
                columns: new[] { "DocumentId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentVersions_ExtractionStatus",
                table: "DocumentVersions",
                column: "ExtractionStatus");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentVersions_Sha256",
                table: "DocumentVersions",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "IX_ExtractionJobs_Claim",
                table: "ExtractionJobs",
                columns: new[] { "Priority", "NextAttemptAtUtc", "CreatedAtUtc" },
                descending: new[] { true, false, false },
                filter: "\"Status\" IN ('queued', 'retry_wait')");

            migrationBuilder.CreateIndex(
                name: "IX_ExtractionJobs_DocumentVersionId_PipelineVersion",
                table: "ExtractionJobs",
                columns: new[] { "DocumentVersionId", "PipelineVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExtractionJobs_RunningLease",
                table: "ExtractionJobs",
                column: "LeaseUntilUtc",
                filter: "\"Status\" = 'running'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentDerivatives");

            migrationBuilder.DropTable(
                name: "DocumentSections");

            migrationBuilder.DropTable(
                name: "ExtractionJobs");

            migrationBuilder.DropTable(
                name: "DocumentVersions");

            migrationBuilder.DropTable(
                name: "Documents");
        }
    }
}
