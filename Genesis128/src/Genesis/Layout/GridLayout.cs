#region License
// GridLayout.cs
//
// Copyright 2013 The CWC Team
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using CkMp.Data.Map;

namespace Genesis.Layout
{
    /// <summary>
    /// One start position ("slot") of a grid layout.
    /// </summary>
    public class GridSlot
    {
        /// <summary>
        /// Gets the 1-based start position number (Player_N_Start).
        /// </summary>
        public int Number { get; internal set; }

        public int Column { get; internal set; }
        public int Row { get; internal set; }

        /// <summary>
        /// Gets the center of the base in heightmap coordinates (including the border).
        /// </summary>
        public Coordinates Center { get; internal set; }

        /// <summary>
        /// Gets the tactical class (1..4) of this slot. The CWC skirmish scripts were written for four start positions;
        /// every slot behaves like the original start position with the same number as its class. The classes form a
        /// 2x2 pattern, so two slots of the same class are never direct (or diagonal) neighbors.
        /// </summary>
        public int TacticalClass { get; internal set; }
    }

    /// <summary>
    /// Describes a map divided into equally sized square cells. Each player gets its own cell with the base in the center;
    /// cell boundaries are kept free as streets so that every base can be reached from every other base.
    /// </summary>
    public class GridLayout
    {
        public int Players { get; private set; }
        public int Columns { get; private set; }
        public int Rows { get; private set; }

        /// <summary>
        /// Gets the side length of a cell in tiles.
        /// </summary>
        public int CellSize { get; private set; }

        /// <summary>
        /// Gets the radius of the flat base area in tiles.
        /// </summary>
        public int BaseRadius { get; private set; }

        /// <summary>
        /// Gets the size of the (unplayable) map border in tiles.
        /// </summary>
        public int Border { get; private set; }

        /// <summary>
        /// Gets the half width of streets and base access lanes in tiles.
        /// </summary>
        public int LaneHalfWidth { get; private set; }

        public IList<GridSlot> Slots { get; private set; }

        /// <summary>
        /// Gets the cells that have no player (used for neutral villages).
        /// </summary>
        public IList<Tuple<int, int>> EmptyCells { get; private set; }

        public int PlayableWidth { get { return Columns * CellSize; } }
        public int PlayableHeight { get { return Rows * CellSize; } }
        public int TotalWidth { get { return PlayableWidth + 2 * Border; } }
        public int TotalHeight { get { return PlayableHeight + 2 * Border; } }

        /// <summary>
        /// Gets the half side length of a cell in world units.
        /// </summary>
        public float HalfCellWorld { get { return CellSize * Map.TileWidth / 2f; } }

        private GridLayout()
        {
        }

        public static GridLayout Create(int players, int cellSize, int baseRadius, int border, int laneHalfWidth = 3)
        {
            if (players < 2)
                throw new ArgumentOutOfRangeException("players", "At least two players are required.");
            if (baseRadius * 2 + 4 * laneHalfWidth + 8 > cellSize)
                throw new ArgumentException(string.Format("A base radius of {0} tiles does not fit into cells of {1} tiles.", baseRadius, cellSize));

            int rows = (int)System.Math.Floor(System.Math.Sqrt(players));
            int columns = (players + rows - 1) / rows;

            var layout = new GridLayout()
            {
                Players = players,
                Columns = columns,
                Rows = rows,
                CellSize = cellSize,
                BaseRadius = baseRadius,
                Border = border,
                LaneHalfWidth = laneHalfWidth,
                Slots = new List<GridSlot>(),
                EmptyCells = new List<Tuple<int, int>>()
            };

            // Leave the cells that are farthest away from the center empty (the corners first).
            int spare = rows * columns - players;
            double cx = (columns - 1) / 2.0, cy = (rows - 1) / 2.0;
            var cells = from r in Enumerable.Range(0, rows)
                        from c in Enumerable.Range(0, columns)
                        select Tuple.Create(c, r);
            var empty = new HashSet<Tuple<int, int>>(cells
                .OrderByDescending(t => (t.Item1 - cx) * (t.Item1 - cx) + (t.Item2 - cy) * (t.Item2 - cy))
                .ThenBy(t => t.Item2).ThenBy(t => t.Item1)
                .Take(spare));

            int number = 1;
            foreach (var cell in cells)
            {
                if (empty.Contains(cell))
                {
                    layout.EmptyCells.Add(cell);
                    continue;
                }

                layout.Slots.Add(new GridSlot()
                {
                    Number = number++,
                    Column = cell.Item1,
                    Row = cell.Item2,
                    Center = layout.CellCenter(cell.Item1, cell.Item2),
                    TacticalClass = 1 + (cell.Item2 % 2) * 2 + (cell.Item1 % 2)
                });
            }

            return layout;
        }

        /// <summary>
        /// Gets the center of a cell in heightmap coordinates.
        /// </summary>
        public Coordinates CellCenter(int column, int row)
        {
            return new Coordinates(Border + column * CellSize + CellSize / 2, Border + row * CellSize + CellSize / 2);
        }

        /// <summary>
        /// Gets a grid vertex (cell corner) in heightmap coordinates.
        /// </summary>
        public Coordinates Vertex(int i, int j)
        {
            return new Coordinates(Border + i * CellSize, Border + j * CellSize);
        }

        public GridSlot GetSlot(int column, int row)
        {
            return Slots.FirstOrDefault(s => s.Column == column && s.Row == row);
        }

        /// <summary>
        /// Gets the directions of the four base access spokes (north, east, south, west) as unit vectors.
        /// </summary>
        public static readonly Point[] SpokeDirections = new Point[]
        {
            new Point(0, -1),
            new Point(1, 0),
            new Point(0, 1),
            new Point(-1, 0)
        };
    }
}
