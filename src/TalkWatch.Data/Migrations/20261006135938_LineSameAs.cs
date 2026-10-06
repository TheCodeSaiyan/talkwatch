using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalkWatch.Data.Migrations
{
    /// <inheritdoc />
    public partial class LineSameAs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SameAs",
                table: "lines",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SameAs",
                table: "lines");
        }
    }
}
