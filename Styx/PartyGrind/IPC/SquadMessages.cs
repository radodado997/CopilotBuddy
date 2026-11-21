using System;
using System.Collections.Generic;
using System.Text.Json;
using Styx.Combat.CombatRoutine;

namespace Styx.PartyGrind.IPC
{
    /// <summary>
    /// Shared NDJSON serialization helpers for Squad IPC.
    /// </summary>
    public static class SquadJson
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        public const int MaxFrameCharacters = 262144;

        public static async System.Threading.Tasks.Task<string?> ReadFrameAsync(
            System.IO.StreamReader reader, System.Threading.CancellationToken ct)
        {
            var buffer = new char[1];
            var line = new System.Text.StringBuilder();
            while (await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false) != 0)
            {
                if (buffer[0] == '\n') return line.ToString().TrimEnd('\r');
                if (line.Length >= MaxFrameCharacters)
                    throw new System.IO.IOException("IPC frame exceeds size limit.");
                line.Append(buffer[0]);
            }
            return line.Length == 0 ? null : line.ToString();
        }

        public static string SerializeEnvelope<TPayload>(string messageType, string teamId, long sequenceId, TPayload payload)
        {
            var envelope = new SquadEnvelope
            {
                MessageType = messageType,
                TeamId = teamId ?? string.Empty,
                SequenceId = sequenceId,
                TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                PayloadJson = JsonSerializer.Serialize(payload, Options)
            };
            return JsonSerializer.Serialize(envelope, Options);
        }

        public static SquadEnvelope? DeserializeEnvelope(string jsonLine)
        {
            if (string.IsNullOrWhiteSpace(jsonLine))
                return null;
            return JsonSerializer.Deserialize<SquadEnvelope>(jsonLine, Options);
        }

        public static TPayload? DeserializePayload<TPayload>(SquadEnvelope envelope)
        {
            if (string.IsNullOrWhiteSpace(envelope.PayloadJson))
                return default;
            var result = JsonSerializer.Deserialize<TPayload>(envelope.PayloadJson, Options);
            if (result is SquadLeaderState leader)
            {
                if (leader.MemberAssignments != null)
                {
                    var assignments = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in leader.MemberAssignments) assignments[kv.Key] = kv.Value;
                    leader.MemberAssignments = assignments;
                }
                if (leader.MemberRoles != null)
                {
                    var roles = new Dictionary<string, SquadRole>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in leader.MemberRoles) roles[kv.Key] = kv.Value;
                    leader.MemberRoles = roles;
                }
            }
            return result;
        }
    }

    public static class SquadMessageTypes
    {
        public const string Handshake = "Handshake";
        public const string MemberState = "MemberState";
        public const string LeaderState = "LeaderState";
    }

    public class SquadEnvelope
    {
        public string MessageType { get; set; } = string.Empty;
        public string TeamId { get; set; } = string.Empty;
        public long SequenceId { get; set; }
        public long TimestampUtcMs { get; set; }
        public string PayloadJson { get; set; } = string.Empty;
    }

    public class SquadHandshake
    {
        public string CharacterName { get; set; } = string.Empty;
        public ulong Guid { get; set; }
        public WoWClass Class { get; set; }
        public SquadRole Role { get; set; }
        public int Level { get; set; }
        public int ProtocolVersion { get; set; } = 1;
    }

    /// <summary>
    /// Normalized "idę do miasta" announcement. A member raises it through
    /// <see cref="SquadMemberState.TownRunRequested"/>; the coordinator consumes it and
    /// rebroadcasts it in <see cref="SquadLeaderState.TownRunRequestedByName"/> so every
    /// squadmate receives the signal and can do its own sell/repair/buy/mail/train.
    /// </summary>
    public class SquadTownRunRequest
    {
        public string CharacterName { get; set; } = string.Empty;
        public ulong Guid { get; set; }
        public SquadRole Role { get; set; }
        /// <summary>Comma-separated reasons: Sell, Repair, Buy, Train, Mail.</summary>
        public string Reason { get; set; } = string.Empty;
        public long RequestedUtcMs { get; set; }
    }

    public class SquadBreadcrumb
    {
        public uint MapId { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public long TimestampUtcMs { get; set; }
        public int RouteGeneration { get; set; }
        public long Sequence { get; set; }
    }

    /// <summary>
    /// Compact member status replicated inside <see cref="SquadLeaderState"/> so every
    /// client (overlay, Mage provisioner, Warlock stone caster, Hybrid off-healer, Tank)
    /// knows the entire 3–5 client squad without peer-to-peer sockets.
    /// </summary>
    public class SquadMemberSummary
    {
        public bool HasTelemetry { get; set; } = true;
        public List<string> KnownBlessings { get; set; } = new();
        public string CharacterName { get; set; } = string.Empty;
        public ulong Guid { get; set; }
        public SquadRole Role { get; set; }
        public WoWClass Class { get; set; }
        public int Level { get; set; }
        public float XpPercent { get; set; }
        public float XpPerHour { get; set; }
        public float HealthPercent { get; set; }
        public float PowerPercent { get; set; }
        public WoWPowerType PowerType { get; set; }
        public uint MapId { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float DistanceToLeader { get; set; }
        public bool IsDead { get; set; }
        public bool IsGhost { get; set; }
        public bool IsInCombat { get; set; }
        public bool NeedsRest { get; set; }
        public bool IsResting { get; set; }
        public bool IsLooting { get; set; }
        public bool NeedsTown { get; set; }
        public bool TownTasksComplete { get; set; }
        public bool CanMount { get; set; }
        public bool CanFly { get; set; }
        public bool IsMounted { get; set; }
        public int FreeBagSlots { get; set; }
        public bool WantsFood { get; set; }
        public bool WantsDrink { get; set; }
        public bool WantsHealthstone { get; set; }
        public bool CanProvideMageFood { get; set; }
        public bool CanProvideMageDrink { get; set; }
        public bool CanProvideHealthstone { get; set; }
        public bool IsProvisioningBusy { get; set; }
        public bool IsConnected { get; set; }
        public string StateLabel { get; set; } = string.Empty;
    }

    public class SquadLeaderState
    {
        // Identity & Phase
        public ulong LeaderGuid { get; set; }
        public string LeaderName { get; set; } = string.Empty;
        public SquadPhase Phase { get; set; } = SquadPhase.Initializing;
        public string PauseReason { get; set; } = string.Empty;

        // Profile & Level-Gating
        public string ProfileXmlPath { get; set; } = string.Empty;
        public string ProfileXmlHash { get; set; } = string.Empty;
        public string ActiveSubProfileName { get; set; } = string.Empty;
        public int ActiveSubProfileMinLevel { get; set; } = 1;
        public int ActiveSubProfileMaxLevel { get; set; } = 85;
        /// <summary>Highest MaxLevel among the loaded profile's sub-profiles; 0 when unknown.</summary>
        public int ProfileMaxLevel { get; set; }
        public int EffectiveSquadMinLevel { get; set; } = 1;
        public string LowestMemberName { get; set; } = string.Empty;
        public float LowestMemberXpPercent { get; set; }
        /// <summary>True when the lowest squad member passed the loaded profile's top level range.</summary>
        public bool LevelCapReached { get; set; }

        // Position, Combat & Focus Target
        public uint MapId { get; set; }
        public uint ZoneId { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Facing { get; set; }
        public bool IsMounted { get; set; }
        public bool IsFlying { get; set; }
        public bool InCombat { get; set; }
        /// <summary>Authoritative bounded loot-window state from the coordinator.</summary>
        public bool IsLootWindowActive { get; set; }
        public bool IsDead { get; set; }
        public bool IsGhost { get; set; }
        public ulong FocusTargetGuid { get; set; }
        public ulong SecondaryTargetGuid { get; set; }
        public long PullStartedUtcMs { get; set; }
        public float ActiveHotspotX { get; set; }
        public float ActiveHotspotY { get; set; }
        public float ActiveHotspotZ { get; set; }
        public List<SquadBreadcrumb> Breadcrumbs { get; set; } = new List<SquadBreadcrumb>();

        // TownRun Rally
        public float TownRallyX { get; set; }
        public float TownRallyY { get; set; }
        public float TownRallyZ { get; set; }
        public uint? TownRallyMapId { get; set; }
        public bool AllTownTasksComplete { get; set; }
        /// <summary>True while combat/death recovery temporarily takes priority over an active town run.</summary>
        public bool TownRunInterrupted { get; set; }
        /// <summary>Last town-run announcement rebroadcast to the squad (empty when none).</summary>
        public string TownRunRequestedByName { get; set; } = string.Empty;
        public string TownRunRequestReason { get; set; } = string.Empty;
        public long TownRunRequestedUtcMs { get; set; }
        public Dictionary<string, long> TownRunRequestAcknowledgements { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // Wipe / Corpse Run
        public float SquadCorpseRallyX { get; set; }
        public float SquadCorpseRallyY { get; set; }
        public float SquadCorpseRallyZ { get; set; }
        public bool ReleaseCorpseOrder { get; set; }

        // Provisioning / Trade Arbitration
        public string ActiveProvisionerName { get; set; } = string.Empty;
        public string ActiveTradeTargetName { get; set; } = string.Empty;

        // Slot Assignments & Full Roster Snapshot
        public Dictionary<string, int> MemberAssignments { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, SquadRole> MemberRoles { get; set; } = new Dictionary<string, SquadRole>(StringComparer.OrdinalIgnoreCase);
        public List<SquadMemberSummary> Members { get; set; } = new List<SquadMemberSummary>();
    }

    public class SquadMemberState
    {
        public List<string> KnownBlessings { get; set; } = new();
        public bool CanResurrect { get; set; }
        public string TownBlockedReason { get; set; } = string.Empty;
        // Identity & Progress
        public string CharacterName { get; set; } = string.Empty;
        public ulong Guid { get; set; }
        public SquadRole Role { get; set; }
        public WoWClass Class { get; set; }
        public int Level { get; set; }
        public uint CurrentXp { get; set; }
        public uint NextLevelXp { get; set; }
        public float XpPerHour { get; set; }

        // Spatial & Health
        public uint MapId { get; set; }
        public uint ZoneId { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float HealthPercent { get; set; }
        public float PowerPercent { get; set; }
        public WoWPowerType PowerType { get; set; }
        public bool IsDead { get; set; }
        public bool IsGhost { get; set; }
        public uint CorpseMapId { get; set; }
        public float CorpseX { get; set; }
        public float CorpseY { get; set; }
        public float CorpseZ { get; set; }

        // Combat & Status Flags
        public bool IsInCombat { get; set; }
        public ulong AggroAttackerGuid { get; set; }
        public bool NeedsPeel { get; set; }
        public bool NeedsRest { get; set; }
        public bool IsResting { get; set; }
        public bool IsLooting { get; set; }

        // Movement & Mount
        public bool CanMount { get; set; }
        public bool CanFly { get; set; }
        public float MaxMountSpeed { get; set; }
        public bool IsMounted { get; set; }

        // Provisioning
        public int FoodCount { get; set; }
        public int DrinkCount { get; set; }
        public bool HasHealthstone { get; set; }
        public bool WantsFood { get; set; }
        public bool WantsDrink { get; set; }
        public bool WantsHealthstone { get; set; }
        public bool IsProvisioningBusy { get; set; }
        public bool CanProvideMageFood { get; set; }
        public bool CanProvideMageDrink { get; set; }
        public bool CanProvideHealthstone { get; set; }
        public int? HealthstoneMinReceiverLevel { get; set; }
        public bool IsHealthstoneRitualPending { get; set; }

        // TownRun
        public bool NeedsSell { get; set; }
        public bool NeedsRepair { get; set; }
        public bool NeedsBuy { get; set; }
        public bool NeedsTrain { get; set; }
        public bool NeedsMail { get; set; }
        public bool TownTasksComplete { get; set; }
        public int FreeBagSlots { get; set; }
        public float LowestDurabilityPct { get; set; }

        // TownRun announcement (member -> coordinator). Carried on the periodic
        // MemberState message so it cannot be dropped by the bounded outbound queue;
        // the coordinator turns it into a synchronized TownRun and rebroadcasts it.
        public bool TownRunRequested { get; set; }
        public string TownRunRequestReason { get; set; } = string.Empty;
        public long TownRunRequestedUtcMs { get; set; }

        // Profile Hash & SubProfile Verification
        public string LoadedProfileHash { get; set; } = string.Empty;
        public int ActiveSubProfileMinLevel { get; set; }
        public int ActiveSubProfileMaxLevel { get; set; }
    }
}
