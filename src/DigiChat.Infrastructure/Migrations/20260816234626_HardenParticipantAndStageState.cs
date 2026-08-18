using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DigiChat.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HardenParticipantAndStageState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HeldForReincarnation",
                table: "Participants",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Before this flag existed, held status was reconstructed from the
            // current generation's death time. Preserve that exact behavior on
            // upgrade so undoing an in-progress death does not strand viewers
            // who chatted while the old binary was running.
            migrationBuilder.Sql("""
                UPDATE p
                SET p.HeldForReincarnation = 1
                FROM Participants AS p
                INNER JOIN AppStates AS s
                    ON s.CurrentStreamSessionId = p.StreamSessionId
                INNER JOIN Generations AS g
                    ON g.Id = s.CurrentGenerationId
                WHERE g.DiedUtc IS NOT NULL
                  AND p.JoinedUtc >= g.DiedUtc
                """);

            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1 FROM AppStates WHERE CurrentStage < 0 OR CurrentStage > 4
                    UNION ALL
                    SELECT 1 FROM Transitions
                    WHERE FromStage < 0 OR FromStage > 4
                       OR ToStage < 0 OR ToStage > 4
                       OR Type < 0 OR Type > 2
                )
                    THROW 51000, 'DigiChat found an invalid legacy stage or transition value. Restore the verified backup and inspect the affected row before retrying the migration.', 1;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Transitions_FromStage",
                table: "Transitions",
                sql: "[FromStage] >= 0 AND [FromStage] <= 4");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Transitions_ToStage",
                table: "Transitions",
                sql: "[ToStage] >= 0 AND [ToStage] <= 4");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Transitions_Type",
                table: "Transitions",
                sql: "[Type] >= 0 AND [Type] <= 2");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AppStates_CurrentStage",
                table: "AppStates",
                sql: "[CurrentStage] >= 0 AND [CurrentStage] <= 4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Transitions_FromStage",
                table: "Transitions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Transitions_ToStage",
                table: "Transitions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Transitions_Type",
                table: "Transitions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AppStates_CurrentStage",
                table: "AppStates");

            migrationBuilder.DropColumn(
                name: "HeldForReincarnation",
                table: "Participants");
        }
    }
}
