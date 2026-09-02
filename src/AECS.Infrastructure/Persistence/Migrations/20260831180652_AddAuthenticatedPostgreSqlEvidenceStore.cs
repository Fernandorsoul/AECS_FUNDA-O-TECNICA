using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AECS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthenticatedPostgreSqlEvidenceStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EvidenceEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    AgentRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    Authority = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "execution_evidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AgentRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateId = table.Column<Guid>(type: "uuid", nullable: false),
                    SchemaVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EvidenceContentHash = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    EvidenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    EvidenceSealJson = table.Column<string>(type: "jsonb", nullable: false),
                    ChainSealJson = table.Column<string>(type: "jsonb", nullable: false),
                    PromotionCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_execution_evidence", x => x.Id);
                    table.CheckConstraint("ck_execution_evidence_promotion_count", "\"PromotionCount\" >= 0");
                    table.CheckConstraint("ck_execution_evidence_schema_version", "\"SchemaVersion\" = 'aecs.execution-evidence/v1'");
                });

            migrationBuilder.CreateTable(
                name: "ExperimentRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TotalTasks = table.Column<int>(type: "integer", nullable: false),
                    VerifiedCount = table.Column<int>(type: "integer", nullable: false),
                    RejectedCount = table.Column<int>(type: "integer", nullable: false),
                    HumanReviewCount = table.Column<int>(type: "integer", nullable: false),
                    TotalDuration = table.Column<TimeSpan>(type: "interval", nullable: false),
                    TotalCost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    Cpvc = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    FirstPassRate = table.Column<double>(type: "double precision", nullable: false),
                    ResultsJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExperimentRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "execution_evidence_promotion_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionEvidenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    PreviousSignature = table.Column<string>(type: "text", nullable: false),
                    PromotionJson = table.Column<string>(type: "jsonb", nullable: false),
                    SealJson = table.Column<string>(type: "jsonb", nullable: false),
                    SignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_execution_evidence_promotion_events", x => x.Id);
                    table.CheckConstraint("ck_execution_evidence_promotion_sequence", "\"Sequence\" > 0");
                    table.ForeignKey(
                        name: "FK_execution_evidence_promotion_events_execution_evidence_Exec~",
                        column: x => x.ExecutionEvidenceId,
                        principalTable: "execution_evidence",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceEvents_OccurredAt",
                table: "EvidenceEvents",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceEvents_TaskId",
                table: "EvidenceEvents",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_execution_evidence_AgentRunId",
                table: "execution_evidence",
                column: "AgentRunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_execution_evidence_CandidateId",
                table: "execution_evidence",
                column: "CandidateId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_execution_evidence_CreatedAt",
                table: "execution_evidence",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_execution_evidence_TaskId",
                table: "execution_evidence",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_execution_evidence_promotion_events_ExecutionEvidenceId_Seq~",
                table: "execution_evidence_promotion_events",
                columns: new[] { "ExecutionEvidenceId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_execution_evidence_promotion_events_SignedAt",
                table: "execution_evidence_promotion_events",
                column: "SignedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentRuns_ExecutedAt",
                table: "ExperimentRuns",
                column: "ExecutedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EvidenceEvents");

            migrationBuilder.DropTable(
                name: "execution_evidence_promotion_events");

            migrationBuilder.DropTable(
                name: "ExperimentRuns");

            migrationBuilder.DropTable(
                name: "execution_evidence");
        }
    }
}
