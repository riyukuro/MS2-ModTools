// The priority of mods. Where two mods set the same thing the mod of the higher priority wins, and the conflict
// message names it. A plan takes its mods in rising priority (Planner.Build: the last mod of the list is the
// highest). The user names the order the other way round, the highest first: the list of the Toolbench view
// (ModList, top = highest) and the priority list of the command line (--priority a,b or MS2_MOD_PRIORITY).
using System;
using System.Collections.Generic;

namespace MS2.ModKit
{
    public static class ModOrder
    {
        /// <summary>Name of the setting (environment or modtools.toml) with the priority list.</summary>
        public const string Setting = "MS2_MOD_PRIORITY";

        /// <summary>Reads "a, b; c" into mod ids, the highest priority first. Null or blank gives an empty list.</summary>
        public static List<string> Parse(string text)
        {
            var ids = new List<string>();
            if (text == null) return ids;
            foreach (string part in text.Split(',', ';'))
            {
                string id = part.Trim();
                if (id.Length > 0 && !ids.Contains(id)) ids.Add(id);
            }
            return ids;
        }

        /// <summary>
        /// The mods in rising priority, as a plan takes them. The mods the priority list does not name stay in the
        /// order given (a later one is higher) and are all below the named ones; of the named ones the first of the
        /// list is the highest. Ids of the list that no mod has are put into <paramref name="unknown"/>.
        /// </summary>
        public static List<Mod> Arrange(IList<Mod> mods, IList<string> priority, List<string> unknown)
        {
            var result = new List<Mod>();
            var named = new List<Mod>();
            if (priority != null)
                for (int i = priority.Count - 1; i >= 0; i--)
                {
                    Mod hit = null;
                    foreach (Mod m in mods) if (m.Id == priority[i]) hit = m;
                    if (hit != null) named.Add(hit);
                    else if (unknown != null) unknown.Insert(0, priority[i]);
                }
            foreach (Mod m in mods) if (!named.Contains(m)) result.Add(m);
            result.AddRange(named);
            return result;
        }

        /// <summary>The mods of a plan as a line for the user, the highest priority first: "c > a > b".</summary>
        public static string Text(IList<Mod> risingPriority)
        {
            var ids = new List<string>();
            for (int i = risingPriority.Count - 1; i >= 0; i--) ids.Add(risingPriority[i].Id);
            return string.Join(" > ", ids.ToArray());
        }

        public const string Lead = "priority, the highest first: ";

        /// <summary>
        /// A report for a box of that many columns: the priority line is broken in front of a " > " where it would
        /// run past the edge, so that every name shows without scrolling. The other lines stay as they are.
        /// </summary>
        public static string Wrap(string report, int columns)
        {
            if (report == null || columns < 8 || report.IndexOf(Lead, StringComparison.Ordinal) < 0) return report;
            string[] lines = report.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith(Lead, StringComparison.Ordinal) || lines[i].Length <= columns) continue;
                string[] ids = lines[i].Substring(Lead.Length).Split(new[] { " > " }, StringSplitOptions.None);
                var sb = new System.Text.StringBuilder(Lead.TrimEnd());
                int col = sb.Length;
                for (int k = 0; k < ids.Length; k++)
                {
                    string part = (k == 0 ? "" : "> ") + ids[k];
                    if (col + 1 + part.Length > columns) { sb.Append("\r\n  "); col = 2; } else { sb.Append(' '); col++; }
                    sb.Append(part); col += part.Length;
                }
                lines[i] = sb.ToString();
            }
            return string.Join("\r\n", lines);
        }
    }
}
