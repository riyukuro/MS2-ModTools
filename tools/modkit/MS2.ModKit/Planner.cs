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
        /// <summary>Written by the mirror between the per-id profiles (PlannerMirror.cs): the same line of the mod on
        /// another target; MirroredFrom is the archive the mod names ("server:Xml.m2d").</summary>
        public bool Mirrored;
        public string MirroredFrom;

        // for the sync pass
        internal XmlElem Elem, Source;
        internal object State;
        internal RowOp Op;
        internal AttrOp AttrOp;
        internal Mod Mod;
        /// <summary>A skill op change: planned on each side by itself, never synced.</summary>
        internal bool OwnSides;

        public override string ToString() { return Text() + (Synced ? "  (synced from the other side)" : Mirrored ? "  (mirrored from " + MirroredFrom + ")" : ""); }

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
        /// <summary>Conflicts only: the id of the mod whose change is in the plan; null when the conflict has no winner.</summary>
        public string WinnerId;
        /// <summary>The mod whose part of the conflict is left out (null where there is no winner).</summary>
        public string LoserId;
        /// <summary>The line of the losing mod that the conflict is about, where the message is that mod's own.</summary>
        internal object Ask;
        /// <summary>The loser's whole version of the entry (its file, or its delete) is left out.</summary>
        internal bool Entire;

        internal const string WinsTail = " wins (it has the higher priority)";

        /// <summary>
        /// A conflict as one line of a report, the outcome first so that it shows in a narrow box:
        /// "conflict, b wins: [a] archive entry: what" or "conflict, no winner: ...".
        /// </summary>
        public string ConflictLine()
        {
            if (WinnerId == null) return ConflictTag + this;
            string all = ToString(), tail = "; mod " + WinnerId + WinsTail;
            return ConflictTag + (all.EndsWith(tail, StringComparison.Ordinal) ? all.Substring(0, all.Length - tail.Length) : all);
        }

        /// <summary>"conflict, b wins: " or "conflict, no winner: "</summary>
        public string ConflictTag { get { return WinnerId == null ? "conflict, no winner: " : "conflict, " + WinnerId + " wins: "; } }

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
        /// <summary>The plan of one mod alone, made inside a plan of several mods.</summary>
        internal bool Alone;
        /// <summary>What the plan of several mods has read of the shipped data, for the plans of one mod alone.</summary>
        internal ShippedCache Shipped;
    }

    /// <summary>Shipped entries as read and parsed once. A parsed document is not changed by a plan (edits are kept beside it).</summary>
    internal sealed class ShippedCache
    {
        internal readonly Dictionary<string, byte[]> Bytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        internal readonly Dictionary<byte[], XmlDoc> Docs = new Dictionary<byte[], XmlDoc>();
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
        /// <summary>The conflicts that no priority decides (two mods claim the same ids, a mod contradicts itself):
        /// nothing is written while there is one, whatever the options say.</summary>
        public int Undecided { get { int n = 0; foreach (PlanMessage m in Conflicts) if (m.WinnerId == null) n++; return n; } }
        public const string UndecidedReason = " conflict(s) that no priority decides";

        public PlanArchive Find(string archive)
        {
            foreach (PlanArchive a in Archives) if (a.Archive == archive) return a;
            return null;
        }

        /// <summary>Adds a note that belongs to no mod (a front end says what would follow the apply).</summary>
        public void Note(string text) { Msg(Notes, null, null, null, text); }

        internal void Msg(List<PlanMessage> list, Mod mod, string archive, string entry, string text)
        {
            string id = mod == null ? null : mod.Id;
            foreach (PlanMessage m in list) if (m.ModId == id && m.Archive == archive && m.Entry == entry && m.Text == text) return;
            list.Add(new PlanMessage { ModId = id, Archive = archive, Entry = entry, Text = text });
        }

        /// <summary>A conflict about whole entries: what the loser has for the entry is left out as a whole.</summary>
        internal void EntryConflict(Mod mod, string archive, string entry, string text, Mod winner, Mod other)
        {
            int n = Conflicts.Count;
            Conflict(mod, archive, entry, text, winner, other);
            if (Conflicts.Count > n) Conflicts[n].Entire = true;
        }

        /// <summary>A conflict between two mods; the text says which one is in the plan (the one with the higher priority).</summary>
        /// <param name="other">the other mod of the pair; needed where <paramref name="mod"/> may be the winner</param>
        /// <param name="why">the reason where it is not the priority: " wins (...)"</param>
        internal void Conflict(Mod mod, string archive, string entry, string text, Mod winner, Mod other = null, string why = null)
        {
            // a shop or skill line that meets another mod, as the winner too: the edit lines of every mod are planned
            // before these lines, so a lower mod's line may stand between two lines of this mod
            if (Lines == mod) UnderLines[mod] = true;
            // either mod of the pair may have a second line in this entry that now never meets its first one
            if (archive != null && entry != null)
            {
                IsUnder(mod, archive, entry);
                IsUnder(winner, archive, entry);
                if (other != null) IsUnder(other, archive, entry);
            }
            int n = Conflicts.Count;
            Msg(Conflicts, mod, archive, entry, text + "; mod " + winner.Id + (why ?? PlanMessage.WinsTail));
            if (Conflicts.Count == n) return;
            Conflicts[n].WinnerId = winner.Id;
            Mod loser = winner != mod ? mod : other;
            if (loser != null && loser != winner) Conflicts[n].LoserId = loser.Id;
            if (loser == mod) Conflicts[n].Ask = Ask;
        }

        /// <summary>
        /// Per mod, the entries where a line of it meets a line of another mod (a conflict with it, lost or won, a
        /// delete or a file of a higher mod): two lines of the mod may never meet each other in this plan.
        /// </summary>
        internal readonly Dictionary<Mod, Dictionary<string, bool>> Under = new Dictionary<Mod, Dictionary<string, bool>>();

        /// <summary>The shipped entries this plan has read (shared with the plans of one mod alone made for it).</summary>
        internal ShippedCache Shipped = new ShippedCache();


        internal static string UnderKey(string archive, string entry) { return archive + "\n" + entry.Replace('\\', '/').ToLowerInvariant(); }

        /// <summary>The mod whose shop, drop box, skill or job skill lines are planned now; null outside of them.</summary>
        internal Mod Lines;

        /// <summary>
        /// The mods with a shop, drop box, skill or job skill line that meets a line of another mod: a conflict
        /// with it (lost or won), a row or an entry that the other mod has deleted. Such lines name no entry of
        /// their own, so they are kept per mod.
        /// </summary>
        internal readonly Dictionary<Mod, bool> UnderLines = new Dictionary<Mod, bool>();

        internal void IsUnder(Mod mod, string archive, string entry)
        {
            Dictionary<string, bool> d;
            if (!Under.TryGetValue(mod, out d)) Under[mod] = d = new Dictionary<string, bool>();
            d[UnderKey(archive, entry)] = true;
        }

        /// <summary>The row line (or the one set of a change) that is planned now; null outside of row lines.</summary>
        internal object Ask;

        /// <summary>
        /// What the priority costs each mod: one line per pair of mods, "c wins 2 parts of b" (two things mod b asks
        /// for are left out, mod c has them), the losing mods from the highest down. The winner's name comes first,
        /// so a narrow box shows it. One line of a mod that loses to several mods is one part, of the highest of them.
        /// </summary>
        public List<string> LeftOut()
        {
            var lines = new List<string>();
            for (int i = Mods.Count - 1; i >= 0; i--)
            {
                var to = new List<string>(); var count = new List<int>();
                var asks = new List<object>(); var best = new List<int>();
                foreach (PlanMessage m in Conflicts)
                {
                    if (m.LoserId != Mods[i].Id || m.WinnerId == null) continue;
                    int w = Mods.FindIndex(x => x.Id == m.WinnerId);
                    // a whole entry that lost to a mod whose own version of the entry lost in turn: the part is
                    // of the mod whose version is written
                    if (m.Entire)
                        for (bool more = true; more; )
                        {
                            more = false;
                            foreach (PlanMessage c in Conflicts)
                            {
                                if (!c.Entire || c.Archive != m.Archive || c.Entry != m.Entry || c.LoserId != Mods[w].Id || c.WinnerId == null) continue;
                                int up = Mods.FindIndex(x => x.Id == c.WinnerId);
                                if (up > w) { w = up; more = true; }
                            }
                        }
                    int at = m.Ask == null ? -1 : asks.IndexOf(m.Ask);
                    if (at < 0) { asks.Add(m.Ask ?? m); best.Add(w); } else if (w > best[at]) best[at] = w;
                }
                foreach (int w in best)
                {
                    string id = w < 0 ? null : Mods[w].Id;
                    if (id == null) continue;
                    int k = to.IndexOf(id);
                    if (k < 0) { to.Add(id); count.Add(1); } else count[k]++;
                }
                for (int k = 0; k < to.Count; k++) lines.Add(to[k] + " wins " + count[k] + (count[k] == 1 ? " part of " : " parts of ") + Mods[i].Id);
            }
            return lines;
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
        sealed class Claim { internal Mod Mod; internal XmlElem Elem; internal string What, Value; internal bool Synced, Mirrored; }

        sealed class AddClaim { internal Mod Mod; internal object Edit; internal PlanChange Change; internal NewId NewId; internal XmlElem Parent; internal string Key, Copy; }

        sealed class EntryState
        {
            internal string Archive, Name;
            internal bool Existed;
            internal byte[] Original, Base;
            internal Mod BaseMod, DeletedBy;
            internal XmlDoc Doc;
            // Final as parsed, for an entry with edits (the check that it still parses); kept for the checks after it
            internal XmlDoc FinalDoc;
            internal XmlEditor Ed;
            internal bool DocFailed;
            /// <summary>A trigger script: the mods whose entry or edit ops touch it (its final bytes are linted).</summary>
            internal List<Mod> LintBy;
            /// <summary>The bytes after the edits (set when the plan is built; null = Base).</summary>
            internal byte[] Final;
            internal readonly Dictionary<string, Claim> Claims = new Dictionary<string, Claim>();
            internal readonly List<KeyValuePair<XmlElem, Mod>> Deleted = new List<KeyValuePair<XmlElem, Mod>>();
            internal readonly Dictionary<string, AddClaim> AddKeys = new Dictionary<string, AddClaim>();
            // the keys of the children of a row of Doc, made at the first row a mod adds there (SameKey)
            internal readonly Dictionary<XmlElem, Dictionary<string, List<XmlElem>>> RowKeys = new Dictionary<XmlElem, Dictionary<string, List<XmlElem>>>();
            /// <summary>Every row a mod adds, for a delete of the row it is added in.</summary>
            internal readonly List<AddClaim> Adds = new List<AddClaim>();
            /// <summary>Mods whose whole file for this entry is left out, with the mod that has the entry instead.</summary>
            internal readonly Dictionary<Mod, Mod> FileLeftOut = new Dictionary<Mod, Mod>();
            /// <summary>Mods with a delete of this entry that is not the delete of the plan: a higher mod has left
            /// it out, or another mod had deleted the entry before. What such a mod does to the entry besides is
            /// as wrong as it is for the mod alone.</summary>
            internal readonly List<Mod> DeleteLeftOut = new List<Mod>();
            /// <summary>Every value a mod asks for, also where a higher mod has the value: a mod that asks for two
            /// values in one place contradicts itself whoever wins.</summary>
            internal readonly List<Asked> Asked = new List<Asked>();
            /// <summary>The same by place (element and attribute), so a value is checked without a walk of them all.</summary>
            internal readonly Dictionary<string, List<Asked>> AskedAt = new Dictionary<string, List<Asked>>(StringComparer.Ordinal);
            /// <summary>The row deletes that a higher mod's change or new row kept from happening.</summary>
            internal readonly List<KeyValuePair<XmlElem, Mod>> KeptDeletes = new List<KeyValuePair<XmlElem, Mod>>();
        }

        sealed class Asked { internal Mod Mod; internal XmlElem Elem; internal string Key, Value; }

        static bool Within(XmlElem e, XmlElem row)
        {
            for (XmlElem c = e; c != null; c = c.Parent) if (c == row) return true;
            return false;
        }

        // the mod's own delete of a row around e, which a higher mod kept from happening
        static bool OwnKeptDelete(Run run, EntryState st, Mod mod, XmlElem e)
        {
            if (run.Mirror != null) return false;
            foreach (KeyValuePair<XmlElem, Mod> d in st.KeptDeletes) if (d.Value == mod && Within(e, d.Key)) return true;
            return false;
        }

        sealed class Run
        {
            internal DataSet Data;
            internal Plan Plan;
            internal readonly Dictionary<string, EntryState> States = new Dictionary<string, EntryState>(StringComparer.Ordinal);
            internal readonly List<EntryState> Order = new List<EntryState>();
            internal readonly Dictionary<string, string> BadArchives = new Dictionary<string, string>();
            internal PlanOptions Options;
            /// <summary>Place of a mod in the list the plan was built from; a mod further back has the higher priority.</summary>
            internal readonly Dictionary<Mod, int> Ranks = new Dictionary<Mod, int>();
            /// <summary>Does a win over b where both set the same thing?</summary>
            internal bool Outranks(Mod a, Mod b) { int x, y; Ranks.TryGetValue(a, out x); Ranks.TryGetValue(b, out y); return x > y; }
            /// <summary>Set while a mirrored op is planned (PlannerMirror.cs).</summary>
            internal MirrorCtx Mirror;
            internal readonly Dictionary<string, Dropped> Dropped = new Dictionary<string, Dropped>(StringComparer.Ordinal);
            internal readonly List<Dropped> DroppedOrder = new List<Dropped>();
            /// <summary>The talk text ids a mirror target has rows for (PlannerTalk.cs), by archive.</summary>
            internal readonly Dictionary<string, HashSet<long>> MirrorTalk = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
            /// <summary>Shop and drop box state (PlannerGroups.cs).</summary>
            internal readonly GroupRun Groups = new GroupRun();

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
                if (States.TryGetValue(key, out st))
                {
                    // a shop or skill line that finds its entry deleted by another mod is skipped there
                    if (Plan.Lines != null && st.DeletedBy != null && st.DeletedBy != Plan.Lines) Plan.UnderLines[Plan.Lines] = true;
                    return st;
                }
                st = new EntryState { Archive = archive, Name = real, Existed = e != null };
                if (e != null)
                {
                    byte[] bytes;
                    if (!Plan.Shipped.Bytes.TryGetValue(key, out bytes)) Plan.Shipped.Bytes[key] = bytes = arch.Read(e);
                    st.Original = st.Base = bytes;
                }
                States[key] = st;
                Order.Add(st);
                return st;
            }
        }

        static string Who(Mod a, Mod b) { return a == b ? "the same mod again" : "mod " + a.Id; }

        public static Plan Build(DataSet data, IList<Mod> mods, PlanOptions options)
        {
            if (options == null) options = new PlanOptions();
            var run = new Run { Data = data, Plan = new Plan(), Options = options };
            Plan plan = run.Plan;
            if (options.Shipped != null) plan.Shipped = options.Shipped;
            var seen = new Dictionary<string, bool>();
            foreach (Mod m in mods)
            {
                if (seen.ContainsKey(m.Id)) throw new ModKitException("two mods have the id " + m.Id);
                seen[m.Id] = true;
                run.Ranks[m] = plan.Mods.Count;
                plan.Mods.Add(m);
            }

            List<MirrorOp> mirrored = MirrorOps(run, mods);
            int total = mirrored.Count, done = 0;
            foreach (Mod m in mods) total += m.Ops.Count * 2 + m.Groups.Count + m.Skills.Count + m.JobSkills.Count;

            // whole entries first: an edit may then work on an entry that a mod adds
            foreach (Mod m in mods)
                foreach (ModOp op in m.Ops)
                {
                    ProgressUtil.Report(options.Progress, "plan", m.Id, ++done, total);
                    var en = op as EntryOp;
                    if (en != null && Targeted(run, m, en)) DoEntryOp(run, m, en);
                }
            foreach (MirrorOp mo in mirrored)
                if (mo.Op is EntryOp) { ProgressUtil.Report(options.Progress, "plan", mo.Mod.Id, ++done, total); RunMirrored(run, mo); }
            // from the highest priority down: what a mod has planned is never undone by a later one, so the result
            // for a mod does not depend on a third mod that loses
            for (int i = mods.Count - 1; i >= 0; i--)
                foreach (ModOp op in mods[i].Ops)
                {
                    ProgressUtil.Report(options.Progress, "plan", mods[i].Id, ++done, total);
                    var ed = op as EditOp;
                    if (ed != null && Targeted(run, mods[i], ed)) DoEditOp(run, mods[i], ed);
                }
            // also from the highest priority down: where two mods make the same thing, the first one keeps it
            for (int i = mods.Count - 1; i >= 0; i--)
            {
                Mod m = mods[i];
                foreach (GroupOp g in m.Groups)
                {
                    ProgressUtil.Report(options.Progress, "plan", m.Id, ++done, total);
                    plan.Lines = m;
                    try { DoGroup(run, m, g); }
                    finally { plan.Lines = null; }
                }
            }
            // and the skill lines, so that three mods on one skill follow the priority as the edit lines do
            for (int i = mods.Count - 1; i >= 0; i--)
            {
                Mod m = mods[i];
                if (m.Skills.Count + m.JobSkills.Count > 0)
                {
                    ProgressUtil.Report(options.Progress, "plan", m.Id, done += m.Skills.Count + m.JobSkills.Count, total);
                    plan.Lines = m;
                    try { DoSkills(run, m); }
                    finally { plan.Lines = null; }
                }
            }
            // the mirrored lines in the same order as the direct ones, so a conflict reads the same on both sides
            for (int i = mods.Count - 1; i >= 0; i--)
                foreach (MirrorOp mo in mirrored)
                    if (mo.Mod == mods[i] && mo.Op is EditOp) { ProgressUtil.Report(options.Progress, "plan", mo.Mod.Id, ++done, total); RunMirrored(run, mo); }
            FinishMirror(run);
            SyncPass(run, options);
            DoMaps(run, mods);
            // the plan of one mod alone is read for its conflicts only: nothing of it is written
            if (options.Alone) return plan;

            foreach (EntryState st in run.Order)
            {
                byte[] final = st.Base;
                if (st.DeletedBy == null && st.Ed != null && !st.Ed.IsEmpty)
                {
                    try
                    {
                        final = st.Ed.ApplyBytes();
                        st.FinalDoc = XmlDoc.Parse(final, st.Archive + " " + st.Name + " (after the edits)");
                    }
                    catch (ModKitException x) { plan.Msg(plan.Errors, null, st.Archive, st.Name, x.Message); continue; }
                }
                st.Final = final;
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

            LintTriggers(run);
            GroupChecks(run);
            TalkNameCheck(run);

            // a fixed order of the outputs, whatever the order of the ops
            plan.Archives.Sort(delegate (PlanArchive x, PlanArchive y) { return string.CompareOrdinal(x.Archive, y.Archive); });

            IdSpaces.Check(data, plan, options.Progress);
            if (options.Schemas != null) options.Schemas.Check(plan);
            if (!options.Alone && mods.Count > 1) OwnContradictions(data, plan, options);
            // the plan is kept by its caller: the shipped bytes and documents are not kept with it
            plan.Shipped = null;
            return plan;
        }

        /// <summary>
        /// Two lines of one mod that contradict each other are a conflict that no priority decides, also where a
        /// higher mod (a delete of the row or of the entry, a file for the entry) has both lines left out and they
        /// never meet in this plan. The entry lines of a mod for the entries where that happened to it
        /// (<see cref="Plan.Under"/>) are planned alone once more; a contradiction that shows only there is added,
        /// unless this plan has the same line for the entry already.
        /// </summary>
        static void OwnContradictions(DataSet data, Plan plan, PlanOptions options)
        {
            foreach (Mod m in plan.Mods)
            {
                Dictionary<string, bool> under;
                // the shop, drop box, skill and job skill lines name no entry: all of them are planned again, with
                // every entry line of the mod for the entries they were left out in
                bool kinds = plan.UnderLines.ContainsKey(m);
                if (!plan.Under.TryGetValue(m, out under))
                {
                    if (!kinds) continue;
                    under = new Dictionary<string, bool>();
                }
                // one line alone contradicts nothing, and neither does one line for the mod's own file: only the
                // entries with two row lines of the mod, two entry lines, or a row line and a delete are planned again
                var lines = new Dictionary<string, int>(StringComparer.Ordinal);
                var whole = new Dictionary<string, int>(StringComparer.Ordinal);
                var deleted = new Dictionary<string, bool>(StringComparer.Ordinal);
                var ops = new List<KeyValuePair<string, ModOp>>();
                foreach (ModOp op in m.Ops)
                {
                    var en = op as EntryOp; var ed = op as EditOp;
                    string name = en != null ? en.Name : ed != null ? ed.Entry : null;
                    if (name == null) continue;
                    string key = Plan.UnderKey(op.Archive, name);
                    if (!under.ContainsKey(key)) continue;
                    int n; lines.TryGetValue(key, out n);
                    int w; whole.TryGetValue(key, out w);
                    if (ed != null) foreach (RowOp r in ed.Rows) n += Math.Max(1, r.Attrs.Count);
                    else { w++; if (en.Delete) deleted[key] = true; }
                    lines[key] = n; whole[key] = w;
                    ops.Add(new KeyValuePair<string, ModOp>(key, op));
                }
                var part = new Mod { Id = m.Id, Name = m.Name, Version = m.Version, Dir = m.Dir };
                foreach (KeyValuePair<string, ModOp> o in ops) 
                {
                    int n = lines[o.Key], w = whole[o.Key];
                    if (kinds || n > 1 || w > 1 || (n > 0 && deleted.ContainsKey(o.Key))) part.Ops.Add(o.Value);
                }
                if (kinds) { part.Groups.AddRange(m.Groups); part.Skills.AddRange(m.Skills); part.JobSkills.AddRange(m.JobSkills); }
                if (part.IsEmpty) continue;
                Plan alone;
                try { alone = Build(data, new[] { part }, new PlanOptions { Maps = options.Maps, NoSync = options.NoSync, Alone = true, Shipped = plan.Shipped }); }
                catch (ModKitException) { continue; }
                var add = new List<PlanMessage>();
                foreach (PlanMessage c in alone.Conflicts)
                {
                    if (c.WinnerId != null || c.ModId != m.Id) continue;
                    bool has = false;
                    foreach (PlanMessage q in plan.Conflicts)
                        if (q.WinnerId == null && q.ModId == m.Id && q.Archive == c.Archive && q.Entry == c.Entry && q.Text == c.Text) { has = true; break; }
                    if (!has) add.Add(c);
                }
                plan.Conflicts.AddRange(add);
            }
        }

        // A line for a profile the data set does not have (a server line in a workspace of the two clients) is not
        // an error: the mod is applied to the targets that are there. The line still reaches the other per-id
        // profile through the mirror; a kms-client line without kms-client data reaches nothing.
        static bool Targeted(Run run, Mod mod, ModOp op)
        {
            Profile p = Profile.OfArchive(op.Archive);
            if (p == null || run.Data.Has(p) || run.Data.Profiles.Count == 0) return true;
            bool mirrored = false;
            foreach (Profile q in run.Data.Profiles) if (p.Mirrors(q)) mirrored = true;
            run.Plan.Msg(run.Plan.Notes, mod, op.Archive, null, p.Name + " is not a target of this data set: its lines are " + (mirrored ? "planned for the other per-id target only" : "not applied"));
            return false;
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
                if (st.DeletedBy != null)
                {
                    plan.Msg(plan.Notes, mod, op.Archive, st.Name, "entry is already deleted by " + Who(st.DeletedBy, mod));
                    if (st.DeletedBy != mod) st.DeleteLeftOut.Add(mod);
                    return;
                }
                if (!st.Existed && st.Base == null) { plan.Msg(plan.Errors, mod, op.Archive, op.Name, "cannot delete the entry, the archive has none of that name (" + ModLine.Of(op.Line) + ")"); return; }
                if (st.BaseMod == mod) { plan.Msg(plan.Conflicts, mod, op.Archive, st.Name, "deletes the entry that the same mod " + (st.Existed ? "replaces" : "adds")); return; }
                if (st.BaseMod != null)
                {
                    bool loses = run.Outranks(st.BaseMod, mod);
                    plan.EntryConflict(mod, op.Archive, st.Name, "deletes the entry that " + Who(st.BaseMod, mod) + " " + (st.Existed ? "replaces" : "adds"), loses ? st.BaseMod : mod, st.BaseMod);
                    if (loses) { st.DeleteLeftOut.Add(mod); return; }
                    // the file is left out, and so are its mod's edits of it
                    st.FileLeftOut[st.BaseMod] = mod;
                    DropEntryChange(plan, st);
                }
                st.DeletedBy = mod; st.BaseMod = null; st.Base = null;
                plan.Changes.Add(Mark(run, new PlanChange { Kind = ChangeKind.DeleteEntry, ModId = mod.Id, Archive = op.Archive, Entry = st.Name }));
                return;
            }

            byte[] bytes; string from;
            byte[] pending;
            if (op.File != null && mod.Files.TryGetValue(op.File.Replace('\\', '/'), out pending))
            {
                bytes = pending;
                from = "file " + op.File;
                if (run.Mirror != null) bytes = MirrorStrip(run, mod, op.Archive, op.Name, bytes);
            }
            else if (op.File != null)
            {
                if (mod.Dir == null) { plan.Msg(plan.Errors, mod, op.Archive, op.Name, "file=\"" + op.File + "\": the mod has no folder"); return; }
                string full = Mod.PathIn(mod.Dir, op.File);
                if (full == null) { plan.Msg(plan.Errors, mod, op.Archive, op.Name, "file=\"" + op.File + "\" is outside of the mod folder"); return; }
                if (!File.Exists(full)) { plan.Msg(plan.Errors, mod, op.Archive, op.Name, "file=\"" + op.File + "\": no such file in the mod folder (" + ModLine.Of(op.Line) + ")"); return; }
                bytes = File.ReadAllBytes(full);
                from = "file " + op.File;
                if (run.Mirror != null) bytes = MirrorStrip(run, mod, op.Archive, op.Name, bytes);
            }
            else
            {
                M2dEntry src = arch.Find(op.Copy);
                if (src == null) { plan.Msg(plan.Errors, mod, op.Archive, op.Name, "copy=\"" + op.Copy + "\": the archive has no such entry (" + ModLine.Of(op.Line) + ")"); return; }
                bytes = arch.Read(src);
                from = "copy of " + src.Name;
            }
            if (run.Mirror == null) LintLater(st, mod);
            if (st.DeletedBy == mod || st.DeleteLeftOut.Contains(mod)) { plan.Msg(plan.Conflicts, mod, op.Archive, st.Name, "puts an entry where the same mod deletes one"); return; }
            if (st.BaseMod == mod && !M2dWriter.BytesEqual(bytes, st.Base)) { plan.Msg(plan.Conflicts, mod, op.Archive, st.Name, (st.Existed ? "replaces" : "adds") + " the entry twice, with different content"); return; }
            if (st.DeletedBy != null)
            {
                bool loses = run.Outranks(st.DeletedBy, mod);
                plan.EntryConflict(mod, op.Archive, st.Name, "puts an entry where " + Who(st.DeletedBy, mod) + " deletes one", loses ? st.DeletedBy : mod, st.DeletedBy);
                if (loses) { st.FileLeftOut[mod] = st.DeletedBy; return; }
                // what the delete had left out is left out by this file now, and so is the delete
                Mod del = st.DeletedBy;
                st.DeleteLeftOut.Add(del);
                foreach (Mod m in new List<Mod>(st.FileLeftOut.Keys)) if (st.FileLeftOut[m] == del) st.FileLeftOut[m] = mod;
                DropEntryChange(plan, st);
            }
            else if (st.BaseMod != null)
            {
                if (M2dWriter.BytesEqual(bytes, st.Base)) { plan.Msg(plan.Notes, mod, op.Archive, st.Name, "same entry content as " + Who(st.BaseMod, mod)); return; }
                bool loses = run.Outranks(st.BaseMod, mod);
                plan.EntryConflict(mod, op.Archive, st.Name, (st.Existed ? "replaces" : "adds") + " the entry that " + Who(st.BaseMod, mod) + " also " + (st.Existed ? "replaces" : "adds"), loses ? st.BaseMod : mod, st.BaseMod);
                // the file that loses is left out, and so are its mod's edits of it (DoEditOp, OpenMap)
                if (loses) { st.FileLeftOut[mod] = st.BaseMod; return; }
                st.FileLeftOut[st.BaseMod] = mod;
                DropEntryChange(plan, st);
            }
            st.DeletedBy = null; st.Base = bytes; st.BaseMod = mod;
            plan.Changes.Add(Mark(run, new PlanChange
            {
                Kind = st.Existed ? ChangeKind.ReplaceEntry : ChangeKind.AddEntry, ModId = mod.Id, Archive = op.Archive, Entry = st.Name,
                NewValue = from + ", " + bytes.Length + " bytes", OldValue = st.Existed ? st.Original.Length + " bytes" : null,
            }));
            if (!st.Existed) plan.NewIds.Add(new NewId { Mod = mod, Archive = op.Archive, Entry = st.Name });
        }

        static void LintLater(EntryState st, Mod mod)
        {
            if (!Triggers.IsScript(st.Name)) return;
            if (st.LintBy == null) st.LintBy = new List<Mod>();
            if (!st.LintBy.Contains(mod)) st.LintBy.Add(mod);
        }

        // The trigger scripts the mods bring or edit are linted as they are written (TriggerLint.cs, the shipped
        // schema and allowlist), after all edits: their final bytes, with imports resolved against the archive as the
        // plan leaves it (the shipped entries with the plan's puts and deletes on them). An error blocks Apply unless
        // every mod that touches the script says trigger-lint="warn"; it is said for the highest of them.
        static void LintTriggers(Run run)
        {
            Plan plan = run.Plan;
            var byArchive = new Dictionary<string, List<EntryState>>(StringComparer.Ordinal);
            foreach (EntryState st in run.Order)
            {
                if (st.LintBy == null || st.DeletedBy != null || st.Final == null) continue;
                List<EntryState> l;
                if (!byArchive.TryGetValue(st.Archive, out l)) byArchive[st.Archive] = l = new List<EntryState>();
                l.Add(st);
            }
            foreach (KeyValuePair<string, List<EntryState>> kv in byArchive)
            {
                TriggerPacks packs;
                try { packs = TriggerPacks.Shipped(run.Data, kv.Key); } catch (ModKitException) { packs = null; }
                if (packs != null)
                {
                    var changes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                    foreach (EntryState st in run.Order)
                    {
                        if (st.Archive != kv.Key || !st.Name.StartsWith(Triggers.Folder, StringComparison.Ordinal)) continue;
                        if (st.DeletedBy != null) { if (st.Existed) changes[st.Name] = null; }
                        else if (st.Final != null && (!st.Existed || !M2dWriter.BytesEqual(st.Final, st.Original))) changes[st.Name] = st.Final;
                    }
                    packs = packs.Overlay(changes);
                }
                foreach (EntryState st in kv.Value)
                {
                    Mod top = st.LintBy[0];
                    bool warn = true;
                    foreach (Mod m in st.LintBy) { if (run.Outranks(m, top)) top = m; if (!m.TriggerLintWarn) warn = false; }
                    int warnings = 0;
                    foreach (TriggerFinding f in TriggerLint.Shipped.Lint(st.Name, st.Final, packs))
                    {
                        if (f.Level == "warning") { warnings++; continue; }
                        if (!f.IsError) continue;
                        string at = f.Line > 0 ? "line " + f.Line + (f.ImportedFrom != null ? " of " + f.ImportedFrom : "") + ": " : "";
                        plan.Msg(warn ? plan.Warnings : plan.Errors, top, st.Archive, st.Name,
                            "trigger lint: " + at + "[" + f.Code + "] " + f.Message + (warn ? " (trigger-lint=\"warn\")" : ""));
                    }
                    if (warnings > 0) plan.Msg(plan.Notes, top, st.Archive, st.Name, "trigger lint: " + warnings + " warning(s)");
                }
            }
        }

        // ---- rows ----

        static void DoEditOp(Run run, Mod mod, EditOp op)
        {
            Plan plan = run.Plan;
            M2dArchive arch = run.Open(mod, op.Archive);
            if (arch == null) return;
            EntryState st = run.State(arch, op.Archive, op.Entry);
            if ((st.DeletedBy != null || st.DeleteLeftOut.Contains(mod)) && DeletedEntryStays(run, mod, st, "edits an entry that " + Who(st.DeleteLeftOut.Contains(mod) ? mod : st.DeletedBy, mod) + " deletes")) return;
            if (st.Base == null)
            {
                plan.Msg(plan.Errors, mod, op.Archive, op.Entry, "the archive has no such entry (" + ModLine.Of(op.Line) + ")" + Suggest(arch, op.Entry));
                return;
            }
            if (run.Mirror == null) LintLater(st, mod);
            Mod instead;
            if (st.BaseMod != mod && st.FileLeftOut.TryGetValue(mod, out instead))
            {
                // the mod's own file is left out, and so are its edits of that file
                plan.Conflict(mod, op.Archive, st.Name, "edits its own entry, which is left out", Written(st, instead));
                plan.IsUnder(mod, st.Archive, st.Name);
                return;
            }
            if (st.BaseMod != null && st.BaseMod != mod)
            {
                string text = "edits an entry that mod " + st.BaseMod.Id + " " + (st.Existed ? "replaces" : "adds");
                // the whole entry of the higher mod is written as that mod has it
                if (!run.Outranks(mod, st.BaseMod)) { plan.Conflict(mod, op.Archive, st.Name, text, st.BaseMod); plan.IsUnder(mod, st.Archive, st.Name); return; }
                if (st.Existed && st.Original != null && st.Doc == null)
                {
                    // the edit is of the higher mod: it is made in the shipped entry, the other mod's file is left out
                    plan.EntryConflict(mod, op.Archive, st.Name, text, mod, st.BaseMod);
                    DropEntryChange(plan, st);
                    st.FileLeftOut[st.BaseMod] = mod;
                    st.Base = st.Original; st.BaseMod = null;
                }
                // nothing of the other mod is left out here: its entry is written, with this edit in it
                else plan.Conflict(mod, op.Archive, st.Name, text + ": the edit is made in that entry", mod);
            }
            if (!EnsureDoc(plan, mod, st)) return;
            foreach (RowOp r in op.Rows)
            {
                try
                {
                    if (r.Kind == RowOpKind.Change) DoChange(run, mod, st, r);
                    else if (r.Kind == RowOpKind.Delete) DoDelete(run, mod, st, r);
                    else DoAdd(run, mod, st, r);
                }
                catch (ModKitException x)
                {
                    string msg = x.Message;
                    string label = st.Doc.Label + ": ";
                    if (msg.StartsWith(label, StringComparison.Ordinal)) msg = msg.Substring(label.Length);
                    plan.Msg(plan.Errors, mod, op.Archive, st.Name, msg + " (" + ModLine.Of(r.Line) + ")");
                }
            }
        }

        // the diff line of the whole-entry op that a mod of higher priority now replaces
        static void DropEntryChange(Plan plan, EntryState st)
        {
            for (int i = plan.Changes.Count - 1; i >= 0; i--)
            {
                PlanChange c = plan.Changes[i];
                if (c.Archive != st.Archive || c.Entry != st.Name || !string.IsNullOrEmpty(c.Row)) continue;
                if (c.Kind == ChangeKind.DeleteEntry || c.Kind == ChangeKind.ReplaceEntry || c.Kind == ChangeKind.AddEntry) { plan.Changes.RemoveAt(i); return; }
            }
        }

        /// <summary>
        /// A mod works on an entry that another mod deletes. true: the entry stays deleted and the mod's lines for it
        /// are left out. false: this mod has the higher priority, the shipped entry is back and the lines go on.
        /// </summary>
        static bool DeletedEntryStays(Run run, Mod mod, EntryState st, string text)
        {
            Plan plan = run.Plan;
            Mod del = st.DeletedBy;
            // the mod's own delete, also where a higher mod has brought the entry back: the mod is wrong in any plan
            if (del == mod || st.DeleteLeftOut.Contains(mod)) { plan.Msg(plan.Errors, mod, st.Archive, st.Name, text); return true; }
            if (!run.Outranks(mod, del))
            {
                plan.Conflict(mod, st.Archive, st.Name, text, del);
                plan.IsUnder(mod, st.Archive, st.Name);
                return true;
            }
            if (st.Original == null)
            {
                // the entry was another mod's; the data ships none that could come back for this mod
                plan.Conflict(mod, st.Archive, st.Name, text, del, null, " wins (the data ships no such entry)");
                plan.IsUnder(mod, st.Archive, st.Name);
                return true;
            }
            plan.EntryConflict(mod, st.Archive, st.Name, text, mod, del);
            DropEntryChange(plan, st);
            // a file that the delete left out stays out: the entry is now the shipped one with this mod's lines
            foreach (Mod m in new List<Mod>(st.FileLeftOut.Keys)) if (st.FileLeftOut[m] == del) st.FileLeftOut[m] = mod;
            st.DeleteLeftOut.Add(del);
            st.DeletedBy = null; st.Base = st.Original;
            return false;
        }

        /// <summary>
        /// The mod whose version of the entry is written, for a mod whose file <paramref name="beat"/> left out:
        /// the one that beat it may have lost to a third mod in turn.
        /// </summary>
        static Mod Written(EntryState st, Mod beat)
        {
            Mod next;
            for (int i = 0; i < st.FileLeftOut.Count && st.FileLeftOut.TryGetValue(beat, out next); i++) beat = next;
            return beat;
        }

        /// <summary>
        /// A mod works inside a row that another mod deletes. true: the row stays deleted. false: this mod has the
        /// higher priority and the row is back (without what mods of lower priority changed in it).
        /// </summary>
        static bool DeletedRowStays(Run run, Mod mod, EntryState st, XmlElem e, string text)
        {
            Plan plan = run.Plan;
            for (int i = 0; i < st.Deleted.Count; i++)
            {
                bool inside = false;
                for (XmlElem c = e; c != null; c = c.Parent) if (c == st.Deleted[i].Key) inside = true;
                if (!inside) continue;
                XmlElem row = st.Deleted[i].Key;
                Mod del = st.Deleted[i].Value;
                // a mod that contradicts itself: no priority decides that
                if (del == mod) { plan.Msg(plan.Conflicts, mod, st.Archive, st.Name, text); return true; }
                if (!run.Outranks(mod, del)) { plan.Conflict(mod, st.Archive, st.Name, text, del); plan.IsUnder(mod, st.Archive, st.Name); return true; }
                plan.Conflict(mod, st.Archive, st.Name, text, mod, del);
                st.Ed.Undelete(row);
                st.Deleted.RemoveAt(i);
                string name = RowPath.Canonical(row);
                for (int k = plan.Changes.Count - 1; k >= 0; k--)
                {
                    PlanChange c = plan.Changes[k];
                    if (c.Kind == ChangeKind.DeleteRow && c.Archive == st.Archive && c.Entry == st.Name && c.Row == name && c.ModId == del.Id) { plan.Changes.RemoveAt(k); break; }
                }
                return false;
            }
            return false;
        }

        static bool EnsureDoc(Plan plan, Mod mod, EntryState st)
        {
            if (st.DocFailed) return false;
            if (st.Doc == null)
            {
                try
                {
                    // a shipped entry is parsed once for the plan and the plans of one mod alone made for it
                    XmlDoc doc = null;
                    bool shipped = st.Base == st.Original && plan.Shipped != null;
                    if (!shipped || !plan.Shipped.Docs.TryGetValue(st.Base, out doc)) doc = XmlDoc.Parse(st.Base, st.Archive + " " + st.Name);
                    if (shipped) plan.Shipped.Docs[st.Base] = doc;
                    st.Doc = doc;
                }
                catch (ModKitException x) { st.DocFailed = true; plan.Msg(plan.Errors, mod, st.Archive, st.Name, "cannot be edited as XML: " + x.Message); return false; }
                if (st.Doc.EncodingFallback)
                    plan.Msg(plan.Notes, mod, st.Archive, st.Name, "the text is not valid in its declared encoding; edited as " + st.Doc.EncodingName);
                st.Ed = new XmlEditor(st.Doc);
            }
            // new rows of a lower mod are written in front of those of a higher one, whatever the planning order
            st.Ed.Rank = plan.Mods.IndexOf(mod);
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

        static void DoChange(Run run, Mod mod, EntryState st, RowOp r)
        {
            object was = run.Plan.Ask;
            try { DoChangeLines(run, mod, st, r); }
            finally { run.Plan.Ask = was; }
        }

        static void DoDelete(Run run, Mod mod, EntryState st, RowOp r)
        {
            object was = run.Plan.Ask;
            run.Plan.Ask = r;
            try { DoDeleteLine(run, mod, st, r); }
            finally { run.Plan.Ask = was; }
        }

        static void DoAdd(Run run, Mod mod, EntryState st, RowOp r)
        {
            object was = run.Plan.Ask;
            run.Plan.Ask = r;
            try { DoAddLine(run, mod, st, r); }
            finally { run.Plan.Ask = was; }
        }

        static void DoChangeLines(Run run, Mod mod, EntryState st, RowOp r)
        {
            Plan plan = run.Plan;
            st.Ed.Rank = plan.Mods.IndexOf(mod);
            XmlElem target = Sub(st.Doc.Root, r.At);
            foreach (AttrOp a in r.Attrs)
            {
                plan.Ask = a;
                XmlElem el = Sub(target, a.At);
                string row = RowPath.Canonical(el);
                Mod del = DeletedBy(st, el);
                if (del != null && DeletedRowStays(run, mod, st, el, "changes the row " + row + " that " + Who(del, mod) + " deletes")) continue;
                // the mod's own delete of the row lost to a higher mod: the mod contradicts itself all the same
                if (del == null && OwnKeptDelete(run, st, mod, el)) { plan.Msg(plan.Conflicts, mod, st.Archive, st.Name, "changes the row " + row + " that " + Who(mod, mod) + " deletes"); continue; }
                string what = a.Kind == AttrOpKind.Text ? "text" : "@" + a.Name;
                string value = a.Kind == AttrOpKind.Unset ? null : a.Value;
                string old = a.Kind == AttrOpKind.Text ? el.InnerText : el.Attr(a.Name);
                if (a.Kind == AttrOpKind.Text && old == null)
                    throw new ModKitException("<" + el.Name + "> at " + row + " has child elements, its text cannot be set");
                string key = el.Start + "\n" + what;
                Claim prev;
                st.Claims.TryGetValue(key, out prev);
                if (run.Mirror != null && !MirrorKeeps(run, mod, st, el, a, prev, row, what, value)) continue;
                if (run.Mirror == null)
                {
                    // a second value of the same mod where a higher mod has the value: no priority decides that
                    Asked first = null; bool known = false;
                    List<Asked> at;
                    if (!st.AskedAt.TryGetValue(key, out at)) st.AskedAt[key] = at = new List<Asked>();
                    foreach (Asked q in at)
                        if (q.Mod == mod) { if (q.Value == value) known = true; else if (first == null) first = q; }
                    if (!known) { var q = new Asked { Mod = mod, Elem = el, Key = key, Value = value }; st.Asked.Add(q); at.Add(q); }
                    if (first != null && prev != null && prev.Mod != mod)
                    {
                        plan.Msg(plan.Conflicts, mod, st.Archive, st.Name, row + " " + what + ": " + Show(value) + " here, " + Show(first.Value) + " in " + Who(mod, mod));
                        continue;
                    }
                }
                if (prev != null)
                {
                    if (prev.Value == value) { plan.Msg(plan.Notes, mod, st.Archive, st.Name, row + " " + what + ": same value as " + Who(prev.Mod, mod)); continue; }
                    bool loses = run.Outranks(prev.Mod, mod);
                    string two = row + " " + what + ": " + Show(value) + " here, " + Show(prev.Value) + " in " + Who(prev.Mod, mod);
                    if (prev.Mod == mod) { plan.Msg(plan.Conflicts, mod, st.Archive, st.Name, two); continue; }
                    plan.Conflict(mod, st.Archive, st.Name, two, loses ? prev.Mod : mod, prev.Mod);
                    if (loses) continue;
                    RemoveChange(plan, st, row, a, prev);
                }
                else if (old == value || (a.Kind == AttrOpKind.Unset && old == null))
                {
                    plan.Msg(plan.Notes, mod, st.Archive, st.Name, row + " " + what + (old == null ? " is not there, nothing to remove" : " already is \"" + old + "\""));
                    st.Claims[key] = new Claim { Mod = mod, Elem = el, What = what, Value = value, Mirrored = run.Mirror != null };
                    continue;
                }
                st.Claims[key] = new Claim { Mod = mod, Elem = el, What = what, Value = value, Mirrored = run.Mirror != null };
                var ch = new PlanChange { ModId = mod.Id, Archive = st.Archive, Entry = st.Name, Row = row, Name = a.Name, OldValue = old, NewValue = value, Elem = el, State = st, Op = r, AttrOp = a, Mod = mod };
                if (a.Kind == AttrOpKind.Set) { st.Ed.SetAttr(el, a.Name, a.Value); ch.Kind = ChangeKind.SetAttr; }
                else if (a.Kind == AttrOpKind.Unset) { st.Ed.RemoveAttr(el, a.Name); ch.Kind = ChangeKind.RemoveAttr; }
                else { st.Ed.SetText(el, a.Value); ch.Kind = ChangeKind.SetText; }
                plan.Changes.Add(Mark(run, ch));
                if (a.Kind == AttrOpKind.Set)
                    plan.Probes.Add(new SchemaProbe { Mod = mod, Archive = st.Archive, Entry = st.Name, ElementPath = RowPath.NamePath(el, true), Attr = a.Name, Value = a.Value });
            }
        }

        static string Show(string v) { return v == null ? "(removed)" : "\"" + v + "\""; }

        // the mod of higher priority wins in the text; its diff line replaces the other one
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

        static void DoDeleteLine(Run run, Mod mod, EntryState st, RowOp r)
        {
            Plan plan = run.Plan;
            XmlElem target = Sub(st.Doc.Root, r.At);
            if (target == st.Doc.Root) throw new ModKitException("the root element cannot be deleted; delete the entry instead");
            string row = RowPath.Canonical(target);
            Mod del = DeletedBy(st, target);
            if (del != null)
            {
                if (del != mod && plan.Lines == mod) plan.UnderLines[mod] = true;
                plan.Msg(plan.Notes, mod, st.Archive, st.Name, "row " + row + " is already deleted by " + Who(del, mod));
                return;
            }
            var drop = new List<string>();
            bool kept = false;
            foreach (KeyValuePair<string, Claim> c in st.Claims)
            {
                bool inside = false;
                for (XmlElem e = c.Value.Elem; e != null; e = e.Parent) if (e == target) { inside = true; break; }
                if (!inside) continue;
                drop.Add(c.Key);
                if (c.Value.Mod != mod && run.Outranks(c.Value.Mod, mod)) kept = true;
            }
            var inside2 = new List<AddClaim>();
            foreach (AddClaim a in st.Adds)
                for (XmlElem e = a.Parent; e != null; e = e.Parent)
                    if (e == target) { inside2.Add(a); if (a.Mod != mod && run.Outranks(a.Mod, mod)) kept = true; break; }
            // a mod of higher priority changes the row or adds a row in it: it stays, and only that mod is named
            // (the row is not deleted, so the delete wins against no one)
            var said = new Dictionary<Mod, bool>();
            foreach (string k in drop)
            {
                Mod other = st.Claims[k].Mod;
                string text = "deletes the row " + row + " that " + Who(other, mod) + " changes";
                if (other == mod) { if (!said.ContainsKey(other)) plan.Msg(plan.Conflicts, mod, st.Archive, st.Name, text); }
                else if (kept ? run.Outranks(other, mod) && !said.ContainsKey(other) : !said.ContainsKey(other))
                    plan.Conflict(mod, st.Archive, st.Name, text, kept ? other : mod, other);
                said[other] = true;
            }
            // a change of the same mod that a higher mod's value hides is in no claim
            if (run.Mirror == null && !said.ContainsKey(mod))
                foreach (Asked q in st.Asked)
                    if (q.Mod == mod && Within(q.Elem, target)) { plan.Msg(plan.Conflicts, mod, st.Archive, st.Name, "deletes the row " + row + " that " + Who(mod, mod) + " changes"); break; }
            said.Clear();
            foreach (AddClaim a in inside2)
            {
                string text = "deletes the row " + row + " that " + Who(a.Mod, mod) + " adds a row in";
                if (a.Mod == mod) { if (!said.ContainsKey(mod)) plan.Msg(plan.Conflicts, mod, st.Archive, st.Name, text); }
                else if (kept ? run.Outranks(a.Mod, mod) && !said.ContainsKey(a.Mod) : !said.ContainsKey(a.Mod))
                    plan.Conflict(mod, st.Archive, st.Name, text, kept ? a.Mod : mod, a.Mod);
                said[a.Mod] = true;
            }
            if (kept) { if (run.Mirror == null) st.KeptDeletes.Add(new KeyValuePair<XmlElem, Mod>(target, mod)); return; }
            foreach (AddClaim a in inside2)
            {
                st.Ed.Remove(a.Edit);
                plan.Changes.Remove(a.Change);
                if (a.NewId != null) plan.NewIds.Remove(a.NewId);
                if (a.Key != null) st.AddKeys.Remove(a.Key);
                st.Adds.Remove(a);
            }
            // the delete wins: the changes inside the row are in no part of the plan
            foreach (string k in drop) st.Claims.Remove(k);
            for (int i = plan.Changes.Count - 1; i >= 0; i--)
            {
                PlanChange c = plan.Changes[i];
                if (c.Archive != st.Archive || c.Entry != st.Name || c.Row == null) continue;
                if (c.Kind != ChangeKind.SetAttr && c.Kind != ChangeKind.RemoveAttr && c.Kind != ChangeKind.SetText) continue;
                if (c.Row == row || c.Row.StartsWith(row + "/", StringComparison.Ordinal)) plan.Changes.RemoveAt(i);
            }
            for (int i = st.Deleted.Count - 1; i >= 0; i--)
                for (XmlElem e = st.Deleted[i].Key; e != null; e = e.Parent)
                    if (e == target) { st.Deleted.RemoveAt(i); break; }
            st.Ed.DropEditsWithin(target);
            st.Ed.Delete(target);
            st.Deleted.Add(new KeyValuePair<XmlElem, Mod>(target, mod));
            plan.Changes.Add(Mark(run, new PlanChange { Kind = ChangeKind.DeleteRow, ModId = mod.Id, Archive = st.Archive, Entry = st.Name, Row = row, OldValue = target.OuterText }));
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

        static readonly List<XmlElem> NoRows = new List<XmlElem>();

        // the children of a row that have the key, in their order. The keys of all its children are made once for
        // the entry (the document of a state is not changed by the plan; a state that gets another document has
        // other rows): a mod that adds many rows to one long table would make them again for every row
        static List<XmlElem> SameKey(EntryState st, XmlElem parent, string key)
        {
            Dictionary<string, List<XmlElem>> keys;
            if (!st.RowKeys.TryGetValue(parent, out keys))
            {
                st.RowKeys[parent] = keys = new Dictionary<string, List<XmlElem>>(StringComparer.Ordinal);
                foreach (XmlElem c in parent.Children)
                {
                    string k = RowKey(c);
                    List<XmlElem> l;
                    if (!keys.TryGetValue(k, out l)) keys[k] = l = new List<XmlElem>(1);
                    l.Add(c);
                }
            }
            List<XmlElem> hits;
            return keys.TryGetValue(key, out hits) ? hits : NoRows;
        }

        static void DoAddLine(Run run, Mod mod, EntryState st, RowOp r)
        {
            Plan plan = run.Plan;
            st.Ed.Rank = plan.Mods.IndexOf(mod);
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
                fromIndent = r.FromIndent ?? XmlEditor.CommonIndent(frag);
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
            if (del != null && DeletedRowStays(run, mod, st, parent, "adds " + row + " inside a row that " + Who(del, mod) + " deletes")) return;
            if (del == null && OwnKeptDelete(run, st, mod, parent)) { plan.Msg(plan.Conflicts, mod, st.Archive, st.Name, "adds " + row + " inside a row that " + Who(mod, mod) + " deletes"); return; }

            if (run.Mirror != null)
            {
                string stripped = MirrorStripRow(run, mod, st, RowPath.NamePath(parent, true), fdoc, frag);
                if (stripped != frag) { frag = stripped; fdoc = XmlDoc.ParseFragment(frag, "the new row"); made = fdoc.Root; }
            }
            string key = parent.Start + "\n" + RowKey(made);
            AddClaim other;
            if (r.Grouped) { }
            // two different rows the mod copies come out equal where the data itself holds twins: both are added
            else if (st.AddKeys.TryGetValue(key, out other) && other.Mod == mod && r.Copy != null && other.Copy != null && other.Copy != r.Copy) { }
            else if (st.AddKeys.TryGetValue(key, out other))
            {
                bool loses = run.Outranks(other.Mod, mod);
                if (other.Mod == mod) plan.Msg(plan.Conflicts, mod, st.Archive, st.Name, "adds the row " + row + " that " + Who(other.Mod, mod) + " also adds");
                else plan.Conflict(mod, st.Archive, st.Name, "adds the row " + row + " that " + Who(other.Mod, mod) + " also adds", loses ? other.Mod : mod, other.Mod);
                if (loses) return;
                if (other.Mod != mod)
                {
                    st.Ed.Remove(other.Edit);
                    st.Adds.Remove(other);
                    plan.Changes.Remove(other.Change);
                    if (other.NewId != null) plan.NewIds.Remove(other.NewId);
                }
            }
            else
            {
                foreach (XmlElem c in SameKey(st, parent, RowKey(made)))
                    if (DeletedBy(st, c) == null)
                    {
                        // a mirrored add of a row this side ships already (the same id, feature and locale; a string
                        // row the mod's side lacks, the other side has): that row gets the new row's attributes
                        if (run.Mirror != null && made.Attr("id") != null && made.Children.Count == 0)
                        {
                            var set = new RowOp { Kind = RowOpKind.Change, At = RowPath.Canonical(c), Line = r.Line };
                            foreach (XmlAttr a in made.Attrs) if (a.Name != "id" && a.Name != "feature" && a.Name != "locale") set.Attrs.Add(new AttrOp { Kind = AttrOpKind.Set, Name = a.Name, Value = a.Value });
                            plan.Msg(plan.Notes, mod, st.Archive, st.Name, "mirrored from " + run.Mirror.From + ": adds " + row + ", which this side ships already (line " + c.Line + "): its attributes are set instead");
                            DoChange(run, mod, st, set);
                            return;
                        }
                        plan.Msg(plan.Warnings, mod, st.Archive, st.Name, "adds " + row + ", and a row with the same " + (made.Attr("id") != null ? "id, feature and locale" : "attributes") + " is already there (line " + c.Line + ")");
                        break;
                    }
            }
            object edit;
            if (anchor == null) edit = st.Ed.Append(parent, frag, fromIndent);
            else if (before) edit = st.Ed.InsertBefore(anchor, frag, fromIndent);
            else edit = st.Ed.InsertAfter(anchor, frag, fromIndent);

            var claim = new AddClaim { Mod = mod, Edit = edit, Parent = parent, Key = r.Grouped ? null : key, Copy = r.Copy };
            st.Adds.Add(claim);
            if (!r.Grouped) st.AddKeys[key] = claim;
            plan.Changes.Add(claim.Change = Mark(run, new PlanChange { Kind = ChangeKind.AddRow, ModId = mod.Id, Archive = st.Archive, Entry = st.Name, Row = row, NewValue = frag, Elem = made, Source = src, State = st, Op = r, Mod = mod }));
            var attrs = new Dictionary<string, string>();
            foreach (XmlAttr a in made.Attrs) attrs[a.Name] = a.Value;
            string names = RowPath.NamePath(parent, false);
            if (!r.NoNewId) plan.NewIds.Add(claim.NewId = new NewId { Mod = mod, Archive = st.Archive, Entry = st.Name, NamePath = (names.Length > 0 ? names + "/" : "") + made.Name, Attrs = attrs });
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
