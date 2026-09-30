using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Story.Api.Migrations
{
    /// <inheritdoc />
    public partial class LiveProvidersReconstructionContinuity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InputTokens",
                table: "ImportJobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "ImportJobs",
                type: "text",
                nullable: false,
                defaultValue: "deterministic-v1");

            migrationBuilder.AddColumn<int>(
                name: "OutputTokens",
                table: "ImportJobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProposedStateJson",
                table: "ImportJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProposedThreadsJson",
                table: "ImportJobs",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "ImportJobs",
                type: "text",
                nullable: false,
                defaultValue: "fixture");

            migrationBuilder.AddColumn<string>(
                name: "Summary",
                table: "ImportJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContextJson",
                table: "GenerationRuns",
                type: "text",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<int>(
                name: "InputTokens",
                table: "GenerationRuns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OutputTokens",
                table: "GenerationRuns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StateProposalJson",
                table: "GenerationRuns",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Confidence",
                table: "Facts",
                type: "double precision",
                nullable: false,
                defaultValue: 1.0);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Facts",
                type: "text",
                nullable: false,
                defaultValue: "fact");

            migrationBuilder.AddColumn<string>(
                name: "KnownByJson",
                table: "Facts",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "NarrativeThreads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Details = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Importance = table.Column<int>(type: "integer", nullable: false),
                    EffectiveSequence = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NarrativeThreads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NarrativeThreads_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NarrativeThreads_BranchId_Status_Importance",
                table: "NarrativeThreads",
                columns: new[] { "BranchId", "Status", "Importance" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NarrativeThreads");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropColumn(
                name: "InputTokens",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "Model",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "OutputTokens",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "ProposedStateJson",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "ProposedThreadsJson",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "Summary",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "ContextJson",
                table: "GenerationRuns");

            migrationBuilder.DropColumn(
                name: "InputTokens",
                table: "GenerationRuns");

            migrationBuilder.DropColumn(
                name: "OutputTokens",
                table: "GenerationRuns");

            migrationBuilder.DropColumn(
                name: "StateProposalJson",
                table: "GenerationRuns");

            migrationBuilder.DropColumn(
                name: "Confidence",
                table: "Facts");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Facts");

            migrationBuilder.DropColumn(
                name: "KnownByJson",
                table: "Facts");
        }
    }
}
