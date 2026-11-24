using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesInventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LockId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LockedUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                    table.CheckConstraint("CK_OutboxMessages_Lock", "([LockId] IS NULL AND [LockedUntilUtc] IS NULL) OR ([LockId] IS NOT NULL AND [LockedUntilUtc] IS NOT NULL)");
                    table.CheckConstraint("CK_OutboxMessages_Payload", "ISJSON([Payload]) = 1");
                    table.CheckConstraint("CK_OutboxMessages_Processed", "([Status] = 'Processed' AND [ProcessedAtUtc] IS NOT NULL) OR ([Status] <> 'Processed' AND [ProcessedAtUtc] IS NULL)");
                    table.CheckConstraint("CK_OutboxMessages_RetryCount", "[RetryCount] >= 0");
                    table.CheckConstraint("CK_OutboxMessages_Status", "[Status] IN ('Pending', 'Processing', 'Processed', 'Failed')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Pending",
                schema: "dbo",
                table: "OutboxMessages",
                columns: new[] { "NextAttemptAtUtc", "OccurredAtUtc" },
                filter: "[Status] = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ProcessingLease",
                schema: "dbo",
                table: "OutboxMessages",
                column: "LockedUntilUtc",
                filter: "[Status] = 'Processing'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutboxMessages",
                schema: "dbo");
        }
    }
}
