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
            migrationBuilder.Sql("""
                ALTER TABLE "FileRequests"
                    ADD COLUMN IF NOT EXISTS "RequestType" text NOT NULL DEFAULT '';

                ALTER TABLE "FileRequests"
                    ADD COLUMN IF NOT EXISTS "RequestedPathsJson" text NOT NULL DEFAULT '';

                ALTER TABLE "Devices"
                    ADD COLUMN IF NOT EXISTS "Nickname" text NOT NULL DEFAULT '';

                CREATE TABLE IF NOT EXISTS "AgentLogs" (
                    "Id" uuid NOT NULL,
                    "DeviceCode" text NOT NULL,
                    "RequestId" uuid NULL,
                    "Level" text NOT NULL,
                    "Category" text NOT NULL,
                    "Message" text NOT NULL,
                    "Step" text NOT NULL,
                    "DetailsJson" text NOT NULL,
                    "CreatedAtUtc" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_AgentLogs" PRIMARY KEY ("Id")
                );

                CREATE TABLE IF NOT EXISTS "DeviceDiagnosticReports" (
                    "Id" uuid NOT NULL,
                    "DeviceCode" text NOT NULL,
                    "CommandId" uuid NULL,
                    "AgentVersion" text NOT NULL,
                    "ServiceStatus" text NOT NULL,
                    "WindowsVersion" text NOT NULL,
                    "ServiceAccount" text NOT NULL,
                    "InternetWorking" boolean NOT NULL,
                    "ApiReachable" boolean NOT NULL,
                    "SystemDriveFreeBytes" bigint NOT NULL,
                    "CurrentRunningTask" text NOT NULL,
                    "LastFileRequest" text NOT NULL,
                    "LastError" text NOT NULL,
                    "LastHeartbeatUtc" timestamp with time zone NULL,
                    "PendingRequestsCount" integer NOT NULL,
                    "DetailsJson" text NOT NULL,
                    "CreatedAtUtc" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_DeviceDiagnosticReports" PRIMARY KEY ("Id")
                );

                CREATE TABLE IF NOT EXISTS "RemoteCommands" (
                    "Id" uuid NOT NULL,
                    "DeviceCode" text NOT NULL,
                    "CommandType" text NOT NULL,
                    "PayloadJson" text NOT NULL,
                    "Status" text NOT NULL,
                    "RequestedBy" text NOT NULL,
                    "RequestedAtUtc" timestamp with time zone NOT NULL,
                    "SentAtUtc" timestamp with time zone NULL,
                    "CompletedAtUtc" timestamp with time zone NULL,
                    "ErrorMessage" text NOT NULL,
                    CONSTRAINT "PK_RemoteCommands" PRIMARY KEY ("Id")
                );

                CREATE INDEX IF NOT EXISTS "IX_AgentLogs_CreatedAtUtc"
                    ON "AgentLogs" ("CreatedAtUtc");

                CREATE INDEX IF NOT EXISTS "IX_AgentLogs_DeviceCode"
                    ON "AgentLogs" ("DeviceCode");

                CREATE INDEX IF NOT EXISTS "IX_AgentLogs_RequestId"
                    ON "AgentLogs" ("RequestId");

                CREATE INDEX IF NOT EXISTS "IX_DeviceDiagnosticReports_CreatedAtUtc"
                    ON "DeviceDiagnosticReports" ("CreatedAtUtc");

                CREATE INDEX IF NOT EXISTS "IX_DeviceDiagnosticReports_DeviceCode"
                    ON "DeviceDiagnosticReports" ("DeviceCode");

                CREATE INDEX IF NOT EXISTS "IX_RemoteCommands_DeviceCode_Status"
                    ON "RemoteCommands" ("DeviceCode", "Status");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "Devices"
                    DROP COLUMN IF EXISTS "Nickname";
                """);
        }
    }
}
