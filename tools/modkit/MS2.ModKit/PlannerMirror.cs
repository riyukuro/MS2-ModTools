// The mirror between the two per-id profiles (gms-client and server, see Profile.cs). Their Xml.m2d share the
// layout: the server's is the global client's plus some server-only attributes, tables and string folders. So a
// line of a mod for one of them is planned a second time for the other one, as it is (same archive path, entry,
// row and attribute), when the data set has the other one:
//
//   - not when the mod has a line of its own for the same entry there (the mod then says what that side gets);
//   - not for an archive the other side does not ship (Server.m2d has no client copy): a note;
//   - not for an entry or table the other side does not ship: a warning, the change has no place there;
//   - not for an attribute that the other side's shipped table never has while this side's table has it (a
//     server-only attribute): a warning per table and attribute, with the number of rows. Whole entries and new
//     rows are written without such attributes.
//   - a new row whose id, feature and locale the other side ships already (a string row one side lacks): that row
//     gets the new row's attributes instead of a second row (a note).
//
// Which attributes and tables a side has comes from that side's shipped archive: its cached schema (modkit schema
// build) when there is a current one, else a scan of the one table, kept for as long as the data set.
// A mirrored line that fails (its row is not in the other side's file) is a warning, never an error: the mod's own
// line is right for the side it names. Mirrored changes are marked (PlanChange.Mirrored) and are not synced again
// by the field maps: the line they come from is.
using System;
using System.Collections.Generic;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public static partial class Planner
    {
        sealed class MirrorOp
        {
            internal Mod Mod;
            internal ModOp Op;          // the derived op, on the other side's archive
            internal string From;       // the archive the mod names
            internal Profile To;
        }

        sealed class MirrorCtx
        {
            internal string From;
            internal Profile To;
        }

        sealed class Dropped
        {
            internal Mod Mod;
            internal string Archive, Entry, Table, Field, From;
            internal int Count;
        }

        static PlanChange Mark(Run run, PlanChange ch)
        {
            if (run.Mirror != null) { ch.Mirrored = true; ch.MirroredFrom = run.Mirror.From; }
            return ch;
        }

        static string Key(string archive, string entry) { return archive + "\n" + entry.Replace('\\', '/').ToLowerInvariant(); }

        // string/<lang>/<file>.xml: the text files of one language
        static bool IsStringFile(string entry)
        {
            string[] p = entry.Replace('\\', '/').Split('/');
            return p.Length == 3 && string.Equals(p[0], "string", StringComparison.OrdinalIgnoreCase);
        }

        static List<MirrorOp> MirrorOps(Run run, IList<Mod> mods)
        {
            var list = new List<MirrorOp>();
            List<Profile> have = run.Data.Profiles;
            var noted = new Dictionary<string, bool>();
            foreach (Mod m in mods)
            {
                var own = new Dictionary<string, bool>();
                foreach (ModOp op in m.Ops)
                {
                    var en = op as EntryOp; var ed = op as EditOp;
                    if (en != null) own[Key(op.Archive, en.Name)] = true;
                    if (ed != null) own[Key(op.Archive, ed.Entry)] = true;
                }
                foreach (ModOp op in m.Ops)
                {
                    Profile from = Profile.OfArchive(op.Archive);
                    if (from == null) continue;
                    var en = op as EntryOp; var ed = op as EditOp;
                    if (en == null && ed == null) continue;
                    foreach (Profile to in have)
                    {
                        if (!from.Mirrors(to)) continue;
                        string target = to.ArchiveIn(op.Archive);
                        if (own.ContainsKey(Key(target, en != null ? en.Name : ed.Entry))) continue;
                        if (!run.Data.Exists(target))
                        {
                            string nk = m.Id + "\n" + target;
                            if (!noted.ContainsKey(nk))
                            {
                                noted[nk] = true;
                                run.Plan.Msg(run.Plan.Notes, m, op.Archive, null, "is not mirrored to " + to.Name + ": the " + to.Name + " data has no " + DataSet.PathOf(target));
                            }
                            continue;
                        }
                        ModOp d;
                        if (en != null) d = new EntryOp { Archive = target, Line = op.Line, Name = en.Name, Copy = en.Copy, File = en.File, Delete = en.Delete };
                        else
                        {
                            var e2 = new EditOp { Archive = target, Line = op.Line, Entry = ed.Entry };
                            e2.Rows.AddRange(ed.Rows);
                            d = e2;
                        }
                        list.Add(new MirrorOp { Mod = m, Op = d, From = op.Archive, To = to });
                    }
                }
            }
            return list;
        }

        static void RunMirrored(Run run, MirrorOp mo)
        {
            Plan plan = run.Plan;
            M2dArchive arch = run.Open(mo.Mod, mo.Op.Archive);
            if (arch == null) return;
            var en = mo.Op as EntryOp; var ed = mo.Op as EditOp;
            string name = en != null ? en.Name : ed.Entry;
            string prefix = "not mirrored from " + mo.From + ": ";

            // the entry (or for a new entry its table) must be one the other side ships
            EntryState known;
            bool there = arch.Find(name) != null || (run.States.TryGetValue(Key(mo.Op.Archive, name), out known) && known.Base != null);
            if (!there)
            {
                if (en != null && en.Delete) return;
                string table = SchemaSet.TableOf(name);
                if (ed != null)
                {
                    // a string file of a language the other side does not ship: nothing reads it there
                    if (IsStringFile(name))
                        plan.Msg(plan.Notes, mo.Mod, mo.Op.Archive, name, prefix + mo.To.Name + " ships no " + name + " (a language it does not read)");
                    else plan.Msg(plan.Warnings, mo.Mod, mo.Op.Archive, name, prefix + mo.To.Name + " has no such entry, so this change has no place there");
                    return;
                }
                if (!HasTable(run, mo.Op.Archive, table))
                {
                    plan.Msg(plan.Warnings, mo.Mod, mo.Op.Archive, name, prefix + mo.To.Name + " ships no " + table + ", so this entry has no place there");
                    return;
                }
            }

            int errors = plan.Errors.Count, conflicts = plan.Conflicts.Count;
            run.Mirror = new MirrorCtx { From = mo.From, To = mo.To };
            try
            {
                if (ed != null && there) ed = WithoutMissingElements(run, mo.Mod, arch, ed);
                if (ed != null && there) ed = MirrorTalk(run, mo, arch, ed);
                if (en != null) DoEntryOp(run, mo.Mod, en);
                else DoEditOp(run, mo.Mod, ed);
            }
            finally { run.Mirror = null; }
            // a failed mirror is a warning: the line is right for the side the mod names
            for (int i = errors; i < plan.Errors.Count; i++)
            {
                PlanMessage m = plan.Errors[i];
                plan.Warnings.Add(new PlanMessage { ModId = m.ModId, Archive = m.Archive, Entry = m.Entry, Text = prefix + m.Text });
            }
            plan.Errors.RemoveRange(errors, plan.Errors.Count - errors);
            // a conflict that the side the mods name has as well is reported once, there; one that is only on this
            // side (a mirrored line against a line a mod has for this side) stays a conflict
            for (int i = conflicts; i < plan.Conflicts.Count; i++)
            {
                PlanMessage m = plan.Conflicts[i];
                m.Text = "mirrored from " + mo.From + ": " + m.Text;
                bool twin = false;
                for (int k = 0; k < conflicts && !twin; k++)
                {
                    PlanMessage o = plan.Conflicts[k];
                    twin = o.Archive == mo.From && o.ModId == m.ModId && o.WinnerId == m.WinnerId && string.Equals(o.Entry, m.Entry, StringComparison.OrdinalIgnoreCase)
                        && m.Text.EndsWith(": " + o.Text, StringComparison.Ordinal);
                }
                if (!twin) continue;
                plan.Notes.Add(m);
                plan.Conflicts.RemoveAt(i--);
            }
        }

        static string StripPredicates(string at)
        {
            return System.Text.RegularExpressions.Regex.Replace(at, @"\[[^\]]*\]", "").Trim('/');
        }

        // the lines of a mirrored edit whose element the other side's table never has (a server-only element such
        // as a whole <drop>): dropped with the same warning as a server-only attribute, not run into "matches nothing"
        static EditOp WithoutMissingElements(Run run, Mod mod, M2dArchive arch, EditOp ed)
        {
            string table = SchemaSet.TableOf(ed.Entry);
            string root = null;
            M2dEntry me = arch.Find(ed.Entry);
            if (me != null)
            {
                try { XmlDoc d = XmlDoc.Parse(arch.Read(me), ed.Entry); if (d.Root != null) root = d.Root.Name; }
                catch (ModKitException) { }
            }
            if (root == null) return ed;
            TableSchema t = TableIn(run, ed.Archive, table), s = TableIn(run, run.Mirror.From, table);
            if (t == null || s == null) return ed;
            EditOp kept = null;
            for (int i = 0; i < ed.Rows.Count; i++)
            {
                RowOp r = ed.Rows[i];
                RowOp nr = r;
                if (r.Kind == RowOpKind.Change && !string.IsNullOrEmpty(r.At))
                {
                    string rowPath = root + "/" + StripPredicates(r.At);
                    foreach (AttrOp a in r.Attrs)
                    {
                        string path = a.At == null ? rowPath : rowPath + "/" + StripPredicates(a.At);
                        if (t.Element(path) != null || s.Element(path) == null) continue;
                        if (nr == r)
                        {
                            nr = new RowOp { Kind = r.Kind, At = r.At, Copy = r.Copy, Xml = r.Xml, After = r.After, Before = r.Before, In = r.In, Sync = r.Sync, Line = r.Line };
                            foreach (AttrOp x in r.Attrs) nr.Attrs.Add(x);
                        }
                        nr.Attrs.Remove(a);
                        Drop(run, mod, ed.Archive, ed.Entry, table, path.Substring(root.Length + 1) + " @" + a.Name);
                    }
                }
                if (nr != r && kept == null)
                {
                    kept = new EditOp { Archive = ed.Archive, Line = ed.Line, Entry = ed.Entry };
                    for (int j = 0; j < i; j++) kept.Rows.Add(ed.Rows[j]);
                }
                if (kept != null && (nr.Attrs.Count > 0 || nr.Kind != RowOpKind.Change)) kept.Rows.Add(nr);
            }
            return kept ?? ed;
        }

        static void FinishMirror(Run run)
        {
            Plan plan = run.Plan;
            foreach (Dropped d in run.DroppedOrder)
                plan.Msg(plan.Warnings, d.Mod, d.Archive, d.Entry,
                    "not mirrored from " + d.From + ": " + d.Field + " has no place in " + Profile.OfArchive(d.Archive).Name + " (its shipped " + d.Table + " never has it); "
                    + d.Count + " row(s) of the mod set it" + (d.Count > 1 ? ", the first one in this entry" : ""));
        }

        // ---- what the other side ships ----

        static bool HasTable(Run run, string archive, string table)
        {
            string key = "tables\n" + archive;
            object o;
            Dictionary<string, bool> set;
            if (run.Data.Cache.TryGetValue(key, out o)) set = (Dictionary<string, bool>)o;
            else
            {
                set = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (M2dEntry e in run.Data.Open(archive).Entries) set[SchemaSet.TableOf(e.Name)] = true;
                run.Data.Cache[key] = set;
            }
            return set.ContainsKey(table);
        }

        static TableSchema TableIn(Run run, string archive, string table)
        {
            SchemaSet cached = run.Options != null ? run.Options.Schemas : null;
            ArchiveSchema a = cached != null ? cached.Archive(archive) : null;
            if (a != null && !a.Stale) return a.Table(table);
            return SchemaSet.ScanTable(run.Data, archive, table);
        }

        /// <summary>True when the other side's table never has the attribute at that element path while the mod's side has it.</summary>
        static bool NoPlace(Run run, string archive, string table, string path, string attr, out string field)
        {
            int slash = path.IndexOf('/');
            field = (slash < 0 ? path : path.Substring(slash + 1)) + " @" + attr;
            TableSchema t = TableIn(run, archive, table);
            if (t == null) return false;
            ElemSchema e = t.Element(path);
            if (e != null && e.Attr(attr) != null) return false;
            TableSchema s = TableIn(run, run.Mirror.From, table);
            ElemSchema se = s == null ? null : s.Element(path);
            return se != null && se.Attr(attr) != null;
        }

        static void Drop(Run run, Mod mod, string archive, string entry, string table, string field)
        {
            string k = mod.Id + "\n" + archive + "\n" + table + "\n" + field;
            Dropped d;
            if (!run.Dropped.TryGetValue(k, out d))
            {
                d = new Dropped { Mod = mod, Archive = archive, Entry = entry, Table = table, Field = field, From = run.Mirror.From };
                run.Dropped[k] = d;
                run.DroppedOrder.Add(d);
            }
            d.Count++;
        }

        // DoChange under the mirror: false when the attribute is not written on this side
        static bool MirrorKeeps(Run run, Mod mod, EntryState st, XmlElem el, AttrOp a, Claim prev, string row, string what, string value)
        {
            Plan plan = run.Plan;
            if (prev != null && !prev.Mirrored && !prev.Synced && prev.Mod == mod)
            {
                // the mod sets it on this side itself: that line wins over the mirror of its other one (against
                // the line of another mod the priority decides, as for two direct lines)
                if (prev.Value != value)
                    plan.Msg(plan.Warnings, mod, st.Archive, st.Name, "not mirrored from " + run.Mirror.From + ": " + row + " " + what + " stays " + Show(prev.Value)
                        + " as " + Who(prev.Mod, mod) + " sets it here; the mirror would write " + Show(value));
                return false;
            }
            if (a.Kind != AttrOpKind.Set) return true;
            string field, table = SchemaSet.TableOf(st.Name);
            if (NoPlace(run, st.Archive, table, RowPath.NamePath(el, true), a.Name, out field))
            {
                Drop(run, mod, st.Archive, st.Name, table, field);
                return false;
            }
            return true;
        }

        // a whole entry of the mod written to the other side: without the attributes that have no place there
        static byte[] MirrorStrip(Run run, Mod mod, string archive, string entry, byte[] bytes)
        {
            XmlDoc doc;
            try { doc = XmlDoc.Parse(bytes, archive + " " + entry); }
            catch (ModKitException) { return bytes; }
            if (doc.Root == null) return bytes;
            var ed = new XmlEditor(doc);
            bool any = Strip(run, mod, archive, entry, SchemaSet.TableOf(entry), "", doc.Root, ed);
            return any ? ed.ApplyBytes() : bytes;
        }

        // a new row of the mod written to the other side: the same
        static string MirrorStripRow(Run run, Mod mod, EntryState st, string parentPath, XmlDoc fdoc, string frag)
        {
            var ed = new XmlEditor(fdoc);
            bool any = Strip(run, mod, st.Archive, st.Name, SchemaSet.TableOf(st.Name), parentPath, fdoc.Root, ed);
            return any ? ed.Apply() : frag;
        }

        static bool Strip(Run run, Mod mod, string archive, string entry, string table, string parentPath, XmlElem e, XmlEditor ed)
        {
            string path = parentPath.Length == 0 ? e.Name : parentPath + "/" + e.Name;
            bool any = false;
            foreach (XmlAttr a in e.Attrs)
            {
                string field;
                if (!NoPlace(run, archive, table, path, a.Name, out field)) continue;
                ed.RemoveAttr(e, a.Name);
                Drop(run, mod, archive, entry, table, field);
                any = true;
            }
            foreach (XmlElem c in e.Children) any |= Strip(run, mod, archive, entry, table, path, c, ed);
            return any;
        }
    }
}
