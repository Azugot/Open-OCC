using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Story.Api.Migrations
{
    /// <inheritdoc />
    public partial class AgentImportDrafts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Legacy extraction could turn failed model responses into paragraph facts.
            // Preserve all source/draft rows, but require explicit reanalysis for active old jobs.
            migrationBuilder.Sql("UPDATE \"ImportJobs\" SET \"Status\" = 'paused', \"LeaseOwner\" = NULL, \"LeaseUntil\" = NULL, \"Error\" = 'Legacy import paused. Reanalyze the preserved source to use agent reconstruction.' WHERE \"Method\" <> 'agent-v2' AND \"Status\" IN ('queued', 'processing')");
            migrationBuilder.AddColumn<bool>(
                name: "Artifact",
                table: "Segments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Section",
                table: "Segments",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Timestamp",
                table: "Segments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Calls",
                table: "ImportJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "InputCharacterLimit",
                table: "ImportJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxCalls",
                table: "ImportJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "OutputTokenLimit",
                table: "ImportJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ProposalRevision",
                table: "ImportJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ResumeJson",
                table: "ImportJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReviewCompleted",
                table: "ImportJobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Stage",
                table: "ImportJobs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "StageCursor",
                table: "ImportJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ImportIssues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    EvidenceJson = table.Column<string>(type: "text", nullable: false),
                    Blocking = table.Column<bool>(type: "boolean", nullable: false),
                    Resolution = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportIssues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportIssues_ImportJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "ImportJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ImportProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    ContentJson = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Current = table.Column<bool>(type: "boolean", nullable: false),
                    Excluded = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportProposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportProposals_ImportJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "ImportJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ImportResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Stage = table.Column<string>(type: "text", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    ResultJson = table.Column<string>(type: "text", nullable: false),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    Model = table.Column<string>(type: "text", nullable: false),
                    InputTokens = table.Column<int>(type: "integer", nullable: true),
                    OutputTokens = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportResults_ImportJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "ImportJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ImportSections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Start = table.Column<int>(type: "integer", nullable: false),
                    End = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportSections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportSections_ImportJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "ImportJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportIssues_JobId_Code",
                table: "ImportIssues",
                columns: new[] { "JobId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImportProposals_JobId_Key_Revision",
                table: "ImportProposals",
                columns: new[] { "JobId", "Key", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImportResults_JobId_Stage_Ordinal",
                table: "ImportResults",
                columns: new[] { "JobId", "Stage", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImportSections_JobId_Ordinal",
                table: "ImportSections",
                columns: new[] { "JobId", "Ordinal" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportIssues");

            migrationBuilder.DropTable(
                name: "ImportProposals");

            migrationBuilder.DropTable(
                name: "ImportResults");

            migrationBuilder.DropTable(
                name: "ImportSections");

            migrationBuilder.DropColumn(
                name: "Artifact",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "Section",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "Timestamp",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "Calls",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "InputCharacterLimit",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "MaxCalls",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "OutputTokenLimit",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "ProposalRevision",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "ResumeJson",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "ReviewCompleted",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "Stage",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "StageCursor",
                table: "ImportJobs");
        }
    }
}
