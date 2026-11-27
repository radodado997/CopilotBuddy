using System;
using System.Collections.Generic;
using System.IO;
using CopilotBuddy.Bots.PartyGrind.Logic;
using Styx;
using Styx.Combat.CombatRoutine;
using Styx.Logic.Inventory.Frames.Trade;
using Styx.PartyGrind;
using Styx.PartyGrind.IPC;
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
        public void HealthstoneSupply_WithoutShardRequiresReadyStone()
        {
            var knownRanks = new[] { (1, 6201), (2, 6202) };
            Assert.Null(SquadProvisioningService.GetHealthstoneMinReceiverLevel(22, false, knownRanks, null));
            Assert.Equal(22, SquadProvisioningService.GetHealthstoneMinReceiverLevel(22, false, knownRanks, 22));
        }

        [Fact]
        public void HealthstoneSupply_WithShardUsesLowestLearnedRankOrReadyStone()
        {
            var knownRanks = new[] { (2, 6202), (3, 5699) };
            Assert.Null(SquadProvisioningService.GetHealthstoneMinReceiverLevel(21, true, knownRanks, null));
            Assert.Equal(22, SquadProvisioningService.GetHealthstoneMinReceiverLevel(34, true, knownRanks, 34));
            Assert.Equal(10, SquadProvisioningService.GetHealthstoneMinReceiverLevel(34, true, knownRanks, 10));
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
            int? foodRank = SquadProvisioningService.SelectBestConjureRank(false, 4, 4);
            int? waterRank = SquadProvisioningService.SelectBestConjureRank(true, 4, 4);
            Assert.Null(foodRank);
            Assert.NotNull(waterRank);
            bool mageCanProvideFood = foodRank.HasValue;
            bool mageCanProvideDrink = waterRank.HasValue;
            Assert.False(mageCanProvideFood);
            Assert.True(mageCanProvideDrink);
            var needs = SquadTownRunService.EvaluateLocalTownNeeds(
                10, 90, false, false, true, 20, 20, false, false, true,
                mageCanProvideFood, mageCanProvideDrink, MakeSettings());
            Assert.True(needs.NeedsFood);
            Assert.False(needs.NeedsDrink);
            Assert.True(needs.NeedsBuy);
            var drinkOnly = SquadTownRunService.EvaluateLocalTownNeeds(
                10, 90, true, false, true, 20, 20, false, false, true,
                mageCanProvideFood, mageCanProvideDrink, MakeSettings());
            Assert.False(drinkOnly.NeedsBuy);
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

        [Fact]
        public void EvaluateTick_AssignsProvisioning_WhenMemberNeedsWaterEvenIfRestingRequired()
        {
            var coordinator = new SquadCoordinatorService();
            var settings = MakeSettings();
            settings.TeamId = "TestSquad";
            settings.IsCoordinator = true;
            settings.Role = SquadRole.TankLeader;
            settings.EnableMageProvisioning = true;
            settings.TriggerRestHealthPct = 50;
            settings.TriggerRestManaPct = 50;
            settings.ResumeRestHealthPct = 80;
            settings.ResumeRestManaPct = 80;

            var leader = new SquadMemberState
            {
                CharacterName = "TankA",
                Guid = 100,
                Class = WoWClass.Warrior,
                Role = SquadRole.TankLeader,
                Level = 20,
                MapId = 0,
                X = 100f,
                Y = 100f,
                Z = 0f,
                HealthPercent = 100f,
                PowerPercent = 0f,
                PowerType = WoWPowerType.Rage
            };

            var mage = new SquadConnectedMemberSnapshot
            {
                CharacterName = "MageB",
                SlotIndex = 1,
                IsConnected = true,
                LastReceivedUtcMs = 1000,
                Handshake = new SquadHandshake
                {
                    CharacterName = "MageB",
                    Guid = 201,
                    Class = WoWClass.Mage,
                    Role = SquadRole.DpsRanged,
                    Level = 20
                },
                LatestState = new SquadMemberState
                {
                    CharacterName = "MageB",
                    Guid = 201,
                    Class = WoWClass.Mage,
                    Role = SquadRole.DpsRanged,
                    Level = 20,
                    MapId = 0,
                    X = 100f,
                    Y = 100f,
                    Z = 0f,
                    HealthPercent = 100f,
                    PowerPercent = 100f,
                    PowerType = WoWPowerType.Mana,
                    FreeBagSlots = 10,
                    CanProvideMageDrink = true
                }
            };

            var priest = new SquadConnectedMemberSnapshot
            {
                CharacterName = "PriestC",
                SlotIndex = 2,
                IsConnected = true,
                LastReceivedUtcMs = 1000,
                Handshake = new SquadHandshake
                {
                    CharacterName = "PriestC",
                    Guid = 202,
                    Class = WoWClass.Priest,
                    Role = SquadRole.Healer,
                    Level = 20
                },
                LatestState = new SquadMemberState
                {
                    CharacterName = "PriestC",
                    Guid = 202,
                    Class = WoWClass.Priest,
                    Role = SquadRole.Healer,
                    Level = 20,
                    MapId = 0,
                    X = 100f,
                    Y = 100f,
                    Z = 0f,
                    HealthPercent = 100f,
                    PowerPercent = 20f,
                    PowerType = WoWPowerType.Mana,
                    FreeBagSlots = 8,
                    WantsDrink = true
                }
            };

            var profile = new SquadProfileSnapshot();
            var state = coordinator.EvaluateTick(settings, leader, new[] { mage, priest }, profile, 0, 0, 0, 0, 1000);

            Assert.Equal(SquadPhase.Provisioning, state.Phase);
            Assert.Equal("MageB", state.ActiveProvisionerName);
            Assert.Equal("PriestC", state.ActiveTradeTargetName);
        }

        [Fact]
        public void CanInitiatePull_ReturnsFalse_WhenActiveProvisionerAssigned()
        {
            var leader = new SquadLeaderState
            {
                Phase = SquadPhase.Grinding,
                ActiveProvisionerName = "MageB",
                ActiveTradeTargetName = "PriestC",
                X = 100f,
                Y = 100f,
                Z = 0f,
                Members = new List<SquadMemberSummary>
                {
                    new SquadMemberSummary { CharacterName = "TankA", IsConnected = true, HasTelemetry = true, X = 100f, Y = 100f, Z = 0f },
                    new SquadMemberSummary { CharacterName = "MageB", IsConnected = true, HasTelemetry = true, X = 100f, Y = 100f, Z = 0f }
                }
            };

            Assert.False(SquadTankBridge.CanInitiatePull(leader, 30f));

            leader.ActiveProvisionerName = string.Empty;
            Assert.True(SquadTankBridge.CanInitiatePull(leader, 30f));
        }

        [Fact]
        public void DungeonItemConsolidation_IdentifiesMissingTransfers_AndCraftsOnlyOnEligibleHolder()
        {
            // Tank lacks 7741 (Shaft of Tsol), carrier holds tradable 7741 -> transfer [7741].
            var needed = SquadProvisioningService.GetNeededDungeonTransferEntries(
                carrierTradableItems: new uint[] { 7741U },
                receiverHeldItems: new uint[] { 7740U },
                activeDungeonId: 22U);
            Assert.Equal(new uint[] { 7741U }, needed);

            // Tank already crafted 7733 (Staff of Prehistoria) -> nothing to transfer.
            var alreadyComplete = SquadProvisioningService.GetNeededDungeonTransferEntries(
                carrierTradableItems: new uint[] { 7740U, 7741U },
                receiverHeldItems: new uint[] { 7733U },
                activeDungeonId: 22U);
            Assert.Empty(alreadyComplete);

            // Different dungeon does not trigger Uldaman recipe.
            var wrongDungeon = SquadProvisioningService.GetNeededDungeonTransferEntries(
                carrierTradableItems: new uint[] { 7741U },
                receiverHeldItems: Array.Empty<uint>(),
                activeDungeonId: 36U);
            Assert.Empty(wrongDungeon);
            var unknownDungeon = SquadProvisioningService.GetNeededDungeonTransferEntries(
                carrierTradableItems: new uint[] { 7741U },
                receiverHeldItems: Array.Empty<uint>(),
                activeDungeonId: 0U);
            Assert.Empty(unknownDungeon);

            Assert.Null(SquadProvisioningService.SelectDungeonRecipeCraftComponent(
                localHeldItems: new uint[] { 7740U, 7741U },
                localTradableItems: new uint[] { 7740U, 7741U },
                activeDungeonId: 22U,
                isDesignatedReceiver: false,
                hasOtherEligibleTankReceiver: true));

            Assert.Equal(7740U, SquadProvisioningService.SelectDungeonRecipeCraftComponent(
                localHeldItems: new uint[] { 7740U, 7741U },
                localTradableItems: new uint[] { 7740U, 7741U },
                activeDungeonId: 22U,
                isDesignatedReceiver: false,
                hasOtherEligibleTankReceiver: false));

            Assert.Equal(7740U, SquadProvisioningService.SelectDungeonRecipeCraftComponent(
                localHeldItems: new uint[] { 7740U, 7741U },
                localTradableItems: new uint[] { 7740U, 7741U },
                activeDungeonId: 22U,
                isDesignatedReceiver: true,
                hasOtherEligibleTankReceiver: false));

            Assert.Equal(7740U, SquadProvisioningService.SelectDungeonRecipeCraftComponent(
                localHeldItems: new uint[] { 7740U, 7741U },
                localTradableItems: new uint[] { 7741U },
                activeDungeonId: 22U,
                isDesignatedReceiver: false,
                hasOtherEligibleTankReceiver: true));
            Assert.Null(SquadProvisioningService.SelectDungeonRecipeCraftComponent(
                localHeldItems: new uint[] { 7740U, 7741U, 7733U },
                localTradableItems: new uint[] { 7740U, 7741U },
                activeDungeonId: 22U,
                isDesignatedReceiver: true,
                hasOtherEligibleTankReceiver: false));
        }

        [Fact]
        public void DungeonItemTransfer_CoordinatorArbitratesFollowerToTankAfterLooting_AndClearsWhenDone()
        {
            var settings = MakeSettings();
            settings.BotMode = SquadBotMode.Dungeon;
            settings.DungeonLootMode = SquadDungeonLootMode.BossesOnly;
            settings.Role = SquadRole.TankLeader;

            var coordinator = new SquadCoordinatorService();
            var tank = new SquadMemberState
            {
                CharacterName = "TankA",
                Guid = 100UL,
                Class = WoWClass.Paladin,
                Role = SquadRole.TankLeader,
                Level = 40,
                MapId = 70U,
                X = -240f,
                Y = 230f,
                Z = -50f,
                HealthPercent = 100f,
                PowerPercent = 100f,
                PowerType = WoWPowerType.Mana,
                FreeBagSlots = 8,
                IsInsideDungeon = true,
                HasDungeonTelemetry = true,
                HeldDungeonItems = new List<uint> { 7740U }
            };
            SquadConnectedMemberSnapshot MakeWarlockSnap(bool isLooting, long ts, List<uint> held, List<uint> tradable) => new()
            {
                CharacterName = "LockB",
                SlotIndex = 1,
                IsConnected = true,
                LastReceivedUtcMs = ts,
                Handshake = new SquadHandshake
                {
                    CharacterName = "LockB",
                    Guid = 202UL,
                    Class = WoWClass.Warlock,
                    Role = SquadRole.DpsRanged,
                    Level = 39
                },
                LatestState = new SquadMemberState
                {
                    CharacterName = "LockB",
                    Guid = 202UL,
                    Class = WoWClass.Warlock,
                    Role = SquadRole.DpsRanged,
                    Level = 39,
                    MapId = 70U,
                    X = -238f,
                    Y = 232f,
                    Z = -50f,
                    HealthPercent = 100f,
                    PowerPercent = 100f,
                    PowerType = WoWPowerType.Mana,
                    FreeBagSlots = 8,
                    IsInsideDungeon = true,
                    HasDungeonTelemetry = true,
                    IsLooting = isLooting,
                    HeldDungeonItems = held,
                    TradableDungeonItems = tradable
                }
            };

            var dungeon = new SquadDungeonSnapshot
            {
                IsInsideDungeon = true,
                ActiveDungeonId = 22U,
                ActiveDungeonMapId = 70U,
                EffectiveTankGuid = 100UL,
                HasRandomPartyMembers = true
            };
            var profile = new SquadProfileSnapshot { MapId = 70U };

            // Wait while follower is still looting.
            var whileLooting = coordinator.EvaluateTick(
                settings, tank,
                new[] { MakeWarlockSnap(true, 1000L, new List<uint> { 7741U }, new List<uint> { 7741U }) },
                profile, 0UL, 0UL, 0L, 0f, 1000L, dungeon: dungeon);
            Assert.NotEqual(SquadPhase.Provisioning, whileLooting.Phase);
            Assert.Equal(string.Empty, whileLooting.ActiveProvisionerName);

            // Once looting completes, coordinator assigns LockB -> TankA even in LFG groups.
            var assigned = coordinator.EvaluateTick(
                settings, tank,
                new[] { MakeWarlockSnap(false, 1500L, new List<uint> { 7741U }, new List<uint> { 7741U }) },
                profile, 0UL, 0UL, 0L, 0f, 1500L, dungeon: dungeon);
            Assert.Equal(SquadPhase.Provisioning, assigned.Phase);
            Assert.Equal("LockB", assigned.ActiveProvisionerName);
            Assert.Equal("TankA", assigned.ActiveTradeTargetName);

            // After transfer completes, coordinator clears provisioning cleanly.
            tank.HeldDungeonItems.Add(7741U);
            var resolved = coordinator.EvaluateTick(
                settings, tank,
                new[] { MakeWarlockSnap(false, 2500L, new List<uint>(), new List<uint>()) },
                profile, 0UL, 0UL, 0L, 0f, 2500L, dungeon: dungeon);
            Assert.Equal(SquadPhase.DungeonClearing, resolved.Phase);
            Assert.Equal(string.Empty, resolved.ActiveProvisionerName);
            Assert.Equal(string.Empty, resolved.ActiveTradeTargetName);

            tank.HeldDungeonItems.Clear();
            tank.FreeBagSlots = 1;
            var lowBagCoordinator = new SquadCoordinatorService();
            var lowBagResult = lowBagCoordinator.EvaluateTick(
                settings, tank,
                new[] { MakeWarlockSnap(false, 3000L, new List<uint> { 7740U, 7741U }, new List<uint> { 7740U, 7741U }) },
                profile, 0UL, 0UL, 0L, 0f, 3000L, dungeon: dungeon);
            Assert.Equal(string.Empty, lowBagResult.ActiveProvisionerName);

            var singlePieceCoordinator = new SquadCoordinatorService();
            var splitCarrierBlock = singlePieceCoordinator.EvaluateTick(
                settings, tank,
                new[] { MakeWarlockSnap(false, 3500L, new List<uint> { 7741U }, new List<uint> { 7741U }) },
                profile, 0UL, 0UL, 0L, 0f, 3500L, dungeon: dungeon);
            Assert.Equal(string.Empty, splitCarrierBlock.ActiveProvisionerName);
            Assert.False(SquadProvisioningService.HasRequiredTankReceiverBagSpace(
                Array.Empty<uint>(), receiverFreeBagSlots: 1, new uint[] { 7741U }, activeDungeonId: 22U));
            Assert.True(SquadProvisioningService.HasRequiredTankReceiverBagSpace(
                new uint[] { 7740U }, receiverFreeBagSlots: 1, new uint[] { 7741U }, activeDungeonId: 22U));

            // Carrier holding both parts with one non-tradable keeps both to self-craft instead of stranding the tradable part.
            Assert.Empty(SquadProvisioningService.GetNeededDungeonTransferEntries(
                carrierTradableItems: new uint[] { 7741U },
                receiverHeldItems: Array.Empty<uint>(),
                activeDungeonId: 22U,
                carrierHeldItems: new uint[] { 7740U, 7741U }));
            Assert.True(SquadProvisioningService.HasRequiredTankReceiverBagSpace(
                Array.Empty<uint>(),
                receiverFreeBagSlots: 0,
                carrierTradableItems: new uint[] { 7741U },
                activeDungeonId: 22U,
                carrierHeldItems: new uint[] { 7740U, 7741U }));

            tank.HeldDungeonItems.Clear();
            tank.FreeBagSlots = 8;
            var partialBoundCoordinator = new SquadCoordinatorService();
            var partialBoundResult = partialBoundCoordinator.EvaluateTick(
                settings, tank,
                new[] { MakeWarlockSnap(false, 4000L, new List<uint> { 7740U, 7741U }, new List<uint> { 7741U }) },
                profile, 0UL, 0UL, 0L, 0f, 4000L, dungeon: dungeon);
            Assert.Equal(string.Empty, partialBoundResult.ActiveProvisionerName);

            // Unstarted transfer assignment cancels if receiver loses needed bag space before trade opens.
            var dropSpaceCoordinator = new SquadCoordinatorService();
            var assignedStart = dropSpaceCoordinator.EvaluateTick(
                settings, tank,
                new[] { MakeWarlockSnap(false, 5000L, new List<uint> { 7740U, 7741U }, new List<uint> { 7740U, 7741U }) },
                profile, 0UL, 0UL, 0L, 0f, 5000L, dungeon: dungeon);
            Assert.Equal("LockB", assignedStart.ActiveProvisionerName);
            tank.FreeBagSlots = 1;
            var droppedSpaceResult = dropSpaceCoordinator.EvaluateTick(
                settings, tank,
                new[] { MakeWarlockSnap(false, 5500L, new List<uint> { 7740U, 7741U }, new List<uint> { 7740U, 7741U }) },
                profile, 0UL, 0UL, 0L, 0f, 5500L, dungeon: dungeon);
            Assert.Equal(string.Empty, droppedSpaceResult.ActiveProvisionerName);

            // Per-member loot hard-cap bypass exempts timed-out looter without bypassing a different active looter.
            tank.FreeBagSlots = 8;
            var hardCapCoordinator = new SquadCoordinatorService();
            tank.IsInCombat = true;
            hardCapCoordinator.EvaluateTick(
                settings, tank,
                new[] { MakeWarlockSnap(false, 6000L, new List<uint> { 7741U }, new List<uint> { 7741U }) },
                profile, 0UL, 0UL, 0L, 0f, 6000L, dungeon: dungeon);
            tank.IsInCombat = false;
            tank.HeldDungeonItems = new List<uint> { 7740U };
            tank.IsLooting = true;
            hardCapCoordinator.EvaluateTick(
                settings, tank,
                new[] { MakeWarlockSnap(false, 6500L, new List<uint> { 7741U }, new List<uint> { 7741U }) },
                profile, 0UL, 0UL, 0L, 0f, 6500L, dungeon: dungeon);
            hardCapCoordinator.EvaluateTick(
                settings, tank,
                new[] { MakeWarlockSnap(false, 25000L, new List<uint> { 7741U }, new List<uint> { 7741U }) },
                profile, 0UL, 0UL, 0L, 0f, 25000L, dungeon: dungeon);
            // Tank timed out at 25000ms -> LockB is allowed to assign when only Tank has stale flag.
            var afterCapStaleTank = hardCapCoordinator.EvaluateTick(
                settings, tank,
                new[] { MakeWarlockSnap(false, 26000L, new List<uint> { 7741U }, new List<uint> { 7741U }) },
                profile, 0UL, 0UL, 0L, 0f, 26000L, dungeon: dungeon);
            Assert.Equal("LockB", afterCapStaleTank.ActiveProvisionerName);
            // Once a different member (LockB) starts looting, provisioning is blocked even while Tank stays stale.
            var newLooterBlocks = hardCapCoordinator.EvaluateTick(
                settings, tank,
                new[] { MakeWarlockSnap(true, 27000L, new List<uint> { 7741U }, new List<uint> { 7741U }) },
                profile, 0UL, 0UL, 0L, 0f, 27000L, dungeon: dungeon);
            Assert.Equal(string.Empty, newLooterBlocks.ActiveProvisionerName);
            tank.IsLooting = false;
            var clearedTankStillBlockedByLockB = hardCapCoordinator.EvaluateTick(
                settings, tank,
                new[] { MakeWarlockSnap(true, 27500L, new List<uint> { 7741U }, new List<uint> { 7741U }) },
                profile, 0UL, 0UL, 0L, 0f, 27500L, dungeon: dungeon);
            Assert.Equal(string.Empty, clearedTankStillBlockedByLockB.ActiveProvisionerName);

            // Disconnecting and reconnecting within the cap window retires any prior exemption.
            tank.IsInCombat = true;
            var multiHardCapCoordinator = new SquadCoordinatorService();
            multiHardCapCoordinator.EvaluateTick(
                settings, tank,
                new[] { MakeWarlockSnap(false, 29000L, new List<uint> { 7741U }, new List<uint> { 7741U }) },
                profile, 0UL, 0UL, 0L, 0f, 29000L, dungeon: dungeon);
            tank.IsInCombat = false;
            multiHardCapCoordinator.EvaluateTick(
                settings, tank,
                new[] { MakeWarlockSnap(true, 29500L, new List<uint> { 7741U }, new List<uint> { 7741U }) },
                profile, 0UL, 0UL, 0L, 0f, 29500L, dungeon: dungeon);
            var cappedLockB = multiHardCapCoordinator.EvaluateTick(
                settings, tank,
                new[] { MakeWarlockSnap(true, 45000L, new List<uint> { 7741U }, new List<uint> { 7741U }) },
                profile, 0UL, 0UL, 0L, 0f, 45000L, dungeon: dungeon);
            Assert.Equal("LockB", cappedLockB.ActiveProvisionerName);
            multiHardCapCoordinator.EvaluateTick(
                settings, tank,
                Array.Empty<SquadConnectedMemberSnapshot>(),
                profile, 0UL, 0UL, 0L, 0f, 46000L, dungeon: dungeon);
            var reconnectedLooting = multiHardCapCoordinator.EvaluateTick(
                settings, tank,
                new[] { MakeWarlockSnap(true, 47000L, new List<uint> { 7741U }, new List<uint> { 7741U }) },
                profile, 0UL, 0UL, 0L, 0f, 47000L, dungeon: dungeon);
            Assert.Equal(string.Empty, reconnectedLooting.ActiveProvisionerName);
        }
    }
}
