// NPC talk scripts for a mod: the steps (select, script, monologue, job) an NPC says, on every target that has them.
// The Korean client keeps all NPCs in combined files (script/npc/npcscript*.xml, one <npc id> each); the global
// client and the server keep one file per NPC (script/npc/<id, 8 digits>.xml, the steps right under the root).
// An edit is made on each side where its place is found, with XmlEditor on the current text, byte-exact elsewhere.
// A combined side is stored as two rows of the mod: a delete of npc[id=N] and an add of the whole new <npc> after
// it, so the rest of the combined file stays as shipped. A per-NPC side is a whole-file entry of the mod (as
// trigger scripts are). A talk edited back to the shipped text leaves the mod.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    /// <summary>A step of a talk: select, script, monologue or job, keyed by name, id, feature and locale.</summary>
    public sealed class TalkStep
    {
        public readonly XmlElem Elem;
        public TalkStep(XmlElem e) { Elem = e; }
        public string Kind { get { return Elem.Name; } }
        public string Id { get { return Elem.Attr("id") ?? ""; } }
        public string Feature { get { return Elem.Attr("feature") ?? ""; } }
        public string Locale { get { return Elem.Attr("locale") ?? ""; } }
        public string Key { get { return KeyOf(Kind, Id, Feature, Locale); } }

        public static string KeyOf(string kind, string id, string feature, string locale)
        {
            return kind + " " + id + (feature.Length > 0 ? " feature=" + feature : "") + (locale.Length > 0 ? " locale=" + locale : "");
        }

        public override string ToString() { return Key; }
    }

    /// <summary>The talk of one NPC on one target.</summary>
    public sealed class TalkSide
    {
        public Profile Profile;
        public string Archive;
        /// <summary>The combined file (Combined) or the NPC's own file.</summary>
        public string Entry;
        public bool Combined;
        /// <summary>The target ships a talk for the NPC.</summary>
        public bool Shipped;
        /// <summary>The mod changes or adds the talk here.</summary>
        public bool InMod;
        /// <summary>The talk text (for a combined side the &lt;npc&gt; element alone).</summary>
        public XmlDoc Doc;
        /// <summary>The element the steps are under: &lt;npc&gt; or the file's root.</summary>
        public XmlElem Root;
        public readonly List<TalkStep> Steps = new List<TalkStep>();

        public TalkStep Step(string key)
        {
            foreach (TalkStep s in Steps) if (s.Key == key) return s;
            return null;
        }

        public string Text { get { return Doc.Text; } }
    }

    /// <summary>The talk of an NPC on every target that has it.</summary>
    public sealed class NpcTalk
    {
        public long NpcId;
        public readonly List<TalkSide> Sides = new List<TalkSide>();

        /// <summary>The step keys of every side, in the order of the first side that has each.</summary>
        public List<string> Keys
        {
            get
            {
                var l = new List<string>();
                var seen = new HashSet<string>();
                foreach (TalkSide s in Sides) foreach (TalkStep t in s.Steps) if (seen.Add(t.Key)) l.Add(t.Key);
                return l;
            }
        }

        public TalkSide Side(string archive)
        {
            foreach (TalkSide s in Sides) if (s.Archive == archive) return s;
            return null;
        }
    }

    /// <summary>
    /// A place in a talk: a step key, then elements below it by name and number among the children of that name
    /// ("content", 0 is the first content of the step). It is found anew on each side.
    /// </summary>
    public sealed class TalkPath
    {
        public readonly string Step;
        public readonly List<KeyValuePair<string, int>> Below = new List<KeyValuePair<string, int>>();

        public TalkPath(string step) { Step = step; }

        public TalkPath Then(string name, int index)
        {
            var p = new TalkPath(Step);
            p.Below.AddRange(Below);
            p.Below.Add(new KeyValuePair<string, int>(name, index));
            return p;
        }

        /// <summary>The path of an element of the side (a step or an element under one); null when it is not under a step.</summary>
        public static TalkPath Of(TalkSide side, XmlElem e) { return Of(side.Root, e); }

        /// <summary>The path of an element under root (an &lt;npc&gt; or a talk file's root); null when it is not under a step.</summary>
        public static TalkPath Of(XmlElem root, XmlElem e)
        {
            var chain = new List<XmlElem>();
            for (XmlElem x = e; x != null && x != root; x = x.Parent) chain.Insert(0, x);
            if (chain.Count == 0 || chain[0].Parent != root) return null;
            var p = new TalkPath(new TalkStep(chain[0]).Key);
            for (int i = 1; i < chain.Count; i++)
            {
                int n = 0;
                foreach (XmlElem c in chain[i].Parent.Children) { if (c == chain[i]) break; if (c.Name == chain[i].Name) n++; }
                p.Below.Add(new KeyValuePair<string, int>(chain[i].Name, n));
            }
            return p;
        }

        /// <summary>The element on the side, or null when the side has no such place.</summary>
        public XmlElem Resolve(TalkSide side)
        {
            TalkStep s = side.Step(Step);
            if (s == null) return null;
            XmlElem e = s.Elem;
            foreach (KeyValuePair<string, int> b in Below)
            {
                XmlElem hit = null;
                int n = 0;
                foreach (XmlElem c in e.Children)
                    if (c.Name == b.Key && n++ == b.Value) { hit = c; break; }
                if (hit == null) return null;
                e = hit;
            }
            return e;
        }

        public override string ToString()
        {
            var sb = new StringBuilder(Step);
            foreach (KeyValuePair<string, int> b in Below) sb.Append(" / ").Append(b.Key).Append(' ').Append(b.Value + 1);
            return sb.ToString();
        }
    }

    public sealed partial class TalkEditor
    {
        public const string Folder = "script/npc/";
        public static readonly string[] StepKinds = { "select", "script", "monologue", "job" };
        static readonly Regex CombinedName = new Regex(@"^script/npc/npcscript[^/]*\.xml$", RegexOptions.IgnoreCase);

        public readonly DataSet Data;
        public readonly Mod Mod;
        readonly TriggerEditor files;

        public TalkEditor(DataSet data, Mod mod)
        {
            if (data == null) throw new ArgumentNullException("data");
            if (mod == null) throw new ArgumentNullException("mod");
            Data = data; Mod = mod;
            files = new TriggerEditor(data, mod);
        }

        public static string Padded(long id) { return id.ToString("00000000", CultureInfo.InvariantCulture); }
        public static string FileEntry(long id) { return Folder + Padded(id) + ".xml"; }
        public static string Selector(long id) { return "npc[id=" + id.ToString(CultureInfo.InvariantCulture) + "]"; }
        public static bool IsStep(XmlElem e) { return Array.IndexOf(StepKinds, e.Name) >= 0; }

        // ---- the shipped combined files, indexed once per archive ----

        sealed class CombinedIndex
        {
            internal readonly List<string> Entries = new List<string>();
            internal readonly Dictionary<long, KeyValuePair<string, XmlElem>> Npcs = new Dictionary<long, KeyValuePair<string, XmlElem>>();
            internal readonly Dictionary<string, XmlDoc> Docs = new Dictionary<string, XmlDoc>(StringComparer.OrdinalIgnoreCase);
        }

        static bool TryId(string s, out long id)
        {
            return long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out id);
        }

        CombinedIndex Index(string archive)
        {
            object o;
            string key = "npctalk.combined\n" + archive;
            if (Data.Cache.TryGetValue(key, out o)) return (CombinedIndex)o;
            var ix = new CombinedIndex();
            if (Data.Exists(archive))
            {
                M2dArchive a = Data.Open(archive);
                foreach (M2dEntry e in a.Entries)
                {
                    if (!CombinedName.IsMatch(e.Name.Replace('\\', '/'))) continue;
                    XmlDoc d;
                    try { d = XmlDoc.Parse(a.Read(e), archive + " " + e.Name); }
                    catch (ModKitException) { continue; }
                    ix.Entries.Add(e.Name);
                    ix.Docs[e.Name] = d;
                    if (d.Root == null) continue;
                    foreach (XmlElem n in d.Root.Children)
                    {
                        long id;
                        if (n.Name == "npc" && TryId(n.Attr("id"), out id) && !ix.Npcs.ContainsKey(id))
                            ix.Npcs[id] = new KeyValuePair<string, XmlElem>(e.Name, n);
                    }
                }
            }
            Data.Cache[key] = ix;
            return ix;
        }

        /// <summary>The archives (Xml.m2d of each target) and whether each keeps the talks in combined files.</summary>
        public List<KeyValuePair<string, bool>> Archives()
        {
            var l = new List<KeyValuePair<string, bool>>();
            foreach (Profile p in Data.Profiles)
            {
                string archive = p.ArchiveIn("x:Xml.m2d");
                if (!Data.Exists(archive)) continue;
                l.Add(new KeyValuePair<string, bool>(archive, Index(archive).Entries.Count > 0));
            }
            return l;
        }

        // ---- the mod's rows of a combined file ----

        EditOp EditOf(string archive, string entry, bool make)
        {
            foreach (ModOp o in Mod.Ops)
            {
                var e = o as EditOp;
                if (e != null && e.Archive == archive && string.Equals(e.Entry.Replace('\\', '/'), entry, StringComparison.OrdinalIgnoreCase)) return e;
            }
            if (!make) return null;
            var n = new EditOp { Archive = archive, Entry = entry };
            Mod.Ops.Add(n);
            return n;
        }

        /// <summary>True for a combined talk file of the Korean client (script/npc/npcscript*.xml).</summary>
        internal static bool IsCombined(string entry) { return entry != null && CombinedName.IsMatch(entry.Replace('\\', '/')); }

        static long NpcOfRow(RowOp r)
        {
            long id;
            if (r.Kind == RowOpKind.Add && r.Xml != null && r.Copy == null)
            {
                Match m = Regex.Match(r.Xml, @"^\s*<npc\s[^>]*?\bid\s*=\s*[""'](\d+)[""']");
                if (m.Success && TryId(m.Groups[1].Value, out id)) return id;
            }
            if (r.Kind == RowOpKind.Delete && r.At != null)
            {
                Match m = Regex.Match(r.At, @"^npc\[id=[""']?(\d+)[""']?\]$");
                if (m.Success && TryId(m.Groups[1].Value, out id)) return id;
            }
            return -1;
        }

        /// <summary>The mod's text of the NPC in a combined file: its added &lt;npc&gt;; deleted = the mod deletes it and adds none.</summary>
        string ModCombined(string archive, long npc, out string entry, out bool deleted)
        {
            entry = null; deleted = false;
            foreach (string name in Index(archive).Entries)
            {
                EditOp ed = EditOf(archive, name, false);
                if (ed == null) continue;
                foreach (RowOp r in ed.Rows)
                {
                    if (NpcOfRow(r) != npc) continue;
                    entry = name;
                    if (r.Kind == RowOpKind.Add) { deleted = false; return r.Xml.Trim(); }
                    deleted = true;
                }
            }
            return null;
        }

        // ---- reading ----

        static TalkSide Side(Profile p, string archive, string entry, bool combined, XmlDoc doc, XmlElem root)
        {
            var s = new TalkSide { Profile = p, Archive = archive, Entry = entry, Combined = combined, Doc = doc, Root = root };
            if (root != null) foreach (XmlElem c in root.Children) if (IsStep(c)) s.Steps.Add(new TalkStep(c));
            return s;
        }

        static XmlDoc Fragment(string text, XmlDoc like, string label)
        {
            XmlDoc d = XmlDoc.ParseFragment(text, label);
            if (like != null) { d.NewLine = like.NewLine; d.IndentUnit = like.IndentUnit; }
            return d;
        }

        /// <summary>The NPC's talk on every target that has it (as the mod has it, else as shipped).</summary>
        public NpcTalk Open(long npc)
        {
            var t = new NpcTalk { NpcId = npc };
            foreach (Profile p in Data.Profiles)
            {
                string archive = p.ArchiveIn("x:Xml.m2d");
                if (!Data.Exists(archive)) continue;
                TalkSide s = Read(p, archive, npc);
                if (s != null) t.Sides.Add(s);
            }
            return t;
        }

        TalkSide Read(Profile p, string archive, long npc)
        {
            CombinedIndex ix = Index(archive);
            if (ix.Entries.Count > 0)
            {
                KeyValuePair<string, XmlElem> shipped;
                bool has = ix.Npcs.TryGetValue(npc, out shipped);
                string entry; bool deleted;
                string text = ModCombined(archive, npc, out entry, out deleted);
                if (text == null && (deleted || !has)) return null;
                XmlDoc like = has ? shipped.Value.Doc : ix.Docs[ix.Entries[ix.Entries.Count - 1]];
                string name = entry ?? (has ? shipped.Key : ix.Entries[ix.Entries.Count - 1]);
                XmlDoc d = Fragment(text ?? shipped.Value.OuterText, like, archive + " " + name + " " + Selector(npc));
                TalkSide s = Side(p, archive, name, true, d, d.Root);
                s.Shipped = has; s.InMod = text != null;
                return s;
            }
            string file = FileEntry(npc);
            EntryOp op = files.OpOf(archive, file);
            string n;
            byte[] ship = files.Shipped(archive, file, out n);
            byte[] b = op != null ? files.ModBytes(op) : ship;
            if (b == null) return null;
            XmlDoc doc = XmlDoc.Parse(b, archive + " " + n);
            TalkSide f = Side(p, archive, op != null ? op.Name : n, false, doc, doc.Root);
            f.Shipped = ship != null; f.InMod = op != null;
            return f;
        }

        /// <summary>The ids of every NPC with a talk on any target, shipped or added by the mod, in order.</summary>
        public List<long> NpcIds()
        {
            var set = new SortedDictionary<long, bool>();
            var fileName = new Regex(@"^script/npc/(\d+)\.xml$", RegexOptions.IgnoreCase);
            foreach (KeyValuePair<string, bool> a in Archives())
            {
                if (a.Value)
                {
                    foreach (long id in Index(a.Key).Npcs.Keys) set[id] = true;
                    continue;
                }
                foreach (M2dEntry e in Data.Open(a.Key).Entries)
                {
                    Match m = fileName.Match(e.Name.Replace('\\', '/'));
                    long id;
                    if (m.Success && TryId(m.Groups[1].Value, out id)) set[id] = true;
                }
            }
            foreach (ModOp o in Mod.Ops)
            {
                var e = o as EntryOp;
                long id;
                if (e != null && !e.Delete)
                {
                    Match m = fileName.Match(e.Name.Replace('\\', '/'));
                    if (m.Success && TryId(m.Groups[1].Value, out id)) set[id] = true;
                }
                var ed = o as EditOp;
                if (ed != null && IsCombined(ed.Entry)) foreach (RowOp r in ed.Rows) if (r.Kind == RowOpKind.Add && (id = NpcOfRow(r)) >= 0) set[id] = true;
            }
            return new List<long>(set.Keys);
        }

        // ---- storing ----

        /// <summary>Puts the side's new text into the mod; the text of the shipped talk takes the side out of the mod.</summary>
        void Store(TalkSide side, long npc, string text)
        {
            if (!side.Combined)
            {
                files.Put(side.Archive, side.Entry, side.Doc.Encode(text));
                return;
            }
            KeyValuePair<string, XmlElem> shipped;
            bool has = Index(side.Archive).Npcs.TryGetValue(npc, out shipped);
            string entry = has ? shipped.Key : side.Entry;
            // the mod's earlier rows of this NPC go; the new ones are made from the text
            foreach (string name in Index(side.Archive).Entries)
            {
                EditOp old = EditOf(side.Archive, name, false);
                if (old == null) continue;
                old.Rows.RemoveAll(r => NpcOfRow(r) == npc);
                if (old.Rows.Count == 0) Mod.Ops.Remove(old);
            }
            if (has && Same(shipped.Value.OuterText, text)) return;
            EditOp ed = EditOf(side.Archive, entry, true);
            if (has)
            {
                ed.Rows.Add(new RowOp { Kind = RowOpKind.Delete, At = Selector(npc), Sync = false });
                ed.Rows.Add(new RowOp { Kind = RowOpKind.Add, Xml = text, After = Selector(npc), Sync = false });
            }
            else ed.Rows.Add(new RowOp { Kind = RowOpKind.Add, Xml = text, Sync = false });
        }

        static bool Same(string a, string b) { return a.Replace("\r\n", "\n").Trim() == b.Replace("\r\n", "\n").Trim(); }

        /// <summary>Takes the NPC's talk out of the mod on every target.</summary>
        public void Revert(long npc)
        {
            foreach (KeyValuePair<string, bool> a in Archives())
            {
                if (!a.Value) { files.Revert(a.Key, FileEntry(npc)); continue; }
                foreach (string name in Index(a.Key).Entries)
                {
                    EditOp ed = EditOf(a.Key, name, false);
                    if (ed == null) continue;
                    ed.Rows.RemoveAll(r => NpcOfRow(r) == npc);
                    if (ed.Rows.Count == 0) Mod.Ops.Remove(ed);
                }
            }
            // the texts the mod gave the NPC go with its talk
            DropUnusedOwnTexts(npc, Open(npc));
        }

        // ---- edits; each is made on every side where the place is found ----

        /// <summary>Sets an attribute at the place on every side that has it; null or empty removes it. Returns the talk as it is now.</summary>
        public NpcTalk SetAttr(NpcTalk t, TalkPath at, string attr, string value, List<string> missed)
        {
            if (string.IsNullOrEmpty(attr)) throw new ModKitException("no attribute name");
            return Each(t, at, missed, (side, ed, e) =>
            {
                if (string.IsNullOrEmpty(value)) ed.RemoveAttr(e, attr);
                else ed.SetAttr(e, attr, value);
            });
        }

        /// <summary>
        /// Sets several attributes at the place at once (a null or empty value removes one), on every side that has it:
        /// one that fails leaves the mod as it was. Returns the talk as it is now.
        /// </summary>
        public NpcTalk SetAttrs(NpcTalk t, TalkPath at, IList<KeyValuePair<string, string>> attrs, List<string> missed)
        {
            foreach (KeyValuePair<string, string> a in attrs) if (string.IsNullOrEmpty(a.Key)) throw new ModKitException("no attribute name");
            return Each(t, at, missed, (side, ed, e) =>
            {
                foreach (KeyValuePair<string, string> a in attrs)
                {
                    if (string.IsNullOrEmpty(a.Value)) ed.RemoveAttr(e, a.Key);
                    else ed.SetAttr(e, a.Key, a.Value);
                }
            });
        }

        /// <summary>Removes the element at the place (a step, content, distractor or event) on every side that has it.</summary>
        public NpcTalk Remove(NpcTalk t, TalkPath at, List<string> missed)
        {
            return Each(t, at, missed, (side, ed, e) =>
            {
                if (e == side.Root) throw new ModKitException("the talk itself cannot be removed here; revert it instead");
                ed.Delete(e);
            });
        }

        /// <summary>
        /// Moves the element at the place (a page, a choice or an event) one place up (by -1) or down (by 1) among the
        /// siblings of its name, on every side that has it and the sibling; the other sides are missed. A step cannot move:
        /// its place in the file means nothing to the client.
        /// </summary>
        public NpcTalk Move(NpcTalk t, TalkPath at, int by, List<string> missed)
        {
            if (at == null || at.Below.Count == 0) throw new ModKitException("only a page, a choice or an event moves, not a step");
            if (by != -1 && by != 1) throw new ModKitException("an element moves one place up or down");
            // the shown side decides: its first element moves no higher, its last no lower
            XmlElem shown = Shown(t, at);
            if (shown != null && shown.Parent != null)
            {
                var sib = new List<XmlElem>();
                foreach (XmlElem c in shown.Parent.Children) if (c.Name == shown.Name) sib.Add(c);
                int k = sib.IndexOf(shown) + by;
                if (k < 0) throw new ModKitException(at + " is the first " + shown.Name + ": it moves no higher");
                if (k >= sib.Count) throw new ModKitException(at + " is the last " + shown.Name + ": it moves no lower");
            }
            return Each(t, at, missed, (side, ed, e) =>
            {
                var same = new List<XmlElem>();
                foreach (XmlElem c in e.Parent.Children) if (c.Name == e.Name) same.Add(c);
                int i = same.IndexOf(e) + by;
                if (i < 0 || i >= same.Count) throw new NotFound();
                XmlElem other = same[i];
                string text = e.OuterText, from = side.Doc.IsFirstOnLine(e.Start) ? side.Doc.IndentOf(e.Start) : XmlEditor.CommonIndent(text);
                ed.Delete(e);
                if (by > 0) ed.InsertAfter(other, text, from);
                else ed.InsertBefore(other, text, from);
            });
        }

        /// <summary>
        /// Adds an element (literal XML, one element) under the place: as its last child, or after the child
        /// "after" (a name and number under the place) when one is given. A null place adds a step to the talk.
        /// </summary>
        public NpcTalk Insert(NpcTalk t, TalkPath under, KeyValuePair<string, int>? after, string xml, List<string> missed)
        {
            XmlDoc frag;
            try { frag = XmlDoc.ParseFragment((xml ?? "").Trim(), "the new element"); }
            catch (ModKitException e) { throw new ModKitException("the new element is not well-formed XML: " + e.Message); }
            if (frag.Top.Count != 1) throw new ModKitException("the new element must be one element, the text holds " + frag.Top.Count);
            string text = xml.Trim();
            string from = XmlEditor.CommonIndent(text);
            if (under == null)
            {
                if (!IsStep(frag.Root)) throw new ModKitException("a talk holds select, script, monologue and job steps, not <" + frag.Root.Name + ">");
                string key = new TalkStep(frag.Root).Key;
                foreach (TalkSide s in t.Sides) if (s.Step(key) != null) throw new ModKitException("the talk has a step " + key + " already (" + s.Archive + ")");
            }
            return Each(t, under, missed, (side, ed, e) =>
            {
                XmlElem anchor = null;
                if (after.HasValue)
                {
                    int n = 0;
                    foreach (XmlElem c in e.Children) if (c.Name == after.Value.Key && n++ == after.Value.Value) { anchor = c; break; }
                    if (anchor == null) throw new NotFound();
                }
                else if (under == null)
                {
                    // a step goes after the last step of its kind, else at the end
                    foreach (XmlElem c in e.Children) if (c.Name == frag.Root.Name) anchor = c;
                }
                if (anchor != null) ed.InsertAfter(anchor, text, from);
                else ed.Append(e, text, from);
            }, true);
        }

        /// <summary>The XML of a new step with one empty content.</summary>
        public static string NewStepXml(string kind, string id)
        {
            if (Array.IndexOf(StepKinds, kind) < 0) throw new ModKitException("\"" + kind + "\" is not a step kind (" + string.Join(", ", StepKinds) + ")");
            long n;
            if (!TryId(id, out n)) throw new ModKitException("the step id \"" + id + "\" is not a number");
            return "<" + kind + " id=\"" + n.ToString(CultureInfo.InvariantCulture) + "\">\n    <content text=\"\" />\n</" + kind + ">";
        }

        /// <summary>Adds a new step (NewStepXml) on every side.</summary>
        public NpcTalk AddStep(NpcTalk t, string kind, string id, List<string> missed)
        {
            return Insert(t, null, null, NewStepXml(kind, id), missed);
        }

        sealed class NotFound : Exception { }

        /// <summary>
        /// A place under a step is the n-th child of its name, found anew on each side; where the step differs between
        /// the sides, the same place names another page or choice. So an edit inside a step goes only to the sides
        /// whose step is the same (SameStep) as on the first side that has it (the side the Dialogue view shows).
        /// Null when the side takes the edit, else the side and why, for missed.
        /// </summary>
        internal static string DiffersFromShown(NpcTalk t, TalkPath at, TalkSide side, bool inside = false)
        {
            if (at == null || (at.Below.Count == 0 && !inside)) return null;
            TalkStep mine = side.Step(at.Step);
            foreach (TalkSide s in t.Sides)
            {
                TalkStep st = s.Step(at.Step);
                if (st == null) continue;
                if (s == side || SameStep(st.Elem, mine.Elem)) return null;
                return side.Archive + " (its " + at.Step + " differs from the " + s.Archive + "'s, so the place may be another line there)";
            }
            return null;
        }

        // inside: the edit goes among the children of the place (an insert), so a step that differs is no place for it
        NpcTalk Each(NpcTalk t, TalkPath at, List<string> missed, Action<TalkSide, XmlEditor, XmlElem> edit, bool inside = false)
        {
            if (t == null || t.Sides.Count == 0) throw new ModKitException("the NPC has no talk to edit");
            var made = new List<KeyValuePair<TalkSide, string>>();
            foreach (TalkSide s in t.Sides)
            {
                XmlElem e = at == null ? s.Root : at.Resolve(s);
                if (e == null) { if (missed != null) missed.Add(s.Archive); continue; }
                string differs = DiffersFromShown(t, at, s, inside);
                if (differs != null) { if (missed != null) missed.Add(differs); continue; }
                var ed = new XmlEditor(s.Doc);
                try { edit(s, ed, e); }
                catch (NotFound) { if (missed != null) missed.Add(s.Archive); continue; }
                made.Add(new KeyValuePair<TalkSide, string>(s, ed.Apply()));
            }
            if (made.Count == 0) throw new ModKitException("no target has " + (at == null ? "the talk" : at.ToString()));
            foreach (KeyValuePair<TalkSide, string> m in made) Store(m.Key, t.NpcId, m.Value);
            NpcTalk now = Open(t.NpcId);
            // a removed line or a line set to another text may leave a text of the NPC's own that nothing names
            DropUnusedOwnTexts(t.NpcId, now);
            return now;
        }
    }
}
