using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CkMp.Data;
using CkMp.Data.Objects;
using CkMp.Data.Scripts;

namespace Genesis.Cli
{
    /// <summary>
    /// Reads a generated map back from disk (with the same reader the generator uses for templates) and checks that
    /// everything the engine and the skirmish scripts look up by name exists for every start position.
    /// </summary>
    public static class MapVerifier
    {
        private static readonly Regex startWaypoint = new Regex(@"^Player_(\d+)_Start$");

        public static bool Verify(string file, TextWriter output)
        {
            var reader = new Reader();
            reader.ReadFile(file);
            var map = reader.Map;
            var errors = new List<string>();

            var waypoints = map.Objects.Where(o => o.IsWaypoint).ToList();
            var waypointNames = new HashSet<string>(waypoints.Select(w => w.GetPropertyValue<string>("waypointName")));
            var waypointsById = waypoints.ToDictionary(w => (int)w.Properties["waypointID"].Value);
            var areaNames = new HashSet<string>(map.Areas.Select(a => a.Name));
            var objectNames = new HashSet<string>(map.Objects.Where(o => !o.IsWaypoint).Select(o => o.GetPropertyValue<string>("objectName")).Where(n => !string.IsNullOrEmpty(n)));

            // labels -> waypoints, and outgoing links per waypoint
            var labels = new Dictionary<string, List<ScriptObject>>();
            foreach (var w in waypoints)
            {
                for (int i = 1; i <= 3; i++)
                {
                    var label = w.GetPropertyValue<string>("waypointPathLabel" + i);
                    if (string.IsNullOrEmpty(label))
                        continue;
                    List<ScriptObject> list;
                    if (!labels.TryGetValue(label, out list))
                        labels.Add(label, list = new List<ScriptObject>());
                    list.Add(w);
                }
            }
            var linked = new HashSet<int>(map.Paths.SelectMany(p => new[] { p.Item1, p.Item2 }));

            int players = waypointNames.Count(n => n != null && startWaypoint.IsMatch(n));
            output.WriteLine("Verifying {0}: {1} start positions", Path.GetFileName(file), players);

            for (int n = 1; n <= players; n++)
            {
                if (!waypointNames.Contains("Player_" + n + "_Start"))
                    errors.Add("missing Player_" + n + "_Start");
                foreach (var area in new[] { "InnerPerimeter", "OuterPerimeter" })
                    if (!areaNames.Contains(area + n))
                        errors.Add("missing area " + area + n);
                foreach (var path in new[] { "Center", "Flank", "Backdoor" })
                {
                    List<ScriptObject> list;
                    if (!labels.TryGetValue(path + n, out list) || list.Count < 2)
                        errors.Add("missing waypath " + path + n);
                    else if (list.Any(w => !linked.Contains((int)w.Properties["waypointID"].Value)))
                        errors.Add("unlinked waypoint on " + path + n);
                }
            }
            foreach (var label in new[] { "G128_patrol1", "G128_patrol2" })
            {
                List<ScriptObject> list;
                if (!labels.TryGetValue(label, out list) || list.Count < 3 * players)
                    errors.Add("patrol path " + label + " incomplete");
            }
            foreach (var area in new[] { "SkirmishWorld", "CombatZone" })
                if (!areaNames.Contains(area))
                    errors.Add("missing area " + area);

            // Every name the scripts refer to must exist.
            int references = 0;
            var players2 = map.Players.Select(p => p.GetPropertyValue<string>("playerName")).ToList();
            for (int p = 0; p < map.PlayerScripts.Count; p++)
            {
                string owner = p < players2.Count ? players2[p] : "?";
                var scripts = map.PlayerScripts[p].SelectMany(s => s is Script ? new[] { (Script)s } : s.Scripts).ToList();
                var scriptNames = new HashSet<string>(scripts.Select(s => s.Name).Concat(map.PlayerScripts[p].OfType<ScriptGroup>().Select(g => g.Name)));
                foreach (var script in scripts)
                {
                    var elements = script.OrConditions.SelectMany(o => o.Conditions).Concat(script.ActionsIfTrue).Concat(script.ActionsIfFalse);
                    foreach (var element in elements)
                    {
                        foreach (var parameter in element.Parameters)
                        {
                            var value = parameter.Value as string;
                            if (string.IsNullOrEmpty(value))
                                continue;
                            references++;
                            switch (parameter.Type)
                            {
                                case ParameterType.Waypoint:
                                    if (!waypointNames.Contains(value))
                                        errors.Add(string.Format("{0}: script '{1}' uses missing waypoint '{2}'", owner, script.Name, value));
                                    break;
                                case ParameterType.Path:
                                    if (!labels.ContainsKey(value))
                                        errors.Add(string.Format("{0}: script '{1}' uses missing waypath '{2}'", owner, script.Name, value));
                                    break;
                                case ParameterType.Area:
                                    if (!value.StartsWith("[Skirmish]") && !areaNames.Contains(value))
                                        errors.Add(string.Format("{0}: script '{1}' uses missing area '{2}'", owner, script.Name, value));
                                    break;
                                case ParameterType.Subroutine:
                                case ParameterType.Script:
                                    if (!scriptNames.Contains(value))
                                        errors.Add(string.Format("{0}: script '{1}' calls missing script '{2}'", owner, script.Name, value));
                                    break;
                                case ParameterType.Unit:
                                    if (Regex.IsMatch(value, @"^(flag|fuel)\d+$") && !objectNames.Contains(value))
                                        errors.Add(string.Format("{0}: script '{1}' uses missing resource '{2}'", owner, script.Name, value));
                                    break;
                            }
                        }
                        if (element.Name == "START_POSITION_IS" && !script.Name.StartsWith("G128 "))
                            errors.Add(string.Format("{0}: script '{1}' still uses START_POSITION_IS", owner, script.Name));
                    }
                }
            }

            var duplicates = map.Objects.Where(o => !o.IsWaypoint).Select(o => o.GetPropertyValue<string>("objectName"))
                .Where(n => !string.IsNullOrEmpty(n)).GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            foreach (var d in duplicates)
                errors.Add("duplicate object name " + d);
            var duplicateWaypoints = waypointNames.Count != waypoints.Count;
            if (duplicateWaypoints)
                errors.Add("duplicate waypoint names");

            output.WriteLine("  {0} waypoints, {1} path links, {2} areas, {3} named objects, {4} script references checked",
                waypoints.Count, map.Paths.Count, map.Areas.Count, objectNames.Count, references);

            // Script references to resources that do not exist are expected only for the original groups 01-10 when
            // there are fewer resources; report them as warnings.
            var resourceWarnings = errors.Where(e => e.Contains("missing resource") || Regex.IsMatch(e, @"missing area '(flag|fuel)\d+_area'") || Regex.IsMatch(e, @"missing waypoint '(flag|fuel)\d+_wp'")).ToList();
            var realErrors = errors.Except(resourceWarnings).ToList();
            if (resourceWarnings.Count > 0)
                output.WriteLine("  {0} warnings about unused resource slots (fewer resources than script groups)", resourceWarnings.Count);
            foreach (var e in realErrors.Take(50))
                output.WriteLine("  ERROR: " + e);
            output.WriteLine(realErrors.Count == 0 ? "  OK: all AI names resolve for every start position." : string.Format("  {0} errors", realErrors.Count));
            return realErrors.Count == 0;
        }
    }
}
