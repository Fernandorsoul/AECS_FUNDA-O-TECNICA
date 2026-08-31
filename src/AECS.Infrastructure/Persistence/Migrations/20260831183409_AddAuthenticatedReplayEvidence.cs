using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AECS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthenticatedReplayEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_execution_evidence_promotion_count",
                table: "execution_evidence");

            migrationBuilder.RenameColumn(
                name: "PromotionCount",
                table: "execution_evidence",
                newName: "EventCount");

            migrationBuilder.CreateTable(
                name: "execution_evidence_replay_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionEvidenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    PreviousSignature = table.Column<string>(type: "text", nullable: false),
                    ReplayJson = table.Column<string>(type: "jsonb", nullable: false),
                    SealJson = table.Column<string>(type: "jsonb", nullable: false),
                    SignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_execution_evidence_replay_events", x => x.Id);
                    table.CheckConstraint("ck_execution_evidence_replay_sequence", "\"Sequence\" > 0");
                    table.ForeignKey(
                        name: "FK_execution_evidence_replay_events_execution_evidence_Executi~",
                        column: x => x.ExecutionEvidenceId,
                        principalTable: "execution_evidence",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_execution_evidence_event_count",
                table: "execution_evidence",
                sql: "\"EventCount\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_execution_evidence_replay_events_ExecutionEvidenceId_Sequen~",
                table: "execution_evidence_replay_events",
                columns: new[] { "ExecutionEvidenceId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_execution_evidence_replay_events_SignedAt",
                table: "execution_evidence_replay_events",
                column: "SignedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "execution_evidence_replay_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_execution_evidence_event_count",
                table: "execution_evidence");

            migrationBuilder.RenameColumn(
                name: "EventCount",
                table: "execution_evidence",
                newName: "PromotionCount");

            migrationBuilder.AddCheckConstraint(
                name: "ck_execution_evidence_promotion_count",
                table: "execution_evidence",
                sql: "\"PromotionCount\" >= 0");
        }
    }
}
