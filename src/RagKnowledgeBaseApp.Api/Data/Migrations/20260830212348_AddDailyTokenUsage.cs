using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RagKnowledgeBaseApp.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDailyTokenUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DailyTokenUsage",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UsageDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EmbeddingTokens = table.Column<long>(type: "bigint", nullable: false),
                    PromptTokens = table.Column<long>(type: "bigint", nullable: false),
                    CompletionTokens = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyTokenUsage", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DailyTokenUsage_TenantId_UsageDate",
                table: "DailyTokenUsage",
                columns: new[] { "TenantId", "UsageDate" });

            migrationBuilder.CreateIndex(
                name: "IX_DailyTokenUsage_UserId_UsageDate",
                table: "DailyTokenUsage",
                columns: new[] { "UserId", "UsageDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyTokenUsage");
        }
    }
}
