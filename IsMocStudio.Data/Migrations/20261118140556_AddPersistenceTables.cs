using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IsMocStudio.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPersistenceTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiQuotaUsages",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    GenerationsUsed = table.Column<int>(type: "integer", nullable: false),
                    RefinementsUsed = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiQuotaUsages", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "ProjectDeliverables",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DesignHtml = table.Column<string>(type: "text", nullable: false),
                    SpecificationJson = table.Column<string>(type: "text", nullable: false),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectDeliverables", x => x.ProjectId);
                });

            migrationBuilder.CreateTable(
                name: "ProjectMemories",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ThreadType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ProjectName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Document = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectMemories", x => x.ProjectId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectMemories_OwnerId",
                table: "ProjectMemories",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectMemories_UpdatedAt",
                table: "ProjectMemories",
                column: "UpdatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiQuotaUsages");

            migrationBuilder.DropTable(
                name: "ProjectDeliverables");

            migrationBuilder.DropTable(
                name: "ProjectMemories");
        }
    }
}
