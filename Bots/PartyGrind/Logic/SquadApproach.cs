using System;
using System.Collections.Generic;
using System.Linq;
using Styx;
using Styx.Logic.Pathing;
using Styx.WoWInternals.WoWObjects;

namespace CopilotBuddy.Bots.PartyGrind.Logic
{
    internal sealed class SquadApproach
    {
        private readonly Dictionary<ulong, (float Distance, long Progress, long Retry, long Attempt)> _progress = new();
        public bool TryMove(WoWUnit target, string reason)
        {
            long now = Environment.TickCount64;
            foreach (var stale in _progress.Where(p => p.Key != target.Guid && now >= p.Value.Retry && now - p.Value.Attempt > 15000).Select(p => p.Key).ToList())
                _progress.Remove(stale);
            float distance = StyxWoW.Me.Location.Distance(target.Location);
            if (!_progress.TryGetValue(target.Guid, out var state)) state = (distance, now, 0, now);
            if (now < state.Retry) return false;
            if (now - state.Attempt > 1000 || state.Retry != 0 || distance < state.Distance - 1f) state = (distance, now, 0, now);

            // A target inside navigation tolerance can still be unreachable for spell
            // casting (e.g. behind geometry). Aim at a nearby navigable casting point
            // instead of the target itself, falling back to the original location.
            WoWPoint destination = target.Location;
            if (distance <= Navigator.PathPrecision)
            {
                var meLocation = StyxWoW.Me.Location;
                float dx = meLocation.X - target.Location.X;
                float dy = meLocation.Y - target.Location.Y;
                float length = (float)Math.Sqrt(dx * dx + dy * dy);
                if (length <= 0.01f) { dx = 1f; dy = 0f; length = 1f; }
                float offset = Navigator.PathPrecision + 1.5f;
                foreach (float direction in new[] { 1f, -1f })
                {
                    var candidate = new WoWPoint(
                        target.Location.X + direction * dx / length * offset,
                        target.Location.Y + direction * dy / length * offset,
                        target.Location.Z);
                    var castingPoint = Navigator.FindNearestPoint(candidate);
                    if (castingPoint.Distance(target.Location) > Navigator.PathPrecision &&
                        castingPoint.Distance(meLocation) > Navigator.PathPrecision)
                    {
                        destination = castingPoint;
                        break;
                    }
                }
            }

            var result = Navigator.MoveTo(destination, reason);
            if (result is MoveResult.Failed or MoveResult.PathGenerationFailed or MoveResult.ReachedDestination || now - state.Progress > 5000)
            {
                // Release any partial navigation order before abandoning this target;
                // the retry bookkeeping below is unchanged.
                Navigator.Clear();
                _progress[target.Guid] = (distance, now, now + 15000, now);
                return false;
            }
            _progress[target.Guid] = (state.Distance, state.Progress, state.Retry, now);
            return true;
        }
    }
}
