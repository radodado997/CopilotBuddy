#nullable disable
using System;
using System.Data.SQLite;
using Styx.Logic.Pathing;

namespace Styx.Database
{
    /// <summary>
    /// Provides NPC query helpers compatible with HB 6.2.3 Query class API.
    /// Plugin code uses: Query.GetNearestNpc(mapId, location, flags, filterDelegate)
    /// </summary>
    public static class Query
    {
        private static SQLiteCommand _getNpcsWithFlagsCmd;
        private static bool _initialized;
        private static readonly object _initLock = new object();

        private static void EnsureInitialized()
        {
            if (_initialized) return;

            lock (_initLock)
            {
                if (_initialized) return;

                // Touch Instance first so Connection initialisation actually runs.
                // The old code set _initialized = true *before* checking
                // Connection.IsAvailable, which returned false while the connection
                // was still unopened, leaving _getNpcsWithFlagsCmd permanently null.
                var connection = Connection.Instance;
                if (connection == null || !Connection.IsAvailable)
                    return;

                _getNpcsWithFlagsCmd = Connection.CreateCommand(
                    "SELECT * FROM npcs WHERE map = @MAP_ID AND flag & @FLAG ORDER BY VECTORDISTANCE(x,y,z,@X,@Y,@Z) ASC");
                _initialized = true;
            }
        }

        /// <summary>
        /// Enumerates NPCs on the given map with the specified NPC flags, calling extraConditions on each.
        /// Returns the first NpcResult for which extraConditions returns true, or null if none match.
        /// Note: extraConditions may always return false and accumulate state internally (BuddyControlPanel pattern).
        /// </summary>
        public static NpcResult GetNearestNpc(uint mapId, WoWPoint searchLocation, global::Styx.UnitNPCFlags npcFlags, Func<NpcResult, bool> extraConditions = null)
        {
            EnsureInitialized();
            var command = _getNpcsWithFlagsCmd;
            if (command == null) return null;

            lock (Connection.SyncRoot)
            {
                using var reader = Connection.ExecuteReader(command,
                    mapId,
                    (uint)npcFlags,
                    searchLocation.X,
                    searchLocation.Y,
                    searchLocation.Z);

                if (reader == null) return null;

                while (reader.Read())
                {
                    var npcResult = new NpcResult(reader);
                    if (extraConditions == null)
                        return npcResult;
                    if (extraConditions(npcResult))
                        return npcResult;
                }
            }

            return null;
        }
    }
}
