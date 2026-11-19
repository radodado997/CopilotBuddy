using System.Threading;
using System.Threading.Tasks;
using CopilotBuddy.Bots.PartyGrind.Logic;
using Xunit;

namespace CopilotBuddy.PartyGrind.Tests
{
    public sealed class SquadPartySyncTests
    {
        [Fact]
        public void InviteScriptChecksPartyInSameExecutionBeforeInviting()
        {
            string script = SquadPartySyncService.BuildInviteScript("Sentino", true);
            Assert.StartsWith("if UnitInParty('Sentino') then return 'member' end;", script);
            Assert.Contains("InviteUnit('Sentino')", script);
            string waiting = SquadPartySyncService.BuildInviteScript("Sentino", false);
            Assert.StartsWith("if UnitInParty('Sentino') then return 'member' end;", waiting);
            Assert.DoesNotContain("InviteUnit", waiting);
        }

        [Fact]
        public void MemberIsNeverInvitedAndCanBeReinvitedAfterLeaving()
        {
            var tracker = new SquadInviteTracker();
            bool inParty = false;
            int invites = 0;
            string Check(bool mayInvite)
            {
                if (inParty) return "member";
                if (mayInvite) { invites++; return "invited"; }
                return "waiting";
            }

            tracker.Check("Sentino", 0, Check); // invite
            inParty = true;
            tracker.Check("Sentino", 100, Check); // membership clears pending cooldown
            tracker.Check("Sentino", 200, Check); // still in party
            Assert.Equal(1, invites);
            inParty = false;
            tracker.Check("Sentino", 300, Check); // left or kicked, even within old cooldown
            Assert.Equal(2, invites);
        }

        [Fact]
        public void AlreadyGroupedPlayerIsNotInvitedAcrossRepeatedChecks()
        {
            var tracker = new SquadInviteTracker();
            int invites = 0;
            for (long now = 0; now <= 30000; now += 10000)
                tracker.Check("Sentino", now, mayInvite =>
                {
                    // Mirrors the Lua guard: membership takes precedence over mayInvite.
                    bool inParty = true;
                    if (inParty) return "member";
                    if (mayInvite) invites++;
                    return "invited";
                });
            Assert.Equal(0, invites);
        }

        [Fact]
        public void PendingOrDeclinedInvitationIsRetriedAfterCooldown()
        {
            var tracker = new SquadInviteTracker();
            int invites = 0, checks = 0;
            string Check(bool mayInvite)
            {
                checks++;
                if (mayInvite) { invites++; return "invited"; }
                return "waiting";
            }

            tracker.Check("Ksionc", 0, Check);
            tracker.Check("KSIONC", 9999, Check);
            Assert.Equal(1, invites);
            Assert.Equal(2, checks); // membership is still queried during cooldown
            tracker.Check("Ksionc", 10000, Check);
            Assert.Equal(2, invites); // rejection and expiry do not permanently suppress retries
        }

        [Fact]
        public void SimultaneousUpdatesOnlyInviteEachPlayerOnce()
        {
            var tracker = new SquadInviteTracker();
            int invites = 0;
            Parallel.For(0, 100, _ => tracker.Check("Sentino", 1000, mayInvite =>
            {
                if (mayInvite) { Interlocked.Increment(ref invites); return "invited"; }
                return "waiting";
            }));
            tracker.Check("Belmondo", 1000, mayInvite =>
            {
                if (mayInvite) { Interlocked.Increment(ref invites); return "invited"; }
                return "waiting";
            });
            Assert.Equal(2, invites);
        }

        [Fact]
        public void UnknownLuaResultDoesNotCauseImmediateDuplicate()
        {
            var tracker = new SquadInviteTracker();
            tracker.Check("Sentino", 0, _ => string.Empty);
            bool allowed = true;
            tracker.Check("Sentino", 100, mayInvite => { allowed = mayInvite; return "waiting"; });
            Assert.False(allowed);
        }
    }
}
