// Read-only views of shipped skills and effects for editors (SkillEditor.cs): the ids with their names, and one id's
// levels, motions, attacks and mapped field values per target, in neutral units.
using System;
using System.Collections.Generic;
using System.Globalization;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    /// <summary>The mapped field values of one level, motion or attack (field name -> neutral value; missing = not there).</summary>
    public sealed class SkillValues
    {
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly List<SkillValues> Children = new List<SkillValues>();
    }

    /// <summary>One target's view of one skill or effect.</summary>
    public sealed class SkillSideInfo
    {
        public string Side;         // profile name
        public string Archive, Entry;
        public string Name;
        public string Icon;
        /// <summary>Level -> the level's values; Children = motions, their Children = attacks.</summary>
        public readonly SortedDictionary<int, SkillValues> Levels = new SortedDictionary<int, SkillValues>();
        /// <summary>Levels written in more than one variant (features or locales); the values are the variant this side reads.</summary>
        public readonly List<int> Variants = new List<int>();
    }

    public sealed class SkillInfo
    {
        public SkillKind Kind;
        public long Id;
        public readonly List<SkillSideInfo> Sides = new List<SkillSideInfo>();
        public string Name { get { foreach (SkillSideInfo s in Sides) if (!string.IsNullOrEmpty(s.Name)) return s.Name; return null; } }
        public string Icon { get { foreach (SkillSideInfo s in Sides) if (!string.IsNullOrEmpty(s.Icon)) return s.Icon; return null; } }
    }

    public static partial class Planner
    {
        static Run ReadRun(DataSet data) { return new Run { Data = data, Plan = new Plan(), Options = new PlanOptions() }; }

        static Mod ReadMod() { return Mod.Parse("<mod id=\"read\" name=\"read\" version=\"1\" />", "read"); }

        /// <summary>id -> name of a side (the region's first language that has rows), cached on the data set.</summary>
        static Dictionary<long, string> SkillNames(Run run, Mod mod, SkillSideCtx c)
        {
            string key = "skillnames\n" + c.Archive + "\n" + c.Map.Name;
            object o;
            if (run.Data.Cache.TryGetValue(key, out o)) return (Dictionary<long, string>)o;
            var names = new Dictionary<long, string>();
            SkillString s = StringRole(c, "name");
            if (s != null)
                foreach (KeyValuePair<string, List<string>> lang in StringFiles(c, s))
                {
                    foreach (string f in lang.Value)
                    {
                        M2dEntry e = c.Arch.Find(f);
                        if (e == null) continue;
                        XmlDoc d;
                        try { d = XmlDoc.Parse(c.Arch.Read(e), c.Archive + " " + f); } catch (ModKitException) { continue; }
                        if (d.Root == null) continue;
                        foreach (XmlElem r in d.Root.Children)
                        {
                            long id;
                            string n = r.Attr(s.Attr);
                            if (string.IsNullOrEmpty(n) || !long.TryParse(r.Attr("id") ?? "", NumberStyles.None, CultureInfo.InvariantCulture, out id)) continue;
                            if (!names.ContainsKey(id)) names[id] = n;
                        }
                    }
                    if (names.Count > 0) break;
                }
            run.Data.Cache[key] = names;
            return names;
        }

        /// <summary>
        /// Shipped skill or effect ids whose id or name (any target) contains the text, with a name, at most max of
        /// them, sorted by id.
        /// </summary>
        public static List<KeyValuePair<long, string>> SkillFind(DataSet data, SkillKind kind, string text, int max)
        {
            Run run = ReadRun(data);
            Mod mod = ReadMod();
            text = (text ?? "").Trim();
            var ids = new SortedDictionary<long, string>();
            foreach (SkillSideCtx c in SkillSides(run, mod, kind))
            {
                Dictionary<long, string> names = SkillNames(run, mod, c);
                foreach (long id in Index(run, c).Where.Keys)
                {
                    string n;
                    names.TryGetValue(id, out n);
                    string had;
                    if (ids.TryGetValue(id, out had) && !string.IsNullOrEmpty(had)) continue;
                    ids[id] = n ?? "";
                }
            }
            var l = new List<KeyValuePair<long, string>>();
            foreach (KeyValuePair<long, string> kv in ids)
            {
                if (text.Length > 0 && kv.Key.ToString(CultureInfo.InvariantCulture).IndexOf(text, StringComparison.Ordinal) < 0
                    && kv.Value.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                l.Add(kv);
                if (l.Count >= max) break;
            }
            return l;
        }

        /// <summary>One shipped skill or effect on every target that has it; null when no target has it.</summary>
        public static SkillInfo SkillOpen(DataSet data, SkillKind kind, long id)
        {
            Run run = ReadRun(data);
            Mod mod = ReadMod();
            var info = new SkillInfo { Kind = kind, Id = id };
            var op = new SkillOp { Kind = kind, Id = id };
            foreach (SkillSideCtx c in SkillSides(run, mod, kind))
            {
                EntryState st;
                int variants;
                XmlElem root = SkillRoot(run, mod, c, id, out st, out variants);
                if (root == null) continue;
                var side = new SkillSideInfo { Side = c.Label, Archive = c.Archive, Entry = st.Name };
                string n;
                if (SkillNames(run, mod, c).TryGetValue(id, out n)) side.Name = n;
                var icons = new List<string>();
                CollectIcons(root, icons);
                if (icons.Count > 0) side.Icon = icons[0];
                foreach (KeyValuePair<int, List<XmlElem>> kv in Levels(root, c.Side))
                    if (kv.Value.Count > 1) side.Variants.Add(kv.Key);
                foreach (KeyValuePair<int, XmlElem> lv in Picked(run, mod, c, op, root, st.Name, false))
                {
                    SkillValues v = ReadValues(c, lv.Value, SkillScope.Level);
                    if (c.Side.Motion != null)
                        foreach (XmlElem m in Named(lv.Value, c.Side.Motion))
                        {
                            SkillValues mv = ReadValues(c, m, SkillScope.Motion);
                            if (c.Side.Attack != null)
                                foreach (XmlElem a in Named(m, c.Side.Attack)) mv.Children.Add(ReadValues(c, a, SkillScope.Attack));
                            v.Children.Add(mv);
                        }
                    side.Levels[lv.Key] = v;
                }
                info.Sides.Add(side);
            }
            return info.Sides.Count == 0 ? null : info;
        }

        static SkillValues ReadValues(SkillSideCtx c, XmlElem scope, SkillScope kind)
        {
            var v = new SkillValues();
            foreach (SkillField f in c.Map.Fields)
            {
                if (f.Scope != kind) continue;
                string at = f.At(c.Bundle);
                XmlElem el = at.Length == 0 ? scope : Child(scope, at);
                string t = el == null ? null : el.Attr(f.Attr(c.Bundle));
                if (t != null) v.Values[f.Name] = SkillValue.Neutral(t, f.Scale(c.Bundle));
            }
            return v;
        }
    }
}
