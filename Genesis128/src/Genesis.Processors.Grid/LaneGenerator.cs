#region License
// LaneGenerator.cs
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
#endregion

using CkMp.Data.Map;
using Genesis.Core;
using Genesis.Layout;
using Genesis.Tiles;

namespace Genesis.Processors.Grid
{
    /// <summary>
    /// Reserves the movement corridors of the grid layout: streets along every cell boundary (including a ring along
    /// the map edge) and four access spokes from every base to the surrounding streets. Mountains, trees, rocks and
    /// resources are kept off these tiles, which guarantees that every base, every resource and every AI approach
    /// path can be reached by ground units.
    /// </summary>
    public class LaneGenerator : IMapProcessor
    {
        public string Description
        {
            get { return "Reserving streets and base access lanes"; }
        }

        public bool UpdatePreview
        {
            get { return false; }
        }

        public void Process(Map map, MapInfo info)
        {
            var grid = info.Grid;
            int w = grid.LaneHalfWidth;
            int minX = grid.Border + w + 1, maxX = grid.Border + grid.PlayableWidth - w - 2;
            int minY = grid.Border + w + 1, maxY = grid.Border + grid.PlayableHeight - w - 2;

            // Vertical streets.
            for (int i = 0; i <= grid.Columns; i++)
            {
                int x = Clamp(grid.Vertex(i, 0).X, minX, maxX);
                Mark(info, x - w, minY - w, x + w, maxY + w);
            }

            // Horizontal streets.
            for (int j = 0; j <= grid.Rows; j++)
            {
                int y = Clamp(grid.Vertex(0, j).Y, minY, maxY);
                Mark(info, minX - w, y - w, maxX + w, y + w);
            }

            // Access spokes from every base to the four surrounding streets.
            int half = grid.CellSize / 2;
            foreach (var slot in grid.Slots)
            {
                var c = slot.Center;
                Mark(info, c.X - w, c.Y - half, c.X + w, c.Y + half);
                Mark(info, c.X - half, c.Y - w, c.X + half, c.Y + w);
            }
        }

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        private static void Mark(MapInfo info, int x1, int y1, int x2, int y2)
        {
            for (int x = x1; x <= x2; x++)
                for (int y = y1; y <= y2; y++)
                    if (info.Tiles.CheckCoordinates(x, y))
                        info.Tiles[x, y] |= TileInfo.Lane;
        }
    }
}
