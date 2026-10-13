#region License
// GridScriptAdapter.cs
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
using System.Text.RegularExpressions;
using CkMp.Data.Map;
using CkMp.Data.Objects;
using CkMp.Data.Scripts;
using Genesis.Core;
using Genesis.Layout;

namespace Genesis.Processors.Grid
{
    /// <summary>
    /// Imports the CWC skirmish AI scripts and adapts them to any number of start positions.
    ///
    /// The CWC scripts were written for four start positions. Every AI player gets its own copy of its faction's
    /// scripts (Player.cpp, duplicateAndQualify), so the scripts must not grow with the number of players. Instead of
    /// duplicating everything 128 times, every start position is mapped to one of the four original positions (its
    /// tactical class, see <see cref="GridSlot.TacticalClass"/>):
    ///  * START_POSITION_IS(&lt;This Player&gt;, k) becomes FLAG("G128 Slot k") - the flag is set once at game start by a
    ///    small dispatcher (flags are qualified per player by the engine).
    ///  * Actions that use the waypoint Player_k_Start (command center rebuild, sensors, ...) are routed through a
    ///    dispatcher that picks the real Player_N_Start of the executing player.
    ///  * Actions that use the convoy entry waypoint (convoy_marker_1, never created by the original generator) are
    ///    routed to the convoy entry point of the executing player (G128_convoy_N at the closest map edge).
    ///  * Patrol paths playerK_patrolJ become the shared labels G128_patrolJ (the engine starts at the closest waypoint).
    ///  * The resource groups (flag01..10 / fuel01..10) are extended to the number of named resources.
    /// </summary>
    public class GridScriptAdapter : IMapProcessor
    {
        public const string SlotFlagPrefix = "G128 Slot ";

        private static readonly Regex startWaypoint = new Regex(@"^Player_(\d+)_Start$");
        private static readonly Regex convoyWaypoint = new Regex(@"^convoy_marker_\d+$");
        private static readonly Regex patrolPath = new Regex(@"^player\d+_patrol(\d+)$");
        private static readonly Regex resourceGroup = new Regex(@"^(?<side>\w+)\.(?<kind>Fu|Fl)\.(?<n>\d\d)$");

        private GridLayout grid;

        private ScriptElement conditionTrue, startPositionIs, flagCondition, setFlag, callSubroutine;

        private Dictionary<string, string> dispatchers;
        private List<Script> dispatcherScripts;

        public string UsaScriptFile { get; set; }
        public string RussiaScriptFile { get; set; }

        /// <summary>
        /// Gets the statistics of the last run.
        /// </summary>
        public static List<string> Report { get; private set; }

        public GridScriptAdapter()
        {
            UsaScriptFile = "Scripts\\USA Skirmish Scripts.scb";
            RussiaScriptFile = "Scripts\\Russia Skirmish Scripts.scb";
        }

        public string Description
        {
            get { return "Importing and adapting skirmish AI scripts"; }
        }

        public bool UpdatePreview
        {
            get { return false; }
        }

        public void Process(Map map, MapInfo info)
        {
            grid = info.Grid;
            Report = new List<string>();

            new ScriptImporter() { ScriptFile = UsaScriptFile, PlayerName = "SkirmishUSA" }.Process(map, info);
            new ScriptImporter() { ScriptFile = RussiaScriptFile, PlayerName = "SkirmishRussia" }.Process(map, info);

            foreach (var name in new[] { "SkirmishUSA", "SkirmishRussia" })
            {
                int index = map.Players.ToList().FindIndex(p => name.Equals(p.GetPropertyValue<string>("playerName")));
                if (index < 0 || index >= map.PlayerScripts.Count)
                    throw new InvalidOperationException("Scripts for " + name + " not found.");

                int before = map.PlayerScripts[index].Sum(s => s.GetSize(true));
                Adapt(map.PlayerScripts[index]);
                int after = map.PlayerScripts[index].Sum(s => s.GetSize(true));
                Report.Add(string.Format("{0}: scripts {1:N0} -> {2:N0} bytes, {3} waypoint dispatchers", name, before, after, dispatchers.Count));
            }
        }

        private void Adapt(IList<ScriptBase> scripts)
        {
            FindTemplates(scripts);
            dispatchers = new Dictionary<string, string>();
            dispatcherScripts = new List<Script>();

            ExpandResourceGroups(scripts, "Fu", GridSkirmishAIGenerator.NamedFuelDepots);
            ExpandResourceGroups(scripts, "Fl", GridSkirmishAIGenerator.NamedFlags);

            foreach (var script in AllScripts(scripts).ToList())
            {
                ReplaceStartPositionConditions(script);
                RenamePatrolPaths(script);
                ReplaceStartWaypoints(script, script.ActionsIfTrue);
                ReplaceStartWaypoints(script, script.ActionsIfFalse);
            }

            scripts.Insert(0, CreateInitGroup());

            var dispatchGroup = new ScriptGroup() { Name = "G128 Start position dispatch", IsActive = true, IsSubroutine = false };
            foreach (var s in dispatcherScripts)
                dispatchGroup.Scripts.Add(s);
            scripts.Add(dispatchGroup);
        }

        #region Templates

        private void FindTemplates(IList<ScriptBase> scripts)
        {
            var conditions = AllScripts(scripts).SelectMany(s => s.OrConditions).SelectMany(o => o.Conditions).ToList();
            var actions = AllScripts(scripts).SelectMany(s => s.ActionsIfTrue.Concat(s.ActionsIfFalse)).ToList();

            conditionTrue = Find(conditions, "CONDITION_TRUE");
            startPositionIs = Find(conditions, "START_POSITION_IS");
            flagCondition = Find(conditions, "FLAG");
            setFlag = Find(actions, "SET_FLAG");
            callSubroutine = Find(actions, "CALL_SUBROUTINE");
        }

        private static ScriptElement Find(IEnumerable<ScriptElement> elements, string name)
        {
            var element = elements.FirstOrDefault(e => e.Name == name);
            if (element == null)
                throw new InvalidOperationException("The script file does not contain a " + name + " element that could be used as a template.");
            return element;
        }

        private ScriptElement ConditionTrue()
        {
            return Clone(conditionTrue);
        }

        private ScriptElement StartPositionIs(int position)
        {
            var element = Clone(startPositionIs);
            element.Parameters.First(p => p.Type == ParameterType.Integer).Value = position;
            return element;
        }

        private ScriptElement FlagIsTrue(string flag)
        {
            var element = Clone(flagCondition);
            element.Parameters.First(p => p.Type == ParameterType.Flag).Value = flag;
            element.Parameters.First(p => p.Type == ParameterType.Boolean).Value = true;
            return element;
        }

        private ScriptElement SetFlag(string flag)
        {
            var element = Clone(setFlag);
            element.Parameters.First(p => p.Type == ParameterType.Flag).Value = flag;
            element.Parameters.First(p => p.Type == ParameterType.Boolean).Value = true;
            return element;
        }

        private ScriptElement CallSubroutine(string subroutine)
        {
            var element = Clone(callSubroutine);
            element.Parameters.First(p => p.Type == ParameterType.Subroutine).Value = subroutine;
            return element;
        }

        private static Script NewScript(string name, bool subroutine, bool once)
        {
            return new Script()
            {
                Name = name,
                IsActive = true,
                IsSubroutine = subroutine,
                Deactivate = once,
                Easy = true,
                Normal = true,
                Hard = true,
                Interval = 0
            };
        }

        #endregion

        #region Transformations

        /// <summary>
        /// Sets the "G128 Slot k" flag of the tactical class at game start. Runs once, before all other scripts.
        /// </summary>
        private ScriptGroup CreateInitGroup()
        {
            var group = new ScriptGroup() { Name = "G128 Init", IsActive = true, IsSubroutine = false };

            var init = NewScript("G128 Init slot flags", false, true);
            init.OrConditions.Add(new OrCondition());
            init.OrConditions[0].Conditions.Add(ConditionTrue());
            group.Scripts.Add(init);

            foreach (var slot in grid.Slots)
            {
                var position = NewScript("G128 Init p" + slot.Number, true, false);
                position.OrConditions.Add(new OrCondition());
                position.OrConditions[0].Conditions.Add(StartPositionIs(slot.Number));
                position.ActionsIfTrue.Add(SetFlag(SlotFlagPrefix + slot.TacticalClass));
                group.Scripts.Add(position);

                init.ActionsIfTrue.Add(CallSubroutine(position.Name));
            }

            return group;
        }

        private void ReplaceStartPositionConditions(Script script)
        {
            foreach (var orCondition in script.OrConditions)
            {
                for (int i = 0; i < orCondition.Conditions.Count; i++)
                {
                    var condition = orCondition.Conditions[i];
                    if (condition.Name != "START_POSITION_IS")
                        continue;
                    var player = condition.Parameters.FirstOrDefault(p => p.Type == ParameterType.Player);
                    var position = condition.Parameters.FirstOrDefault(p => p.Type == ParameterType.Integer);
                    if (player == null || position == null || !"<This Player>".Equals(player.Value))
                        continue;

                    orCondition.Conditions[i] = FlagIsTrue(SlotFlagPrefix + Convert.ToInt32(position.Value));
                }
            }
        }

        private static void RenamePatrolPaths(Script script)
        {
            foreach (var parameter in AllElements(script).SelectMany(e => e.Parameters))
            {
                var value = parameter.Value as string;
                if (value == null)
                    continue;
                var match = patrolPath.Match(value);
                if (match.Success)
                    parameter.Value = match.Groups[1].Value == "1" ? GridSkirmishAIGenerator.PatrolLabel1 : GridSkirmishAIGenerator.PatrolLabel2;
            }
        }

        /// <summary>
        /// Replaces every action that refers to Player_k_Start (or to the convoy entry point) by a call to a dispatcher
        /// that executes the action with the waypoint of the player running the script. For Player_k_Start only the
        /// slots of tactical class k are considered, because the original action could only be reached by start position k.
        /// </summary>
        private void ReplaceStartWaypoints(Script script, IList<ScriptElement> actions)
        {
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                var waypoint = action.Parameters.FirstOrDefault(p => p.Type == ParameterType.Waypoint && p.Value is string &&
                                                                     (startWaypoint.IsMatch((string)p.Value) || convoyWaypoint.IsMatch((string)p.Value)));
                if (waypoint == null)
                    continue;

                IEnumerable<GridSlot> slots;
                string format;
                var match = startWaypoint.Match((string)waypoint.Value);
                if (match.Success)
                {
                    int tacticalClass = int.Parse(match.Groups[1].Value);
                    slots = grid.Slots.Where(s => s.TacticalClass == tacticalClass);
                    format = "Player_{0}_Start";
                }
                else
                {
                    slots = grid.Slots;
                    format = GridSkirmishAIGenerator.ConvoyWaypointFormat;
                }

                string key = Signature(action, waypoint) + "|" + waypoint.Value;
                string dispatcher;
                if (!dispatchers.TryGetValue(key, out dispatcher))
                {
                    dispatcher = "G128 H" + (dispatchers.Count + 1).ToString("000");
                    dispatchers.Add(key, dispatcher);

                    var main = NewScript(dispatcher, true, false);
                    main.OrConditions.Add(new OrCondition());
                    main.OrConditions[0].Conditions.Add(ConditionTrue());
                    main.Comment = Signature(action, waypoint);
                    dispatcherScripts.Add(main);

                    int index = action.Parameters.IndexOf(waypoint);
                    foreach (var slot in slots)
                    {
                        var position = NewScript(dispatcher + " p" + slot.Number, true, false);
                        position.OrConditions.Add(new OrCondition());
                        position.OrConditions[0].Conditions.Add(StartPositionIs(slot.Number));
                        var clone = Clone(action);
                        clone.Parameters[index].Value = string.Format(format, slot.Number);
                        position.ActionsIfTrue.Add(clone);
                        dispatcherScripts.Add(position);

                        main.ActionsIfTrue.Add(CallSubroutine(position.Name));
                    }
                }

                actions[i] = CallSubroutine(dispatcher);
            }
        }

        private static string Signature(ScriptElement element, Parameter skip)
        {
            return element.Name + "(" + string.Join(", ", element.Parameters.Select(p => p == skip ? "*" : p.Type + ":" + FormatValue(p.Value))) + ")";
        }

        private static string FormatValue(object value)
        {
            var array = value as Array;
            if (array != null)
                return "[" + string.Join(";", array.Cast<object>()) + "]";
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Clones the "XX.Fu.01" / "XX.Fl.01" group for every named resource beyond 10 and enables the new
        /// "Activate" scripts wherever the original scripts enable/disable "Activate XX.Fu.10".
        /// </summary>
        private void ExpandResourceGroups(IList<ScriptBase> scripts, string kind, int count)
        {
            var groups = scripts.OfType<ScriptGroup>()
                .Select(g => new { Group = g, Match = resourceGroup.Match(g.Name) })
                .Where(g => g.Match.Success && g.Match.Groups["kind"].Value == kind)
                .ToList();
            if (groups.Count == 0)
                return;

            var template = groups.FirstOrDefault(g => g.Match.Groups["n"].Value == "01");
            if (template == null)
                return;

            string side = template.Match.Groups["side"].Value;
            string resource = kind == "Fu" ? "fuel" : "flag";
            int existing = groups.Max(g => int.Parse(g.Match.Groups["n"].Value));
            if (count <= existing)
                return;

            int insertAt = scripts.IndexOf(groups.Last().Group) + 1;
            for (int n = existing + 1; n <= count; n++)
            {
                var replacements = new[]
                {
                    Tuple.Create(side + "." + kind + ".01", side + "." + kind + "." + n.ToString("00")),
                    Tuple.Create(resource + "01", resource + n.ToString("00"))
                };
                scripts.Insert(insertAt++, CloneGroup(template.Group, replacements));
            }

            // Enable / disable the new groups together with the last original one.
            string last = "Activate " + side + "." + kind + "." + existing.ToString("00");
            foreach (var script in AllScripts(scripts).ToList())
            {
                ExtendActivation(script.ActionsIfTrue, last, side, kind, existing, count);
                ExtendActivation(script.ActionsIfFalse, last, side, kind, existing, count);
            }

            Report.Add(string.Format("{0}: resource groups {1}.{2}.{3:00}-{4:00} added", side, side, kind, existing + 1, count));
        }

        private static void ExtendActivation(IList<ScriptElement> actions, string last, string side, string kind, int existing, int count)
        {
            for (int i = 0; i < actions.Count; i++)
            {
                var parameter = actions[i].Parameters.FirstOrDefault(p => p.Type == ParameterType.Script && last.Equals(p.Value));
                if (parameter == null)
                    continue;

                int index = actions[i].Parameters.IndexOf(parameter);
                for (int n = count; n > existing; n--)
                {
                    var clone = Clone(actions[i]);
                    clone.Parameters[index].Value = "Activate " + side + "." + kind + "." + n.ToString("00");
                    actions.Insert(i + 1, clone);
                }
                i += count - existing;
            }
        }

        #endregion

        #region Cloning

        private static IEnumerable<Script> AllScripts(IEnumerable<ScriptBase> scripts)
        {
            foreach (var s in scripts)
            {
                var script = s as Script;
                if (script != null)
                    yield return script;
                else
                    foreach (var child in s.Scripts)
                        yield return child;
            }
        }

        private static IEnumerable<ScriptElement> AllElements(Script script)
        {
            return script.OrConditions.SelectMany(o => o.Conditions).Concat(script.ActionsIfTrue).Concat(script.ActionsIfFalse);
        }

        private static ScriptGroup CloneGroup(ScriptGroup group, Tuple<string, string>[] replacements)
        {
            var clone = new ScriptGroup() { Name = Replace(group.Name, replacements), IsActive = group.IsActive, IsSubroutine = group.IsSubroutine };
            foreach (var script in group.Scripts)
                clone.Scripts.Add(CloneScript(script, replacements));
            return clone;
        }

        private static Script CloneScript(Script script, Tuple<string, string>[] replacements)
        {
            var clone = new Script()
            {
                Name = Replace(script.Name, replacements),
                Comment = script.Comment,
                ConditionComment = script.ConditionComment,
                ActionComment = script.ActionComment,
                IsActive = script.IsActive,
                IsSubroutine = script.IsSubroutine,
                Deactivate = script.Deactivate,
                Easy = script.Easy,
                Normal = script.Normal,
                Hard = script.Hard,
                Interval = script.Interval
            };
            foreach (var orCondition in script.OrConditions)
            {
                var o = new OrCondition();
                foreach (var c in orCondition.Conditions)
                    o.Conditions.Add(Clone(c, replacements));
                clone.OrConditions.Add(o);
            }
            foreach (var a in script.ActionsIfTrue)
                clone.ActionsIfTrue.Add(Clone(a, replacements));
            foreach (var a in script.ActionsIfFalse)
                clone.ActionsIfFalse.Add(Clone(a, replacements));
            return clone;
        }

        private static ScriptElement Clone(ScriptElement element, Tuple<string, string>[] replacements = null)
        {
            var clone = new ScriptElement() { Id = element.Id, Name = element.Name };
            foreach (var p in element.Parameters)
            {
                object value = p.Value;
                var text = value as string;
                if (text != null && replacements != null)
                    value = Replace(text, replacements);
                else if (value is Array)
                    value = ((Array)value).Clone();
                clone.Parameters.Add(new Parameter() { Type = p.Type, Value = value });
            }
            return clone;
        }

        private static string Replace(string text, Tuple<string, string>[] replacements)
        {
            if (text == null || replacements == null)
                return text;
            foreach (var r in replacements)
                text = text.Replace(r.Item1, r.Item2);
            return text;
        }

        #endregion
    }
}
