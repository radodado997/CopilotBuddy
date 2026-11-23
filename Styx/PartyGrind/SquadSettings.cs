using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using Styx.Helpers;

namespace Styx.PartyGrind
{
    /// <summary>
    /// Per-character settings for PartyGrindBot.
    /// Stored in Settings/SquadSettings_{Me.Name}.xml.
    /// </summary>
    public class SquadSettings : Settings
    {
        private static SquadSettings? _instance;
        private static readonly object _instanceLock = new object();

        /// <summary>
        /// Gets the active per-character <see cref="SquadSettings"/> instance.
        /// Lazily constructs a default instance if <see cref="Initialize"/> has not yet run.
        /// </summary>
        public static SquadSettings Instance
        {
            get
            {
                lock (_instanceLock)
                {
                    if (_instance == null)
                        _instance = new SquadSettings();
                    return _instance;
                }
            }
            internal set
            {
                lock (_instanceLock)
                {
                    _instance = value;
                }
            }
        }

        /// <summary>
        /// Initializes (or reloads) <see cref="Instance"/> for the currently attached character.
        /// </summary>
        public static void Initialize()
        {
            lock (_instanceLock)
            {
                _instance = new SquadSettings();
            }
        }

        public SquadSettings()
            : this(BuildDefaultSettingsPath())
        {
        }

        internal SquadSettings(string settingsPath)
            : base(settingsPath)
        {
            // Default IsCoordinator to true when Role == TankLeader if the file did not specify <IsCoordinator>.
            bool hasExplicitCoordinator = false;
            try
            {
                if (File.Exists(settingsPath))
                {
                    XElement doc = XElement.Load(settingsPath);
                    hasExplicitCoordinator = doc.Element(nameof(IsCoordinator)) != null;
                }
            }
            catch
            {
                hasExplicitCoordinator = true;
            }

            if (!hasExplicitCoordinator && Role == SquadRole.TankLeader)
            {
                IsCoordinator = true;
            }
        }

        private static string BuildDefaultSettingsPath()
        {
            string charName = StyxWoW.Me != null ? (StyxWoW.Me.Name ?? "") : "";
            return Path.Combine(SettingsDirectory, $"SquadSettings_{charName}.xml");
        }

        #region 1. Identity & IPC

        [Setting]
        [Styx.Helpers.DefaultValueAttribute("Squad1")]
        public string TeamId { get; set; } = "Squad1";

        [Setting]
        [Styx.Helpers.DefaultValueAttribute("")]
        public string IpcMemberSecretsPath { get; set; } = string.Empty;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(1339)]
        public int IpcPort { get; set; } = 1339;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(SquadRole.DpsMelee)]
        public SquadRole Role { get; set; } = SquadRole.DpsMelee;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(false)]
        public bool IsCoordinator { get; set; } = false;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute("")]
        public string CoordinatorName { get; set; } = "";

        [Setting]
        [Styx.Helpers.DefaultValueAttribute("")]
        public string ExpectedMemberNamesCsv { get; set; } = "";

        #endregion

        #region 2. Party & Loot

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(true)]
        public bool AutoInviteMembers { get; set; } = true;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(10)]
        public int InviteIntervalSeconds { get; set; } = 10;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(true)]
        public bool AutoAcceptCoordinatorInvite { get; set; } = true;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(true)]
        public bool StrictInviteWhitelist { get; set; } = true;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(SquadLootMethod.FreeForAll)]
        public SquadLootMethod LootMethod { get; set; } = SquadLootMethod.FreeForAll;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(SquadRollRule.Greed)]
        public SquadRollRule RollRule { get; set; } = SquadRollRule.Greed;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(6)]
        public int MaxLootWindowSeconds { get; set; } = 6;

        #endregion

        #region 3. Level Sync & Profiles

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(SquadLevelAdvanceMode.WaitForLowestMember)]
        public SquadLevelAdvanceMode LevelAdvanceMode { get; set; } = SquadLevelAdvanceMode.WaitForLowestMember;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(120)]
        public int DisconnectGraceSeconds { get; set; } = 120;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(true)]
        public bool AutoLoadCoordinatorProfile { get; set; } = true;

        #endregion

        #region 4. Distances, Formation & Pace

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(35f)]
        public float SoftWaitDistance { get; set; } = 35f;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(20f)]
        public float ResumeDistance { get => _resumeDistance; set => _resumeDistance = float.IsFinite(value) ? value : 20f; }
        private float _resumeDistance = 20f;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(65f)]
        public float HardBacktrackDistance { get; set; } = 65f;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(28f)]
        public float MaxPullGatherDistance { get; set; } = 28f;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(21f)]
        public float RangedStandoffDistance { get; set; } = 21f;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(22f)]
        public float HealerStandoffDistance { get; set; } = 22f;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(3.2f)]
        public float MeleeFlankRadius { get; set; } = 3.2f;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(SquadMountPolicy.SyncWithSlowestMember)]
        public SquadMountPolicy MountPolicy { get; set; } = SquadMountPolicy.SyncWithSlowestMember;

        #endregion

        #region 5. Rest

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(50)]
        public int TriggerRestHealthPct { get; set; } = 50;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(35)]
        public int TriggerRestManaPct { get; set; } = 35;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(85)]
        public int ResumeRestHealthPct { get; set; } = 85;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(80)]
        public int ResumeRestManaPct { get; set; } = 80;

        #endregion

        #region 6. Combat & Roles

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(700)]
        public int DpsEngageDelayMs { get; set; } = 700;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(70)]
        public int HealerDpsAboveManaPct { get; set; } = 70;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(true)]
        public bool HealerAssistDps { get; set; } = true;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(true)]
        public bool UseEmergencySelfHealBeforeParty { get; set; } = true;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(true)]
        public bool ForceTankDefensiveStanceOrAura { get; set; } = true;

        #endregion

        #region 7. Town & Death

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(true)]
        public bool SyncTownRuns { get; set; } = true;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(2)]
        public int TriggerTownFreeBagSlots { get; set; } = 2;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(20)]
        public int TriggerTownDurabilityPct { get; set; } = 20;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(true)]
        public bool TrainSkillsInGroupTownRun { get; set; } = true;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(45)]
        [System.ComponentModel.Description("Seconds to wait per squad recovery period, starting with the first casualty; later casualties share that deadline.")]
        public int WaitForPartyRessSeconds { get; set; } = 45;

        #endregion

        #region 8. Provisioning & Party Buffs

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(true)]
        public bool EnableMageProvisioning { get; set; } = true;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(true)]
        public bool PreferMageFoodOverVendor { get; set; } = true;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(10)]
        public int MinMemberFoodStack { get; set; } = 10;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(20)]
        public int TargetMemberFoodStack { get; set; } = 20;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(10)]
        public int MinMemberDrinkStack { get; set; } = 10;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(20)]
        public int TargetMemberDrinkStack { get; set; } = 20;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(12)]
        public int TradeOfferTimeoutSeconds { get; set; } = 12;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(30)]
        public int ProvisionCooldownSeconds { get; set; } = 30;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(true)]
        public bool EnableWarlockHealthstones { get; set; } = true;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(true)]
        public bool UseRitualTablesWhenAvailable { get; set; } = true;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(true)]
        public bool EnableCrossPartyBuffs { get; set; } = true;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(true)]
        public bool SetRaidTargetMarkers { get; set; } = true;

        #endregion

        #region 9. Dungeon Mode

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(SquadBotMode.OpenWorldGrind)]
        public SquadBotMode BotMode { get; set; } = SquadBotMode.OpenWorldGrind;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(SquadDungeonEntryMode.LfgRandomNormal)]
        public SquadDungeonEntryMode DungeonEntryMode { get; set; } = SquadDungeonEntryMode.LfgRandomNormal;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute("")]
        public string SelectedDungeonIdsCsv { get; set; } = "";

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(false)]
        public bool KillOptionalBosses { get; set; } = false;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(SquadDungeonLootMode.BossesOnly)]
        public SquadDungeonLootMode DungeonLootMode { get; set; } = SquadDungeonLootMode.BossesOnly;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(true)]
        public bool AllowPartialSquadLfg { get; set; } = true;

        [Setting]
        [Styx.Helpers.DefaultValueAttribute(5)]
        public int MaxInstancesPerHour { get; set; } = 5;

        #endregion

        public uint[] GetSelectedDungeonIds()
        {
            if (string.IsNullOrWhiteSpace(SelectedDungeonIdsCsv))
                return Array.Empty<uint>();

            var seen = new HashSet<uint>();
            var list = new List<uint>();
            foreach (string part in SelectedDungeonIdsCsv.Split(new[] { ',', ';', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (uint.TryParse(part.Trim(), out uint id) && id > 0 && seen.Add(id))
                    list.Add(id);
            }
            return list.ToArray();
        }

        /// <summary>
        /// Parses <see cref="ExpectedMemberNamesCsv"/> into trimmed, distinct, non-empty character names.
        /// </summary>
        public string[] GetExpectedMemberNames()
        {
            if (string.IsNullOrWhiteSpace(ExpectedMemberNamesCsv))
                return Array.Empty<string>();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            foreach (string part in ExpectedMemberNamesCsv.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = part.Trim();
                if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed))
                    list.Add(trimmed);
            }
            return list.ToArray();
        }

        public bool ApplyCliOverrides()
        {
            return ApplyCliOverrides(
                CommandLine.SquadRole,
                CommandLine.SquadCoordinator,
                CommandLine.SquadTeam,
                CommandLine.SquadPort,
                CommandLine.SquadRoster,
                CommandLine.SquadLeader,
                CommandLine.SquadBotMode,
                CommandLine.SquadDungeonEntry,
                CommandLine.SquadDungeonIds);
        }

        public bool ApplyCliOverrides(
            string? squadRole,
            bool? squadCoordinator,
            string? squadTeam,
            int? squadPort,
            string? squadRoster,
            string? squadLeader,
            string? squadMode = null,
            string? dungeonEntry = null,
            string? dungeonIds = null)
        {
            bool any = false;

            if (!string.IsNullOrWhiteSpace(squadRole) && !squadRole.Contains(",") && Enum.TryParse<SquadRole>(squadRole.Trim(), true, out SquadRole parsedRole) && Enum.IsDefined(parsedRole))
            {
                Role = parsedRole;
                if (!squadCoordinator.HasValue)
                    IsCoordinator = (parsedRole == SquadRole.TankLeader);
                any = true;
            }

            if (squadCoordinator.HasValue)
            {
                IsCoordinator = squadCoordinator.Value;
                any = true;
            }

            if (!string.IsNullOrWhiteSpace(squadTeam))
            {
                TeamId = squadTeam.Trim();
                any = true;
            }

            if (squadPort.HasValue && squadPort.Value > 0 && squadPort.Value <= 65535)
            {
                IpcPort = squadPort.Value;
                any = true;
            }

            if (!string.IsNullOrWhiteSpace(squadRoster))
            {
                ExpectedMemberNamesCsv = squadRoster.Trim();
                any = true;
            }

            if (!string.IsNullOrWhiteSpace(squadLeader))
            {
                CoordinatorName = squadLeader.Trim();
                any = true;
            }

            if (!string.IsNullOrWhiteSpace(squadMode) &&
                Enum.TryParse<SquadBotMode>(squadMode.Trim(), true, out SquadBotMode parsedMode) &&
                Enum.IsDefined(parsedMode))
            {
                BotMode = parsedMode;
                any = true;
            }

            if (!string.IsNullOrWhiteSpace(dungeonEntry) &&
                Enum.TryParse<SquadDungeonEntryMode>(dungeonEntry.Trim(), true, out SquadDungeonEntryMode parsedEntry) &&
                Enum.IsDefined(parsedEntry))
            {
                DungeonEntryMode = parsedEntry;
                any = true;
            }

            if (!string.IsNullOrWhiteSpace(dungeonIds))
            {
                SelectedDungeonIdsCsv = dungeonIds.Trim();
                any = true;
            }

            return any;
        }
    }
}
