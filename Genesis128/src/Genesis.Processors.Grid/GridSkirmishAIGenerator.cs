#region License
// GridSkirmishAIGenerator.cs
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using CkMp.Data.Map;
using CkMp.Data.Objects;
using Genesis.Algorithms;
using Genesis.Core;
using Genesis.Layout;
using Genesis.Tiles;

namespace Genesis.Processors.Grid
{
    /// <summary>
    /// Creates everything the skirmish AI needs for any number of start positions on a grid layout.
    ///
    /// Names resolved by the engine per start position N (see AISkirmishPlayer.cpp, ScriptEngine.cpp, ScriptActions.cpp):
    ///  * InnerPerimeterN / OuterPerimeterN      ([Skirmish]My/EnemyInner/OuterPerimeter)
    ///  * CenterN / FlankN / BackdoorN waypaths  (skirmish approach paths, base defense placement, mines)
    ///    An attacking team picks the waypoint of the enemy's path that is closest to itself and follows the path to its end,
    ///    so all paths run from the street in front of the base towards the base.
    /// Names used by the CWC scripts:
    ///  * flagNN, fuelNN (+ _wp, _area), SkirmishWorld, CombatZone, MapCenter
    ///  * patrol paths: the per-player names (player1_patrol1, ...) are replaced by the script adapter with the shared labels
    ///    G128_patrol1/G128_patrol2. TEAM_FOLLOW_WAYPOINTS starts at the waypoint closest to the team, which is always on
    ///    the team's own patrol loop.
    /// </summary>
    public class GridSkirmishAIGenerator : IMapProcessor
    {
        public const string PatrolLabel1 = "G128_patrol1";
        public const string PatrolLabel2 = "G128_patrol2";

        /// <summary>
        /// Name of the per-player convoy entry waypoint (the CWC scripts spawn supply convoys at convoy_marker_1).
        /// </summary>
        public const string ConvoyWaypointFormat = "G128_convoy_{0}";

        private const string objectName = "objectName";
        private const float resourceAreaRadius = 200f;
        private const TileInfo invalidTiles = TileInfo.Structure | TileInfo.Cliff;

        private Map map;
        private MapInfo info;
        private GridLayout grid;
        private SnailShell<Coordinates> snailShell;
        private float mapWidth, mapHeight;
        private List<Area> outerPerimeters;

        /// <summary>
        /// Gets or sets the maximum number of flags (and of fuel depots) that are tracked by name by the CWC AI scripts.
        /// Every tracked resource adds a full script group to every AI player's copy of the scripts.
        /// </summary>
        public int TrackedResourcesPerType { get; set; }

        /// <summary>
        /// Gets the number of flags / fuel depots that have been named (and therefore need script groups).
        /// </summary>
        public static int NamedFlags { get; private set; }
        public static int NamedFuelDepots { get; private set; }

        /// <summary>
        /// Gets all waypoints of the generated AI paths (for validation).
        /// </summary>
        public static List<Tuple<string, ScriptObject>> PathWaypoints { get; private set; }

        /// <summary>
        /// Gets or sets whether the CWC skirmish template players (SkirmishUSA/SkirmishRussia) are added. Vanilla Zero
        /// Hour takes its skirmish players and scripts from Data\Scripts\SkirmishScripts.scb instead.
        /// </summary>
        public bool AddCwcSkirmishPlayers { get; set; }

        public GridSkirmishAIGenerator()
        {
            AddCwcSkirmishPlayers = true;
            TrackedResourcesPerType = 24;
        }

        public string Description
        {
            get { return "Generating skirmish AI data (grid)"; }
        }

        public bool UpdatePreview
        {
            get { return false; }
        }

        public void Process(Map map, MapInfo info)
        {
            this.map = map;
            this.info = info;
            this.grid = info.Grid;
            this.snailShell = new SnailShell<Coordinates>((x, y) => new Coordinates(x, y));
            this.mapWidth = map.HeightMap.MapWidth * Map.TileWidth;
            this.mapHeight = map.HeightMap.MapHeight * Map.TileWidth;
            this.outerPerimeters = new List<Area>();
            PathWaypoints = new List<Tuple<string, ScriptObject>>();

            NameResources();

            float innerHalf = System.Math.Min(grid.BaseRadius * Map.TileWidth + 60f, grid.HalfCellWorld - 60f);
            float outerHalf = grid.HalfCellWorld - 10f;
            Point mapCenter = new Point(mapWidth / 2, mapHeight / 2);

            foreach (var slot in grid.Slots)
            {
                Point start = map.CoordinatesToPosition(slot.Center);
                int n = slot.Number;

                map.Areas.Add(Square("InnerPerimeter" + n, start, innerHalf));
                var outer = Square("OuterPerimeter" + n, start, outerHalf);
                map.Areas.Add(outer);
                outerPerimeters.Add(outer);

                // Spoke towards the map center = "Center", the two perpendicular spokes = "Flank" and "Backdoor".
                int centerSpoke = Enumerable.Range(0, 4)
                    .OrderByDescending(d => Dot(GridLayout.SpokeDirections[d], mapCenter - start))
                    .ThenBy(d => d)
                    .First();
                int flankSpoke = (centerSpoke + 1) % 4;
                int backdoorSpoke = (centerSpoke + 3) % 4;

                AddApproachPath("Center" + n, start, GridLayout.SpokeDirections[centerSpoke]);
                AddApproachPath("Flank" + n, start, GridLayout.SpokeDirections[flankSpoke]);
                AddApproachPath("Backdoor" + n, start, GridLayout.SpokeDirections[backdoorSpoke]);

                AddPatrolPath(PatrolLabel1, start, GridLayout.SpokeDirections[centerSpoke], GridLayout.SpokeDirections[flankSpoke], innerHalf, outerHalf);
                AddPatrolPath(PatrolLabel2, start, GridLayout.SpokeDirections[centerSpoke], GridLayout.SpokeDirections[backdoorSpoke], innerHalf, outerHalf);

                AddConvoyEntry(n, start);
            }

            map.Objects.Add(info.ObjectFactory.CreateWaypoint(mapCenter.X, mapCenter.Y, "MapCenter"));

            var corners = new[] { new Point(0, 0), new Point(mapWidth, 0), new Point(mapWidth, mapHeight), new Point(0, mapHeight) };
            map.Areas.Add(info.ObjectFactory.CreateArea("SkirmishWorld", corners));
            AddCombatZone(corners);

            if (!AddCwcSkirmishPlayers)
                return;

            map.Players.Add(new Player("SkirmishRussia", false, "SkirmishRussia", "FactionRussia", string.Empty, string.Empty, 0x00, 0xFF, 0x00, 0x00));
            map.Players.Add(new Player("SkirmishUSA", false, "SkirmishUSA", "FactionUSA", string.Empty, string.Empty, 0x00, 0x00, 0x00, 0xFF));
        }

        /// <summary>
        /// Convoys enter the map at the edge street closest to the base.
        /// </summary>
        private void AddConvoyEntry(int number, Point start)
        {
            float inset = (grid.LaneHalfWidth + 2) * Map.TileWidth;
            var candidates = new[]
            {
                new Point(start.X, inset),
                new Point(start.X, mapHeight - inset),
                new Point(inset, start.Y),
                new Point(mapWidth - inset, start.Y)
            };
            var entry = candidates.OrderBy(c => (c - start).VectorLengthSquared()).First();
            var waypoint = CreateWaypoint(entry, "convoy");
            waypoint.SetProperty("waypointName", PropertyType.OneByteString, string.Format(ConvoyWaypointFormat, number));
            map.Objects.Add(waypoint);
        }

        private static float Dot(Point a, Point b)
        {
            return a.X * b.X + a.Y * b.Y;
        }

        /// <summary>
        /// Names the resources. The contested resources are the ones worth fighting for, so they are tracked first
        /// (closest to the map center first), then the villages, then the players' own resources.
        /// Untracked resources stay unnamed: they can still be captured by everyone, the CWC scripts just do not
        /// manage them individually.
        /// </summary>
        private void NameResources()
        {
            var placed = GridResourceGenerator.Placed ?? new List<PlacedResource>();
            Point center = new Point(mapWidth / 2, mapHeight / 2);

            Func<PlacedResource, int> rolePriority = r => r.Role == ResourceRole.Contested ? 0 : (r.Role == ResourceRole.Village ? 1 : 2);

            NamedFlags = NameResources(placed.Where(r => r.IsFlag), "flag", rolePriority, center);
            NamedFuelDepots = NameResources(placed.Where(r => !r.IsFlag), "fuel", rolePriority, center);
        }

        private int NameResources(IEnumerable<PlacedResource> resources, string prefix, Func<PlacedResource, int> rolePriority, Point center)
        {
            var ordered = resources
                .OrderBy(rolePriority)
                .ThenBy(r => (new Point(r.Object.X, r.Object.Y) - center).VectorLengthSquared())
                .ThenBy(r => r.Owner)
                .ToList();

            int count = 0;
            foreach (var resource in ordered)
            {
                if (count < TrackedResourcesPerType)
                {
                    count++;
                    string name = prefix + count.ToString("00");
                    resource.Object.SetProperty(objectName, PropertyType.OneByteString, name);

                    map.Objects.Add(info.ObjectFactory.CreateWaypoint(resource.Object.X, resource.Object.Y, name + "_wp"));
                    map.Areas.Add(Square(name + "_area", new Point(resource.Object.X, resource.Object.Y), resourceAreaRadius));
                }
                else
                {
                    resource.Object.Properties.Remove(objectName);
                }
            }
            return count;
        }

        private Area Square(string name, Point center, float half)
        {
            return info.ObjectFactory.CreateArea(
                name,
                new Point(System.Math.Max(0, center.X - half), System.Math.Max(0, center.Y - half)),
                new Point(System.Math.Max(0, center.X - half), System.Math.Min(mapHeight, center.Y + half)),
                new Point(System.Math.Min(mapWidth, center.X + half), System.Math.Min(mapHeight, center.Y + half)),
                new Point(System.Math.Min(mapWidth, center.X + half), System.Math.Max(0, center.Y - half)));
        }

        /// <summary>
        /// Approach path from the street in front of the base along the access lane to the edge of the base plateau.
        /// The path does not end in the base center: the AI uses the direction from its base center to the closest
        /// waypoint of its own paths to decide where to build base defenses.
        /// </summary>
        private void AddApproachPath(string label, Point start, Point direction)
        {
            float street = grid.HalfCellWorld - Map.TileWidth;
            float outerEdge = grid.HalfCellWorld * 0.75f;
            float plateauEdge = grid.BaseRadius * Map.TileWidth * 0.85f;

            var waypoints = new[]
            {
                CreateWaypoint(start + direction * street, label),
                CreateWaypoint(start + direction * outerEdge, label),
                CreateWaypoint(start + direction * plateauEdge, label)
            };
            map.AddWaypath(label, waypoints);
        }

        /// <summary>
        /// Patrol loop along the front of the base between the center lane and a side lane.
        /// </summary>
        private void AddPatrolPath(string label, Point start, Point front, Point side, float innerHalf, float outerHalf)
        {
            float radius = (innerHalf + outerHalf) / 2;
            var diagonal = (front + side).Normalize();

            var first = CreateWaypoint(start + front * radius, label);
            map.AddWaypath(
                label,
                first,
                CreateWaypoint(start + diagonal * radius, label),
                CreateWaypoint(start + side * radius, label),
                first);
        }

        private ScriptObject CreateWaypoint(Point position, string label)
        {
            var coordinates = map.PositionToCoordinates(position);
            var enumerator = snailShell.GetCoordinates(coordinates.X, coordinates.Y).GetEnumerator();
            int tries = 0;
            while (!info.Tiles.CheckCoordinates(coordinates.X, coordinates.Y) ||
                   (info.Tiles[coordinates.X, coordinates.Y] & invalidTiles) != TileInfo.Free ||
                   map.Tiles[coordinates.X, coordinates.Y].Impassable)
            {
                enumerator.MoveNext();
                coordinates = enumerator.Current;
                if (++tries > 10000)
                    break;
            }

            position = map.CoordinatesToPosition(coordinates);
            var waypoint = info.ObjectFactory.CreateWaypoint(position.X, position.Y);
            PathWaypoints.Add(Tuple.Create(label, waypoint));
            return waypoint;
        }

        /// <summary>
        /// The combat zone is the whole map except the outer perimeters. The engine tests polygons with the even-odd
        /// rule (PolygonTrigger::pointInTrigger), so the perimeters are cut out as separate loops connected to the map
        /// corner by zero-width bridges (every bridge is traversed twice and cancels itself out).
        /// </summary>
        private void AddCombatZone(Point[] corners)
        {
            var points = new List<Point>(corners);
            points.Add(corners[0]);
            foreach (var perimeter in outerPerimeters)
            {
                var square = perimeter.Points.Select(p => new Point(p[0], p[1])).ToList();
                points.AddRange(square);
                points.Add(square[0]);
                points.Add(corners[0]);
            }
            map.Areas.Add(info.ObjectFactory.CreateArea("CombatZone", points.ToArray()));
        }
    }
}
