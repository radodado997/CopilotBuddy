using System;
using System.Collections.Generic;
using Styx;
using Styx.Logic.Pathing;
using Styx.WoWInternals.WoWObjects;

namespace CopilotBuddy.Bots.PartyGrind.Logic
{
    internal sealed class SquadApproach
    {
        private readonly Dictionary<ulong, (float Distance, long Progress, long Retry)> _progress = new();
        public bool TryMove(WoWUnit target, string reason)
        {
            long now = Environment.TickCount64;
            float distance = StyxWoW.Me.Location.Distance(target.Location);
            if (!_progress.TryGetValue(target.Guid, out var state)) state = (distance, now, 0);
            if (now < state.Retry) return false;
            if (state.Retry != 0 || distance < state.Distance - 1f) state = (distance, now, 0);
            var result = Navigator.MoveTo(target.Location, reason);
            if (result is MoveResult.Failed or MoveResult.PathGenerationFailed or MoveResult.ReachedDestination || now - state.Progress > 5000)
            {
                _progress[target.Guid] = (distance, now, now + 15000);
                return false;
            }
            _progress[target.Guid] = state;
            return true;
        }
    }
}
