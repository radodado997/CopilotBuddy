using CopilotBuddy.Bots.PartyGrind.Logic;
using Xunit;

namespace CopilotBuddy.PartyGrind.Tests
{
    public sealed class SquadTownRunMandatoryVendorTests
    {
        [Fact]
        public void VendorWorkNeeds_AreMandatoryEvenWhenThresholdsAreQuiet()
        {
            var quiet = new TownNeedsEvaluation
            {
                NeedsSell = false,
                NeedsRepair = false,
                NeedsBuy = false
            };

            var work = SquadTownRunService.GetVendorWorkNeeds(quiet);

            Assert.True(work.NeedsSell);
            Assert.True(work.NeedsRepair);
            Assert.False(work.NeedsBuy);
        }

        [Fact]
        public void VendorWorkNeeds_PreserveBuyAndConsumableNeeds()
        {
            var needs = new TownNeedsEvaluation
            {
                NeedsSell = false,
                NeedsRepair = false,
                NeedsBuy = true,
                NeedsFood = true,
                NeedsDrink = true
            };

            var work = SquadTownRunService.GetVendorWorkNeeds(needs);

            Assert.True(work.NeedsSell);
            Assert.True(work.NeedsRepair);
            Assert.True(work.NeedsBuy);
            Assert.True(work.NeedsFood);
            Assert.True(work.NeedsDrink);
        }

        [Fact]
        public void VendorWorkNeeds_DoNotInventMailOrTrainingNeeds()
        {
            var needs = new TownNeedsEvaluation { NeedsSell = true, NeedsRepair = true };

            var work = SquadTownRunService.GetVendorWorkNeeds(needs);

            Assert.False(work.NeedsMail);
            Assert.False(work.NeedsTrain);
        }

        [Theory]
        [InlineData(false, false, false, false, false)]
        [InlineData(true, false, false, false, true)]
        [InlineData(true, true, false, false, false)]
        [InlineData(true, false, true, false, false)]
        [InlineData(true, false, false, true, false)]
        public void VendorCompletion_RequiresVisitAndResolvedNeeds(
            bool visited, bool sell, bool repair, bool buy, bool expected)
        {
            var needs = new TownNeedsEvaluation
            {
                NeedsSell = sell,
                NeedsRepair = repair,
                NeedsBuy = buy
            };

            Assert.Equal(expected, SquadTownRunService.IsVendorWorkComplete(visited, needs));
        }
    }
}
