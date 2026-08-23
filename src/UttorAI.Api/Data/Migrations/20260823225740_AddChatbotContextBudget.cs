using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UttorAI.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatbotContextBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxContextTokens",
                table: "Chatbots",
                type: "int",
                nullable: false,
                defaultValue: 12000);

            // A new non-nullable column takes the migration's default for rows that already exist;
            // the C# property initialiser only applies to newly constructed objects. Without this
            // backfill every chatbot created before this migration would run with a zero context
            // budget, which silently disables retrieval context entirely.
            migrationBuilder.Sql("UPDATE Chatbots SET MaxContextTokens = 12000 WHERE MaxContextTokens <= 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxContextTokens",
                table: "Chatbots");
        }
    }
}
