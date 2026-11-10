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
        /// <summary>Fields the row leaves out whose value is the map's default (shown as "1 (default)").</summary>
        public readonly HashSet<string> Defaulted = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>One target's view of one skill or effect.</summary>
    public sealed class SkillSideInfo
    {
        public string Side;         // profile name
        public string Archive, Entry;
        public string Name;
        /// <summary>The language Name was read in.</summary>
        public string NameLanguage;
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

        /// <summary>A side's own language: Korean on the kms-client, English on the gms-client and the server.</summary>
        public static string OwnLanguage(Profile p) { return p.Region == "kr" ? "kr" : "en"; }

        /// <summary>
        /// The language a side's names are read in: lang when the side has name rows in it, else the side's own language,
        /// else the first language that has rows; null when the side has no name rows.
        /// </summary>
        static string NameLanguage(SkillSideCtx c, string lang)
        {
            SkillString s = StringRole(c, "name");
            if (s == null) return null;
            List<KeyValuePair<string, List<string>>> files = StringFiles(c, s);
            string own = OwnLanguage(c.P);
            foreach (string want in new[] { lang, own })
                if (want != null)
                    foreach (KeyValuePair<string, List<string>> kv in files) if (kv.Key == want) return want;
            return files.Count > 0 ? files[0].Key : null;
        }

        /// <summary>id -> name of a side in one language (see NameLanguage), cached on the data set.</summary>
        static Dictionary<long, string> SkillNames(Run run, Mod mod, SkillSideCtx c, string lang, out string used)
        {
            used = NameLanguage(c, lang);
            string key = "skillnames\n" + c.Archive + "\n" + c.Map.Name + "\n" + used;
            object o;
            if (run.Data.Cache.TryGetValue(key, out o)) return (Dictionary<long, string>)o;
            var names = new Dictionary<long, string>();
            SkillString s = StringRole(c, "name");
            if (s != null && used != null)
                foreach (KeyValuePair<string, List<string>> l in StringFiles(c, s))
                {
                    if (l.Key != used) continue;
                    foreach (string f in l.Value)
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
                }
            run.Data.Cache[key] = names;
            return names;
        }

        /// <summary>The languages any target has skill or effect names in, sorted.</summary>
        public static List<string> SkillNameLanguages(DataSet data, SkillKind kind)
        {
            Run run = ReadRun(data);
            Mod mod = ReadMod();
            var all = new SortedDictionary<string, bool>(StringComparer.Ordinal);
            foreach (SkillSideCtx c in SkillSides(run, mod, kind))
            {
                SkillString s = StringRole(c, "name");
                if (s == null) continue;
                foreach (KeyValuePair<string, List<string>> kv in StringFiles(c, s)) all[kv.Key] = true;
            }
            return new List<string>(all.Keys);
        }

        /// <summary>
        /// Shipped skill or effect ids whose id or name (any target) contains the text, with a name, at most max of
        /// them, sorted by id. lang: the names' language (null: each target's own); used: "side lang" per target, the
        /// language each target's names were read in.
        /// </summary>
        public static List<KeyValuePair<long, string>> SkillFind(DataSet data, SkillKind kind, string text, int max, string lang, out string used)
        {
            Run run = ReadRun(data);
            Mod mod = ReadMod();
            text = (text ?? "").Trim();
            var ids = new SortedDictionary<long, string>();
            var usedList = new List<string>();
            foreach (SkillSideCtx c in SkillSides(run, mod, kind))
            {
                string u;
                Dictionary<long, string> names = SkillNames(run, mod, c, lang, out u);
                usedList.Add(c.Label + " " + (u ?? "(no names)"));
                foreach (long id in Index(run, c).Where.Keys)
                {
                    string n;
                    names.TryGetValue(id, out n);
                    string had;
                    if (ids.TryGetValue(id, out had) && !string.IsNullOrEmpty(had)) continue;
                    ids[id] = n ?? "";
                }
            }
            used = string.Join(", ", usedList.ToArray());
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

        public static List<KeyValuePair<long, string>> SkillFind(DataSet data, SkillKind kind, string text, int max)
        {
            string used;
            return SkillFind(data, kind, text, max, null, out used);
        }

        /// <summary>One shipped skill or effect on every target that has it; null when no target has it.</summary>
        public static SkillInfo SkillOpen(DataSet data, SkillKind kind, long id) { return SkillOpen(data, kind, id, null); }

        /// <summary>The same, with the names in lang (null: each target's own language).</summary>
        public static SkillInfo SkillOpen(DataSet data, SkillKind kind, long id, string lang)
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
                string usedLang;
                if (SkillNames(run, mod, c, lang, out usedLang).TryGetValue(id, out n)) side.Name = n;
                side.NameLanguage = usedLang;
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
                else if (el != null && f.Default != null) { v.Values[f.Name] = f.Default; v.Defaulted.Add(f.Name); }
            }
            return v;
        }
    }
}
