#region License
// ConnectivityValidator.cs
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
using Genesis.Core;
using Genesis.Tiles;
using Genesis.Tools;

namespace Genesis.Processors.Grid
{
    /// <summary>
    /// Verifies that ground units can reach every start position, every AI path waypoint and every resource from
    /// start position 1. Passability is what the engine uses: the impassable ("cliff") flags of the tiles
    /// (WorldHeightMap::getCliffState) plus the tiles blocked by template structures.
    /// Unreachable targets are connected by cutting a ramp through the blocking mountain.
    /// </summary>
    public class ConnectivityValidator : IMapProcessor
    {
        private const TileInfo blockingTiles = TileInfo.Structure;

        public static List<string> Report { get; private set; }

        public int MaxRepairs { get; set; }

        public ConnectivityValidator()
        {
            MaxRepairs = 500;
        }

        public string Description
        {
            get { return "Validating ground connectivity"; }
        }

        public bool UpdatePreview
        {
            get { return true; }
        }

        public void Process(Map map, MapInfo info)
        {
            Report = new List<string>();

            var targets = new List<Tuple<string, Coordinates>>();
            for (int i = 0; i < info.StartingPositions.Length; i++)
                targets.Add(Tuple.Create("Player_" + (i + 1) + "_Start", info.StartingPositions[i]));
            if (GridSkirmishAIGenerator.PathWaypoints != null)
                foreach (var w in GridSkirmishAIGenerator.PathWaypoints)
                    targets.Add(Tuple.Create(w.Item1, map.PositionToCoordinates(new Point(w.Item2.X, w.Item2.Y))));
            if (GridResourceGenerator.Placed != null)
                foreach (var r in GridResourceGenerator.Placed)
                    targets.Add(Tuple.Create((r.IsFlag ? "flag" : "fuel") + " (" + r.Role + ")", map.PositionToCoordinates(new Point(r.Object.X, r.Object.Y))));

            int repairs = 0;
            var ramp = new RampGenerator(null);
            while (true)
            {
                var reachable = Flood(map, info, info.StartingPositions[0]);
                var unreachable = targets.Where(t => !IsReachable(reachable, map, t.Item2)).ToList();
                if (unreachable.Count == 0)
                    break;

                if (repairs >= MaxRepairs)
                {
                    foreach (var t in unreachable.Take(20))
                        Report.Add("UNREACHABLE: " + t.Item1 + " at " + t.Item2);
                    Report.Add(string.Format("{0} targets unreachable after {1} repairs", unreachable.Count, repairs));
                    return;
                }

                // Repair the first unreachable target: cut a ramp to the nearest reachable tile.
                var target = unreachable[0].Item2;
                var nearest = NearestReachable(reachable, map, target);
                if (nearest == null)
                {
                    Report.Add("UNREACHABLE (no reachable tile found): " + unreachable[0].Item1);
                    return;
                }

                Cut(map, info, ramp, target, nearest.Value);
                repairs++;
            }

            Report.Add(string.Format("All {0} targets (start positions, AI path waypoints, resources) are reachable; {1} mountain passes were cut.", targets.Count, repairs));
        }

        private static bool Passable(Map map, MapInfo info, int x, int y)
        {
            return !map.Tiles[x, y].Impassable && (info.Tiles[x, y] & blockingTiles) == TileInfo.Free;
        }

        private static bool[] Flood(Map map, MapInfo info, Coordinates start)
        {
            int width = map.Tiles.Width, height = map.Tiles.Height;
            var reachable = new bool[width * height];
            var queue = new Queue<int>();
            int s = map.Tiles.Index(start.X, start.Y);
            reachable[s] = true;
            queue.Enqueue(s);

            int border = map.HeightMap.Border;
            while (queue.Count > 0)
            {
                int index = queue.Dequeue();
                int x = index % width, y = index / width;
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < border || ny < border || nx >= width - border || ny >= height - border)
                            continue;
                        int n = ny * width + nx;
                        if (reachable[n] || !Passable(map, info, nx, ny))
                            continue;
                        // no diagonal squeezing between two blocked tiles
                        if (dx != 0 && dy != 0 && !Passable(map, info, x + dx, y) && !Passable(map, info, x, y + dy))
                            continue;
                        reachable[n] = true;
                        queue.Enqueue(n);
                    }
                }
            }
            return reachable;
        }

        /// <summary>
        /// A target counts as reachable if it or one of the tiles within two tiles around it is reachable
        /// (resources themselves stand on blocked tiles).
        /// </summary>
        private static bool IsReachable(bool[] reachable, Map map, Coordinates c)
        {
            int width = map.Tiles.Width;
            for (int r = 0; r <= 12; r++)
                for (int dx = -r; dx <= r; dx++)
                    for (int dy = -r; dy <= r; dy++)
                        if (map.Tiles.CheckCoordinates(c.X + dx, c.Y + dy) && reachable[(c.Y + dy) * width + c.X + dx])
                            return true;
            return false;
        }

        private static Coordinates? NearestReachable(bool[] reachable, Map map, Coordinates c)
        {
            int width = map.Tiles.Width;
            for (int r = 1; r < System.Math.Max(map.Tiles.Width, map.Tiles.Height); r++)
            {
                Coordinates? best = null;
                int bestDistance = int.MaxValue;
                for (int dx = -r; dx <= r; dx++)
                {
                    for (int dy = -r; dy <= r; dy++)
                    {
                        if (System.Math.Max(System.Math.Abs(dx), System.Math.Abs(dy)) != r)
                            continue;
                        int x = c.X + dx, y = c.Y + dy;
                        if (map.Tiles.CheckCoordinates(x, y) && reachable[y * width + x] && dx * dx + dy * dy < bestDistance)
                        {
                            best = new Coordinates(x, y);
                            bestDistance = dx * dx + dy * dy;
                        }
                    }
                }
                if (best != null)
                    return best;
            }
            return null;
        }

        private static void Cut(Map map, MapInfo info, RampGenerator ramp, Coordinates from, Coordinates to)
        {
            Point a = map.CoordinatesToPosition(from);
            Point b = map.CoordinatesToPosition(to);
            var direction = (b - a).Normalize();
            // extend a little into the reachable area so that the pass is open at both ends
            b = b + direction * Map.TileWidth * 3;
            a = a - direction * Map.TileWidth * 2;

            ramp.GenerateRamp(map, a, b, 25f, 15f);

            var bresenham = new Genesis.Algorithms.Bresenham<Coordinates>((x, y) => new Coordinates(x, y));
            var start = map.PositionToCoordinates(a);
            var end = map.PositionToCoordinates(b);
            foreach (var c in bresenham.GetCoordinates(start.X, start.Y, end.X, end.Y))
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    for (int dy = -2; dy <= 2; dy++)
                    {
                        int x = c.X + dx, y = c.Y + dy;
                        if (!map.Tiles.CheckCoordinates(x, y))
                            continue;
                        map.Tiles[x, y].Impassable = false;
                        info.Tiles[x, y] &= ~TileInfo.Cliff;
                        info.Tiles[x, y] |= TileInfo.Lane;
                    }
                }
            }
        }
    }
}
