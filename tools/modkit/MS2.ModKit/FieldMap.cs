// Field maps: which field of the client's copy of an entity corresponds to which field of the server's copy,
// and how the two values are converted. The maps are data (Maps/*.fieldmap.xml, embedded); nothing here knows
// an entity by name. A field is written "element-path@attribute", the path counted from the element named by
// the side's "under" attribute.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public enum FieldKind { Unknown, Mapped, Candidate, ClientOnly, ServerOnly }

    /// <summary>One pair of corresponding fields.</summary>
    public sealed class FieldMapEntry
    {
        public string Client, Server;
        /// <summary>same, bool, numlist, affix or pick (see Maps/item.fieldmap.xml).</summary>
        public string Convert = "same";
        public string Prefix = "", Suffix = "";
        /// <summary>pick: the server field that holds the position (from 1) of the client's element in the server's list.</summary>
        public string Index;
        /// <summary>The server value that an absent client attribute stands for; null when there is none.</summary>
        public string Default;
        public bool Candidate;
        public int Compared, Agree, Differ, ClientAbsent;
        public string Loader = "", Confidence = "";

        public static string ElemOf(string field) { int i = field.LastIndexOf('@'); return i < 0 ? field : field.Substring(0, i); }
        public static string AttrOf(string field) { int i = field.LastIndexOf('@'); return i < 0 ? "" : field.Substring(i + 1); }

        static double[] Numbers(string v)
        {
            string[] parts = v.Split(',');
            var r = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out r[i])) return null;
            return r;
        }

        static int Position(string index)
        {
            int k;
            return index != null && int.TryParse(index.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out k) ? k : 0;
        }

        /// <summary>Whether a client value and a server value say the same thing. null = the attribute is not there.</summary>
        public bool Corresponds(string client, string server, string index)
        {
            if (client == null) return server == null || (Default != null && server == Default);
            if (server == null) return false;
            switch (Convert)
            {
                case "bool":
                    return (client == "true" ? "1" : client == "false" ? "0" : client) == server;
                case "numlist":
                {
                    double[] a = Numbers(client), b = Numbers(server);
                    if (a == null || b == null) return client == server;
                    if (a.Length != b.Length) return false;
                    for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
                    return true;
                }
                case "affix":
                    return client == server || (client.IndexOf('/') < 0 && string.Equals(Prefix + client + Suffix, server, StringComparison.OrdinalIgnoreCase));
                case "pick":
                {
                    if (client.IndexOf(',') >= 0) return client == server;
                    string[] list = server.Split(',');
                    int k = Position(index);
                    return k >= 1 && k <= list.Length && list[k - 1] == client;
                }
                default:
                    return client == server;
            }
        }

        /// <summary>The server value for a client value. serverNow is what the server holds before the change.</summary>
        public bool TryToServer(string client, string serverNow, string index, out string server, out string why)
        {
            server = null; why = null;
            if (client == null)
            {
                if (Default == null) { why = "the client attribute is removed and the map gives no server value for that"; return false; }
                server = Default;
                return true;
            }
            switch (Convert)
            {
                case "bool":
                    server = client == "true" ? "1" : client == "false" ? "0" : client;
                    return true;
                case "numlist":
                {
                    double[] a = Numbers(client);
                    if (a == null) { why = "\"" + client + "\" is not a list of numbers"; return false; }
                    var sb = new StringBuilder();
                    for (int i = 0; i < a.Length; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(a[i].ToString("0.000000", CultureInfo.InvariantCulture));
                    }
                    server = sb.ToString();
                    return true;
                }
                case "affix":
                    server = client.IndexOf('/') >= 0 || client.Length == 0 ? client : Prefix + client + Suffix;
                    return true;
                case "pick":
                {
                    if (client.IndexOf(',') >= 0) { server = client; return true; }
                    if (serverNow == null) { why = "the server holds no list to put the value into"; return false; }
                    string[] list = serverNow.Split(',');
                    int k = Position(index);
                    if (k < 1 || k > list.Length) { why = "the server's " + Index + " (\"" + index + "\") does not point into its list of " + list.Length; return false; }
                    list[k - 1] = client;
                    server = string.Join(",", list);
                    return true;
                }
                default:
                    server = client;
                    return true;
            }
        }

        /// <summary>The client value for a server value. clientNow is what the client holds before the change (it decides the form).</summary>
        public bool TryToClient(string server, string clientNow, string index, out string client, out string why)
        {
            client = null; why = null;
            if (server == null) { why = "the server attribute is removed"; return false; }
            switch (Convert)
            {
                case "bool":
                    if (clientNow != null && clientNow != "true" && clientNow != "false") client = server;
                    else client = server == "1" ? "true" : server == "0" ? "false" : server;
                    return true;
                case "numlist":
                {
                    double[] a = Numbers(server);
                    if (a == null) { why = "\"" + server + "\" is not a list of numbers"; return false; }
                    var sb = new StringBuilder();
                    for (int i = 0; i < a.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append(a[i].ToString("0.######", CultureInfo.InvariantCulture));
                    }
                    client = sb.ToString();
                    return true;
                }
                case "affix":
                    if ((clientNow == null || clientNow.IndexOf('/') < 0) && server.Length > Prefix.Length + Suffix.Length
                        && server.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && server.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
                        client = server.Substring(Prefix.Length, server.Length - Prefix.Length - Suffix.Length);
                    else client = server;
                    return true;
                case "pick":
                {
                    if (clientNow != null && clientNow.IndexOf(',') >= 0) { client = server; return true; }
                    string[] list = server.Split(',');
                    int k = Position(index);
                    if (k < 1 || k > list.Length) { why = "the server's " + Index + " (\"" + index + "\") does not point into its list of " + list.Length; return false; }
                    client = list[k - 1];
                    return true;
                }
                default:
                    client = server;
                    return true;
            }
        }
    }

    /// <summary>Where one side keeps an entity: archive, entry name pattern, the row element (null: the entry is the row).</summary>
    public sealed class EntitySide
    {
        public string Archive, EntryPattern, Row, Key = "id", Under;
        /// <summary>The entry name carries id / Divisor.</summary>
        public long Divisor = 1;
        Regex regex;

        internal void Compile()
        {
            var sb = new StringBuilder("^");
            string p = EntryPattern;
            for (int i = 0; i < p.Length; i++)
            {
                char c = p[i];
                if (c == '*') sb.Append("[^/]+");
                else if (c == '{')
                {
                    int end = p.IndexOf('}', i);
                    string tok = end < 0 ? "" : p.Substring(i + 1, end - i - 1);
                    if (tok == "id") Divisor = 1;
                    else if (tok.StartsWith("id/", StringComparison.Ordinal) && long.TryParse(tok.Substring(3), NumberStyles.None, CultureInfo.InvariantCulture, out Divisor) && Divisor > 0) { }
                    else throw new ModKitException("field map: entry pattern \"" + p + "\" may hold {id} or {id/N} only");
                    sb.Append("(\\d+)");
                    i = end;
                }
                else sb.Append(Regex.Escape(c.ToString()));
            }
            sb.Append('$');
            regex = new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        /// <summary>Whether an entry belongs to the entity; num is the number in its name.</summary>
        public bool Match(string entry, out long num)
        {
            num = 0;
            Match m = regex.Match(entry.Replace('\\', '/'));
            if (!m.Success) return false;
            return m.Groups.Count < 2 || long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out num);
        }

        /// <summary>The number the entry of an id carries in its name.</summary>
        public long NameNumber(long id) { return id / Divisor; }

        /// <summary>The entry of an id among the given entry names, or null.</summary>
        public string EntryOf(long id, IEnumerable<string> names)
        {
            long want = NameNumber(id), n;
            foreach (string name in names) if (Match(name, out n) && n == want) return name;
            return null;
        }
    }

    /// <summary>Where a field of an entity sits in a document.</summary>
    public sealed class FieldLoc
    {
        public long Id;
        public XmlElem Under;       // the element the paths count from
        public string Path;         // element names below Under, joined by '/' ("" for Under itself)
    }

    public sealed class EntityMap
    {
        public string Name;
        public int Sample;
        public EntitySide Client, Server;
        public readonly List<FieldMapEntry> Entries = new List<FieldMapEntry>();        // map and candidate lines
        public readonly List<string> ClientOnly = new List<string>(), ServerOnly = new List<string>();
        /// <summary>Tables whose rows belong to an id of this entity (see ServerRefs).</summary>
        public readonly List<RefRule> Refs = new List<RefRule>();
        readonly Dictionary<string, FieldMapEntry> byClient = new Dictionary<string, FieldMapEntry>(StringComparer.Ordinal);
        readonly Dictionary<string, FieldMapEntry> byServer = new Dictionary<string, FieldMapEntry>(StringComparer.Ordinal);
        readonly Dictionary<string, FieldMapEntry> candClient = new Dictionary<string, FieldMapEntry>(StringComparer.Ordinal);
        readonly Dictionary<string, FieldMapEntry> candServer = new Dictionary<string, FieldMapEntry>(StringComparer.Ordinal);
        readonly Dictionary<string, bool> onlyClient = new Dictionary<string, bool>(StringComparer.Ordinal), onlyServer = new Dictionary<string, bool>(StringComparer.Ordinal);

        public EntitySide Side(bool client) { return client ? Client : Server; }

        internal void Add(FieldMapEntry e)
        {
            Entries.Add(e);
            if (e.Candidate) { if (!candClient.ContainsKey(e.Client)) candClient[e.Client] = e; if (!candServer.ContainsKey(e.Server)) candServer[e.Server] = e; return; }
            if (byClient.ContainsKey(e.Client)) throw new ModKitException("field map " + Name + ": client field " + e.Client + " is mapped twice");
            if (byServer.ContainsKey(e.Server)) throw new ModKitException("field map " + Name + ": server field " + e.Server + " is mapped twice");
            byClient[e.Client] = e; byServer[e.Server] = e;
        }

        internal void AddOnly(bool client, string field)
        {
            (client ? ClientOnly : ServerOnly).Add(field);
            (client ? onlyClient : onlyServer)[field] = true;
        }

        /// <summary>What the map says about a field of one side. entry is the map or candidate line, if any.</summary>
        public FieldKind Classify(bool clientSide, string field, out FieldMapEntry entry)
        {
            if ((clientSide ? byClient : byServer).TryGetValue(field, out entry)) return FieldKind.Mapped;
            if ((clientSide ? onlyClient : onlyServer).ContainsKey(field)) return clientSide ? FieldKind.ClientOnly : FieldKind.ServerOnly;
            if ((clientSide ? candClient : candServer).TryGetValue(field, out entry)) return FieldKind.Candidate;
            return FieldKind.Unknown;
        }

        /// <summary>The entity id and path of an element of one side's entry; null when the element is not below the side's "under" element.</summary>
        public FieldLoc Locate(bool clientSide, string entry, XmlElem el)
        {
            EntitySide side = Side(clientSide);
            var names = new List<string>();
            for (XmlElem c = el; c != null; c = c.Parent)
            {
                if (c.Name == side.Under && (side.Row == null ? c.Parent != null && c.Parent.Parent == null : c.Parent != null && c.Parent.Name == side.Row))
                {
                    var loc = new FieldLoc { Under = c, Path = string.Join("/", names.ToArray()) };
                    if (side.Row == null) { if (!side.Match(entry, out loc.Id)) return null; }
                    else if (!long.TryParse(c.Parent.Attr(side.Key) ?? "", NumberStyles.None, CultureInfo.InvariantCulture, out loc.Id)) return null;
                    return loc;
                }
                names.Insert(0, c.Name);
            }
            return null;
        }

        /// <summary>The "under" elements of an id in one side's document (root = the entry's root element, or a lone row).</summary>
        public List<XmlElem> UnderElems(bool clientSide, XmlElem root, long id)
        {
            EntitySide side = Side(clientSide);
            var list = new List<XmlElem>();
            if (root == null) return list;
            if (side.Row == null) { foreach (XmlElem c in root.Children) if (c.Name == side.Under) list.Add(c); return list; }
            string want = id.ToString(CultureInfo.InvariantCulture);
            if (root.Name == side.Row && root.Parent == null && root.Attr(side.Key) == want)
                foreach (XmlElem c in root.Children) if (c.Name == side.Under) list.Add(c);
            foreach (XmlElem row in root.Children)
            {
                if (row.Name != side.Row || row.Attr(side.Key) != want) continue;
                foreach (XmlElem c in row.Children) if (c.Name == side.Under) list.Add(c);
            }
            return list;
        }

        static string Variant(XmlElem under)
        {
            return (under.Attr("feature") ?? "") + "\n" + (under.Attr("locale") ?? "");
        }

        /// <summary>
        /// The "under" element among the candidates that belongs to the given one of the other side: the only one with
        /// the same feature and locale, or the only one at all when the other side has only one too. Else null and why.
        /// </summary>
        public static XmlElem Pair(XmlElem like, int likeCount, List<XmlElem> candidates, out string why)
        {
            why = null;
            if (candidates.Count == 0) { why = "has no <" + like.Name + ">"; return null; }
            XmlElem hit = null; int n = 0;
            string v = Variant(like);
            foreach (XmlElem c in candidates) if (Variant(c) == v) { hit = c; n++; }
            if (n == 1) return hit;
            if (n == 0 && candidates.Count == 1 && likeCount <= 1) return candidates[0];
            why = n > 1 ? "has " + n + " <" + like.Name + "> of the same feature and locale" : "has no <" + like.Name + "> of the same feature and locale";
            return null;
        }

        /// <summary>The element at a path below "under". Where a name repeats, the position of "like" among its same-named siblings is used.</summary>
        public static XmlElem Walk(XmlElem under, string path, XmlElem like, XmlElem likeUnder)
        {
            if (path.Length == 0) return under;
            string[] names = path.Split('/');
            var nth = new int[names.Length];
            if (like != null)
            {
                int d = names.Length - 1;
                for (XmlElem c = like; c != null && c != likeUnder && d >= 0; c = c.Parent, d--)
                {
                    int k = 0;
                    if (c.Parent != null)
                        foreach (XmlElem s in c.Parent.Children) { if (s == c) break; if (s.Name == c.Name) k++; }
                    nth[d] = k;
                }
            }
            XmlElem at = under;
            for (int i = 0; i < names.Length; i++)
            {
                XmlElem next = null; int k = 0;
                foreach (XmlElem c in at.Children)
                    if (c.Name == names[i]) { if (k == nth[i]) { next = c; break; } k++; }
                if (next == null) return null;
                at = next;
            }
            return at;
        }

        /// <summary>The value of a field below "under" (the first element of each name), or null.</summary>
        public static string ValueAt(XmlElem under, string field)
        {
            XmlElem e = Walk(under, FieldMapEntry.ElemOf(field), null, null);
            return e == null ? null : e.Attr(FieldMapEntry.AttrOf(field));
        }
    }

    public sealed class FieldMaps
    {
        public readonly List<EntityMap> Entities = new List<EntityMap>();

        static FieldMaps dflt;
        static readonly object gate = new object();

        /// <summary>The maps that ship inside the library.</summary>
        public static FieldMaps Default
        {
            get
            {
                lock (gate)
                {
                    if (dflt != null) return dflt;
                    var maps = new FieldMaps();
                    Assembly asm = typeof(FieldMaps).Assembly;
                    string[] names = asm.GetManifestResourceNames();
                    Array.Sort(names, StringComparer.Ordinal);
                    foreach (string n in names)
                    {
                        if (!n.EndsWith(".fieldmap.xml", StringComparison.Ordinal)) continue;
                        using (Stream s = asm.GetManifestResourceStream(n))
                        {
                            var buf = new byte[s.Length];
                            int got = 0;
                            while (got < buf.Length) { int r = s.Read(buf, got, buf.Length - got); if (r <= 0) break; got += r; }
                            maps.Add(buf, n);
                        }
                    }
                    return dflt = maps;
                }
            }
        }

        /// <summary>No map at all: nothing is synced, and only the same-table warning is left.</summary>
        public static FieldMaps None() { return new FieldMaps(); }

        public static FieldMaps Load(string path)
        {
            var maps = new FieldMaps();
            maps.Add(File.ReadAllBytes(path), path);
            return maps;
        }

        public static FieldMaps Parse(string text)
        {
            var maps = new FieldMaps();
            maps.Add(new UTF8Encoding(false).GetBytes(text), "field map");
            return maps;
        }

        public EntityMap Entity(string name)
        {
            foreach (EntityMap e in Entities) if (e.Name == name) return e;
            return null;
        }

        /// <summary>
        /// The entity an entry belongs to, and on which side; null when no map covers it. A map's server side
        /// (the per-id layout) stands for the gms-client too: "gms-client:Xml.m2d" is found as "server:Xml.m2d".
        /// </summary>
        public EntityMap Find(string archive, string entry, out bool clientSide)
        {
            long n;
            if (Profile.OfArchive(archive) == Profile.GmsClient) archive = Profile.Server.ArchiveIn(archive);
            foreach (EntityMap e in Entities)
            {
                if (string.Equals(e.Client.Archive, archive, StringComparison.OrdinalIgnoreCase) && e.Client.Match(entry, out n)) { clientSide = true; return e; }
                if (string.Equals(e.Server.Archive, archive, StringComparison.OrdinalIgnoreCase) && e.Server.Match(entry, out n)) { clientSide = false; return e; }
            }
            clientSide = false;
            return null;
        }

        static int Int(XmlElem e, string name)
        {
            int v;
            return int.TryParse(e.Attr(name) ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        static string Need(XmlElem e, string name, string label)
        {
            string v = e.Attr(name);
            if (string.IsNullOrEmpty(v)) throw new ModKitException(label + " line " + e.Line + ": <" + e.Name + "> needs " + name + "=\"...\"");
            return v;
        }

        static EntitySide ReadSide(XmlElem e, string label)
        {
            var s = new EntitySide
            {
                Archive = DataSet.NormalizeRef(Need(e, "archive", label)), EntryPattern = Need(e, "entry", label),
                Row = e.Attr("row"), Key = e.Attr("key") ?? "id", Under = Need(e, "under", label),
            };
            if (s.Row != null && s.Row.Length == 0) s.Row = null;
            s.Compile();
            return s;
        }

        public void Add(byte[] bytes, string label)
        {
            XmlDoc doc = XmlDoc.Parse(bytes, label);
            if (doc.Root == null || doc.Root.Name != "fieldmaps") throw new ModKitException(label + ": the root element is not <fieldmaps>");
            foreach (XmlElem en in doc.Root.Children)
            {
                if (en.Name != "entity") throw new ModKitException(label + " line " + en.Line + ": <" + en.Name + "> where <entity> is expected");
                var em = new EntityMap { Name = Need(en, "name", label), Sample = Int(en, "sample") };
                if (Entity(em.Name) != null) throw new ModKitException(label + ": entity " + em.Name + " is defined twice");
                foreach (XmlElem c in en.Children)
                {
                    switch (c.Name)
                    {
                        case "client": em.Client = ReadSide(c, label); break;
                        case "server": em.Server = ReadSide(c, label); break;
                        case "map":
                        case "candidate":
                        {
                            var e = new FieldMapEntry
                            {
                                Client = Need(c, "client", label), Server = Need(c, "server", label), Convert = c.Attr("convert") ?? "same",
                                Prefix = c.Attr("prefix") ?? "", Suffix = c.Attr("suffix") ?? "", Index = c.Attr("index"), Default = c.Attr("default"),
                                Candidate = c.Name == "candidate", Compared = Int(c, "compared"), Agree = Int(c, "agree"), Differ = Int(c, "differ"),
                                ClientAbsent = Int(c, "clientAbsent"), Loader = c.Attr("loader") ?? "", Confidence = c.Attr("confidence") ?? "",
                            };
                            if (e.Client.IndexOf('@') < 0 || e.Server.IndexOf('@') < 0) throw new ModKitException(label + " line " + c.Line + ": a field is written element@attribute");
                            switch (e.Convert)
                            {
                                case "same": case "bool": case "numlist": case "affix": break;
                                case "pick": if (e.Index == null) throw new ModKitException(label + " line " + c.Line + ": convert=\"pick\" needs index=\"...\""); break;
                                default: throw new ModKitException(label + " line " + c.Line + ": convert=\"" + e.Convert + "\" is not known");
                            }
                            em.Add(e);
                            break;
                        }
                        case "clientOnly": em.AddOnly(true, Need(c, "field", label)); break;
                        case "serverOnly": em.AddOnly(false, Need(c, "field", label)); break;
                        case "ref":
                            em.Refs.Add(new RefRule { Name = Need(c, "name", label), Archive = DataSet.NormalizeRef(Need(c, "archive", label)), Entries = Need(c, "entries", label),
                                Attribute = Need(c, "attribute", label), Key = c.Attr("key"), Of = Int(c, "of"), Hit = Int(c, "hit") });
                            break;
                        default: throw new ModKitException(label + " line " + c.Line + ": <" + c.Name + "> is not known in a field map");
                    }
                }
                if (em.Client == null || em.Server == null) throw new ModKitException(label + ": entity " + em.Name + " needs <client> and <server>");
                Entities.Add(em);
            }
        }
    }
}
