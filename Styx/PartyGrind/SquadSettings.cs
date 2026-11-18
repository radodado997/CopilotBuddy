using System;
using System.Collections.Generic;
using System.ComponentModel;
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
            return Path.Combine(Logging.ApplicationPath, "Settings", $"SquadSettings_{charName}.xml");
        }

        #region 1. Identity & IPC

        [Setting]
        [DefaultValue("Squad1")]
        public string TeamId { get; set; } = "Squad1";

        [Setting]
        [DefaultValue(1339)]
        public int IpcPort { get; set; } = 1339;

        [Setting]
        [DefaultValue(SquadRole.DpsMelee)]
        public SquadRole Role { get; set; } = SquadRole.DpsMelee;

        [Setting]
        [DefaultValue(false)]
        public bool IsCoordinator { get; set; } = false;

        [Setting]
        [DefaultValue("")]
        public string CoordinatorName { get; set; } = "";

        [Setting]
        [DefaultValue("")]
        public string ExpectedMemberNamesCsv { get; set; } = "";

        #endregion

        #region 2. Party & Loot

        [Setting]
        [DefaultValue(true)]
        public bool AutoInviteMembers { get; set; } = true;

        [Setting]
        [DefaultValue(10)]
        public int InviteIntervalSeconds { get; set; } = 10;

        [Setting]
        [DefaultValue(true)]
        public bool AutoAcceptCoordinatorInvite { get; set; } = true;

        [Setting]
        [DefaultValue(true)]
        public bool StrictInviteWhitelist { get; set; } = true;

        [Setting]
        [DefaultValue(SquadLootMethod.FreeForAll)]
        public SquadLootMethod LootMethod { get; set; } = SquadLootMethod.FreeForAll;

        [Setting]
        [DefaultValue(SquadRollRule.Greed)]
        public SquadRollRule RollRule { get; set; } = SquadRollRule.Greed;

        [Setting]
        [DefaultValue(6)]
        public int MaxLootWindowSeconds { get; set; } = 6;

        #endregion

        #region 3. Level Sync & Profiles

        [Setting]
        [DefaultValue(SquadLevelAdvanceMode.WaitForLowestMember)]
        public SquadLevelAdvanceMode LevelAdvanceMode { get; set; } = SquadLevelAdvanceMode.WaitForLowestMember;

        [Setting]
        [DefaultValue(120)]
        public int DisconnectGraceSeconds { get; set; } = 120;

        [Setting]
        [DefaultValue(true)]
        public bool AutoLoadCoordinatorProfile { get; set; } = true;

        #endregion

        #region 4. Distances, Formation & Pace

        [Setting]
        [DefaultValue(35f)]
        public float SoftWaitDistance { get; set; } = 35f;

        [Setting]
        [DefaultValue(20f)]
        public float ResumeDistance { get; set; } = 20f;

        [Setting]
        [DefaultValue(65f)]
        public float HardBacktrackDistance { get; set; } = 65f;

        [Setting]
        [DefaultValue(28f)]
        public float MaxPullGatherDistance { get; set; } = 28f;

        [Setting]
        [DefaultValue(21f)]
        public float RangedStandoffDistance { get; set; } = 21f;

        [Setting]
        [DefaultValue(22f)]
        public float HealerStandoffDistance { get; set; } = 22f;

        [Setting]
        [DefaultValue(3.2f)]
        public float MeleeFlankRadius { get; set; } = 3.2f;

        [Setting]
        [DefaultValue(SquadMountPolicy.SyncWithSlowestMember)]
        public SquadMountPolicy MountPolicy { get; set; } = SquadMountPolicy.SyncWithSlowestMember;

        #endregion

        #region 5. Rest

        [Setting]
        [DefaultValue(50)]
        public int TriggerRestHealthPct { get; set; } = 50;

        [Setting]
        [DefaultValue(35)]
        public int TriggerRestManaPct { get; set; } = 35;

        [Setting]
        [DefaultValue(85)]
        public int ResumeRestHealthPct { get; set; } = 85;

        [Setting]
        [DefaultValue(80)]
        public int ResumeRestManaPct { get; set; } = 80;

        #endregion

        #region 6. Combat & Roles

        [Setting]
        [DefaultValue(700)]
        public int DpsEngageDelayMs { get; set; } = 700;

        [Setting]
        [DefaultValue(70)]
        public int HealerDpsAboveManaPct { get; set; } = 70;

        [Setting]
        [DefaultValue(true)]
        public bool HealerAssistDps { get; set; } = true;

        [Setting]
        [DefaultValue(true)]
        public bool UseEmergencySelfHealBeforeParty { get; set; } = true;

        [Setting]
        [DefaultValue(true)]
        public bool ForceTankDefensiveStanceOrAura { get; set; } = true;

        #endregion

        #region 7. Town & Death

        [Setting]
        [DefaultValue(true)]
        public bool SyncTownRuns { get; set; } = true;

        [Setting]
        [DefaultValue(2)]
        public int TriggerTownFreeBagSlots { get; set; } = 2;

        [Setting]
        [DefaultValue(20)]
        public int TriggerTownDurabilityPct { get; set; } = 20;

        [Setting]
        [DefaultValue(true)]
        public bool TrainSkillsInGroupTownRun { get; set; } = true;

        [Setting]
        [DefaultValue(45)]
        public int WaitForPartyRessSeconds { get; set; } = 45;

        #endregion

        #region 8. Provisioning & Party Buffs

        [Setting]
        [DefaultValue(true)]
        public bool EnableMageProvisioning { get; set; } = true;

        [Setting]
        [DefaultValue(true)]
        public bool PreferMageFoodOverVendor { get; set; } = true;

        [Setting]
        [DefaultValue(10)]
        public int MinMemberFoodStack { get; set; } = 10;

        [Setting]
        [DefaultValue(20)]
        public int TargetMemberFoodStack { get; set; } = 20;

        [Setting]
        [DefaultValue(10)]
        public int MinMemberDrinkStack { get; set; } = 10;

        [Setting]
        [DefaultValue(20)]
        public int TargetMemberDrinkStack { get; set; } = 20;

        [Setting]
        [DefaultValue(12)]
        public int TradeOfferTimeoutSeconds { get; set; } = 12;

        [Setting]
        [DefaultValue(30)]
        public int ProvisionCooldownSeconds { get; set; } = 30;

        [Setting]
        [DefaultValue(true)]
        public bool EnableWarlockHealthstones { get; set; } = true;

        [Setting]
        [DefaultValue(true)]
        public bool UseRitualTablesWhenAvailable { get; set; } = true;

        [Setting]
        [DefaultValue(true)]
        public bool EnableCrossPartyBuffs { get; set; } = true;

        [Setting]
        [DefaultValue(true)]
        public bool SetRaidTargetMarkers { get; set; } = true;

        #endregion

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

        /// <summary>
        /// Applies -squad* CLI arguments from <see cref="CommandLine"/> onto this settings instance.
        /// Returns true if any override was applied.
        /// </summary>
        public bool ApplyCliOverrides()
        {
            return ApplyCliOverrides(
                CommandLine.SquadRole,
                CommandLine.SquadCoordinator,
                CommandLine.SquadTeam,
                CommandLine.SquadPort,
                CommandLine.SquadRoster,
                CommandLine.SquadLeader);
        }

        /// <summary>
        /// Applies explicit CLI override values onto this settings instance.
        /// Returns true if any setting changed or was specified.
        /// </summary>
        public bool ApplyCliOverrides(
            string? squadRole,
            bool? squadCoordinator,
            string? squadTeam,
            int? squadPort,
            string? squadRoster,
            string? squadLeader)
        {
            bool any = false;

            if (!string.IsNullOrWhiteSpace(squadRole) && Enum.TryParse<SquadRole>(squadRole.Trim(), true, out SquadRole parsedRole))
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

            return any;
        }
    }
}
