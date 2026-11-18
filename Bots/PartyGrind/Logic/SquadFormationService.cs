using System;
using System.Collections.Generic;
using System.Linq;
using Styx;
using Styx.Logic;
using Styx.Logic.Pathing;
using Styx.PartyGrind;
using Styx.PartyGrind.IPC;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;

namespace CopilotBuddy.Bots.PartyGrind.Logic
{
    /// <summary>
    /// Handles deterministic non-overlapping combat positioning (melee flank arcs,
    /// ranged/healer standoff slots), breadcrumb travel following, and mount policy gating.
    /// </summary>
    public sealed class SquadTravelProgress
    {
        public long LastReachedTimestamp { get; set; }
        public uint MapId { get; set; }
        public ulong LeaderGuid { get; set; }
    }

    public static class SquadFormationService
    {
        private const float Deg2Rad = MathF.PI / 180f;

        /// <summary>
        /// Computes a non-overlapping flank position behind <paramref name="targetPos"/> for melee DPS.
        /// Guarantees >= 2.0 yd separation across all slots for 1–5 melee attackers.
        /// </summary>
        public static WoWPoint ComputeMeleeFlankPosition(
            WoWPoint targetPos,
            float targetFacingRadians,
            float meleeFlankRadius,
            int meleeSlotIndex,
            int totalMeleeCount)
        {
            float radius = MathF.Max(totalMeleeCount >= 4 ? 3.4f : totalMeleeCount == 3 ? 3.0f : 2.2f, meleeFlankRadius);
            float behindAngle = targetFacingRadians + MathF.PI;

            float[] offsetsDeg = totalMeleeCount switch
            {
                <= 1 => new[] { 0f },
                2 => new[] { -35f, 35f },
                _ => new[] { -40f, 40f, 0f, -75f, 75f }
            };

            float angleOffset = offsetsDeg[Math.Abs(meleeSlotIndex) % offsetsDeg.Length] * Deg2Rad;
            float angle = behindAngle + angleOffset;

            return new WoWPoint(
                targetPos.X + MathF.Cos(angle) * radius,
                targetPos.Y + MathF.Sin(angle) * radius,
                targetPos.Z);
        }

        /// <summary>
        /// Computes a non-overlapping standoff position behind <paramref name="tankPos"/>
        /// for ranged DPS or Healers (typically 18–24 yd behind the tank, away from the active mob).
        /// </summary>
        public static WoWPoint ComputeStandoffPosition(
            WoWPoint tankPos,
            float tankFacingRadians,
            WoWPoint? focusTargetPos,
            float standoffDistance,
            int backlineSlotIndex,
            int totalBacklineCount)
        {
            float dist = Math.Clamp(standoffDistance, 18f, 24f);

            // Backward direction points away from the focus target behind the tank,
            // falling back to opposite the tank's facing direction when no target exists.
            float backAngle;
            if (focusTargetPos.HasValue)
            {
                float dx = tankPos.X - focusTargetPos.Value.X;
                float dy = tankPos.Y - focusTargetPos.Value.Y;
                if ((dx * dx + dy * dy) > 0.25f)
                    backAngle = MathF.Atan2(dy, dx);
                else
                    backAngle = tankFacingRadians + MathF.PI;
            }
            else
            {
                backAngle = tankFacingRadians + MathF.PI;
            }

            float dirX = MathF.Cos(backAngle);
            float dirY = MathF.Sin(backAngle);
            // Perpendicular unit vector for lateral spread (+/- 4.5 yd)
            float perpX = -dirY;
            float perpY = dirX;

            float[] lateralOffsets = totalBacklineCount switch
            {
                <= 1 => new[] { 0f },
                2 => new[] { -4.0f, 4.0f },
                _ => new[] { -4.5f, 4.5f, 0f, -9.0f, 9.0f }
            };

            float lateral = lateralOffsets[Math.Abs(backlineSlotIndex) % lateralOffsets.Length];
            // Keep overall radial standoff within 18-24 yd by projecting longitudinal distance inside the triangle
            float maxAxial = MathF.Sqrt(MathF.Max(1.0f, dist * dist - lateral * lateral));
            float axial = Math.Clamp(maxAxial, 18f, dist);

            return new WoWPoint(
                tankPos.X + dirX * axial + perpX * lateral,
                tankPos.Y + dirY * axial + perpY * lateral,
                tankPos.Z);
        }

        /// <summary>
        /// Computes travel formation follow point using leader breadcrumbs and a small slot offset
        /// so 3–5 clients travel naturally in column/stagger without stacking on one coordinate.
        /// </summary>
        public static WoWPoint ComputeTravelFollowPosition(
            WoWPoint memberPos,
            SquadLeaderState leader,
            int memberSlotIndex,
            SquadTravelProgress? progress = null)
        {
            var leaderPos = new WoWPoint(leader.X, leader.Y, leader.Z);
            WoWPoint baseAnchor = leaderPos;

            if (leader.Breadcrumbs != null && leader.Breadcrumbs.Count > 0)
            {
                // Pick the earliest breadcrumb ahead of the member that is still at least 6 yd away,
                // or fall back to leader position when close.
                float distToLeader = memberPos.Distance(leaderPos);
                if (distToLeader > 22f)
                {
                    var route = leader.Breadcrumbs.Where(b => b.MapId == leader.MapId).ToList();
                    if (progress != null && (progress.MapId != leader.MapId || progress.LeaderGuid != leader.LeaderGuid))
                    {
                        progress.LastReachedTimestamp = 0;
                        progress.MapId = leader.MapId;
                        progress.LeaderGuid = leader.LeaderGuid;
                    }
                    int nearest = -1;
                    float bestDist = float.MaxValue;
                    for (int i = 0; i < route.Count; i++)
                    {
                        var bc = route[i];
                        if (progress != null && bc.TimestampUtcMs <= progress.LastReachedTimestamp) continue;
                        float d = memberPos.Distance(new WoWPoint(bc.X, bc.Y, bc.Z));
                        if (d <= bestDist)
                        {
                            nearest = i;
                            bestDist = d;
                        }
                    }
                    if (progress != null && nearest >= 0 && bestDist < 5f)
                        progress.LastReachedTimestamp = route[nearest].TimestampUtcMs;
                    int next = bestDist < 5f ? nearest + 1 : nearest;
                    if (next >= 0 && next < route.Count)
                    {
                        var chosen = route[next];
                        // The breadcrumb stays the route anchor; the follower slot offset is applied on
                        // top of it so squadmates on the same breadcrumb do not stack on one point.
                        return ApplyFollowerSlotOffset(
                            new WoWPoint(chosen.X, chosen.Y, chosen.Z), leader.Facing, memberSlotIndex);
                    }

                }
            }

            return ApplyFollowerSlotOffset(baseAnchor, leader.Facing, memberSlotIndex);
        }

        /// <summary>
        /// Applies the deterministic follower slot offset (trail distance + lateral stagger) around
        /// a route anchor so followers never stack on identical coordinates.
        /// </summary>
        public static WoWPoint ApplyFollowerSlotOffset(WoWPoint anchor, float facing, int memberSlotIndex)
        {
            float backAngle = facing + MathF.PI;
            float dirX = MathF.Cos(backAngle);
            float dirY = MathF.Sin(backAngle);
            float perpX = -dirY;
            float perpY = dirX;

            float trailDist = 3.5f + (Math.Max(0, memberSlotIndex) * 1.4f);
            float lateral = (memberSlotIndex % 2 == 0 ? 1f : -1f) * (1.8f + (memberSlotIndex / 2) * 1.5f);

            return new WoWPoint(
                anchor.X + dirX * trailDist + perpX * lateral,
                anchor.Y + dirY * trailDist + perpY * lateral,
                anchor.Z);
        }

        /// <summary>
        /// Resolves deterministic melee and backline slot indices for the local member.
        /// </summary>
        public static (int RoleSlotIndex, int RoleCount) ResolveSubRoleSlot(
            string myName,
            SquadRole myRole,
            SquadLeaderState? leader)
        {
            if (leader == null || leader.Members == null || leader.Members.Count == 0)
                return (0, 1);

            bool isMelee = myRole == SquadRole.DpsMelee;
            bool isBackline = myRole == SquadRole.DpsRanged || myRole == SquadRole.Healer || myRole == SquadRole.HybridOffHeal;

            List<string> peers;
            if (isMelee)
            {
                peers = leader.Members
                    .Where(m => m.Role == SquadRole.DpsMelee)
                    .Select(m => m.CharacterName)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            else if (isBackline)
            {
                // Order ranged DPS slots before Healer slot so Healer lands in the center (index 2 when 2 ranged + 1 healer)
                peers = leader.Members
                    .Where(m => m.Role == SquadRole.DpsRanged || m.Role == SquadRole.Healer || m.Role == SquadRole.HybridOffHeal)
                    .OrderBy(m => m.Role == SquadRole.Healer ? 1 : 0)
                    .ThenBy(m => m.CharacterName, StringComparer.OrdinalIgnoreCase)
                    .Select(m => m.CharacterName)
                    .ToList();
            }
            else
            {
                return (0, 1);
            }

            int idx = peers.FindIndex(n => string.Equals(n, myName, StringComparison.OrdinalIgnoreCase));
            return idx >= 0 ? (idx, peers.Count) : (0, Math.Max(1, peers.Count));
        }

        /// <summary>
        /// Determines whether the squad (or local player under the active <see cref="SquadMountPolicy"/>)
        /// is allowed to mount.
        /// </summary>
        public static bool CanSquadMount(
            SquadMountPolicy policy,
            bool localCanMount,
            SquadLeaderState? leader)
        {
            if (!localCanMount)
                return false;

            switch (policy)
            {
                case SquadMountPolicy.NeverMount:
                    return false;

                case SquadMountPolicy.MountAndWaitAtCheckpoints:
                    return true;

                case SquadMountPolicy.SyncWithSlowestMember:
                default:
                    if (leader == null || leader.Members == null || leader.Members.Count == 0)
                        return localCanMount;
                    return leader.Members
                        .Where(m => m.IsConnected)
                        .All(m => m.HasTelemetry && (m.IsDead || m.IsGhost || m.CanMount));
            }
        }

        /// <summary>
        /// True when the character actually owns a usable ground mount. Mount availability is a
        /// capability reported to the squad; it must not be inferred from level alone.
        /// </summary>
        public static bool HasUsableMount(LocalPlayer? me)
        {
            if (me == null || me.Level < 20)
                return false;

            try
            {
                return (MountHelper.GroundMounts?.Count ?? 0) > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Local mounting gate: mount capability plus the transient conditions (combat, death,
        /// mount/combat cooldowns) that only apply to this client.
        /// </summary>
        public static bool CanMountLocally(LocalPlayer? me)
        {
            if (!HasUsableMount(me))
                return false;
            if (me!.Combat || me.Dead || me.IsGhost)
                return false;
            return Mount.AreMountTimersReady;
        }

        /// <summary>
        /// Mount.OnMountUp guard installed while PartyGrindBot is running so Navigator's
        /// internal StateMount does not leave non-mount squadmates behind.
        /// </summary>
        public static void HandleMountUpEvent(object? sender, MountUpEventArgs args)
        {
            if (!SquadContext.IsActive || SquadContext.ActiveSettings == null)
                return;

            var me = StyxWoW.Me;
            if (!CanMountLocally(me) ||
                !CanSquadMount(SquadContext.ActiveSettings.MountPolicy, true, SquadContext.GetLeaderStateSnapshot()))
            {
                args.Cancel = true;
            }
        }
    }
}
