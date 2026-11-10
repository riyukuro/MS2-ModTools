// An entity editor's model: one id of an entity (an item, an npc) seen across the files that make it up on every
// target of the data set, with every edit kept as the mod.xml operation it will become (the same operations the
// table editor writes). Nothing is written to any archive here.
//
// The targets keep an entity in one of two layouts (Layout.cs, Profile.cs):
//   bundle          kms-client  <entity>data/NNN.xml, one <row id> per id; NNN is the id divided by 100000
//   one file per id gms-client, server  <entity>/<a>/<b>/<id>.xml, the entity's element is the file's root
// and every target has string/<lang>/<entity>name.xml (and further string files) with <key id ...> rows.
//
// The editor works on whatever targets the data set has:
//   the bundle side     the kms-client archive, when the data set has one
//   the per-id side     the server archive, else the gms-client archive. The gms-client is not edited when the
//                       server is there: the plan mirrors every server line to it (PlannerMirror.cs).
// An edit is written once, on the side it is made on; the plan carries a mapped field to the other side (the field
// map, PlannerSync.cs) and to the gms-client (the mirror). Only rows no map carries are written on each side by the
// editor itself: the string rows (names) and, for a clone, the new row and the new file.
//
// A part is one row (or file); a section is one element of a part that has attributes; a field is an attribute.
// A cloned entity is a set of <add copy> rows (and an <entry copy> on the per-id side) with the new id.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public sealed class EntityHit
    {
        public long Id;
        public string Name = "";
        /// <summary>True for an id the mod adds.</summary>
        public bool Added;

        public override string ToString() { return Id.ToString(CultureInfo.InvariantCulture) + "  " + Name + (Added ? "  (new)" : ""); }
    }

    public sealed class EntityPart
    {
        /// <summary>
        /// The entity's name ("item", "npc") for its bundle row, "file" for its file of the per-id side, the kind of a
        /// string file ("name", "plural", "description", "title"), or "option" (an item's stat row).
        /// </summary>
        public string Kind;
        public string Label;
        public string Archive, Entry;
        /// <summary>The language folder of a string part, else null.</summary>
        public string Language;
        /// <summary>The element unedited values come from (for a row the mod adds: the row it copies).</summary>
        public XmlElem Root;
        /// <summary>True when other ids use this row too (a stat row of the item option table).</summary>
        public bool Shared;
        /// <summary>True when the mod adds this row or file.</summary>
        public bool IsNew;
        internal RowOp Add;

        /// <summary>The target of the part: "client" (kms-client), "gms-client" or "server".</summary>
        public string Target { get { return DataSet.RootOf(Archive); } }
    }

    public sealed class EntitySection
    {
        public EntityPart Part;
        public XmlElem Elem;
        /// <summary>Selector of the element under the part's row; "" for the row itself.</summary>
        public string Path;
        public string Title;
        public readonly List<string> Fields = new List<string>();
        internal string Chain;
    }

    public class EntityDraft
    {
        public readonly EntityEditor Editor;
        public readonly long Id;
        public readonly List<EntityPart> Parts = new List<EntityPart>();
        public readonly List<EntitySection> Sections = new List<EntitySection>();

        internal EntityDraft(EntityEditor editor, long id) { Editor = editor; Id = id; }

        bool IsRow(EntityPart p) { return p.Kind == Editor.Entity || p.Kind == EntityEditor.FileKind; }

        /// <summary>True when the mod adds this id.</summary>
        public bool IsNew { get { foreach (EntityPart p in Parts) if (IsRow(p)) return p.IsNew; return false; } }

        // ---- fields ----

        static bool IsField(AttrOp a, string path, string field)
        {
            return a.Kind != AttrOpKind.Text && a.Name == field && (a.At ?? "") == path;
        }

        /// <summary>The value the field will have (null: the element has no such attribute).</summary>
        public string Get(EntitySection s, string field)
        {
            RowOp op = s.Part.Add ?? Editor.FindChange(s.Part, s.Elem, false);
            string path = s.Part.Add != null ? s.Path : "";
            if (op != null)
                for (int i = op.Attrs.Count - 1; i >= 0; i--)
                    if (IsField(op.Attrs[i], path, field)) return op.Attrs[i].Kind == AttrOpKind.Set ? op.Attrs[i].Value : null;
            return s.Elem.Attr(field);
        }

        /// <summary>The value in the original data (for a new id: of the id it copies).</summary>
        public string GetOriginal(EntitySection s, string field) { return s.Elem.Attr(field); }

        public bool IsEdited(EntitySection s, string field) { return Get(s, field) != s.Elem.Attr(field); }

        /// <summary>Sets a field on this section only; null removes the attribute. Setting the original value back drops the edit.</summary>
        public void Set(EntitySection s, string field, string value)
        {
            if (!TableDraft.IsAttrName(field)) throw new ModKitException("\"" + field + "\" is not an XML attribute name");
            if (s.Part.Add != null && s.Path.Length == 0 && field == Editor.KeyOf(s.Part) && value != Get(s, field))
                throw new ModKitException("the " + field + " of a new row is what makes it this " + Editor.Entity + "; clone again for another id");
            bool added = s.Part.Add != null;
            RowOp op = added ? s.Part.Add : Editor.FindChange(s.Part, s.Elem, false);
            string path = added ? s.Path : "";
            if (op != null) op.Attrs.RemoveAll(a => IsField(a, path, field));
            if (value != s.Elem.Attr(field))
            {
                if (op == null) op = Editor.FindChange(s.Part, s.Elem, true);
                op.Attrs.Add(new AttrOp { Kind = value == null ? AttrOpKind.Unset : AttrOpKind.Set, Name = field, Value = value, At = path.Length == 0 ? null : path });
                if (!s.Fields.Contains(field)) s.Fields.Add(field);
            }
            else if (op != null && !added && op.Attrs.Count == 0) Editor.DropChange(s.Part, op);
        }

        /// <summary>
        /// Sets a field of the bundle row or the per-id file. The edit is written on this side only, the same way a
        /// mod file has it: on Plan, the field map carries a mapped field to the other side when the two sides
        /// corresponded before, and the mirror carries a server edit to the gms-client. Without sync, a mapped field
        /// is marked sync="false", so Plan leaves the other side alone and says so. Returns the section of the other
        /// side that Plan will change too, or null.
        /// </summary>
        public EntitySection Set(EntitySection s, string field, string value, bool sync)
        {
            string before = Get(s, field);
            Set(s, field, value);
            string otherField;
            FieldMapEntry e;
            EntitySection m = Mirror(s, field, out otherField, out e);
            if (!sync)
            {
                if (m != null) MarkOneSided(s, field);
                return null;
            }
            if (m == null) return null;
            string otherNow = Get(m, otherField);
            if (e == null) return otherNow == before ? m : null;
            bool client = s.Part.Kind == Editor.Entity;
            string index = e.Index == null ? null : ServerValue(client ? m : s, e.Index);
            if (!(client ? e.Corresponds(before, otherNow, index) : e.Corresponds(otherNow, before, index))) return null;
            string conv, why;
            if (!(client ? e.TryToServer(value, otherNow, index, out conv, out why) : e.TryToClient(value, otherNow, index, out conv, out why))) return null;
            return m;
        }

        /// <summary>
        /// Sets a field on both sides: the bundle row's field (as the field map's client side names it, for example
        /// "@level" or "stat@hp") and the field of the per-id file the map pairs with it, converted as the map says.
        /// For a field the plan would not carry (the shipped sides differ) or a new id. Returns the number of sections set.
        /// </summary>
        public int SetBoth(string clientField, string value)
        {
            EntityMap em = Editor.Maps == null ? null : Editor.Maps.Entity(Editor.Entity);
            FieldMapEntry e = null;
            if (em != null && em.Classify(true, clientField, out e) != FieldKind.Mapped) e = null;
            int n = 0;
            foreach (EntitySection s in Sections.ToArray())
            {
                if (!s.Chain.StartsWith("environment[", StringComparison.Ordinal)) continue;
                bool client = s.Part.Kind == Editor.Entity;
                if (!client && s.Part.Kind != EntityEditor.FileKind) continue;
                string want = client ? clientField : e != null ? e.Server : null;
                if (want == null) continue;
                if (BelowEnv(s.Chain) != FieldMapEntry.ElemOf(want)) continue;
                string attr = FieldMapEntry.AttrOf(want), v = value;
                if (!client && value != null)
                {
                    string why;
                    if (!e.TryToServer(value, Get(s, attr), e.Index == null ? null : ServerValue(s, e.Index), out v, out why))
                        throw new ModKitException(clientField + " = \"" + value + "\" has no " + want + " on the " + s.Part.Target + ": " + why);
                }
                Set(s, attr, v);
                n++;
            }
            return n;
        }

        /// <summary>Sets a field of the per-id file only (a field the bundle side does not have, as "basic@difficulty"). Returns the number of sections set.</summary>
        public int SetOnFile(string field, string value)
        {
            int n = 0;
            foreach (EntitySection s in Sections.ToArray())
            {
                if (s.Part.Kind != EntityEditor.FileKind || !s.Chain.StartsWith("environment[", StringComparison.Ordinal)) continue;
                if (BelowEnv(s.Chain) != FieldMapEntry.ElemOf(field)) continue;
                Set(s, FieldMapEntry.AttrOf(field), value);
                n++;
            }
            return n;
        }

        /// <summary>The value of a field of the bundle row (as the map's client side names it), else of the per-id file's mapped field.</summary>
        public string GetField(string clientField)
        {
            EntityMap em = Editor.Maps == null ? null : Editor.Maps.Entity(Editor.Entity);
            FieldMapEntry e = null;
            if (em != null && em.Classify(true, clientField, out e) != FieldKind.Mapped) e = null;
            string fileValue = null;
            foreach (EntitySection s in Sections)
            {
                if (!s.Chain.StartsWith("environment[", StringComparison.Ordinal)) continue;
                if (s.Part.Kind == Editor.Entity && BelowEnv(s.Chain) == FieldMapEntry.ElemOf(clientField)) return Get(s, FieldMapEntry.AttrOf(clientField));
                if (fileValue == null && e != null && s.Part.Kind == EntityEditor.FileKind && BelowEnv(s.Chain) == FieldMapEntry.ElemOf(e.Server))
                {
                    string v = Get(s, FieldMapEntry.AttrOf(e.Server)), c, why;
                    fileValue = v != null && e.TryToClient(v, null, null, out c, out why) ? c : v;
                }
            }
            return fileValue;
        }

        void MarkOneSided(EntitySection s, string field)
        {
            RowOp op = s.Part.Add ?? Editor.FindChange(s.Part, s.Elem, false);
            string path = s.Part.Add != null ? s.Path : "";
            if (op == null) return;
            foreach (AttrOp a in op.Attrs) if (IsField(a, path, field)) a.Sync = false;
        }

        internal static string EnvLink(string chain) { int i = chain.IndexOf('/'); return i < 0 ? chain : chain.Substring(0, i); }
        internal static string BelowEnv(string chain) { int i = chain.IndexOf('/'); return i < 0 ? "" : chain.Substring(i + 1); }

        // the value of a per-id field ("element@attribute" under the environment) beside a per-id section
        string ServerValue(EntitySection server, string field)
        {
            string want = EnvLink(server.Chain) + "/" + FieldMapEntry.ElemOf(field);
            if (FieldMapEntry.ElemOf(field).Length == 0) want = EnvLink(server.Chain);
            foreach (EntitySection t in Sections)
                if (t.Part == server.Part && t.Chain == want) return Get(t, FieldMapEntry.AttrOf(field));
            return null;
        }

        /// <summary>The section of the other side that holds the corresponding field (see Set), or null.</summary>
        public EntitySection Mirror(EntitySection s, string field)
        {
            string f; FieldMapEntry e;
            return Mirror(s, field, out f, out e);
        }

        /// <summary>As Mirror; otherField is the attribute there, entry the map line (null: no map, same name).</summary>
        public EntitySection Mirror(EntitySection s, string field, out string otherField, out FieldMapEntry entry)
        {
            otherField = field; entry = null;
            string other = s.Part.Kind == Editor.Entity ? EntityEditor.FileKind : s.Part.Kind == EntityEditor.FileKind ? Editor.Entity : null;
            if (other == null) return null;
            if (!s.Chain.StartsWith("environment[", StringComparison.Ordinal)) return null;
            string env = EnvLink(s.Chain), below = BelowEnv(s.Chain), otherBelow = below;
            EntityMap em = Editor.Maps == null ? null : Editor.Maps.Entity(Editor.Entity);
            if (em != null)
            {
                if (em.Classify(other == EntityEditor.FileKind, (below.Length == 0 ? "" : below) + "@" + field, out entry) != FieldKind.Mapped) { entry = null; return null; }
                string of = other == EntityEditor.FileKind ? entry.Server : entry.Client;
                otherBelow = FieldMapEntry.ElemOf(of);
                otherField = FieldMapEntry.AttrOf(of);
            }
            // the environment of the other side: the one of the same feature and locale, or the only one of each side
            var mine = new List<string>(); var theirs = new List<string>();
            foreach (EntitySection t in Sections)
            {
                if (!t.Chain.StartsWith("environment[", StringComparison.Ordinal)) continue;
                string link = EnvLink(t.Chain);
                if (t.Part == s.Part) { if (!mine.Contains(link)) mine.Add(link); }
                else if (t.Part.Kind == other && !theirs.Contains(link)) theirs.Add(link);
            }
            string theirEnv = env;
            if (!theirs.Contains(env) && mine.Count == 1 && theirs.Count == 1) theirEnv = theirs[0];
            string want = otherBelow.Length == 0 ? theirEnv : theirEnv + "/" + otherBelow;
            EntitySection hit = null;
            foreach (EntitySection t in Sections)
            {
                if (t.Part.Kind != other || t.Chain != want) continue;
                if (hit != null) return null;       // two elements of that path: no telling which
                hit = t;
            }
            if (hit == null) return null;
            if (em == null && hit.Elem.Attr(field) == null) return null;
            foreach (EntitySection t in Sections)
                if (t != s && t.Part == s.Part && t.Chain == s.Chain) return null;
            return hit;
        }

        // ---- string rows ----

        internal EntitySection RootSection(EntityPart p)
        {
            foreach (EntitySection s in Sections) if (s.Part == p && s.Path.Length == 0) return s;
            return null;
        }

        /// <summary>The value of a string field in the editor's language, from the archive names are searched in first.</summary>
        protected string First(string kind, string field)
        {
            EntityPart best = null;
            int bestScore = -1;
            foreach (EntityPart p in Parts)
            {
                if (p.Kind != kind) continue;
                int score = (p.Language == Editor.Language ? 2 : 0) + (p.Archive == Editor.NameXml ? 1 : 0);
                if (score > bestScore) { best = p; bestScore = score; }
            }
            if (best == null) return null;
            EntitySection s = RootSection(best);
            return s == null ? null : Get(s, field);
        }

        /// <summary>The string parts of a kind ("name"), on every target the editor writes.</summary>
        public List<EntityPart> StringParts(string kind)
        {
            var list = new List<EntityPart>();
            foreach (EntityPart p in Parts) if (p.Kind == kind) list.Add(p);
            return list;
        }

        /// <summary>A string field of one part (see StringParts).</summary>
        public string GetText(EntityPart p, string field)
        {
            EntitySection s = RootSection(p);
            return s == null ? null : Get(s, field);
        }

        /// <summary>Sets a string field of one part (one language of one target).</summary>
        public void SetText(EntityPart p, string field, string value)
        {
            if (value == null) throw new ArgumentNullException("value");
            EntitySection s = RootSection(p);
            if (s == null) throw new ModKitException(p.Label + " has no row");
            Set(s, field, value);
        }

        protected int SetAll(string kind, string field, string value)
        {
            int n = 0;
            foreach (EntityPart p in Parts)
            {
                if (p.Kind != kind) continue;
                EntitySection s = RootSection(p);
                if (s == null) continue;
                Set(s, field, value);
                n++;
            }
            return n;
        }

        /// <summary>The name in the editor's language (null: the id has no name row).</summary>
        public string Name { get { return First("name", "name"); } }

        /// <summary>Sets the name in every language file of every target that has a row for the id. Returns the number of rows.</summary>
        public int SetName(string value)
        {
            if (value == null) throw new ArgumentNullException("value");
            return SetAll("name", "name", value);
        }

        /// <summary>Sets the name in one language on every target that has a row for it. Returns the number of rows.</summary>
        public int SetName(string language, string value)
        {
            if (value == null) throw new ArgumentNullException("value");
            int n = 0;
            foreach (EntityPart p in StringParts("name"))
                if (string.Equals(p.Language, language, StringComparison.OrdinalIgnoreCase)) { SetText(p, "name", value); n++; }
            return n;
        }
    }

    public abstract class EntityEditor
    {
        public const string ClientXml = "client:Xml.m2d", GmsXml = "gms-client:Xml.m2d", ServerXml = "server:Xml.m2d";
        /// <summary>The part kind of the per-id file.</summary>
        public const string FileKind = "file";

        /// <summary>The entity and its id space: "item", "npc".</summary>
        public readonly string Entity;
        public readonly DataSet Data;
        public readonly Mod Mod;
        /// <summary>The field maps the sync of EntityDraft.Set follows; null: fields of the same name only.</summary>
        public FieldMaps Maps = FieldMaps.Default;
        /// <summary>The bundle side (the kms-client's Xml.m2d), or null when the data set has none.</summary>
        public readonly string BundleXml;
        /// <summary>The per-id side (the server's Xml.m2d, else the gms-client's), or null when the data set has neither.</summary>
        public readonly string FileXml;
        /// <summary>The archive names are searched and shown from: the bundle side, else the per-id side.</summary>
        public readonly string NameXml;
        /// <summary>The language folders of NameXml that hold a name file of the entity.</summary>
        public readonly List<string> Languages;
        /// <summary>The language names are searched and shown in. Edits of a name go to every language.</summary>
        public string Language;

        readonly EntityLayout bundle, files;

        sealed class DocInfo
        {
            internal XmlDoc Doc;
            internal string Name;
            internal readonly Dictionary<string, Dictionary<string, List<XmlElem>>> Index = new Dictionary<string, Dictionary<string, List<XmlElem>>>();
            internal readonly Dictionary<string, Dictionary<long, List<XmlElem>>> Numbers = new Dictionary<string, Dictionary<long, List<XmlElem>>>();

            internal List<XmlElem> Rows(string key, string value)
            {
                Dictionary<string, List<XmlElem>> ix;
                if (!Index.TryGetValue(key, out ix))
                {
                    Index[key] = ix = new Dictionary<string, List<XmlElem>>(StringComparer.Ordinal);
                    foreach (XmlElem c in Doc.Root.Children)
                    {
                        string v = c.Attr(key);
                        if (v == null) continue;
                        List<XmlElem> l;
                        if (!ix.TryGetValue(v, out l)) ix[v] = l = new List<XmlElem>();
                        l.Add(c);
                    }
                }
                List<XmlElem> hit;
                return ix.TryGetValue(value, out hit) ? hit : new List<XmlElem>();
            }

            // the rows whose key is the number (string files write "02040998" for npc 2040998)
            internal List<XmlElem> RowsOf(string key, long id)
            {
                Dictionary<long, List<XmlElem>> ix;
                if (!Numbers.TryGetValue(key, out ix))
                {
                    Numbers[key] = ix = new Dictionary<long, List<XmlElem>>();
                    foreach (XmlElem c in Doc.Root.Children)
                    {
                        long v;
                        if (!TryId(c.Attr(key), out v)) continue;
                        List<XmlElem> l;
                        if (!ix.TryGetValue(v, out l)) ix[v] = l = new List<XmlElem>();
                        l.Add(c);
                    }
                }
                List<XmlElem> hit;
                return ix.TryGetValue(id, out hit) ? hit : new List<XmlElem>();
            }
        }

        /// <param name="stringFiles">The string files of the entity under string/&lt;lang&gt;/, the name file first.</param>
        /// <param name="stringKinds">The part kind of each string file ("name", ...).</param>
        protected EntityEditor(string entity, DataSet data, Mod mod, string[] stringFiles, string[] stringKinds)
        {
            if (data == null) throw new ArgumentNullException("data");
            if (mod == null) throw new ArgumentNullException("mod");
            Entity = entity; Data = data; Mod = mod; StringFiles = stringFiles; StringKinds = stringKinds;
            foreach (Profile p in Profile.All)
            {
                string archive = p.Root + ":Xml.m2d";
                EntityLayout l = p.Layout(entity);
                if (l == null || !data.Has(p) || !data.Exists(archive)) continue;
                if (l.Bundle) { if (BundleXml == null) { BundleXml = archive; bundle = l; } }
                else if (FileXml == null || p.IsServer) { FileXml = archive; files = l; }
            }
            if (BundleXml == null && FileXml == null)
                throw new ModKitException("the " + entity + " editor needs an Xml.m2d of the kms-client, the gms-client or the server");
            NameXml = BundleXml ?? FileXml;
            Languages = LanguagesIn(NameXml);
            Language = Languages.Contains("en") ? "en" : Languages.Count > 0 ? Languages[0] : null;
        }

        internal readonly string[] StringFiles, StringKinds;

        /// <summary>True when the data set has both sides: an edit of a mapped field reaches the other one on Plan.</summary>
        public bool TwoSided { get { return BundleXml != null && FileXml != null; } }
        /// <summary>True when the per-id side is the server's archive.</summary>
        public bool HasServer { get { return FileXml == ServerXml && FileIndex().Count > 0; } }

        /// <summary>The archives the editor writes: the bundle side and the per-id side (those the data set has).</summary>
        public List<string> Sides()
        {
            var l = new List<string>();
            if (BundleXml != null) l.Add(BundleXml);
            if (FileXml != null) l.Add(FileXml);
            return l;
        }

        /// <summary>
        /// The language folders of an archive that hold the entity's name file, read from the archive; when it has
        /// none, the languages its profile reads (Profile.Languages).
        /// </summary>
        public List<string> LanguagesIn(string archive)
        {
            string key = "entitylangs\n" + archive + "\n" + StringFiles[0];
            object o;
            if (Data.Cache.TryGetValue(key, out o)) return new List<string>((List<string>)o);
            var list = new List<string>();
            var rx = new Regex("^string/([^/]+)/" + Regex.Escape(StringFiles[0]) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (Data.Exists(archive))
                foreach (M2dEntry e in Data.Open(archive).Entries)
                {
                    Match m = rx.Match(e.Name);
                    if (m.Success && !list.Contains(m.Groups[1].Value)) list.Add(m.Groups[1].Value);
                }
            if (list.Count == 0)
            {
                Profile p = Profile.OfArchive(archive);
                if (p != null) list.AddRange(p.Languages);
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            Data.Cache[key] = list;
            return new List<string>(list);
        }

        // ---- the original data ----

        protected static string S(long id) { return id.ToString(CultureInfo.InvariantCulture); }

        internal static bool TryId(string s, out long id)
        {
            id = 0;
            return s != null && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out id);
        }

        /// <summary>The bundle file of an id ("itemdata/012.xml"), or null when the data set has no bundle side.</summary>
        public string BundleEntry(long id) { return bundle == null ? null : bundle.EntryOf(id); }

        /// <summary>The per-id file a new id gets ("npc/12/34/12345678.xml"), or null without a per-id side.</summary>
        public string NewFileEntry(long id) { return files == null ? null : files.EntryOf(id); }

        DocInfo Doc(string archive, string entry)
        {
            string key = "entitydoc\n" + archive + "\n" + entry.ToLowerInvariant();
            object o;
            if (Data.Cache.TryGetValue(key, out o)) return (DocInfo)o;
            DocInfo info = null;
            if (Data.Exists(archive))
            {
                M2dArchive arch = Data.Open(archive);
                M2dEntry e = arch.Find(entry);
                if (e != null)
                {
                    XmlDoc doc = XmlDoc.Parse(arch.Read(e), archive + " " + e.Name);
                    if (doc.Root != null) info = new DocInfo { Doc = doc, Name = e.Name };
                }
            }
            Data.Cache[key] = info;
            return info;
        }

        Dictionary<long, string> FileIndex()
        {
            if (FileXml == null) return new Dictionary<long, string>();
            string key = "entityfiles\n" + Entity + "\n" + FileXml;
            object o;
            if (Data.Cache.TryGetValue(key, out o)) return (Dictionary<long, string>)o;
            var ix = new Dictionary<long, string>();
            foreach (M2dEntry e in Data.Open(FileXml).Entries)
            {
                long id;
                if (files.Match(e.Name, out id)) ix[id] = e.Name;
            }
            Data.Cache[key] = ix;
            return ix;
        }

        /// <summary>The shipped per-id file of an id, or null.</summary>
        public string FileOf(long id)
        {
            string name;
            return FileIndex().TryGetValue(id, out name) ? name : null;
        }

        static string StringEntry(string lang, string file) { return "string/" + lang + "/" + file; }

        // ---- the mod's operations ----

        static bool SameEntry(string a, string b) { return string.Equals(a.Replace('\\', '/'), b, StringComparison.OrdinalIgnoreCase); }

        IEnumerable<EditOp> Edits(string archive, string entry)
        {
            foreach (ModOp op in Mod.Ops.ToArray())
            {
                var ed = op as EditOp;
                if (ed != null && ed.Archive == archive && SameEntry(ed.Entry, entry)) yield return ed;
            }
        }

        internal EditOp Edit(string archive, string entry)
        {
            foreach (EditOp ed in Edits(archive, entry)) return ed;
            var made = new EditOp { Archive = archive, Entry = entry };
            Mod.Ops.Add(made);
            return made;
        }

        internal static string KeyValue(RowOp add, string key)
        {
            for (int i = add.Attrs.Count - 1; i >= 0; i--)
            {
                AttrOp a = add.Attrs[i];
                if (a.Kind == AttrOpKind.Set && string.IsNullOrEmpty(a.At) && a.Name == key) return a.Value;
            }
            return null;
        }

        static string AddKey(RowOp r, string key)
        {
            string v = KeyValue(r, key);
            if (v == null && r.Xml != null)
            {
                XmlElem lit = Literal(r);
                v = lit == null ? null : lit.Attr(key);
            }
            return v;
        }

        // the rows the mod adds to an entry under a key value: <add ...><set name="id" value="..."/>, or a literal row
        internal List<RowOp> Adds(string archive, string entry, string key, string value)
        {
            var list = new List<RowOp>();
            foreach (EditOp ed in Edits(archive, entry))
                foreach (RowOp r in ed.Rows)
                    if (r.Kind == RowOpKind.Add && AddKey(r, key) == value) list.Add(r);
            return list;
        }

        // the same for a number key (string files may pad the id with zeros)
        List<RowOp> AddsOf(string archive, string entry, string key, long id)
        {
            var list = new List<RowOp>();
            foreach (EditOp ed in Edits(archive, entry))
                foreach (RowOp r in ed.Rows)
                {
                    long v;
                    if (r.Kind == RowOpKind.Add && TryId(AddKey(r, key), out v) && v == id) list.Add(r);
                }
            return list;
        }

        static XmlElem Literal(RowOp r)
        {
            try
            {
                XmlDoc f = XmlDoc.ParseFragment(r.Xml.Trim(), "the new row");
                return f.Top.Count == 1 ? f.Root : null;
            }
            catch (ModKitException) { return null; }
        }

        static XmlElem BaseOf(DocInfo doc, RowOp add)
        {
            if (add.Copy == null) return Literal(add);
            string err;
            return RowPath.Parse(add.Copy).TryResolve(doc.Doc.Root, out err);
        }

        EntryOp FileAdd(long id)
        {
            if (FileXml == null) return null;
            foreach (ModOp op in Mod.Ops)
            {
                var en = op as EntryOp;
                if (en == null || en.Archive != FileXml || en.Delete || en.Copy == null) continue;
                long v;
                if (files.Match(en.Name.Replace('\\', '/'), out v) && v == id) return en;
            }
            return null;
        }

        internal RowOp FindChange(EntityPart part, XmlElem elem, bool create)
        {
            string at = RowPath.Canonical(elem);
            foreach (EditOp ed in Edits(part.Archive, part.Entry))
                foreach (RowOp r in ed.Rows)
                    if (r.Kind == RowOpKind.Change && (r.At ?? "") == at) return r;
            if (!create) return null;
            var op = new RowOp { Kind = RowOpKind.Change, At = at };
            Edit(part.Archive, part.Entry).Rows.Add(op);
            return op;
        }

        internal void DropChange(EntityPart part, RowOp op)
        {
            foreach (EditOp ed in Edits(part.Archive, part.Entry))
                if (ed.Rows.Remove(op))
                {
                    if (ed.Rows.Count == 0) Mod.Ops.Remove(ed);
                    return;
                }
        }

        /// <summary>The key attribute of a part's row.</summary>
        internal virtual string KeyOf(EntityPart p) { return p.Kind == EntityEditor.FileKind ? null : "id"; }

        // ---- search ----

        /// <summary>The ids the mod adds (a new bundle row or a new per-id file).</summary>
        public List<long> AddedIds()
        {
            var ids = new List<long>();
            foreach (ModOp op in Mod.Ops)
            {
                long id;
                var ed = op as EditOp;
                if (ed != null && BundleXml != null && ed.Archive == BundleXml && bundle.Match(ed.Entry.Replace('\\', '/'), out id))
                    foreach (RowOp r in ed.Rows)
                        if (r.Kind == RowOpKind.Add && TryId(KeyValue(r, bundle.Key), out id) && !ids.Contains(id)) ids.Add(id);
                var en = op as EntryOp;
                if (en != null && FileXml != null && en.Archive == FileXml && !en.Delete && en.Copy != null && files.Match(en.Name.Replace('\\', '/'), out id) && !ids.Contains(id)) ids.Add(id);
            }
            return ids;
        }

        /// <summary>
        /// Ids that are the text or start with it, or whose name (in Language) contains it, at most max.
        /// The ids the mod adds come first. An empty text gives the first ids of the name file.
        /// </summary>
        public List<EntityHit> Find(string text, int max)
        {
            text = (text ?? "").Trim();
            var hits = new List<EntityHit>();
            var seen = new HashSet<long>();
            foreach (long id in AddedIds())
            {
                string name = OpenAny(id).Name ?? "";
                if (text.Length > 0 && !S(id).StartsWith(text, StringComparison.Ordinal) && name.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                hits.Add(new EntityHit { Id = id, Name = name, Added = true });
                seen.Add(id);
            }
            long exact;
            bool number = TryId(text, out exact);
            if (number && !seen.Contains(exact) && Exists(exact))
            {
                hits.Add(new EntityHit { Id = exact, Name = OriginalName(exact) ?? "" });
                seen.Add(exact);
            }
            DocInfo names = Language == null ? null : Doc(NameXml, StringEntry(Language, StringFiles[0]));
            if (names != null)
                foreach (XmlElem k in names.Doc.Root.Children)
                {
                    if (hits.Count >= max) break;
                    string ids = k.Attr("id"), name = k.Attr("name") ?? "";
                    long id;
                    if (!TryId(ids, out id) || seen.Contains(id)) continue;
                    if (text.Length > 0 && !(number && S(id).StartsWith(text, StringComparison.Ordinal)) && name.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    seen.Add(id);
                    hits.Add(new EntityHit { Id = id, Name = name });
                }
            return hits;
        }

        string OriginalName(long id)
        {
            DocInfo names = Language == null ? null : Doc(NameXml, StringEntry(Language, StringFiles[0]));
            if (names == null) return null;
            List<XmlElem> rows = names.RowsOf("id", id);
            return rows.Count == 0 ? null : rows[0].Attr("name");
        }

        /// <summary>True when the original data has the id: a bundle row, a name row or a per-id file.</summary>
        public bool Exists(long id)
        {
            if (BundleXml != null)
            {
                DocInfo b = Doc(BundleXml, BundleEntry(id));
                if (b != null && b.RowsOf(bundle.Key, id).Count > 0) return true;
            }
            foreach (string archive in Sides())
                foreach (string lang in LanguagesIn(archive))
                {
                    DocInfo n = Doc(archive, StringEntry(lang, StringFiles[0]));
                    if (n != null && n.RowsOf("id", id).Count > 0) return true;
                }
            return FileIndex().ContainsKey(id);
        }

        // ---- one id ----

        protected abstract EntityDraft NewDraft(long id);

        /// <summary>The id as the original data and the mod have it.</summary>
        public EntityDraft OpenAny(long id)
        {
            EntityDraft d = NewDraft(id);
            Fill(d);
            if (d.Parts.Count == 0) throw new ModKitException("there is no " + Entity + " " + id + " in the data or in the mod");
            return d;
        }

        static string TargetName(string archive) { Profile p = Profile.OfArchive(archive); return p != null ? p.Name : DataSet.RootOf(archive); }

        internal void Fill(EntityDraft d)
        {
            d.Parts.Clear();
            d.Sections.Clear();
            bool two = TwoSided || Sides().Count > 1;
            if (BundleXml != null)
                RowParts(d, Entity, TargetName(BundleXml) + " " + Entity, BundleXml, BundleEntry(d.Id), bundle.Key, d.Id, null, false);
            foreach (string archive in Sides())
                for (int f = 0; f < StringFiles.Length; f++)
                    foreach (string lang in LanguagesIn(archive))
                        RowParts(d, StringKinds[f], StringKinds[f] + " (" + lang + (two ? ", " + TargetName(archive) : "") + ")", archive, StringEntry(lang, StringFiles[f]), "id", d.Id, lang, false);
            MoreParts(d);
            if (FileXml != null)
            {
                EntryOp en = FileAdd(d.Id);
                DocInfo doc = null;
                string name;
                if (en != null) doc = Doc(FileXml, en.Copy);
                else if (FileIndex().TryGetValue(d.Id, out name)) doc = Doc(FileXml, name);
                if (doc != null)
                    AddPart(d, new EntityPart { Kind = FileKind, Label = TargetName(FileXml) + " " + Entity + " file", Archive = FileXml, Entry = en != null ? en.Name : doc.Name, Root = doc.Doc.Root, IsNew = en != null });
            }
        }

        /// <summary>Parts beyond the row, the strings and the file (an item's stat rows); called after the strings.</summary>
        protected virtual void MoreParts(EntityDraft d) { }

        /// <summary>The parts of the rows of an entry whose key is the value (shipped rows and rows the mod adds).</summary>
        protected void RowParts(EntityDraft d, string kind, string label, string archive, string entry, string key, string value, bool shared)
        {
            DocInfo doc = Doc(archive, entry);
            if (doc == null) return;
            AddRows(d, kind, label, archive, doc, doc.Rows(key, value), Adds(archive, doc.Name, key, value), null, shared);
        }

        void RowParts(EntityDraft d, string kind, string label, string archive, string entry, string key, long id, string lang, bool shared)
        {
            DocInfo doc = Doc(archive, entry);
            if (doc == null) return;
            AddRows(d, kind, label, archive, doc, doc.RowsOf(key, id), AddsOf(archive, doc.Name, key, id), lang, shared);
        }

        void AddRows(EntityDraft d, string kind, string label, string archive, DocInfo doc, List<XmlElem> rows, List<RowOp> adds, string lang, bool shared)
        {
            foreach (XmlElem row in rows)
            {
                string feature = row.Attr("feature");
                AddPart(d, new EntityPart
                {
                    Kind = kind, Label = label + (string.IsNullOrEmpty(feature) ? "" : " [" + feature + "]"), Archive = archive, Entry = doc.Name,
                    Language = lang, Root = row, Shared = shared,
                });
            }
            foreach (RowOp add in adds)
            {
                XmlElem root = BaseOf(doc, add);
                if (root == null) continue;
                AddPart(d, new EntityPart { Kind = kind, Label = label + " (new)", Archive = archive, Entry = doc.Name, Language = lang, Root = root, Add = add, IsNew = true });
            }
        }

        void AddPart(EntityDraft d, EntityPart part)
        {
            d.Parts.Add(part);
            Walk(d, part, part.Root, "", "");
        }

        void Walk(EntityDraft d, EntityPart part, XmlElem e, string path, string chain)
        {
            if (e.Attrs.Count > 0 || PendingNames(part, e, path).Count > 0)
            {
                var s = new EntitySection { Part = part, Elem = e, Path = path, Chain = chain, Title = part.Label + (path.Length > 0 ? " / " + path : "") };
                foreach (XmlAttr a in e.Attrs) s.Fields.Add(a.Name);
                foreach (string n in PendingNames(part, e, path)) if (!s.Fields.Contains(n)) s.Fields.Add(n);
                d.Sections.Add(s);
            }
            foreach (XmlElem c in e.Children)
            {
                string step = RowPath.StepFor(c);
                string link = c.Name + (c.Name == "environment" ? "[" + (c.Attr("feature") ?? "") + "][" + (c.Attr("locale") ?? "") + "]" : "");
                Walk(d, part, c, path.Length > 0 ? path + "/" + step : step, chain.Length > 0 ? chain + "/" + link : link);
            }
        }

        List<string> PendingNames(EntityPart part, XmlElem e, string path)
        {
            var names = new List<string>();
            RowOp op = part.Add ?? FindChange(part, e, false);
            if (op == null) return names;
            string at = part.Add != null ? path : "";
            foreach (AttrOp a in op.Attrs)
                if (a.Kind == AttrOpKind.Set && (a.At ?? "") == at && !names.Contains(a.Name)) names.Add(a.Name);
            return names;
        }

        // ---- new rows ----

        /// <summary>Adds a literal row to the string file of every language of every side that has the file. Returns the number of rows.</summary>
        internal int AddStringRows(int file, string rowXml)
        {
            int n = 0;
            foreach (string archive in Sides())
                foreach (string lang in LanguagesIn(archive))
                {
                    DocInfo doc = Doc(archive, StringEntry(lang, StringFiles[file]));
                    if (doc == null) continue;
                    Edit(archive, doc.Name).Rows.Add(new RowOp { Kind = RowOpKind.Add, Xml = rowXml });
                    n++;
                }
            return n;
        }

        /// <summary>
        /// Other files named by the id that a clone gets a copy of on every side where the source has one (the npc's
        /// emotion and script files). The same order for every id.
        /// </summary>
        protected virtual string[] CompanionFiles(long id) { return new string[0]; }

        /// <summary>The block of 100000 ids a new id has to share with the id it copies (the bundle file the client reads it from).</summary>
        public static long BlockOf(long id) { return id / 100000; }

        /// <summary>The lowest id of the source's block that is free in the data and in the mod, from <paramref name="from"/> up (0: from the source id).</summary>
        public long NextFreeId(long sourceId, long from)
        {
            long end = BlockOf(sourceId) * 100000 + 99999;
            var added = new HashSet<long>(AddedIds());
            for (long id = Math.Max(from, sourceId + 1); id <= end; id++)
                if (!added.Contains(id) && !Exists(id)) return id;
            throw new ModKitException("no id is free from " + Math.Max(from, sourceId + 1) + " to " + end);
        }

        /// <summary>
        /// A new id that starts as a copy of a shipped one: its bundle row, its string rows in every language of every
        /// side, and its per-id file, all under the new id. The new id has to be in the source's block of 100000 (the
        /// client finds an id by the bundle file of its block). The id is reserved in the mod's &lt;ids space&gt;.
        /// A string row gets the id written as the source row writes it (zero padded where the source is).
        /// </summary>
        public EntityDraft CloneAny(long sourceId, long newId)
        {
            if (newId <= 0) throw new ModKitException("the new id must be above 0");
            if (BlockOf(newId) != BlockOf(sourceId))
                throw new ModKitException("the new id " + newId + " is not in the block of " + Entity + " " + sourceId + " (" + (BlockOf(sourceId) * 100000) + " to " + (BlockOf(sourceId) * 100000 + 99999) + "); the client reads an " + Entity + " from the file of its block");
            if (AddedIds().Contains(newId) || FileAdd(newId) != null) throw new ModKitException("the mod already adds an " + Entity + " " + newId);
            if (Exists(newId)) throw new ModKitException("the id " + newId + " is already used in the data");
            if (AddedIds().Contains(sourceId)) throw new ModKitException(Entity + " " + sourceId + " is itself new in this mod; clone the shipped " + Entity + " it was copied from");

            DocInfo b = BundleXml == null ? null : Doc(BundleXml, BundleEntry(sourceId));
            List<XmlElem> rows = b == null ? new List<XmlElem>() : b.RowsOf(bundle.Key, sourceId);
            string src = FileOf(sourceId);
            if (BundleXml != null && rows.Count == 0)
                throw new ModKitException("the " + TargetName(BundleXml) + " data has no " + Entity + " " + sourceId + " (" + BundleEntry(sourceId) + ")");
            if (BundleXml == null && src == null)
                throw new ModKitException("the " + TargetName(FileXml) + " data has no " + Entity + " " + sourceId);

            foreach (XmlElem row in rows) Copy(BundleXml, b, row, bundle.Key, newId);
            foreach (string archive in Sides())
                for (int f = 0; f < StringFiles.Length; f++)
                    foreach (string lang in LanguagesIn(archive))
                    {
                        DocInfo doc = Doc(archive, StringEntry(lang, StringFiles[f]));
                        if (doc == null) continue;
                        foreach (XmlElem row in doc.RowsOf("id", sourceId)) Copy(archive, doc, row, "id", newId);
                    }

            bool file = false;
            if (src != null)
            {
                Mod.Ops.Add(new EntryOp { Archive = FileXml, Name = NewFileEntry(newId), Copy = src });
                file = true;
            }
            string[] from = CompanionFiles(sourceId), to = CompanionFiles(newId);
            foreach (string archive in Sides())
            {
                M2dArchive ar = Data.Open(archive);
                for (int i = 0; i < from.Length; i++)
                    if (ar.Find(from[i]) != null) Mod.Ops.Add(new EntryOp { Archive = archive, Name = to[i], Copy = from[i] });
            }
            Reserve(newId, rows.Count > 0, file);
            return OpenAny(newId);
        }

        void Copy(string archive, DocInfo doc, XmlElem row, string key, long id)
        {
            string was = row.Attr(key) ?? "", ids = S(id);
            if (was.Length > ids.Length && was.StartsWith("0", StringComparison.Ordinal)) ids = ids.PadLeft(was.Length, '0');
            var add = new RowOp { Kind = RowOpKind.Add, Copy = RowPath.Canonical(row) };
            add.Attrs.Add(new AttrOp { Kind = AttrOpKind.Set, Name = key, Value = ids });
            Edit(archive, doc.Name).Rows.Add(add);
        }

        // the places of a reservation: the bundle rows and the per-id files the id is added to
        List<IdPlace> Places(bool row, bool file)
        {
            var list = new List<IdPlace>();
            if (row)
            {
                string e = bundle.Entry;
                int open = e.IndexOf('{'), close = e.IndexOf('}');
                list.Add(new IdPlace { Archive = BundleXml, Entry = e.Substring(0, open) + "*" + e.Substring(close + 1), Rows = bundle.Row, Key = bundle.Key });
            }
            if (file) list.Add(new IdPlace { Archive = FileXml, Entry = files.Entry });
            return list;
        }

        static bool SamePlaces(List<IdPlace> a, List<IdPlace> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].Archive != b[i].Archive || !string.Equals(a[i].Entry, b[i].Entry, StringComparison.OrdinalIgnoreCase) || a[i].Rows != b[i].Rows) return false;
            return true;
        }

        void Reserve(long id, bool row, bool file)
        {
            foreach (IdRange r in Mod.Ids)
                if (r.Space == Entity && id >= r.From && id <= r.To) return;
            List<IdPlace> want = Places(row, file);
            foreach (IdRange r in Mod.Ids)
            {
                if (r.Space != Entity || BlockOf(r.From) != BlockOf(id) || !SamePlaces(r.Places, want)) continue;
                if (r.To + 1 == id) { r.To = id; return; }
                if (r.From - 1 == id) { r.From = id; return; }
            }
            var made = new IdRange { Space = Entity, From = id, To = id };
            made.Places.AddRange(want);
            Mod.Ids.Add(made);
        }

        /// <summary>Takes a new id out of the mod again: its rows, its per-id file and the edits of that file.</summary>
        public void RemoveClone(long id)
        {
            if (!AddedIds().Contains(id)) throw new ModKitException("the mod adds no " + Entity + " " + id);
            var entries = new List<KeyValuePair<string, string>>();
            if (BundleXml != null) entries.Add(new KeyValuePair<string, string>(BundleXml, BundleEntry(id)));
            foreach (string archive in Sides())
                foreach (string lang in LanguagesIn(archive))
                    foreach (string f in StringFiles) entries.Add(new KeyValuePair<string, string>(archive, StringEntry(lang, f)));
            foreach (KeyValuePair<string, string> kv in entries)
                foreach (EditOp ed in Edits(kv.Key, kv.Value))
                {
                    foreach (RowOp r in AddsOf(kv.Key, kv.Value, "id", id)) ed.Rows.Remove(r);
                    if (ed.Rows.Count == 0) Mod.Ops.Remove(ed);
                }
            EntryOp en = FileAdd(id);
            if (en != null)
            {
                Mod.Ops.Remove(en);
                foreach (EditOp ed in Edits(FileXml, en.Name.Replace('\\', '/'))) Mod.Ops.Remove(ed);
            }
            foreach (string c in CompanionFiles(id))
                for (int i = Mod.Ops.Count - 1; i >= 0; i--)
                {
                    var eo = Mod.Ops[i] as EntryOp; var eu = Mod.Ops[i] as EditOp;
                    string name = eo != null ? eo.Name : eu != null ? eu.Entry : null;
                    if (name != null && string.Equals(name.Replace('\\', '/'), c, StringComparison.OrdinalIgnoreCase)) Mod.Ops.RemoveAt(i);
                }
            for (int i = Mod.Ids.Count - 1; i >= 0; i--)
            {
                IdRange r = Mod.Ids[i];
                if (r.Space != Entity || id < r.From || id > r.To) continue;
                if (r.From == r.To) Mod.Ids.RemoveAt(i);
                else if (r.From == id) r.From++;
                else if (r.To == id) r.To--;
                else
                {
                    var tail = new IdRange { Space = r.Space, From = id + 1, To = r.To };
                    tail.Places.AddRange(r.Places);
                    r.To = id - 1;
                    Mod.Ids.Insert(i + 1, tail);
                }
            }
        }

        /// <summary>A shipped row of the bundle side's entry (an item's option table) by a key.</summary>
        internal XmlElem BundleRow(string entry, string key, string value, out string docName)
        {
            docName = null;
            if (BundleXml == null) return null;
            DocInfo doc = Doc(BundleXml, entry);
            if (doc == null) return null;
            docName = doc.Name;
            List<XmlElem> rows = doc.Rows(key, value);
            return rows.Count == 0 ? null : rows[0];
        }

        internal bool HasBundleEntry(string entry) { return BundleXml != null && Doc(BundleXml, entry) != null; }
    }
}
