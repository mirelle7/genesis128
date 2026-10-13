using System;
using System.IO;
using System.Linq;
using CkMp.Data;
using CkMp.Data.Scripts;

namespace Genesis.Cli
{
    /// <summary>
    /// Prints the script tree of a .scb/.map file in a readable form (debugging aid).
    /// </summary>
    public static class ScriptDump
    {
        public static void Dump(string file, TextWriter output)
        {
            var reader = new Reader();
            reader.ReadFile(file);
            var map = reader.Map;
            for (int i = 0; i < map.PlayerScripts.Count; i++)
            {
                var player = i < map.Players.Count ? map.Players[i] : null;
                output.WriteLine("PLAYER {0}: {1}", i, player == null ? "?" : string.Join(", ", player.Properties.Select(p => p.Key + "=" + p.Value.Value)));
                foreach (var s in map.PlayerScripts[i])
                    DumpScriptBase(s, output, "  ");
            }
            output.WriteLine("TEAMS: {0}", map.Teams.Count);
            foreach (var t in map.Teams)
                output.WriteLine("  TEAM " + string.Join(", ", t.Properties.Select(p => p.Key + "=" + p.Value.Value)));
        }

        private static void DumpScriptBase(ScriptBase s, TextWriter o, string indent)
        {
            if (s is ScriptGroup g)
            {
                o.WriteLine("{0}GROUP '{1}' active={2} sub={3}", indent, g.Name, g.IsActive, g.IsSubroutine);
                foreach (var c in g.Scripts) DumpScriptBase(c, o, indent + "  ");
            }
            else if (s is Script sc)
            {
                o.WriteLine("{0}SCRIPT '{1}' active={2} sub={3} once={4} e/n/h={5}{6}{7} int={8}", indent, sc.Name, sc.IsActive, sc.IsSubroutine, sc.Deactivate, sc.Easy ? 1 : 0, sc.Normal ? 1 : 0, sc.Hard ? 1 : 0, sc.Interval);
                foreach (var or in sc.OrConditions)
                {
                    o.WriteLine("{0}  OR", indent);
                    foreach (var c in or.Conditions) o.WriteLine("{0}    IF {1}", indent, Elem(c));
                }
                foreach (var a in sc.ActionsIfTrue) o.WriteLine("{0}  DO {1}", indent, Elem(a));
                foreach (var a in sc.ActionsIfFalse) o.WriteLine("{0}  ELSE {1}", indent, Elem(a));
            }
        }

        private static string Elem(ScriptElement e)
        {
            return string.Format("{0}[{1}]({2})", e.Name, e.Id, string.Join(", ", e.Parameters.Select(p => p.Type + ":" + p.Value)));
        }
    }
}
