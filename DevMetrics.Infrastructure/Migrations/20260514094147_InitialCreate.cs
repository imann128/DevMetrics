using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevMetrics.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Repositories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false, defaultValue: ""),
                    LastScannedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Repositories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Commits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Author = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    DateUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LinesAdded = table.Column<int>(type: "INTEGER", nullable: false),
                    LinesDeleted = table.Column<int>(type: "INTEGER", nullable: false),
                    FilesChanged = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Commits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Commits_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DailySummaries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TotalCommits = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalLinesAdded = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalLinesDeleted = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailySummaries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DailySummaries_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Commits_DateUtc",
                table: "Commits",
                column: "DateUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Commits_Hash",
                table: "Commits",
                column: "Hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Commits_RepositoryId_DateUtc",
                table: "Commits",
                columns: new[] { "RepositoryId", "DateUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DailySummaries_Date",
                table: "DailySummaries",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_DailySummaries_RepositoryId_Date",
                table: "DailySummaries",
                columns: new[] { "RepositoryId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_LastScannedUtc",
                table: "Repositories",
                column: "LastScannedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_Path",
                table: "Repositories",
                column: "Path",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Commits");

            migrationBuilder.DropTable(
                name: "DailySummaries");

            migrationBuilder.DropTable(
                name: "Repositories");
        }
    }
}
