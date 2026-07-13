using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EPDeskServerApi.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceScanExclusions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceScanExclusions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceCode = table.Column<string>(type: "text", nullable: false),
                    ExclusionType = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceScanExclusions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceScanExclusions_DeviceCode_ExclusionType_Value",
                table: "DeviceScanExclusions",
                columns: new[] { "DeviceCode", "ExclusionType", "Value" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceScanExclusions_DeviceCode_IsActive",
                table: "DeviceScanExclusions",
                columns: new[] { "DeviceCode", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceScanExclusions");
        }
    }
}
