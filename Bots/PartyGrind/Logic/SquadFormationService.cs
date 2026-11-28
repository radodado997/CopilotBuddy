using System;
using System.Collections.Generic;
using System.Linq;
using Styx;
using Styx.Logic;
using Styx.Logic.Pathing;
using Styx.PartyGrind;
using Styx.PartyGrind.IPC;
using Styx.WoWInternals;

namespace CopilotBuddy.Bots.PartyGrind.Logic
{
    /// <summary>
    /// Handles deterministic non-overlapping combat positioning (melee flank arcs,
    /// ranged/healer standoff slots), breadcrumb travel following, and mount policy gating.
    /// </summary>
    public sealed class SquadTravelProgress
    {
        public long LastReachedSequence { get; set; }
        public int RouteGeneration { get; set; }
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
            int totalMeleeCount,
            float targetCombatReach = 1.5f)
        {
            float minReachRadius = MathF.Max(2.2f, targetCombatReach + 1.2f);
            float radius = MathF.Max(MathF.Max(totalMeleeCount >= 4 ? 3.4f : totalMeleeCount == 3 ? 3.0f : 2.2f, meleeFlankRadius), minReachRadius);
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

        public static WoWPoint ComputeDungeonMeleePosition(
            WoWPoint targetPos,
            WoWPoint tankPos,
            float targetFacingRadians,
            float meleeFlankRadius,
            int meleeSlotIndex,
            int totalMeleeCount,
            float targetCombatReach = 1.5f)
        {
            float dx = tankPos.X - targetPos.X;
            float dy = tankPos.Y - targetPos.Y;
            float tankDist = MathF.Sqrt(dx * dx + dy * dy);

            float ux;
            float uy;
            if (tankDist > 0.25f)
            {
                ux = dx / tankDist;
                uy = dy / tankDist;
            }
            else
            {
                ux = MathF.Cos(targetFacingRadians);
                uy = MathF.Sin(targetFacingRadians);
            }

            float px = -uy;
            float py = ux;

            float minReachRadius = MathF.Max(2.2f, targetCombatReach + 1.2f);
            float safeMeleeReach = MathF.Max(minReachRadius, targetCombatReach + 2.2f);
            float requestedRadius = MathF.Max(MathF.Max(totalMeleeCount >= 4 ? 3.4f : totalMeleeCount == 3 ? 3.0f : 2.2f, meleeFlankRadius), minReachRadius);
            float radius = MathF.Min(requestedRadius, safeMeleeReach);

            float[] offsetsDeg = totalMeleeCount switch
            {
                <= 1 => new[] { 0f },
                2 => new[] { -30f, 30f },
                _ => new[] { -35f, 35f, 0f, -55f, 55f }
            };

            float angleOffset = offsetsDeg[Math.Abs(meleeSlotIndex) % offsetsDeg.Length] * Deg2Rad;
            float axial = MathF.Cos(angleOffset) * radius;
            float lateral = MathF.Sin(angleOffset) * radius;

            if (tankDist > 0.25f && axial < tankDist)
                axial = tankDist;

            float swingReach = MathF.Max(radius, targetCombatReach + 2.83f);
            float meleeGateReach = MathF.Max(5.0f, targetCombatReach + 2.8333334f);
            bool tankInsideMeleeGate = tankDist <= meleeGateReach;
            float maxMeleeReach = totalMeleeCount > 1 && tankDist > swingReach && tankInsideMeleeGate
                ? MathF.Max(swingReach, meleeGateReach - 0.16f)
                : swingReach;

            if (tankInsideMeleeGate)
            {
                if (totalMeleeCount <= 1 && axial > meleeGateReach - 0.25f)
                {
                    float minNonAheadAxial = MathF.Max(0f, tankDist - 0.6f);
                    axial = MathF.Max(minNonAheadAxial, meleeGateReach - 0.25f);
                }
                else if (totalMeleeCount > 1)
                {
                    float currentLateralLimit = axial < maxMeleeReach
                        ? MathF.Sqrt(MathF.Max(0f, maxMeleeReach * maxMeleeReach - axial * axial))
                        : 0f;
                    if (currentLateralLimit < 0.8f)
                    {
                        float minNonAheadAxial = MathF.Max(0f, tankDist - 0.6f);
                        float desiredAxial = MathF.Max(minNonAheadAxial, MathF.Min(axial, maxMeleeReach - 0.5f));
                        axial = MathF.Min(axial, desiredAxial);
                    }
                }
            }

            if (totalMeleeCount <= 1 && axial >= maxMeleeReach)
            {
                lateral = 0f;
            }
            else
            {
                float lateralLimit = axial < maxMeleeReach
                    ? MathF.Sqrt(MathF.Max(0f, maxMeleeReach * maxMeleeReach - axial * axial))
                    : 2.0f;
                int activeSlotCount = Math.Min(Math.Max(1, totalMeleeCount), offsetsDeg.Length);
                float maxPatternLateral = 0f;
                for (int i = 0; i < activeSlotCount; i++)
                {
                    float candidateLateral = MathF.Abs(MathF.Sin(offsetsDeg[i] * Deg2Rad) * radius);
                    if (candidateLateral > maxPatternLateral)
                        maxPatternLateral = candidateLateral;
                }
                if (maxPatternLateral > lateralLimit && maxPatternLateral > 0.001f)
                    lateral *= lateralLimit / maxPatternLateral;
            }

            float z = tankPos.Distance(targetPos) > meleeGateReach ? tankPos.Z : targetPos.Z;
            return new WoWPoint(
                targetPos.X + ux * axial + px * lateral,
                targetPos.Y + uy * axial + py * lateral,
                z);
        }

        public static bool IsPositionAheadOfTank(
            WoWPoint position,
            WoWPoint targetPos,
            WoWPoint tankPos,
            float tolerance = 0.75f)
        {
            float dx = tankPos.X - targetPos.X;
            float dy = tankPos.Y - targetPos.Y;
            float tankDist = MathF.Sqrt(dx * dx + dy * dy);
            if (tankDist <= 0.25f)
                return false;

            float ux = dx / tankDist;
            float uy = dy / tankDist;
            float proj = (position.X - targetPos.X) * ux + (position.Y - targetPos.Y) * uy;
            return proj < tankDist - tolerance;
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
                // Track route progress on every pulse, even while in close formation,
                // so a follower that later falls behind resumes after the last breadcrumb
                // it actually reached.
                var route = leader.Breadcrumbs.Where(b => b.MapId == leader.MapId).ToList();
                int routeGeneration = route.Count > 0 ? route[^1].RouteGeneration : 0;
                if (progress != null && (progress.MapId != leader.MapId || progress.LeaderGuid != leader.LeaderGuid ||
                                         progress.RouteGeneration != routeGeneration))
                {
                    progress.LastReachedSequence = 0;
                    progress.MapId = leader.MapId;
                    progress.LeaderGuid = leader.LeaderGuid;
                    progress.RouteGeneration = routeGeneration;
                }
                int nearest = -1;
                float bestDist = float.MaxValue;
                for (int i = 0; i < route.Count; i++)
                {
                    var bc = route[i];
                    if (bc.RouteGeneration != routeGeneration) continue;
                    if (progress != null && bc.Sequence <= progress.LastReachedSequence) continue;
                    float d = memberPos.Distance(new WoWPoint(bc.X, bc.Y, bc.Z));
                    if (d <= bestDist)
                    {
                        nearest = i;
                        bestDist = d;
                    }
                }
                if (progress != null && nearest >= 0 && bestDist < 5f)
                    progress.LastReachedSequence = route[nearest].Sequence;

                float distToLeader = memberPos.Distance(leaderPos);
                if (distToLeader > 22f)
                {
                    int next = bestDist < 5f ? nearest + 1 : nearest;
                    if (next >= 0 && next < route.Count)
                    {
                        var chosen = route[next];
                        return new WoWPoint(chosen.X, chosen.Y, chosen.Z);
                    }
                }
            }

            float backAngle = leader.Facing + MathF.PI;
            float dirX = MathF.Cos(backAngle);
            float dirY = MathF.Sin(backAngle);
            float perpX = -dirY;
            float perpY = dirX;

            float trailDist = 3.0f + (Math.Max(0, memberSlotIndex) * 2.5f);
            float lateral = (memberSlotIndex % 2 == 0 ? 1f : -1f) * (1.5f + (memberSlotIndex / 2) * 1.2f);
            float resumeDistance = leader.ResumeDistance > 0f
                ? leader.ResumeDistance
                : (SquadContext.ActiveSettings?.ResumeDistance ?? 20.0f);
            float maxOffset = Math.Max(0.5f, resumeDistance - 2.6f);
            float offsetMag = MathF.Sqrt(trailDist * trailDist + lateral * lateral);
            if (offsetMag > maxOffset)
            {
                float scale = maxOffset / offsetMag;
                trailDist *= scale;
                lateral *= scale;
            }

            return new WoWPoint(
                baseAnchor.X + dirX * trailDist + perpX * lateral,
                baseAnchor.Y + dirY * trailDist + perpY * lateral,
                baseAnchor.Z);
        }

        public static WoWPoint ComputeDungeonFollowPosition(
            WoWPoint memberPos,
            SquadLeaderState leader,
            int memberSlotIndex,
            SquadTravelProgress? progress = null)
        {
            var leaderPos = new WoWPoint(leader.X, leader.Y, leader.Z);
            int clampedSlot = Math.Max(0, memberSlotIndex);
            float desiredTrail = 2.8f + clampedSlot * 1.9f;
            float lateral = (clampedSlot % 2 == 0 ? 1f : -1f) * Math.Min(1.1f, 0.55f + (clampedSlot / 2) * 0.45f);

            if (leader.Breadcrumbs != null && leader.Breadcrumbs.Count > 0)
            {
                var route = leader.Breadcrumbs.Where(b => b.MapId == leader.MapId).ToList();
                int routeGeneration = route.Count > 0 ? route[^1].RouteGeneration : 0;
                if (progress != null && (progress.MapId != leader.MapId || progress.LeaderGuid != leader.LeaderGuid ||
                                         progress.RouteGeneration != routeGeneration))
                {
                    progress.LastReachedSequence = 0;
                    progress.MapId = leader.MapId;
                    progress.LeaderGuid = leader.LeaderGuid;
                    progress.RouteGeneration = routeGeneration;
                }

                int nearest = -1;
                float bestDist = float.MaxValue;
                for (int i = 0; i < route.Count; i++)
                {
                    var bc = route[i];
                    if (bc.RouteGeneration != routeGeneration)
                        continue;
                    if (progress != null && bc.Sequence <= progress.LastReachedSequence)
                        continue;
                    float d = memberPos.Distance(new WoWPoint(bc.X, bc.Y, bc.Z));
                    if (d <= bestDist)
                    {
                        nearest = i;
                        bestDist = d;
                    }
                }

                if (progress != null && nearest >= 0 && bestDist < 4.5f)
                    progress.LastReachedSequence = route[nearest].Sequence;

                if (memberPos.Distance(leaderPos) > 16f && route.Count > 0)
                {
                    int next = bestDist < 4.5f ? nearest + 1 : nearest;
                    if (next >= 0 && next < route.Count)
                    {
                        var chosen = route[next];
                        return new WoWPoint(chosen.X, chosen.Y, chosen.Z);
                    }
                }

                if (route.Count >= 1)
                {
                    for (int i = route.Count - 1; i >= 0; i--)
                    {
                        var bcPt = new WoWPoint(route[i].X, route[i].Y, route[i].Z);
                        float dLeader = leaderPos.Distance(bcPt);
                        if (dLeader >= desiredTrail * 0.7f && dLeader <= desiredTrail + 6.0f)
                        {
                            float dx = bcPt.X - leaderPos.X;
                            float dy = bcPt.Y - leaderPos.Y;
                            float len = MathF.Sqrt(dx * dx + dy * dy);
                            if (len > 0.1f)
                            {
                                float ux = dx / len;
                                float uy = dy / len;
                                float px = -uy;
                                float py = ux;
                                return new WoWPoint(
                                    leaderPos.X + ux * desiredTrail + px * lateral,
                                    leaderPos.Y + uy * desiredTrail + py * lateral,
                                    bcPt.Z);
                            }
                        }
                    }
                }
            }

            float backAngle = leader.Facing + MathF.PI;
            float dirX = MathF.Cos(backAngle);
            float dirY = MathF.Sin(backAngle);
            float perpX = -dirY;
            float perpY = dirX;

            return new WoWPoint(
                leaderPos.X + dirX * desiredTrail + perpX * lateral,
                leaderPos.Y + dirY * desiredTrail + perpY * lateral,
                leaderPos.Z);
        }

        public static WoWPoint ComputeDungeonStandoffPosition(
            WoWPoint tankPos,
            float tankFacingRadians,
            WoWPoint? focusTargetPos,
            IReadOnlyList<SquadBreadcrumb>? breadcrumbs,
            uint mapId,
            float standoffDistance,
            int backlineSlotIndex,
            int totalBacklineCount,
            Func<WoWPoint, WoWPoint, bool>? lineOfSightCheck = null,
            Func<WoWPoint, bool>? isReachableCheck = null)
        {
            float dist = Math.Clamp(standoffDistance, 16f, 20f);
            float[] lateralOffsets = totalBacklineCount switch
            {
                <= 1 => new[] { 0f },
                2 => new[] { -1.6f, 1.6f },
                _ => new[] { -1.8f, 1.8f, 0f, -3.2f, 3.2f }
            };
            float lateral = lateralOffsets[Math.Abs(backlineSlotIndex) % lateralOffsets.Length];

            Dictionary<(float X, float Y, float Z), bool>? reachabilityCache = isReachableCheck != null
                ? new Dictionary<(float X, float Y, float Z), bool>()
                : null;
            bool CheckReachable(WoWPoint pt)
            {
                if (isReachableCheck == null)
                    return true;
                var key = (pt.X, pt.Y, pt.Z);
                if (reachabilityCache!.TryGetValue(key, out bool cached))
                    return cached;
                bool result = isReachableCheck(pt);
                reachabilityCache[key] = result;
                return result;
            }

            WoWPoint? fallbackCandidate = null;
            if (breadcrumbs != null && breadcrumbs.Count > 0)
            {
                for (int i = breadcrumbs.Count - 1; i >= 0; i--)
                {
                    var bc = breadcrumbs[i];
                    if (bc.MapId != mapId)
                        continue;
                    var bcPt = new WoWPoint(bc.X, bc.Y, bc.Z);
                    float dTank = tankPos.Distance(bcPt);
                    if (dTank < 4.0f || dTank > 26.0f)
                        continue;
                    if (focusTargetPos.HasValue && bcPt.Distance(focusTargetPos.Value) < focusTargetPos.Value.Distance(tankPos))
                        continue;

                    float dx = bcPt.X - tankPos.X;
                    float dy = bcPt.Y - tankPos.Y;
                    float len = MathF.Sqrt(dx * dx + dy * dy);
                    if (len > 0.25f)
                    {
                        float ux = dx / len;
                        float uy = dy / len;
                        float px = -uy;
                        float py = ux;
                        float clampedDist = Math.Min(dist, dTank + 3.0f);
                        var candidate = new WoWPoint(
                            tankPos.X + ux * clampedDist + px * lateral,
                            tankPos.Y + uy * clampedDist + py * lateral,
                            bcPt.Z);
                        if (!focusTargetPos.HasValue || candidate.Distance(focusTargetPos.Value) >= dist)
                        {
                            bool reachable = CheckReachable(candidate);
                            if (reachable)
                            {
                                if (lineOfSightCheck == null || !focusTargetPos.HasValue || lineOfSightCheck(candidate, focusTargetPos.Value))
                                    return candidate;
                                fallbackCandidate ??= candidate;
                            }
                        }
                    }
                }
            }

            float backAngle;
            if (focusTargetPos.HasValue)
            {
                float dx = tankPos.X - focusTargetPos.Value.X;
                float dy = tankPos.Y - focusTargetPos.Value.Y;
                backAngle = (dx != 0f || dy != 0f)
                    ? MathF.Atan2(dy, dx)
                    : tankFacingRadians + MathF.PI;
            }
            else
            {
                backAngle = tankFacingRadians + MathF.PI;
            }

            float dirX = MathF.Cos(backAngle);
            float dirY = MathF.Sin(backAngle);
            float perpX = -dirY;
            float perpY = dirX;
            float axial = MathF.Sqrt(MathF.Max(1.0f, dist * dist - lateral * lateral));

            var geometricCandidate = new WoWPoint(
                tankPos.X + dirX * axial + perpX * lateral,
                tankPos.Y + dirY * axial + perpY * lateral,
                tankPos.Z);
            bool geometricReachable = CheckReachable(geometricCandidate);
            bool geometricHasLoS = lineOfSightCheck == null || !focusTargetPos.HasValue || lineOfSightCheck(geometricCandidate, focusTargetPos.Value);
            if (geometricReachable && geometricHasLoS)
                return geometricCandidate;
            if (fallbackCandidate.HasValue)
                return fallbackCandidate.Value;
            return geometricReachable ? geometricCandidate : WoWPoint.Zero;
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
        /// Mount.OnMountUp guard installed while PartyGrindBot is running so Navigator's
        /// internal StateMount does not leave non-mount squadmates behind.
        /// </summary>
        public static void HandleMountUpEvent(object? sender, MountUpEventArgs args)
        {
            if (!SquadContext.IsActive || SquadContext.ActiveSettings == null)
                return;

            var me = StyxWoW.Me;
            bool localCanMount = me != null && me.Level >= 20;
            if (!CanSquadMount(SquadContext.ActiveSettings.MountPolicy, localCanMount, SquadContext.GetLeaderStateSnapshot()))
            {
                args.Cancel = true;
            }
        }
    }
}
