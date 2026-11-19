using CopilotBuddy.Bots.PartyGrind.Logic;
using Xunit;

namespace CopilotBuddy.PartyGrind.Tests
{
    public sealed class SquadTownVendorAttemptsTests
    {
        private static readonly TownNeedsEvaluation Sell = new() { NeedsSell = true };

        [Fact]
        public void SuccessfulVisitWaitsForServerUpdatesBeforeRetrying()
        {
            var retry = new SquadVendorAttempts();
            Assert.Equal(SquadVendorAttemptDecision.Try, retry.Evaluate(0, Sell, 1));
            retry.Record(0, Sell, 1, true);
            Assert.Equal(SquadVendorAttemptDecision.Wait, retry.Evaluate(1499, Sell, 1));
            Assert.Equal(SquadVendorAttemptDecision.Try, retry.Evaluate(1500, Sell, 1));
            Assert.Equal(1, retry.Count);
        }

        [Fact]
        public void BlocksOnlyAfterFiveAttemptsAndFinalConfirmationDelay()
        {
            var retry = new SquadVendorAttempts();
            for (int i = 0; i < 5; i++)
            {
                long now = i * 1500L;
                Assert.Equal(SquadVendorAttemptDecision.Try, retry.Evaluate(now, Sell, 0));
                retry.Record(now, Sell, 0, false);
            }
            Assert.Equal(SquadVendorAttemptDecision.Wait, retry.Evaluate(7499, Sell, 0));
            Assert.Equal(SquadVendorAttemptDecision.Exhausted, retry.Evaluate(7500, Sell, 0));
            retry.Reset();
            Assert.Equal(SquadVendorAttemptDecision.Try, retry.Evaluate(7500, Sell, 0));
            Assert.False(retry.LastInteractionSucceeded);
        }

        [Fact]
        public void PartialSalesEarnFreshAttemptsButFailedInteractionDoesNot()
        {
            var retry = new SquadVendorAttempts();
            retry.Record(0, Sell, 0, true);
            Assert.Equal(SquadVendorAttemptDecision.Try, retry.Evaluate(1500, Sell, 1));
            Assert.Equal(0, retry.Count);
            retry.Record(1500, Sell, 1, false);
            Assert.Equal(SquadVendorAttemptDecision.Try, retry.Evaluate(3000, Sell, 2));
            Assert.Equal(1, retry.Count);
        }

        [Fact]
        public void MailFirstWaitsForPendingVendorConfirmationWithoutResettingAttempts()
        {
            var retry = new SquadVendorAttempts();
            Assert.False(SquadTownRunService.ShouldWaitForVendorBeforeMail(true, retry, 0));
            retry.Record(1000, Sell, 0, true);
            Assert.False(SquadTownRunService.ShouldWaitForVendorBeforeMail(false, retry, 1001));
            Assert.True(SquadTownRunService.ShouldWaitForVendorBeforeMail(true, retry, 1001));
            Assert.Equal(1, retry.Count);
            Assert.Equal(SquadVendorAttemptDecision.Wait, retry.Evaluate(2499, Sell, 0));
            Assert.True(SquadTownRunService.ShouldWaitForVendorBeforeMail(true, retry, 2499));
            Assert.False(SquadTownRunService.ShouldWaitForVendorBeforeMail(true, retry, 2500));
            Assert.Equal(1, retry.Count);
            Assert.Equal(SquadVendorAttemptDecision.Try, retry.Evaluate(2500, Sell, 0));
        }

        [Fact]
        public void CompletedRepairOrConsumableNeedIsRecognizedAsProgress()
        {
            var retry = new SquadVendorAttempts();
            var before = new TownNeedsEvaluation { NeedsRepair = true, NeedsBuy = true, NeedsFood = true };
            var after = new TownNeedsEvaluation { NeedsRepair = true };
            retry.Record(0, before, 2, true);
            Assert.Equal(SquadVendorAttemptDecision.Try, retry.Evaluate(1500, after, 2));
            Assert.Equal(0, retry.Count);
        }
    }
}
