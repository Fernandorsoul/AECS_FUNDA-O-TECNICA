using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AECS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHistoricalDecisionRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "historical_decisions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    SchemaVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Authority = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ReviewStatus = table.Column<int>(type: "integer", nullable: false),
                    Enforcement = table.Column<int>(type: "integer", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ContentHash = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    DecisionJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_historical_decisions", x => new { x.Id, x.Version });
                    table.CheckConstraint("ck_historical_decisions_schema_version", "\"SchemaVersion\" = 'aecs.historical-decision/v1'");
                    table.CheckConstraint("ck_historical_decisions_version", "\"Version\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "historical_decision_suppressions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    SchemaVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DecisionId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DecisionVersion = table.Column<int>(type: "integer", nullable: false),
                    Actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    SuppressionJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_historical_decision_suppressions", x => new { x.Id, x.Version });
                    table.CheckConstraint("ck_historical_decision_suppressions_schema_version", "\"SchemaVersion\" = 'aecs.historical-decision-suppression/v1'");
                    table.CheckConstraint("ck_historical_decision_suppressions_version", "\"Version\" > 0");
                    table.ForeignKey(
                        name: "FK_historical_decision_suppressions_historical_decisions_Decis~",
                        columns: x => new { x.DecisionId, x.DecisionVersion },
                        principalTable: "historical_decisions",
                        principalColumns: new[] { "Id", "Version" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_historical_decision_suppressions_DecisionId_DecisionVersion",
                table: "historical_decision_suppressions",
                columns: new[] { "DecisionId", "DecisionVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_historical_decision_suppressions_ExpiresAt",
                table: "historical_decision_suppressions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_historical_decisions_ReviewStatus",
                table: "historical_decisions",
                column: "ReviewStatus");

            migrationBuilder.CreateIndex(
                name: "IX_historical_decisions_Source",
                table: "historical_decisions",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_historical_decisions_ValidUntil",
                table: "historical_decisions",
                column: "ValidUntil");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "historical_decision_suppressions");

            migrationBuilder.DropTable(
                name: "historical_decisions");
        }
    }
}
