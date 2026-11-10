// An item editor's model: one item id seen across the files that make it up, with every edit kept as the mod.xml
// operation it will become (the same operations the table editor writes). Nothing is written to any archive here.
//
// What makes up an item, as the game client reads it:
//   client:Xml.m2d  itemdata/NNN.xml                  <item id> rows; NNN is the id divided by 100000. The client
//                                                     looks an id up in the file of its own block only.
//                   string/<lang>/itemname.xml        <key id name>            the name, one file per language
//                   string/<lang>/itemnameplural.xml  <key id name>            the plural name
//                   string/<lang>/koritemdescription.xml  <key id tooltipDescription guideDescription>
//                   table/itemoptionconstant.xml      <option code> rows       stat rows; an item names one by
//                                                     <option constantID>, and many items share one row
// and on the server:
//   server:Xml.m2d  item/<a>/<bb>/<id>.xml            one file per item, the id is the file name
//
// A part is one such row (or file); a section is one element of a part that has attributes; a field is an
// attribute. A cloned item is a set of <add copy> rows (and an <entry copy> on the server) with the new id.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public sealed class ItemHit
    {
        public long Id;
        public string Name = "";
        /// <summary>True for an item the mod adds.</summary>
        public bool Added;

        public override string ToString() { return Id.ToString(CultureInfo.InvariantCulture) + "  " + Name + (Added ? "  (new)" : ""); }
    }

    public sealed class ItemPart
    {
        /// <summary>"item", "name", "plural", "description", "option" or "server".</summary>
        public string Kind;
        public string Label;
        public string Archive, Entry;
        /// <summary>The language folder of a string part, else null.</summary>
        public string Language;
        /// <summary>The element unedited values come from (for a row the mod adds: the row it copies).</summary>
        public XmlElem Root;
        /// <summary>True when other items use this row too (a stat row of the option table).</summary>
        public bool Shared;
        /// <summary>True when the mod adds this row or file.</summary>
        public bool IsNew;
        internal RowOp Add;
    }

    public sealed class ItemSection
    {
        public ItemPart Part;
        public XmlElem Elem;
        /// <summary>Selector of the element under the part's row; "" for the row itself.</summary>
        public string Path;
        public string Title;
        public readonly List<string> Fields = new List<string>();
        internal string Chain;
    }

    public sealed class ItemDraft
    {
        public readonly ItemEditor Editor;
        public readonly long Id;
        public readonly List<ItemPart> Parts = new List<ItemPart>();
        public readonly List<ItemSection> Sections = new List<ItemSection>();

        internal ItemDraft(ItemEditor editor, long id) { Editor = editor; Id = id; }

        /// <summary>True when the mod adds this item.</summary>
        public bool IsNew { get { foreach (ItemPart p in Parts) if (p.Kind == "item") return p.IsNew; return false; } }

        // ---- fields ----

        static bool IsField(AttrOp a, string path, string field)
        {
            return a.Kind != AttrOpKind.Text && a.Name == field && (a.At ?? "") == path;
        }

        /// <summary>The value the field will have (null: the element has no such attribute).</summary>
        public string Get(ItemSection s, string field)
        {
            RowOp op = s.Part.Add ?? Editor.FindChange(s.Part, s.Elem, false);
            string path = s.Part.Add != null ? s.Path : "";
            if (op != null)
                for (int i = op.Attrs.Count - 1; i >= 0; i--)
                    if (IsField(op.Attrs[i], path, field)) return op.Attrs[i].Kind == AttrOpKind.Set ? op.Attrs[i].Value : null;
            return s.Elem.Attr(field);
        }

        /// <summary>The value in the original data (for a new item: of the item it copies).</summary>
        public string GetOriginal(ItemSection s, string field) { return s.Elem.Attr(field); }

        public bool IsEdited(ItemSection s, string field) { return Get(s, field) != s.Elem.Attr(field); }

        /// <summary>Sets a field; null removes the attribute. Setting the original value back drops the edit.</summary>
        public void Set(ItemSection s, string field, string value)
        {
            if (!TableDraft.IsAttrName(field)) throw new ModKitException("\"" + field + "\" is not an XML attribute name");
            if (s.Part.Add != null && s.Path.Length == 0 && field == KeyOf(s.Part) && value != Get(s, field))
                throw new ModKitException("the " + field + " of a new row is what makes it this item; clone again for another id");
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
        /// Sets a field and, when asked, the same field of the item's other side (client item row and server item
        /// file): the element of the same name path, if it has that attribute and held the same value before.
        /// Returns the section that was set too, or null.
        /// </summary>
        public ItemSection Set(ItemSection s, string field, string value, bool mirror)
        {
            string before = Get(s, field);
            Set(s, field, value);
            if (!mirror) return null;
            ItemSection m = Mirror(s, field);
            if (m == null || Get(m, field) != before) return null;
            Set(m, field, value);
            return m;
        }

        /// <summary>The same field on the item's other side (see Set), or null.</summary>
        public ItemSection Mirror(ItemSection s, string field)
        {
            string other = s.Part.Kind == "item" ? "server" : s.Part.Kind == "server" ? "item" : null;
            if (other == null) return null;
            ItemSection hit = null;
            foreach (ItemSection t in Sections)
            {
                if (t.Part.Kind != other || t.Chain != s.Chain) continue;
                if (hit != null) return null;       // two elements of that path: no telling which
                hit = t;
            }
            if (hit == null || hit.Elem.Attr(field) == null) return null;
            foreach (ItemSection t in Sections)
                if (t != s && t.Part == s.Part && t.Chain == s.Chain) return null;
            return hit;
        }

        static string KeyOf(ItemPart p) { return p.Kind == "option" ? "code" : "id"; }

        // ---- name and description ----

        ItemSection RootSection(ItemPart p)
        {
            foreach (ItemSection s in Sections) if (s.Part == p && s.Path.Length == 0) return s;
            return null;
        }

        string First(string kind, string field)
        {
            ItemPart best = null;
            foreach (ItemPart p in Parts)
                if (p.Kind == kind && (best == null || (p.Language == Editor.Language && best.Language != Editor.Language))) best = p;
            if (best == null) return null;
            ItemSection s = RootSection(best);
            return s == null ? null : Get(s, field);
        }

        int SetAll(string kind, string field, string value)
        {
            int n = 0;
            foreach (ItemPart p in Parts)
            {
                if (p.Kind != kind) continue;
                ItemSection s = RootSection(p);
                if (s == null) continue;
                Set(s, field, value);
                n++;
            }
            return n;
        }

        /// <summary>The name in the editor's language (null: the item has no name row).</summary>
        public string Name { get { return First("name", "name"); } }
        public string Tooltip { get { return First("description", "tooltipDescription"); } }
        public string Guide { get { return First("description", "guideDescription"); } }

        /// <summary>Sets the name in every language file that has a row for the item. Returns the number of rows.</summary>
        public int SetName(string value)
        {
            if (value == null) throw new ArgumentNullException("value");
            return SetAll("name", "name", value);
        }

        /// <summary>
        /// Sets the tooltip text in every language file. An item without a description row gets one in every
        /// language that has a description file. Returns the number of rows.
        /// </summary>
        public int SetTooltip(string value) { return SetDescription("tooltipDescription", value); }

        /// <summary>The second description line (the guide text), handled like the tooltip.</summary>
        public int SetGuide(string value) { return SetDescription("guideDescription", value); }

        int SetDescription(string field, string value)
        {
            if (value == null) throw new ArgumentNullException("value");
            bool any = false;
            foreach (ItemPart p in Parts) if (p.Kind == "description") any = true;
            if (!any)
            {
                if (value.Length == 0) return 0;
                Editor.AddDescriptionRows(Id);
                Editor.Fill(this);
            }
            return SetAll("description", field, value);
        }

        // ---- stat rows ----

        /// <summary>The codes of the stat rows (option table) the item names.</summary>
        public List<string> OptionCodes()
        {
            var list = new List<string>();
            foreach (ItemPart p in Parts) if (p.Kind == "option") list.Add(Get(RootSection(p), "code"));
            return list;
        }

        /// <summary>
        /// Gives the item a stat row of its own: the shared row <paramref name="code"/> is copied to
        /// <paramref name="newCode"/> and the item's constantID is pointed at the copy, so that changing the
        /// stats no longer changes every item that uses the shared row.
        /// </summary>
        public void GiveOwnOption(string code, string newCode)
        {
            Editor.OwnOption(this, code, newCode);
            Editor.Fill(this);
        }
    }

    public sealed class ItemEditor
    {
        public const string ClientXml = "client:Xml.m2d", ServerXml = "server:Xml.m2d";
        public const string OptionTable = "table/itemoptionconstant.xml";
        public const string IdSpace = "item";
        const string ClientIdPlace = "itemdata/*.xml", ServerIdPlace = "item/*/*/{id}.xml";
        static readonly string[] StringFiles = { "itemname.xml", "itemnameplural.xml", "koritemdescription.xml" };
        static readonly string[] StringKinds = { "name", "plural", "description" };
        static readonly Regex ServerItem = new Regex("^item/[^/]+/[^/]+/(\\d+)\\.xml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex NameFile = new Regex("^string/([^/]+)/itemname\\.xml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public readonly DataSet Data;
        public readonly Mod Mod;
        /// <summary>The language folders that hold an item name file.</summary>
        public readonly List<string> Languages = new List<string>();
        /// <summary>The language names are searched and shown in. Edits of a name go to every language.</summary>
        public string Language;
        /// <summary>True when the data has a server archive with item files.</summary>
        public readonly bool HasServer;

        sealed class DocInfo
        {
            internal XmlDoc Doc;
            internal string Name;
            internal readonly Dictionary<string, Dictionary<string, List<XmlElem>>> Index = new Dictionary<string, Dictionary<string, List<XmlElem>>>();

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
        }

        public ItemEditor(DataSet data, Mod mod)
        {
            if (data == null) throw new ArgumentNullException("data");
            if (mod == null) throw new ArgumentNullException("mod");
            Data = data; Mod = mod;
            if (!data.Exists(ClientXml)) throw new ModKitException("the item editor needs the client archive " + ClientXml);
            foreach (M2dEntry e in data.Open(ClientXml).Entries)
            {
                Match m = NameFile.Match(e.Name);
                if (m.Success) Languages.Add(m.Groups[1].Value);
            }
            Languages.Sort(StringComparer.OrdinalIgnoreCase);
            Language = Languages.Contains("en") ? "en" : Languages.Count > 0 ? Languages[0] : null;
            HasServer = data.Exists(ServerXml) && ServerIndex().Count > 0;
        }

        // ---- the original data ----

        static string S(long id) { return id.ToString(CultureInfo.InvariantCulture); }

        /// <summary>The item file of an id: the client reads an id from the file of its own block of 100000 only.</summary>
        public static string BucketEntry(long id) { return "itemdata/" + (id / 100000).ToString("000", CultureInfo.InvariantCulture) + ".xml"; }

        DocInfo Doc(string archive, string entry)
        {
            string key = "itemdoc\n" + archive + "\n" + entry.ToLowerInvariant();
            object o;
            if (Data.Cache.TryGetValue(key, out o)) return (DocInfo)o;
            DocInfo info = null;
            M2dArchive arch = Data.Open(archive);
            M2dEntry e = arch.Find(entry);
            if (e != null)
            {
                XmlDoc doc = XmlDoc.Parse(arch.Read(e), archive + " " + e.Name);
                if (doc.Root != null) info = new DocInfo { Doc = doc, Name = e.Name };
            }
            Data.Cache[key] = info;
            return info;
        }

        Dictionary<long, string> ServerIndex()
        {
            const string key = "itemserver";
            object o;
            if (Data.Cache.TryGetValue(key, out o)) return (Dictionary<long, string>)o;
            var ix = new Dictionary<long, string>();
            foreach (M2dEntry e in Data.Open(ServerXml).Entries)
            {
                Match m = ServerItem.Match(e.Name);
                long id;
                if (m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out id)) ix[id] = e.Name;
            }
            Data.Cache[key] = ix;
            return ix;
        }

        static string StringEntry(string lang, int file) { return "string/" + lang + "/" + StringFiles[file]; }

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

        EditOp Edit(string archive, string entry)
        {
            foreach (EditOp ed in Edits(archive, entry)) return ed;
            var made = new EditOp { Archive = archive, Entry = entry };
            Mod.Ops.Add(made);
            return made;
        }

        static string KeyValue(RowOp add, string key)
        {
            for (int i = add.Attrs.Count - 1; i >= 0; i--)
            {
                AttrOp a = add.Attrs[i];
                if (a.Kind == AttrOpKind.Set && string.IsNullOrEmpty(a.At) && a.Name == key) return a.Value;
            }
            return null;
        }

        // the rows the mod adds to an entry under a key value: <add ...><set name="id" value="..."/>, or a literal row
        List<RowOp> Adds(string archive, string entry, string key, string value)
        {
            var list = new List<RowOp>();
            foreach (EditOp ed in Edits(archive, entry))
                foreach (RowOp r in ed.Rows)
                {
                    if (r.Kind != RowOpKind.Add) continue;
                    string v = KeyValue(r, key);
                    if (v == null && r.Xml != null)
                    {
                        XmlElem lit = Literal(r);
                        v = lit == null ? null : lit.Attr(key);
                    }
                    if (v == value) list.Add(r);
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

        XmlElem BaseOf(DocInfo doc, RowOp add)
        {
            if (add.Copy == null) return Literal(add);
            string err;
            return RowPath.Parse(add.Copy).TryResolve(doc.Doc.Root, out err);
        }

        EntryOp ServerAdd(long id)
        {
            foreach (ModOp op in Mod.Ops)
            {
                var en = op as EntryOp;
                if (en == null || en.Archive != ServerXml || en.Delete || en.Copy == null) continue;
                Match m = ServerItem.Match(en.Name.Replace('\\', '/'));
                long v;
                if (m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out v) && v == id) return en;
            }
            return null;
        }

        internal RowOp FindChange(ItemPart part, XmlElem elem, bool create)
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

        internal void DropChange(ItemPart part, RowOp op)
        {
            foreach (EditOp ed in Edits(part.Archive, part.Entry))
                if (ed.Rows.Remove(op))
                {
                    if (ed.Rows.Count == 0) Mod.Ops.Remove(ed);
                    return;
                }
        }

        // ---- search ----

        /// <summary>The ids of the items the mod adds.</summary>
        public List<long> AddedIds()
        {
            var ids = new List<long>();
            foreach (ModOp op in Mod.Ops)
            {
                var ed = op as EditOp;
                if (ed == null || ed.Archive != ClientXml || !ed.Entry.Replace('\\', '/').StartsWith("itemdata/", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (RowOp r in ed.Rows)
                {
                    long id;
                    if (r.Kind == RowOpKind.Add && long.TryParse(KeyValue(r, "id") ?? "", NumberStyles.None, CultureInfo.InvariantCulture, out id) && !ids.Contains(id)) ids.Add(id);
                }
            }
            return ids;
        }

        /// <summary>
        /// Items whose id is the text or starts with it, or whose name (in Language) contains it, at most max.
        /// The items the mod adds come first. An empty text gives the first items of the name file.
        /// </summary>
        public List<ItemHit> Find(string text, int max)
        {
            text = (text ?? "").Trim();
            var hits = new List<ItemHit>();
            var seen = new HashSet<long>();
            foreach (long id in AddedIds())
            {
                string name = Open(id).Name ?? "";
                if (text.Length > 0 && !S(id).StartsWith(text, StringComparison.Ordinal) && name.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                hits.Add(new ItemHit { Id = id, Name = name, Added = true });
                seen.Add(id);
            }
            long exact;
            bool number = long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out exact);
            if (number && !seen.Contains(exact) && Exists(exact))
            {
                hits.Add(new ItemHit { Id = exact, Name = OriginalName(exact) ?? "" });
                seen.Add(exact);
            }
            DocInfo names = Language == null ? null : Doc(ClientXml, StringEntry(Language, 0));
            if (names != null)
                foreach (XmlElem k in names.Doc.Root.Children)
                {
                    if (hits.Count >= max) break;
                    string ids = k.Attr("id"), name = k.Attr("name") ?? "";
                    long id;
                    if (ids == null || !long.TryParse(ids, NumberStyles.None, CultureInfo.InvariantCulture, out id) || seen.Contains(id)) continue;
                    if (text.Length > 0 && !(number && ids.StartsWith(text, StringComparison.Ordinal)) && name.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    seen.Add(id);
                    hits.Add(new ItemHit { Id = id, Name = name });
                }
            return hits;
        }

        string OriginalName(long id)
        {
            DocInfo names = Language == null ? null : Doc(ClientXml, StringEntry(Language, 0));
            if (names == null) return null;
            List<XmlElem> rows = names.Rows("id", S(id));
            return rows.Count == 0 ? null : rows[0].Attr("name");
        }

        /// <summary>True when the original data has the id: an item row, a name row or a server item file.</summary>
        public bool Exists(long id)
        {
            DocInfo b = Doc(ClientXml, BucketEntry(id));
            if (b != null && b.Rows("id", S(id)).Count > 0) return true;
            foreach (string lang in Languages)
            {
                DocInfo n = Doc(ClientXml, StringEntry(lang, 0));
                if (n != null && n.Rows("id", S(id)).Count > 0) return true;
            }
            return HasServer && ServerIndex().ContainsKey(id);
        }

        // ---- one item ----

        /// <summary>The item of an id, as the original data and the mod have it.</summary>
        public ItemDraft Open(long id)
        {
            var d = new ItemDraft(this, id);
            Fill(d);
            if (d.Parts.Count == 0) throw new ModKitException("there is no item " + id + " in the data or in the mod");
            return d;
        }

        internal void Fill(ItemDraft d)
        {
            d.Parts.Clear();
            d.Sections.Clear();
            string ids = S(d.Id);

            RowParts(d, "item", "client item", BucketEntry(d.Id), "id", ids, null);
            for (int f = 0; f < StringFiles.Length; f++)
                foreach (string lang in Languages)
                    RowParts(d, StringKinds[f], StringKinds[f] + " (" + lang + ")", StringEntry(lang, f), "id", ids, lang);

            // the stat rows the item names
            var codes = new List<string>();
            foreach (ItemSection s in d.Sections.ToArray())
            {
                if (s.Part.Kind != "item" || s.Elem.Name != "option" || s.Elem.Attr("constantID") == null) continue;
                string code = d.Get(s, "constantID");
                if (string.IsNullOrEmpty(code) || code == "0" || codes.Contains(code)) continue;
                codes.Add(code);
                RowParts(d, "option", "stat row " + code, OptionTable, "code", code, null);
            }

            if (HasServer)
            {
                string name;
                EntryOp en = ServerAdd(d.Id);
                DocInfo doc = null;
                if (en != null) doc = Doc(ServerXml, en.Copy);
                else if (ServerIndex().TryGetValue(d.Id, out name)) doc = Doc(ServerXml, name);
                if (doc != null)
                    AddPart(d, new ItemPart { Kind = "server", Label = "server item", Archive = ServerXml, Entry = en != null ? en.Name : doc.Name, Root = doc.Doc.Root, IsNew = en != null });
            }
        }

        void RowParts(ItemDraft d, string kind, string label, string entry, string key, string value, string lang)
        {
            DocInfo doc = Doc(ClientXml, entry);
            if (doc == null) return;
            foreach (XmlElem row in doc.Rows(key, value))
            {
                string feature = row.Attr("feature");
                AddPart(d, new ItemPart
                {
                    Kind = kind, Label = label + (string.IsNullOrEmpty(feature) ? "" : " [" + feature + "]"), Archive = ClientXml, Entry = doc.Name,
                    Language = lang, Root = row, Shared = kind == "option",
                });
            }
            foreach (RowOp add in Adds(ClientXml, doc.Name, key, value))
            {
                XmlElem root = BaseOf(doc, add);
                if (root == null) continue;
                AddPart(d, new ItemPart { Kind = kind, Label = label + " (new)", Archive = ClientXml, Entry = doc.Name, Language = lang, Root = root, Add = add, IsNew = true });
            }
        }

        void AddPart(ItemDraft d, ItemPart part)
        {
            d.Parts.Add(part);
            Walk(d, part, part.Root, "", "");
        }

        void Walk(ItemDraft d, ItemPart part, XmlElem e, string path, string chain)
        {
            if (e.Attrs.Count > 0 || PendingNames(part, e, path).Count > 0)
            {
                var s = new ItemSection { Part = part, Elem = e, Path = path, Chain = chain, Title = part.Label + (path.Length > 0 ? " / " + path : "") };
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

        List<string> PendingNames(ItemPart part, XmlElem e, string path)
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

        internal void AddDescriptionRows(long id)
        {
            foreach (string lang in Languages)
            {
                DocInfo doc = Doc(ClientXml, StringEntry(lang, 2));
                if (doc == null) continue;
                Edit(ClientXml, doc.Name).Rows.Add(new RowOp { Kind = RowOpKind.Add, Xml = "<key id=\"" + S(id) + "\" tooltipDescription=\"\" guideDescription=\"\" />" });
            }
        }

        internal void OwnOption(ItemDraft d, string code, string newCode)
        {
            long n;
            if (!long.TryParse(newCode ?? "", NumberStyles.None, CultureInfo.InvariantCulture, out n) || n <= 0) throw new ModKitException("the new stat row code must be a number above 0");
            DocInfo doc = Doc(ClientXml, OptionTable);
            if (doc == null) throw new ModKitException(ClientXml + " has no " + OptionTable);
            if (doc.Rows("code", newCode).Count > 0 || Adds(ClientXml, doc.Name, "code", newCode).Count > 0)
                throw new ModKitException("the stat row code " + newCode + " is already used");
            ItemPart src = null;
            foreach (ItemPart p in d.Parts)
                if (p.Kind == "option" && (p.Add != null ? KeyValue(p.Add, "code") : p.Root.Attr("code")) == code) { src = p; break; }
            if (src == null) throw new ModKitException("item " + d.Id + " names no stat row " + code);
            var add = new RowOp { Kind = RowOpKind.Add };
            if (src.Add != null)
            {
                add.Copy = src.Add.Copy; add.Xml = src.Add.Xml;
                foreach (AttrOp a in src.Add.Attrs)
                    if (!(string.IsNullOrEmpty(a.At) && a.Name == "code")) add.Attrs.Add(new AttrOp { Kind = a.Kind, Name = a.Name, Value = a.Value, At = a.At });
            }
            else add.Copy = RowPath.Canonical(src.Root);
            add.Attrs.Add(new AttrOp { Kind = AttrOpKind.Set, Name = "code", Value = newCode });
            Edit(ClientXml, doc.Name).Rows.Add(add);
            foreach (ItemSection s in d.Sections.ToArray())
                if (s.Part.Kind == "item" && s.Elem.Name == "option" && d.Get(s, "constantID") == code) d.Set(s, "constantID", newCode);
        }

        /// <summary>The lowest id of the source item's block that is free in the data and in the mod, from <paramref name="from"/> up (0: from the source id).</summary>
        public long NextFreeId(long sourceId, long from)
        {
            long block = sourceId / 100000, end = block * 100000 + 99999;
            var added = new HashSet<long>(AddedIds());
            for (long id = Math.Max(from, sourceId + 1); id <= end; id++)
                if (!added.Contains(id) && !Exists(id)) return id;
            throw new ModKitException("no id is free from " + Math.Max(from, sourceId + 1) + " to " + end);
        }

        /// <summary>
        /// A new item that starts as a copy of a shipped one: its item row, its name, plural name and description
        /// rows in every language, and its server item file, all under the new id. The new id has to be in the
        /// source item's block of 100000 (the client finds an item by its block, and the block also says what kind
        /// of item it is). name and tooltip may be null to keep the source's. The id is reserved in the mod's
        /// &lt;ids space="item"&gt;.
        /// </summary>
        public ItemDraft Clone(long sourceId, long newId, string name, string tooltip)
        {
            if (newId <= 0) throw new ModKitException("the new id must be above 0");
            if (newId / 100000 != sourceId / 100000)
                throw new ModKitException("the new id " + newId + " is not in the block of item " + sourceId + " (" + (sourceId / 100000 * 100000) + " to " + (sourceId / 100000 * 100000 + 99999) + "); the client reads an item from the file of its block");
            if (AddedIds().Contains(newId) || ServerAdd(newId) != null) throw new ModKitException("the mod already adds an item " + newId);
            if (Exists(newId)) throw new ModKitException("the id " + newId + " is already used in the data");
            if (AddedIds().Contains(sourceId)) throw new ModKitException("item " + sourceId + " is itself new in this mod; clone the shipped item it was copied from");
            DocInfo bucket = Doc(ClientXml, BucketEntry(sourceId));
            List<XmlElem> rows = bucket == null ? new List<XmlElem>() : bucket.Rows("id", S(sourceId));
            if (rows.Count == 0) throw new ModKitException("the client data has no item " + sourceId + " (" + BucketEntry(sourceId) + ")");

            string ids = S(newId);
            foreach (XmlElem row in rows) Copy(bucket, row, "id", ids, null, null);
            for (int f = 0; f < StringFiles.Length; f++)
                foreach (string lang in Languages)
                {
                    DocInfo doc = Doc(ClientXml, StringEntry(lang, f));
                    if (doc == null) continue;
                    foreach (XmlElem row in doc.Rows("id", S(sourceId)))
                        Copy(doc, row, "id", ids, f == 0 ? "name" : f == 2 ? "tooltipDescription" : null, f == 0 ? name : f == 2 ? tooltip : null);
                }

            bool server = false;
            string src;
            if (HasServer && ServerIndex().TryGetValue(sourceId, out src))
            {
                int slash = src.LastIndexOf('/');
                string file = src.Substring(slash + 1, src.Length - slash - 5);
                Mod.Ops.Add(new EntryOp { Archive = ServerXml, Name = src.Substring(0, slash + 1) + ids.PadLeft(file.Length, '0') + ".xml", Copy = src });
                server = true;
            }
            Reserve(newId, server);

            ItemDraft d = Open(newId);
            if (tooltip != null && d.Tooltip == null) d.SetTooltip(tooltip);
            return d;
        }

        void Copy(DocInfo doc, XmlElem row, string key, string value, string field, string fieldValue)
        {
            var add = new RowOp { Kind = RowOpKind.Add, Copy = RowPath.Canonical(row) };
            add.Attrs.Add(new AttrOp { Kind = AttrOpKind.Set, Name = key, Value = value });
            if (field != null && fieldValue != null && fieldValue != row.Attr(field)) add.Attrs.Add(new AttrOp { Kind = AttrOpKind.Set, Name = field, Value = fieldValue });
            Edit(ClientXml, doc.Name).Rows.Add(add);
        }

        void Reserve(long id, bool server)
        {
            foreach (IdRange r in Mod.Ids)
                if (r.Space == IdSpace && id >= r.From && id <= r.To) return;
            foreach (IdRange r in Mod.Ids)
            {
                if (r.Space != IdSpace || r.From / 100000 != id / 100000 || (r.Places.Count == 2) != server) continue;
                if (r.To + 1 == id) { r.To = id; return; }
                if (r.From - 1 == id) { r.From = id; return; }
            }
            var made = new IdRange { Space = IdSpace, From = id, To = id };
            made.Places.Add(new IdPlace { Archive = ClientXml, Entry = ClientIdPlace, Rows = "item", Key = "id" });
            if (server) made.Places.Add(new IdPlace { Archive = ServerXml, Entry = ServerIdPlace });
            Mod.Ids.Add(made);
        }

        /// <summary>Takes a new item out of the mod again: its rows, its server file and the edits of that file.</summary>
        public void RemoveClone(long id)
        {
            if (!AddedIds().Contains(id)) throw new ModKitException("the mod adds no item " + id);
            string ids = S(id);
            var entries = new List<string> { BucketEntry(id) };
            foreach (string lang in Languages) for (int f = 0; f < StringFiles.Length; f++) entries.Add(StringEntry(lang, f));
            foreach (string entry in entries)
                foreach (EditOp ed in Edits(ClientXml, entry))
                {
                    foreach (RowOp r in Adds(ClientXml, entry, "id", ids)) ed.Rows.Remove(r);
                    if (ed.Rows.Count == 0) Mod.Ops.Remove(ed);
                }
            EntryOp en = ServerAdd(id);
            if (en != null)
            {
                Mod.Ops.Remove(en);
                foreach (EditOp ed in Edits(ServerXml, en.Name.Replace('\\', '/'))) Mod.Ops.Remove(ed);
            }
            for (int i = Mod.Ids.Count - 1; i >= 0; i--)
            {
                IdRange r = Mod.Ids[i];
                if (r.Space != IdSpace || id < r.From || id > r.To) continue;
                if (r.From == r.To) Mod.Ids.RemoveAt(i);
                else if (r.From == id) r.From++;
                else if (r.To == id) r.To--;
            }
        }
    }
}
