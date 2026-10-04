using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalkWatch.Data.Migrations
{
    /// <inheritdoc />
    public partial class PersonContextDid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContextDid",
                table: "AspNetUsers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContextDid",
                table: "AspNetUsers");
        }
    }
}
