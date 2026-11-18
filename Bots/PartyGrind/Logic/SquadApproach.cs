using System;
using System.Collections.Generic;
using Styx;
using Styx.Logic.Pathing;
using Styx.WoWInternals.World;
using Styx.WoWInternals.WoWObjects;

namespace CopilotBuddy.Bots.PartyGrind.Logic
{
    internal sealed class SquadApproach
    {
        private const long RetryBackoffMs = 15000;
        private const long SightSeekCacheMs = 1000;
        private const long StaleRecordMs = 120000;
        private const float DestinationDriftYd = 3f;

        private readonly Dictionary<ulong, ApproachRecord> _progress = new();

        private struct ApproachRecord
        {
            public float Distance;
            public long Progress;
            public long Retry;
            public long Attempt;
            public long LastTouch;
            public WoWPoint Destination;
            public WoWPoint SightSeekDestination;
            public long SightSeekTimestamp;
        }

        /// <summary>
        /// Drops every approach record. Called when the bot session ends so retry budgets and
        /// backoff deadlines never leak into the next session.
        /// </summary>
        public void Clear() => _progress.Clear();

        public bool TryMove(WoWUnit target, string reason)
        {
            if (target == null || !target.IsValid)
                return false;

            var me = StyxWoW.Me;
            if (me == null || !me.IsAlive)
                return false;

            long now = Environment.TickCount64;
            EvictStaleRecords(now);

            float distance = me.Location.Distance(target.Location);
            bool hasState = _progress.TryGetValue(target.Guid, out var state);
            if (!hasState)
                state = new ApproachRecord { Distance = distance, Progress = now, Attempt = now, LastTouch = now };

            // Navigation arrival and spell readiness stay separate outcomes: when the destination is
            // reached without spell sight we keep steering to a sighted point next to the target.
            WoWPoint destination = ResolveDestination(target, me, now, ref state);

            // A backoff only describes the destination it was recorded for; when the target moves
            // materially the retry deadline is stale and a fresh attempt is allowed.
            if (state.Retry != 0 && state.Destination.Distance(destination) > DestinationDriftYd)
                state.Retry = 0;

            state.LastTouch = now;

            if (now < state.Retry)
            {
                _progress[target.Guid] = state;
                return false;
            }

            if (!hasState || now - state.Attempt > 1000 || state.Retry != 0 || distance < state.Distance - 1f)
            {
                state.Distance = distance;
                state.Progress = now;
                state.Retry = 0;
                state.Attempt = now;
            }

            state.Destination = destination;

            var result = Navigator.MoveTo(destination, reason);

            bool arrived = result == MoveResult.ReachedDestination;
            bool needsMoreSight = arrived && !target.InLineOfSpellSight;

            if (result is MoveResult.Failed or MoveResult.PathGenerationFailed || now - state.Progress > 5000)
            {
                state.Retry = now + RetryBackoffMs;
                _progress[target.Guid] = state;
                return false;
            }

            _progress[target.Guid] = state;

            // Reached the destination with spell sight available: no movement this tick and no
            // backoff, so the caller can cast immediately.
            if (arrived && !needsMoreSight)
                return false;

            return true;
        }

        private static WoWPoint ResolveDestination(WoWUnit target, LocalPlayer me, long now, ref ApproachRecord state)
        {
            WoWPoint targetPos = target.Location;
            if (target.InLineOfSpellSight)
                return targetPos;

            // Reuse a recently computed sighted point while it still lines up with the target.
            if (state.SightSeekTimestamp != 0 &&
                now - state.SightSeekTimestamp <= SightSeekCacheMs &&
                state.SightSeekDestination.Distance(targetPos) <= 12f)
            {
                return state.SightSeekDestination;
            }

            WoWPoint best = targetPos;
            float bestDistance = float.MaxValue;
            float[] radii = { 6f, 14f };
            for (int r = 0; r < radii.Length && bestDistance == float.MaxValue; r++)
            {
                for (int i = 0; i < 8; i++)
                {
                    float angle = i * (MathF.PI / 4f);
                    var candidate = new WoWPoint(
                        targetPos.X + MathF.Cos(angle) * radii[r],
                        targetPos.Y + MathF.Sin(angle) * radii[r],
                        targetPos.Z);

                    float distanceFromMe = me.Location.Distance(candidate);
                    if (distanceFromMe >= bestDistance)
                        continue;
                    if (!GameWorld.IsInLineOfSpellSight(candidate, targetPos))
                        continue;

                    best = candidate;
                    bestDistance = distanceFromMe;
                }
            }

            state.SightSeekTimestamp = now;
            state.SightSeekDestination = best;
            return best;
        }

        private void EvictStaleRecords(long now)
        {
            if (_progress.Count <= 16)
                return;

            List<ulong>? stale = null;
            foreach (var kv in _progress)
            {
                var record = kv.Value;
                // Evict only once the retry deadline elapsed (or the record was abandoned), so an
                // active attempt keeps its progress and its pending backoff.
                bool retryExpired = record.Retry != 0 && now > record.Retry;
                bool abandoned = now - record.LastTouch > StaleRecordMs;
                if (!retryExpired && !abandoned)
                    continue;

                stale ??= new List<ulong>();
                stale.Add(kv.Key);
            }

            if (stale == null)
                return;

            foreach (ulong guid in stale)
                _progress.Remove(guid);
        }
    }
}
