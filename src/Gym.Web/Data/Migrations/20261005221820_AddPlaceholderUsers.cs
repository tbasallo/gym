using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gym.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaceholderUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimCodeExpiresUtc",
                table: "AspNetUsers",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimCodeHash",
                table: "AspNetUsers",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPlaceholder",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimCodeExpiresUtc",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ClaimCodeHash",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "IsPlaceholder",
                table: "AspNetUsers");
        }
    }
}
