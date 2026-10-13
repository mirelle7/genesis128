#region License
// GridStartingPositionGenerator.cs
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
#endregion

using System;
using System.Linq;
using CkMp.Data.Map;
using Genesis.Core;
using Genesis.Layout;
using Genesis.Tiles;

namespace Genesis.Processors.Grid
{
    /// <summary>
    /// Places one start position in the center of every occupied cell of the grid layout.
    /// Replaces the classic StartingPositionGenerator, which puts all players on a single ring and cannot fit
    /// more than a handful of bases.
    /// </summary>
    public class GridStartingPositionGenerator : IMapProcessor
    {
        public string Description
        {
            get { return "Generating starting positions (grid)"; }
        }

        public bool UpdatePreview
        {
            get { return true; }
        }

        public void Process(Map map, MapInfo info)
        {
            var grid = info.Grid;
            if (grid == null)
                throw new InvalidOperationException("The grid layout has not been set.");
            if (grid.Slots.Count != info.NumberOfPlayers)
                throw new InvalidOperationException("The grid layout does not match the number of players.");

            info.BaseRadius = grid.BaseRadius;

            int r = grid.BaseRadius;
            foreach (var slot in grid.Slots)
            {
                info.StartingPositions[slot.Number - 1] = slot.Center;

                for (int x = slot.Center.X - r; x <= slot.Center.X + r; x++)
                {
                    for (int y = slot.Center.Y - r; y <= slot.Center.Y + r; y++)
                    {
                        int dx = x - slot.Center.X, dy = y - slot.Center.Y;
                        if (dx * dx + dy * dy < r * r)
                            info.Tiles[x, y] |= TileInfo.Base;
                    }
                }
            }

            foreach (var slot in grid.Slots)
            {
                Point position = map.CoordinatesToPosition(slot.Center);
                map.Objects.Add(info.ObjectFactory.CreateWaypoint(position.X, position.Y, string.Format("Player_{0}_Start", slot.Number)));
            }
        }
    }
}
