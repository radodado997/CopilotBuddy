using System;
using System.Collections.Generic;
using System.Linq;
using Styx;
using Styx.Combat.CombatRoutine;
using Styx.Helpers;
using Styx.Logic.Combat;
using Styx.Logic.Inventory;
using Styx.Logic.Inventory.Frames.Trade;
using Styx.Logic.Pathing;
using Styx.PartyGrind;
using Styx.PartyGrind.IPC;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;

namespace CopilotBuddy.Bots.PartyGrind.Logic
{
    /// <summary>
    /// Pure snapshot of a consumable item candidate in bags for unit-testable trade selection.
    /// </summary>
    public sealed class TradableConsumableCandidate
    {
        public uint Entry { get; init; }
        public string Name { get; init; } = string.Empty;
        public int RequiredLevel { get; init; }
        public int StackCount { get; init; }
        public bool IsSoulbound { get; init; }
        public bool IsFood { get; init; }
        public bool IsDrink { get; init; }
        public bool IsHealthstone { get; init; }
    }

    public readonly struct ConsumableNeedEvaluation
    {
        public bool WantsFood { get; init; }
        public bool WantsDrink { get; init; }
        public bool WantsHealthstone { get; init; }
    }

    /// <summary>
    /// Handles Mage food/water/refreshment conjuring & trading, Ritual of Refreshment tables,
    /// Warlock Healthstones/Soulwell/Soulstone, and secure TradeFrame arbitration.
    /// </summary>
    public sealed class SquadProvisioningService
    {
        public const uint RefreshmentTableObjectEntry = 186812U;
        public const uint SoulwellObjectEntry = 181621U;
        public const uint ArcanePowderItemEntry = 17020U;
        private const uint ItemFlagSoulbound = 0x1U;

        private static readonly (int Rank, int CasterLevel, int ItemReqLevel)[] ConjureWaterRanks =
        {
            (1, 4, 1), (2, 10, 5), (3, 20, 15), (4, 30, 25),
            (5, 40, 35), (6, 50, 45), (7, 60, 55), (8, 65, 60), (9, 70, 65)
        };

        private static readonly (int Rank, int CasterLevel, int ItemReqLevel)[] ConjureFoodRanks =
        {
            (1, 6, 1), (2, 12, 5), (3, 22, 15), (4, 32, 25),
            (5, 42, 35), (6, 52, 45), (7, 62, 55), (8, 68, 65)
        };

        private static readonly (int Rank, int CasterLevel, int ItemReqLevel)[] CreateHealthstoneRanks =
        {
            (1, 10, 10), (2, 22, 22), (3, 34, 34), (4, 46, 46), (5, 58, 58), (6, 68, 68)
        };

        private readonly Dictionary<string, long> _cooldownUntilUtcMs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private string _activeTradeTarget = string.Empty;
        private long _tradeStartedUtcMs;
        private bool _tradeItemsPlaced;
        private readonly List<(int Slot, uint Entry, int Count)> _offeredItems = new();

        public bool IsTradeBusy => !string.IsNullOrEmpty(_activeTradeTarget) || TradeFrame.Instance.IsVisible;

        /// <summary>
        /// Evaluates what consumables the local player needs based on stacks, mana class, and bag space.
        /// </summary>
        public static ConsumableNeedEvaluation EvaluateConsumableNeeds(
            bool inCombat,
            int freeNormalBagSlots,
            int foodCount,
            int drinkCount,
            bool usesMana,
            bool hasHealthstone,
            bool squadHasWarlock,
            SquadSettings settings)
        {
            if (inCombat || freeNormalBagSlots < 2)
            {
                return default;
            }

            return new ConsumableNeedEvaluation
            {
                WantsFood = foodCount < settings.MinMemberFoodStack,
                WantsDrink = usesMana && drinkCount < settings.MinMemberDrinkStack,
                WantsHealthstone = squadHasWarlock && !hasHealthstone
            };
        }

        /// <summary>
        /// Selects the best non-soulbound bag item that satisfies <c>RequiredLevel &lt;= receiverLevel</c>.
        /// </summary>
        public static TradableConsumableCandidate? SelectBestTradableItem(
            IEnumerable<TradableConsumableCandidate> bagItems,
            int receiverLevel,
            bool wantFood,
            bool wantDrink,
            bool wantHealthstone)
        {
            if (bagItems == null)
                return null;

            return bagItems
                .Where(i => !i.IsSoulbound && i.StackCount > 0 && i.RequiredLevel <= receiverLevel)
                .Where(i =>
                    (wantFood && i.IsFood) ||
                    (wantDrink && i.IsDrink) ||
                    (wantHealthstone && i.IsHealthstone))
                .OrderByDescending(i => i.RequiredLevel)
                .ThenByDescending(i => i.StackCount)
                .FirstOrDefault();
        }

        /// <summary>
        /// Selects the highest WotLK rank for Conjure Water / Conjure Food / Create Healthstone
        /// whose created item RequiredLevel &lt;= <paramref name="receiverLevel"/>.
        /// </summary>
        public static int? SelectBestConjureRank(bool isWater, int receiverLevel, int casterLevel)
        {
            var table = isWater ? ConjureWaterRanks : ConjureFoodRanks;
            int? best = null;
            foreach (var row in table)
            {
                if (casterLevel >= row.CasterLevel && receiverLevel >= row.ItemReqLevel)
                    best = row.Rank;
            }
            return best;
        }

        public static int? SelectBestHealthstoneRank(int receiverLevel, int casterLevel)
        {
            int? best = null;
            foreach (var row in CreateHealthstoneRanks)
            {
                if (casterLevel >= row.CasterLevel && receiverLevel >= row.ItemReqLevel)
                    best = row.Rank;
            }
            return best;
        }

        public static bool IsHealthstoneName(string? name)
        {
            return !string.IsNullOrEmpty(name) &&
                   name.Contains("Healthstone", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsSoulstoneName(string? name)
        {
            return !string.IsNullOrEmpty(name) &&
                   name.Contains("Soulstone", StringComparison.OrdinalIgnoreCase);
        }

        public static int CountFoodInBags()
        {
            var me = StyxWoW.Me;
            if (me == null) return 0;
            int minReq = me.Level;
            return Consumable.GetFood()
                .Where(i => i != null && i.IsValid && i.RequiredLevel <= minReq)
                .Sum(i => (int)i.StackCount);
        }

        public static int CountDrinksInBags()
        {
            var me = StyxWoW.Me;
            if (me == null) return 0;
            int minReq = me.Level;
            return Consumable.GetDrinks()
                .Where(i => i != null && i.IsValid && i.RequiredLevel <= minReq)
                .Sum(i => (int)i.StackCount);
        }

        public static bool HasHealthstoneInBags()
        {
            var me = StyxWoW.Me;
            if (me == null) return false;
            return me.BagItems.Any(i => i != null && i.IsValid && IsHealthstoneName(i.Name));
        }

        private void OnTradeCancelledOrTimeout(string targetName, int cooldownSeconds)
        {
            if (!TradeFrame.IsPartnerAuthorized(targetName, _activeTradeTarget)) return;
            if (!string.IsNullOrWhiteSpace(targetName))
            {
                long until = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Math.Max(1, cooldownSeconds) * 1000L;
                _cooldownUntilUtcMs[targetName.Trim()] = until;
            }
            ResetActiveTradeState();
        }

        public void Stop()
        {
            if (!string.IsNullOrEmpty(_activeTradeTarget) && (!TradeFrame.Instance.IsVisible ||
                TradeFrame.IsPartnerAuthorized(TradeFrame.Instance.PartnerName, _activeTradeTarget)))
                TradeFrame.Instance.CancelTrade();
            ResetActiveTradeState();
        }


        private void ResetActiveTradeState()
        {
            _activeTradeTarget = string.Empty;
            _tradeStartedUtcMs = 0;
            _tradeItemsPlaced = false;
            _offeredItems.Clear();
        }

        private bool IsOnCooldown(string targetName)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return !string.IsNullOrWhiteSpace(targetName) &&
                   _cooldownUntilUtcMs.TryGetValue(targetName.Trim(), out long until) &&
                   now < until;
        }

        /// <summary>
        /// Runs one provisioning tick for either a provider (Mage / Warlock) or a receiver.
        /// </summary>
        public bool RunProvisioningTick(SquadSettings settings, SquadLeaderState? leader)
        {
            var me = StyxWoW.Me;
            if (me == null || !me.IsAlive || me.Combat || me.Mounted)
                return false;

            if (me.IsCasting || me.ChanneledCastingSpellId != 0)
                return true;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 1. Handle currently open TradeFrame first (both receiver and provider safety checks).
            if (TradeFrame.Instance.IsVisible)
            {
                return HandleOpenTradeFrame(me, settings, leader, now);
            }

            // 2. Clean up stale initiated trade timeout if frame never opened.
            if (!string.IsNullOrEmpty(_activeTradeTarget))
            {
                long elapsed = now - _tradeStartedUtcMs;
                if (elapsed > Math.Max(4, settings.TradeOfferTimeoutSeconds) * 1000L)
                {
                    Logging.WriteDebug("[PartyGrind Provision] Trade request with {0} timed out; applying cooldown.", _activeTradeTarget);
                    if (TradeFrame.Instance.IsVisible)
                        TradeFrame.Instance.CancelTrade();
                    OnTradeCancelledOrTimeout(_activeTradeTarget, settings.ProvisionCooldownSeconds);
                    return false;
                }
                return true;
            }

            // 3. Interact with nearby Refreshment Table / Soulwell / Ritual objects if needed!
            if (settings.UseRitualTablesWhenAvailable && TryUseNearbyRitualOrTable(me, settings))
                return true;

            // 4. Warlock Soulstone on Healer (or Tank fallback) out of combat when missing.
            if (me.Class == WoWClass.Warlock && settings.EnableWarlockHealthstones && TryMaintainSoulstone(me, leader))
                return true;

            // 5. Provider behavior when Coordinator authorized us (or solo in phase Provisioning).
            if (leader == null || string.IsNullOrEmpty(leader.ActiveProvisionerName) ||
                !string.Equals(leader.ActiveProvisionerName, me.Name, StringComparison.OrdinalIgnoreCase))
            {
                // Receiver: if coordinator designated us as ActiveTradeTargetName, step closer to provisioner.
                if (leader != null &&
                    !string.IsNullOrEmpty(leader.ActiveProvisionerName) &&
                    string.Equals(leader.ActiveTradeTargetName, me.Name, StringComparison.OrdinalIgnoreCase))
                {
                    var prov = leader.Members?.FirstOrDefault(m =>
                        string.Equals(m.CharacterName, leader.ActiveProvisionerName, StringComparison.OrdinalIgnoreCase));
                    if (prov != null && me.Location.Distance(new WoWPoint(prov.X, prov.Y, prov.Z)) > 8f)
                    {
                        Navigator.MoveTo(new WoWPoint(prov.X, prov.Y, prov.Z), "Approaching provisioner " + prov.CharacterName);
                        return true;
                    }
                }
                return false;
            }

            if (me.Class == WoWClass.Mage && settings.EnableMageProvisioning)
                return RunMageProvisionerTick(me, settings, leader, now);

            if (me.Class == WoWClass.Warlock && settings.EnableWarlockHealthstones)
                return RunWarlockProvisionerTick(me, settings, leader, now);

            return false;
        }

        private bool HandleOpenTradeFrame(LocalPlayer me, SquadSettings settings, SquadLeaderState? leader, long now)
        {
            string partner = TradeFrame.Instance.PartnerName;

            // If we are the provisioner currently trading with _activeTradeTarget:
            if (!string.IsNullOrEmpty(_activeTradeTarget))
            {
                if (!TradeFrame.IsPartnerAuthorized(partner, _activeTradeTarget))
                {
                    TradeFrame.Instance.CancelTrade();
                    ResetActiveTradeState();
                    return true;
                }

                if ((now - _tradeStartedUtcMs) > Math.Max(4, settings.TradeOfferTimeoutSeconds) * 1000L)
                {
                    TradeFrame.Instance.CancelTrade();
                    OnTradeCancelledOrTimeout(_activeTradeTarget, settings.ProvisionCooldownSeconds);
                    return true;
                }

                if (!_tradeItemsPlaced)
                {
                    var targetSummary = leader?.Members?.FirstOrDefault(m =>
                        string.Equals(m.CharacterName, _activeTradeTarget, StringComparison.OrdinalIgnoreCase));
                    int receiverLevel = targetSummary?.Level ?? me.Level;
                    bool wantFood = targetSummary?.WantsFood ?? true;
                    bool wantDrink = targetSummary?.WantsDrink ?? true;
                    bool wantHs = targetSummary?.WantsHealthstone ?? false;

                    int tradeSlot = 1;
                    bool placed = true;
                    foreach (var request in new[] { (wantFood, true, false, false, settings.TargetMemberFoodStack),
                        (wantDrink, false, true, false, settings.TargetMemberDrinkStack), (wantHs, false, false, true, 1) })
                    {
                        if (!request.Item1) continue;
                        var item = FindTradableBagItem(me, receiverLevel, request.Item2, request.Item3, request.Item4);
                        if (item == null) { placed = false; break; }
                        int count = (int)Math.Min(item.StackCount, (uint)Math.Max(1, request.Item5));
                        if (_offeredItems.Any(i => i.Entry == item.Entry && i.Count >= count)) continue;
                        if (!TradeFrame.Instance.PutItem(item, tradeSlot, count) ||
                            !TradeFrame.Instance.ContainsPlayerItem(tradeSlot, item.Entry, count))
                        { placed = false; break; }
                        _offeredItems.Add((tradeSlot, item.Entry, count));
                        tradeSlot++;
                    }
                    if (!placed || tradeSlot == 1)
                    {
                        TradeFrame.Instance.CancelTrade();
                        OnTradeCancelledOrTimeout(_activeTradeTarget, settings.ProvisionCooldownSeconds);
                        return true;
                    }

                    _tradeItemsPlaced = true;
                }

                if (_offeredItems.Count == 0 || _offeredItems.Any(i => !TradeFrame.Instance.ContainsPlayerItem(i.Slot, i.Entry, i.Count)))
                {
                    TradeFrame.Instance.CancelTrade();
                    OnTradeCancelledOrTimeout(_activeTradeTarget, settings.ProvisionCooldownSeconds);
                    return true;
                }
                if (!TradeFrame.Instance.PlayerAccepted)
                    TradeFrame.Instance.AcceptTrade();

                return true;
            }

            // Otherwise we are the RECEIVER: enforce strict partner check against ActiveProvisionerName.
            string expectedProvisioner = leader?.ActiveProvisionerName ?? string.Empty;
            if (!TradeFrame.IsPartnerAuthorized(partner, expectedProvisioner) ||
                !TradeFrame.IsPartnerAuthorized(me.Name, leader?.ActiveTradeTargetName))
            {
                Logging.Write("[PartyGrind Provision] Rejecting unauthorized trade from '{0}' (expected '{1}').",
                    partner, expectedProvisioner);
                TradeFrame.Instance.CancelTrade();
                return true;
            }

            if (TradeFrame.Instance.HasEmptyPlayerOffer && TradeFrame.Instance.TargetAccepted && !TradeFrame.Instance.PlayerAccepted)
            {
                TradeFrame.Instance.AcceptTrade();
            }
            return true;
        }

        private static bool CanUseRefreshment(int receiverLevel)
        {
            if (!SpellManager.Spells.TryGetValue("Conjure Refreshment", out var spell) || spell.CreatesItemId <= 0) return false;
            var item = ItemInfo.FromId((uint)spell.CreatesItemId);
            return item != null && item.RequiredLevel <= receiverLevel;
        }

        private bool RunMageProvisionerTick(LocalPlayer me, SquadSettings settings, SquadLeaderState leader, long now)
        {
            var members = leader.Members ?? new List<SquadMemberSummary>();
            var needers = members.Where(m =>
                m.IsConnected && !m.IsDead && !m.IsGhost &&
                !string.Equals(m.CharacterName, me.Name, StringComparison.OrdinalIgnoreCase) &&
                (m.WantsFood || m.WantsDrink)).ToList();

            // At 70+, drop a Refreshment Table if Arcane Powder is available and >= 2 need food/drink.
            if (settings.UseRitualTablesWhenAvailable &&
                me.Level >= 70 &&
                needers.Count >= 2 &&
                SpellManager.HasSpell("Ritual of Refreshment") &&
                me.BagItems.Any(i => i != null && i.IsValid && i.Entry == ArcanePowderItemEntry) &&
                SpellManager.CanCast("Ritual of Refreshment", me, false, false))
            {
                if (me.IsMoving) WoWMovement.MoveStop();
                return SpellManager.Cast("Ritual of Refreshment", me);
            }

            string targetName = leader.ActiveTradeTargetName;
            if (string.IsNullOrEmpty(targetName) || IsOnCooldown(targetName))
                return false;

            var targetSummary = members.FirstOrDefault(m =>
                string.Equals(m.CharacterName, targetName, StringComparison.OrdinalIgnoreCase));
            if (targetSummary == null || targetSummary.FreeBagSlots < 2)
                return false;

            int receiverLevel = Math.Max(1, targetSummary.Level);

            // Ensure we have tradable food if receiver wants food.
            if (targetSummary.WantsFood && FindTradableBagItem(me, receiverLevel, true, false, false) == null)
            {
                if (me.Level >= 75 && CanUseRefreshment(receiverLevel) && SpellManager.CanCast("Conjure Refreshment", me, false, false))
                {
                    if (me.IsMoving) WoWMovement.MoveStop();
                    return SpellManager.Cast("Conjure Refreshment", me);
                }

                int? foodRank = SelectBestConjureRank(isWater: false, receiverLevel, me.Level);
                if (foodRank.HasValue && SpellManager.HasSpell("Conjure Food"))
                {
                    if (me.IsMoving) WoWMovement.MoveStop();
                    Lua.DoString($"CastSpellByName('Conjure Food(Rank {foodRank.Value})')");
                    return true;
                }
            }

            // Ensure we have tradable water if receiver wants drink.
            if (targetSummary.WantsDrink && FindTradableBagItem(me, receiverLevel, false, true, false) == null)
            {
                if (me.Level >= 75 && CanUseRefreshment(receiverLevel) && SpellManager.CanCast("Conjure Refreshment", me, false, false))
                {
                    if (me.IsMoving) WoWMovement.MoveStop();
                    return SpellManager.Cast("Conjure Refreshment", me);
                }

                int? waterRank = SelectBestConjureRank(isWater: true, receiverLevel, me.Level);
                if (waterRank.HasValue && SpellManager.HasSpell("Conjure Water"))
                {
                    if (me.IsMoving) WoWMovement.MoveStop();
                    Lua.DoString($"CastSpellByName('Conjure Water(Rank {waterRank.Value})')");
                    return true;
                }
            }

            return InitiateTradeWithMember(me, targetSummary, now);
        }

        private bool RunWarlockProvisionerTick(LocalPlayer me, SquadSettings settings, SquadLeaderState leader, long now)
        {
            var members = leader.Members ?? new List<SquadMemberSummary>();
            var needers = members.Where(m =>
                m.IsConnected && !m.IsDead && !m.IsGhost &&
                !string.Equals(m.CharacterName, me.Name, StringComparison.OrdinalIgnoreCase) &&
                m.WantsHealthstone).ToList();

            if (settings.UseRitualTablesWhenAvailable &&
                me.Level >= 68 &&
                needers.Count >= 2 &&
                SpellManager.CanCast("Ritual of Souls", me, false, false))
            {
                if (me.IsMoving) WoWMovement.MoveStop();
                return SpellManager.Cast("Ritual of Souls", me);
            }

            string targetName = leader.ActiveTradeTargetName;
            if (string.IsNullOrEmpty(targetName) || IsOnCooldown(targetName))
                return false;

            var targetSummary = members.FirstOrDefault(m =>
                string.Equals(m.CharacterName, targetName, StringComparison.OrdinalIgnoreCase));
            if (targetSummary == null || targetSummary.FreeBagSlots < 2)
                return false;

            if (FindTradableBagItem(me, targetSummary.Level, false, false, true) == null)
            {
                int? hsRank = SelectBestHealthstoneRank(targetSummary.Level, me.Level);
                if (hsRank.HasValue && SpellManager.HasSpell("Create Healthstone"))
                {
                    if (me.IsMoving) WoWMovement.MoveStop();
                    Lua.DoString($"CastSpellByName('Create Healthstone(Rank {hsRank.Value})')");
                    return true;
                }
                return false;
            }

            return InitiateTradeWithMember(me, targetSummary, now);
        }

        private bool InitiateTradeWithMember(LocalPlayer me, SquadMemberSummary targetSummary, long now)
        {
            WoWPlayer? targetPlayer = targetSummary.Guid != 0
                ? ObjectManager.GetObjectByGuid<WoWPlayer>(targetSummary.Guid)
                : ObjectManager.GetObjectsOfType<WoWPlayer>(false, false)
                    .FirstOrDefault(p => string.Equals(p.Name, targetSummary.CharacterName, StringComparison.OrdinalIgnoreCase));

            if (targetPlayer == null || !targetPlayer.IsValid || targetPlayer.Dead)
                return false;

            if (targetPlayer.Distance > 8f)
            {
                Navigator.MoveTo(targetPlayer.Location, "Moving to trade " + targetPlayer.Name);
                return true;
            }

            if (me.IsMoving) WoWMovement.MoveStop();
            if (me.CurrentTargetGuid != targetPlayer.Guid)
                targetPlayer.Target();

            if (me.CurrentTargetGuid != targetPlayer.Guid)
                return false;

            _activeTradeTarget = targetSummary.CharacterName;
            _tradeStartedUtcMs = now;
            _tradeItemsPlaced = false;
            _offeredItems.Clear();
            TradeFrame.Instance.InitiateTrade("target");
            return true;
        }

        private static bool TryUseNearbyRitualOrTable(LocalPlayer me, SquadSettings settings)
        {
            if (me.FreeNormalBagSlots < 2)
                return false;

            var gameObjects = ObjectManager.GetObjectsOfType<WoWGameObject>(false, false)
                .Where(g => g != null && g.IsValid && g.Location.Distance(me.Location) <= 25f)
                .ToList();

            // 1. Assist active Ritual summoning portal if created by a party member.
            var memberGuids = SquadContext.GetKnownMemberGuids();
            var ritual = gameObjects.FirstOrDefault(g =>
                g.SubType == WoWGameObjectType.Ritual &&
                g.CreatedByGuid != me.Guid &&
                memberGuids.Contains(g.CreatedByGuid));
            if (ritual != null)
            {
                if (ritual.Location.Distance(me.Location) > 4.5f)
                    Navigator.MoveTo(ritual.Location, "Assisting party ritual");
                else
                    ritual.Interact();
                return true;
            }

            // 2. Click Refreshment Table when needing food or drink.
            bool wantsFoodOrDrink = CountFoodInBags() < settings.MinMemberFoodStack ||
                ((me.PowerType == WoWPowerType.Mana || me.Class == WoWClass.Druid) &&
                 CountDrinksInBags() < settings.MinMemberDrinkStack);

            if (wantsFoodOrDrink)
            {
                var table = gameObjects.FirstOrDefault(g => g.Entry == RefreshmentTableObjectEntry);
                if (table != null)
                {
                    if (table.Location.Distance(me.Location) > 4.5f)
                        Navigator.MoveTo(table.Location, "Moving to Refreshment Table");
                    else
                        table.Interact();
                    return true;
                }
            }

            // 3. Click Soulwell when lacking a Healthstone.
            if (settings.EnableWarlockHealthstones && !HasHealthstoneInBags())
            {
                var well = gameObjects.FirstOrDefault(g => g.Entry == SoulwellObjectEntry);
                if (well != null)
                {
                    if (well.Location.Distance(me.Location) > 4.5f)
                        Navigator.MoveTo(well.Location, "Moving to Soulwell");
                    else
                        well.Interact();
                    return true;
                }
            }

            return false;
        }

        private static bool TryMaintainSoulstone(LocalPlayer me, SquadLeaderState? leader)
        {
            var target = leader?.Members?
                .Where(m => m.IsConnected && !m.IsDead && !m.IsGhost &&
                    (m.Role == SquadRole.Healer || m.Role == SquadRole.TankLeader))
                .Select(m => new { Member = m, Unit = ObjectManager.GetObjectByGuid<WoWPlayer>(m.Guid) })
                .Where(p => p.Unit != null && p.Unit.IsValid && !p.Unit.Dead && !p.Unit.IsGhost &&
                    p.Unit.Distance <= 30f && !p.Unit.HasAura("Soulstone Resurrection"))
                .OrderBy(p => p.Member.Role == SquadRole.Healer ? 0 : 1)
                .Select(p => p.Unit).FirstOrDefault();
            if (target == null) return false;

            WoWItem? stone = me.BagItems.FirstOrDefault(i => i != null && i.IsValid && IsSoulstoneName(i.Name));
            if (stone != null)
            {
                if (stone.Cooldown == 0)
                {
                    if (me.IsMoving) WoWMovement.MoveStop();
                    return stone.Use(target.Guid);
                }
                return false;
            }

            if (SpellManager.CanCast("Create Soulstone", me, false, false))
            {
                if (me.IsMoving) WoWMovement.MoveStop();
                return SpellManager.Cast("Create Soulstone", me);
            }

            return false;
        }

        private static WoWItem? FindTradableBagItem(
            LocalPlayer me,
            int receiverLevel,
            bool isFood,
            bool isDrink,
            bool isHealthstone)
        {
            IEnumerable<WoWItem> candidates;
            if (isFood)
                candidates = Consumable.GetFood();
            else if (isDrink)
                candidates = Consumable.GetDrinks();
            else if (isHealthstone)
                candidates = me.BagItems.Where(i => i != null && i.IsValid && IsHealthstoneName(i.Name));
            else
                return null;

            return candidates
                .Where(i => i != null && i.IsValid && i.StackCount > 0 &&
                            (i.Flags & ItemFlagSoulbound) == 0 &&
                            i.RequiredLevel <= receiverLevel)
                .OrderByDescending(i => i.RequiredLevel)
                .ThenByDescending(i => i.StackCount)
                .FirstOrDefault();
        }
    }
}
