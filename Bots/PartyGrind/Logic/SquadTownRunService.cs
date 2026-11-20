using System;
using System.Collections.Generic;
using System.Linq;
using Bots.Grind;
using Styx;
using Styx.Combat.CombatRoutine;
using Styx.Database;
using Styx.Helpers;
using Styx.Logic;
using Styx.Logic.Combat;
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
        public bool NeedsFood { get; init; }
        public bool NeedsDrink { get; init; }
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
        private bool _sellRepairVisitDone;
        private readonly Dictionary<int, (bool Food, bool Drink)> _vendorStock = new();
        private int _mailAttempts;
        private readonly SquadVendorAttempts _vendorAttempts = new();
        private long _stageStarted;
        private long _blockedAt;
        private long _regroupStarted;
        public string BlockedReason { get; private set; } = string.Empty;

        private bool Block(string reason, out bool complete)
        {
            BlockedReason = reason;
            _blockedAt = Environment.TickCount64;
            Logging.Write("[PartyGrind Town] Blocked: {0}", reason);
            complete = false;
            return true;
        }

        public void ObservePhase(SquadPhase? phase, bool townRunInterrupted = false)
        {
            // Combat/death recovery can temporarily take priority while the town run is
            // still active; that is an interruption, not the end of the run.
            if (_wasInTownRun && phase != SquadPhase.TownRun && phase != SquadPhase.Paused &&
                !townRunInterrupted)
                Reset();
        }

        public void RecheckBlocked(SquadSettings settings, SquadLeaderState? leader)
        {
            var me = StyxWoW.Me;
            if (me == null || BlockedReason.Length == 0) return;
            var needs = EvaluateLiveLocalNeeds(settings, leader);

            // Failure-based blocks are retried on a cooldown so a resolved configuration
            // problem (vendor/mailbox/trainer appearing later) unblocks the squad without
            // depending on RunTownTick, which is skipped while the phase is Paused.
            bool retryAllowed = Environment.TickCount64 - _blockedAt >= 15000;
            bool mailboxAvailable = ProfileManager.CurrentProfile?.MailboxManager?.GetClosestMailbox() != null ||
                string.IsNullOrWhiteSpace(CharacterSettings.Instance.MailRecipient) ||
                !Styx.Logic.Inventory.InventoryManager.GetItemsToMail().Any();
            bool vendorAvailable = _sellRepairVisitDone && !needs.NeedsSell && !needs.NeedsRepair && needs.NeedsBuy
                ? ResolveBuyVendor(needs) != null
                : ResolveSellOrRepairVendor(GetVendorWorkNeeds(needs)) != null;
            bool trainerAvailable = !needs.NeedsTrain || ResolveClassTrainerLocation(me, out _, out _).HasValue;
            WoWPoint rally = leader != null && (leader.TownRallyX != 0f || leader.TownRallyY != 0f)
                ? new WoWPoint(leader.TownRallyX, leader.TownRallyY, leader.TownRallyZ)
                : ResolveTownRallyPoint().Location;
            bool atRally = (leader == null || !leader.TownRallyMapId.HasValue || leader.TownRallyMapId == me.MapId) &&
                me.Location.Distance2D(rally) <= 15.0f;

            bool resolved = BlockedReason switch
            {
                "No eligible vendor found." => vendorAvailable,
                "No mailbox found." => mailboxAvailable,
                "Class trainer not found; training remains pending." => trainerAvailable,
                "Rally is on another map; cross-map town travel is unavailable." =>
                    leader != null && (!leader.TownRallyMapId.HasValue || leader.TownRallyMapId == me.MapId),
                "Town task timed out before completion." =>
                    (_sellRepairVisitDone && !needs.Any) ||
                    (retryAllowed && vendorAvailable && mailboxAvailable && trainerAvailable),
                "Vendor tasks remain unmet after five attempts." =>
                    (_sellRepairVisitDone && !needs.NeedsSell && !needs.NeedsRepair && !needs.NeedsBuy) ||
                    (retryAllowed && vendorAvailable),
                "Mailbox failed to open or finish mailing after three attempts." => mailboxAvailable &&
                    (!Styx.Logic.Inventory.InventoryManager.GetItemsToMail().Any() || retryAllowed),
                "Unable to reach town rally point before timeout." => atRally ||
                    (retryAllowed && (leader == null || !leader.TownRallyMapId.HasValue || leader.TownRallyMapId == me.MapId)),
                _ => false
            };
            if (resolved)
            {
                BlockedReason = string.Empty;
                _blockedAt = 0;
                _mailAttempts = 0;
                _vendorAttempts.Reset();
                _regroupStarted = 0;
                _stageStarted = Environment.TickCount64;
            }
        }

        public void Reset()
        {
            _stage1SellRepairBuyDone = false;
            _stage1MailDone = false;
            _stage2TrainDone = false;
            _wasInTownRun = false;
            _mailRequested = false;
            _sellRepairVisitDone = false;
            _vendorStock.Clear();
            _mailAttempts = 0;
            _vendorAttempts.Reset();
            _stageStarted = Environment.TickCount64;
            _blockedAt = 0;
            _regroupStarted = 0;
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

            bool needsFood = desiredFoodAmount > 0 && !hasFood && !foodProvided;
            bool needsDrink = usesMana && desiredDrinkAmount > 0 && !hasDrink && !drinkProvided;
            bool needsBuy = needsFood || needsDrink;

            bool needsTrain = settings.TrainSkillsInGroupTownRun && needClassTraining;
            bool needsMail = hasMailRecipient && needsSell;

            return new TownNeedsEvaluation
            {
                NeedsSell = needsSell,
                NeedsRepair = needsRepair,
                NeedsBuy = needsBuy,
                NeedsFood = needsFood,
                NeedsDrink = needsDrink,
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
            var mages = (leader?.Members ?? new List<SquadMemberSummary>())
                .Where(m => m.IsConnected && !m.IsDead && !m.IsGhost && m.Class == WoWClass.Mage && m.MapId == me.MapId);
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

            RecheckBlocked(settings, leader);
            if (BlockedReason.Length > 0) { townTasksComplete = false; return true; }
            if (leader.TownRallyMapId.HasValue && leader.TownRallyMapId != me.MapId)
                return Block("Rally is on another map; cross-map town travel is unavailable.", out townTasksComplete);
            var needs = EvaluateLiveLocalNeeds(settings, leader);
            // When bags are full, mail first if requested, but never abandon an
            // in-flight vendor confirmation or clear its attempt count.
            if (!_stage1MailDone) _mailRequested |= needs.NeedsMail;
            bool mailBeforeVendor = _mailRequested && !_stage1MailDone && needs.NeedsSell;
            if (ShouldWaitForVendorBeforeMail(mailBeforeVendor, _vendorAttempts, Environment.TickCount64))
            {
                townTasksComplete = false;
                return true;
            }

            // A renewed requirement (e.g. NeedsBuy after bag space frees up) reopens
            // the vendor stage instead of publishing completion with unmet needs.
            if (_stage1SellRepairBuyDone && !mailBeforeVendor &&
                (needs.NeedsBuy || needs.NeedsRepair || needs.NeedsSell))
            {
                _stage1SellRepairBuyDone = false;
                _sellRepairVisitDone = false;
                _vendorAttempts.Reset();
                _stageStarted = Environment.TickCount64;
                _regroupStarted = 0;
            }
            if (!(_stage1SellRepairBuyDone && _stage1MailDone && _stage2TrainDone) && Environment.TickCount64 - _stageStarted > 120000)
                return Block("Town task timed out before completion.", out townTasksComplete);

            // Stage 1A: Travel to rally vendor & Sell/Repair/Buy.
            if (!_stage1SellRepairBuyDone && !mailBeforeVendor)
            {
                int freeSlots = (int)me.FreeNormalBagSlots;
                var vendorNeeds = GetVendorWorkNeeds(needs);
                var decision = _vendorAttempts.Evaluate(Environment.TickCount64, needs, freeSlots);
                if (decision == SquadVendorAttemptDecision.Wait)
                {
                    townTasksComplete = false;
                    return true;
                }

                if (IsVendorWorkComplete(_sellRepairVisitDone, needs))
                {
                    _stage1SellRepairBuyDone = true;
                }
                else if (decision == SquadVendorAttemptDecision.Exhausted)
                {
                    var profile = ProfileManager.CurrentProfile;
                    Logging.WriteDebug("[PartyGrind Sync {0}] Vendor tasks still pending: slots={1}, required > {2}, sell={3}, repair={4}, food={5}, drink={6}; profile sell grey/white/green/blue/purple={7}/{8}/{9}/{10}/{11}. Check merchant access, protected items and vendor stock.",
                        me.Name, freeSlots, settings.TriggerTownFreeBagSlots, needs.NeedsSell, needs.NeedsRepair,
                        needs.NeedsFood, needs.NeedsDrink, profile?.SellGrey, profile?.SellWhite,
                        profile?.SellGreen, profile?.SellBlue, profile?.SellPurple);
                    return Block("Vendor tasks remain unmet after five attempts.", out townTasksComplete);
                }
                else
                {
                    bool sellAndRepair = !_sellRepairVisitDone || needs.NeedsSell || needs.NeedsRepair;
                    Vendor? vendor = sellAndRepair
                        ? ResolveSellOrRepairVendor(vendorNeeds)
                        : ResolveBuyVendor(needs);
                    if (vendor == null) return Block("No eligible vendor found.", out townTasksComplete);
                    WoWPoint targetLoc = vendor.Location;
                    if (me.Location.Distance(targetLoc) > 5.0f)
                    {
                        Navigator.MoveTo(targetLoc, sellAndRepair
                            ? "TownRun: moving to vendor"
                            : "TownRun: moving to consumable vendor");
                        townTasksComplete = false;
                        return true;
                    }

                    if (me.IsMoving) WoWMovement.MoveStop();
                    bool interacted = PerformVendorInteraction(vendor, vendorNeeds, sellAndRepair, out string outcome);
                    _vendorAttempts.Record(Environment.TickCount64, needs, freeSlots, interacted);
                    if (interacted)
                    {
                        if (sellAndRepair)
                        {
                            _sellRepairVisitDone = true;
                            if (!needs.NeedsSell && !needs.NeedsRepair)
                                _vendorAttempts.ResetAttemptBudget();
                        }
                    }
                    Logging.WriteDebug("[PartyGrind Sync {0}] Vendor {1} ({2}): {3}; attempt={4}, slots={5}, sell={6}, repair={7}, food={8}, drink={9}.",
                        me.Name, vendor.Name, vendor.Entry, outcome, _vendorAttempts.Count, freeSlots,
                        needs.NeedsSell, needs.NeedsRepair, needs.NeedsFood, needs.NeedsDrink);
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
                var remaining = EvaluateLiveLocalNeeds(settings, leader);
                if (remaining.NeedsSell || remaining.NeedsBuy || remaining.NeedsRepair)
                {
                    // Mail freed space or left other needs pending: retry the vendor stage
                    // with a fresh bounded attempt window.
                    _stage1SellRepairBuyDone = false;
                    _sellRepairVisitDone = false;
                    _vendorAttempts.Reset();
                    _stageStarted = Environment.TickCount64;
                    _regroupStarted = 0;
                    townTasksComplete = false;
                    return true;
                }
            }

            // Stage 2: Diverge individually to own Class Trainer if needed. A live
            // training request reopens this stage even if it had already completed.
            if (settings.TrainSkillsInGroupTownRun && needs.NeedsTrain && _stage2TrainDone)
            {
                _stage2TrainDone = false;
                _stageStarted = Environment.TickCount64;
                _regroupStarted = 0;
            }
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
                        _stage2TrainDone = PerformTrainerInteraction(me, trainerEntry);
                        if (!_stage2TrainDone) { townTasksComplete = false; return true; }
                    }
                }
            }

            // Stage 3: Return to TownRallyPoint (<= 15 yd) before marking TownTasksComplete = true.
            // Once every task flag is set the regroup itself is time-bounded, so a member
            // that cannot reach the rally transitions to a bounded blocked state instead of
            // reporting an incomplete task forever.
            if (_stage1SellRepairBuyDone && _stage1MailDone && _stage2TrainDone && _regroupStarted == 0)
                _regroupStarted = Environment.TickCount64;

            if (me.Location.Distance2D(rallyPoint) > 15.0f)
            {
                if (_regroupStarted > 0 && Environment.TickCount64 - _regroupStarted > 120000)
                    return Block("Unable to reach town rally point before timeout.", out townTasksComplete);
                Navigator.MoveTo(rallyPoint, "TownRun: returning to squad rally point");
                townTasksComplete = false;
                return true;
            }

            var remainingNeeds = EvaluateLiveLocalNeeds(settings, leader);
            townTasksComplete = _stage1SellRepairBuyDone && !remainingNeeds.NeedsSell && !remainingNeeds.NeedsRepair &&
                !remainingNeeds.NeedsBuy && !remainingNeeds.NeedsTrain && !remainingNeeds.NeedsMail;
            return true;
        }

        internal static bool ShouldWaitForVendorBeforeMail(bool mailBeforeVendor, SquadVendorAttempts attempts, long now)
            => mailBeforeVendor && attempts.IsConfirmationPending(now);

        internal static bool IsVendorWorkComplete(bool sellRepairVisitDone, TownNeedsEvaluation needs)
            => sellRepairVisitDone && !needs.NeedsSell && !needs.NeedsRepair && !needs.NeedsBuy;

        internal static TownNeedsEvaluation GetVendorWorkNeeds(TownNeedsEvaluation needs)
        {
            return new TownNeedsEvaluation
            {
                NeedsSell = true,
                NeedsRepair = true,
                NeedsBuy = needs.NeedsBuy,
                NeedsFood = needs.NeedsFood,
                NeedsDrink = needs.NeedsDrink
            };
        }

        private static Vendor? ResolveSellOrRepairVendor(TownNeedsEvaluation needs)
        {
            var vm = ProfileManager.CurrentProfile?.VendorManager;
            if (vm == null) return null;
            if (needs.NeedsRepair)
                return vm.GetClosestVendor(Vendor.VendorType.Repair);
            if (needs.NeedsBuy)
                return vm.GetClosestVendor(Vendor.VendorType.Food) ?? vm.GetClosestVendor(Vendor.VendorType.Restock) ?? vm.GetClosestVendor(Vendor.VendorType.Sell);
            return vm.GetClosestVendor(Vendor.VendorType.Sell) ?? vm.GetClosestVendor(Vendor.VendorType.Repair);
        }

        internal static bool CanSupplyNeededConsumables(TownNeedsEvaluation needs, bool hasFood, bool hasDrink)
            => (!needs.NeedsFood && !needs.NeedsDrink) ||
                (needs.NeedsFood && hasFood) || (needs.NeedsDrink && hasDrink);

        private Vendor? ResolveBuyVendor(TownNeedsEvaluation needs)
        {
            var vm = ProfileManager.CurrentProfile?.VendorManager;
            if (vm == null) return null;
            var excluded = _vendorStock
                .Where(v => !CanSupplyNeededConsumables(needs, v.Value.Food, v.Value.Drink))
                .Select(v => v.Key).ToHashSet();
            return vm.GetClosestVendor(Vendor.VendorType.Food, excluded)
                ?? vm.GetClosestVendor(Vendor.VendorType.Restock, excluded)
                ?? vm.GetClosestVendor(Vendor.VendorType.Sell, excluded)
                ?? vm.GetClosestVendor(Vendor.VendorType.Repair, excluded);
        }

        internal static bool IsVendorCatalogueComplete(int loadedCount, int observedCount, bool allItemInfoLoaded)
            => loadedCount >= 0 && loadedCount == observedCount && allItemInfoLoaded;

        private static void ObserveMerchantCatalogue()
        {
            Lua.DoString(@"
                if not CopilotBuddyTownMerchantCatalogue then
                    local f = CreateFrame('Frame')
                    CopilotBuddyTownMerchantCatalogue = f
                    f:RegisterEvent('MERCHANT_SHOW')
                    f:RegisterEvent('MERCHANT_UPDATE')
                    f:RegisterEvent('MERCHANT_CLOSED')
                    f:SetScript('OnEvent', function(self, event)
                        if event == 'MERCHANT_CLOSED' then
                            self.guid = nil
                            self.count = nil
                        else
                            self.guid = UnitGUID('npc')
                            self.count = GetMerchantNumItems()
                        end
                    end)
                end");
        }

        private static int GetLoadedMerchantCatalogueCount()
            => Lua.GetReturnVal<int>(@"
                local f = CopilotBuddyTownMerchantCatalogue
                if f and f.guid and f.guid == UnitGUID('npc') and
                    MerchantFrame and MerchantFrame:IsVisible() and
                    f.count == GetMerchantNumItems() then
                    return f.count
                end
                return -1", 0);

        private bool PerformVendorInteraction(
            Vendor? vendor,
            TownNeedsEvaluation vendorNeeds,
            bool sellAndRepair,
            out string outcome)
        {
            outcome = "player unavailable";
            var me = StyxWoW.Me;
            if (me == null) return false;

            // Prefer the configured vendor entry; an unrelated nearby merchant must not
            // be used to satisfy a configured vendor's Sell/Repair/Buy stage.
            WoWUnit? npc = ObjectManager.GetObjectsOfType<WoWUnit>(false, false)
                .Where(u => u != null && u.IsValid && !u.Dead && u.IsAnyVendor &&
                            (vendor == null || u.Entry == (uint)vendor.Entry))
                .OrderBy(u => u.Location.Distance(me.Location))
                .FirstOrDefault();

            if (npc == null)
            {
                outcome = "configured merchant not found";
                return false;
            }

            ObserveMerchantCatalogue();
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

            if (!MerchantFrame.Instance.IsVisible)
            {
                outcome = "merchant window did not open";
                return false;
            }
            var openMerchant = MerchantFrame.Instance.Merchant;
            if (openMerchant != null && openMerchant.IsValid && openMerchant.Entry != npc.Entry)
            {
                // The frame belongs to a different merchant — do not buy or blacklist
                // anything against the selected vendor.
                MerchantFrame.Instance.Close();
                outcome = "merchant window belongs to another NPC";
                return false;
            }
            if (MerchantFrame.Instance.IsVisible)
            {
                if (sellAndRepair)
                {
                    Vendors.SellAllItems();
                    Vendors.RepairAllItems();
                }
                if (!sellAndRepair && vendorNeeds.NeedsBuy)
                {
                    Vendors.WaitForMerchantItemCache();
                    bool hasFood = MerchantFrame.Instance.GetBestFoodFromVendor() != -1;
                    bool hasDrink = MerchantFrame.Instance.GetBestDrinkFromVendor() != -1;
                    var catalogue = MerchantFrame.Instance.GetAllMerchantItems();
                    int loadedCount = GetLoadedMerchantCatalogueCount();
                    if (vendor != null && openMerchant != null && openMerchant.IsValid &&
                        openMerchant.Entry == npc.Entry && IsVendorCatalogueComplete(
                            loadedCount, catalogue.Length, catalogue.All(i => i.ItemInfo != null)))
                        _vendorStock[vendor.Entry] = (hasFood, hasDrink);
                    if (!CanSupplyNeededConsumables(vendorNeeds, hasFood, hasDrink))
                    {
                        MerchantFrame.Instance.Close();
                        outcome = "merchant has no usable consumable matching remaining needs";
                        return false;
                    }
                    if (vendor != null && (vendor.Type == Vendor.VendorType.Food || vendor.Type == Vendor.VendorType.Restock))
                    {
                        BotPoi.Current = new BotPoi(vendor, PoiType.Buy);
                        Vendors.BuyItems(vendorNeeds.NeedsFood, vendorNeeds.NeedsDrink);
                    }
                    else
                    {
                        BuyFoodAndDrinkDirectly(vendorNeeds.NeedsFood, vendorNeeds.NeedsDrink);
                    }
                }
                if (MerchantFrame.Instance.IsVisible)
                    MerchantFrame.Instance.Close();
            }

            outcome = "vendor actions submitted; awaiting update";
            return true;
        }

        private static void BuyFoodAndDrinkDirectly(bool needsFood, bool needsDrink)
        {
            int bestDrinkIndex = MerchantFrame.Instance.GetBestDrinkFromVendor();
            int bestFoodIndex = MerchantFrame.Instance.GetBestFoodFromVendor();

            if (needsDrink && bestDrinkIndex != -1 && CharacterSettings.Instance.DrinkAmount > 0)
                MerchantFrame.Instance.BuyItem(bestDrinkIndex, CharacterSettings.Instance.DrinkAmount);

            if (needsFood && bestFoodIndex != -1 && CharacterSettings.Instance.FoodAmount > 0)
                MerchantFrame.Instance.BuyItem(bestFoodIndex, CharacterSettings.Instance.FoodAmount);
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

        private static bool PerformTrainerInteraction(LocalPlayer me, int trainerEntry)
        {
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

            bool frameMatchesTrainer = TrainerFrame.Instance.IsVisible &&
                !TrainerFrame.Instance.IsTradeskillTrainer &&
                me.CurrentTargetGuid == trainer.Guid;
            if (!frameMatchesTrainer)
            {
                if (TrainerFrame.Instance.IsVisible)
                    TrainerFrame.Instance.Close();
                trainer.Target();
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

            frameMatchesTrainer = TrainerFrame.Instance.IsVisible &&
                !TrainerFrame.Instance.IsTradeskillTrainer &&
                me.CurrentTargetGuid == trainer.Guid;
            if (frameMatchesTrainer)
            {
                bool forceTrainer = Vendors.ForceTrainer;
                int spellsBefore = SpellManager.NumKnownSpells;
                // Normalize filters so BuyAll and the purchasability check see every row,
                // regardless of how the trainer window was left configured.
                TrainerFrame.Instance.SetServiceFilter(TrainerServiceFilter.Available, true);
                TrainerFrame.Instance.SetServiceFilter(TrainerServiceFilter.Unavailable, true);
                TrainerFrame.Instance.SetServiceFilter(TrainerServiceFilter.Used, true);
                TrainerFrame.Instance.BuyAll();

                // BuyTrainerService is answered by the server asynchronously. Wait briefly
                // for the spellbook/trainer list to settle before declaring training done.
                // Count only rows the client still reports as trainable; unrelated rows
                // (already used or unavailable) must not block completion.
                bool hasPurchasableService = TrainerFrame.Instance.HasPurchasableService();
                for (int attempt = 0; attempt < 3 && hasPurchasableService; attempt++)
                {
                    StyxWoW.Sleep(300);
                    SpellManager.Refresh();
                    hasPurchasableService = TrainerFrame.Instance.HasPurchasableService();
                }
                // The spell-count delta is progress evidence only: an available service
                // must still block completion even if another spell was learned.
                bool learnedSpell = SpellManager.NumKnownSpells > spellsBefore;
                bool trained = !hasPurchasableService;
                Logging.WriteDebug("[PartyGrind Town] Trainer visit: available={0}, learnedSpell={1}",
                    hasPurchasableService, learnedSpell);
                TrainerFrame.Instance.Close();

                if (!trained)
                {
                    // Keep the request pending so a later town run retries training.
                    if (forceTrainer) Vendors.ForceTrainer = true;
                    return false;
                }

                Vendors.NeedClassTraining = false;
                Vendors.ForceTrainer = false;
                return true;
            }
            return false;
        }
    }
    internal enum SquadVendorAttemptDecision { Wait, Try, Exhausted }

    /// <summary>Spaces attempts and allows server-side bag updates before declaring failure.</summary>
    internal sealed class SquadVendorAttempts
    {
        private const long ConfirmationDelayMs = 1500;
        private const int MaxAttempts = 5;
        private long _nextAttempt;
        private TownNeedsEvaluation _before;
        private int _slotsBefore;
        public int Count { get; private set; }
        public bool LastInteractionSucceeded { get; private set; }

        public void Reset()
        {
            Count = 0;
            _nextAttempt = 0;
            LastInteractionSucceeded = false;
        }

        internal void ResetAttemptBudget() => Count = 0;

        internal bool IsConfirmationPending(long now) => now < _nextAttempt;

        public SquadVendorAttemptDecision Evaluate(long now, TownNeedsEvaluation needs, int freeSlots)
        {
            if (IsConfirmationPending(now))
                return SquadVendorAttemptDecision.Wait;

            if (Count > 0 && LastInteractionSucceeded &&
                (freeSlots > _slotsBefore ||
                 (_before.NeedsSell && !needs.NeedsSell) ||
                 (_before.NeedsRepair && !needs.NeedsRepair) ||
                 (_before.NeedsFood && !needs.NeedsFood) ||
                 (_before.NeedsDrink && !needs.NeedsDrink)))
                Count = 0; // Partial progress earns a fresh set of attempts.

            return Count >= MaxAttempts ? SquadVendorAttemptDecision.Exhausted : SquadVendorAttemptDecision.Try;
        }

        public void Record(long now, TownNeedsEvaluation needs, int freeSlots, bool succeeded)
        {
            Count++;
            _before = needs;
            _slotsBefore = freeSlots;
            LastInteractionSucceeded = succeeded;
            _nextAttempt = now + ConfirmationDelayMs;
        }
    }

}
