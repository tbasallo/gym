using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gym.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSupersets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SupersetGroup",
                table: "SessionExercises",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SupersetGroup",
                table: "SessionExercises");
        }
    }
}
