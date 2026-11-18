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
        private long _lastInviteUtcMs;
        private long _lastLootMethodUtcMs;

        public SquadPartySyncService(SquadProvisioningService provisioning)
        {
            _provisioning = provisioning ?? throw new ArgumentNullException(nameof(provisioning));
        }

        public void AttachEvents()
        {
            if (_eventsAttached)
                return;

            Lua.Events.AttachEvent("PARTY_INVITE_REQUEST", OnPartyInviteRequest);
            Lua.Events.AttachEvent("RESURRECT_REQUEST", OnResurrectRequest);
            Lua.Events.AttachEvent("START_LOOT_ROLL", OnStartLootRoll);
            Lua.Events.AttachEvent("CONFIRM_SUMMON", OnConfirmSummon);
            Lua.Events.AttachEvent("QUEST_ACCEPT_CONFIRM", OnQuestAcceptConfirm);
            Lua.Events.AttachEvent("TRADE_REQUEST_CANCEL", OnTradeRequestCancel);
            Lua.Events.AttachEvent("TRADE_CLOSED", OnTradeClosed);
            _eventsAttached = true;
        }

        public void DetachEvents()
        {
            if (!_eventsAttached)
                return;

            try
            {
                Lua.Events.DetachEvent("PARTY_INVITE_REQUEST", OnPartyInviteRequest);
                Lua.Events.DetachEvent("RESURRECT_REQUEST", OnResurrectRequest);
                Lua.Events.DetachEvent("START_LOOT_ROLL", OnStartLootRoll);
                Lua.Events.DetachEvent("CONFIRM_SUMMON", OnConfirmSummon);
                Lua.Events.DetachEvent("QUEST_ACCEPT_CONFIRM", OnQuestAcceptConfirm);
                Lua.Events.DetachEvent("TRADE_REQUEST_CANCEL", OnTradeRequestCancel);
                Lua.Events.DetachEvent("TRADE_CLOSED", OnTradeClosed);
            }
            catch (Exception ex)
            {
                Logging.WriteDebug("[PartyGrind Sync] DetachEvents warning: {0}", ex.Message);
            }
            finally
            {
                _eventsAttached = false;
            }
        }

        /// <summary>
        /// Coordinator periodically invites missing squadmates and enforces the configured LootMethod.
        /// </summary>
        public void PulseCoordinatorPartySync(SquadSettings settings, IReadOnlyList<SquadConnectedMemberSnapshot> connected)
        {
            var me = StyxWoW.Me;
            if (me == null || !settings.IsCoordinator)
                return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 1. Invite missing members on interval.
            if (settings.AutoInviteMembers &&
                (now - _lastInviteUtcMs) >= Math.Max(3, settings.InviteIntervalSeconds) * 1000L)
            {
                _lastInviteUtcMs = now;

                var currentPartyNames = new HashSet<string>(
                    me.PartyMemberInfos.Select(p => p.Name ?? string.Empty),
                    StringComparer.OrdinalIgnoreCase);

                var desiredNames = new HashSet<string>(settings.GetExpectedMemberNames(), StringComparer.OrdinalIgnoreCase);
                foreach (var snap in connected)
                {
                    if (!string.IsNullOrWhiteSpace(snap.CharacterName))
                        desiredNames.Add(snap.CharacterName.Trim());
                }

                desiredNames.Remove(me.Name ?? string.Empty);

                foreach (string name in desiredNames)
                {
                    if (!currentPartyNames.Contains(name))
                    {
                        Logging.WriteDebug("[PartyGrind Sync] Inviting squadmate: {0}", name);
                        Lua.DoString($"InviteUnit('{Lua.Escape(name)}')");
                    }
                }
            }

            // 2. Enforce configured party loot method (freeforall / group).
            if (me.PartyMemberInfos.Count > 0 && (now - _lastLootMethodUtcMs) >= 15000L)
            {
                _lastLootMethodUtcMs = now;
                string method = settings.LootMethod == SquadLootMethod.FreeForAll ? "freeforall" : "group";
                Lua.DoString($"if GetLootMethod() ~= '{method}' then SetLootMethod('{method}') end");
            }
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

            if (leader != null && !string.IsNullOrWhiteSpace(leader.LeaderName) &&
                string.Equals(trimmed, leader.LeaderName.Trim(), StringComparison.OrdinalIgnoreCase))
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
                    return 1; // Need when WoW considers this class/spec eligible for Need
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

        private void OnTradeRequestCancel(object sender, LuaEventArgs args)
        {
            var settings = SquadContext.ActiveSettings;
            var leader = SquadContext.GetLeaderStateSnapshot();
            string target = leader?.ActiveTradeTargetName ?? string.Empty;
            _provisioning.OnTradeCancelledOrTimeout(target, settings?.ProvisionCooldownSeconds ?? 30);
        }

        private void OnTradeClosed(object sender, LuaEventArgs args)
        {
            _provisioning.OnTradeClosed();
        }
    }
}
