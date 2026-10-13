#region License
// GridResourceGenerator.cs
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CkMp.Data.Map;
using CkMp.Data.Objects;
using Genesis.Core;
using Genesis.Layout;
using Genesis.Objects;
using Genesis.Tiles;

namespace Genesis.Processors.Grid
{
    /// <summary>
    /// The role of a generated resource. Used later by the skirmish AI generator to decide which resources the
    /// AI scripts track by name.
    /// </summary>
    public enum ResourceRole
    {
        Own,
        Contested,
        Village
    }

    /// <summary>
    /// A resource object (flag or fuel depot) placed by the <see cref="GridResourceGenerator"/>.
    /// </summary>
    public class PlacedResource
    {
        public ScriptObject Object { get; set; }
        public bool IsFlag { get; set; }
        public ResourceRole Role { get; set; }

        /// <summary>
        /// Gets or sets the start position this resource belongs to (only for <see cref="ResourceRole.Own"/>).
        /// </summary>
        public int Owner { get; set; }
    }

    /// <summary>
    /// Places resources for the grid layout:
    ///  * every player gets the same small flag and small fuel depot at the same relative position in its own cell,
    ///  * every other inner grid vertex gets a large contested resource that is equally far away from four bases,
    ///  * the empty cells (map corners) get a neutral village.
    /// </summary>
    public class GridResourceGenerator : IMapProcessor
    {
        private const string objectName = "objectName";

        /// <summary>
        /// Templates larger than this (in tiles) do not fit into the pocket between base and streets.
        /// </summary>
        private const int maxOwnTemplateSize = 12;

        private Map map;
        private MapInfo info;
        private Random random;

        private ObjectTemplate[] smallFlags, largeFlags, smallFuelDepots, largeFuelDepots, villages;

        /// <summary>
        /// Gets the resources placed by the last run. Shared with later processors through this static list because
        /// processors are independent objects.
        /// </summary>
        public static List<PlacedResource> Placed { get; private set; }

        /// <summary>
        /// Gets the positions of the contested resources in world coordinates (used by the road generator).
        /// </summary>
        public static List<Tuple<Point, float>> ContestedSites { get; private set; }

        /// <summary>
        /// Gets or sets the object types used instead of the CWC templates (vanilla Zero Hour target).
        /// If null, the CWC templates from the Templates folder are used.
        /// </summary>
        public ZeroHourResources ZeroHour { get; set; }

        public bool PlaceContestedResources { get; set; }
        public bool PlaceVillages { get; set; }

        public GridResourceGenerator()
        {
            PlaceContestedResources = true;
            PlaceVillages = true;
        }

        public string Description
        {
            get { return "Generating resources (grid)"; }
        }

        public bool UpdatePreview
        {
            get { return true; }
        }

        public void Process(Map map, MapInfo info)
        {
            this.map = map;
            this.info = info;
            this.random = new Random(info.MapId);
            Placed = new List<PlacedResource>();
            ContestedSites = new List<Tuple<Point, float>>();

            var grid = info.Grid;
            if (ZeroHour != null)
            {
                PlaceZeroHourResources(grid);
                return;
            }

            LoadTemplates();

            // Own resources: identical template, rotation and offset for every player => fair.
            float pocket = OwnResourceOffset(grid) * Map.TileWidth;
            var ownFlag = Pick(smallFlags.Where(t => Fits(t)).DefaultIfEmpty(smallFlags[0]).ToArray());
            var ownFuel = Pick(smallFuelDepots.Where(t => Fits(t)).DefaultIfEmpty(smallFuelDepots[0]).ToArray());
            float flagRotation = (float)(random.Next(4) * System.Math.PI / 2);
            float fuelRotation = (float)(random.Next(4) * System.Math.PI / 2);

            foreach (var slot in grid.Slots)
            {
                if (info.IsCancellationRequested)
                    return;

                Point center = map.CoordinatesToPosition(slot.Center);
                // flag in the north-east pocket, fuel depot in the south-west pocket of the cell
                Place(ownFlag, center + new Point(pocket, -pocket), flagRotation, true, ResourceRole.Own, slot.Number);
                Place(ownFuel, center + new Point(-pocket, pocket), fuelRotation, false, ResourceRole.Own, slot.Number);
            }

            // Contested resources on every other inner grid vertex (equally far away from the four adjacent bases).
            if (PlaceContestedResources)
            {
                for (int i = 1; i < grid.Columns; i++)
                {
                    for (int j = 1; j < grid.Rows; j++)
                    {
                        if ((i + j) % 2 != 0)
                            continue;

                        // only between occupied cells
                        bool surrounded = grid.GetSlot(i - 1, j - 1) != null && grid.GetSlot(i, j - 1) != null &&
                                          grid.GetSlot(i - 1, j) != null && grid.GetSlot(i, j) != null;
                        if (!surrounded)
                            continue;

                        bool isFlag = (i / 2 + j / 2) % 2 == 0;
                        var template = Pick(isFlag ? largeFlags : largeFuelDepots);
                        Point position = map.CoordinatesToPosition(grid.Vertex(i, j)) - Map.TileWidth / 2f;
                        Place(template, position, (float)(random.Next(4) * System.Math.PI / 2), isFlag, ResourceRole.Contested, 0);
                        ContestedSites.Add(Tuple.Create(position, TemplateRadius(template)));
                    }
                }
            }

            // Villages in the empty cells.
            if (PlaceVillages)
            {
                var available = villages.ToList();
                foreach (var cell in grid.EmptyCells)
                {
                    if (available.Count == 0)
                        available = villages.ToList();
                    var template = available[random.Next(available.Count)];
                    available.Remove(template);
                    Point position = map.CoordinatesToPosition(grid.CellCenter(cell.Item1, cell.Item2));
                    PlaceVillage(template, position, (float)(random.Next(4) * System.Math.PI / 2));
                }
            }
        }

        /// <summary>
        /// Vanilla Zero Hour: the same positions as for CWC, but single supply objects instead of the CWC templates.
        /// </summary>
        private void PlaceZeroHourResources(GridLayout grid)
        {
            float pocket = OwnResourceOffset(grid) * Map.TileWidth;
            foreach (var slot in grid.Slots)
            {
                Point center = map.CoordinatesToPosition(slot.Center);
                PlaceObject(ZeroHour.Own1, center + new Point(pocket, -pocket), true, ResourceRole.Own, slot.Number, ZeroHour.Own1Radius);
                PlaceObject(ZeroHour.Own2, center + new Point(-pocket, pocket), false, ResourceRole.Own, slot.Number, ZeroHour.Own2Radius);
            }

            if (PlaceContestedResources)
            {
                for (int i = 1; i < grid.Columns; i++)
                {
                    for (int j = 1; j < grid.Rows; j++)
                    {
                        if ((i + j) % 2 != 0)
                            continue;
                        bool surrounded = grid.GetSlot(i - 1, j - 1) != null && grid.GetSlot(i, j - 1) != null &&
                                          grid.GetSlot(i - 1, j) != null && grid.GetSlot(i, j) != null;
                        if (!surrounded)
                            continue;

                        bool first = (i / 2 + j / 2) % 2 == 0;
                        Point position = map.CoordinatesToPosition(grid.Vertex(i, j)) - Map.TileWidth / 2f;
                        float radius = first ? ZeroHour.Contested1Radius : ZeroHour.Contested2Radius;
                        PlaceObject(first ? ZeroHour.Contested1 : ZeroHour.Contested2, position, first, ResourceRole.Contested, 0, radius);
                        ContestedSites.Add(Tuple.Create(position, radius * Map.TileWidth));
                    }
                }
            }

            if (PlaceVillages)
            {
                foreach (var cell in grid.EmptyCells)
                    PlaceObject(ZeroHour.Contested1, map.CoordinatesToPosition(grid.CellCenter(cell.Item1, cell.Item2)), true, ResourceRole.Village, 0, ZeroHour.Contested1Radius);
            }
        }

        private void PlaceObject(string type, Point position, bool isFlag, ResourceRole role, int owner, float radiusInTiles)
        {
            var obj = info.ObjectFactory.CreateObject(type, position.X, position.Y, 0, (float)(System.Math.PI / 4));
            map.Objects.Add(obj);
            Placed.Add(new PlacedResource() { Object = obj, IsFlag = isFlag, Role = role, Owner = owner });

            UpdateTileInfo(position, (int)System.Math.Ceiling(radiusInTiles), TileInfo.Structure);
            UpdateTileInfo(position, info.ResourceRadiusNear + (int)radiusInTiles, isFlag ? TileInfo.FlagNear : TileInfo.FuelDepotNear);
            info.FlattenedAreas.Add(new Genesis.Math.Circle(position, (radiusInTiles + 2) * Map.TileWidth));
        }

        /// <summary>
        /// Gets the offset (along both axes, in tiles) of the own resources from the base center: the middle of the
        /// pocket between the base circle and the streets.
        /// </summary>
        public static float OwnResourceOffset(GridLayout grid)
        {
            float inner = grid.BaseRadius / (float)System.Math.Sqrt(2) + maxOwnTemplateSize / 2f;
            float outer = grid.CellSize / 2f - grid.LaneHalfWidth - 1 - maxOwnTemplateSize / 2f;
            return System.Math.Max(inner, (inner + outer) / 2f);
        }

        private static bool Fits(ObjectTemplate template)
        {
            return template.Tiles.Width <= maxOwnTemplateSize && template.Tiles.Height <= maxOwnTemplateSize;
        }

        private static float TemplateRadius(ObjectTemplate template)
        {
            return System.Math.Max(template.Tiles.Width, template.Tiles.Height) * Map.TileWidth * 0.75f;
        }

        private ObjectTemplate Pick(ObjectTemplate[] templates)
        {
            return templates[random.Next(templates.Length)];
        }

        private void LoadTemplates()
        {
            smallFlags = Load("Templates/SmallFlag");
            largeFlags = Load("Templates/LargeFlag");
            smallFuelDepots = Load("Templates/SmallFuelDepot");
            largeFuelDepots = Load("Templates/LargeFuelDepot");
            villages = Load("Templates/Village");
        }

        private static ObjectTemplate[] Load(string directory)
        {
            var templates = Directory.GetFiles(directory, "*.map").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Select(ObjectTemplate.ReadFromFile).ToArray();
            if (templates.Length == 0)
                throw new InvalidOperationException("No templates found in " + directory);
            return templates;
        }

        private void Place(ObjectTemplate template, Point position, float rotation, bool isFlag, ResourceRole role, int owner)
        {
            var clone = template.Clone();
            info.ObjectFactory.AddTemplateToMap(clone, map, info, TileInfo.Structure, position, rotation, info.Settings.Scenery.Road);

            var resource = clone.Objects.FirstOrDefault(o => isFlag ? o.IsFlag() : o.IsFuelDepot());
            if (resource == null)
                throw new InvalidOperationException("Template without resource: " + template.Map.GetPropertyValue<string>("mapName"));

            Register(clone, resource, isFlag, role, owner);
        }

        private void PlaceVillage(ObjectTemplate template, Point position, float rotation)
        {
            var clone = template.Clone();
            info.ObjectFactory.AddTemplateToMap(clone, map, info, TileInfo.Structure, position, rotation, info.Settings.Scenery.Road);

            foreach (var resource in clone.Objects.Where(o => o.IsFlag() || o.IsFuelDepot()).ToArray())
                Register(clone, resource, resource.IsFlag(), ResourceRole.Village, 0);
            StripNames(clone, null);
        }

        private void Register(ObjectTemplate clone, ScriptObject resource, bool isFlag, ResourceRole role, int owner)
        {
            StripNames(clone, resource);

            Placed.Add(new PlacedResource() { Object = resource, IsFlag = isFlag, Role = role, Owner = owner });

            var position = new Point(resource.X, resource.Y);
            UpdateTileInfo(position, info.ResourceRadiusNear, isFlag ? TileInfo.FlagNear : TileInfo.FuelDepotNear);
        }

        /// <summary>
        /// Template objects are named "flag"/"fuel"; the names are assigned later by the skirmish AI generator.
        /// Names on anything that is not a resource (e.g. a car in one of the village templates) would collide.
        /// </summary>
        private static void StripNames(ObjectTemplate template, ScriptObject keep)
        {
            foreach (var obj in template.Objects)
            {
                if (obj == keep || obj.IsFlag() || obj.IsFuelDepot())
                    continue;
                obj.Properties.Remove(objectName);
            }
        }

        private void UpdateTileInfo(Point position, int radius, TileInfo tileInfo)
        {
            var coordinates = map.PositionToCoordinates(position);
            for (int x = coordinates.X - radius; x <= coordinates.X + radius; x++)
            {
                for (int y = coordinates.Y - radius; y <= coordinates.Y + radius; y++)
                {
                    int dx = x - coordinates.X, dy = y - coordinates.Y;
                    if (info.Tiles.CheckCoordinates(x, y) && dx * dx + dy * dy <= radius * radius)
                        info.Tiles[x, y] |= tileInfo;
                }
            }
        }
    }
}

namespace Genesis.Processors.Grid
{
    /// <summary>
    /// Object types for the vanilla Zero Hour target (radii in tiles, used to keep trees and roads away).
    /// </summary>
    public class ZeroHourResources
    {
        public string Own1 = "SupplyDock";
        public float Own1Radius = 4;
        public string Own2 = "TechOilDerrick";
        public float Own2Radius = 3;
        public string Contested1 = "SupplyWarehouse";
        public float Contested1Radius = 5;
        public string Contested2 = "TechOilDerrick";
        public float Contested2Radius = 3;
    }
}
