using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EPDeskServerApi.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceNickname : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequestType",
                table: "FileRequests",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RequestedPathsJson",
                table: "FileRequests",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Nickname",
                table: "Devices",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "AgentLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceCode = table.Column<string>(type: "text", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    Level = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    Step = table.Column<string>(type: "text", nullable: false),
                    DetailsJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceDiagnosticReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceCode = table.Column<string>(type: "text", nullable: false),
                    CommandId = table.Column<Guid>(type: "uuid", nullable: true),
                    AgentVersion = table.Column<string>(type: "text", nullable: false),
                    ServiceStatus = table.Column<string>(type: "text", nullable: false),
                    WindowsVersion = table.Column<string>(type: "text", nullable: false),
                    ServiceAccount = table.Column<string>(type: "text", nullable: false),
                    InternetWorking = table.Column<bool>(type: "boolean", nullable: false),
                    ApiReachable = table.Column<bool>(type: "boolean", nullable: false),
                    SystemDriveFreeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CurrentRunningTask = table.Column<string>(type: "text", nullable: false),
                    LastFileRequest = table.Column<string>(type: "text", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: false),
                    LastHeartbeatUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PendingRequestsCount = table.Column<int>(type: "integer", nullable: false),
                    DetailsJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceDiagnosticReports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RemoteCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceCode = table.Column<string>(type: "text", nullable: false),
                    CommandType = table.Column<string>(type: "text", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    RequestedBy = table.Column<string>(type: "text", nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteCommands", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentLogs_CreatedAtUtc",
                table: "AgentLogs",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AgentLogs_DeviceCode",
                table: "AgentLogs",
                column: "DeviceCode");

            migrationBuilder.CreateIndex(
                name: "IX_AgentLogs_RequestId",
                table: "AgentLogs",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceDiagnosticReports_CreatedAtUtc",
                table: "DeviceDiagnosticReports",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceDiagnosticReports_DeviceCode",
                table: "DeviceDiagnosticReports",
                column: "DeviceCode");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteCommands_DeviceCode_Status",
                table: "RemoteCommands",
                columns: new[] { "DeviceCode", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentLogs");

            migrationBuilder.DropTable(
                name: "DeviceDiagnosticReports");

            migrationBuilder.DropTable(
                name: "RemoteCommands");

            migrationBuilder.DropColumn(
                name: "RequestType",
                table: "FileRequests");

            migrationBuilder.DropColumn(
                name: "RequestedPathsJson",
                table: "FileRequests");

            migrationBuilder.DropColumn(
                name: "Nickname",
                table: "Devices");
        }
    }
}
