#region License
// GridRoadGenerator.cs
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
using Genesis.Algorithms;
using Genesis.Core;
using Genesis.Layout;
using Genesis.Roads;
using Genesis.Tiles;

namespace Genesis.Processors.Grid
{
    /// <summary>
    /// Builds a road network along the inner streets of the grid layout and connects the roads of the inserted
    /// templates to it. The classic RoadGenerator connects every pair of road nodes with A*, which is far too slow
    /// for hundreds of templates and does not follow the streets.
    /// </summary>
    public class GridRoadGenerator : IMapProcessor
    {
        /// <summary>
        /// Gets or sets the maximum length of a single road segment in world units.
        /// </summary>
        public float SegmentLength { get; set; }

        /// <summary>
        /// Gets or sets the maximum distance (in world units) over which template road endpoints are connected to the network.
        /// </summary>
        public float TemplateConnectionRange { get; set; }

        private Map map;
        private MapInfo info;
        private TileGraph tileGraph;

        public GridRoadGenerator()
        {
            SegmentLength = 200f;
            TemplateConnectionRange = 300f;
        }

        public string Description
        {
            get { return "Generating roads (grid)"; }
        }

        public bool UpdatePreview
        {
            get { return false; }
        }

        public void Process(Map map, MapInfo info)
        {
            this.map = map;
            this.info = info;
            this.tileGraph = new TileGraph(info.Tiles, TileInfo.Structure | TileInfo.Cliff);

            var grid = info.Grid;
            var templateNodes = info.Roads.Nodes.Where(n => n.CreatedByTemplate && n.CanBeConnected && n.Neighbors.Count <= 1).ToList();
            var contested = GridResourceGenerator.ContestedSites ?? new List<Tuple<Point, float>>();

            float width = grid.PlayableWidth * Map.TileWidth;
            float height = grid.PlayableHeight * Map.TileWidth;
            float cell = grid.CellSize * Map.TileWidth;

            // vertical streets
            for (int i = 1; i < grid.Columns; i++)
            {
                var stops = Enumerable.Range(0, grid.Rows + 1).Select(j => new Point(i * cell, j * cell)).ToList();
                stops[0] = new Point(stops[0].X, -Map.TileWidth * 3);
                stops[stops.Count - 1] = new Point(stops[0].X, height + Map.TileWidth * 3);
                AddStreet(stops, contested);
            }

            // horizontal streets
            for (int j = 1; j < grid.Rows; j++)
            {
                var stops = Enumerable.Range(0, grid.Columns + 1).Select(i => new Point(i * cell, j * cell)).ToList();
                stops[0] = new Point(-Map.TileWidth * 3, stops[0].Y);
                stops[stops.Count - 1] = new Point(width + Map.TileWidth * 3, stops[0].Y);
                AddStreet(stops, contested);
            }

            ConnectTemplateRoads(templateNodes);
        }

        /// <summary>
        /// Adds one street consisting of the road sections between consecutive grid vertices. Vertices with a contested
        /// resource are left out, the road ends in front of the resource instead.
        /// </summary>
        private void AddStreet(IList<Point> vertices, IList<Tuple<Point, float>> contested)
        {
            for (int k = 0; k + 1 < vertices.Count; k++)
            {
                Point start = vertices[k];
                Point end = vertices[k + 1];
                var direction = (end - start).Normalize();

                var blockedStart = contested.FirstOrDefault(c => (c.Item1 - start).VectorLengthSquared() < 100f * 100f);
                var blockedEnd = contested.FirstOrDefault(c => (c.Item1 - end).VectorLengthSquared() < 100f * 100f);
                if (blockedStart != null)
                    start = start + direction * (blockedStart.Item2 + Map.TileWidth * 2);
                if (blockedEnd != null)
                    end = end - direction * (blockedEnd.Item2 + Map.TileWidth * 2);

                if ((end - start).VectorLength() < SegmentLength / 2)
                    continue;

                AddRoad(Subdivide(start, end));
            }
        }

        private IList<Point> Subdivide(Point start, Point end)
        {
            float length = (end - start).VectorLength();
            int count = System.Math.Max(1, (int)System.Math.Ceiling(length / SegmentLength));
            var points = new List<Point>(count + 1);
            for (int n = 0; n <= count; n++)
                points.Add(start + (end - start) * (n / (float)count));
            points[count] = end; // avoid rounding differences at the shared vertices
            return points;
        }

        private void AddRoad(IList<Point> points)
        {
            info.Roads.AddRoad(points, map, info.ObjectFactory, info.Settings.Scenery.Road);

            var bresenham = new Bresenham<Coordinates>((x, y) => new Coordinates(x, y));
            var tiles = new List<Coordinates>();
            for (int n = 1; n < points.Count; n++)
            {
                var a = map.PositionToCoordinates(points[n - 1]);
                var b = map.PositionToCoordinates(points[n]);
                tiles.AddRange(bresenham.GetCoordinates(a.X, a.Y, b.X, b.Y).Where(c => info.Tiles.CheckCoordinates(c.X, c.Y)));
            }
            new TileGraph(info.Tiles, TileInfo.Free).UpdateTiles(tiles, TileInfo.Road, 2);
        }

        /// <summary>
        /// Connects every open road end of the templates to the closest node of the street network with a straight road,
        /// if that road does not run through buildings.
        /// </summary>
        private void ConnectTemplateRoads(IList<RoadGraphNode> templateNodes)
        {
            var networkNodes = info.Roads.Nodes.Where(n => !n.CreatedByTemplate).ToList();
            if (networkNodes.Count == 0)
                return;

            foreach (var node in templateNodes)
            {
                var candidates = networkNodes
                    .Select(n => new { Node = n, Distance = (n.Position - node.Position).VectorLength() })
                    .Where(c => c.Distance <= TemplateConnectionRange && c.Distance > 1f)
                    .OrderBy(c => c.Distance);

                foreach (var candidate in candidates.Take(4))
                {
                    var points = Subdivide(node.Position, candidate.Node.Position);
                    if (tileGraph.GetTiles(map, points.Skip(1).Take(System.Math.Max(1, points.Count - 2)).ToArray()) == null && points.Count > 2)
                        continue;

                    AddRoad(points);
                    break;
                }
            }
        }
    }
}
