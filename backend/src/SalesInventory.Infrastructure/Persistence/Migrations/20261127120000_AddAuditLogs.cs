using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesInventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLogs",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EntityName = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    EntityId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Action = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    ActorType = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    Source = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    OldValues = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NewValues = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ChangedFields = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                    table.CheckConstraint("CK_AuditLogs_Action", "[Action] IN ('INSERT', 'UPDATE', 'DELETE')");
                    table.CheckConstraint("CK_AuditLogs_Actor", "([ActorType] = 'User' AND [UserId] IS NOT NULL) OR ([ActorType] = 'System' AND [UserId] IS NULL)");
                    table.CheckConstraint("CK_AuditLogs_ActorType", "[ActorType] IN ('User', 'System')");
                    table.CheckConstraint("CK_AuditLogs_Json", "([OldValues] IS NULL OR ISJSON([OldValues]) = 1) AND ([NewValues] IS NULL OR ISJSON([NewValues]) = 1)");
                    table.CheckConstraint("CK_AuditLogs_Source", "[Source] IN ('API', 'Worker')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Correlation",
                schema: "dbo",
                table: "AuditLogs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Entity",
                schema: "dbo",
                table: "AuditLogs",
                columns: new[] { "EntityName", "EntityId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_OccurredAt",
                schema: "dbo",
                table: "AuditLogs",
                columns: new[] { "OccurredAtUtc", "Id" },
                descending: new[] { true, true });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_User",
                schema: "dbo",
                table: "AuditLogs",
                columns: new[] { "UserId", "OccurredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLogs",
                schema: "dbo");
        }
    }
}
