// Mods + original data -> Plan: the new bytes of every touched entry, the row-level diff, conflicts, warnings.
// Nothing is written here. Every op is resolved against the ORIGINAL entry (or the entry a mod puts in its place).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public enum ChangeKind { SetAttr, RemoveAttr, SetText, AddRow, DeleteRow, AddEntry, ReplaceEntry, DeleteEntry }

    /// <summary>One line of the dry-run diff.</summary>
    public sealed class PlanChange
    {
        public ChangeKind Kind;
        public string ModId, Archive, Entry;
        public string Row = "";         // selector of the row from the entry's root element
        public string Name;             // attribute name
        public string OldValue, NewValue;
        /// <summary>Written by the two-sided sync (PlannerSync.cs), not by a line of the mod.</summary>
        public bool Synced;

        // for the sync pass
        internal XmlElem Elem, Source;
        internal object State;
        internal RowOp Op;
        internal AttrOp AttrOp;
        internal Mod Mod;

        public override string ToString() { return Text() + (Synced ? "  (synced from the other side)" : ""); }

        string Text()
        {
            string where = Archive + " " + Entry + (Row.Length > 0 ? " " + Row : "");
            switch (Kind)
            {
                case ChangeKind.SetAttr: return where + " @" + Name + ": " + (OldValue == null ? "(none)" : "\"" + OldValue + "\"") + " -> \"" + NewValue + "\"";
                case ChangeKind.RemoveAttr: return where + " @" + Name + ": \"" + OldValue + "\" -> (removed)";
                case ChangeKind.SetText: return where + " text: \"" + OldValue + "\" -> \"" + NewValue + "\"";
                case ChangeKind.AddRow: return where + " + " + OneLine(NewValue);
                case ChangeKind.DeleteRow: return where + " - " + OneLine(OldValue);
                case ChangeKind.AddEntry: return where + " + entry (" + NewValue + ")";
                case ChangeKind.ReplaceEntry: return where + " = entry (" + NewValue + ")";
                default: return where + " - entry";
            }
        }

        static string OneLine(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            bool space = false;
            foreach (char c in s)
            {
                if (c == '\r' || c == '\n' || c == '\t' || c == ' ') { space = sb.Length > 0; continue; }
                if (space) sb.Append(' ');
                space = false;
                sb.Append(c);
                if (sb.Length >= 160) { sb.Append(" ..."); break; }
            }
            return sb.ToString();
        }
    }

    public sealed class PlanMessage
    {
        public string ModId, Archive, Entry, Text;

        public override string ToString()
        {
            var sb = new StringBuilder();
            if (ModId != null) sb.Append('[').Append(ModId).Append("] ");
            if (Archive != null) sb.Append(Archive).Append(' ');
            if (Entry != null) sb.Append(Entry).Append(": ");
            return sb.Append(Text).ToString();
        }
    }

    /// <summary>What a plan does to one archive.</summary>
    public sealed class PlanArchive
    {
        public string Archive;
        public readonly List<KeyValuePair<string, byte[]>> Put = new List<KeyValuePair<string, byte[]>>();
        public readonly List<string> Delete = new List<string>();

        public M2dChanges ToChanges()
        {
            var c = new M2dChanges();
            c.Put.AddRange(Put);
            c.Delete.AddRange(Delete);
            return c;
        }
    }

    public sealed class PlanOptions
    {
        /// <summary>Schemas to check new values against (warnings only). May be null.</summary>
        public SchemaSet Schemas;
        public Action<ProgressInfo> Progress;
        /// <summary>Field maps for the two-sided sync; null = the maps inside the library.</summary>
        public FieldMaps Maps;
        /// <summary>true: nothing is written to the other side; every one-sided change of a mapped field is a warning.</summary>
        public bool NoSync;
    }

    public sealed class Plan
    {
        public readonly List<Mod> Mods = new List<Mod>();
        public readonly List<PlanChange> Changes = new List<PlanChange>();
        public readonly List<PlanMessage> Conflicts = new List<PlanMessage>();
        public readonly List<PlanMessage> Errors = new List<PlanMessage>();
        public readonly List<PlanMessage> Warnings = new List<PlanMessage>();
        public readonly List<PlanMessage> Notes = new List<PlanMessage>();
        /// <summary>Touched archives by name ("client:Xml.m2d"), in the order they were first touched.</summary>
        public readonly List<PlanArchive> Archives = new List<PlanArchive>();

        internal readonly List<NewId> NewIds = new List<NewId>();
        internal readonly List<SchemaProbe> Probes = new List<SchemaProbe>();

        public bool HasErrors { get { return Errors.Count > 0; } }
        public bool HasConflicts { get { return Conflicts.Count > 0; } }

        public PlanArchive Find(string archive)
        {
            foreach (PlanArchive a in Archives) if (a.Archive == archive) return a;
            return null;
        }

        internal void Msg(List<PlanMessage> list, Mod mod, string archive, string entry, string text)
        {
            string id = mod == null ? null : mod.Id;
            foreach (PlanMessage m in list) if (m.ModId == id && m.Archive == archive && m.Entry == entry && m.Text == text) return;
            list.Add(new PlanMessage { ModId = id, Archive = archive, Entry = entry, Text = text });
        }
    }

    /// <summary>A new row or entry that may carry an id of a reserved space.</summary>
    internal sealed class NewId
    {
        internal Mod Mod;
        internal string Archive, Entry;
        internal string NamePath;                       // element names from the root's children; null for a whole entry
        internal Dictionary<string, string> Attrs;
    }

    internal sealed class SchemaProbe
    {
        internal Mod Mod;
        internal string Archive, Entry, ElementPath, Attr, Value;
    }

    public static partial class Planner
    {
        sealed class Claim { internal Mod Mod; internal XmlElem Elem; internal string What, Value; internal bool Synced; }

        sealed class EntryState
        {
            internal string Archive, Name;
            internal bool Existed;
            internal byte[] Original, Base;
            internal Mod BaseMod, DeletedBy;
            internal XmlDoc Doc;
            internal XmlEditor Ed;
            internal bool DocFailed;
            internal readonly Dictionary<string, Claim> Claims = new Dictionary<string, Claim>();
            internal readonly List<KeyValuePair<XmlElem, Mod>> Deleted = new List<KeyValuePair<XmlElem, Mod>>();
            internal readonly Dictionary<string, Mod> AddKeys = new Dictionary<string, Mod>();
        }

        sealed class Run
        {
            internal DataSet Data;
            internal Plan Plan;
            internal readonly Dictionary<string, EntryState> States = new Dictionary<string, EntryState>(StringComparer.Ordinal);
            internal readonly List<EntryState> Order = new List<EntryState>();
            internal readonly Dictionary<string, string> BadArchives = new Dictionary<string, string>();

            internal M2dArchive Open(Mod mod, string archive)
            {
                string err;
                if (BadArchives.TryGetValue(archive, out err)) { Plan.Msg(Plan.Errors, mod, archive, null, err); return null; }
                try { return Data.Open(archive); }
                catch (ModKitException x)
                {
                    BadArchives[archive] = x.Message;
                    Plan.Msg(Plan.Errors, mod, archive, null, x.Message);
                    return null;
                }
            }

            internal EntryState State(M2dArchive arch, string archive, string name)
            {
                M2dEntry e = arch.Find(name);
                string real = e != null ? e.Name : name;
                string key = archive + "\n" + real.ToLowerInvariant();
                EntryState st;
                if (States.TryGetValue(key, out st)) return st;
                st = new EntryState { Archive = archive, Name = real, Existed = e != null };
                if (e != null) st.Original = st.Base = arch.Read(e);
                States[key] = st;
                Order.Add(st);
                return st;
            }
        }

        static string Who(Mod a, Mod b) { return a == b ? "the same mod again" : "mod " + a.Id; }

        public static Plan Build(DataSet data, IList<Mod> mods, PlanOptions options)
        {
            if (options == null) options = new PlanOptions();
            var run = new Run { Data = data, Plan = new Plan() };
            Plan plan = run.Plan;
            var seen = new Dictionary<string, bool>();
            foreach (Mod m in mods)
            {
                if (seen.ContainsKey(m.Id)) throw new ModKitException("two mods have the id " + m.Id);
                seen[m.Id] = true;
                plan.Mods.Add(m);
            }

            int total = 0, done = 0;
            foreach (Mod m in mods) total += m.Ops.Count * 2;

            // whole entries first: an edit may then work on an entry that a mod adds
            foreach (Mod m in mods)
                foreach (ModOp op in m.Ops)
                {
                    ProgressUtil.Report(options.Progress, "plan", m.Id, ++done, total);
                    var en = op as EntryOp;
                    if (en != null) DoEntryOp(run, m, en);
                }
            foreach (Mod m in mods)
                foreach (ModOp op in m.Ops)
                {
                    ProgressUtil.Report(options.Progress, "plan", m.Id, ++done, total);
                    var ed = op as EditOp;
                    if (ed != null) DoEditOp(run, m, ed);
                }
            SyncPass(run, options);

            foreach (EntryState st in run.Order)
            {
                byte[] final = st.Base;
                if (st.DeletedBy == null && st.Ed != null && !st.Ed.IsEmpty)
                {
                    try
                    {
                        final = st.Ed.ApplyBytes();
                        XmlDoc.Parse(final, st.Archive + " " + st.Name + " (after the edits)");
                    }
                    catch (ModKitException x) { plan.Msg(plan.Errors, null, st.Archive, st.Name, x.Message); continue; }
                }
                PlanArchive pa = plan.Find(st.Archive);
                if (st.DeletedBy != null)
                {
                    if (!st.Existed) continue;
                    if (pa == null) plan.Archives.Add(pa = new PlanArchive { Archive = st.Archive });
                    pa.Delete.Add(st.Name);
                }
                else if (final != null && (!st.Existed || !M2dWriter.BytesEqual(final, st.Original)))
                {
                    if (pa == null) plan.Archives.Add(pa = new PlanArchive { Archive = st.Archive });
                    pa.Put.Add(new KeyValuePair<string, byte[]>(st.Name, final));
                }
            }

            // a fixed order of the outputs, whatever the order of the ops
            plan.Archives.Sort(delegate (PlanArchive x, PlanArchive y) { return string.CompareOrdinal(x.Archive, y.Archive); });

            IdSpaces.Check(data, plan, options.Progress);
            if (options.Schemas != null) options.Schemas.Check(plan);
            return plan;
        }

        // ---- whole entries ----

        static void DoEntryOp(Run run, Mod mod, EntryOp op)
        {
            Plan plan = run.Plan;
            M2dArchive arch = run.Open(mod, op.Archive);
            if (arch == null) return;
            EntryState st = run.State(arch, op.Archive, op.Name);
            if (op.Delete)
            {
                if (st.DeletedBy != null) { plan.Msg(plan.Notes, mod, op.Archive, st.Name, "entry is already deleted by " + Who(st.DeletedBy, mod)); return; }
                if (!st.Existed && st.Base == null) { plan.Msg(plan.Errors, mod, op.Archive, op.Name, "cannot delete the entry, the archive has none of that name (mod.xml line " + op.Line + ")"); return; }
                if (st.BaseMod != null) plan.Msg(plan.Conflicts, mod, op.Archive, st.Name, "deletes the entry that " + Who(st.BaseMod, mod) + " " + (st.Existed ? "replaces" : "adds"));
                st.DeletedBy = mod; st.BaseMod = null; st.Base = null;
                plan.Changes.Add(new PlanChange { Kind = ChangeKind.DeleteEntry, ModId = mod.Id, Archive = op.Archive, Entry = st.Name });
                return;
            }

            byte[] bytes; string from;
            if (op.File != null)
            {
                if (mod.Dir == null) { plan.Msg(plan.Errors, mod, op.Archive, op.Name, "file=\"" + op.File + "\": the mod has no folder"); return; }
                string full = Path.GetFullPath(Path.Combine(mod.Dir, op.File.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
                string dir = Path.GetFullPath(mod.Dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(dir, StringComparison.Ordinal)) { plan.Msg(plan.Errors, mod, op.Archive, op.Name, "file=\"" + op.File + "\" is outside of the mod folder"); return; }
                if (!File.Exists(full)) { plan.Msg(plan.Errors, mod, op.Archive, op.Name, "file=\"" + op.File + "\": no such file in the mod folder (mod.xml line " + op.Line + ")"); return; }
                bytes = File.ReadAllBytes(full);
                from = "file " + op.File;
            }
            else
            {
                M2dEntry src = arch.Find(op.Copy);
                if (src == null) { plan.Msg(plan.Errors, mod, op.Archive, op.Name, "copy=\"" + op.Copy + "\": the archive has no such entry (mod.xml line " + op.Line + ")"); return; }
                bytes = arch.Read(src);
                from = "copy of " + src.Name;
            }
            if (st.DeletedBy != null)
                plan.Msg(plan.Conflicts, mod, op.Archive, st.Name, "puts an entry where " + Who(st.DeletedBy, mod) + " deletes one");
            else if (st.BaseMod != null)
            {
                if (M2dWriter.BytesEqual(bytes, st.Base)) { plan.Msg(plan.Notes, mod, op.Archive, st.Name, "same entry content as " + Who(st.BaseMod, mod)); return; }
                plan.Msg(plan.Conflicts, mod, op.Archive, st.Name, (st.Existed ? "replaces" : "adds") + " the entry that " + Who(st.BaseMod, mod) + " also " + (st.Existed ? "replaces" : "adds"));
            }
            st.DeletedBy = null; st.Base = bytes; st.BaseMod = mod;
            plan.Changes.Add(new PlanChange
            {
                Kind = st.Existed ? ChangeKind.ReplaceEntry : ChangeKind.AddEntry, ModId = mod.Id, Archive = op.Archive, Entry = st.Name,
                NewValue = from + ", " + bytes.Length + " bytes", OldValue = st.Existed ? st.Original.Length + " bytes" : null,
            });
            if (!st.Existed) plan.NewIds.Add(new NewId { Mod = mod, Archive = op.Archive, Entry = st.Name });
        }

        // ---- rows ----

        static void DoEditOp(Run run, Mod mod, EditOp op)
        {
            Plan plan = run.Plan;
            M2dArchive arch = run.Open(mod, op.Archive);
            if (arch == null) return;
            EntryState st = run.State(arch, op.Archive, op.Entry);
            if (st.DeletedBy != null)
            {
                plan.Msg(st.DeletedBy == mod ? plan.Errors : plan.Conflicts, mod, op.Archive, st.Name, "edits an entry that " + Who(st.DeletedBy, mod) + " deletes");
                return;
            }
            if (st.Base == null)
            {
                plan.Msg(plan.Errors, mod, op.Archive, op.Entry, "the archive has no such entry (mod.xml line " + op.Line + ")" + Suggest(arch, op.Entry));
                return;
            }
            if (st.BaseMod != null && st.BaseMod != mod)
                plan.Msg(plan.Conflicts, mod, op.Archive, st.Name, "edits an entry that mod " + st.BaseMod.Id + " " + (st.Existed ? "replaces" : "adds"));
            if (!EnsureDoc(plan, mod, st)) return;
            foreach (RowOp r in op.Rows)
            {
                try
                {
                    if (r.Kind == RowOpKind.Change) DoChange(plan, mod, st, r);
                    else if (r.Kind == RowOpKind.Delete) DoDelete(plan, mod, st, r);
                    else DoAdd(plan, mod, st, r);
                }
                catch (ModKitException x)
                {
                    string msg = x.Message;
                    string label = st.Doc.Label + ": ";
                    if (msg.StartsWith(label, StringComparison.Ordinal)) msg = msg.Substring(label.Length);
                    plan.Msg(plan.Errors, mod, op.Archive, st.Name, msg + " (mod.xml line " + r.Line + ")");
                }
            }
        }

        static bool EnsureDoc(Plan plan, Mod mod, EntryState st)
        {
            if (st.DocFailed) return false;
            if (st.Doc == null)
            {
                try { st.Doc = XmlDoc.Parse(st.Base, st.Archive + " " + st.Name); }
                catch (ModKitException x) { st.DocFailed = true; plan.Msg(plan.Errors, mod, st.Archive, st.Name, "cannot be edited as XML: " + x.Message); return false; }
                if (st.Doc.EncodingFallback)
                    plan.Msg(plan.Notes, mod, st.Archive, st.Name, "the text is not valid in its declared encoding; edited as " + st.Doc.EncodingName);
                st.Ed = new XmlEditor(st.Doc);
            }
            return true;
        }

        static string Suggest(M2dArchive arch, string name)
        {
            string file = name.Substring(name.LastIndexOf('/') + 1);
            var hits = new List<string>();
            foreach (M2dEntry e in arch.Entries)
                if (e.Name.EndsWith("/" + file, StringComparison.OrdinalIgnoreCase) || string.Equals(e.Name, file, StringComparison.OrdinalIgnoreCase))
                { hits.Add(e.Name); if (hits.Count == 3) break; }
            return hits.Count == 0 ? "" : "; did you mean " + string.Join(", ", hits.ToArray()) + "?";
        }

        static Mod DeletedBy(EntryState st, XmlElem e)
        {
            foreach (KeyValuePair<XmlElem, Mod> d in st.Deleted)
                for (XmlElem c = e; c != null; c = c.Parent)
                    if (c == d.Key) return d.Value;
            return null;
        }

        static XmlElem Sub(XmlElem from, string selector)
        {
            return string.IsNullOrEmpty(selector) ? from : RowPath.Parse(selector).Resolve(from);
        }

        static void DoChange(Plan plan, Mod mod, EntryState st, RowOp r)
        {
            XmlElem target = Sub(st.Doc.Root, r.At);
            foreach (AttrOp a in r.Attrs)
            {
                XmlElem el = Sub(target, a.At);
                string row = RowPath.Canonical(el);
                Mod del = DeletedBy(st, el);
                if (del != null)
                {
                    plan.Msg(plan.Conflicts, mod, st.Archive, st.Name, "changes the row " + row + " that " + Who(del, mod) + " deletes");
                    continue;
                }
                string what = a.Kind == AttrOpKind.Text ? "text" : "@" + a.Name;
                string value = a.Kind == AttrOpKind.Unset ? null : a.Value;
                string old = a.Kind == AttrOpKind.Text ? el.InnerText : el.Attr(a.Name);
                if (a.Kind == AttrOpKind.Text && old == null)
                    throw new ModKitException("<" + el.Name + "> at " + row + " has child elements, its text cannot be set");
                string key = el.Start + "\n" + what;
                Claim prev;
                if (st.Claims.TryGetValue(key, out prev))
                {
                    if (prev.Value == value) { plan.Msg(plan.Notes, mod, st.Archive, st.Name, row + " " + what + ": same value as " + Who(prev.Mod, mod)); continue; }
                    plan.Msg(plan.Conflicts, mod, st.Archive, st.Name, row + " " + what + ": " + Show(value) + " here, " + Show(prev.Value) + " in " + Who(prev.Mod, mod));
                    RemoveChange(plan, st, row, a, prev);
                }
                else if (old == value || (a.Kind == AttrOpKind.Unset && old == null))
                {
                    plan.Msg(plan.Notes, mod, st.Archive, st.Name, row + " " + what + (old == null ? " is not there, nothing to remove" : " already is \"" + old + "\""));
                    st.Claims[key] = new Claim { Mod = mod, Elem = el, What = what, Value = value };
                    continue;
                }
                st.Claims[key] = new Claim { Mod = mod, Elem = el, What = what, Value = value };
                var ch = new PlanChange { ModId = mod.Id, Archive = st.Archive, Entry = st.Name, Row = row, Name = a.Name, OldValue = old, NewValue = value, Elem = el, State = st, Op = r, AttrOp = a, Mod = mod };
                if (a.Kind == AttrOpKind.Set) { st.Ed.SetAttr(el, a.Name, a.Value); ch.Kind = ChangeKind.SetAttr; }
                else if (a.Kind == AttrOpKind.Unset) { st.Ed.RemoveAttr(el, a.Name); ch.Kind = ChangeKind.RemoveAttr; }
                else { st.Ed.SetText(el, a.Value); ch.Kind = ChangeKind.SetText; }
                plan.Changes.Add(ch);
                if (a.Kind == AttrOpKind.Set)
                    plan.Probes.Add(new SchemaProbe { Mod = mod, Archive = st.Archive, Entry = st.Name, ElementPath = RowPath.NamePath(el, true), Attr = a.Name, Value = a.Value });
            }
        }

        static string Show(string v) { return v == null ? "(removed)" : "\"" + v + "\""; }

        // the later op wins in the text; its diff line replaces the earlier one
        static void RemoveChange(Plan plan, EntryState st, string row, AttrOp a, Claim prev)
        {
            for (int i = plan.Changes.Count - 1; i >= 0; i--)
            {
                PlanChange c = plan.Changes[i];
                if (c.Archive != st.Archive || c.Entry != st.Name || c.Row != row || c.ModId != prev.Mod.Id) continue;
                bool text = a.Kind == AttrOpKind.Text;
                if (text ? c.Kind == ChangeKind.SetText : (c.Kind == ChangeKind.SetAttr || c.Kind == ChangeKind.RemoveAttr) && c.Name == a.Name)
                { plan.Changes.RemoveAt(i); return; }
            }
        }

        static void DoDelete(Plan plan, Mod mod, EntryState st, RowOp r)
        {
            XmlElem target = Sub(st.Doc.Root, r.At);
            if (target == st.Doc.Root) throw new ModKitException("the root element cannot be deleted; delete the entry instead");
            string row = RowPath.Canonical(target);
            Mod del = DeletedBy(st, target);
            if (del != null) { plan.Msg(plan.Notes, mod, st.Archive, st.Name, "row " + row + " is already deleted by " + Who(del, mod)); return; }
            var drop = new List<string>();
            foreach (KeyValuePair<string, Claim> c in st.Claims)
            {
                bool inside = false;
                for (XmlElem e = c.Value.Elem; e != null; e = e.Parent) if (e == target) { inside = true; break; }
                if (!inside) continue;
                drop.Add(c.Key);
                plan.Msg(plan.Conflicts, mod, st.Archive, st.Name, "deletes the row " + row + " that " + Who(c.Value.Mod, mod) + " changes");
            }
            foreach (string k in drop) st.Claims.Remove(k);
            for (int i = st.Deleted.Count - 1; i >= 0; i--)
                for (XmlElem e = st.Deleted[i].Key; e != null; e = e.Parent)
                    if (e == target) { st.Deleted.RemoveAt(i); break; }
            st.Ed.DropEditsWithin(target);
            st.Ed.Delete(target);
            st.Deleted.Add(new KeyValuePair<XmlElem, Mod>(target, mod));
            plan.Changes.Add(new PlanChange { Kind = ChangeKind.DeleteRow, ModId = mod.Id, Archive = st.Archive, Entry = st.Name, Row = row, OldValue = target.OuterText });
        }

        static string RowKey(XmlElem e)
        {
            var sb = new StringBuilder(e.Name);
            if (e.Attr("id") != null)
            {
                foreach (string n in new[] { "id", "feature", "locale" }) sb.Append('\n').Append(e.Attr(n) ?? "");
                return sb.ToString();
            }
            foreach (XmlAttr a in e.Attrs) sb.Append('\n').Append(a.Name).Append('=').Append(a.Value);
            return sb.ToString();
        }

        static void DoAdd(Plan plan, Mod mod, EntryState st, RowOp r)
        {
            XmlDoc doc = st.Doc;
            string frag, fromIndent;
            XmlElem src = null;
            if (r.Copy != null)
            {
                src = Sub(doc.Root, r.Copy);
                if (src == doc.Root) throw new ModKitException("copy=\"\" would copy the root element");
                frag = src.OuterText;
                fromIndent = doc.IndentOf(src.Start);
            }
            else
            {
                frag = r.Xml.Trim();
                fromIndent = XmlEditor.CommonIndent(frag);
            }
            XmlDoc fdoc = XmlDoc.ParseFragment(frag, "the new row");
            if (fdoc.Top.Count != 1) throw new ModKitException("<add> takes one element, the text holds " + fdoc.Top.Count);
            if (r.Attrs.Count > 0)
            {
                var fed = new XmlEditor(fdoc);
                foreach (AttrOp a in r.Attrs)
                {
                    XmlElem el = Sub(fdoc.Root, a.At);
                    if (a.Kind == AttrOpKind.Set) fed.SetAttr(el, a.Name, a.Value);
                    else if (a.Kind == AttrOpKind.Unset) fed.RemoveAttr(el, a.Name);
                    else fed.SetText(el, a.Value);
                }
                frag = fed.Apply();
                fdoc = XmlDoc.ParseFragment(frag, "the new row");
            }
            XmlElem made = fdoc.Root;

            XmlElem anchor = null, parent;
            bool before = false;
            if (r.After != null) { anchor = Sub(doc.Root, r.After); parent = anchor.Parent; }
            else if (r.Before != null) { anchor = Sub(doc.Root, r.Before); parent = anchor.Parent; before = true; }
            else
            {
                parent = r.In != null ? Sub(doc.Root, r.In) : src != null ? src.Parent : doc.Root;
                for (int i = parent.Children.Count - 1; i >= 0 && anchor == null; i--)
                    if (parent.Children[i].Name == made.Name) anchor = parent.Children[i];
            }
            if (parent == null) throw new ModKitException("a row cannot be added beside the root element");
            string where = RowPath.Canonical(parent);
            string row = (where.Length > 0 ? where + "/" : "") + made.Name + (made.Attr("id") != null ? "[id=" + RowPath.Quote(made.Attr("id")) + "]" : "");
            Mod del = DeletedBy(st, parent);
            if (del != null)
            {
                plan.Msg(plan.Conflicts, mod, st.Archive, st.Name, "adds " + row + " inside a row that " + Who(del, mod) + " deletes");
                return;
            }

            string key = parent.Start + "\n" + RowKey(made);
            Mod other;
            if (st.AddKeys.TryGetValue(key, out other))
                plan.Msg(plan.Conflicts, mod, st.Archive, st.Name, "adds the row " + row + " that " + Who(other, mod) + " also adds");
            else
            {
                string mk = RowKey(made);
                foreach (XmlElem c in parent.Children)
                    if (c.Name == made.Name && RowKey(c) == mk && DeletedBy(st, c) == null)
                    {
                        plan.Msg(plan.Warnings, mod, st.Archive, st.Name, "adds " + row + ", and a row with the same " + (made.Attr("id") != null ? "id, feature and locale" : "attributes") + " is already there (line " + c.Line + ")");
                        break;
                    }
            }
            st.AddKeys[key] = mod;

            if (anchor == null) st.Ed.Append(parent, frag, fromIndent);
            else if (before) st.Ed.InsertBefore(anchor, frag, fromIndent);
            else st.Ed.InsertAfter(anchor, frag, fromIndent);

            plan.Changes.Add(new PlanChange { Kind = ChangeKind.AddRow, ModId = mod.Id, Archive = st.Archive, Entry = st.Name, Row = row, NewValue = frag, Elem = made, Source = src, State = st, Op = r, Mod = mod });
            var attrs = new Dictionary<string, string>();
            foreach (XmlAttr a in made.Attrs) attrs[a.Name] = a.Value;
            string names = RowPath.NamePath(parent, false);
            plan.NewIds.Add(new NewId { Mod = mod, Archive = st.Archive, Entry = st.Name, NamePath = (names.Length > 0 ? names + "/" : "") + made.Name, Attrs = attrs });
            AddProbes(plan, mod, st, RowPath.NamePath(parent, true), made);
        }

        static void AddProbes(Plan plan, Mod mod, EntryState st, string parentPath, XmlElem e)
        {
            string path = parentPath + "/" + e.Name;
            if (e.Attrs.Count == 0) plan.Probes.Add(new SchemaProbe { Mod = mod, Archive = st.Archive, Entry = st.Name, ElementPath = path });
            foreach (XmlAttr a in e.Attrs)
                plan.Probes.Add(new SchemaProbe { Mod = mod, Archive = st.Archive, Entry = st.Name, ElementPath = path, Attr = a.Name, Value = a.Value });
            foreach (XmlElem c in e.Children) AddProbes(plan, mod, st, path, c);
        }
    }
}
