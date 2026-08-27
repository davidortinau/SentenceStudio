using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentenceStudio.Api.Coach.Persistence.Migrations
{
    /// <summary>
    /// Holds a turn to one write proposal, in the database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both surfaces carry one proposal per turn: the live turn response has a single write
    /// operation, and rebuilt history anchors a single card to the turn's last coach message. The
    /// ledger's own count already refuses a second call with a sentence the model can act on; this
    /// index is what makes that refusal true when two requests for one turn arrive at once and
    /// both counts read zero.
    /// </para>
    /// <para>
    /// Additive and non-destructive. Reversal rows carry a derived turn identity of their own, so
    /// they do not contend for the slot, and rows written before a turn identity was required
    /// carry null — which PostgreSQL treats as distinct, so they neither collide with each other
    /// nor block a new proposal.
    /// </para>
    /// <para>
    /// The discovery attributes are inline rather than in a generated designer file, and they are
    /// load-bearing: without <c>[Migration]</c> EF does not see this migration at all and
    /// <c>MigrateAsync</c> skips it in silence, leaving the count in the ledger as the only bound
    /// and the concurrent case unguarded — which is the failure this exists to close.
    /// </para>
    /// </remarks>
    [DbContext(typeof(CoachDbContext))]
    [Migration("20260819130000_AddCoachWriteOperationTurnUniqueness")]
    public partial class AddCoachWriteOperationTurnUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_CoachWriteOperation_UserProfileId_ConversationId_TurnId",
                table: "CoachWriteOperation",
                columns: new[] { "UserProfileId", "ConversationId", "TurnId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CoachWriteOperation_UserProfileId_ConversationId_TurnId",
                table: "CoachWriteOperation");
        }
    }
}
