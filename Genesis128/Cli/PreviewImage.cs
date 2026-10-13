using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using CkMp.Data.Map;
using CkMp.Data.Objects;
using Genesis.Core;
using Genesis.Processors.Grid;
using Genesis.Tiles;

namespace Genesis.Cli
{
    /// <summary>
    /// Map previews: the small .tga the game shows in the map list, and a large annotated overview image.
    /// </summary>
    public static class PreviewImage
    {
        /// <summary>
        /// Creates an uncompressed 32 bit TGA preview whose longer side is at most <paramref name="maxSize"/> pixels.
        /// (The original generator wrote the preview in full map size, which is far too large for maps of this size.)
        /// </summary>
        public static byte[] CreateTga(Map map, MapInfo info, int maxSize)
        {
            int mapWidth = map.HeightMap.MapWidth, mapHeight = map.HeightMap.MapHeight;
            float scale = System.Math.Max(mapWidth, mapHeight) / (float)maxSize;
            int width = System.Math.Max(1, (int)(mapWidth / scale));
            int height = System.Math.Max(1, (int)(mapHeight / scale));
            int border = map.HeightMap.Border;

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((byte)2); // uncompressed true color
                writer.Write((ushort)0);
                writer.Write((ushort)0);
                writer.Write((byte)0);
                writer.Write((ushort)0);
                writer.Write((ushort)0);
                writer.Write((ushort)width);
                writer.Write((ushort)height);
                writer.Write((byte)32);
                writer.Write((byte)0);

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int mx = border + (int)(x * scale), my = border + (int)(y * scale);
                        var color = TileColor(map, info, mx, my);
                        writer.Write(color.B);
                        writer.Write(color.G);
                        writer.Write(color.R);
                        writer.Write((byte)255);
                    }
                }
                return stream.ToArray();
            }
        }

        private static Color TileColor(Map map, MapInfo info, int x, int y)
        {
            byte h = map.HeightMap[x, y];
            var tile = info.Tiles[x, y];
            if (tile.HasFlag(TileInfo.Structure))
                return Color.FromArgb(230, 200, 90);
            if (map.Tiles[x, y].Impassable)
                return Color.FromArgb(90 + h / 4, 60, 50);
            if (tile.HasFlag(TileInfo.Road))
                return Color.FromArgb(150, 150, 150);
            if (tile.HasFlag(TileInfo.Base))
                return Color.FromArgb(70 + h / 5, 110 + h / 5, 70 + h / 5);
            return Color.FromArgb(40 + h / 4, 90 + h / 4, 40 + h / 4);
        }

        /// <summary>
        /// Creates an annotated overview (bases colored by tactical class, start numbers, AI paths, resources).
        /// </summary>
        public static byte[] CreateLayoutPng(Map map, MapInfo info, int pixelsPerTile)
        {
            int border = map.HeightMap.Border;
            int width = map.HeightMap.MapWidth, height = map.HeightMap.MapHeight;
            float scale = pixelsPerTile / Map.TileWidth;
            var classColors = new[] { Color.Red, Color.DodgerBlue, Color.Gold, Color.MediumOrchid };

            using (var bitmap = new Bitmap(width * pixelsPerTile, height * pixelsPerTile, PixelFormat.Format24bppRgb))
            {
                var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                var row = new byte[data.Stride];
                for (int py = 0; py < bitmap.Height; py++)
                {
                    for (int px = 0; px < bitmap.Width; px++)
                    {
                        var c = TileColor(map, info, border + px / pixelsPerTile, border + py / pixelsPerTile);
                        row[px * 3] = c.B;
                        row[px * 3 + 1] = c.G;
                        row[px * 3 + 2] = c.R;
                    }
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + py * data.Stride, data.Stride);
                }
                bitmap.UnlockBits(data);

                using (var g = Graphics.FromImage(bitmap))
                using (var font = new Font("Arial", System.Math.Max(8, pixelsPerTile * 3), FontStyle.Bold))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                    // trees and rocks as small dots
                    foreach (var o in map.Objects.Where(o => !o.IsWaypoint && o.RoadOptions == RoadOptions.None))
                    {
                        if (info.Settings.Scenery.Trees.Contains(o.Type))
                            g.FillRectangle(Brushes.DarkGreen, o.X * scale, o.Y * scale, 2, 2);
                    }

                    // AI paths
                    var waypoints = map.Objects.Where(o => o.IsWaypoint).ToDictionary(o => (int)o.Properties["waypointID"].Value);
                    foreach (var link in map.Paths)
                    {
                        ScriptObject a, b;
                        if (!waypoints.TryGetValue(link.Item1, out a) || !waypoints.TryGetValue(link.Item2, out b))
                            continue;
                        string label = a.GetPropertyValue<string>("waypointPathLabel1") ?? string.Empty;
                        var pen = label.StartsWith("Center") ? Pens.OrangeRed : label.StartsWith("Flank") ? Pens.Cyan : label.StartsWith("Backdoor") ? Pens.Magenta : Pens.White;
                        g.DrawLine(pen, a.X * scale, a.Y * scale, b.X * scale, b.Y * scale);
                    }

                    // resources
                    foreach (var r in GridResourceGenerator.Placed)
                    {
                        float x = r.Object.X * scale, y = r.Object.Y * scale, s = r.Role == ResourceRole.Own ? 6 : 10;
                        g.FillEllipse(r.IsFlag ? Brushes.Yellow : Brushes.Black, x - s / 2, y - s / 2, s, s);
                        g.DrawEllipse(Pens.White, x - s / 2, y - s / 2, s, s);
                    }

                    // bases
                    foreach (var slot in info.Grid.Slots)
                    {
                        var p = map.CoordinatesToPosition(slot.Center);
                        float r = info.Grid.BaseRadius * Map.TileWidth * scale;
                        using (var pen = new Pen(classColors[slot.TacticalClass - 1], 3))
                            g.DrawEllipse(pen, p.X * scale - r, p.Y * scale - r, 2 * r, 2 * r);
                        var text = slot.Number.ToString();
                        var size = g.MeasureString(text, font);
                        g.DrawString(text, font, Brushes.Black, p.X * scale - size.Width / 2 + 1, p.Y * scale - size.Height / 2 + 1);
                        g.DrawString(text, font, Brushes.White, p.X * scale - size.Width / 2, p.Y * scale - size.Height / 2);
                    }
                }

                using (var stream = new MemoryStream())
                {
                    bitmap.Save(stream, ImageFormat.Png);
                    return stream.ToArray();
                }
            }
        }
    }
}
