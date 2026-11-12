// A table editor's model: one entry of one archive seen as rows and columns, with every edit kept as the
// mod.xml operation it will become. A front end (the Toolbench plugin, tools/modkit/plugin) shows Columns and
// Rows in a grid and calls Set / Clone / Delete / Restore; the draft changes the <edit> of its Mod and nothing
// else. Nothing is written to any archive here: Planner and Workspace do that from the Mod.
//
// A row is a child element of one parent element of the entry (the root element unless a selector says
// otherwise); a column is an attribute name. Operations the grid has no cell for (an <add> elsewhere in the
// entry, a <change> of a nested row, <text>, <set at="...">) stay in the mod as they are and are counted in
// OtherOps.
using System;
using System.Collections.Generic;
using System.IO;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public sealed class TableRow
    {
        /// <summary>The row of the original entry; null for a row the mod adds.</summary>
        public XmlElem Original;
        /// <summary>Where unedited values come from: Original, or for an added row the row it copies or its literal element.</summary>
        public XmlElem Base;
        /// <summary>The selector a change or delete of this row uses; null for an added row.</summary>
        public string Selector;
        public RowOp Change, Delete, Add;
        /// <summary>A second change of the row, in an &lt;edit mirror="false"&gt; after the mirrored edit that holds
        /// Change: what an editor that edits each side itself sets on a row a hand-written, mirrored change names.
        /// The two never set the same cell: a cell the editor sets leaves Change, the rest of Change is mirrored as written.</summary>
        public RowOp Own;
        internal bool ChangeMirrored;

        public bool IsAdded { get { return Add != null; } }
        public bool IsDeleted { get { return Delete != null; } }
        public bool IsChanged { get { return Change != null || Own != null; } }

        /// <summary>A short name for the row header: "pet[id=100]", or "+ pet (copy of pet[id=101])".</summary>
        public string Label
        {
            get
            {
                if (Original != null) return RowPath.StepFor(Original);
                return "+ " + Base.Name + (Add.Copy != null ? " (copy of " + Add.Copy + ")" : "");
            }
        }
    }

    public sealed class TableDraft
    {
        public readonly Mod Mod;
        public readonly string Archive, Entry, ParentSelector;
        public readonly XmlDoc Doc;
        public readonly XmlElem Parent;
        public readonly List<string> Columns = new List<string>();
        public readonly List<TableRow> Rows = new List<TableRow>();
        /// <summary>Operations of the mod on this entry that have no row here; they are kept untouched.</summary>
        public int OtherOps;
        /// <summary>Operations of the mod on this entry that do not fit the original entry (the plan reports them as errors).</summary>
        public readonly List<string> Problems = new List<string>();

        /// <param name="data">the original data</param>
        /// <param name="mod">the mod that takes the edits (it may already hold some)</param>
        /// <param name="archive">"client:Xml.m2d"</param>
        /// <param name="entry">"table/pet.xml"</param>
        /// <param name="parentSelector">the element whose children are the rows; null or "" for the root element</param>
        public TableDraft(DataSet data, Mod mod, string archive, string entry, string parentSelector)
        {
            if (mod == null) throw new ArgumentNullException("mod");
            Mod = mod;
            Archive = DataSet.NormalizeRef(archive);
            Entry = entry.Replace('\\', '/');
            ParentSelector = parentSelector ?? "";
            M2dArchive arch = data.Open(Archive);
            M2dEntry e = arch.Find(Entry);
            if (e == null) throw new ModKitException(Archive + " has no entry " + Entry);
            Entry = e.Name;
            Doc = XmlDoc.Parse(arch.Read(e), Archive + " " + Entry);
            if (Doc.Root == null) throw new ModKitException(Archive + " " + Entry + " holds no XML element");
            Parent = ParentSelector.Length == 0 ? Doc.Root : RowPath.Parse(ParentSelector).Resolve(Doc.Root);

            var byElem = new Dictionary<XmlElem, TableRow>();
            foreach (XmlElem c in Parent.Children)
            {
                var row = new TableRow { Original = c, Base = c, Selector = RowPath.Canonical(c) };
                Rows.Add(row);
                byElem[c] = row;
                foreach (XmlAttr a in c.Attrs) AddColumn(a.Name);
            }
            foreach (EditOp ed in Edits())
                foreach (RowOp r in ed.Rows.ToArray())
                    Adopt(ed, r, byElem);
        }

        // ---- reading ----

        /// <summary>The value the row will have (null: the row has no such attribute).</summary>
        public string Get(TableRow row, string column)
        {
            string v;
            if (row.Own != null && Has(row.Own, column, out v)) return v;
            return Under(row, column);
        }

        // the value without the row's own (not mirrored) change
        string Under(TableRow row, string column)
        {
            string v;
            RowOp op = row.Add ?? row.Change;
            return op != null && Has(op, column, out v) ? v : row.Base.Attr(column);
        }

        static bool Has(RowOp op, string column, out string value)
        {
            for (int i = op.Attrs.Count - 1; i >= 0; i--)
            {
                AttrOp a = op.Attrs[i];
                if (IsCell(a) && a.Name == column) { value = a.Kind == AttrOpKind.Set ? a.Value : null; return true; }
            }
            value = null;
            return false;
        }

        /// <summary>The value in the original entry (for an added row: of the row it starts from).</summary>
        public string GetOriginal(TableRow row, string column) { return row.Base.Attr(column); }

        /// <summary>True when the mod gives this cell a value other than the original one.</summary>
        public bool IsEdited(TableRow row, string column) { return Get(row, column) != row.Base.Attr(column); }

        /// <summary>True when the mod holds any operation on this entry.</summary>
        public bool HasEdits { get { foreach (EditOp ed in Edits()) if (ed.Rows.Count > 0) return true; return false; } }

        // ---- editing ----

        /// <summary>Sets a cell; value null removes the attribute. Setting the original value back drops the edit.</summary>
        public void Set(TableRow row, string column, string value)
        {
            if (!IsAttrName(column)) throw new ModKitException("\"" + column + "\" is not an XML attribute name");
            if (row.IsDeleted) throw new ModKitException("row " + row.Label + " is deleted; restore it before editing");
            if (row.Add == null && row.Change != null && (row.Own != null || (NoMirror && row.ChangeMirrored)))
            {
                // the row's change is in an <edit> written to be mirrored: its other cells stay as written, and
                // this value goes into a change of its own in the <edit mirror="false">. The mirrored change no
                // longer sets this cell (Plan takes two values for one cell in a mod as a conflict), so the cell
                // is no longer mirrored either
                if (row.Own != null) row.Own.Attrs.RemoveAll(a => IsCell(a) && a.Name == column);
                row.Change.Attrs.RemoveAll(a => IsCell(a) && a.Name == column);
                if (row.Change.Attrs.Count == 0)
                {
                    Remove(row.Change);
                    row.Change = row.Own;
                    row.Own = null;
                    row.ChangeMirrored = false;
                    Set(row, column, value);    // nothing mirrored is left on the row: the plain case
                    return;
                }
                if (value != row.Base.Attr(column))
                {
                    if (row.Own == null)
                    {
                        row.Own = new RowOp { Kind = RowOpKind.Change, At = row.Selector };
                        Edit(true).Rows.Add(row.Own);
                    }
                    row.Own.Attrs.Add(value == null ? new AttrOp { Kind = AttrOpKind.Unset, Name = column } : new AttrOp { Kind = AttrOpKind.Set, Name = column, Value = value });
                    AddColumn(column);
                }
                else if (row.Own != null && row.Own.Attrs.Count == 0) { Remove(row.Own); row.Own = null; }
                return;
            }
            RowOp op = row.Add ?? row.Change;
            if (op != null) op.Attrs.RemoveAll(a => IsCell(a) && a.Name == column);
            if (value != row.Base.Attr(column))
            {
                if (op == null)
                {
                    op = row.Change = new RowOp { Kind = RowOpKind.Change, At = row.Selector };
                    EditOp into = Edit(true);
                    into.Rows.Add(op);
                    row.ChangeMirrored = into.Mirror;
                }
                op.Attrs.Add(value == null ? new AttrOp { Kind = AttrOpKind.Unset, Name = column } : new AttrOp { Kind = AttrOpKind.Set, Name = column, Value = value });
                AddColumn(column);
            }
            else if (op != null && op == row.Change && op.Attrs.Count == 0)
            {
                Remove(op);
                row.Change = null;
            }
        }

        /// <summary>True for a name an XML attribute can have: a letter, '_' or ':' first, then letters, digits, '.', '-', '_', ':'.</summary>
        public static bool IsAttrName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool ok = char.IsLetter(c) || c == '_' || c == ':' || (i > 0 && (char.IsDigit(c) || c == '.' || c == '-'));
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>A new row that starts as a copy of this one as it is shown (pending edits included).</summary>
        public TableRow Clone(TableRow row)
        {
            if (row.IsDeleted) throw new ModKitException("row " + row.Label + " is deleted; restore it before copying");
            var op = new RowOp { Kind = RowOpKind.Add };
            if (row.Add != null) { op.Copy = row.Add.Copy; op.Xml = row.Add.Xml; op.After = row.Add.After; op.Before = row.Add.Before; op.In = row.Add.In; }
            else op.Copy = row.Selector;
            RowOp from = row.Add ?? row.Change;
            foreach (RowOp o in new[] { from, row.Own })
                if (o != null)
                    foreach (AttrOp a in o.Attrs)
                        op.Attrs.Add(new AttrOp { Kind = a.Kind, Name = a.Name, Value = a.Value, At = a.At });
            Edit(true).Rows.Add(op);
            var made = new TableRow { Add = op, Base = row.Base };
            Rows.Add(made);
            return made;
        }

        /// <summary>A new row from its element text ("&lt;pet id=\"1\" /&gt;"), added at the end of the parent.</summary>
        public TableRow AddRow(string xml) { return AddRow(xml, null, false); }

        /// <summary>A new row from its element text, added right after (or before) a row of the entry; at the end when the anchor is null.</summary>
        public TableRow AddRow(string xml, TableRow anchor, bool before)
        {
            if (Parent != Doc.Root) throw new ModKitException("a new row from text goes in the root element; copy a row of " + ParentSelector + " instead");
            if (anchor != null && (anchor.IsAdded || anchor.Selector == null)) throw new ModKitException("a new row goes beside a row of the entry, not beside an added one");
            XmlDoc f = XmlDoc.ParseFragment(xml.Trim(), "the new row");
            if (f.Top.Count != 1) throw new ModKitException("a new row is one element, the text holds " + f.Top.Count);
            var op = new RowOp { Kind = RowOpKind.Add, Xml = xml.Trim() };
            if (anchor != null) { if (before) op.Before = anchor.Selector; else op.After = anchor.Selector; }
            Edit(true).Rows.Add(op);
            var made = new TableRow { Add = op, Base = f.Root };
            Rows.Add(made);
            foreach (XmlAttr a in f.Root.Attrs) AddColumn(a.Name);
            return made;
        }

        /// <summary>Deletes a row. An added row goes away; an original row gets a delete operation and its pending changes are dropped.</summary>
        public void Delete(TableRow row)
        {
            if (row.IsAdded)
            {
                Remove(row.Add);
                Rows.Remove(row);
                return;
            }
            if (row.IsDeleted) return;
            if (row.Own != null) { Remove(row.Own); row.Own = null; }
            if (row.Change != null) { Remove(row.Change); row.Change = null; }
            row.Delete = new RowOp { Kind = RowOpKind.Delete, At = row.Selector };
            Edit(true).Rows.Add(row.Delete);
        }

        /// <summary>Drops every cell the mod sets on an original row: its own change and the cells of a hand-written one.</summary>
        public void RevertCells(TableRow row)
        {
            if (row.Own != null) { Remove(row.Own); row.Own = null; }
            if (row.Change == null) return;
            row.Change.Attrs.RemoveAll(IsCell);
            if (row.Change.Attrs.Count == 0) { Remove(row.Change); row.Change = null; }
        }

        /// <summary>Takes back the delete of an original row.</summary>
        public void Restore(TableRow row)
        {
            if (row.Delete == null) return;
            Remove(row.Delete);
            row.Delete = null;
        }

        /// <summary>
        /// Writes this entry's operations into the mod for another archive too (a table that is in the client and
        /// the server archive is changed in both by saying so twice). Operations the mod already has for that
        /// archive and entry are replaced. Returns the number of operations copied.
        /// </summary>
        public int CopyEditsTo(string otherArchive)
        {
            otherArchive = DataSet.NormalizeRef(otherArchive);
            if (otherArchive == Archive) throw new ModKitException("the edits are already for " + Archive);
            Mod.Ops.RemoveAll(o => o is EditOp && o.Archive == otherArchive && SameEntry(((EditOp)o).Entry));
            var copy = new EditOp { Archive = otherArchive, Entry = Entry };
            foreach (EditOp ed in Edits())
                foreach (RowOp r in ed.Rows)
                {
                    var c = new RowOp { Kind = r.Kind, At = r.At, Copy = r.Copy, Xml = r.Xml, After = r.After, Before = r.Before, In = r.In };
                    foreach (AttrOp a in r.Attrs) c.Attrs.Add(new AttrOp { Kind = a.Kind, Name = a.Name, Value = a.Value, At = a.At });
                    copy.Rows.Add(c);
                }
            if (copy.Rows.Count > 0) Mod.Ops.Add(copy);
            return copy.Rows.Count;
        }

        // ---- the mod's <edit> of this entry ----

        static bool IsCell(AttrOp a) { return string.IsNullOrEmpty(a.At) && a.Kind != AttrOpKind.Text; }

        bool SameEntry(string name) { return string.Equals(name.Replace('\\', '/'), Entry, StringComparison.OrdinalIgnoreCase); }

        List<EditOp> Edits()
        {
            var list = new List<EditOp>();
            foreach (ModOp op in Mod.Ops)
            {
                var ed = op as EditOp;
                if (ed != null && ed.Archive == Archive && SameEntry(ed.Entry)) list.Add(ed);
            }
            return list;
        }

        /// <summary>
        /// When true, the &lt;edit&gt; this draft makes carries mirror="false": Plan does not mirror it between the
        /// global client and the server (an editor that shows and edits each of them itself).
        /// </summary>
        public bool NoMirror;

        EditOp Edit(bool create)
        {
            List<EditOp> list = Edits();
            if (!create) return list.Count > 0 ? list[0] : null;
            // a new line of a NoMirror draft goes into an <edit mirror="false">: one the mod has, else a new one after
            // the entry's other edits. An <edit> written to be mirrored stays as it is (its lines are the author's)
            foreach (EditOp e in list) if (e.Mirror == !NoMirror || !NoMirror) return e;
            var ed = new EditOp { Archive = Archive, Entry = Entry, Mirror = !NoMirror };
            int at = list.Count == 0 ? Mod.Ops.Count : Mod.Ops.IndexOf(list[list.Count - 1]) + 1;
            Mod.Ops.Insert(at, ed);
            return ed;
        }

        void Remove(RowOp op)
        {
            foreach (EditOp ed in Edits())
                if (ed.Rows.Remove(op))
                {
                    if (ed.Rows.Count == 0) Mod.Ops.Remove(ed);     // an <edit> without operations does not load
                    return;
                }
        }

        void AddColumn(string name) { if (!Columns.Contains(name)) Columns.Add(name); }

        XmlElem Find(string selector, string what)
        {
            if (string.IsNullOrEmpty(selector)) return Doc.Root;
            string error;
            XmlElem e;
            try { e = RowPath.Parse(selector).TryResolve(Doc.Root, out error); }
            catch (ModKitException x) { e = null; error = x.Message; }
            if (e == null) Problems.Add(what + "=\"" + selector + "\": " + error);
            return e;
        }

        // gives an operation that was in the mod already its row, the way Planner reads it
        void Adopt(EditOp ed, RowOp r, Dictionary<XmlElem, TableRow> byElem)
        {
            TableRow row;
            if (r.Kind != RowOpKind.Add)
            {
                XmlElem el = Find(r.At, r.Kind == RowOpKind.Change ? "change at" : "delete at");
                if (el == null) return;
                if (!byElem.TryGetValue(el, out row)) { OtherOps++; return; }
                if (r.Kind == RowOpKind.Delete)
                {
                    if (row.Delete == null) row.Delete = r; else ed.Rows.Remove(r);
                    return;
                }
                if (row.Change == null) { row.Change = r; row.ChangeMirrored = ed.Mirror; }
                else if (row.ChangeMirrored && !ed.Mirror)
                {
                    // a change in an <edit mirror="false"> after a mirrored one stays apart (TableRow.Own)
                    if (row.Own == null) row.Own = r;
                    else { row.Own.Attrs.AddRange(r.Attrs); ed.Rows.Remove(r); }
                }
                else { row.Change.Attrs.AddRange(r.Attrs); ed.Rows.Remove(r); }     // two changes of one row are one change
                foreach (AttrOp a in r.Attrs) if (IsCell(a)) AddColumn(a.Name);
                return;
            }

            XmlElem src = null, parent;
            if (r.Copy != null)
            {
                src = Find(r.Copy, "add copy");
                if (src == null) return;
            }
            else
            {
                try
                {
                    XmlDoc f = XmlDoc.ParseFragment(r.Xml.Trim(), "the new row");
                    if (f.Top.Count != 1) { Problems.Add("<add> takes one element, the text holds " + f.Top.Count); return; }
                    src = f.Root;
                }
                catch (ModKitException x) { Problems.Add("add: " + x.Message); return; }
            }
            if (r.After != null || r.Before != null)
            {
                XmlElem anchor = Find(r.After ?? r.Before, r.After != null ? "add after" : "add before");
                if (anchor == null) return;
                parent = anchor.Parent;
            }
            else if (r.In != null)
            {
                parent = Find(r.In, "add in");
                if (parent == null) return;
            }
            else parent = r.Copy != null ? src.Parent : Doc.Root;
            if (parent != Parent) { OtherOps++; return; }
            row = new TableRow { Add = r, Base = src };
            Rows.Add(row);
            foreach (XmlAttr a in src.Attrs) AddColumn(a.Name);
            foreach (AttrOp a in r.Attrs) if (IsCell(a)) AddColumn(a.Name);
        }
    }

    /// <summary>A folder of mods: every subfolder with a mod.xml is one mod.</summary>
    public static class ModFolder
    {
        /// <summary>
        /// The mods of a folder in the order of their folder names. Folders whose name starts with '.' or '_' are
        /// left out (a way to park a mod). A missing folder gives an empty list.
        /// </summary>
        public static List<Mod> LoadAll(string modsDir)
        {
            var mods = new List<Mod>();
            if (string.IsNullOrEmpty(modsDir) || !Directory.Exists(modsDir)) return mods;
            string[] dirs = Directory.GetDirectories(modsDir);
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            foreach (string d in dirs)
            {
                string name = Path.GetFileName(d);
                if (name.StartsWith(".", StringComparison.Ordinal) || name.StartsWith("_", StringComparison.Ordinal)) continue;
                if (File.Exists(Path.Combine(d, "mod.xml"))) mods.Add(Mod.Load(d));
            }
            return mods;
        }

        /// <summary>
        /// The mods of the folder with one of them taken from memory: a mod with the id of <paramref name="current"/>
        /// is replaced by it, otherwise it is added at the end (if it holds any operation). This is the set an
        /// editor plans with while its mod is not saved yet.
        /// </summary>
        public static List<Mod> LoadAllWith(string modsDir, Mod current)
        {
            List<Mod> mods = LoadAll(modsDir);
            if (current == null) return mods;
            for (int i = 0; i < mods.Count; i++)
                if (mods[i].Id == current.Id)
                {
                    if (!current.IsEmpty) mods[i] = current; else mods.RemoveAt(i);
                    return mods;
                }
            if (!current.IsEmpty) mods.Add(current);
            return mods;
        }

        /// <summary>The folder a mod of this id is saved to under the mods folder.</summary>
        public static string DirOf(string modsDir, string modId) { return Path.Combine(modsDir, modId); }
    }
}
