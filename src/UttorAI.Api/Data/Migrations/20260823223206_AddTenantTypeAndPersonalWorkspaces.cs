using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UttorAI.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantTypeAndPersonalWorkspaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                table: "Tenants",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Type",
                table: "Tenants",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "Tenants");
        }
    }
}
