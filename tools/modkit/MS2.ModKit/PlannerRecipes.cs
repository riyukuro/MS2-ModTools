// The plan's checks of mastery recipes (table/masteryreceipe.xml, see Recipes.cs): every recipe row the mods add or
// change is read as it ends up in each target and checked with RecipeRules (type, mastery value, numbers, counts,
// a material that is also a result, items of the target). A new recipe one target gets and another target that ships
// the table does not get is a warning, unless an <ids> range of the mods covers the id (IdSpaces says it then).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public static partial class Planner
    {
        sealed class RecipeSide
        {
            internal Profile Profile;
            internal string Archive;
            internal Dictionary<string, XmlElem> Final, Original;
        }

        static string RecipeKey(XmlElem e)
        {
            return (e.Attr("id") ?? "") + "\n" + (e.Attr("feature") ?? "") + "\n" + (e.Attr("locale") ?? "");
        }

        static Dictionary<string, XmlElem> RecipeRowsOf(XmlDoc d)
        {
            var m = new Dictionary<string, XmlElem>();
            if (d == null || d.Root == null) return m;
            foreach (XmlElem e in d.Root.Children)
                if (e.Name == RecipeRules.Rows && !string.IsNullOrEmpty(e.Attr("id"))) m[RecipeKey(e)] = e;
            return m;
        }

        static string RowText(XmlElem e)
        {
            var sb = new StringBuilder();
            foreach (XmlAttr a in e.Attrs) sb.Append(a.Name).Append('=').Append(a.Value).Append('\n');
            return sb.ToString();
        }

        // the mod a recipe row is blamed on: the last mod whose edit of the table names the id, else the last that edits the table
        static Mod RecipeMod(Run run, string id)
        {
            Mod named = null, any = null;
            foreach (Mod m in run.Plan.Mods)
                foreach (ModOp op in m.Ops)
                {
                    var ed = op as EditOp;
                    if (ed == null || !string.Equals(ed.Entry.Replace('\\', '/'), RecipeRules.Entry, StringComparison.OrdinalIgnoreCase)) continue;
                    any = m;
                    var inStep = new Regex(@"\[id=[""']?" + Regex.Escape(id) + @"[""']?\]");
                    var inRow = new Regex(@"\sid\s*=\s*[""']" + Regex.Escape(id) + @"[""']");
                    foreach (RowOp r in ed.Rows)
                    {
                        bool hit = r.At != null && inStep.IsMatch(r.At) || r.Copy != null && inStep.IsMatch(r.Copy)
                            || r.Xml != null && inRow.IsMatch(r.Xml);
                        foreach (AttrOp a in r.Attrs) if (a.Name == "id" && a.Value == id) hit = true;
                        if (hit) named = m;
                    }
                }
            return named ?? any;
        }

        static void RecipeChecks(Run run)
        {
            // first which targets ship the table and whether any plan touches it; the tables are read only then
            var found = new List<KeyValuePair<Profile, M2dArchive>>();
            bool touched = false;
            foreach (Profile p in run.Data.Profiles)
            {
                string archive = p.ArchiveIn("x:Xml.m2d");
                M2dArchive arch;
                try { arch = run.Data.Open(archive); }
                catch (ModKitException) { continue; }
                M2dEntry e = arch.Find(RecipeRules.Entry);
                if (e == null) continue;
                if (run.State(arch, archive, e.Name).Final != null) touched = true;
                found.Add(new KeyValuePair<Profile, M2dArchive>(p, arch));
            }
            if (!touched) return;
            var sides = new List<RecipeSide>();
            foreach (KeyValuePair<Profile, M2dArchive> f in found)
            {
                string archive = f.Key.ArchiveIn("x:Xml.m2d");
                M2dEntry e = f.Value.Find(RecipeRules.Entry);
                EntryState st = run.State(f.Value, archive, e.Name);
                XmlDoc orig;
                try { orig = XmlDoc.Parse(st.Original ?? f.Value.Read(e), archive + " " + e.Name); }
                catch (ModKitException) { continue; }
                XmlDoc fin = FinalDoc(run, archive, e.Name);
                sides.Add(new RecipeSide { Profile = f.Key, Archive = archive, Original = RecipeRowsOf(orig), Final = RecipeRowsOf(fin) });
            }
            Plan plan = run.Plan;

            MasteryTable mastery = null;
            foreach (RecipeSide s in sides)
            {
                XmlDoc md = FinalDoc(run, s.Archive, RecipeRules.MasteryEntry);
                if (md != null) { mastery = MasteryTable.Parse(md); break; }
            }

            var reserved = new List<IdRange>();
            foreach (Mod m in plan.Mods) foreach (IdRange r in m.Ids) if (r.Space == RecipeRules.Space) reserved.Add(r);

            foreach (RecipeSide s in sides)
            {
                HashSet<long> items = Items(run, s.Profile);
                foreach (KeyValuePair<string, XmlElem> kv in s.Final)
                {
                    XmlElem had;
                    bool isNew = !s.Original.TryGetValue(kv.Key, out had);
                    if (!isNew && RowText(had) == RowText(kv.Value)) continue;
                    XmlElem row = kv.Value;
                    string id = row.Attr("id");
                    Mod mod = RecipeMod(run, id);
                    string label = "recipe " + id + (string.IsNullOrEmpty(row.Attr("feature")) ? "" : " (feature " + row.Attr("feature") + ")");
                    // the item check per target; the rest once, whichever target shows it first
                    foreach (string t in RecipeRules.Check(row.Attr, mastery, null))
                        plan.Msg(plan.Warnings, mod, null, RecipeRules.Entry, label + ": " + t);
                    if (items != null)
                        foreach (string t in RecipeRules.Check(row.Attr, null, it => items.Contains(it) ? null : s.Profile.Title))
                            if (t.Contains(" is not an item of the ")) plan.Msg(plan.Warnings, mod, s.Archive, RecipeRules.Entry, label + ": " + t);
                    if (!isNew) continue;

                    long n;
                    bool covered = false;
                    if (long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out n))
                        foreach (IdRange r in reserved) if (n >= r.From && n <= r.To) covered = true;
                    if (covered) continue;
                    foreach (RecipeSide o in sides)
                        if (o != s && !o.Final.ContainsKey(kv.Key) && !o.Original.ContainsKey(kv.Key))
                            plan.Msg(plan.Warnings, mod, s.Archive, RecipeRules.Entry, label + " is added here but not in " + o.Archive + ": client and server read recipes from their own table");
                }
            }
        }
    }
}
