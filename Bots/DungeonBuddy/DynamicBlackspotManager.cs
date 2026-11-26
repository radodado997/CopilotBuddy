using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using Styx;
using Styx.Helpers;
using Styx.Logic.Pathing;
using Tripper.Navigation;
using PathNavigator = Styx.Logic.Pathing.Navigator;

namespace Bots.DungeonBuddy
{
    /// <summary>
    /// HB WoD DungeonBuddy dynamic blackspot manager. Restores polygon areas when blackspots expire.
    /// </summary>
    public static class DynamicBlackspotManager
    {
        private static readonly object _lock = new object();
        private static readonly List<DynamicBlackspot> _blackspots = new List<DynamicBlackspot>();
        private static readonly Dictionary<PolygonReference, (uint MapId, byte Area)> _originalAreas = new Dictionary<PolygonReference, (uint MapId, byte Area)>();

        static DynamicBlackspotManager()
        {
            PathNavigator.TripperNavigator.TileLoaded += OnTileLoaded;
        }

        public static void Pulse()
        {
            bool changed = false;

            DynamicBlackspot[] toRemove;
            lock (_lock)
            {
                toRemove = _blackspots.Where(blackspot => blackspot.ShouldRemove && blackspot.ClearApplied()).ToArray();
            }

            foreach (DynamicBlackspot blackspot in toRemove)
            {
                if (!string.IsNullOrEmpty(blackspot.Name))
                    Logging.Write(Colors.RoyalBlue, "Removed blackspot for {0}", blackspot.Name);

                changed = true;
            }

            if (changed)
                Reset();

            DynamicBlackspot[] candidates;
            lock (_lock)
            {
                candidates = _blackspots.Where(blackspot => blackspot.ShouldApplyNow).ToArray();
            }

            foreach (DynamicBlackspot blackspot in candidates)
            {
                if (!blackspot.Apply())
                    continue;

                if (blackspot.EffectedPolys != null)
                {
                    lock (_lock)
                    {
                        foreach (KeyValuePair<PolygonReference, byte> polygonArea in blackspot.EffectedPolys)
                        {
                            if (!_originalAreas.ContainsKey(polygonArea.Key))
                                _originalAreas[polygonArea.Key] = ((uint)blackspot.MapId, polygonArea.Value);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(blackspot.Name))
                    Logging.Write(Colors.RoyalBlue, "Applied blackspot for {0}", blackspot.Name);

                changed = true;
            }

            if (changed)
                PathNavigator.Clear();
        }

        public static void AddBlackspot(DynamicBlackspot blackspot)
        {
            lock (_lock)
            {
                _blackspots.Add(blackspot);
            }
        }

        public static void AddBlackspots(params DynamicBlackspot[] blackspots)
        {
            AddBlackspots((IEnumerable<DynamicBlackspot>)blackspots);
        }

        public static void AddBlackspots(IEnumerable<DynamicBlackspot> blackspots)
        {
            lock (_lock)
            {
                foreach (DynamicBlackspot blackspot in blackspots)
                    _blackspots.Add(blackspot);
            }
        }

        public static void RemoveBlackspot(DynamicBlackspot blackspot)
        {
            RemoveBlackspots(new[] { blackspot });
        }

        public static void RemoveBlackspots(params DynamicBlackspot[] blackspots)
        {
            RemoveBlackspots((IEnumerable<DynamicBlackspot>)blackspots);
        }

        public static void RemoveBlackspots(IEnumerable<DynamicBlackspot> blackspots)
        {
            Reset();
            lock (_lock)
            {
                foreach (DynamicBlackspot blackspot in blackspots)
                    _blackspots.Remove(blackspot);
            }
        }

        public static void Clear()
        {
            Reset();
            lock (_lock)
            {
                _blackspots.Clear();
            }
        }

        public static void Reset()
        {
            KeyValuePair<PolygonReference, (uint MapId, byte Area)>[] areasToRestore;
            lock (_lock)
            {
                foreach (DynamicBlackspot blackspot in _blackspots)
                    blackspot.ClearApplied();

                areasToRestore = _originalAreas.ToArray();
                _originalAreas.Clear();
            }

            var nav = PathNavigator.TripperNavigator;
            foreach (KeyValuePair<PolygonReference, (uint MapId, byte Area)> polygonArea in areasToRestore)
                nav.SetPolyArea(polygonArea.Value.MapId, polygonArea.Key, polygonArea.Value.Area);
        }

        private static void OnTileLoaded(object? sender, TileLoadedEventArgs e)
        {
            DynamicBlackspot[] overlapping;
            lock (_lock)
            {
                overlapping = _blackspots.Where(b => b.OverlapsTile(e.MapId, e.TileX, e.TileY)).ToArray();
            }

            if (overlapping.Length == 0)
                return;

            bool changed = false;
            foreach (DynamicBlackspot blackspot in overlapping)
            {
                Dictionary<PolygonReference, byte>? added = blackspot.ApplyNewlyLoadedPolygons();
                if (added == null || added.Count == 0)
                    continue;

                lock (_lock)
                {
                    foreach (KeyValuePair<PolygonReference, byte> polygonArea in added)
                    {
                        if (!_originalAreas.ContainsKey(polygonArea.Key))
                            _originalAreas[polygonArea.Key] = ((uint)blackspot.MapId, polygonArea.Value);
                    }
                }

                changed = true;
            }

            if (changed)
                PathNavigator.Clear();
        }
    }
}
