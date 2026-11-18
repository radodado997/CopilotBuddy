using System;
using System.Collections.Generic;
using System.Linq;
using Bots.Grind;
using Styx;
using Styx.Combat.CombatRoutine;
using Styx.Database;
using Styx.Helpers;
using Styx.Logic;
using Styx.Logic.Inventory.Frames.Gossip;
using Styx.Logic.Inventory.Frames.MailBox;
using Styx.Logic.Inventory.Frames.Merchant;
using Styx.Logic.Inventory.Frames.Trainer;
using Styx.Logic.Pathing;
using Styx.Logic.POI;
using Styx.Logic.Profiles;
using Styx.PartyGrind;
using Styx.PartyGrind.IPC;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;

namespace CopilotBuddy.Bots.PartyGrind.Logic
{
    public readonly struct TownNeedsEvaluation
    {
        public bool NeedsSell { get; init; }
        public bool NeedsRepair { get; init; }
        public bool NeedsBuy { get; init; }
        public bool NeedsTrain { get; init; }
        public bool NeedsMail { get; init; }
        public bool Any => NeedsSell || NeedsRepair || NeedsBuy || NeedsTrain || NeedsMail;
    }

    /// <summary>
    /// Coordinates synchronized 3-stage TownRuns:
    /// 1) Group march to TownRallyPoint (closest Sell/Repair vendor) for Sell/Repair/Buy/Mail.
    /// 2) Individual class training at each member's class trainer.
    /// 3) Regroup at TownRallyPoint (&lt;= 20 yd) before setting TownTasksComplete = true.
    /// </summary>
    public sealed class SquadTownRunService
    {
        private bool _stage1SellRepairBuyDone;
        private bool _stage1MailDone;
        private bool _stage2TrainDone;
        private bool _wasInTownRun;

        public void Reset()
        {
            _stage1SellRepairBuyDone = false;
            _stage1MailDone = false;
            _stage2TrainDone = false;
            _wasInTownRun = false;
        }

        /// <summary>
        /// Pure testable evaluation of local TownRun triggers.
        /// </summary>
        public static TownNeedsEvaluation EvaluateLocalTownNeeds(
            int freeNormalBagSlots,
            double lowestDurabilityPct,
            bool hasFood,
            bool hasDrink,
            bool usesMana,
            int desiredFoodAmount,
            int desiredDrinkAmount,
            bool needClassTraining,
            bool hasMailRecipient,
            bool preferMageFoodOverVendor,
            bool squadHasActiveMageLvl4Plus,
            SquadSettings settings)
        {
            bool needsSell = freeNormalBagSlots <= settings.TriggerTownFreeBagSlots;
            bool needsRepair = lowestDurabilityPct > 0 && lowestDurabilityPct <= settings.TriggerTownDurabilityPct;
            bool suppressFoodDrinkBuy = preferMageFoodOverVendor && squadHasActiveMageLvl4Plus;

            bool needsBuy = !suppressFoodDrinkBuy &&
                ((desiredFoodAmount > 0 && !hasFood) ||
                 (usesMana && desiredDrinkAmount > 0 && !hasDrink));

            bool needsTrain = settings.TrainSkillsInGroupTownRun && needClassTraining;
            bool needsMail = hasMailRecipient && needsSell;

            return new TownNeedsEvaluation
            {
                NeedsSell = needsSell,
                NeedsRepair = needsRepair,
                NeedsBuy = needsBuy,
                NeedsTrain = needsTrain,
                NeedsMail = needsMail
            };
        }

        public static TownNeedsEvaluation EvaluateLiveLocalNeeds(SquadSettings settings, SquadLeaderState? leader)
        {
            var me = StyxWoW.Me;
            if (me == null)
                return default;

            bool usesMana = me.PowerType == WoWPowerType.Mana || me.Class == WoWClass.Druid;
            bool hasActiveMage = (leader?.Members ?? new List<SquadMemberSummary>())
                .Any(m => m.IsConnected && !m.IsDead && m.Class == WoWClass.Mage && m.Level >= 4);

            bool hasFood = SquadProvisioningService.CountFoodInBags() > 0;
            bool hasDrink = SquadProvisioningService.CountDrinksInBags() > 0;
            bool needsTrain = Vendors.ForceTrainer || Vendors.NeedClassTraining;
            bool hasMail = !string.IsNullOrWhiteSpace(CharacterSettings.Instance.MailRecipient);

            return EvaluateLocalTownNeeds(
                (int)me.FreeNormalBagSlots,
                me.LowestDurabilityPercent,
                hasFood,
                hasDrink,
                usesMana,
                CharacterSettings.Instance.FoodAmount,
                CharacterSettings.Instance.DrinkAmount,
                needsTrain,
                hasMail,
                settings.PreferMageFoodOverVendor,
                hasActiveMage,
                settings);
        }

        public static (uint MapId, WoWPoint Location) ResolveTownRallyPoint()
        {
            var profile = ProfileManager.CurrentProfile;
            Vendor? vendor = profile?.VendorManager?.GetClosestVendor(Vendor.VendorType.Repair)
                ?? profile?.VendorManager?.GetClosestVendor(Vendor.VendorType.Sell)
                ?? profile?.VendorManager?.GetClosestVendor();

            if (vendor != null)
                return (StyxWoW.Me?.MapId ?? 0, vendor.Location);

            return (StyxWoW.Me?.MapId ?? 0, StyxWoW.Me?.Location ?? WoWPoint.Zero);
        }

        /// <summary>
        /// Executes one tick of the synchronized 3-stage TownRun behavior.
        /// Returns true while still busy in TownRun, false once ready at TownRallyPoint.
        /// </summary>
        public bool RunTownTick(
            SquadSettings settings,
            SquadLeaderState? leader,
            out bool townTasksComplete)
        {
            var me = StyxWoW.Me;
            if (me == null)
            {
                townTasksComplete = false;
                return false;
            }

            if (leader == null || leader.Phase != SquadPhase.TownRun)
            {
                if (_wasInTownRun)
                    Reset();
                townTasksComplete = false;
                return false;
            }

            if (!_wasInTownRun)
            {
                Reset();
                _wasInTownRun = true;
            }

            WoWPoint rallyPoint = new WoWPoint(leader.TownRallyX, leader.TownRallyY, leader.TownRallyZ);
            if (rallyPoint == WoWPoint.Zero)
                rallyPoint = ResolveTownRallyPoint().Location;

            var needs = EvaluateLiveLocalNeeds(settings, leader);

            // Stage 1A: Travel to rally vendor & Sell/Repair/Buy.
            if (!_stage1SellRepairBuyDone)
            {
                if (!needs.NeedsSell && !needs.NeedsRepair && !needs.NeedsBuy)
                {
                    _stage1SellRepairBuyDone = true;
                }
                else
                {
                    Vendor? vendor = ResolveSellOrRepairVendor(needs);
                    WoWPoint targetLoc = vendor?.Location ?? rallyPoint;
                    if (me.Location.Distance(targetLoc) > 5.0f)
                    {
                        Navigator.MoveTo(targetLoc, "TownRun: moving to vendor");
                        townTasksComplete = false;
                        return true;
                    }

                    if (me.IsMoving) WoWMovement.MoveStop();
                    if (PerformVendorInteraction(vendor, needs))
                    {
                        _stage1SellRepairBuyDone = true;
                    }
                    townTasksComplete = false;
                    return true;
                }
            }

            // Stage 1B: Mailbox (optional if MailRecipient configured & needed).
            if (!_stage1MailDone)
            {
                if (!needs.NeedsMail || string.IsNullOrWhiteSpace(CharacterSettings.Instance.MailRecipient))
                {
                    _stage1MailDone = true;
                }
                else
                {
                    var mailbox = ProfileManager.CurrentProfile?.MailboxManager?.GetClosestMailbox();
                    if (mailbox == null)
                    {
                        _stage1MailDone = true;
                    }
                    else if (me.Location.Distance(mailbox.Location) > 5.0f)
                    {
                        Navigator.MoveTo(mailbox.Location, "TownRun: moving to mailbox");
                        townTasksComplete = false;
                        return true;
                    }
                    else
                    {
                        var mailObj = ObjectManager.GetObjectsOfType<WoWGameObject>(false, false)
                            .Where(g => g.SubType == WoWGameObjectType.Mailbox)
                            .OrderBy(g => g.Location.Distance(me.Location))
                            .FirstOrDefault();
                        if (mailObj != null)
                        {
                            if (!MailFrame.Instance.IsVisible)
                            {
                                mailObj.Interact();
                                StyxWoW.Sleep(600);
                            }
                            if (MailFrame.Instance.IsVisible)
                            {
                                Vendors.MailAllItems();
                                MailFrame.Instance.Close();
                            }
                        }
                        _stage1MailDone = true;
                    }
                }
            }

            // Stage 2: Diverge individually to own Class Trainer if needed.
            if (!_stage2TrainDone)
            {
                if (!settings.TrainSkillsInGroupTownRun || !needs.NeedsTrain)
                {
                    _stage2TrainDone = true;
                }
                else
                {
                    WoWPoint? trainerLoc = ResolveClassTrainerLocation(me, out int trainerEntry, out string trainerName);
                    if (!trainerLoc.HasValue)
                    {
                        Vendors.NeedClassTraining = false;
                        Vendors.ForceTrainer = false;
                        _stage2TrainDone = true;
                    }
                    else if (me.Location.Distance(trainerLoc.Value) > 4.5f)
                    {
                        Navigator.MoveTo(trainerLoc.Value, "TownRun: moving to " + trainerName);
                        townTasksComplete = false;
                        return true;
                    }
                    else
                    {
                        if (me.IsMoving) WoWMovement.MoveStop();
                        PerformTrainerInteraction(me, trainerEntry);
                        _stage2TrainDone = true;
                    }
                }
            }

            // Stage 3: Return to TownRallyPoint (<= 20 yd) before marking TownTasksComplete = true.
            if (me.Location.Distance2D(rallyPoint) > 15.0f)
            {
                Navigator.MoveTo(rallyPoint, "TownRun: returning to squad rally point");
                townTasksComplete = false;
                return true;
            }

            townTasksComplete = true;
            return true;
        }

        private static Vendor? ResolveSellOrRepairVendor(TownNeedsEvaluation needs)
        {
            var vm = ProfileManager.CurrentProfile?.VendorManager;
            if (vm == null) return null;
            if (needs.NeedsRepair)
                return vm.GetClosestVendor(Vendor.VendorType.Repair) ?? vm.GetClosestVendor(Vendor.VendorType.Sell);
            if (needs.NeedsBuy)
                return vm.GetClosestVendor(Vendor.VendorType.Food) ?? vm.GetClosestVendor(Vendor.VendorType.Restock) ?? vm.GetClosestVendor(Vendor.VendorType.Sell);
            return vm.GetClosestVendor(Vendor.VendorType.Sell) ?? vm.GetClosestVendor(Vendor.VendorType.Repair);
        }

        private static bool PerformVendorInteraction(Vendor? vendor, TownNeedsEvaluation needs)
        {
            var me = StyxWoW.Me;
            if (me == null) return true;

            WoWUnit? npc = ObjectManager.GetObjectsOfType<WoWUnit>(false, false)
                .Where(u => u != null && u.IsValid && !u.Dead && u.IsAnyVendor &&
                            (vendor == null || u.Entry == (uint)vendor.Entry || u.Location.Distance(me.Location) <= 10f))
                .OrderBy(u => u.Location.Distance(me.Location))
                .FirstOrDefault();

            if (npc == null)
                return true;

            if (!MerchantFrame.Instance.IsVisible)
            {
                npc.Interact();
                StyxWoW.Sleep(600);
                if (GossipFrame.Instance.IsVisible && GossipFrame.Instance.GossipOptionEntries != null)
                {
                    foreach (var entry in GossipFrame.Instance.GossipOptionEntries)
                    {
                        if (entry.Type == GossipEntry.GossipEntryType.Vendor)
                        {
                            GossipFrame.Instance.SelectGossipOption(entry.Index);
                            StyxWoW.Sleep(500);
                            break;
                        }
                    }
                }
            }

            if (MerchantFrame.Instance.IsVisible)
            {
                if (needs.NeedsSell)
                    Vendors.SellAllItems();
                if (needs.NeedsRepair)
                    Vendors.RepairAllItems();
                if (needs.NeedsBuy)
                {
                    if (vendor != null && (vendor.Type == Vendor.VendorType.Food || vendor.Type == Vendor.VendorType.Restock))
                    {
                        BotPoi.Current = new BotPoi(vendor, PoiType.Buy);
                        Vendors.BuyItems();
                    }
                    else
                    {
                        BuyFoodAndDrinkDirectly(me);
                    }
                }
                if (MerchantFrame.Instance.IsVisible)
                    MerchantFrame.Instance.Close();
            }

            return true;
        }

        private static void BuyFoodAndDrinkDirectly(LocalPlayer me)
        {
            bool usesMana = me.PowerType == WoWPowerType.Mana || me.Class == WoWClass.Druid;
            int bestDrinkIndex = MerchantFrame.Instance.GetBestDrinkFromVendor();
            int bestFoodIndex = MerchantFrame.Instance.GetBestFoodFromVendor();

            if (bestDrinkIndex != -1 && usesMana && CharacterSettings.Instance.DrinkAmount > 0 &&
                SquadProvisioningService.CountDrinksInBags() == 0)
            {
                MerchantFrame.Instance.BuyItem(bestDrinkIndex, CharacterSettings.Instance.DrinkAmount);
            }

            if (bestFoodIndex != -1 && CharacterSettings.Instance.FoodAmount > 0 &&
                SquadProvisioningService.CountFoodInBags() == 0)
            {
                MerchantFrame.Instance.BuyItem(bestFoodIndex, CharacterSettings.Instance.FoodAmount);
            }
        }

        private static WoWPoint? ResolveClassTrainerLocation(LocalPlayer me, out int entry, out string name)
        {
            entry = 0;
            name = "Class Trainer";

            var profileVendor = ProfileManager.CurrentProfile?.VendorManager?.GetClosestVendor(Vendor.VendorType.Train);
            if (profileVendor != null)
            {
                entry = profileVendor.Entry;
                name = string.IsNullOrWhiteSpace(profileVendor.Name) ? "Class Trainer" : profileVendor.Name;
                return profileVendor.Location;
            }

            NpcResult? dbTrainer = NpcQueries.GetNearestTrainer(me.FactionTemplate, me.MapId, me.Location, me.Class);
            if (dbTrainer != null)
            {
                entry = dbTrainer.Entry;
                name = string.IsNullOrWhiteSpace(dbTrainer.Name) ? "Class Trainer" : dbTrainer.Name;
                return dbTrainer.Location;
            }

            return null;
        }

        private static void PerformTrainerInteraction(LocalPlayer me, int trainerEntry)
        {
            WoWUnit? trainer = ObjectManager.GetObjectsOfType<WoWUnit>(false, false)
                .Where(u => u != null && u.IsValid && !u.Dead && u.IsAnyTrainer &&
                            (trainerEntry == 0 || u.Entry == (uint)trainerEntry || u.Location.Distance(me.Location) <= 10f))
                .OrderBy(u => u.Location.Distance(me.Location))
                .FirstOrDefault();

            if (trainer == null)
            {
                Vendors.NeedClassTraining = false;
                Vendors.ForceTrainer = false;
                return;
            }

            if (!TrainerFrame.Instance.IsVisible)
            {
                trainer.Interact();
                StyxWoW.Sleep(600);
                if (GossipFrame.Instance.IsVisible && GossipFrame.Instance.GossipOptionEntries != null)
                {
                    foreach (var entry in GossipFrame.Instance.GossipOptionEntries)
                    {
                        if (entry.Type == GossipEntry.GossipEntryType.Trainer)
                        {
                            GossipFrame.Instance.SelectGossipOption(entry.Index);
                            StyxWoW.Sleep(500);
                            break;
                        }
                    }
                }
            }

            if (TrainerFrame.Instance.IsVisible)
            {
                Vendors.TrainSkills();
                StyxWoW.Sleep(400);
                TrainerFrame.Instance.Close();
            }

            Vendors.NeedClassTraining = false;
            Vendors.ForceTrainer = false;
        }
    }
}
