// A mod: a folder with mod.xml (hand-writable) and optional data files. See tools/modkit/README.md for the format.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public sealed class IdPlace
    {
        public string Archive;      // "client:Xml.m2d"
        public string Entry;        // entry name pattern: * = any run without '/', {id} = the id
        public string Rows;         // selector of the rows inside the entries, or null when the entry is the row
        public string Key = "id";   // the attribute that holds the id (with Rows)
    }

    public sealed class IdRange
    {
        public string Space;
        public long From, To;
        public bool Strict = true;  // false: an id that is already used gives a warning instead of an error
        public readonly List<IdPlace> Places = new List<IdPlace>();
        public int Line;
    }

    public abstract class ModOp
    {
        public string Archive;
        public int Line;
    }

    /// <summary>Add or replace a whole entry (from a file of the mod or as a copy of a shipped entry), or delete one.</summary>
    public sealed class EntryOp : ModOp
    {
        public string Name;
        public string Copy;         // entry of the same archive
        public string File;         // path under the mod folder
        public bool Delete;
    }

    public enum RowOpKind { Change, Add, Delete }
    public enum AttrOpKind { Set, Unset, Text }

    public sealed class AttrOp
    {
        public AttrOpKind Kind;
        public string Name;
        public string Value;
        public string At;           // selector under the row of the op, or null
        /// <summary>false (sync="false" on the line): this attribute is not carried to the other side by the field maps.</summary>
        public bool Sync = true;
        public int Line;
    }

    public sealed class RowOp
    {
        public RowOpKind Kind;
        public string At;           // Change, Delete
        public string Copy;         // Add: the row to copy
        public string Xml;          // Add: literal element text
        public string After, Before, In;    // Add: where (at most one)
        public readonly List<AttrOp> Attrs = new List<AttrOp>();
        /// <summary>false (sync="false" in mod.xml): what this row changes is not carried to the other side by the field maps.</summary>
        public bool Sync = true;
        public int Line;
        /// <summary>Set by the group planner: one of many rows with the same key (shop items share item ids, drop rows repeat); no duplicate-row check.</summary>
        internal bool Grouped;
        /// <summary>Set by the group planner: the indent Xml was cut out with (null = the indent of its lines).</summary>
        internal string FromIndent;
        /// <summary>Set by the group planner: a row added to a group that is already there; it brings no new id.</summary>
        internal bool NoNewId;
    }

    public sealed class EditOp : ModOp
    {
        public string Entry;
        public readonly List<RowOp> Rows = new List<RowOp>();
    }

    public sealed partial class Mod
    {
        public const int FormatVersion = 1;

        public string Id;
        public string Name = "";
        public string Version = "";
        public string Description = "";
        /// <summary>The folder of the mod (for file= of entry ops). Null for a mod built in memory.</summary>
        public string Dir;
        public readonly List<IdRange> Ids = new List<IdRange>();
        public readonly List<ModOp> Ops = new List<ModOp>();
        /// <summary>Shops and drop boxes: row operations on "one id, many rows" groups (Groups.cs).</summary>
        public readonly List<GroupOp> Groups = new List<GroupOp>();

        /// <summary>The mod changes nothing: no entry, edit, shop or drop box op.</summary>
        public bool IsEmpty { get { return Ops.Count == 0 && Groups.Count == 0 && Skills.Count == 0 && JobSkills.Count == 0; } }

        // ---- load ----

        /// <summary>Loads a mod from its folder or from the path of its mod.xml.</summary>
        public static Mod Load(string path)
        {
            string file = Directory.Exists(path) ? Path.Combine(path, "mod.xml") : path;
            if (!File.Exists(file)) throw new ModKitException("no mod manifest at " + file);
            Mod m = Parse(File.ReadAllBytes(file), file);
            m.Dir = Path.GetDirectoryName(Path.GetFullPath(file));
            return m;
        }

        public static Mod Parse(byte[] bytes, string label)
        {
            XmlDoc doc = XmlDoc.Parse(bytes, label);
            var r = new Reader { Label = label };
            return r.ReadMod(doc.Root);
        }

        public static Mod Parse(string text, string label) { return Parse(new UTF8Encoding(false).GetBytes(text), label); }

        sealed partial class Reader
        {
            internal string Label;

            internal ModKitException Bad(XmlElem e, string what) { return new ModKitException(Label + " line " + e.Line + ": " + what); }

            void Only(XmlElem e, params string[] names)
            {
                foreach (XmlAttr a in e.Attrs)
                    if (Array.IndexOf(names, a.Name) < 0)
                        throw Bad(e, "<" + e.Name + "> has no attribute \"" + a.Name + "\" (known: " + string.Join(", ", names) + ")");
            }

            string Need(XmlElem e, string name)
            {
                string v = e.Attr(name);
                if (string.IsNullOrEmpty(v)) throw Bad(e, "<" + e.Name + "> needs the attribute \"" + name + "\"");
                return v;
            }

            long Num(XmlElem e, string name)
            {
                long v;
                if (!long.TryParse(Need(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) || v < 0)
                    throw Bad(e, name + "=\"" + e.Attr(name) + "\" is not a number");
                return v;
            }

            bool Flag(XmlElem e, string name, bool dflt)
            {
                string v = e.Attr(name);
                if (v == null) return dflt;
                if (v == "true" || v == "1") return true;
                if (v == "false" || v == "0") return false;
                throw Bad(e, name + "=\"" + v + "\" is neither true nor false");
            }

            string ArchiveRef(XmlElem e)
            {
                string a = Need(e, "archive");
                if (a.IndexOf(':') <= 0) throw Bad(e, "archive=\"" + a + "\" must be <root>:<path>, for example client:Xml.m2d");
                return DataSet.NormalizeRef(a);
            }

            internal Mod ReadMod(XmlElem root)
            {
                if (root.Name != "mod") throw Bad(root, "the root element is <" + root.Name + ">, expected <mod>");
                Only(root, "id", "name", "version", "modkit");
                var m = new Mod { Id = Need(root, "id"), Name = root.Attr("name") ?? "", Version = root.Attr("version") ?? "" };
                foreach (char c in m.Id)
                    if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_'))
                        throw Bad(root, "mod id \"" + m.Id + "\" may hold letters, digits, '.', '-' and '_' only");
                string fv = root.Attr("modkit");
                if (fv != null && fv != "1") throw Bad(root, "this mod is written for manifest format " + fv + "; this modkit reads format " + FormatVersion);
                foreach (XmlElem e in root.Children)
                {
                    switch (e.Name)
                    {
                        case "description": m.Description = (e.InnerText ?? "").Trim(); break;
                        case "ids": m.Ids.Add(ReadIds(e)); break;
                        case "entry": m.Ops.Add(ReadEntry(e)); break;
                        case "edit": m.Ops.Add(ReadEdit(e)); break;
                        case "shop": m.Groups.Add(ReadGroup(e, GroupKind.Shop)); break;
                        case "dropbox": m.Groups.Add(ReadGroup(e, GroupKind.DropBox)); break;
                        case "skill": m.Skills.Add(ReadSkill(e, SkillKind.Skill)); break;
                        case "effect": m.Skills.Add(ReadSkill(e, SkillKind.Effect)); break;
                        case "jobskill": m.JobSkills.Add(ReadJobSkill(e)); break;
                        default: throw Bad(e, "unknown element <" + e.Name + "> in <mod> (known: description, ids, entry, edit, shop, dropbox, skill, effect, jobskill)");
                    }
                }
                return m;
            }

            IdRange ReadIds(XmlElem e)
            {
                Only(e, "space", "from", "to", "strict");
                var r = new IdRange { Space = Need(e, "space"), From = Num(e, "from"), To = Num(e, "to"), Strict = Flag(e, "strict", true), Line = e.Line };
                if (r.To < r.From) throw Bad(e, "to is below from");
                foreach (XmlElem w in e.Children)
                {
                    if (w.Name != "where") throw Bad(w, "unknown element <" + w.Name + "> in <ids> (known: where)");
                    Only(w, "archive", "entry", "rows", "key");
                    var p = new IdPlace { Archive = ArchiveRef(w), Entry = Need(w, "entry").Replace('\\', '/'), Rows = w.Attr("rows"), Key = w.Attr("key") ?? "id" };
                    if (p.Rows == null && p.Entry.IndexOf("{id}", StringComparison.Ordinal) < 0)
                        throw Bad(w, "<where> needs {id} in entry=\"...\" or a rows=\"...\" selector");
                    if (p.Rows != null) RowPath.Parse(p.Rows);
                    r.Places.Add(p);
                }
                return r;
            }

            EntryOp ReadEntry(XmlElem e)
            {
                Only(e, "archive", "name", "copy", "file", "delete");
                var op = new EntryOp { Archive = ArchiveRef(e), Name = Need(e, "name").Replace('\\', '/'), Copy = e.Attr("copy"), File = e.Attr("file"), Delete = Flag(e, "delete", false), Line = e.Line };
                int n = (op.Copy != null ? 1 : 0) + (op.File != null ? 1 : 0) + (op.Delete ? 1 : 0);
                if (n != 1) throw Bad(e, "<entry> needs exactly one of copy, file, delete=\"true\"");
                if (op.Copy != null) op.Copy = op.Copy.Replace('\\', '/');
                if (e.Children.Count > 0) throw Bad(e, "<entry> has no child elements; use <edit> to change rows");
                return op;
            }

            EditOp ReadEdit(XmlElem e)
            {
                Only(e, "archive", "entry", "sync");
                var op = new EditOp { Archive = ArchiveRef(e), Entry = Need(e, "entry").Replace('\\', '/'), Line = e.Line };
                bool sync = Flag(e, "sync", true);
                foreach (XmlElem c in e.Children)
                {
                    var r = new RowOp { Line = c.Line, Sync = Flag(c, "sync", sync) };
                    switch (c.Name)
                    {
                        case "change":
                            Only(c, "at", "sync");
                            r.Kind = RowOpKind.Change; r.At = c.Attr("at") ?? "";
                            ReadAttrOps(c, r, false);
                            if (r.Attrs.Count == 0) throw Bad(c, "<change> without <set>, <unset> or <text>");
                            break;
                        case "delete":
                            Only(c, "at");
                            r.Kind = RowOpKind.Delete; r.At = Need(c, "at");
                            if (c.Children.Count > 0) throw Bad(c, "<delete> has no child elements");
                            break;
                        case "add":
                            Only(c, "copy", "after", "before", "in", "sync");
                            r.Kind = RowOpKind.Add; r.Copy = c.Attr("copy"); r.After = c.Attr("after"); r.Before = c.Attr("before"); r.In = c.Attr("in");
                            if ((r.After != null ? 1 : 0) + (r.Before != null ? 1 : 0) + (r.In != null ? 1 : 0) > 1) throw Bad(c, "<add> takes at most one of after, before, in");
                            ReadAttrOps(c, r, true);
                            if ((r.Copy != null) == (r.Xml != null)) throw Bad(c, "<add> needs either copy=\"selector\" or an <xml> child with the new element");
                            break;
                        default: throw Bad(c, "unknown element <" + c.Name + "> in <edit> (known: change, add, delete)");
                    }
                    foreach (string s in new[] { r.At, r.Copy, r.After, r.Before, r.In })
                        if (s != null) try { RowPath.Parse(s); } catch (ModKitException x) { throw Bad(c, x.Message); }
                    op.Rows.Add(r);
                }
                if (op.Rows.Count == 0) throw Bad(e, "<edit> without <change>, <add> or <delete>");
                return op;
            }

            void ReadAttrOps(XmlElem c, RowOp r, bool allowXml)
            {
                foreach (XmlElem s in c.Children)
                {
                    var a = new AttrOp { Line = s.Line };
                    switch (s.Name)
                    {
                        case "set": Only(s, "name", "value", "at", "sync"); a.Sync = Flag(s, "sync", true); a.Kind = AttrOpKind.Set; a.Name = Need(s, "name"); a.Value = s.Attr("value"); if (a.Value == null) throw Bad(s, "<set> needs value=\"...\" (it may be empty)"); break;
                        case "unset": Only(s, "name", "at", "sync"); a.Sync = Flag(s, "sync", true); a.Kind = AttrOpKind.Unset; a.Name = Need(s, "name"); break;
                        case "text": Only(s, "value", "at"); a.Kind = AttrOpKind.Text; a.Value = s.Attr("value"); if (a.Value == null) throw Bad(s, "<text> needs value=\"...\""); break;
                        case "xml":
                            if (!allowXml) throw Bad(s, "<xml> belongs in <add>");
                            if (r.Xml != null) throw Bad(s, "a second <xml> in one <add>");
                            if (s.Children.Count > 0)
                            {
                                if (s.Children.Count != 1) throw Bad(s, "<xml> holds exactly one element");
                                r.Xml = s.Children[0].OuterText;
                            }
                            else r.Xml = (s.InnerText ?? "").Trim();
                            if (r.Xml.Length == 0) throw Bad(s, "<xml> is empty");
                            continue;
                        default: throw Bad(s, "unknown element <" + s.Name + "> in <" + c.Name + ">");
                    }
                    a.At = s.Attr("at");
                    if (a.At != null) try { RowPath.Parse(a.At); } catch (ModKitException x) { throw Bad(s, x.Message); }
                    r.Attrs.Add(a);
                }
            }
        }

        // ---- save ----

        static void A(StringBuilder sb, string name, string value)
        {
            if (value != null) sb.Append(' ').Append(name).Append("=\"").Append(XmlDoc.EscapeAttr(value, '"')).Append('"');
        }

        /// <summary>The manifest text (UTF-8, LF). Load of the result gives the same mod.</summary>
        public string ToXml()
        {
            var sb = new StringBuilder();
            sb.Append("<mod"); A(sb, "id", Id); A(sb, "name", Name); A(sb, "version", Version); A(sb, "modkit", "1"); sb.Append(">\n");
            if (!string.IsNullOrEmpty(Description)) sb.Append("  <description>").Append(XmlDoc.EscapeText(Description)).Append("</description>\n");
            foreach (IdRange r in Ids)
            {
                sb.Append("  <ids"); A(sb, "space", r.Space); A(sb, "from", r.From.ToString(CultureInfo.InvariantCulture)); A(sb, "to", r.To.ToString(CultureInfo.InvariantCulture));
                if (!r.Strict) A(sb, "strict", "false");
                sb.Append(">\n");
                foreach (IdPlace p in r.Places)
                {
                    sb.Append("    <where"); A(sb, "archive", p.Archive); A(sb, "entry", p.Entry); A(sb, "rows", p.Rows);
                    if (p.Rows != null) A(sb, "key", p.Key);
                    sb.Append(" />\n");
                }
                sb.Append("  </ids>\n");
            }
            foreach (ModOp op in Ops)
            {
                var en = op as EntryOp;
                if (en != null)
                {
                    sb.Append("  <entry"); A(sb, "archive", en.Archive); A(sb, "name", en.Name); A(sb, "copy", en.Copy); A(sb, "file", en.File);
                    if (en.Delete) A(sb, "delete", "true");
                    sb.Append(" />\n");
                    continue;
                }
                var ed = (EditOp)op;
                sb.Append("  <edit"); A(sb, "archive", ed.Archive); A(sb, "entry", ed.Entry); sb.Append(">\n");
                foreach (RowOp r in ed.Rows)
                {
                    if (r.Kind == RowOpKind.Delete) { sb.Append("    <delete"); A(sb, "at", r.At); sb.Append(" />\n"); continue; }
                    string tag = r.Kind == RowOpKind.Change ? "change" : "add";
                    sb.Append("    <").Append(tag);
                    if (r.Kind == RowOpKind.Change) A(sb, "at", r.At);
                    else { A(sb, "copy", r.Copy); A(sb, "after", r.After); A(sb, "before", r.Before); A(sb, "in", r.In); }
                    if (!r.Sync) A(sb, "sync", "false");
                    sb.Append(">\n");
                    if (r.Xml != null)
                    {
                        if (r.Xml.IndexOf("]]>", StringComparison.Ordinal) >= 0) throw new ModKitException("mod " + Id + ": literal XML with \"]]>\" cannot be saved");
                        sb.Append("      <xml><![CDATA[").Append(r.Xml).Append("]]></xml>\n");
                    }
                    foreach (AttrOp a in r.Attrs)
                    {
                        sb.Append("      <").Append(a.Kind == AttrOpKind.Set ? "set" : a.Kind == AttrOpKind.Unset ? "unset" : "text");
                        A(sb, "at", a.At);
                        if (a.Kind != AttrOpKind.Text) A(sb, "name", a.Name);
                        if (a.Kind != AttrOpKind.Unset) A(sb, "value", a.Value);
                        if (!a.Sync && a.Kind != AttrOpKind.Text) A(sb, "sync", "false");
                        sb.Append(" />\n");
                    }
                    sb.Append("    </").Append(tag).Append(">\n");
                }
                sb.Append("  </edit>\n");
            }
            foreach (GroupOp g in Groups) WriteGroup(sb, g);
            WriteSkills(sb);
            sb.Append("</mod>\n");
            return sb.ToString();
        }

        public void Save(string dir)
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "mod.xml"), new UTF8Encoding(false).GetBytes(ToXml()));
            Dir = Path.GetFullPath(dir);
        }
    }
}
