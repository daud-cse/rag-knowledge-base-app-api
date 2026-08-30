using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RagKnowledgeBaseApp.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackfillChatbotContextBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AddChatbotContextBudget shipped with defaultValue: 0, so every chatbot that existed
            // before it ran was left with a zero context budget. A zero budget means no retrieved
            // passage ever reaches the model, which looks exactly like "the document was not
            // indexed". Repair those rows; correcting the earlier migration only helps databases
            // that have not applied it yet.
            migrationBuilder.Sql("UPDATE Chatbots SET MaxContextTokens = 12000 WHERE MaxContextTokens <= 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data repair; nothing to undo.
        }
    }
}
