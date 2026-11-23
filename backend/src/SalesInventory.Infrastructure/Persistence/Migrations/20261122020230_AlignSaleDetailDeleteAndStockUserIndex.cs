using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SalesInventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AlignSaleDetailDeleteAndStockUserIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SaleDetails_Sales",
                schema: "dbo",
                table: "SaleDetails");

            migrationBuilder.AddForeignKey(
                name: "FK_SaleDetails_Sales",
                schema: "dbo",
                table: "SaleDetails",
                column: "SaleId",
                principalSchema: "dbo",
                principalTable: "Sales",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.Sql(
                """
                IF NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE object_id = OBJECT_ID(N'[dbo].[StockMovements]')
                      AND name = N'IX_StockMovements_UserId'
                )
                BEGIN
                    CREATE INDEX [IX_StockMovements_UserId]
                        ON [dbo].[StockMovements] ([UserId]);
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SaleDetails_Sales",
                schema: "dbo",
                table: "SaleDetails");

            migrationBuilder.AddForeignKey(
                name: "FK_SaleDetails_Sales",
                schema: "dbo",
                table: "SaleDetails",
                column: "SaleId",
                principalSchema: "dbo",
                principalTable: "Sales",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
