using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Styx;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;
using Styx.Logic.Pathing;
using Styx.Helpers;
using Bots.DungeonBuddy.Attributes;
using Bots.DungeonBuddy.Enums;
using Bots.DungeonBuddy.Profiles;
using TreeSharp;

namespace Bots.DungeonBuddy
{
    /// <summary>
    /// Gère les boss du donjon actuel.
    /// Track les boss tués et le boss actuel à target.
    /// </summary>
    public static class BossManager
    {
        private static readonly HashSet<uint> _killedBossIds = new();
        private static readonly List<Boss> _bosses = new();
        public static readonly Stopwatch BossTimer = new Stopwatch();

        /// <summary>
        /// Fired after a boss is marked as dead. Payload is the killed boss.
        /// </summary>
        public static event Action<Boss> OnBossKill;

        public class Boss
        {
            public uint Entry { get; set; }
            public uint EntryId { get { return Entry; } set { Entry = value; } }
            public string Name { get; set; }
            public bool IsOptional { get; set; }
            public bool Optional { get { return IsOptional; } set { IsOptional = value; } }
            public bool IsFinal { get; set; }
            public BossAvailableToFaction FinalFaction { get; set; } = BossAvailableToFaction.Both;
            public BossAvailableToFaction Faction { get; set; } = BossAvailableToFaction.Both;
            public bool IsDead { get; set; }
            public bool IsAlive => !IsDead;
            public int CastingSpellId { get; set; }
            public CircularQueue<WoWPoint> PathBreadCrumbs { get; set; } = new CircularQueue<WoWPoint>();

            public WoWUnit ToWoWUnit()
            {
                return ObjectManager.GetObjectsOfType<WoWUnit>().FirstOrDefault(u => u.Entry == this.Entry);
            }

            /// <summary>
            /// Best-effort world location: uses live ObjectManager unit if in range,
            /// otherwise WoWPoint.Zero. Callers should guard for Zero.
            /// </summary>
            public WoWPoint Location => ToWoWUnit()?.Location ?? WoWPoint.Zero;

            /// <summary>
            /// Marks this boss as dead. Called by dungeon scripts (HB Boss.MarkAsDead() parity).
            /// </summary>
            public void MarkAsDead() { IsDead = true; }

            /// <summary>
            /// Resets this boss to alive. Called by BossManager.Reset().
            /// </summary>
            public void Reset() { IsDead = false; }
        }

        /// <summary>
        /// Uses <see cref="Styx.PartyGrind.SquadContext.ActiveKillOptionalBosses"/>
        /// in squad dungeon mode and <c>DungeonBuddySettings.Instance.KillOptionalBosses</c> otherwise.
        /// </summary>
        public static bool KillOptionalBossesEnabled =>
            Styx.PartyGrind.SquadContext.IsDungeonModeActive
                ? Styx.PartyGrind.SquadContext.ActiveKillOptionalBosses
                : DungeonBuddySettings.Instance.KillOptionalBosses;

        /// <summary>
        /// HB 4.3.4 parity: returns the first boss not marked dead, subject to the optional boss policy,
        /// in registration order — the profile's KillOrder, then any script-only bosses.
        /// Does NOT require the boss to be in ObjectManager range — using ObjectManager
        /// here would return null when bosses are out of draw distance at the dungeon
        /// entrance, causing IsComplete = true and premature 'dungeon complete'.
        /// Matches HB smethod_9: IsAlive check + Optional/KillOptionalBosses guard.
        /// </summary>
        public static Boss? CurrentBoss => SelectCurrentBoss(KillOptionalBossesEnabled, TryGetLocalIsHorde());

        internal static Boss? SelectCurrentBoss(bool killOptionalBosses, bool? isHorde)
        {
            if (IsFinalBossDeadForFaction(isHorde))
                return null;
            return _bosses
                .Where(b => !b.IsDead &&
                            IsBossAvailableToFaction(b, isHorde) &&
                            (!b.IsOptional || killOptionalBosses))
                .FirstOrDefault();
        }

        internal static bool IsBossAvailableToFaction(Boss boss, bool? isHorde)
        {
            if (boss.Faction == BossAvailableToFaction.Both || !isHorde.HasValue)
                return true;
            return isHorde.Value
                ? boss.Faction == BossAvailableToFaction.Horde
                : boss.Faction == BossAvailableToFaction.Alliance;
        }

        private static bool? TryGetLocalIsHorde()
        {
            try
            {
                return StyxWoW.IsInGame ? StyxWoW.Me?.IsHorde : null;
            }
            catch
            {
                return null;
            }
        }

        internal static bool IsBossFinalForFaction(Boss boss, bool? isHorde)
        {
            if (!boss.IsFinal || !IsBossAvailableToFaction(boss, isHorde))
                return false;
            if (boss.FinalFaction == BossAvailableToFaction.Both || !isHorde.HasValue)
                return true;
            return isHorde.Value
                ? boss.FinalFaction == BossAvailableToFaction.Horde
                : boss.FinalFaction == BossAvailableToFaction.Alliance;
        }

        internal static bool IsFinalBossDeadForFaction(bool? isHorde) =>
            _bosses.Any(b => b.IsDead && IsBossFinalForFaction(b, isHorde));

        public static bool IsFinalBossDead => IsFinalBossDeadForFaction(TryGetLocalIsHorde());

        /// <summary>
        /// Liste de tous les boss du donjon
        /// </summary>
        public static IReadOnlyList<Boss> Bosses => _bosses;
        public static IReadOnlyList<Boss> BossEncounters => _bosses;

        /// <summary>
        /// Initialise les boss pour le donjon actuel.
        /// Remet à zéro les boss tués et re-peuple la liste depuis le profil XML, puis les attributs [EncounterHandler].
        /// </summary>
        public static void Initialize(Dungeon dungeon)
        {
            if (dungeon == null)
            {
                _killedBossIds.Clear();
                _bosses.Clear();
                return;
            }

            PopulateFromProfileAndType(ProfileManager.CurrentProfile, dungeon.GetType());
        }

        internal static void PopulateFromProfileAndType(Profile? profile, Type? dungeonType)
        {
            _killedBossIds.Clear();
            _bosses.Clear();

            var profileBossEntries = new HashSet<uint>();
            if (profile != null)
            {
                var ordered = profile.BossEncounters.OrderBy(b => b.KillOrder).ToList();
                bool hasExplicitAllianceFinal = ordered.Any(b => b.IsFinal && b.Faction != BossAvailableToFaction.Horde);
                bool hasExplicitHordeFinal = ordered.Any(b => b.IsFinal && b.Faction != BossAvailableToFaction.Alliance);
                uint? inferredAllianceFinal = !hasExplicitAllianceFinal
                    ? (ordered.LastOrDefault(b => !b.Optional && b.Faction != BossAvailableToFaction.Horde)?.Entry ??
                       ordered.LastOrDefault(b => b.Faction != BossAvailableToFaction.Horde)?.Entry)
                    : null;
                uint? inferredHordeFinal = !hasExplicitHordeFinal
                    ? (ordered.LastOrDefault(b => !b.Optional && b.Faction != BossAvailableToFaction.Alliance)?.Entry ??
                       ordered.LastOrDefault(b => b.Faction != BossAvailableToFaction.Alliance)?.Entry)
                    : null;

                foreach (var profileBoss in ordered)
                {
                    profileBossEntries.Add(profileBoss.Entry);
                    bool isAllianceFinal = inferredAllianceFinal.HasValue && profileBoss.Entry == inferredAllianceFinal.Value;
                    bool isHordeFinal = inferredHordeFinal.HasValue && profileBoss.Entry == inferredHordeFinal.Value;
                    bool isFinal = profileBoss.IsFinal || isAllianceFinal || isHordeFinal;
                    BossAvailableToFaction finalFaction = profileBoss.IsFinal
                        ? profileBoss.Faction
                        : (isAllianceFinal && isHordeFinal
                            ? BossAvailableToFaction.Both
                            : (isAllianceFinal
                                ? BossAvailableToFaction.Alliance
                                : (isHordeFinal ? BossAvailableToFaction.Horde : BossAvailableToFaction.Both)));
                    RegisterBoss(profileBoss.Entry, profileBoss.Name, profileBoss.Optional, isFinal, profileBoss.Faction, finalFaction);
                }
            }

            if (dungeonType != null)
            {
                foreach (var method in dungeonType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    foreach (EncounterHandlerAttribute attr in method.GetCustomAttributes(typeof(EncounterHandlerAttribute), false))
                    {
                        if (attr.BossEntry != 0)
                        {
                            uint entry = (uint)attr.BossEntry;
                            bool isScriptOnly = profileBossEntries.Count > 0 && !profileBossEntries.Contains(entry);
                            RegisterBoss(entry, attr.BossName, isOptional: isScriptOnly);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Marque un boss comme tué et fire l'événement OnBossKill.
        /// Also syncs Profile.Boss.MarkAsDead() so PathBreadCrumbs advances to the next boss.
        /// </summary>
        public static IReadOnlyCollection<uint> KilledBossIds => _killedBossIds;

        public static void RestoreKilledBosses(IEnumerable<uint> killedBossIds)
        {
            if (killedBossIds == null)
                return;

            foreach (uint entryId in killedBossIds)
            {
                _killedBossIds.Add(entryId);
                var boss = _bosses.FirstOrDefault(b => b.EntryId == entryId);
                if (boss != null)
                {
                    boss.IsDead = true;
                }
                ProfileManager.CurrentProfile?.BossEncounters
                    .FirstOrDefault(b => b.Entry == entryId)
                    ?.MarkAsDead();
            }
        }

        public static void MarkBossDead(uint entryId)
        {
            _killedBossIds.Add(entryId);

            var boss = _bosses.FirstOrDefault(b => b.EntryId == entryId);
            if (boss != null && !boss.IsDead)
            {
                boss.IsDead = true;
                BossTimer.Reset();
                OnBossKill?.Invoke(boss);
            }

            // Sync to Profile.Boss so GetSoloFarmMoveToPoint advances to the next boss.
            ProfileManager.CurrentProfile?.BossEncounters
                .FirstOrDefault(b => b.Entry == entryId)
                ?.MarkAsDead();
        }

        /// <summary>
        /// HB 4.3.4 parity: poll one dead boss candidate per tick and mark it dead.
        /// Used as a dedicated root branch (DungeonBot.method_12 array[2]).
        /// </summary>
        public static Composite CreateCheckForDeadBossBehavior()
        {
            return new PrioritySelector(
                new ContextChangeHandler(ctx => _bosses
                    .Where(b => !b.IsDead)
                    .Select(b => b.ToWoWUnit())
                    .FirstOrDefault(u => u != null && u.IsDead)),
                new Decorator(
                    ctx => ctx is WoWUnit,
                    new global::TreeSharp.Action(ctx =>
                    {
                        var bossUnit = ctx as WoWUnit;
                        if (bossUnit != null)
                        {
                            MarkBossDead(bossUnit.Entry);
                        }

                        return RunStatus.Success;
                    })
                )
            );
        }

        /// <summary>
        /// Register un boss (appelé par DungeonManager lors du chargement des scripts)
        /// </summary>
        public static void RegisterBoss(
            uint entryId,
            string name,
            bool isOptional = false,
            bool isFinal = false,
            BossAvailableToFaction faction = BossAvailableToFaction.Both,
            BossAvailableToFaction finalFaction = BossAvailableToFaction.Both)
        {
            var existing = _bosses.FirstOrDefault(b => b.EntryId == entryId);
            if (existing == null)
            {
                _bosses.Add(new Boss
                {
                    EntryId = entryId,
                    Name = name,
                    IsOptional = isOptional,
                    IsFinal = isFinal,
                    FinalFaction = finalFaction,
                    Faction = faction,
                    IsDead = false
                });
            }
            else
            {
                if (isFinal)
                {
                    if (!existing.IsFinal)
                        existing.FinalFaction = finalFaction;
                    else if (existing.FinalFaction != finalFaction)
                        existing.FinalFaction = BossAvailableToFaction.Both;
                    existing.IsFinal = true;
                }
                if (faction != BossAvailableToFaction.Both)
                    existing.Faction = faction;
            }
        }

        /// <summary>
        /// Marque un boss comme tué (par nom, utilisé par l'UI)
        /// </summary>
        public static void MarkBossDead(string name)
        {
            var boss = _bosses.FirstOrDefault(b => b.Name == name);
            if (boss != null)
            {
                boss.IsDead = true;
                _killedBossIds.Add(boss.EntryId);
            }
        }

        /// <summary>
        /// Remet un boss à vivant (par nom, utilisé par l'UI)
        /// </summary>
        public static void ResetBoss(string name)
        {
            var boss = _bosses.FirstOrDefault(b => b.Name == name);
            if (boss != null)
            {
                boss.IsDead = false;
                _killedBossIds.Remove(boss.EntryId);
            }
        }

        /// <summary>
        /// Vérifie si tous les boss obligatoires sont morts
        /// </summary>
        public static bool AreAllRequiredBossesDead()
        {
            return _bosses.Where(b => !b.IsOptional).All(b => b.IsDead);
        }

        /// <summary>
        /// Reset pour nouveau donjon
        /// </summary>
        public static void Reset()
        {
            _killedBossIds.Clear();
            _bosses.Clear();
        }
    }
}