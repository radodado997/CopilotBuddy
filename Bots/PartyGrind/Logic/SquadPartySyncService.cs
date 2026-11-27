using System;
using System.Collections.Generic;
using System.Linq;
using Styx;
using Styx.Combat.CombatRoutine;
using Styx.Helpers;
using Styx.PartyGrind;
using Styx.PartyGrind.IPC;
using Styx.WoWInternals;

namespace CopilotBuddy.Bots.PartyGrind.Logic
{
    /// <summary>
    /// Handles WoW group invitations, loot method enforcement, smart group loot rolling,
    /// resurrect acceptance, shared quest acceptance, and trade event forwarding.
    /// </summary>
    public sealed class SquadPartySyncService
    {
        private readonly SquadProvisioningService _provisioning;
        private bool _eventsAttached;
        private Styx.WoWInternals.LuaEvents? _eventsDispatcher;
        private readonly List<(string Name, LuaEventHandlerDelegate Handler)> _attachedHandlers = new();
        private long _lastInviteUtcMs;
        private readonly object _invitePulseLock = new();
        private readonly SquadInviteTracker _inviteTracker = new();
        private long _lastLootMethodUtcMs;
        private long _lastUninviteUtcMs;
        private SquadDungeonResetPhase _lastResetPhase = SquadDungeonResetPhase.Idle;

        private static readonly object ActionLogLock = new();
        private static string _lastAction = string.Empty;
        private static string _lastActionCharacter = string.Empty;

        internal static long LastActionUtcMs;

        internal static void ResetActionLog()
        {
            lock (ActionLogLock)
            {
                _lastAction = _lastActionCharacter = string.Empty;
                LastActionUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
        }

        internal static void MarkActivity()
        {
            lock (ActionLogLock)
                LastActionUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        internal static void LogAction(string action)
        {
            string name = StyxWoW.Me?.Name ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name)) return;
            lock (ActionLogLock)
            {
                if (_lastAction == action && _lastActionCharacter == name) return;
                _lastAction = action;
                _lastActionCharacter = name;
                LastActionUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Logging.WriteDebug("[PartyGrind Sync {0}] {1}", name, action);
            }
        }

        public SquadPartySyncService(SquadProvisioningService provisioning)
        {
            _provisioning = provisioning ?? throw new ArgumentNullException(nameof(provisioning));
        }

        public void AttachEvents()
        {
            var dispatcher = Lua.Events;
            if (_eventsAttached && ReferenceEquals(_eventsDispatcher, dispatcher))
                return;
            if (_eventsAttached)
                DetachEvents(); // handlers belong to a dispatcher that was replaced

            var registrations = new (string Name, LuaEventHandlerDelegate Handler)[]
            {
                ("PARTY_INVITE_REQUEST", OnPartyInviteRequest),
                ("RESURRECT_REQUEST", OnResurrectRequest),
                ("START_LOOT_ROLL", OnStartLootRoll),
                ("CONFIRM_SUMMON", OnConfirmSummon),
                ("QUEST_ACCEPT_CONFIRM", OnQuestAcceptConfirm),
                ("UI_ERROR_MESSAGE", OnUiErrorMessage)
            };
            var registered = new List<(string Name, LuaEventHandlerDelegate Handler)>();
            try
            {
                foreach (var registration in registrations)
                {
                    dispatcher.AttachEvent(registration.Name, registration.Handler);
                    registered.Add(registration);
                }
            }
            catch
            {
                // Roll back every handler that was registered before the failure so a
                // retry on the same dispatcher cannot leave duplicate subscriptions.
                for (int i = registered.Count - 1; i >= 0; i--)
                    try { dispatcher.DetachEvent(registered[i].Name, registered[i].Handler); } catch { }
                throw;
            }

            _eventsDispatcher = dispatcher;
            _attachedHandlers.Clear();
            _attachedHandlers.AddRange(registered);
            _eventsAttached = true;
        }

        public void DetachEvents()
        {
            var dispatcher = _eventsDispatcher;
            if (!_eventsAttached || dispatcher == null)
                return;

            try
            {
                for (int i = _attachedHandlers.Count - 1; i >= 0; i--)
                {
                    try { dispatcher.DetachEvent(_attachedHandlers[i].Name, _attachedHandlers[i].Handler); }
                    catch (Exception ex) { Logging.WriteDebug("[PartyGrind Sync] DetachEvents warning: {0}", ex.Message); }
                }
            }
            finally
            {
                _eventsAttached = false;
                _eventsDispatcher = null;
                _attachedHandlers.Clear();
            }
        }

        /// <summary>
        /// Coordinator periodically invites missing squadmates and enforces the configured LootMethod.
        /// </summary>
        public void PulseCoordinatorPartySync(
            SquadSettings settings,
            IReadOnlyList<SquadConnectedMemberSnapshot> connected,
            SquadLeaderState? leader = null)
        {
            var me = StyxWoW.Me;
            if (me == null || !settings.IsCoordinator)
                return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            SquadDungeonResetPhase resetPhase = leader?.DungeonResetPhase ?? SquadDungeonResetPhase.Idle;
            bool suspendAutoInvite = leader?.SuspendAutoInvite == true;
            bool anyCombatOrDead = leader != null &&
                (leader.Phase == SquadPhase.Paused || leader.InCombat || leader.IsDead || leader.IsGhost ||
                 leader.Members.Any(m => m.IsConnected && (m.IsDead || m.IsGhost)));

            if (resetPhase == SquadDungeonResetPhase.Disbanding)
            {
                if (_lastResetPhase != SquadDungeonResetPhase.Disbanding)
                    _lastUninviteUtcMs = 0;
                if (!anyCombatOrDead && now - _lastUninviteUtcMs >= 1200L)
                {
                    _lastUninviteUtcMs = now;
                    UninviteAllPartyMembers(me);
                }
            }
            else
            {
                _lastUninviteUtcMs = 0;
            }

            if (resetPhase == SquadDungeonResetPhase.Reinviting &&
                _lastResetPhase != SquadDungeonResetPhase.Reinviting)
            {
                _inviteTracker.Reset();
                _lastInviteUtcMs = 0;
            }
            _lastResetPhase = resetPhase;

            // Serialize overlapping pulses as well as each player's pending-invite window.
            lock (_invitePulseLock)
            {
                bool allowInvite = !suspendAutoInvite &&
                    (settings.AutoInviteMembers || resetPhase == SquadDungeonResetPhase.Reinviting);
                long inviteIntervalMs = resetPhase == SquadDungeonResetPhase.Reinviting
                    ? 2000L
                    : Math.Max(3, settings.InviteIntervalSeconds) * 1000L;
                long trackerCooldownMs = resetPhase == SquadDungeonResetPhase.Reinviting ? 4000L : 10000L;

                if (allowInvite && (now - _lastInviteUtcMs) >= inviteIntervalMs)
                {
                    _lastInviteUtcMs = now;
                    var desiredNames = new HashSet<string>(
                        settings.GetExpectedMemberNames().Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()),
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var snap in connected)
                    {
                        if (!string.IsNullOrWhiteSpace(snap.CharacterName))
                            desiredNames.Add(snap.CharacterName.Trim());
                    }
                    desiredNames.Remove((me.Name ?? string.Empty).Trim());

                    foreach (string name in desiredNames)
                    {
                        _inviteTracker.Check(name, Environment.TickCount64, mayInvite =>
                        {
                            // Name is a valid UnitId for a party member in 3.3.5a.
                            // Check and invite in the same Lua execution so a roster update
                            // cannot occur between the membership test and InviteUnit.
                            return Lua.GetReturnVal<string>(BuildInviteScript(name, mayInvite), 0U)
                                ?? string.Empty;
                        }, trackerCooldownMs);
                    }
                }
            }

            // 2. Enforce configured party loot method (freeforall / group).
            if (resetPhase != SquadDungeonResetPhase.Disbanding &&
                (now - _lastLootMethodUtcMs) >= 15000L)
            {
                _lastLootMethodUtcMs = now;
                string method = settings.LootMethod == SquadLootMethod.FreeForAll ? "freeforall" : "group";
                Lua.DoString($"if GetPartyMember(1) and GetLootMethod() ~= '{method}' then SetLootMethod('{method}') end");
            }
        }

        internal static string BuildUninviteAllScript() =>
            "local count = 0; " +
            "if GetNumRaidMembers() > 0 then " +
                "if IsRaidLeader() then " +
                    "local meName = UnitName('player'); " +
                    "for i = GetNumRaidMembers(), 1, -1 do " +
                        "local name = GetRaidRosterInfo(i); " +
                        "if name and name ~= '' and name ~= meName then UninviteUnit(name); count = count + 1; end " +
                    "end " +
                "end " +
            "elseif GetNumPartyMembers() > 0 then " +
                "if IsPartyLeader() then " +
                    "for i = GetNumPartyMembers(), 1, -1 do " +
                        "local name = UnitName('party' .. i); " +
                        "if name and name ~= '' then UninviteUnit(name); count = count + 1; end " +
                    "end " +
                "end " +
            "end; " +
            "return count;";

        public static int UninviteAllPartyMembers(Styx.WoWInternals.WoWObjects.LocalPlayer? me)
        {
            if (me == null)
                return 0;

            int count = Lua.GetReturnVal<int>(BuildUninviteAllScript(), 0);
            if (count > 0)
            {
                Logging.Write("[PartyGrind Sync] Disbanding party after dungeon ({0} uninvite calls).", count);
                LogAction($"Disbanding party ({count})");
            }
            return count;
        }

        internal static string BuildInviteScript(string name, bool mayInvite)
        {
            string escaped = Lua.Escape(name);
            return $"if UnitInParty('{escaped}') then return 'member' end; " +
                   (mayInvite ? $"InviteUnit('{escaped}'); return 'invited'" : "return 'waiting'");
        }

        public static bool IsInviterAuthorized(string inviterName, SquadSettings settings, SquadLeaderState? leader)
        {
            if (string.IsNullOrWhiteSpace(inviterName))
                return false;

            if (!settings.StrictInviteWhitelist)
                return true;

            string trimmed = inviterName.Trim();
            if (!string.IsNullOrWhiteSpace(settings.CoordinatorName) &&
                string.Equals(trimmed, settings.CoordinatorName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return settings.GetExpectedMemberNames()
                .Any(n => string.Equals(n, trimmed, StringComparison.OrdinalIgnoreCase));
        }

        private void OnPartyInviteRequest(object sender, LuaEventArgs args)
        {
            var settings = SquadContext.ActiveSettings;
            if (settings == null || !settings.AutoAcceptCoordinatorInvite)
                return;

            string inviter = args.Args.Length > 0 ? args.Args[0]?.ToString() ?? string.Empty : string.Empty;
            if (!IsInviterAuthorized(inviter, settings, SquadContext.GetLeaderStateSnapshot()))
            {
                Logging.Write("[PartyGrind Sync] Declined invite from unauthorized player '{0}'.", inviter);
                Lua.DoString("DeclineGroup(); StaticPopup_Hide('PARTY_INVITE_REQUEST');");
                return;
            }

            Logging.Write("[PartyGrind Sync] Accepting squad invite from {0}.", inviter);
            Lua.DoString("AcceptGroup(); StaticPopup_Hide('PARTY_INVITE_REQUEST');");
        }

        private void OnResurrectRequest(object sender, LuaEventArgs args)
        {
            var me = StyxWoW.Me;
            if (me == null || me.IsAlive)
                return;

            Logging.Write("[PartyGrind Sync] Accepting party resurrection.");
            Lua.DoString("AcceptResurrect(); StaticPopup_Hide('RESURRECT'); StaticPopup_Hide('RESURRECT_NO_SICKNESS'); StaticPopup_Hide('RESURRECT_NO_TIMER');");
        }

        private void OnStartLootRoll(object sender, LuaEventArgs args)
        {
            var settings = SquadContext.ActiveSettings;
            if (settings == null || args.Args.Length == 0)
                return;

            if (!int.TryParse(args.Args[0]?.ToString(), out int rollId))
                return;

            int rollType = ComputeRollType(rollId, settings.RollRule);
            Lua.DoString($"RollOnLoot({rollId}, {rollType})");
        }

        private static int ComputeRollType(int rollId, SquadRollRule rule)
        {
            if (rule != SquadRollRule.SmartUpgrade)
                return (int)rule; // 0=Pass, 1=Need, 2=Greed, 3=Disenchant

            // SmartUpgrade: inspect roll link & whether item is usable upgrade, else Greed (2).
            try
            {
                var me = StyxWoW.Me;
                if (me == null) return 2;

                string canNeed = Lua.GetReturnVal<string>(
                    $"local _, _, _, _, _, canNeed = GetLootRollItemInfo({rollId}); " +
                    $"local link = GetLootRollItemLink({rollId}); " +
                    $"return tostring(canNeed == 1 or canNeed == true) .. '|' .. (link or '')", 0U) ?? string.Empty;

                if (canNeed.StartsWith("true|", StringComparison.OrdinalIgnoreCase))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(canNeed.Substring(5), @"item:(\d+)");
                    if (match.Success && uint.TryParse(match.Groups[1].Value, out uint entry))
                    {
                        var item = Styx.WoWInternals.WoWObjects.ItemInfo.FromId(entry);
                        if (item != null && new Styx.CommonBot.CharacterManagement.AutoEquipper().IsUpgrade(item))
                            return 1;
                    }
                }
            }
            catch
            {
            }
            return 2; // Greed fallback
        }

        private void OnConfirmSummon(object sender, LuaEventArgs args)
        {
            var settings = SquadContext.ActiveSettings;
            if (settings == null || !SquadContext.IsActive)
                return;

            Logging.Write("[PartyGrind Sync] Accepting party summon.");
            Lua.DoString("ConfirmSummon(); StaticPopup_Hide('CONFIRM_SUMMON');");
        }

        private void OnQuestAcceptConfirm(object sender, LuaEventArgs args)
        {
            if (!SquadContext.IsActive)
                return;
            Lua.DoString("ConfirmAcceptQuest()");
        }

        private void OnUiErrorMessage(object sender, LuaEventArgs args)
        {
            if (!SquadContext.IsActive)
                return;

            string message = args.Args.Length > 0 ? args.Args[0]?.ToString() ?? string.Empty : string.Empty;
            if (SquadTankBridge.IsLineOfSightErrorMessage(message))
                SquadTankBridge.NotifyPullLineOfSightError(message);
        }

    }
    /// <summary>Suppress duplicate in-flight invitations without treating them as party membership.</summary>
    internal sealed class SquadInviteTracker
    {
        private const long InviteCooldownMs = 10000;
        private readonly object _gate = new();
        private readonly Dictionary<string, long> _lastAttempts = new(StringComparer.OrdinalIgnoreCase);

        internal void Reset()
        {
            lock (_gate)
            {
                _lastAttempts.Clear();
            }
        }

        internal void Check(string name, long now, Func<bool, string> checkAndInvite, long cooldownMs = InviteCooldownMs)
        {
            lock (_gate)
            {
                bool mayInvite = !_lastAttempts.TryGetValue(name, out long last) || now - last >= cooldownMs;
                // Reserve before executing Lua, including when execution fails or returns no result.
                if (mayInvite)
                    _lastAttempts[name] = now;
                string result = checkAndInvite(mayInvite);
                if (result == "member")
                    _lastAttempts.Remove(name); // A later departure can be invited again.
            }
        }
    }

}
