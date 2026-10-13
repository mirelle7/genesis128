using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Serialization;
using CkMp.Data;
using CkMp.Data.Enumerations;
using CkMp.Data.Lighting;
using CkMp.Data.Map;
using CkMp.Data.Objects;
using Genesis.Core;
using Genesis.Layout;
using Genesis.Previews;
using Genesis.Processors;
using Genesis.Processors.CWC;
using Genesis.Processors.Grid;
using Genesis.Settings;
using Genesis.Tools;

namespace Genesis.Cli
{
    /// <summary>
    /// Command line front end of the grid based CWC map generator.
    ///
    ///   Genesis128 [generate] [--players 128] [--cell 76] [--base-radius 24] [--border 30] [--scenery Woodland]
    ///              [--cliffs Low|Medium|High] [--trees Low|Medium|High] [--max-trees 15000] [--time Morning|Afternoon|Evening|Night]
    ///              [--tracked-resources 24] [--target cwc|zh] [--seed N] [--name NAME] [--out DIR]
    ///   Genesis128 dump FILE        print the scripts of a .map/.scb file
    ///   Genesis128 verify FILE      read a generated map back and check the AI data for every start position
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            // Template, script and scenery paths are relative to the executable.
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);

            try
            {
                string command = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "generate";
                var options = ParseOptions(args.SkipWhile(a => !a.StartsWith("--")).ToArray());

                switch (command)
                {
                    case "dump":
                        ScriptDump.Dump(args[1], Console.Out);
                        return 0;
                    case "verify":
                        return MapVerifier.Verify(args[1], Console.Out) ? 0 : 2;
                    case "generate":
                        return Generate(options);
                    default:
                        Console.Error.WriteLine("Unknown command: " + command);
                        return 1;
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 1;
            }
        }

        private static Dictionary<string, string> ParseOptions(string[] args)
        {
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--"))
                    throw new ArgumentException("Unexpected argument: " + args[i]);
                string key = args[i].Substring(2);
                string value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
                options[key] = value;
            }
            return options;
        }

        private static string Get(Dictionary<string, string> options, string key, string fallback)
        {
            string value;
            return options.TryGetValue(key, out value) ? value : fallback;
        }

        private static int Generate(Dictionary<string, string> options)
        {
            int players = int.Parse(Get(options, "players", "128"));
            int cell = int.Parse(Get(options, "cell", "76"));
            int baseRadius = int.Parse(Get(options, "base-radius", "24"));
            int border = int.Parse(Get(options, "border", "30"));
            int seed = int.Parse(Get(options, "seed", new Random().Next().ToString()));
            int tracked = int.Parse(Get(options, "tracked-resources", "24"));
            int maxTrees = int.Parse(Get(options, "max-trees", "15000"));
            var cliffs = (Frequency)Enum.Parse(typeof(Frequency), Get(options, "cliffs", "Medium"), true);
            var trees = (Frequency)Enum.Parse(typeof(Frequency), Get(options, "trees", "Medium"), true);
            var time = (TimeOfDay)Enum.Parse(typeof(TimeOfDay), Get(options, "time", "Afternoon"), true);
            string sceneryName = Get(options, "scenery", "Woodland");
            bool zeroHour = string.Equals(Get(options, "target", "cwc"), "zh", StringComparison.OrdinalIgnoreCase);
            string name = Get(options, "name", string.Format("CWC {0} Players Grid {1}", players, seed));
            string output = Path.GetFullPath(Get(options, "out", Path.Combine(AppContext.BaseDirectory, "Maps")));

            var grid = GridLayout.Create(players, cell, baseRadius, border);
            var layout = LoadLayouts().First();
            var scenery = LoadScenery(sceneryName);
            if (zeroHour)
            {
                // Vanilla Zero Hour has no CWC trees; the textures, rocks and roads of the sceneries exist in both.
                scenery.Trees = new List<string>() { "TreeDogwood1", "TreeDogwood1", "TreeFir01B", "TreeFir01B", "TreeCherryBlossom01" };
            }
            var settings = new MapSettings(layout, scenery, cliffs, trees, time);

            Console.WriteLine("Genesis128 - CWC map generator for {0} players", players);
            Console.WriteLine("  grid {0} x {1} cells of {2} tiles, base radius {3}, empty cells {4}", grid.Columns, grid.Rows, cell, baseRadius, grid.EmptyCells.Count);
            Console.WriteLine("  map {0} x {1} tiles (playable {2} x {3}), scenery {4}, seed {5}", grid.TotalWidth, grid.TotalHeight, grid.PlayableWidth, grid.PlayableHeight, scenery.Name, seed);

            var map = CreateMap(grid, settings);
            var info = new MapInfo(seed, map.HeightMap.Width, map.HeightMap.Height, settings, players, CancellationToken.None);
            info.Grid = grid;
            info.BaseRadius = baseRadius;

            var preview = new PreviewGenerator();
            var stopwatch = Stopwatch.StartNew();
            foreach (var processor in CreatePipeline(tracked, maxTrees, zeroHour))
            {
                var step = Stopwatch.StartNew();
                Console.Write("  {0,-52}", processor.Description + "...");
                processor.Process(map, info);
                Console.WriteLine(" {0,6:0.0}s", step.Elapsed.TotalSeconds);
            }
            preview.Process(map, info);

            if (options.ContainsKey("self-test-validator"))
            {
                // Debug aid: wall off start position 2 with an impassable ring and let the validator repair it.
                var c = info.StartingPositions[1];
                int r = grid.BaseRadius + 3;
                for (int x = c.X - r - 1; x <= c.X + r + 1; x++)
                    for (int y = c.Y - r - 1; y <= c.Y + r + 1; y++)
                    {
                        int d2 = (x - c.X) * (x - c.X) + (y - c.Y) * (y - c.Y);
                        if (d2 >= r * r && d2 <= (r + 1) * (r + 1))
                            map.Tiles[x, y].Impassable = true;
                    }
                new ConnectivityValidator().Process(map, info);
                Console.WriteLine("  self test: " + string.Join(" / ", ConnectivityValidator.Report));
            }

            string directory = Path.Combine(output, name);
            Directory.CreateDirectory(directory);
            string mapFile = Path.Combine(directory, name + ".map");
            new Writer() { Map = map }.WriteFile(mapFile);
            if (info.MapIniTemplates.Count > 0)
                new MapIniWriter().Write(Path.Combine(directory, "map.ini"), info.MapIniTemplates);
            File.WriteAllBytes(Path.Combine(directory, name + ".tga"), PreviewImage.CreateTga(map, info, 256));
            // overview for humans, kept outside the map folder
            File.WriteAllBytes(Path.Combine(output, name + " - layout.png"), PreviewImage.CreateLayoutPng(map, info, 4));

            Console.WriteLine();
            Console.WriteLine("Generated in {0:0.0}s", stopwatch.Elapsed.TotalSeconds);
            foreach (var line in (GridScriptAdapter.Report ?? new List<string>()).Concat(ConnectivityValidator.Report ?? new List<string>()))
                Console.WriteLine("  " + line);
            Console.WriteLine("  target {3}; named resources: {0} flags, {1} fuel depots (of {2} placed)",
                GridSkirmishAIGenerator.NamedFlags, GridSkirmishAIGenerator.NamedFuelDepots, GridResourceGenerator.Placed.Count, zeroHour ? "vanilla Zero Hour" : "Cold War Crisis");
            Console.WriteLine("  objects {0}, waypoints {1}, areas {2}, file {3:N0} bytes",
                map.Objects.Count, map.Objects.Count(o => o.IsWaypoint), map.Areas.Count, new FileInfo(mapFile).Length);
            Console.WriteLine("Saved to " + directory);
            Console.WriteLine();

            return MapVerifier.Verify(mapFile, Console.Out) ? 0 : 2;
        }

        private static IEnumerable<IMapProcessor> CreatePipeline(int trackedResources, int maxTrees, bool zeroHour)
        {
            yield return new PlayerGenerator();
            yield return new GridStartingPositionGenerator();
            yield return new LaneGenerator();
            yield return new GridResourceGenerator() { ZeroHour = zeroHour ? new ZeroHourResources() : null };
            yield return new GridRoadGenerator();
            yield return new TerrainGenerator() { Detail = false };
            // Shorter falloff than the classic 20 tiles: neighboring plateaus must not overlap.
            yield return new StartingPositionFlattener() { Radius = 10 };
            yield return new TerrainGenerator() { Detail = true };
            yield return new Flattener();
            yield return new ParticleCliffGenerator();
            yield return new RoadFlattener();
            yield return new TerrainSmoother();
            // Vanilla Zero Hour uses its own SkirmishScripts.scb, which only needs the engine names (perimeters, approach paths).
            yield return new GridSkirmishAIGenerator() { TrackedResourcesPerType = zeroHour ? 0 : trackedResources, AddCwcSkirmishPlayers = !zeroHour };
            yield return new ConnectivityValidator();
            yield return new TextureGenerator();
            yield return new TreeGenerator() { MaxTrees = maxTrees };
            yield return new RockGenerator();
            yield return new InfoGenerator();
            if (zeroHour)
                yield break;
            yield return new ScriptImporter() { ScriptFile = @"Scripts\Radar.scb", PlayerName = "PlyrCivilian" };
            yield return new GridScriptAdapter();
            yield return new MapIniProvider();
        }

        private static Map CreateMap(GridLayout grid, MapSettings settings)
        {
            var map = new Map(grid.TotalWidth, grid.TotalHeight, grid.Border);
            map.GlobalLightOptions = GlobalLightOptions.GetDefault();
            map.GlobalLightOptions.TimeOfDay = settings.TimeOfDay;
            map.SetProperty("compression", PropertyType.Integer, 0);
            map.SetProperty("weather", PropertyType.Integer, settings.Scenery.Weather);

            for (int i = 0; i < map.HeightMap.Count; i++)
                map.HeightMap[i] = settings.Layout.Height;
            return map;
        }

        private static MapLayout[] LoadLayouts()
        {
            var serializer = new XmlSerializer(typeof(MapLayout[]), new XmlRootAttribute("MapLayouts"));
            using (var stream = File.OpenRead("MapLayouts.xml"))
                return (MapLayout[])serializer.Deserialize(stream);
        }

        private static Scenery LoadScenery(string name)
        {
            var serializer = new XmlSerializer(typeof(Scenery));
            foreach (var file in Directory.GetFiles("Sceneries", "*.xml"))
            {
                using (var stream = File.OpenRead(file))
                {
                    var scenery = (Scenery)serializer.Deserialize(stream);
                    if (string.Equals(scenery.Name, name, StringComparison.OrdinalIgnoreCase))
                        return scenery;
                }
            }
            throw new ArgumentException("Unknown scenery: " + name + " (see the Sceneries folder)");
        }
    }
}
