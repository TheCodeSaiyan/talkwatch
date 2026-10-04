using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalkWatch.Data.Migrations
{
    /// <inheritdoc />
    public partial class NumberRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "number_roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Did = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_number_roles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_number_roles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_number_roles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_number_roles_sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "number_routes",
                columns: table => new
                {
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Did = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_number_routes", x => new { x.SiteId, x.Did, x.Kind, x.Key });
                });

            migrationBuilder.CreateIndex(
                name: "IX_number_roles_RoleId",
                table: "number_roles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_number_roles_SiteId_Did",
                table: "number_roles",
                columns: new[] { "SiteId", "Did" });

            migrationBuilder.CreateIndex(
                name: "IX_number_roles_SiteId_UserId_Did",
                table: "number_roles",
                columns: new[] { "SiteId", "UserId", "Did" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_number_roles_UserId",
                table: "number_roles",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_number_routes_SiteId_Kind_Key",
                table: "number_routes",
                columns: new[] { "SiteId", "Kind", "Key" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "number_roles");

            migrationBuilder.DropTable(
                name: "number_routes");
        }
    }
}
