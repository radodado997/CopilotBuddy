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
        private bool _mailRequested;
        private int _mailAttempts, _vendorAttempts, _trainerAttempts;
        private long _stageStarted;
        private long _blockedAtUtcMs;
        private const long RecoveryDelayMs = 30000L;
        public string BlockedReason { get; private set; } = string.Empty;

        private bool Block(string reason, out bool complete)
        {
            BlockedReason = reason;
            _blockedAtUtcMs = Environment.TickCount64;
            Logging.Write("[PartyGrind Town] Blocked: {0}", reason);
            complete = false;
            return true;
        }

        public void ObservePhase(SquadPhase? phase)
        {
            if (!_wasInTownRun)
                return;

            if (phase != SquadPhase.TownRun && phase != SquadPhase.Paused)
            {
                Reset();
                return;
            }

            // Still inside the town run but no longer paused: the coordinator has dropped the block,
            // so resume only the failed stage with a fresh retry budget while keeping completed work.
            if (BlockedReason.Length > 0 && phase == SquadPhase.TownRun)
            {
                BlockedReason = string.Empty;
                _blockedAtUtcMs = 0;
                ResetRetryBudgets();
            }
        }

        private void ResetRetryBudgets()
        {
            _vendorAttempts = 0;
            _mailAttempts = 0;
            _trainerAttempts = 0;
        }

        public void RecheckBlocked(SquadSettings settings, SquadLeaderState? leader)
        {
            var me = StyxWoW.Me;
            if (me == null || BlockedReason.Length == 0) return;
            var needs = EvaluateLiveLocalNeeds(settings, leader);
            // Exhausted retry budgets and the town deadline are recoverable only after a bounded
            // delay (or once the underlying need disappears); completed stages are always retained.
            bool recoveryDelayElapsed = _blockedAtUtcMs == 0 ||
                (Environment.TickCount64 - _blockedAtUtcMs) >= RecoveryDelayMs;
            bool resolved = BlockedReason switch
            {
                "No eligible vendor found." => ResolveSellOrRepairVendor(needs) != null ||
                    (!needs.NeedsSell && !needs.NeedsRepair && !needs.NeedsBuy),
                "No mailbox found." => ProfileManager.CurrentProfile?.MailboxManager?.GetClosestMailbox() != null ||
                    string.IsNullOrWhiteSpace(CharacterSettings.Instance.MailRecipient) ||
                    !Styx.Logic.Inventory.InventoryManager.GetItemsToMail().Any(),
                "Class trainer not found; training remains pending." => !needs.NeedsTrain ||
                    ResolveClassTrainerLocation(me, out _, out _).HasValue,
                "Rally is on another map; cross-map town travel is unavailable." => leader != null &&
                    (!leader.TownRallyMapId.HasValue || leader.TownRallyMapId == me.MapId),
                "Vendor tasks remain unmet after five attempts." =>
                    (!needs.NeedsSell && !needs.NeedsRepair && !needs.NeedsBuy) || recoveryDelayElapsed,
                "Mailbox failed to open or finish mailing after three attempts." =>
                    string.IsNullOrWhiteSpace(CharacterSettings.Instance.MailRecipient) ||
                    !Styx.Logic.Inventory.InventoryManager.GetItemsToMail().Any() || recoveryDelayElapsed,
                "Class trainer has skills that could not be trained." =>
                    !needs.NeedsTrain || recoveryDelayElapsed,
                "Town task timed out before completion." => recoveryDelayElapsed,
                _ => false
            };
            if (resolved)
            {
                BlockedReason = string.Empty;
                _blockedAtUtcMs = 0;
                _stageStarted = Environment.TickCount64;
                ResetRetryBudgets();
            }
        }

        public void Reset()
        {
            _stage1SellRepairBuyDone = false;
            _stage1MailDone = false;
            _stage2TrainDone = false;
            _wasInTownRun = false;
            _mailRequested = false;
            _mailAttempts = _vendorAttempts = 0;
            _trainerAttempts = 0;
            _blockedAtUtcMs = 0;
            _stageStarted = Environment.TickCount64;
            BlockedReason = string.Empty;
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
            bool mageCanProvideFood,
            bool mageCanProvideDrink,
            SquadSettings settings)
        {
            bool needsSell = freeNormalBagSlots <= settings.TriggerTownFreeBagSlots;
            bool needsRepair = double.IsFinite(lowestDurabilityPct) && lowestDurabilityPct >= 0 && lowestDurabilityPct <= settings.TriggerTownDurabilityPct;
            bool foodProvided = preferMageFoodOverVendor && mageCanProvideFood;
            bool drinkProvided = preferMageFoodOverVendor && mageCanProvideDrink;

            bool needsBuy = (desiredFoodAmount > 0 && !hasFood && !foodProvided) ||
                 (usesMana && desiredDrinkAmount > 0 && !hasDrink && !drinkProvided);

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
            // A mage can only suppress vendor needs when it is actually able to trade with us, which
            // includes being on the same map. Food and drink stay independent so a mage that only
            // provides one of them does not suppress the other.
            var mages = (leader?.Members ?? new List<SquadMemberSummary>())
                .Where(m => m.IsConnected && m.HasTelemetry && !m.IsDead && !m.IsGhost &&
                            m.Class == WoWClass.Mage && m.MapId == me.MapId)
                .ToList();
            bool foodProvided = settings.PreferMageFoodOverVendor && mages.Any(m => m.CanProvideMageFood);
            bool drinkProvided = settings.PreferMageFoodOverVendor && mages.Any(m => m.CanProvideMageDrink);

            bool hasFood = SquadProvisioningService.CountFoodInBags() > 0;
            bool hasDrink = SquadProvisioningService.CountDrinksInBags() > 0;
            bool needsTrain = Vendors.ForceTrainer || Vendors.NeedClassTraining;
            bool hasMail = !string.IsNullOrWhiteSpace(CharacterSettings.Instance.MailRecipient);

            return EvaluateLocalTownNeeds(
                (int)me.FreeNormalBagSlots,
                me.Inventory.Equipped.PhysicalItems.Where(i => i.MaxDurability > 0)
                    .Select(i => 100.0 * i.Durability / i.MaxDurability).DefaultIfEmpty(double.NaN).Min(),
                hasFood,
                hasDrink,
                usesMana,
                CharacterSettings.Instance.FoodAmount,
                CharacterSettings.Instance.DrinkAmount,
                needsTrain,
                hasMail,
                settings.PreferMageFoodOverVendor,
                foodProvided,
                drinkProvided,
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

            if (BlockedReason.Length > 0) { townTasksComplete = false; return true; }
            if (leader.TownRallyMapId.HasValue && leader.TownRallyMapId != me.MapId)
                return Block("Rally is on another map; cross-map town travel is unavailable.", out townTasksComplete);
            var needs = EvaluateLiveLocalNeeds(settings, leader);
            if (!_stage1MailDone) _mailRequested |= needs.NeedsMail;
            if (!(_stage1SellRepairBuyDone && _stage1MailDone && _stage2TrainDone) && Environment.TickCount64 - _stageStarted > 120000)
                return Block("Town task timed out before completion.", out townTasksComplete);

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
                    if (vendor == null) return Block("No eligible vendor found.", out townTasksComplete);
                    WoWPoint targetLoc = vendor.Location;
                    if (me.Location.Distance(targetLoc) > 5.0f)
                    {
                        Navigator.MoveTo(targetLoc, "TownRun: moving to vendor");
                        townTasksComplete = false;
                        return true;
                    }

                    if (me.IsMoving) WoWMovement.MoveStop();
                    if (PerformVendorInteraction(vendor, needs))
                    {
                        var remaining = EvaluateLiveLocalNeeds(settings, leader);
                        _stage1SellRepairBuyDone = !remaining.NeedsBuy && !remaining.NeedsRepair &&
                            (!remaining.NeedsSell || (_mailRequested && !_stage1MailDone));
                    }
                    if (++_vendorAttempts >= 5 && !_stage1SellRepairBuyDone)
                    {
                        var remaining = EvaluateLiveLocalNeeds(settings, leader);
                        if (_mailRequested && !_stage1MailDone && !remaining.NeedsBuy && !remaining.NeedsRepair)
                            _stage1SellRepairBuyDone = true;
                        else
                            return Block("Vendor tasks remain unmet after five attempts.", out townTasksComplete);
                    }
                    townTasksComplete = false;
                    return true;
                }
            }

            // Stage 1B: Mailbox (optional if MailRecipient configured & needed).
            if (!_stage1MailDone)
            {
                if (!_mailRequested || string.IsNullOrWhiteSpace(CharacterSettings.Instance.MailRecipient) ||
                    !Styx.Logic.Inventory.InventoryManager.GetItemsToMail().Any())
                {
                    _stage1MailDone = true;
                }
                else
                {
                    var mailbox = ProfileManager.CurrentProfile?.MailboxManager?.GetClosestMailbox();
                    if (mailbox == null)
                    {
                        return Block("No mailbox found.", out townTasksComplete);
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
                                _stage1MailDone = !Styx.Logic.Inventory.InventoryManager.GetItemsToMail().Any();
                            }
                        }
                        if (!_stage1MailDone)
                        {
                            if (++_mailAttempts >= 3) return Block("Mailbox failed to open or finish mailing after three attempts.", out townTasksComplete);
                            townTasksComplete = false;
                            return true;
                        }
                    }
                }
            }

            if (_stage1MailDone && _mailRequested)
            {
                _mailRequested = false;
                if (EvaluateLiveLocalNeeds(settings, leader).NeedsSell)
                {
                    _stage1SellRepairBuyDone = false;
                    if (_vendorAttempts >= 5)
                        return Block("Vendor tasks remain unmet after five attempts.", out townTasksComplete);
                    townTasksComplete = false;
                    return true;
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
                        return Block("Class trainer not found; training remains pending.", out townTasksComplete);
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
                        _stage2TrainDone = PerformTrainerInteraction(me, trainerEntry, out bool purchaseAttempted);
                        if (!_stage2TrainDone)
                        {
                            // Only a real purchase attempt counts towards the retry budget; a closed
                            // trainer frame is just retried.
                            if (purchaseAttempted)
                            {
                                _trainerAttempts++;
                                if (_trainerAttempts >= 3)
                                {
                                    return Block("Class trainer has skills that could not be trained.", out townTasksComplete);
                                }
                            }
                            townTasksComplete = false;
                            return true;
                        }
                        _trainerAttempts = 0;
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

            // Never report completion while a need came back: reopen the affected vendor stage.
            var finalNeeds = EvaluateLiveLocalNeeds(settings, leader);
            if (finalNeeds.NeedsSell || finalNeeds.NeedsRepair || finalNeeds.NeedsBuy)
            {
                _stage1SellRepairBuyDone = false;
                if (finalNeeds.NeedsMail) _mailRequested = true;
                townTasksComplete = false;
                return true;
            }
            if (settings.TrainSkillsInGroupTownRun && finalNeeds.NeedsTrain)
            {
                _stage2TrainDone = false;
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
            if (me == null) return false;

            var nearbyVendors = ObjectManager.GetObjectsOfType<WoWUnit>(false, false)
                .Where(u => u != null && u.IsValid && !u.Dead && u.IsAnyVendor &&
                            u.Location.Distance(me.Location) <= 10f)
                .OrderBy(u => u.Location.Distance(me.Location))
                .ToList();

            // The configured profile vendor always wins over nearer unrelated merchants.
            WoWUnit? npc = null;
            if (vendor != null)
            {
                npc = nearbyVendors.FirstOrDefault(u => u.Entry == (uint)vendor.Entry);
            }

            bool usingFallbackMerchant = false;
            if (npc == null)
            {
                // A fallback merchant is only acceptable when it can finish the remaining task;
                // otherwise failing here must not be attributed to the configured vendor.
                npc = nearbyVendors.FirstOrDefault(u => CanCompleteVendorTask(u, needs));
                usingFallbackMerchant = npc != null;
            }

            if (npc == null)
                return false;

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

            if (!MerchantFrame.Instance.IsVisible) return false;

            // Confirm the merchant that actually opened is the one we selected before buying.
            WoWUnit? openMerchant = MerchantFrame.Instance.Merchant;
            if (openMerchant != null && openMerchant.Guid != npc.Guid)
                return false;

            bool matchesConfiguredVendor = vendor != null && npc.Entry == (uint)vendor.Entry;
            if (needs.NeedsSell)
                Vendors.SellAllItems();
            if (needs.NeedsRepair)
                Vendors.RepairAllItems();
            if (needs.NeedsBuy)
            {
                if (!usingFallbackMerchant && matchesConfiguredVendor &&
                    (vendor!.Type == Vendor.VendorType.Food || vendor.Type == Vendor.VendorType.Restock))
                {
                    BotPoi.Current = new BotPoi(vendor, PoiType.Buy);
                    Vendors.BuyItems();
                }
                else
                {
                    // Fallback merchants are restocked directly so a failure is never attributed to
                    // the configured vendor entry.
                    BuyFoodAndDrinkDirectly(me);
                }
            }
            if (MerchantFrame.Instance.IsVisible)
                MerchantFrame.Instance.Close();

            return true;
        }

        /// <summary>
        /// True when the given vendor is able to finish the outstanding town task (restock, sell or
        /// repair) on its own.
        /// </summary>
        private static bool CanCompleteVendorTask(WoWUnit merchant, TownNeedsEvaluation needs)
        {
            if (merchant == null || !merchant.IsAnyVendor)
                return false;

            if (needs.NeedsBuy && !merchant.IsFoodVendor && !merchant.IsAmmoVendor)
                return false;

            return needs.NeedsSell || needs.NeedsRepair || needs.NeedsBuy;
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

        private static bool PerformTrainerInteraction(LocalPlayer me, int trainerEntry, out bool purchaseAttempted)
        {
            purchaseAttempted = false;
            WoWUnit? trainer = ObjectManager.GetObjectsOfType<WoWUnit>(false, false)
                .Where(u => u != null && u.IsValid && !u.Dead && u.IsAnyTrainer &&
                            (u.Entry == (uint)trainerEntry || (u.Location.Distance(me.Location) <= 10f &&
                             NpcQueries.GetNpcById(u.Entry)?.TrainerClass == (int)me.Class)))
                .OrderBy(u => u.Entry == (uint)trainerEntry ? 0 : 1)
                .ThenBy(u => u.Location.Distance(me.Location))
                .FirstOrDefault();

            if (trainer == null)
            {
                return false;
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
                purchaseAttempted = true;
                int servicesBefore = TrainerFrame.Instance.NumTrainerServices;
                bool hadPendingTraining = Vendors.NeedClassTraining || Vendors.ForceTrainer;

                Vendors.TrainSkills();
                StyxWoW.Sleep(400);

                int servicesAfter = TrainerFrame.Instance.NumTrainerServices;
                TrainerFrame.Instance.Close();

                bool trainedSomething = servicesAfter < servicesBefore;
                if (hadPendingTraining && !trainedSomething && servicesAfter > 0)
                {
                    // The purchase produced no progress (no gold, level gate, ...). Keep the pending
                    // state instead of reporting success so the stage retries.
                    Vendors.NeedClassTraining = true;
                    Vendors.ForceTrainer = true;
                    return false;
                }

                Vendors.NeedClassTraining = false;
                Vendors.ForceTrainer = false;
                return true;
            }
            return false;
        }
    }
}
