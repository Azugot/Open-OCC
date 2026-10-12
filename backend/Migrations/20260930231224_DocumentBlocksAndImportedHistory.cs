using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Story.Api.Migrations
{
    /// <inheritdoc />
    public partial class DocumentBlocksAndImportedHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NormalizedText",
                table: "Sources",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "StructureJson",
                table: "Sources",
                type: "text",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<int>(
                name: "Length",
                table: "Segments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Start",
                table: "Segments",
                type: "integer",
                nullable: false,
                defaultValue: -1);

            migrationBuilder.CreateTable(
                name: "ImportedHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    EffectiveSequence = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportedHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportedHistory_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ImportedHistory_ImportJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "ImportJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ImportedHistory_Sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "Sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportedHistory_BranchId_EffectiveSequence",
                table: "ImportedHistory",
                columns: new[] { "BranchId", "EffectiveSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportedHistory_BranchId_JobId",
                table: "ImportedHistory",
                columns: new[] { "BranchId", "JobId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImportedHistory_JobId",
                table: "ImportedHistory",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportedHistory_SourceId",
                table: "ImportedHistory",
                column: "SourceId");

            // Establish source history on legacy branches/forks from evidence that
            // was actually approved there, without rewriting original citations.
            migrationBuilder.Sql("""
                INSERT INTO "ImportedHistory" ("Id", "BranchId", "SourceId", "JobId", "EffectiveSequence")
                SELECT gen_random_uuid(), f."BranchId", j."SourceId", j."Id", MIN(f."EffectiveSequence")
                FROM "Facts" f JOIN "Evidence" e ON e."FactId" = f."Id"
                JOIN "Segments" s ON s."Id" = e."SegmentId"
                JOIN "ImportJobs" j ON j."Id" = s."JobId"
                WHERE j."Status" = 'approved'
                GROUP BY f."BranchId", j."SourceId", j."Id";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportedHistory");

            migrationBuilder.DropColumn(
                name: "NormalizedText",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "StructureJson",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "Length",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "Start",
                table: "Segments");
        }
    }
}
