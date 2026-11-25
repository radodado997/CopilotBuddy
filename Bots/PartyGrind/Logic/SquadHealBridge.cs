using System;
using System.Collections.Generic;
using System.Linq;
using Styx;
using Styx.Combat.CombatRoutine;
using Styx.CommonBot;
using Styx.Helpers;
using Styx.Logic.Combat;
using Styx.Logic.Pathing;
using Styx.PartyGrind;
using Styx.PartyGrind.IPC;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;
using TreeSharp;

namespace CopilotBuddy.Bots.PartyGrind.Logic
{
    /// <summary>
    /// Lightweight candidate representation used for priority heal selection and unit tests.
    /// </summary>
    public sealed class SquadHealCandidate
    {
        public ulong Guid { get; init; }
        public string Name { get; init; } = string.Empty;
        public double HealthPercent { get; init; }
        public double Distance { get; init; }
        public bool InCombat { get; init; }
        public bool IsTankLeader { get; init; }
        public bool IsSelf { get; init; }
    }

    /// <summary>
    /// Two-stage healer & hybrid off-heal bridge that guarantees reliable party healing
    /// from level 1–80 even when Singular lowbie/open-world HealBehavior only self-heals.
    /// </summary>
    public static class SquadHealBridge
    {
        private static readonly SquadApproach HealApproach = new();
        private static readonly Dictionary<ulong, (int Failures, long RetryUntil)> _resApproaches = new();
        private static readonly Dictionary<ulong, (double Distance, long Progress)> _resProgress = new();
        private static readonly Dictionary<ulong, long> _resPending = new();
        private static readonly Dictionary<ulong, long> _resCastRetry = new();
        private static readonly Dictionary<string, long> _healDiagRetry = new();
        private static Composite? _activeHealBehavior;

        /// <summary>Stops a retained routine when healing is no longer being ticked.</summary>
        public static void StopActiveHealBehavior()
        {
            if (_activeHealBehavior == null)
                return;
            try { _activeHealBehavior.Stop(null); } catch { }
            _activeHealBehavior = null;
        }

        private static void LogHealDiagnostic(string key, string message)
        {
            long now = Environment.TickCount64;
            if (_healDiagRetry.TryGetValue(key, out long allowedAt) && now < allowedAt)
                return;
            _healDiagRetry[key] = now + 15000;
            Logging.WriteDebug($"[PartyGrind Heal {StyxWoW.Me?.Name}] {message}");
        }
        private static readonly HashSet<string> RangedAssistSpellSet = new(StringComparer.OrdinalIgnoreCase)
        {
            "Smite", "Holy Fire", "Shadow Word: Pain",
            "Lightning Bolt", "Flame Shock", "Earth Shock",
            "Wrath", "Moonfire", "Starfire",
            "Exorcism", "Judgement of Light", "Judgement of Wisdom"
        };
        private static readonly string[] RangedAssistSpells =
        {
            "Smite", "Holy Fire", "Shadow Word: Pain",
            "Lightning Bolt", "Flame Shock", "Earth Shock",
            "Wrath", "Moonfire", "Starfire",
            "Exorcism", "Judgement of Light", "Judgement of Wisdom"
        };

        /// <summary>
        /// Selects the highest-priority heal target. Applies a -15% effective HP priority bonus
        /// for the TankLeader (unless emergency self-preservation puts self below critical HP).
        /// </summary>
        public static SquadHealCandidate? SelectPriorityHealTarget(
            IEnumerable<SquadHealCandidate> candidates,
            double healthThresholdPct,
            bool prioritizeSelfCritical = true)
        {
            if (candidates == null)
                return null;

            var eligible = candidates
                .Where(c => c.Distance <= 45.0 && c.HealthPercent < healthThresholdPct)
                .ToList();

            if (eligible.Count == 0)
                return null;

            if (prioritizeSelfCritical)
            {
                var selfCrit = eligible.FirstOrDefault(c => c.IsSelf && c.HealthPercent < 35.0);
                if (selfCrit != null)
                    return selfCrit;
            }

            return eligible
                .OrderBy(c => c.HealthPercent - (c.IsTankLeader ? 15.0 : 0.0))
                .ThenBy(c => c.HealthPercent)
                .ThenBy(c => c.Distance)
                .First();
        }

        public static int CountInjuredAllies(IEnumerable<SquadHealCandidate> candidates, double healthThresholdPct)
        {
            if (candidates == null)
                return 0;
            return candidates.Count(c => c.Distance <= 38.0 && c.HealthPercent < healthThresholdPct);
        }

        // Out-of-combat healing is deliberately below the rest hysteresis
        // (SquadSettings.TriggerRestHealthPct = 50 by default): food/drink regenerate the party
        // for free, so the healer only spends mana on real damage.
        internal const double HealThresholdCombat = 75.0;
        internal const double HealThresholdOutOfCombat = 40.0;
        internal const double HealThresholdResting = 40.0;
        internal const double HealThresholdDrinking = 35.0;
        // The routine healer owns its own (much greedier) targeting, so the bridge hands over
        // control only when somebody actually needs a heal right now, never for top-ups.
        internal const double RoutineHealHandoffThreshold = 70.0;
        internal const double LowManaSpellSwapPercent = 45.0;
        internal const long HealDuplicateWindowMs = 2500L;
        private static readonly Dictionary<ulong, (string Spell, long AtUtcMs)> _recentHealCasts = new();

        /// <summary>
        /// Computes the maximum health percentage at which a healer or off-healer should act,
        /// conserving mana out of combat and avoiding interrupting active drinking/eating
        /// unless a squadmate is in combat or critically injured.
        /// </summary>
        public static double ComputeEffectiveHealThreshold(
            bool emergencyOnly,
            bool anyCombat,
            bool healerActivelyDrinkingOrEating,
            bool healerInRestPhase,
            bool mainHealerUnavailableOrOom = false)
        {
            if (emergencyOnly)
            {
                if (healerActivelyDrinkingOrEating && !anyCombat)
                    return HealThresholdDrinking;
                if (mainHealerUnavailableOrOom)
                    return anyCombat ? 70.0 : 55.0;
                return anyCombat ? 50.0 : HealThresholdOutOfCombat;
            }

            if (anyCombat)
                return HealThresholdCombat;

            if (healerActivelyDrinkingOrEating)
                return HealThresholdDrinking;

            if (healerInRestPhase)
                return HealThresholdResting;

            return HealThresholdOutOfCombat;
        }

        /// <summary>
        /// Caps the heal threshold by the healer's own mana pool: a low-mana healer only reacts to
        /// targets that would otherwise die, instead of topping the party off and going OOM.
        /// </summary>
        public static double ApplyManaReserveThreshold(double threshold, double healerManaPercent)
        {
            if (threshold <= 0.0 || healerManaPercent >= 45.0)
                return threshold;

            double reserveCap = healerManaPercent < 15.0 ? 35.0
                : healerManaPercent < 30.0 ? 50.0
                : 60.0;
            return Math.Min(threshold, reserveCap);
        }

        internal static void RecordHealCast(ulong targetGuid, string? spellName, long nowUtcMs)
        {
            if (targetGuid == 0 || string.IsNullOrEmpty(spellName))
                return;

            if (_recentHealCasts.Count > 32)
            {
                var stale = _recentHealCasts
                    .Where(kv => nowUtcMs - kv.Value.AtUtcMs > HealDuplicateWindowMs)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (ulong guid in stale)
                    _recentHealCasts.Remove(guid);
            }

            _recentHealCasts[targetGuid] = (spellName!, nowUtcMs);
        }

        internal static bool IsHealCastDuplicate(ulong targetGuid, string? spellName, long nowUtcMs)
        {
            if (targetGuid == 0 || string.IsNullOrEmpty(spellName))
                return false;
            if (!_recentHealCasts.TryGetValue(targetGuid, out var last))
                return false;
            if (nowUtcMs - last.AtUtcMs > HealDuplicateWindowMs)
                return false;
            return string.Equals(last.Spell, spellName, StringComparison.OrdinalIgnoreCase);
        }

        internal static void ResetHealCastHistory() => _recentHealCasts.Clear();

        private static double ReadManaPercent(LocalPlayer me)
        {
            if (me.MaxMana <= 0)
                return 100.0;
            return me.PowerType == WoWPowerType.Mana ? me.PowerPercent : me.ManaPercent;
        }

        private static double ResolveHealThreshold(LocalPlayer me, double baseThreshold) =>
            ApplyManaReserveThreshold(baseThreshold, ReadManaPercent(me));

        private static bool IsSquadCombatActive(LocalPlayer me, SquadLeaderState? leader, IReadOnlyCollection<SquadHealCandidate> candidates)
        {
            if (me.Combat)
                return true;
            if (leader != null && (leader.InCombat || leader.Members?.Any(m => m.IsConnected && !m.IsDead && m.IsInCombat) == true))
                return true;
            return candidates.Any(c => c.InCombat);
        }

        internal static bool IsSquadMidWipe(SquadLeaderState? leader)
        {
            if (leader?.Members == null)
                return false;

            int alive = 0;
            int down = 0;
            float lowestAliveHealth = 100f;
            foreach (var member in leader.Members)
            {
                if (!member.IsConnected)
                    continue;
                if (member.IsDead || member.IsGhost)
                {
                    down++;
                    continue;
                }

                alive++;
                if (member.HealthPercent < lowestAliveHealth)
                    lowestAliveHealth = member.HealthPercent;
            }

            return down > 0 && alive >= 2 && lowestAliveHealth < 80f;
        }

        private static bool IsActivelyConsumingRestAura(LocalPlayer me)
        {
            bool drinking = (me.HasAura("Drink") || me.HasAura("Refreshment")) &&
                            (me.PowerType != WoWPowerType.Mana || me.PowerPercent < 95.0);
            bool eating = (me.HasAura("Food") || me.HasAura("Refreshment")) &&
                          me.HealthPercent < 95.0;
            return drinking || eating;
        }

        /// <summary>
        /// Checks whether any nearby squadmate meets the current combat/OOC/rest heal threshold.
        /// </summary>
        public static bool HasHealCandidate(SquadLeaderState? leader, bool emergencyOnly = false)
        {
            var me = StyxWoW.Me;
            if (me == null || !me.IsAlive)
                return false;

            var candidates = CollectLiveHealCandidates(leader);
            if (candidates.Count == 0)
                return false;

            bool squadCombat = IsSquadCombatActive(me, leader, candidates);
            bool midWipe = IsSquadMidWipe(leader);
            bool anyCombat = squadCombat || midWipe;
            bool activelyDrinking = !anyCombat && IsActivelyConsumingRestAura(me);
            bool inRestPhase = !anyCombat &&
                ((leader != null && leader.Phase == SquadPhase.Resting) ||
                 SquadContext.GetLocalMemberStateSnapshot()?.NeedsRest == true);
            bool mainHealerDown = false;
            if (emergencyOnly && leader?.Members != null)
            {
                var mainHealer = leader.Members.FirstOrDefault(m => m.Role == SquadRole.Healer);
                mainHealerDown = mainHealer != null &&
                    (mainHealer.IsDead || mainHealer.IsGhost || !mainHealer.IsConnected || mainHealer.PowerPercent < 20f);
            }
            double threshold = ResolveHealThreshold(me, ComputeEffectiveHealThreshold(
                emergencyOnly: emergencyOnly,
                anyCombat: anyCombat,
                healerActivelyDrinkingOrEating: activelyDrinking,
                healerInRestPhase: inRestPhase,
                mainHealerUnavailableOrOom: mainHealerDown));

            if (!candidates.Any(c => c.HealthPercent < threshold))
                return false;

            if (!emergencyOnly && me.Level >= 10 && RoutineManager.Current?.HealBehavior != null)
                return true;

            int injured78 = CountInjuredAllies(candidates, 78.0);
            int injured82 = CountInjuredAllies(candidates, 82.0);
            foreach (var c in candidates)
            {
                if (c.HealthPercent >= threshold)
                    continue;

                WoWUnit? targetUnit = c.IsSelf ? me : ObjectManager.GetObjectByGuid<WoWUnit>(c.Guid);
                if (targetUnit == null || !targetUnit.IsValid || targetUnit.Dead)
                    continue;

                bool combatActive = anyCombat || targetUnit.Combat;
                var (spellName, _) = SelectHealSpellForClass(me, targetUnit, injured78, injured82, emergencyOnly, combatActive);
                if (spellName != null)
                    return true;

                if (!c.IsSelf)
                {
                    var (approachSpell, _) = SelectHealSpellForClass(me, targetUnit, injured78, injured82, emergencyOnly, combatActive, checkRange: false);
                    if (approachSpell != null)
                        return true;
                }
            }

            return false;
        }

        public static bool RunHealerTick(bool emergencyOnly, SquadSettings settings, SquadLeaderState? leader)
        {
            var me = StyxWoW.Me;
            if (me == null || !me.IsAlive)
                return false;

            long nowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var candidates = CollectLiveHealCandidates(leader);
            bool squadCombat = IsSquadCombatActive(me, leader, candidates);
            bool midWipe = IsSquadMidWipe(leader);
            bool anyCombat = squadCombat || midWipe;
            bool activelyDrinking = !anyCombat && IsActivelyConsumingRestAura(me);
            bool inRestPhase = !anyCombat &&
                ((leader != null && leader.Phase == SquadPhase.Resting) ||
                 SquadContext.GetLocalMemberStateSnapshot()?.NeedsRest == true);

            // Check if a HybridOffHeal should step in aggressively because main healer is dead or < 20% mana.
            bool mainHealerDown = false;
            if (emergencyOnly && leader?.Members != null)
            {
                var mainHealer = leader.Members.FirstOrDefault(m => m.Role == SquadRole.Healer);
                mainHealerDown = mainHealer != null &&
                    (mainHealer.IsDead || mainHealer.IsGhost || !mainHealer.IsConnected || mainHealer.PowerPercent < 20f);
            }

            double threshold = ResolveHealThreshold(me, ComputeEffectiveHealThreshold(
                emergencyOnly: emergencyOnly,
                anyCombat: anyCombat,
                healerActivelyDrinkingOrEating: activelyDrinking,
                healerInRestPhase: inRestPhase,
                mainHealerUnavailableOrOom: mainHealerDown));

            if (me.IsCasting || me.ChanneledCastingSpellId != 0)
            {
                string? castingName = me.CastingSpell?.Name;
                double cancelThreshold = Math.Min(75.0, threshold);
                if (!string.IsNullOrEmpty(castingName) && RangedAssistSpellSet.Contains(castingName) &&
                    SelectPriorityHealTarget(candidates, cancelThreshold, prioritizeSelfCritical: settings.UseEmergencySelfHealBeforeParty) != null)
                {
                    SpellManager.StopCasting();
                }
                else
                {
                    return true;
                }
            }

            if (candidates.Count == 0 || (!emergencyOnly && me.Level < 10))
                StopActiveHealBehavior();

            var remaining = candidates.ToList();
            bool triedRoutine = false;
            while (remaining.Count > 0)
            {
                var priorityCandidate = SelectPriorityHealTarget(
                    remaining,
                    threshold,
                    prioritizeSelfCritical: settings.UseEmergencySelfHealBeforeParty);

                if (priorityCandidate == null)
                    return false;

                remaining.Remove(priorityCandidate);
                WoWUnit? targetUnit = priorityCandidate.IsSelf
                    ? me
                    : ObjectManager.GetObjectByGuid<WoWUnit>(priorityCandidate.Guid);

                if (targetUnit == null || !targetUnit.IsValid || targetUnit.Dead)
                    continue;

                // Stage 1: Try Routine's HealBehavior when level >= 10 and not in Hybrid emergency-only mode.
                // The routine targets on its own thresholds (it happily heals 80-90% members), so it
                // only takes over during combat and only when a target is genuinely low.
                bool routineHandoffAllowed = anyCombat &&
                    priorityCandidate.HealthPercent < Math.Min(threshold, RoutineHealHandoffThreshold);
                if (!routineHandoffAllowed)
                    StopActiveHealBehavior();
                if (!triedRoutine && routineHandoffAllowed && !emergencyOnly && me.Level >= 10 &&
                    targetUnit.Distance <= 35f && targetUnit.InLineOfSpellSight)
                {
                    triedRoutine = true;
                    var healBehavior = RoutineManager.Current?.HealBehavior;
                    if (healBehavior != null)
                    {
                        try
                        {
                            HealTargeting.Instance.Pulse();
                            if (!ReferenceEquals(_activeHealBehavior, healBehavior))
                            {
                                _activeHealBehavior?.Stop(null);
                                _activeHealBehavior = null;
                                healBehavior.Start(null);
                                _activeHealBehavior = healBehavior;
                            }
                            RunStatus status = healBehavior.Tick(null);
                            if (status == RunStatus.Running || me.IsCasting || me.ChanneledCastingSpellId != 0)
                            {
                                RecordHealCast(targetUnit.Guid, me.CastingSpell?.Name, nowUtcMs);
                                SquadPartySyncService.LogAction("Running heal routine");
                                return true;
                            }
                            healBehavior.Stop(null);
                            _activeHealBehavior = null;
                            if (status == RunStatus.Success && (SpellManager.GlobalCooldown || me.IsMoving))
                            {
                                SquadPartySyncService.LogAction("Running heal routine");
                                return true;
                            }
                        }
                        catch (Exception ex)
                        {
                            // Start may have succeeded before Tick threw — clean up either way.
                            try { healBehavior.Stop(null); } catch { }
                            _activeHealBehavior = null;
                            Logging.WriteDebug("[PartyGrind Heal] Routine HealBehavior fallback: {0}", ex.Message);
                        }
                    }
                }

                // Stage 2: Explicit class-aware party heal fallback (Level 1-80, open world & instances).
                if (targetUnit.Distance > 35f || !targetUnit.InLineOfSpellSight)
                {
                    if (HealApproach.TryMove(targetUnit, "Moving to heal " + targetUnit.Name)) return true;
                    continue;
                }

                int injured78 = CountInjuredAllies(candidates, 78.0);
                int injured82 = CountInjuredAllies(candidates, 82.0);
                bool combatActive = anyCombat || targetUnit.Combat;

                var (spellName, blockedSpell) = SelectHealSpellForClass(me, targetUnit, injured78, injured82, emergencyOnly, combatActive);
                if (spellName != null && IsHealCastDuplicate(targetUnit.Guid, spellName, nowUtcMs))
                    continue;
                if (spellName == null)
                {
                    if (blockedSpell != null)
                    {
                        if (targetUnit.HealthPercent < 50)
                            LogHealDiagnostic(targetUnit.Guid + "|blocked", "Heal blocked: " + blockedSpell + " for " + targetUnit.Name);
                    }
                    else if (targetUnit.HealthPercent < 60)
                        LogHealDiagnostic(targetUnit.Guid + "|no-spell", "Heal: no spell for " + targetUnit.Name + " (" + targetUnit.Class + ")");
                    continue;
                }

                if (SpellManager.Spells.TryGetValue(spellName, out WoWSpell? spellInfo) && spellInfo.CastTime > 0 && me.IsMoving)
                {
                    WoWMovement.MoveStop();
                }

                if (SpellManager.CanCast(spellName, targetUnit, true, false))
                {
                    if (SpellManager.Cast(spellName, targetUnit))
                    {
                        RecordHealCast(targetUnit.Guid, spellName, nowUtcMs);
                        SquadPartySyncService.LogAction("Healing " + targetUnit.Name + " with " + spellName);
                        return true;
                    }
                }
                else if (targetUnit.HealthPercent < 50)
                {
                    LogHealDiagnostic(targetUnit.Guid + "|blocked", "Heal blocked: " + spellName + " for " + targetUnit.Name);
                }

            }
            return false;
        }

        /// <summary>
        /// Out-of-combat resurrection behavior for Healer / HybridOffHeal / res-capable classes.
        /// </summary>
        public static string? GetResurrectionSpell(WoWClass wowClass) => wowClass switch
            {
                WoWClass.Priest => "Resurrection",
                WoWClass.Paladin => "Redemption",
                WoWClass.Druid => "Revive",
                WoWClass.Shaman => "Ancestral Spirit",
                _ => null
            };


        public static bool RunPostCombatResurrectionTick()
        {
            var me = StyxWoW.Me;
            if (me == null || !me.IsAlive || me.Combat || me.Mounted)
                return false;

            if (me.IsCasting || me.ChanneledCastingSpellId != 0)
            {
                foreach (ulong guid in _resPending.Keys.ToList())
                {
                    _resCastRetry[guid] = Environment.TickCount64 + 15000;
                    _resPending.Remove(guid);
                }
                return true;
            }

            string? resSpell = GetResurrectionSpell(me.Class);

            if (string.IsNullOrEmpty(resSpell) || !SpellManager.HasSpell(resSpell))
                return false;

            var memberGuids = SquadContext.GetKnownMemberGuids();
            long now = Environment.TickCount64;
            foreach (ulong stale in _resApproaches.Keys.Where(g => !memberGuids.Contains(g)).ToList())
                _resApproaches.Remove(stale);
            // A new death episode starts with a clean slate.
            foreach (var player in ObjectManager.GetObjectsOfType<WoWPlayer>(false, false))
                if (player != null && _resApproaches.ContainsKey(player.Guid) && (!player.Dead || player.IsGhost))
                    _resApproaches.Remove(player.Guid);

            var deadAllies = ObjectManager.GetObjectsOfType<WoWPlayer>(false, false)
                .Where(p => p != null && p.IsValid && p.Dead && !p.IsGhost &&
                            memberGuids.Contains(p.Guid) && p.Location.Distance(me.Location) <= 45f)
                .OrderBy(p => p.Location.Distance(me.Location))
                .ToList();

            var active = deadAllies.Select(p => p.Guid).ToHashSet();
            foreach (ulong guid in _resProgress.Keys.Concat(_resPending.Keys).Concat(_resCastRetry.Keys).Distinct().Where(g => !active.Contains(g)).ToList())
            {
                _resProgress.Remove(guid);
                _resPending.Remove(guid);
                _resCastRetry.Remove(guid);
            }
            foreach (var deadAlly in deadAllies)
            {
                if (_resPending.TryGetValue(deadAlly.Guid, out long dispatched))
                {
                    if (now - dispatched < 2000) return true;
                    _resPending.Remove(deadAlly.Guid);
                    _resCastRetry[deadAlly.Guid] = now + 15000;
                    continue;
                }
                if (_resCastRetry.TryGetValue(deadAlly.Guid, out long retry) && now < retry)
                    continue;
                _resApproaches.TryGetValue(deadAlly.Guid, out var attempt);
                if (deadAlly.Distance > 28f || !deadAlly.InLineOfSpellSight)
                {
                    // Exhausted approaches suppress further movement, but the corpse
                    // remains eligible for a direct cast if it comes into range/LoS.
                    if (attempt.Failures >= 3 || now < attempt.RetryUntil)
                        continue;
                    if (!_resProgress.TryGetValue(deadAlly.Guid, out var progress) || deadAlly.Distance < progress.Distance - 1f)
                        _resProgress[deadAlly.Guid] = progress = (deadAlly.Distance, now);
                    var result = Navigator.MoveTo(deadAlly.Location, "Moving to resurrect " + deadAlly.Name);
                    // Path failures and arrival without line of sight count against this
                    // corpse; try the next one instead of retrying it forever.
                    if (result is MoveResult.Failed or MoveResult.PathGenerationFailed or MoveResult.ReachedDestination || now - progress.Progress >= 5000)
                    {
                        int failures = _resApproaches.TryGetValue(deadAlly.Guid, out var previous) ? previous.Failures + 1 : 1;
                        _resApproaches[deadAlly.Guid] = (failures, now + 15000);
                        _resProgress.Remove(deadAlly.Guid);
                        Navigator.Clear();
                        continue;
                    }
                    return true;
                }

                if (me.IsMoving)
                    WoWMovement.MoveStop();

                if (SpellManager.CanCast(resSpell, deadAlly, true, false))
                {
                    Logging.Write("[PartyGrind] Resurrecting {0} with {1}", deadAlly.Name, resSpell);
                    // A failed cast is not success: drop this corpse only when the cast
                    // actually lands, otherwise try the next eligible corpse.
                    if (SpellManager.Cast(resSpell, deadAlly))
                    {
                        _resPending[deadAlly.Guid] = now;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Safe ranged assist DPS for Healer when all squadmates are healthy and mana >= threshold.
        /// Never walks into melee range.
        /// </summary>
        public static bool TryHealerAssistDps(SquadSettings settings, SquadLeaderState? leader)
        {
            var me = StyxWoW.Me;
            if (me == null || !me.IsAlive || !settings.HealerAssistDps || leader == null)
                return false;

            // Keep a mana reserve for healing: assisting below the reserve is what turned
            // harmless Smite filler into an out-of-mana wipe.
            int assistManaFloor = Math.Max(settings.HealerDpsAboveManaPct, 80);
            if (me.PowerType == WoWPowerType.Mana && me.PowerPercent < assistManaFloor)
                return false;

            var candidates = CollectLiveHealCandidates(leader);
            if (candidates.Any(c => c.HealthPercent < 94.0))
                return false;

            ulong targetGuid = leader.FocusTargetGuid != 0 ? leader.FocusTargetGuid : leader.SecondaryTargetGuid;
            if (targetGuid == 0)
                return false;

            WoWUnit? target = ObjectManager.GetObjectByGuid<WoWUnit>(targetGuid);
            if (target == null || !target.IsValid || target.Dead || !target.Attackable || target.IsFriendly)
                return false;

            if (target.Distance > 30f || !target.InLineOfSpellSight)
                return false;

            if (me.CurrentTargetGuid != target.Guid)
                target.Target();

            if (!me.IsFacing(target))
                target.Face();

            foreach (string spell in RangedAssistSpells)
            {
                if (SpellManager.HasSpell(spell) && SpellManager.CanCast(spell, target, true, false))
                {
                    if (SpellManager.Spells.TryGetValue(spell, out WoWSpell? info) && info.CastTime > 0 && me.IsMoving)
                        WoWMovement.MoveStop();
                    return SpellManager.Cast(spell, target);
                }
            }

            return false;
        }

        private static List<SquadHealCandidate> CollectLiveHealCandidates(SquadLeaderState? leader)
        {
            var me = StyxWoW.Me;
            var result = new List<SquadHealCandidate>();
            if (me == null)
                return result;

            var seenGuids = new HashSet<ulong>();
            ulong tankGuid = leader?.LeaderGuid ?? 0UL;

            result.Add(new SquadHealCandidate
            {
                Guid = me.Guid,
                Name = me.Name ?? "Me",
                HealthPercent = me.HealthPercent,
                Distance = 0,
                InCombat = me.Combat,
                IsTankLeader = me.Guid == tankGuid || SquadContext.ActiveRole == SquadRole.TankLeader,
                IsSelf = true
            });
            seenGuids.Add(me.Guid);

            foreach (ulong guid in SquadContext.GetKnownMemberGuids())
            {
                if (!seenGuids.Add(guid))
                    continue;

                WoWUnit? unit = ObjectManager.GetObjectByGuid<WoWUnit>(guid);
                if (unit == null || !unit.IsValid || unit.Dead || unit.IsGhost)
                    continue;

                double dist = unit.Location.Distance(me.Location);
                if (dist > 45.0)
                    continue;

                bool isTank = guid == tankGuid || SquadContext.IsMemberRole(guid, SquadRole.TankLeader);
                result.Add(new SquadHealCandidate
                {
                    Guid = guid,
                    Name = unit.Name ?? string.Empty,
                    HealthPercent = unit.HealthPercent,
                    Distance = dist,
                    InCombat = unit.Combat,
                    IsTankLeader = isTank,
                    IsSelf = false
                });
            }

            return result;
        }

        internal static (string? SpellName, string? BlockedSpell) EvaluateClassHealSpell(
            WoWClass healerClass,
            double hp,
            bool isTank,
            bool inCombat,
            bool emergencyOnly,
            int injured78Count,
            int injured82Count,
            bool hasWeakenedSoul,
            Func<string, bool> hasAuraFromHealer,
            Func<string, bool> hasSpell,
            Func<string, bool> canCastSpell,
            double healerManaPercent = 100.0)
        {
            string? blockedSpell = null;
            bool lowMana = healerManaPercent < LowManaSpellSwapPercent;

            bool CanCastHeal(string spellName)
            {
                if (!hasSpell(spellName))
                    return false;
                if (canCastSpell(spellName))
                    return true;

                blockedSpell ??= spellName;
                return false;
            }

            switch (healerClass)
            {
                case WoWClass.Priest:
                    if (lowMana)
                    {
                        if (hp < 30.0 && CanCastHeal("Flash Heal"))
                            return ("Flash Heal", null);
                        if (hp < (inCombat ? 70.0 : 60.0) && !hasAuraFromHealer("Renew") && CanCastHeal("Renew"))
                            return ("Renew", null);
                        if (hp < (inCombat ? 62.0 : 52.0) && CanCastHeal("Heal"))
                            return ("Heal", null);
                        if (hp < (inCombat ? 62.0 : 52.0) && CanCastHeal("Lesser Heal"))
                            return ("Lesser Heal", null);
                        break;
                    }
                    // Critical health first: a shield does not stop lethal damage on a 20% target.
                    if (hp < 35.0 && CanCastHeal("Flash Heal"))
                        return ("Flash Heal", null);
                    if (hp < 25.0 && CanCastHeal("Greater Heal"))
                        return ("Greater Heal", null);
                    if (!emergencyOnly && injured78Count >= 3 && CanCastHeal("Prayer of Healing"))
                        return ("Prayer of Healing", null);
                    if (!emergencyOnly && injured78Count >= 2 && CanCastHeal("Circle of Healing"))
                        return ("Circle of Healing", null);
                    if (inCombat &&
                        hp < (isTank ? 80.0 : 50.0) &&
                        !hasWeakenedSoul &&
                        !hasAuraFromHealer("Power Word: Shield") &&
                        CanCastHeal("Power Word: Shield"))
                        return ("Power Word: Shield", null);
                    if (!emergencyOnly &&
                        hp < (inCombat ? 88.0 : 78.0) &&
                        !hasAuraFromHealer("Renew") &&
                        CanCastHeal("Renew"))
                        return ("Renew", null);
                    if (hp < 55.0 && CanCastHeal("Flash Heal"))
                        return ("Flash Heal", null);
                    if (hp < 55.0 && CanCastHeal("Greater Heal"))
                        return ("Greater Heal", null);
                    if (hp < (inCombat ? 75.0 : 68.0) && CanCastHeal("Heal"))
                        return ("Heal", null);
                    if (hp < (inCombat ? 75.0 : 68.0) && CanCastHeal("Greater Heal"))
                        return ("Greater Heal", null);
                    if (hp < (inCombat ? 82.0 : 75.0) && CanCastHeal("Lesser Heal"))
                        return ("Lesser Heal", null);
                    break;

                case WoWClass.Paladin:
                    if (lowMana)
                    {
                        if (hp < 35.0 && CanCastHeal("Holy Shock"))
                            return ("Holy Shock", null);
                        if (hp < (inCombat ? 70.0 : 55.0) && CanCastHeal("Flash of Light"))
                            return ("Flash of Light", null);
                        break;
                    }
                    if (!emergencyOnly && inCombat && isTank && !hasAuraFromHealer("Beacon of Light") &&
                        CanCastHeal("Beacon of Light"))
                        return ("Beacon of Light", null);
                    if (!emergencyOnly && inCombat && isTank && !hasAuraFromHealer("Sacred Shield") &&
                        CanCastHeal("Sacred Shield"))
                        return ("Sacred Shield", null);
                    if (hp < (inCombat ? 50.0 : 40.0) && CanCastHeal("Holy Shock"))
                        return ("Holy Shock", null);
                    if (hp < (inCombat ? 78.0 : 60.0) && CanCastHeal("Flash of Light"))
                        return ("Flash of Light", null);
                    if (hp < (inCombat ? 55.0 : 45.0) && CanCastHeal("Holy Light"))
                        return ("Holy Light", null);
                    break;

                case WoWClass.Druid:
                    if (lowMana)
                    {
                        if (hp < (inCombat ? 70.0 : 60.0) && !hasAuraFromHealer("Rejuvenation") && CanCastHeal("Rejuvenation"))
                            return ("Rejuvenation", null);
                        if (hp < (inCombat ? 60.0 : 50.0) && CanCastHeal("Nourish"))
                            return ("Nourish", null);
                        if (hp < (inCombat ? 60.0 : 50.0) && CanCastHeal("Healing Touch"))
                            return ("Healing Touch", null);
                        break;
                    }
                    if (!emergencyOnly && injured78Count >= 2 && CanCastHeal("Wild Growth"))
                        return ("Wild Growth", null);
                    if (!emergencyOnly && inCombat && isTank && hp < 92.0 && !hasAuraFromHealer("Lifebloom") &&
                        CanCastHeal("Lifebloom"))
                        return ("Lifebloom", null);
                    if (!emergencyOnly && hp < (inCombat ? 88.0 : 78.0) && !hasAuraFromHealer("Rejuvenation") &&
                        CanCastHeal("Rejuvenation"))
                        return ("Rejuvenation", null);
                    if (hp < (inCombat ? 72.0 : 65.0) && !hasAuraFromHealer("Regrowth") &&
                        CanCastHeal("Regrowth"))
                        return ("Regrowth", null);
                    if (hp < (inCombat ? 75.0 : 68.0) && CanCastHeal("Nourish"))
                        return ("Nourish", null);
                    if (hp < (inCombat ? 72.0 : 65.0) && CanCastHeal("Healing Touch"))
                        return ("Healing Touch", null);
                    break;

                case WoWClass.Shaman:
                    if (lowMana)
                    {
                        if (hp < 35.0 && CanCastHeal("Riptide"))
                            return ("Riptide", null);
                        if (hp < (inCombat ? 68.0 : 58.0) && CanCastHeal("Lesser Healing Wave"))
                            return ("Lesser Healing Wave", null);
                        if (hp < (inCombat ? 58.0 : 48.0) && CanCastHeal("Healing Wave"))
                            return ("Healing Wave", null);
                        break;
                    }
                    if (!emergencyOnly && (inCombat || hp < 85.0) && isTank && !hasAuraFromHealer("Earth Shield") &&
                        CanCastHeal("Earth Shield"))
                        return ("Earth Shield", null);
                    if (!emergencyOnly && injured82Count >= 2 && CanCastHeal("Chain Heal"))
                        return ("Chain Heal", null);
                    if (hp < (inCombat ? 86.0 : 78.0) && CanCastHeal("Riptide"))
                        return ("Riptide", null);
                    if (hp < (inCombat ? 78.0 : 72.0) && CanCastHeal("Lesser Healing Wave"))
                        return ("Lesser Healing Wave", null);
                    if (hp < (inCombat ? 70.0 : 65.0) && CanCastHeal("Healing Wave"))
                        return ("Healing Wave", null);
                    break;
            }

            return (null, blockedSpell);
        }

        private static (string? SpellName, string? BlockedSpell) SelectHealSpellForClass(
            LocalPlayer me,
            WoWUnit target,
            int injured78Count,
            int injured82Count,
            bool emergencyOnly,
            bool inCombat,
            bool checkRange = true)
        {
            bool isTank = SquadContext.IsMemberRole(target.Guid, SquadRole.TankLeader);
            return EvaluateClassHealSpell(
                healerClass: me.Class,
                hp: target.HealthPercent,
                isTank: isTank,
                inCombat: inCombat,
                emergencyOnly: emergencyOnly,
                injured78Count: injured78Count,
                injured82Count: injured82Count,
                hasWeakenedSoul: target.HasAura("Weakened Soul") || target.HasAura("Power Word: Shield"),
                hasAuraFromHealer: aura => HasMyAura(me.Guid, target, aura),
                hasSpell: SpellManager.HasSpell,
                canCastSpell: spell => SpellManager.CanCast(spell, target, checkRange, false),
                healerManaPercent: ReadManaPercent(me));
        }

        private static bool HasMyAura(ulong myGuid, WoWUnit unit, string auraName)
        {
            foreach (WoWAura aura in unit.GetAllAuras())
            {
                if (aura.CreatorGuid == myGuid &&
                    string.Equals(aura.Name, auraName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
