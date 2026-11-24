using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesInventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkerHeartbeats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FailedAtUtc",
                schema: "dbo",
                table: "OutboxMessages",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorkerHeartbeats",
                schema: "dbo",
                columns: table => new
                {
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSuccessfulCycleAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastCycleErrorType = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    Version = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerHeartbeats", x => x.InstanceId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_FailedAt",
                schema: "dbo",
                table: "OutboxMessages",
                column: "FailedAtUtc",
                filter: "[Status] = 'Failed'");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ProcessedAt",
                schema: "dbo",
                table: "OutboxMessages",
                column: "ProcessedAtUtc",
                filter: "[Status] = 'Processed'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkerHeartbeats",
                schema: "dbo");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_FailedAt",
                schema: "dbo",
                table: "OutboxMessages");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_ProcessedAt",
                schema: "dbo",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "FailedAtUtc",
                schema: "dbo",
                table: "OutboxMessages");
        }
    }
}
