using Styx;
using Styx.WoWInternals.WoWObjects;
using Xunit;

namespace CopilotBuddy.PartyGrind.Tests
{
    public sealed class SquadNpcVendorTests
    {
        [Theory]
        [InlineData(UnitNPCFlags.Vendor)]
        [InlineData(UnitNPCFlags.AmmoVendor)]
        [InlineData(UnitNPCFlags.FoodVendor)]
        [InlineData(UnitNPCFlags.PoisionVendor)]
        [InlineData(UnitNPCFlags.ReagentVendor)]
        public void AnySingleMerchantFlagIsRecognized(UnitNPCFlags flag)
        {
            Assert.True(WoWUnit.HasAnyVendorFlag((uint)flag));
            Assert.True(WoWUnit.HasAnyVendorFlag((uint)(flag | UnitNPCFlags.Repair)));
        }

        [Theory]
        [InlineData(UnitNPCFlags.None)]
        [InlineData(UnitNPCFlags.Repair)]
        [InlineData(UnitNPCFlags.ClassTrainer)]
        [InlineData(UnitNPCFlags.Gossip)]
        public void NonVendorFlagsDoNotIdentifyAMerchant(UnitNPCFlags flag)
        {
            Assert.False(WoWUnit.HasAnyVendorFlag((uint)flag));
        }
    }
}
