using System;
using System.Collections.Generic;
using System.IO;
using CopilotBuddy.Bots.PartyGrind.Logic;
using Styx.Logic.Inventory.Frames.Trade;
using Styx.PartyGrind;
using Xunit;

namespace CopilotBuddy.PartyGrind.Tests
{
    public sealed class SquadProvisioningAndTradeTests : IDisposable
    {
        private readonly string _tempDir;

        public SquadProvisioningAndTradeTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "PartyGrindProvTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
            }
        }

        private SquadSettings MakeSettings()
        {
            return new SquadSettings(Path.Combine(_tempDir, "SquadSettings.xml"))
            {
                MinMemberFoodStack = 10,
                TargetMemberFoodStack = 20,
                MinMemberDrinkStack = 10,
                TargetMemberDrinkStack = 20,
                TriggerTownFreeBagSlots = 2,
                TriggerTownDurabilityPct = 20,
                PreferMageFoodOverVendor = true,
                TrainSkillsInGroupTownRun = true,
                ProvisionCooldownSeconds = 30
            };
        }

        [Fact]
        public void WaterOnlyMage_DoesNotSuppressFoodVendorNeed()
        {
            var need = SquadTownRunService.EvaluateLocalTownNeeds(10, 90, false, false, true,
                20, 20, false, false, true, false, true, MakeSettings());
            Assert.True(need.NeedsFood);
            Assert.False(need.NeedsDrink);
            Assert.True(need.NeedsBuy);
        }

        [Fact]
        public void EvaluateConsumableNeeds_RespectsThresholdsCombatAndBagSpace()
        {
            var settings = MakeSettings();

            var needed = SquadProvisioningService.EvaluateConsumableNeeds(
                inCombat: false,
                freeNormalBagSlots: 6,
                foodCount: 4,
                drinkCount: 8,
                usesMana: true,
                hasHealthstone: false,
                squadHasWarlock: true,
                settings: settings);
            Assert.True(needed.WantsFood);
            Assert.True(needed.WantsDrink);
            Assert.True(needed.WantsHealthstone);

            // Warrior (non-mana) should never request drinks
            var warrior = SquadProvisioningService.EvaluateConsumableNeeds(
                inCombat: false,
                freeNormalBagSlots: 6,
                foodCount: 15,
                drinkCount: 0,
                usesMana: false,
                hasHealthstone: true,
                squadHasWarlock: true,
                settings: settings);
            Assert.False(warrior.WantsFood);
            Assert.False(warrior.WantsDrink);
            Assert.False(warrior.WantsHealthstone);

            // In combat or < 2 free bag slots -> never trigger trade requests
            var blocked = SquadProvisioningService.EvaluateConsumableNeeds(
                inCombat: false,
                freeNormalBagSlots: 1,
                foodCount: 0,
                drinkCount: 0,
                usesMana: true,
                hasHealthstone: false,
                squadHasWarlock: true,
                settings: settings);
            Assert.False(blocked.WantsFood);
            Assert.False(blocked.WantsDrink);
            Assert.False(blocked.WantsHealthstone);
            var combatBlocked = SquadProvisioningService.EvaluateConsumableNeeds(
                inCombat: true, freeNormalBagSlots: 6, foodCount: 0, drinkCount: 0,
                usesMana: true, hasHealthstone: false, squadHasWarlock: true, settings: settings);
            Assert.False(combatBlocked.WantsFood);
            Assert.False(combatBlocked.WantsDrink);
            Assert.False(combatBlocked.WantsHealthstone);
        }

        [Fact]
        public void SelectBestTradableItem_AndConjureRank_EnforcesReceiverLevelRequirement()
        {
            var mageBags = new[]
            {
                new TradableConsumableCandidate { Entry = 22895, Name = "Conjured Cinnamon Roll", RequiredLevel = 55, StackCount = 20, IsFood = true },
                new TradableConsumableCandidate { Entry = 1114, Name = "Conjured Rye", RequiredLevel = 15, StackCount = 16, IsFood = true },
                new TradableConsumableCandidate { Entry = 5349, Name = "Conjured Muffin", RequiredLevel = 1, StackCount = 20, IsFood = true },
                new TradableConsumableCandidate { Entry = 2136, Name = "Conjured Purified Water", RequiredLevel = 15, StackCount = 20, IsDrink = true },
                new TradableConsumableCandidate { Entry = 5350, Name = "Conjured Water", RequiredLevel = 1, StackCount = 20, IsDrink = true },
                new TradableConsumableCandidate { Entry = 5512, Name = "Healthstone", RequiredLevel = 10, StackCount = 1, IsSoulbound = true, IsHealthstone = true }
            };

            // Receiver is Level 12 -> must receive ReqLevel 1 water/food, NOT ReqLevel 15 or 55!
            var foodFor12 = SquadProvisioningService.SelectBestTradableItem(mageBags, receiverLevel: 12, wantFood: true, wantDrink: false, wantHealthstone: false);
            Assert.NotNull(foodFor12);
            Assert.Equal("Conjured Muffin", foodFor12!.Name);

            var drinkFor12 = SquadProvisioningService.SelectBestTradableItem(mageBags, receiverLevel: 12, wantFood: false, wantDrink: true, wantHealthstone: false);
            Assert.NotNull(drinkFor12);
            Assert.Equal("Conjured Water", drinkFor12!.Name);

            // Soulbound items must never be selected for trade
            var soulboundHs = SquadProvisioningService.SelectBestTradableItem(mageBags, receiverLevel: 80, wantFood: false, wantDrink: false, wantHealthstone: true);
            Assert.Null(soulboundHs);

            // Conjure rank check: Mage Lvl 25 casting for Receiver Lvl 12 must drop to Rank 2 (ReqLevel 5), NOT Rank 3 (ReqLevel 15)
            int? rankFor12 = SquadProvisioningService.SelectBestConjureRank(isWater: true, receiverLevel: 12, casterLevel: 25);
            Assert.Equal(2, rankFor12);

            int? rankFor20 = SquadProvisioningService.SelectBestConjureRank(isWater: true, receiverLevel: 20, casterLevel: 25);
            Assert.Equal(3, rankFor20);

            // Only ranks the provider actually knows are eligible; each learned record is kept.
            var knownRanks = new List<(int Rank, int SpellId)> { (1, 100), (2, 101), (2, 102) };
            Assert.Equal(2, SquadProvisioningService.SelectBestConjureRank(true, 20, 25, knownRanks));
            Assert.Equal(1, SquadProvisioningService.SelectBestConjureRank(
                true, 20, 25, new List<(int Rank, int SpellId)> { (1, 100) }));
            Assert.Null(SquadProvisioningService.SelectBestConjureRank(
                true, 20, 25, new List<(int Rank, int SpellId)>()));
        }

        [Fact]
        public void TradeFrame_IsPartnerAuthorized_BlocksUnauthorizedTradePartners()
        {
            Assert.True(TradeFrame.IsPartnerAuthorized("JainaMage", "jainamage"));
            Assert.False(TradeFrame.IsPartnerAuthorized("RandomGoldSeller", "JainaMage"));
            Assert.False(TradeFrame.IsPartnerAuthorized("", "JainaMage"));
            Assert.False(TradeFrame.IsPartnerAuthorized("JainaMage", ""));
        }

        [Fact]
        public void LevelFourMage_WaterDoesNotSuppressFoodVendorNeed()
        {
            bool mageCanProvideFood = SquadProvisioningService.SelectBestConjureRank(false, 4, 4).HasValue;
            bool mageCanProvideDrink = SquadProvisioningService.SelectBestConjureRank(true, 4, 4).HasValue;
            Assert.False(mageCanProvideFood);
            Assert.True(mageCanProvideDrink);
            var needs = SquadTownRunService.EvaluateLocalTownNeeds(
                10, 90, false, false, true, 20, 20, false, false, true,
                mageCanProvideFood, mageCanProvideDrink, MakeSettings());
            Assert.True(needs.NeedsFood);
            Assert.False(needs.NeedsDrink);
            Assert.True(needs.NeedsBuy);
        }

        [Fact]
        public void EvaluateLocalTownNeeds_SuppressesFoodDrinkVendorRunWhenMageActive()
        {
            var settings = MakeSettings();

            var withMage = SquadTownRunService.EvaluateLocalTownNeeds(
                freeNormalBagSlots: 10,
                lowestDurabilityPct: 90.0,
                hasFood: false,
                hasDrink: false,
                usesMana: true,
                desiredFoodAmount: 20,
                desiredDrinkAmount: 20,
                needClassTraining: false,
                hasMailRecipient: false,
                preferMageFoodOverVendor: true,
                mageCanProvideFood: true,
                mageCanProvideDrink: true,
                settings: settings);

            Assert.False(withMage.NeedsFood);
            Assert.False(withMage.NeedsDrink);
            Assert.False(withMage.NeedsBuy);
            Assert.False(withMage.Any);

            // A drink-only shortage is also covered when the mage can provide water.
            var drinkOnly = SquadTownRunService.EvaluateLocalTownNeeds(
                freeNormalBagSlots: 10,
                lowestDurabilityPct: 90.0,
                hasFood: true,
                hasDrink: false,
                usesMana: true,
                desiredFoodAmount: 20,
                desiredDrinkAmount: 20,
                needClassTraining: false,
                hasMailRecipient: false,
                preferMageFoodOverVendor: true,
                mageCanProvideFood: false,
                mageCanProvideDrink: true,
                settings: settings);
            Assert.False(drinkOnly.NeedsFood);
            Assert.False(drinkOnly.NeedsDrink);
            Assert.False(drinkOnly.NeedsBuy);

            // Without Mage, missing food/drink triggers NeedsBuy
            var withoutMage = SquadTownRunService.EvaluateLocalTownNeeds(
                freeNormalBagSlots: 10,
                lowestDurabilityPct: 90.0,
                hasFood: false,
                hasDrink: false,
                usesMana: true,
                desiredFoodAmount: 20,
                desiredDrinkAmount: 20,
                needClassTraining: false,
                hasMailRecipient: false,
                preferMageFoodOverVendor: true,
                mageCanProvideFood: false,
                mageCanProvideDrink: false,
                settings: settings);

            Assert.True(withoutMage.NeedsBuy);
            Assert.True(withoutMage.Any);
        }
    }
}
