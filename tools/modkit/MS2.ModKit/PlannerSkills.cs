// Skill, effect and job skill tree ops (Skills.cs) planned on every target the data set has. Each side is planned
// on its own (its own levels, motions and units), so these changes take no part in the sync pass.
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
        public const string SkillTables = "Xml.m2d";
        public const string JobEntry = "table/job.xml";
        public const string AnimationEntry = "anikeytext.xml";
        const string NewBundle = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ms2>\n</ms2>\n";

        /// <summary>One target of a skill op.</summary>
        sealed class SkillSideCtx
        {
            internal Profile P;
            internal bool Bundle;
            internal string Archive;
            internal M2dArchive Arch;
            internal FeatureGate Gate;
            internal SkillEntityMap Map;
            internal SkillSide Side { get { return Map.Side(Bundle); } }
            internal string Label { get { return P.Name; } }
        }

        /// <summary>The ids a side ships: id -> entry (bundle: the file with the row; files: the id's file).</summary>
        sealed class SkillIndex
        {
            internal readonly Dictionary<long, string> Where = new Dictionary<long, string>();
        }

        static readonly Regex XmlPathRx = new Regex(@"^[A-Za-z0-9_\-/. ]+\.xml$", RegexOptions.CultureInvariant);

        static IEnumerable<SkillSideCtx> SkillSides(Run run, Mod mod, SkillKind kind)
        {
            foreach (Profile p in Profile.All)
            {
                if (!run.Data.Has(p)) continue;
                string archive = p.Root + ":" + SkillTables;
                M2dArchive arch = run.Open(mod, archive);
                if (arch == null) continue;
                yield return new SkillSideCtx { P = p, Bundle = p == Profile.KmsClient, Archive = archive, Arch = arch, Gate = Gate(run, archive, arch, p), Map = SkillMap.Default.Entity(kind) };
            }
        }

        static FeatureGate Gate(Run run, string archive, M2dArchive arch, Profile p)
        {
            string key = "gate\n" + archive;
            object o;
            if (run.Data.Cache.TryGetValue(key, out o)) return (FeatureGate)o;
            FeatureGate g = FeatureGate.Of(arch, FeatureGate.LocaleOf(p));
            run.Data.Cache[key] = g;
            return g;
        }

        static bool InFolder(string entry, string pattern)
        {
            // "skilldata/*.xml": a file right in the folder
            int star = pattern.IndexOf('*');
            if (star < 0) return string.Equals(entry, pattern, StringComparison.OrdinalIgnoreCase);
            string pre = pattern.Substring(0, star), post = pattern.Substring(star + 1);
            return entry.Length >= pre.Length + post.Length && entry.StartsWith(pre, StringComparison.OrdinalIgnoreCase)
                && entry.EndsWith(post, StringComparison.OrdinalIgnoreCase) && entry.IndexOf('/', pre.Length) < 0;
        }

        static SkillIndex Index(Run run, SkillSideCtx c)
        {
            string key = "skillidx\n" + c.Archive + "\n" + c.Map.Name;
            object o;
            if (run.Data.Cache.TryGetValue(key, out o)) return (SkillIndex)o;
            var ix = new SkillIndex();
            SkillSide s = c.Side;
            if (s.Bundle)
            {
                foreach (M2dEntry e in c.Arch.Entries)
                {
                    if (!InFolder(e.Name, s.Entry)) continue;
                    XmlDoc d;
                    try { d = XmlDoc.Parse(c.Arch.Read(e), c.Archive + " " + e.Name); } catch (ModKitException) { continue; }
                    if (d.Root == null) continue;
                    foreach (XmlElem r in d.Root.Children)
                    {
                        long id;
                        if (r.Name == s.Row && long.TryParse(r.Attr(s.Key) ?? "", NumberStyles.None, CultureInfo.InvariantCulture, out id) && !ix.Where.ContainsKey(id)) ix.Where[id] = e.Name;
                    }
                }
            }
            else
            {
                Regex rx = IdSpaces.EntryRegex(Regex.Replace(s.Entry, @"\{id/\d+\}", "*"));
                foreach (M2dEntry e in c.Arch.Entries)
                {
                    Match m = rx.Match(e.Name);
                    long id;
                    if (m.Success && long.TryParse(m.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out id)) ix.Where[id] = e.Name;
                }
            }
            run.Data.Cache[key] = ix;
            return ix;
        }

        /// <summary>The shipped element of an id on a side (bundle: the row; files: the root element), in the plan's state of its entry.</summary>
        static XmlElem SkillRoot(Run run, Mod mod, SkillSideCtx c, long id, out EntryState st, out int variants)
        {
            st = null; variants = 0;
            string entry;
            if (!Index(run, c).Where.TryGetValue(id, out entry)) return null;
            st = run.State(c.Arch, c.Archive, entry);
            if (st.Base == null || !EnsureDoc(run.Plan, mod, st)) return null;
            if (!c.Bundle) return st.Doc.Root;
            var rows = new List<XmlElem>();
            string text = id.ToString(CultureInfo.InvariantCulture);
            foreach (XmlElem r in st.Doc.Root.Children) if (r.Name == c.Side.Row && r.Attr(c.Side.Key) == text) rows.Add(r);
            variants = rows.Count;
            return c.Gate.Pick(rows) ?? (rows.Count > 0 ? rows[0] : null);
        }

        static string LevelOf(XmlElem level, SkillSide s)
        {
            string at, attr;
            int a = s.LevelKey.LastIndexOf('@');
            at = s.LevelKey.Substring(0, a); attr = s.LevelKey.Substring(a + 1);
            XmlElem e = at.Length == 0 ? level : Child(level, at);
            return e == null ? null : e.Attr(attr);
        }

        /// <summary>A plain path of element names ("consume/stat"): the first match per step.</summary>
        static XmlElem Child(XmlElem from, string path)
        {
            XmlElem cur = from;
            foreach (string step in path.Split('/'))
            {
                XmlElem next = null;
                foreach (XmlElem c in cur.Children) if (c.Name == step) { next = c; break; }
                if (next == null) return null;
                cur = next;
            }
            return cur;
        }

        static List<XmlElem> Named(XmlElem parent, string name)
        {
            var l = new List<XmlElem>();
            if (parent != null) foreach (XmlElem c in parent.Children) if (c.Name == name) l.Add(c);
            return l;
        }

        /// <summary>Level number -> the variants of that level, in file order.</summary>
        static SortedDictionary<int, List<XmlElem>> Levels(XmlElem root, SkillSide s)
        {
            var d = new SortedDictionary<int, List<XmlElem>>();
            foreach (XmlElem l in Named(root, s.Level))
            {
                int n;
                if (!int.TryParse(LevelOf(l, s) ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) continue;
                List<XmlElem> v;
                if (!d.TryGetValue(n, out v)) d[n] = v = new List<XmlElem>();
                v.Add(l);
            }
            return d;
        }

        static string LevelText(List<int> levels)
        {
            var parts = new List<string>();
            foreach (int i in levels) parts.Add(i.ToString(CultureInfo.InvariantCulture));
            return string.Join(", ", parts.ToArray());
        }

        // ---- one field on one side: where it is and its new value ----

        sealed class FieldHit
        {
            internal XmlElem Elem;
            internal string Attr, Old, New;
            internal int Level;
            internal SkillField Field;
        }

        /// <summary>The attributes a set changes on one side, with the side's levels picked. Misses become warnings.</summary>
        static List<FieldHit> Hits(Run run, Mod mod, SkillSideCtx c, SkillOp op, SkillSet set, XmlElem root, string entry, Dictionary<int, XmlElem> picked)
        {
            Plan plan = run.Plan;
            var hits = new List<FieldHit>();
            List<int> want = SkillLevels.Parse(set.Levels);
            var missing = new List<int>();
            if (want != null) foreach (int l in want) if (!picked.ContainsKey(l)) missing.Add(l);
            if (missing.Count > 0)
                plan.Msg(plan.Warnings, mod, c.Archive, entry, op.Tag + " " + op.Id + ": level " + LevelText(missing) + " is not there on " + c.Label + ", skipped (" + ModLine.Of(set.Line) + ")");
            SkillSide s = c.Side;
            foreach (KeyValuePair<int, XmlElem> lv in picked)
            {
                if (want != null && !want.Contains(lv.Key)) continue;
                XmlElem scope = lv.Value;
                string where = "level " + lv.Key;
                if (set.Scope != SkillScope.Level)
                {
                    List<XmlElem> motions = Named(scope, s.Motion);
                    if (set.Motion > motions.Count)
                    {
                        plan.Msg(plan.Warnings, mod, c.Archive, entry, op.Tag + " " + op.Id + " " + where + ": motion " + set.Motion + " is not there on " + c.Label + " (" + motions.Count + " motions), skipped (" + ModLine.Of(set.Line) + ")");
                        continue;
                    }
                    scope = motions[set.Motion - 1];
                    where += " motion " + set.Motion;
                    if (set.Scope == SkillScope.Attack)
                    {
                        List<XmlElem> attacks = Named(scope, s.Attack);
                        if (set.Attack > attacks.Count)
                        {
                            plan.Msg(plan.Warnings, mod, c.Archive, entry, op.Tag + " " + op.Id + " " + where + ": attack " + set.Attack + " is not there on " + c.Label + " (" + attacks.Count + " attacks), skipped (" + ModLine.Of(set.Line) + ")");
                            continue;
                        }
                        scope = attacks[set.Attack - 1];
                        where += " attack " + set.Attack;
                    }
                }
                foreach (KeyValuePair<string, string> f in set.Fields)
                {
                    SkillField fm = c.Map.Field(f.Key);
                    string at = fm.At(c.Bundle);
                    XmlElem el = at.Length == 0 ? scope : Child(scope, at);
                    if (el == null)
                    {
                        plan.Msg(plan.Warnings, mod, c.Archive, entry, op.Tag + " " + op.Id + " " + where + ": " + f.Key + " has no <" + at + "> on " + c.Label + ", skipped (" + ModLine.Of(set.Line) + ")");
                        continue;
                    }
                    string old = el.Attr(fm.Attr(c.Bundle)), why;
                    string nv = SkillValue.Convert(f.Value, fm.Scale(c.Bundle), fm.Whole(c.Bundle), old ?? DefaultText(fm, c.Bundle), out why);
                    if (nv == null)
                    {
                        plan.Msg(plan.Warnings, mod, c.Archive, entry, op.Tag + " " + op.Id + " " + where + ": " + f.Key + "=\"" + f.Value + "\" on " + c.Label + ": " + why + ", skipped (" + ModLine.Of(set.Line) + ")");
                        continue;
                    }
                    hits.Add(new FieldHit { Elem = el, Attr = fm.Attr(c.Bundle), Old = old, New = nv, Level = lv.Key, Field = fm });
                }
            }
            return hits;
        }

        /// <summary>The field's default in the side's units, null when the map gives none.</summary>
        static string DefaultText(SkillField f, bool bundle)
        {
            decimal d;
            if (f.Default == null || !SkillValue.TryNum(f.Default, out d)) return null;
            return SkillValue.Text(d * f.Scale(bundle));
        }

        /// <summary>Level -> the variant the side reads, with a warning when other variants of an edited level exist.</summary>
        static Dictionary<int, XmlElem> Picked(Run run, Mod mod, SkillSideCtx c, SkillOp op, XmlElem root, string entry, bool warn)
        {
            var picked = new Dictionary<int, XmlElem>();
            var multi = new List<int>();
            foreach (KeyValuePair<int, List<XmlElem>> kv in Levels(root, c.Side))
            {
                XmlElem w = c.Gate.Pick(kv.Value);
                if (w == null) continue;
                picked[kv.Key] = w;
                if (kv.Value.Count > 1) multi.Add(kv.Key);
            }
            if (warn && multi.Count > 0)
                run.Plan.Msg(run.Plan.Warnings, mod, c.Archive, entry, op.Tag + " " + op.Id + ": level " + LevelText(multi) + " is written " + (multi.Count == 1 ? "in several variants" : "in several variants each")
                    + " (features or locales) on " + c.Label + "; the one this side reads (" + c.Gate.Locale + ") is changed, the others are left as they are");
            return picked;
        }

        static void Own(Run run, int from)
        {
            for (int i = from; i < run.Plan.Changes.Count; i++) run.Plan.Changes[i].OwnSides = true;
        }

        static void Change(Run run, Mod mod, EntryState st, XmlElem el, string attr, string value, int line, bool unset)
        {
            var r = new RowOp { Kind = RowOpKind.Change, At = RowPath.Canonical(el), Line = line, Sync = false };
            r.Attrs.Add(new AttrOp { Kind = unset ? AttrOpKind.Unset : AttrOpKind.Set, Name = attr, Value = value, Sync = false, Line = line });
            int n = run.Plan.Changes.Count;
            try { DoChange(run, mod, st, r); }
            catch (ModKitException x) { run.Plan.Msg(run.Plan.Errors, mod, st.Archive, st.Name, x.Message + " (" + ModLine.Of(line) + ")"); }
            Own(run, n);
        }

        // ---- the ops ----

        static void DoSkills(Run run, Mod mod)
        {
            var all = new List<SkillOp>();
            foreach (SkillOp op in mod.Skills)
            {
                all.Add(op);
                foreach (KeyValuePair<long, long> o in op.Own)
                    all.Add(new SkillOp { Kind = SkillKind.Effect, Id = o.Value, Clone = o.Key, Line = op.Line, OwnedBy = op });
            }
            foreach (SkillOp op in all)
            {
                foreach (SkillSideCtx c in SkillSides(run, mod, op.Kind))
                {
                    try
                    {
                        if (op.IsClone) DoSkillClone(run, mod, c, op);
                        else DoSkillEdit(run, mod, c, op);
                    }
                    catch (ModKitException x) { run.Plan.Msg(run.Plan.Errors, mod, c.Archive, null, op.Tag + " " + op.Id + " (" + ModLine.Of(op.Line) + "): " + x.Message); }
                }
                CountCheck(run, mod, op);
            }
            foreach (JobSkillOp j in mod.JobSkills)
                foreach (SkillSideCtx c in SkillSides(run, mod, SkillKind.Skill))
                {
                    try { DoJobSkill(run, mod, c, j); }
                    catch (ModKitException x) { run.Plan.Msg(run.Plan.Errors, mod, c.Archive, JobEntry, "jobskill (" + ModLine.Of(j.Line) + "): " + x.Message); }
                }
        }

        static void DoSkillEdit(Run run, Mod mod, SkillSideCtx c, SkillOp op)
        {
            Plan plan = run.Plan;
            EntryState st;
            int variants;
            XmlElem root = SkillRoot(run, mod, c, op.Id, out st, out variants);
            if (root == null)
            {
                plan.Msg(plan.Warnings, mod, c.Archive, null, op.Tag + " " + op.Id + " is not on " + c.Label + ", skipped there (" + ModLine.Of(op.Line) + ")");
                return;
            }
            if (variants > 1)
                plan.Msg(plan.Warnings, mod, c.Archive, st.Name, op.Tag + " " + op.Id + " has " + variants + " rows (features or locales) on " + c.Label + "; the one this side reads is changed");
            Dictionary<int, XmlElem> picked = Picked(run, mod, c, op, root, st.Name, true);
            var tooltipLevels = new List<int>();
            foreach (SkillSet set in op.Sets)
                foreach (FieldHit h in Hits(run, mod, c, op, set, root, st.Name, picked))
                {
                    Change(run, mod, st, h.Elem, h.Attr, h.New, set.Line, false);
                    if (h.Field.Tooltip && h.Old != h.New && !tooltipLevels.Contains(h.Level)) tooltipLevels.Add(h.Level);
                }
            foreach (SkillRaw r in op.Raws)
            {
                if (r.Side != c.P.Name) continue;
                foreach (KeyValuePair<int, XmlElem> lv in picked)
                {
                    if (!SkillLevels.Has(r.Levels, lv.Key)) continue;
                    string err;
                    XmlElem at = r.At.Length == 0 ? lv.Value : RowPath.Parse(r.At).TryResolve(lv.Value, out err);
                    if (at == null) { plan.Msg(plan.Warnings, mod, c.Archive, st.Name, op.Tag + " " + op.Id + " level " + lv.Key + ": at=\"" + r.At + "\" is not there, skipped (" + ModLine.Of(r.Line) + ")"); continue; }
                    foreach (AttrOp a in r.Attrs)
                    {
                        string err2;
                        XmlElem el = string.IsNullOrEmpty(a.At) ? at : RowPath.Parse(a.At).TryResolve(at, out err2);
                        if (el == null) { plan.Msg(plan.Warnings, mod, c.Archive, st.Name, op.Tag + " " + op.Id + " level " + lv.Key + ": at=\"" + a.At + "\" is not there, skipped (" + ModLine.Of(r.Line) + ")"); continue; }
                        Change(run, mod, st, el, a.Name, a.Value, r.Line, a.Kind == AttrOpKind.Unset);
                        RefCheckValue(run, mod, c, st.Name, op, el.Name, a.Name, a.Value, el.Attr(a.Name));
                    }
                }
            }
            foreach (SkillSwap w in op.Swaps) DoSwap(run, mod, c, op, w, st, picked);
            DoTexts(run, mod, c, op, st, picked, tooltipLevels);
        }

        static void DoSwap(Run run, Mod mod, SkillSideCtx c, SkillOp op, SkillSwap w, EntryState st, Dictionary<int, XmlElem> picked)
        {
            Plan plan = run.Plan;
            EntryState fst;
            int v;
            XmlElem froot = SkillRoot(run, mod, c, w.FromSkill, out fst, out v);
            XmlElem flevel;
            Dictionary<int, XmlElem> fp = froot == null ? null : Picked(run, mod, c, op, froot, fst.Name, false);
            if (fp == null || !fp.TryGetValue(w.FromLevel, out flevel))
            {
                plan.Msg(plan.Warnings, mod, c.Archive, st.Name, op.Tag + " " + op.Id + ": skill " + w.FromSkill + " level " + w.FromLevel + " is not on " + c.Label + ", swap skipped (" + ModLine.Of(w.Line) + ")");
                return;
            }
            List<XmlElem> fm = Named(flevel, c.Side.Motion);
            if (w.FromMotion > fm.Count) { plan.Msg(plan.Warnings, mod, c.Archive, st.Name, op.Tag + " " + op.Id + ": skill " + w.FromSkill + " level " + w.FromLevel + " has " + fm.Count + " motions on " + c.Label + ", swap skipped (" + ModLine.Of(w.Line) + ")"); return; }
            XmlElem src = fm[w.FromMotion - 1];
            foreach (KeyValuePair<int, XmlElem> lv in picked)
            {
                if (!SkillLevels.Has(w.Levels, lv.Key)) continue;
                List<XmlElem> m = Named(lv.Value, c.Side.Motion);
                if (w.Motion > m.Count) { plan.Msg(plan.Warnings, mod, c.Archive, st.Name, op.Tag + " " + op.Id + " level " + lv.Key + ": motion " + w.Motion + " is not there on " + c.Label + " (" + m.Count + " motions), swap skipped (" + ModLine.Of(w.Line) + ")"); continue; }
                XmlElem target = m[w.Motion - 1];
                int n = plan.Changes.Count;
                string sel = RowPath.Canonical(target);
                try
                {
                    DoAdd(run, mod, st, new RowOp { Kind = RowOpKind.Add, After = sel, Xml = src.OuterText, FromIndent = src.Doc.IndentOf(src.Start), Line = w.Line, NoNewId = true, Sync = false });
                    DoDelete(run, mod, st, new RowOp { Kind = RowOpKind.Delete, At = sel, Line = w.Line, Sync = false });
                }
                catch (ModKitException x) { plan.Msg(plan.Errors, mod, st.Archive, st.Name, x.Message + " (" + ModLine.Of(w.Line) + ")"); }
                Own(run, n);
                NewRefChecks(run, mod, c, st.Name, op, src, target);
            }
        }

        // ---- tooltips and string rows ----

        static void DoTexts(Run run, Mod mod, SkillSideCtx c, SkillOp op, EntryState st, Dictionary<int, XmlElem> picked, List<int> changed)
        {
            Plan plan = run.Plan;
            var covered = new List<int>();
            foreach (SkillText t in op.Texts)
            {
                string value = c.Bundle ? t.KmsParam : t.GmsText;
                foreach (KeyValuePair<int, XmlElem> lv in picked)
                {
                    if (!SkillLevels.Has(t.Levels, lv.Key)) continue;
                    if (value == null) continue;
                    covered.Add(lv.Key);
                    if (c.Bundle) Change(run, mod, st, lv.Value, "stringParam", value, t.Line, false);
                    else SetStringRow(run, mod, c, op.Id, lv.Key, "text", value, t.Lang, t.Line);
                }
            }
            var left = new List<int>();
            foreach (int l in changed) if (!covered.Contains(l)) left.Add(l);
            left.Sort();
            if (left.Count > 0)
                plan.Msg(plan.Warnings, mod, c.Archive, st.Name, op.Tag + " " + op.Id + " level " + LevelText(left) + ": a number the tooltip shows is changed and the tooltip is not ("
                    + (c.Bundle ? "kms-client fills it from level@stringParam: set <tooltip kms-param=...>" : c.Label + " writes the numbers in the description text: set <tooltip gms-text=...>") + ")");
        }

        /// <summary>The string files of one role on a side, per language: lang -> entries.</summary>
        static List<KeyValuePair<string, List<string>>> StringFiles(SkillSideCtx c, SkillString s)
        {
            var l = new List<KeyValuePair<string, List<string>>>();
            foreach (string lang in c.P.Languages)
            {
                var files = new List<string>();
                foreach (M2dEntry e in c.Arch.Entries)
                    if (InFolder(e.Name, "string/" + lang + "/" + s.Files)) files.Add(e.Name);
                files.Sort(StringComparer.Ordinal);
                if (files.Count > 0) l.Add(new KeyValuePair<string, List<string>>(lang, files));
            }
            return l;
        }

        static SkillString StringRole(SkillSideCtx c, string role)
        {
            foreach (SkillString s in c.Map.Strings) if (s.Bundle == c.Bundle && s.Role == role) return s;
            return null;
        }

        /// <summary>
        /// Sets the text of one string row (id, level) in one language of the side (lang null: every language); adds
        /// the row to that language (null: the region's) when it has none.
        /// </summary>
        static void SetStringRow(Run run, Mod mod, SkillSideCtx c, long id, int level, string role, string value, string only, int line)
        {
            SkillString s = StringRole(c, role);
            if (s == null) return;
            if (only != null && !c.P.ReadsLanguage(only))
            {
                run.Plan.Msg(run.Plan.Warnings, mod, c.Archive, null, "id " + id + " level " + level + ": " + c.Label + " has no language \"" + only + "\", the text is not written there (" + ModLine.Of(line) + ")");
                return;
            }
            string ids = id.ToString(CultureInfo.InvariantCulture), lvs = level.ToString(CultureInfo.InvariantCulture);
            bool any = false;
            foreach (KeyValuePair<string, List<string>> lang in StringFiles(c, s))
                if (only == null || lang.Key == only)
                foreach (string f in lang.Value)
                {
                    EntryState st = run.State(c.Arch, c.Archive, f);
                    if (st.Base == null || !EnsureDoc(run.Plan, mod, st)) continue;
                    foreach (XmlElem r in st.Doc.Root.Children)
                        if (r.Attr("id") == ids && (!s.Leveled || r.Attr("level") == lvs)) { Change(run, mod, st, r, s.Attr, value, line, false); any = true; }
                }
            if (any) return;
            string lang0 = only ?? OwnLanguage(c.P);
            string file = "string/" + lang0 + "/" + (s.NewFile ?? s.Files);
            var sb = new StringBuilder("<key");
            Attr(sb, "id", ids);
            if (s.Leveled) Attr(sb, "level", lvs);
            Attr(sb, s.Attr, value);
            sb.Append(" />");
            AddStringRow(run, mod, c, file, sb.ToString(), "", line);
        }

        static void Attr(StringBuilder sb, string n, string v) { sb.Append(' ').Append(n).Append("=\"").Append(XmlEscape(v)).Append('"'); }

        static string XmlEscape(string v)
        {
            return v.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        static void AddStringRow(Run run, Mod mod, SkillSideCtx c, string file, string xml, string fromIndent, int line)
        {
            EntryState st = run.State(c.Arch, c.Archive, file);
            if (st.Base == null)
            {
                run.Plan.Msg(run.Plan.Warnings, mod, c.Archive, file, "no such string file on " + c.Label + ", the row is not added: " + xml);
                return;
            }
            if (!EnsureDoc(run.Plan, mod, st)) return;
            int n = run.Plan.Changes.Count;
            try { DoAdd(run, mod, st, new RowOp { Kind = RowOpKind.Add, Xml = xml, FromIndent = fromIndent, Line = line, NoNewId = true, Sync = false, Grouped = true }); }
            catch (ModKitException x) { run.Plan.Msg(run.Plan.Errors, mod, c.Archive, file, x.Message + " (" + ModLine.Of(line) + ")"); }
            Own(run, n);
        }

        /// <summary>A clone's string rows: the source's rows copied with the new id (only the kept levels), the name overridden.</summary>
        static SkillText GmsTextFor(SkillOp op, int level)
        {
            SkillText v = null;
            foreach (SkillText t in op.Texts) if (t.GmsText != null && SkillLevels.Has(t.Levels, level)) v = t;
            return v;
        }

        /// <summary>Returns the levels whose copied text row got the mod's text (in the text's language).</summary>
        static HashSet<int> CloneStrings(Run run, Mod mod, SkillSideCtx c, SkillOp op, ICollection<int> kept)
        {
            var texted = new HashSet<int>();
            string src = op.Clone.ToString(CultureInfo.InvariantCulture), dst = op.Id.ToString(CultureInfo.InvariantCulture);
            var done = new HashSet<string>(StringComparer.Ordinal);
            bool named = false;
            foreach (SkillString s in c.Map.Strings)
            {
                if (s.Bundle != c.Bundle) continue;
                if (!done.Add(s.Files)) { if (s.Role == "name") named |= done.Contains(s.Files); continue; }
                foreach (KeyValuePair<string, List<string>> lang in StringFiles(c, s))
                {
                    var rows = new List<XmlElem>();
                    string from = null;
                    foreach (string f in lang.Value)
                    {
                        EntryState st = run.State(c.Arch, c.Archive, f);
                        if (st.Base == null || !EnsureDoc(run.Plan, mod, st)) continue;
                        foreach (XmlElem r in st.Doc.Root.Children)
                        {
                            if (r.Attr("id") != src) continue;
                            int lv;
                            if (s.Leveled && int.TryParse(r.Attr("level") ?? "", out lv) && kept != null && !kept.Contains(lv)) continue;
                            rows.Add(r);
                            if (from == null) from = f;
                        }
                    }
                    if (rows.Count == 0) continue;
                    // files side: new rows go to the generic file of the language when it is there
                    string target = from;
                    if (!c.Bundle && s.NewFile != null)
                    {
                        string g = "string/" + lang.Key + "/" + s.NewFile;
                        if (c.Arch.Find(g) != null) target = g;
                    }
                    foreach (XmlElem r in rows)
                    {
                        XmlDoc fd = XmlDoc.ParseFragment(r.OuterText, "string row");
                        var ed = new XmlEditor(fd);
                        ed.SetAttr(fd.Root, "id", dst);
                        foreach (SkillString s2 in c.Map.Strings)
                            if (s2.Bundle == c.Bundle && s2.Files == s.Files && s2.Role == "name" && op.Name != null && fd.Root.FindAttr(s2.Attr) != null)
                            { ed.SetAttr(fd.Root, s2.Attr, op.Name); named = true; }
                        int rl;
                        if (!c.Bundle && int.TryParse(r.Attr("level") ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out rl))
                            foreach (SkillString s2 in c.Map.Strings)
                                if (s2.Bundle == c.Bundle && s2.Files == s.Files && s2.Role == "text" && fd.Root.FindAttr(s2.Attr) != null)
                                {
                                    SkillText gt = GmsTextFor(op, rl);
                                    if (gt != null && gt.Lang == lang.Key) { ed.SetAttr(fd.Root, s2.Attr, gt.GmsText); texted.Add(rl); }
                                }
                        AddStringRow(run, mod, c, target, ed.Apply(), r.Doc.IndentOf(r.Start), op.Line);
                    }
                    if (s.Role == "name") named = true;
                }
            }
            if (op.Name != null && !named)
            {
                SkillString ns = StringRole(c, "name");
                if (ns != null)
                {
                    var sb = new StringBuilder("<key");
                    Attr(sb, "id", dst);
                    if (ns.Leveled) Attr(sb, "level", "1");
                    Attr(sb, ns.Attr, op.Name);
                    sb.Append(" />");
                    AddStringRow(run, mod, c, "string/" + (c.P.Region == "kr" ? "kr" : "en") + "/" + (ns.NewFile ?? ns.Files), sb.ToString(), "", op.Line);
                }
            }
            return texted;
        }

        // ---- clones ----

        static void StripVariant(XmlEditor ed, XmlElem e)
        {
            if (e.FindAttr("feature") != null) ed.RemoveAttr(e, "feature");
            if (e.FindAttr("locale") != null) ed.RemoveAttr(e, "locale");
        }

        /// <summary>Drops the variants the side does not read under e (siblings of one name and id or level), recursively.</summary>
        static void ResolveVariants(XmlEditor ed, XmlElem e, FeatureGate gate, SkillSide side, List<XmlElem> dropped)
        {
            var groups = new Dictionary<string, List<XmlElem>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (XmlElem c in e.Children)
            {
                if (dropped.Contains(c)) continue;
                string k = c.Name + "\n" + (c.Attr("id") ?? (c.Name == side.Level ? LevelOf(c, side) : "") ?? "");
                List<XmlElem> l;
                if (!groups.TryGetValue(k, out l)) { groups[k] = l = new List<XmlElem>(); order.Add(k); }
                l.Add(c);
            }
            foreach (string k in order)
            {
                List<XmlElem> l = groups[k];
                bool variant = false;
                foreach (XmlElem c in l) if (!string.IsNullOrEmpty(c.Attr("feature")) || !string.IsNullOrEmpty(c.Attr("locale"))) variant = true;
                if (!variant) { foreach (XmlElem c in l) { StripVariant(ed, c); ResolveVariants(ed, c, gate, side, dropped); } continue; }
                XmlElem w = gate.Pick(l);
                foreach (XmlElem c in l)
                {
                    if (c == w) { StripVariant(ed, c); ResolveVariants(ed, c, gate, side, dropped); }
                    else { ed.Delete(c); dropped.Add(c); }
                }
            }
        }

        static string NewIdText(SkillOp op, string table, string value, out bool changed)
        {
            changed = false;
            var parts = value.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                long v;
                if (!long.TryParse(parts[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out v)) continue;
                long to = -1;
                SkillOp skillOp = op.OwnedBy ?? op;
                if ((table == "skill" || table == "either") && op.Kind == SkillKind.Skill && v == op.Clone) to = op.Id;
                if (table == "effect" || table == "either")
                    foreach (KeyValuePair<long, long> o in skillOp.Own) if (o.Key == v) to = o.Value;
                if (to >= 0) { parts[i] = parts[i].Replace(v.ToString(CultureInfo.InvariantCulture), to.ToString(CultureInfo.InvariantCulture)); changed = true; }
            }
            return string.Join(",", parts);
        }

        static void Repoint(XmlEditor ed, XmlElem e, SkillSideCtx c, SkillOp op, List<XmlElem> dropped)
        {
            if (dropped.Contains(e)) return;   // deleted with a level or variant the clone does not keep
            foreach (XmlAttr a in e.Attrs)
            {
                string table = c.Map.RefTo(c.Bundle, e.Name, a.Name);
                if (table == null || a.Value.Length == 0) continue;
                bool changed;
                string v = NewIdText(op, table, a.Value, out changed);
                if (changed) ed.SetAttr(e, a.Name, v);
            }
            foreach (XmlElem k in e.Children) Repoint(ed, k, c, op, dropped);
        }

        static bool Inside(XmlElem e, List<XmlElem> dropped)
        {
            for (XmlElem p = e; p != null; p = p.Parent) if (dropped.Contains(p)) return true;
            return false;
        }

        /// <summary>
        /// A clone's basic/stateAttr@cooldownGroupID: kept unless the mod sets cooldown-group ("own" = the new id). A kept
        /// group other than 0 is a warning: the clone and its source then share one cooldown.
        /// </summary>
        static void CooldownGroup(Plan plan, Mod mod, SkillSideCtx c, XmlEditor ed, XmlElem root, SkillOp op, string what, List<string> notes)
        {
            XmlElem sa = Child(root, "basic/stateAttr");
            string old = sa == null ? null : sa.Attr("cooldownGroupID");
            if (op.CooldownGroup == null)
            {
                if (old != null && old.Trim() != "0" && old.Trim().Length > 0)
                    plan.Msg(plan.Warnings, mod, c.Archive, null, what + ": shares cooldown group " + old.Trim() + " with its source on " + c.Label + " (using one starts the other's cooldown); set cooldown-group=\"own\" to give it its own (" + ModLine.Of(op.Line) + ")");
                return;
            }
            string v = op.CooldownGroup == "own" ? op.Id.ToString(CultureInfo.InvariantCulture) : op.CooldownGroup;
            if (sa == null) { plan.Msg(plan.Warnings, mod, c.Archive, null, what + ": " + op.Clone + " has no basic/stateAttr on " + c.Label + ", cooldown-group not written"); return; }
            if (old == v) return;
            ed.SetAttr(sa, "cooldownGroupID", v);
            notes.Add("cooldownGroupID " + (old ?? "(none)") + " -> " + v);
        }

        static void DoSkillClone(Run run, Mod mod, SkillSideCtx c, SkillOp op)
        {
            Plan plan = run.Plan;
            EntryState sst;
            int variants;
            XmlElem sroot = SkillRoot(run, mod, c, op.Clone, out sst, out variants);
            string what = op.Tag + " " + op.Id + " (clone of " + op.Clone + ")";
            if (sroot == null) { plan.Msg(plan.Warnings, mod, c.Archive, null, what + ": " + op.Clone + " is not on " + c.Label + ", skipped there (" + ModLine.Of(op.Line) + ")"); return; }
            if (Index(run, c).Where.ContainsKey(op.Id)) { plan.Msg(plan.Warnings, mod, c.Archive, null, what + ": " + c.Label + " ships id " + op.Id + " already, skipped there"); return; }

            // the source as its own document: a row fragment (bundle) or the whole file
            XmlDoc doc = c.Bundle ? XmlDoc.ParseFragment(sroot.OuterText, c.Archive + " " + sst.Name + " row " + op.Clone) : XmlDoc.Parse(sst.Base, c.Archive + " " + sst.Name);
            XmlElem root = doc.Root;
            var ed = new XmlEditor(doc);
            if (c.Bundle) { ed.SetAttr(root, c.Side.Key, op.Id.ToString(CultureInfo.InvariantCulture)); StripVariant(ed, root); }
            var dropped = new List<XmlElem>();
            List<int> keep = SkillLevels.Parse(op.Levels);
            if (keep != null)
                foreach (KeyValuePair<int, List<XmlElem>> kv in Levels(root, c.Side))
                    if (!keep.Contains(kv.Key)) foreach (XmlElem l in kv.Value) { ed.Delete(l); dropped.Add(l); }
            ResolveVariants(ed, root, c.Gate, c.Side, dropped);
            var kept = new List<int>();
            var picked = new Dictionary<int, XmlElem>();
            foreach (KeyValuePair<int, List<XmlElem>> kv in Levels(root, c.Side))
                foreach (XmlElem l in kv.Value)
                {
                    if (Inside(l, dropped)) continue;
                    picked[kv.Key] = l;
                    if (!kept.Contains(kv.Key)) kept.Add(kv.Key);
                }
            if (keep != null)
            {
                var missing = new List<int>();
                foreach (int l in keep) if (!kept.Contains(l)) missing.Add(l);
                if (missing.Count > 0) plan.Msg(plan.Warnings, mod, c.Archive, null, what + ": level " + LevelText(missing) + " is not in " + op.Clone + " on " + c.Label);
            }
            if (kept.Count == 0) { plan.Msg(plan.Warnings, mod, c.Archive, null, what + ": no level of " + op.Clone + " is read on " + c.Label + ", skipped there"); return; }
            Repoint(ed, root, c, op, dropped);
            var notes = new List<string>();
            if (op.Kind == SkillKind.Skill) CooldownGroup(plan, mod, c, ed, root, op, what, notes);
            var tooltip = new List<int>();
            foreach (SkillSet set in op.Sets)
                foreach (FieldHit h in Hits(run, mod, c, op, set, root, null, picked))
                {
                    ed.SetAttr(h.Elem, h.Attr, h.New);
                    notes.Add("level " + h.Level + " " + h.Field.Name + " " + (h.Old ?? "(none)") + " -> " + h.New);
                    if (h.Field.Tooltip && h.Old != h.New && !tooltip.Contains(h.Level)) tooltip.Add(h.Level);
                }
            foreach (SkillRaw r in op.Raws)
            {
                if (r.Side != c.P.Name) continue;
                foreach (KeyValuePair<int, XmlElem> lv in picked)
                {
                    if (!SkillLevels.Has(r.Levels, lv.Key)) continue;
                    string err;
                    XmlElem at = r.At.Length == 0 ? lv.Value : RowPath.Parse(r.At).TryResolve(lv.Value, out err);
                    if (at == null) { plan.Msg(plan.Warnings, mod, c.Archive, null, what + " level " + lv.Key + ": at=\"" + r.At + "\" is not there, skipped (" + ModLine.Of(r.Line) + ")"); continue; }
                    foreach (AttrOp a in r.Attrs)
                    {
                        XmlElem el = string.IsNullOrEmpty(a.At) ? at : RowPath.Parse(a.At).TryResolve(at, out err);
                        if (el == null) continue;
                        if (a.Kind == AttrOpKind.Unset) ed.RemoveAttr(el, a.Name); else ed.SetAttr(el, a.Name, a.Value);
                        notes.Add("level " + lv.Key + " " + RowPath.NamePath(el, false) + "@" + a.Name + " " + (el.Attr(a.Name) ?? "(none)") + " -> " + (a.Value ?? "(removed)"));
                    }
                }
            }
            foreach (SkillText t in op.Texts)
                if (c.Bundle && t.KmsParam != null)
                    foreach (KeyValuePair<int, XmlElem> lv in picked)
                        if (SkillLevels.Has(t.Levels, lv.Key)) { ed.SetAttr(lv.Value, "stringParam", t.KmsParam); tooltip.Remove(lv.Key); }
            string text = ed.Apply();
            XmlDoc made = c.Bundle ? XmlDoc.ParseFragment(text, "the clone") : XmlDoc.Parse(doc.Encode(text), "the clone");

            int n = plan.Changes.Count;
            string entry;
            if (c.Bundle)
            {
                entry = SkillMap.Fill(c.Side.NewEntry, op.Id);
                EntryState st = run.State(c.Arch, c.Archive, entry);
                if (st.Base == null && st.DeletedBy == null)
                {
                    st.Base = Encoding.UTF8.GetBytes(NewBundle); st.BaseMod = mod;
                    plan.Changes.Add(Mark(run, new PlanChange { Kind = ChangeKind.AddEntry, ModId = mod.Id, Archive = c.Archive, Entry = st.Name, NewValue = "new " + c.Side.Row + " bundle file" }));
                }
                if (!EnsureDoc(plan, mod, st)) return;
                DoAdd(run, mod, st, new RowOp { Kind = RowOpKind.Add, Xml = text, FromIndent = sst.Doc.IndentOf(sroot.Start), Line = op.Line, Sync = false });
            }
            else
            {
                entry = c.Side.EntryOf(op.Id);
                EntryState st = run.State(c.Arch, c.Archive, entry);
                if (st.Base != null) { plan.Msg(plan.Warnings, mod, c.Archive, entry, what + ": the entry is there already, skipped"); return; }
                byte[] bytes = doc.Encode(text);
                st.Base = bytes; st.BaseMod = mod; st.DeletedBy = null;
                plan.Changes.Add(Mark(run, new PlanChange { Kind = ChangeKind.AddEntry, ModId = mod.Id, Archive = c.Archive, Entry = st.Name, NewValue = "clone of " + sst.Name + ", " + bytes.Length + " bytes" }));
                plan.NewIds.Add(new NewId { Mod = mod, Archive = c.Archive, Entry = st.Name });
            }
            Own(run, n);
            plan.Msg(plan.Notes, mod, c.Archive, entry, what + " on " + c.Label + ": level " + LevelText(kept) + (notes.Count > 0 ? "; " + string.Join("; ", notes.ToArray()) : ""));
            if (op.OwnedBy == null || op.Kind == SkillKind.Skill)
            {
                List<int> left = new List<int>(tooltip);
                if (!c.Bundle) foreach (SkillText t in op.Texts) if (t.GmsText != null) left.RemoveAll(l => SkillLevels.Has(t.Levels, l));
                if (left.Count > 0) plan.Msg(plan.Warnings, mod, c.Archive, entry, what + " level " + LevelText(left) + ": a number the tooltip shows is changed and the tooltip is not; set <tooltip>");
            }
            HashSet<int> texted = CloneStrings(run, mod, c, op, kept);
            if (!c.Bundle)
                foreach (int l in kept)
                {
                    SkillText gt = GmsTextFor(op, l);
                    if (gt != null && !texted.Contains(l)) SetStringRow(run, mod, c, op.Id, l, "text", gt.GmsText, gt.Lang, op.Line);
                }
            NewRefChecks(run, mod, c, entry, op, made.Root, sroot);
            IconCheck(run, mod, c, entry, op, made.Root);
            RangeCheck(run, mod, c, entry, op);
        }

        // ---- job skill trees ----

        internal static int[] ParsePos(string p) { return SkillJobs.ParsePos(p); }

        static void DoJobSkill(Run run, Mod mod, SkillSideCtx c, JobSkillOp j)
        {
            Plan plan = run.Plan;
            EntryState st = run.State(c.Arch, c.Archive, JobEntry);
            if (st.Base == null) { plan.Msg(plan.Warnings, mod, c.Archive, JobEntry, "no " + JobEntry + " on " + c.Label + ", jobskill skipped (" + ModLine.Of(j.Line) + ")"); return; }
            if (!EnsureDoc(plan, mod, st)) return;
            string jobKey = c.Bundle ? "jobGroupID" : "code";
            string jobVal = c.Bundle ? j.Job.ToString(CultureInfo.InvariantCulture) : j.Job.ToString("000", CultureInfo.InvariantCulture);
            List<XmlElem> jobs = new List<XmlElem>();
            foreach (XmlElem e in st.Doc.Root.Children) if (e.Name == "job" && e.Attr(jobKey) == jobVal) jobs.Add(e);
            XmlElem job = c.Gate.Pick(jobs);
            string tag = "jobskill job " + j.Job + " skill " + j.Skill;
            if (job == null) { plan.Msg(plan.Warnings, mod, c.Archive, JobEntry, tag + ": job " + jobVal + " is not read on " + c.Label + ", skipped (" + ModLine.Of(j.Line) + ")"); return; }
            if (jobs.Count > 1) plan.Msg(plan.Warnings, mod, c.Archive, JobEntry, tag + ": job " + jobVal + " is written " + jobs.Count + " times (features) on " + c.Label + "; the one this side reads (feature \"" + (job.Attr("feature") ?? "") + "\") is changed");
            XmlElem skills = Child(job, "skills");
            if (skills == null) { plan.Msg(plan.Warnings, mod, c.Archive, JobEntry, tag + ": job " + jobVal + " has no <skills> on " + c.Label + ", skipped"); return; }
            string row = c.Bundle ? "v" : "skill", idAttr = c.Bundle ? "skillID" : "main";
            var missing = new List<string>();
            var fields = new List<KeyValuePair<string, string>>();
            if (j.MaxLevel != null) fields.Add(new KeyValuePair<string, string>("maxLevel", j.MaxLevel));
            if (c.Bundle)
            {
                if (j.LearnLevel != null) fields.Add(new KeyValuePair<string, string>("requireLevel", j.LearnLevel));
                if (j.RequireSkills != null) fields.Add(new KeyValuePair<string, string>("requireSkillIDs", j.RequireSkills));
                if (j.RequireLevels != null) fields.Add(new KeyValuePair<string, string>("requireSkillLevels", j.RequireLevels));
            }
            else
            {
                if (j.LearnLevel != null) missing.Add("learn-level");
                if (j.RequireSkills != null) missing.Add("require-skills");
                if (j.RequireLevels != null) missing.Add("require-levels");
            }
            if (missing.Count > 0)
                plan.Msg(plan.Warnings, mod, c.Archive, JobEntry, tag + ": " + c.Label + "'s job.xml has no field for " + string.Join(", ", missing.ToArray()) + " (the skill's own file holds such conditions), not written there");
            if (!Index(run, c).Where.ContainsKey(j.Skill) && !ModMakes(mod, SkillKind.Skill, j.Skill))
                plan.Msg(plan.Warnings, mod, c.Archive, JobEntry, tag + ": skill " + j.Skill + " is neither on " + c.Label + " nor made by this mod");
            int n = plan.Changes.Count;
            if (j.Replace >= 0)
            {
                string rv = j.Replace.ToString(CultureInfo.InvariantCulture);
                XmlElem slot = null;
                foreach (XmlElem e in skills.Children) if (e.Name == row && e.Attr(idAttr) == rv) { slot = e; break; }
                if (slot == null) { plan.Msg(plan.Warnings, mod, c.Archive, JobEntry, tag + ": job " + jobVal + " has no slot with skill " + rv + " on " + c.Label + ", skipped (" + ModLine.Of(j.Line) + ")"); return; }
                Change(run, mod, st, slot, idAttr, j.Skill.ToString(CultureInfo.InvariantCulture), j.Line, false);
                foreach (KeyValuePair<string, string> f in fields) Change(run, mod, st, slot, f.Key, f.Value, j.Line, false);
            }
            else
            {
                int[] pos = SkillJobs.ParsePos(j.Position);
                string tierKey = c.Bundle ? "jobLevel" : "subJobCode";
                string tierVal = c.Bundle ? j.Tier.ToString(CultureInfo.InvariantCulture) : j.Tier == 2 ? (j.Job * 10 + 1).ToString(CultureInfo.InvariantCulture) : "";
                foreach (XmlElem e in skills.Children)
                {
                    if (e.Name != row || (e.Attr(tierKey) ?? "") != tierVal) continue;
                    int[] p = SkillJobs.ParsePos(e.Attr("uiPosition") ?? "");
                    if (p != null && p[0] == pos[0] && p[1] == pos[1])
                        plan.Msg(plan.Warnings, mod, c.Archive, JobEntry, tag + ": position " + pos[0] + "," + pos[1] + " of tier " + j.Tier + " is taken by skill " + e.Attr(idAttr) + " on " + c.Label + " (line " + e.Line + ")");
                }
                var sb = new StringBuilder("<" + row);
                Attr(sb, idAttr, j.Skill.ToString(CultureInfo.InvariantCulture));
                if (tierVal.Length > 0) Attr(sb, tierKey, tierVal);
                Attr(sb, "uiPosition", c.Bundle ? pos[0] + "," + pos[1] : pos[0] + ", " + pos[1]);
                foreach (KeyValuePair<string, string> f in fields) Attr(sb, f.Key, f.Value);
                sb.Append(" />");
                try { DoAdd(run, mod, st, new RowOp { Kind = RowOpKind.Add, Xml = sb.ToString(), FromIndent = "", In = RowPath.Canonical(skills), Line = j.Line, NoNewId = true, Sync = false }); }
                catch (ModKitException x) { plan.Msg(plan.Errors, mod, c.Archive, JobEntry, x.Message + " (" + ModLine.Of(j.Line) + ")"); }
            }
            Own(run, n);
        }

        static bool ModMakes(Mod mod, SkillKind kind, long id)
        {
            foreach (SkillOp s in mod.Skills)
            {
                if (s.Kind == kind && s.IsClone && s.Id == id) return true;
                if (kind == SkillKind.Effect) foreach (KeyValuePair<long, long> o in s.Own) if (o.Value == id) return true;
            }
            return false;
        }

        // ---- checks (warnings) ----

        sealed class RefFacts
        {
            internal readonly HashSet<string> Refs = new HashSet<string>(StringComparer.Ordinal);      // "skill 123", "effect 456"
            internal readonly HashSet<string> Files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> Sequences = new HashSet<string>(StringComparer.Ordinal);
        }

        static readonly string[] SequenceAttrs = { "sequenceName" };

        static void Collect(SkillSideCtx c, XmlElem e, RefFacts f)
        {
            foreach (XmlAttr a in e.Attrs)
            {
                if (a.Value.Length == 0) continue;
                string table = c.Map.RefTo(c.Bundle, e.Name, a.Name);
                if (table != null)
                    foreach (string part in a.Value.Split(','))
                    {
                        long v;
                        if (long.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out v) && v != 0) f.Refs.Add(table + " " + v.ToString(CultureInfo.InvariantCulture));
                    }
                if (Array.IndexOf(SequenceAttrs, a.Name) >= 0) f.Sequences.Add(a.Value);
                if (a.Value.IndexOf(".xml", StringComparison.OrdinalIgnoreCase) > 0)
                    foreach (string part in a.Value.Split(','))
                    {
                        string p = part.Trim();
                        int eq = p.IndexOf('=');
                        if (eq >= 0) p = p.Substring(eq + 1);
                        if (XmlPathRx.IsMatch(p) && p.IndexOf('/') > 0) f.Files.Add(p);
                    }
            }
            foreach (XmlElem k in e.Children) Collect(c, k, f);
        }

        static bool RefExists(Run run, Mod mod, SkillSideCtx c, string table, long id)
        {
            foreach (SkillKind k in new[] { SkillKind.Skill, SkillKind.Effect })
            {
                if (table != "either" && table != (k == SkillKind.Skill ? "skill" : "effect")) continue;
                var cc = new SkillSideCtx { P = c.P, Bundle = c.Bundle, Archive = c.Archive, Arch = c.Arch, Gate = c.Gate, Map = SkillMap.Default.Entity(k) };
                if (Index(run, cc).Where.ContainsKey(id) || ModMakes(mod, k, id)) return true;
            }
            return false;
        }

        static HashSet<string> PlayerSequences(Run run, SkillSideCtx c)
        {
            string key = "playerseq\n" + c.Archive;
            object o;
            if (run.Data.Cache.TryGetValue(key, out o)) return (HashSet<string>)o;
            HashSet<string> s = null;
            M2dEntry e = c.Arch.Find(AnimationEntry);
            if (e != null)
            {
                s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    XmlDoc d = XmlDoc.Parse(c.Arch.Read(e), c.Archive + " " + AnimationEntry);
                    foreach (XmlElem k in d.Root.Children)
                    {
                        string n = (k.Attr("name") ?? "").ToLowerInvariant();
                        if (k.Name != "kfm" || (n != "dummymale" && n != "dummyfemale")) continue;
                        foreach (XmlElem q in k.Children) if (q.Name == "seq" && q.Attr("name") != null) s.Add(q.Attr("name"));
                    }
                }
                catch (ModKitException) { s = null; }
            }
            run.Data.Cache[key] = s;
            return s;
        }

        /// <summary>Warnings for what made has and was does not: dangling ids, effect files, sequences.</summary>
        static void NewRefChecks(Run run, Mod mod, SkillSideCtx c, string entry, SkillOp op, XmlElem made, XmlElem was)
        {
            Plan plan = run.Plan;
            var now = new RefFacts();
            var before = new RefFacts();
            Collect(c, made, now);
            if (was != null) Collect(c, was, before);
            foreach (string r in now.Refs)
            {
                if (before.Refs.Contains(r)) continue;
                int sp = r.IndexOf(' ');
                long id = long.Parse(r.Substring(sp + 1), CultureInfo.InvariantCulture);
                if (id == op.Id) continue;
                if (!RefExists(run, mod, c, r.Substring(0, sp), id))
                    plan.Msg(plan.Warnings, mod, c.Archive, entry, op.Tag + " " + op.Id + ": refers to " + r + ", which " + c.Label + " does not have and this mod does not make");
            }
            foreach (string f in now.Files)
            {
                if (before.Files.Contains(f)) continue;
                if (c.Arch.Find("effect/" + f.ToLowerInvariant()) == null)
                    plan.Msg(plan.Warnings, mod, c.Archive, entry, op.Tag + " " + op.Id + ": effect file " + f + " is not in the effect/ folder of " + c.Label);
            }
            HashSet<string> seq = PlayerSequences(run, c);
            if (seq != null)
                foreach (string s in now.Sequences)
                    if (!before.Sequences.Contains(s) && !seq.Contains(s))
                        plan.Msg(plan.Warnings, mod, c.Archive, entry, op.Tag + " " + op.Id + ": sequence " + s + " is not a player animation sequence (" + AnimationEntry + " dummymale/dummyfemale) on " + c.Label);
        }

        static void RefCheckValue(Run run, Mod mod, SkillSideCtx c, string entry, SkillOp op, string element, string attr, string value, string old)
        {
            if (value == null) return;
            var made = XmlDoc.ParseFragment("<" + element + " " + attr + "=\"" + XmlEscape(value) + "\" />", "value");
            XmlElem was = old == null ? null : XmlDoc.ParseFragment("<" + element + " " + attr + "=\"" + XmlEscape(old) + "\" />", "value").Root;
            NewRefChecks(run, mod, c, entry, op, made.Root, was);
        }

        static void IconCheck(Run run, Mod mod, SkillSideCtx c, string entry, SkillOp op, XmlElem made)
        {
            var icons = new List<string>();
            CollectIcons(made, icons);
            if (icons.Count == 0) return;
            ImageSource src = Images(run, c.P);
            if (src == null || !src.Available) return;
            foreach (string i in icons)
                if (!src.Has(Icons.ImageEntry(i)))
                    run.Plan.Msg(run.Plan.Warnings, mod, c.Archive, entry, op.Tag + " " + op.Id + ": icon " + i + " is not in " + Icons.ImageArchive + " of " + c.Label);
        }

        static void CollectIcons(XmlElem e, List<string> l)
        {
            // skills and the kms effect bundles: <ui icon>; the per-id effect files (gms-client, server): <UIProperty icon>
            if (e.Name.Equals("ui", StringComparison.OrdinalIgnoreCase) || e.Name.Equals("UIProperty", StringComparison.OrdinalIgnoreCase))
            {
                string i = e.Attr("icon");
                if (!string.IsNullOrEmpty(i) && !l.Contains(i)) l.Add(i);
            }
            foreach (XmlElem k in e.Children) CollectIcons(k, l);
        }

        static ImageSource Images(Run run, Profile p)
        {
            string key = "images\n" + p.Name;
            object o;
            if (run.Data.Cache.TryGetValue(key, out o)) return (ImageSource)o;
            ImageSource s = null;
            try { s = new ImageSource(run.Data, p); } catch (ModKitException) { }
            run.Data.Cache[key] = s;
            return s;
        }

        static void RangeCheck(Run run, Mod mod, SkillSideCtx c, string entry, SkillOp op)
        {
            string space = op.Kind == SkillKind.Skill ? IdSpaces.SkillSpace : IdSpaces.EffectSpace;
            long from, to;
            IdSpaces.DefaultRange(space, out from, out to);
            foreach (IdRange r in mod.Ids) if (r.Space == space && op.Id >= r.From && op.Id <= r.To) return;   // the mod declares its own range
            if (op.Id < from || op.Id > to)
                run.Plan.Msg(run.Plan.Warnings, mod, c.Archive, entry, op.Tag + " " + op.Id + ": new " + op.Tag + " ids belong in " + from + "-" + to + " (the range kept free for mods)");
        }

        /// <summary>Motions and attacks of an edited level that differ in number between the sides (a position names a different block).</summary>
        static void CountCheck(Run run, Mod mod, SkillOp op)
        {
            if (op.Kind != SkillKind.Skill) return;
            bool any = false;
            foreach (SkillSet s in op.Sets) if (s.Scope != SkillScope.Level) any = true;
            if (op.Swaps.Count > 0) any = true;
            if (!any) return;
            long id = op.IsClone ? op.Clone : op.Id;
            var shapes = new List<KeyValuePair<string, Dictionary<int, string>>>();
            foreach (SkillSideCtx c in SkillSides(run, mod, op.Kind))
            {
                EntryState st;
                int v;
                XmlElem root = SkillRoot(run, mod, c, id, out st, out v);
                if (root == null) continue;
                var shape = new Dictionary<int, string>();
                foreach (KeyValuePair<int, XmlElem> lv in Picked(run, mod, c, op, root, st.Name, false))
                {
                    var sb = new StringBuilder();
                    foreach (XmlElem m in Named(lv.Value, c.Side.Motion)) sb.Append(Named(m, c.Side.Attack).Count).Append(',');
                    shape[lv.Key] = sb.ToString();
                }
                shapes.Add(new KeyValuePair<string, Dictionary<int, string>>(c.Label, shape));
            }
            for (int i = 1; i < shapes.Count; i++)
            {
                var diff = new List<int>();
                foreach (KeyValuePair<int, string> kv in shapes[0].Value)
                {
                    string o;
                    if (shapes[i].Value.TryGetValue(kv.Key, out o) && o != kv.Value) diff.Add(kv.Key);
                }
                if (diff.Count > 0)
                    run.Plan.Msg(run.Plan.Warnings, mod, null, null, op.Tag + " " + id + " level " + LevelText(diff) + ": " + shapes[0].Key + " and " + shapes[i].Key + " have a different number of motions or attacks, so motion and attack positions may name different blocks");
            }
        }
    }

    /// <summary>Job skill tree helpers.</summary>
    public static class SkillJobs
    {
        /// <summary>"x,y" or "x, y" -> {x, y}; null when it is not that.</summary>
        public static int[] ParsePos(string p)
        {
            if (p == null) return null;
            string[] a = p.Split(',');
            int x, y;
            if (a.Length != 2 || !int.TryParse(a[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out x) || !int.TryParse(a[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out y)) return null;
            return new[] { x, y };
        }
    }
}
