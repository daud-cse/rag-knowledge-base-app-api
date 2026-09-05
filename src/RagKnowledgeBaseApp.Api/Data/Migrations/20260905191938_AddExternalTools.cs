using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RagKnowledgeBaseApp.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalTools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Tools",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    BaseUrl = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: true),
                    ConnectorApp = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    AuthType = table.Column<int>(type: "int", nullable: false),
                    AuthHeaderName = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    AuthSecret = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    HumanApproval = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    OperationsRefreshedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tools", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChatbotTools",
                columns: table => new
                {
                    ChatbotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ToolId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatbotTools", x => new { x.ChatbotId, x.ToolId });
                    table.ForeignKey(
                        name: "FK_ChatbotTools_Chatbots_ChatbotId",
                        column: x => x.ChatbotId,
                        principalTable: "Chatbots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChatbotTools_Tools_ToolId",
                        column: x => x.ToolId,
                        principalTable: "Tools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ToolInvocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ToolId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationName = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ArgumentsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DecidedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToolInvocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ToolInvocations_Tools_ToolId",
                        column: x => x.ToolId,
                        principalTable: "Tools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ToolOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ToolId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    HttpMethod = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    Path = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsReadOnly = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToolOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ToolOperations_Tools_ToolId",
                        column: x => x.ToolId,
                        principalTable: "Tools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatbotTools_ToolId",
                table: "ChatbotTools",
                column: "ToolId");

            migrationBuilder.CreateIndex(
                name: "IX_ToolInvocations_ConversationId_Status",
                table: "ToolInvocations",
                columns: new[] { "ConversationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ToolInvocations_TenantId_CreatedAt",
                table: "ToolInvocations",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ToolInvocations_ToolId",
                table: "ToolInvocations",
                column: "ToolId");

            migrationBuilder.CreateIndex(
                name: "IX_ToolOperations_ToolId_Name",
                table: "ToolOperations",
                columns: new[] { "ToolId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tools_TenantId_Name",
                table: "Tools",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tools_TenantId_Type",
                table: "Tools",
                columns: new[] { "TenantId", "Type" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatbotTools");

            migrationBuilder.DropTable(
                name: "ToolInvocations");

            migrationBuilder.DropTable(
                name: "ToolOperations");

            migrationBuilder.DropTable(
                name: "Tools");
        }
    }
}
