// The id lists of the tables that have no editor of their own: maps, interact objects and dungeons. The client reads
//   maps             Table/fieldData.xml, one <fieldData id> per map (kms-client), or one file map/<id>.xml
//                    (gms-client, server); names in string/<lang>/mapname.xml
//   interact objects Table/InteractObject.xml and Table/InteractObject_mastery.xml, one <interact id> per object;
//                    names in string/<lang>/interactname.xml
//   dungeons         Table/DungeonRoom.xml (in the region folder table/na/ on the global targets), one
//                    <dungeonRoom dungeonRoomID> per dungeon; names in string/<lang>/stringfieldenterance.xml
//                    (string table 20, the one the party dialog names a dungeon from)
// The ids of every target are listed; the names come from the first target that has the name file. A mod adds an
// id with a new row of the table (or a new map file) and may name it with a new row of the name file: those ids
// come first.
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public sealed class TableIdList
    {
        public readonly string Kind;
        /// <summary>The table entries (any region folder of table/ is looked in too), their row element and id attribute.</summary>
        readonly string[] tables;
        readonly string row, key;
        /// <summary>The folder of one file per id ("map/"), or null.</summary>
        readonly string folder;
        /// <summary>The name file under string/&lt;lang&gt;/.</summary>
        public readonly string NameFile;
        readonly DataSet data;
        readonly Mod mod;
        /// <summary>The language the names are read in: "en" when there, else the first one.</summary>
        public string Language;

        static readonly string[] Xmls = { EntityEditor.ClientXml, EntityEditor.GmsXml, EntityEditor.ServerXml };

        TableIdList(string kind, DataSet data, Mod mod, string[] tables, string row, string key, string folder, string nameFile)
        {
            if (data == null) throw new ArgumentNullException("data");
            if (mod == null) throw new ArgumentNullException("mod");
            Kind = kind; this.data = data; this.mod = mod; this.tables = tables; this.row = row; this.key = key; this.folder = folder; NameFile = nameFile;
            List<string> langs = Languages();
            Language = langs.Contains("en") ? "en" : langs.Count > 0 ? langs[0] : null;
        }

        public static TableIdList Maps(DataSet data, Mod mod)
        {
            return new TableIdList(IdLookup.Map, data, mod, new[] { "fielddata.xml" }, "fieldData", "id", "map/", "mapname.xml");
        }

        /// <summary>The NPC names only (Names); the NPCs themselves are files of their own, see NpcEditor.</summary>
        public static TableIdList NpcNames(DataSet data, Mod mod)
        {
            return new TableIdList(IdLookup.Npc, data, mod, new string[0], "npc", "id", null, "npcname.xml");
        }

        public static TableIdList InteractObjects(DataSet data, Mod mod)
        {
            return new TableIdList(IdLookup.InteractObject, data, mod, new[] { "interactobject.xml", "interactobject_mastery.xml" }, "interact", "id", null, "interactname.xml");
        }

        public static TableIdList Dungeons(DataSet data, Mod mod)
        {
            return new TableIdList(IdLookup.Dungeon, data, mod, new[] { "dungeonroom.xml" }, "dungeonRoom", "dungeonRoomID", null, "stringfieldenterance.xml");
        }

        // ---- the shipped data ----

        Regex TableRx()
        {
            var alt = new List<string>();
            foreach (string t in tables) alt.Add(Regex.Escape(t));
            return new Regex("^table/([a-z]{2}/)?(" + string.Join("|", alt.ToArray()) + ")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        Regex FolderRx() { return folder == null ? null : new Regex("^" + Regex.Escape(folder) + "([0-9]+)\\.xml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }

        /// <summary>The shipped ids of every target, in id order.</summary>
        public List<long> Ids()
        {
            string ck = "tableids\n" + Kind;
            object o;
            if (data.Cache.TryGetValue(ck, out o)) return (List<long>)o;
            var set = new HashSet<long>();
            Regex trx = TableRx(), frx = FolderRx();
            foreach (string archive in Xmls)
            {
                if (!data.Exists(archive)) continue;
                M2dArchive a = data.Open(archive);
                foreach (M2dEntry e in a.Entries)
                {
                    long id;
                    Match m = frx == null ? null : frx.Match(e.Name);
                    if (m != null && m.Success) { if (EntityEditor.TryId(m.Groups[1].Value, out id)) set.Add(id); continue; }
                    if (!trx.IsMatch(e.Name)) continue;
                    XmlDoc doc = XmlDoc.Parse(a.Read(e), archive + " " + e.Name);
                    if (doc.Root == null) continue;
                    foreach (XmlElem r in doc.Root.Children)
                        if (r.Name == row && EntityEditor.TryId(r.Attr(key), out id)) set.Add(id);
                }
            }
            var list = new List<long>(set);
            list.Sort();
            data.Cache[ck] = list;
            return list;
        }

        string NameArchive()
        {
            foreach (string archive in Xmls)
                if (data.Exists(archive) && LanguagesIn(archive).Count > 0) return archive;
            return null;
        }

        List<string> LanguagesIn(string archive)
        {
            var list = new List<string>();
            var rx = new Regex("^string/([^/]+)/" + Regex.Escape(NameFile) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            foreach (M2dEntry e in data.Open(archive).Entries)
            {
                Match m = rx.Match(e.Name);
                if (m.Success && !list.Contains(m.Groups[1].Value)) list.Add(m.Groups[1].Value);
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        /// <summary>The languages of the name file (of the first target that has one).</summary>
        public List<string> Languages()
        {
            string archive = NameArchive();
            return archive == null ? new List<string>() : LanguagesIn(archive);
        }

        /// <summary>The shipped names in Language: id -> name.</summary>
        public Dictionary<long, string> Names()
        {
            string archive = NameArchive();
            string ck = "tablenames\n" + Kind + "\n" + archive + "\n" + Language;
            object o;
            if (data.Cache.TryGetValue(ck, out o)) return (Dictionary<long, string>)o;
            var names = new Dictionary<long, string>();
            if (archive != null && Language != null)
            {
                M2dArchive a = data.Open(archive);
                M2dEntry e = a.Find("string/" + Language + "/" + NameFile);
                XmlDoc doc = e == null ? null : XmlDoc.Parse(a.Read(e), archive + " " + e.Name);
                if (doc != null && doc.Root != null)
                    foreach (XmlElem k in doc.Root.Children)
                    {
                        long id;
                        if (k.Name == "key" && EntityEditor.TryId(k.Attr("id"), out id) && !names.ContainsKey(id)) names[id] = k.Attr("name") ?? "";
                    }
            }
            data.Cache[ck] = names;
            return names;
        }

        // ---- the mod ----

        static string AddValue(RowOp r, string attr)
        {
            string v = EntityEditor.KeyValue(r, attr);
            if (v != null || r.Xml == null) return v;
            try
            {
                XmlDoc f = XmlDoc.ParseFragment(r.Xml.Trim(), "the new row");
                return f.Top.Count == 1 ? f.Root.Attr(attr) : null;
            }
            catch (ModKitException) { return null; }
        }

        /// <summary>The ids the mod adds (a new table row or a new file of the folder), in the mod's order.</summary>
        public List<long> AddedIds()
        {
            var ids = new List<long>();
            Regex trx = TableRx(), frx = FolderRx();
            foreach (ModOp op in mod.Ops)
            {
                long id;
                var ed = op as EditOp;
                if (ed != null && trx.IsMatch(ed.Entry.Replace('\\', '/')))
                    foreach (RowOp r in ed.Rows)
                        if (r.Kind == RowOpKind.Add && EntityEditor.TryId(AddValue(r, key), out id) && !ids.Contains(id)) ids.Add(id);
                var en = op as EntryOp;
                Match m = en == null || frx == null || en.Delete ? null : frx.Match(en.Name.Replace('\\', '/'));
                if (m != null && m.Success && (en.Copy != null || en.File != null) && EntityEditor.TryId(m.Groups[1].Value, out id) && !ids.Contains(id)) ids.Add(id);
            }
            return ids;
        }

        /// <summary>The names the mod gives with new rows of the name file in Language: id -> name.</summary>
        public Dictionary<long, string> AddedNames()
        {
            var names = new Dictionary<long, string>();
            string entry = "string/" + Language + "/" + NameFile;
            foreach (ModOp op in mod.Ops)
            {
                var ed = op as EditOp;
                if (ed == null || !string.Equals(ed.Entry.Replace('\\', '/'), entry, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (RowOp r in ed.Rows)
                {
                    long id;
                    if (r.Kind == RowOpKind.Add && EntityEditor.TryId(AddValue(r, "id"), out id)) names[id] = AddValue(r, "name") ?? "";
                }
            }
            return names;
        }

        // ---- search ----

        /// <summary>
        /// Ids that are the text or start with it, or whose name contains it, at most max; the ids the mod adds
        /// first, then an id that is the text. An empty text gives the first ids.
        /// </summary>
        public List<EntityHit> Find(string text, int max)
        {
            text = (text ?? "").Trim();
            var hits = new List<EntityHit>();
            var seen = new HashSet<long>();
            Dictionary<long, string> names = Names(), added = AddedNames();
            Func<long, string> nameOf = delegate (long id)
            {
                string n;
                return added.TryGetValue(id, out n) || names.TryGetValue(id, out n) ? n : "";
            };
            long exact;
            bool number = EntityEditor.TryId(text, out exact);
            Func<long, string, bool> fits = delegate (long id, string name)
            {
                return text.Length == 0 || (number && id.ToString(System.Globalization.CultureInfo.InvariantCulture).StartsWith(text, StringComparison.Ordinal))
                    || name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
            };
            foreach (long id in AddedIds())
            {
                if (hits.Count >= max) return hits;
                string name = nameOf(id);
                if (fits(id, name) && seen.Add(id)) hits.Add(new EntityHit { Id = id, Name = name, Added = true });
            }
            List<long> ids = Ids();
            if (number && hits.Count < max && !seen.Contains(exact) && ids.BinarySearch(exact) >= 0)
            {
                seen.Add(exact);
                hits.Add(new EntityHit { Id = exact, Name = nameOf(exact) });
            }
            foreach (long id in ids)
            {
                if (hits.Count >= max) break;
                if (seen.Contains(id)) continue;
                string name = nameOf(id);
                if (!fits(id, name)) continue;
                seen.Add(id);
                hits.Add(new EntityHit { Id = id, Name = name });
            }
            return hits;
        }
    }
}
