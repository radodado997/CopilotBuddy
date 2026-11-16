#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SQLite;
using Styx.Combat.CombatRoutine;
using Styx.Logic.Pathing;
using Styx.WoWInternals;

namespace Styx.Database
{
    /// <summary>
    /// Provides NPC lookup queries from the SQLite database.
    /// </summary>
    public static class NpcQueries
    {
        #region SQL Commands

        // SELECT * FROM npcs WHERE name = @name LIMIT 1
        private static SQLiteCommand _getNpcByNameCmd;

        // SELECT * FROM npcs WHERE entry = @entry LIMIT 1
        private static SQLiteCommand _getNpcByIdCmd;

        // SELECT * FROM npcs WHERE map = @map AND trainer_class = @class AND level >= @LEVEL 
        // ORDER BY VECTORDISTANCE(x, y, z, @x, @y, @z) ASC
        private static SQLiteCommand _getNearestTrainerCmd;

        // SELECT * FROM npcs WHERE map = @map AND (flag & @flags) != 0 
        // ORDER BY VECTORDISTANCE(x, y, z, @x, @y, @z) LIMIT 10
        private static SQLiteCommand _getNearestNpcCmd;

        private static bool _initialized = false;

        // Navigation caches - same as HB 4.3.4 dictionary_0 / dictionary_1
        // Concurrent: GetNearestNpc/GetNearestTrainer are called from the bot thread,
        // plugin threads and the UI (vendor/trainer scans), and the old Dictionary
        // TryGetValue+Add pair could throw or lose entries under concurrency.
        private static readonly ConcurrentDictionary<NpcResult, bool> _trainerNavCache = new ConcurrentDictionary<NpcResult, bool>();
        private static readonly ConcurrentDictionary<NpcResult, bool> _npcNavCache = new ConcurrentDictionary<NpcResult, bool>();

        #endregion

        #region Initialization

        private static readonly object _initLock = new object();

        private static void EnsureInitialized()
        {
            if (_initialized) return;

            lock (_initLock)
            {
                if (_initialized) return;

                // Access Instance to trigger initialization
                var conn = Connection.Instance;
                if (conn == null || !Connection.IsAvailable) return;

                // Initialize SQL commands - EXACT comme HB
                _getNpcByNameCmd = Connection.CreateCommand(
                    "SELECT * FROM npcs WHERE name LIKE @NAME LIMIT 1");

                _getNpcByIdCmd = Connection.CreateCommand(
                    "SELECT * FROM npcs WHERE entry = @ENTRY LIMIT 1");

                _getNearestTrainerCmd = Connection.CreateCommand(
                    "SELECT * FROM npcs WHERE map = @MAP_ID AND trainer_class = @TRAINER_CLASS AND level >= @LEVEL ORDER BY VECTORDISTANCE(x,y,z,@X,@Y,@Z) ASC");

                _getNearestNpcCmd = Connection.CreateCommand(
                    "SELECT * FROM npcs WHERE map = @MAP_ID AND flag & @FLAG ORDER BY VECTORDISTANCE(x,y,z,@X,@Y,@Z) ASC LIMIT 25");

                _initialized = true;
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Gets an NPC by name.
        /// </summary>
        /// <param name="name">The name to search for.</param>
        /// <returns>The NPC result, or null if not found.</returns>
        public static NpcResult GetNpcByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            EnsureInitialized();
            var command = _getNpcByNameCmd;
            if (command == null) return null;

            // The '%' wildcards belong in the *value*, not inside the SQL literal:
            // "name LIKE '%@NAME%'" matches the literal text "@NAME", i.e. never
            // anything.  The command is shared, so parameter + execute + read must
            // happen under the connection lock.
            lock (Connection.SyncRoot)
            {
                using var reader = Connection.ExecuteReader(command, "%" + name + "%");
                if (reader != null && reader.Read())
                {
                    return new NpcResult(reader);
                }
            }
            return null;
        }

        /// <summary>
        /// Gets an NPC by entry ID.
        /// </summary>
        /// <param name="id">The entry ID.</param>
        /// <returns>The NPC result, or null if not found.</returns>
        public static NpcResult GetNpcById(uint id)
        {
            EnsureInitialized();
            var command = _getNpcByIdCmd;
            if (command == null) return null;

            lock (Connection.SyncRoot)
            {
                using var reader = Connection.ExecuteReader(command, id);
                if (reader != null && reader.Read())
                {
                    return new NpcResult(reader);
                }
            }
            return null;
        }

        /// <summary>
        /// Gets the nearest trainer of a specific class.
        /// </summary>
        /// <param name="myFaction">The player's faction.</param>
        /// <param name="mapId">The map ID.</param>
        /// <param name="searchLocation">The search location.</param>
        /// <param name="searchClass">The class to search for.</param>
        /// <returns>The nearest trainer, or null if not found.</returns>
        public static NpcResult GetNearestTrainer(WoWFactionTemplate myFaction, uint mapId, WoWPoint searchLocation, WoWClass searchClass, ISet<int>? excludedEntries = null)
        {
            EnsureInitialized();
            var command = _getNearestTrainerCmd;
            if (command == null) return null;

            var me = StyxWoW.Me;
            if (me == null) return null;

            int minLevel = me.Level > 10 ? 10 : 0;
            WoWPoint location = me.Location;

            lock (Connection.SyncRoot)
            {
                using var reader = Connection.ExecuteReader(command,
                    mapId,
                    (int)searchClass,
                    minLevel,
                    searchLocation.X,
                    searchLocation.Y,
                    searchLocation.Z);

                if (reader == null) return null;

                while (reader.Read())
                {
                    NpcResult result = new NpcResult(reader);
                    if ((result.NpcFlags & 32U) == 0U ||
                        (excludedEntries != null && excludedEntries.Contains(result.Entry)))
                        continue;

                    if (!IsFriendly(myFaction, result.Faction))
                        continue;

                    if (_trainerNavCache.TryGetValue(result, out bool cached))
                    {
                        if (!cached) continue;
                    }
                    else
                    {
                        bool canNav = CanNavigateSafely(location, result.Location);
                        _trainerNavCache[result] = canNav;
                        if (!canNav) continue;
                    }
                    return result;
                }
            }
            return null;
        }

        /// <summary>
        /// Reaction check that never throws: FromId returns null for faction ids that are
        /// not in FactionTemplate.dbc (custom/private-server NPCs) and GetReactionTowards
        /// dereferences it.  Unknown factions are treated as not-friendly (fail closed).
        /// </summary>
        private static bool IsFriendly(WoWFactionTemplate myFaction, uint factionId)
        {
            if (myFaction == null || factionId == 0)
                return false;

            var otherFaction = WoWFactionTemplate.FromId(factionId);
            if (otherFaction == null)
                return false;

            return myFaction.GetReactionTowards(otherFaction) >= WoWUnitReaction.Neutral;
        }

        /// <summary>
        /// Navigator.CanNavigateFully throws InvalidOperationException until a navigation
        /// provider has been set (OnBotStart).  Called from NpcQueries during startup that
        /// exception escaped from inside an open SQLiteDataReader; fail closed instead.
        /// </summary>
        private static bool CanNavigateSafely(WoWPoint from, WoWPoint to)
        {
            try
            {
                return Navigator.CanNavigateFully(from, to);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Gets the nearest NPC with specific flags.
        /// </summary>
        /// <param name="myFaction">The player's faction.</param>
        /// <param name="mapId">The map ID.</param>
        /// <param name="searchLocation">The search location.</param>
        /// <param name="npcFlags">The NPC flags to search for.</param>
        /// <returns>The nearest NPC, or null if not found.</returns>
        public static NpcResult GetNearestNpc(WoWFactionTemplate myFaction, uint mapId, WoWPoint searchLocation, UnitNPCFlags npcFlags, ISet<int>? excludedEntries = null)
        {
            EnsureInitialized();
            if (_getNearestNpcCmd == null) return null;

            var me = StyxWoW.Me;
            if (me == null) return null;

            WoWClass myClass = me.Class;

            // If looking for class trainer, use specialized query
            if ((npcFlags & UnitNPCFlags.ClassTrainer) != UnitNPCFlags.None)
            {
                return GetNearestTrainer(myFaction, mapId, searchLocation, myClass, excludedEntries);
            }

            lock (Connection.SyncRoot)
            {
                using var reader = Connection.ExecuteReader(_getNearestNpcCmd,
                    mapId,
                    (uint)npcFlags,
                    searchLocation.X,
                    searchLocation.Y,
                    searchLocation.Z);

                if (reader == null) return null;

                WoWPoint location = me.Location;

                while (reader.Read())
                {
                    NpcResult result = new NpcResult(reader);

                    if (excludedEntries != null && excludedEntries.Contains(result.Entry))
                        continue;

                    // Skip NPCs with invalid faction
                    if (result.Faction == 0)
                        continue;

                    // Check if class trainer matches our class (if applicable) and faction is friendly
                    if (((npcFlags & UnitNPCFlags.ClassTrainer) == UnitNPCFlags.None || result.TrainerClass == (int)myClass) &&
                        IsFriendly(myFaction, result.Faction))
                    {
                        if (_npcNavCache.TryGetValue(result, out bool cached))
                        {
                            if (!cached) continue;
                        }
                        else
                        {
                            bool canNav = CanNavigateSafely(location, result.Location);
                            _npcNavCache[result] = canNav;
                            if (!canNav) continue;
                        }
                        return result;
                    }
                }
            }
            return null;
        }

        #endregion
    }
}
