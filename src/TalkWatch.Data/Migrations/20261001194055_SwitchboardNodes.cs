using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalkWatch.Data.Migrations
{
    /// <inheritdoc />
    public partial class SwitchboardNodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "switchboard_nodes",
                columns: table => new
                {
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    InternalId = table.Column<int>(type: "integer", nullable: true),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Key = table.Column<int>(type: "integer", nullable: true),
                    ParentId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Numbers = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    GreetingFile = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    GreetingSeconds = table.Column<double>(type: "double precision", nullable: true),
                    Present = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_switchboard_nodes", x => new { x.SiteId, x.NodeId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "switchboard_nodes");
        }
    }
}
