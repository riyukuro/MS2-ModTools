// The trigger scripts of the maps: entries trigger/<xblock>/<name>.xml of an Xml.m2d, one folder per map file
// (the <xblock name> of the map's row in table/fielddata.xml or map/<id>.xml, in any letter case; the folder is
// written in lower case). The server reads its own Xml.m2d; the gms-client and kms-client carry copies. The
// scripts are read from the first archive of server, gms-client, client that has any.
//
// A script is <ms2> with <state name> children; a state has <onEnter> (actions, transitions), conditions (each
// with its actions and transitions, a group condition with a <group> of conditions) and <onExit> (actions).
// TriggerNode is that tree as the file has it (names not normalized; the lint, TriggerLint.cs, normalizes).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public enum TriggerKind { Script, State, OnEnter, OnExit, Condition, Group, Action, Transition, Other }

    public sealed class TriggerNode
    {
        public TriggerKind Kind;
        public XmlElem Elem;
        public TriggerNode Parent;
        public readonly List<TriggerNode> Children = new List<TriggerNode>();

        /// <summary>The name attribute (state, action, condition), the target state of a transition; may be null.</summary>
        public string Name;
        public int Line { get { return Elem == null ? 0 : Elem.Line; } }

        /// <summary>The states a transition, a condition or a state goes to (direct children and groups), in order.</summary>
        public List<string> GoesTo()
        {
            var l = new List<string>();
            Collect(this, l);
            return l;
        }

        static void Collect(TriggerNode n, List<string> l)
        {
            if (n.Kind == TriggerKind.Transition) { if (n.Name != null && !l.Contains(n.Name)) l.Add(n.Name); return; }
            foreach (TriggerNode c in n.Children) Collect(c, l);
        }

        /// <summary>The attributes other than name (a transition's state included only when it is not the name).</summary>
        public string Args()
        {
            if (Elem == null) return "";
            var sb = new StringBuilder();
            foreach (XmlAttr a in Elem.Attrs)
            {
                if (a.Name == "name" || (Kind == TriggerKind.Transition && a.Name == "state")) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(a.Name).Append("=\"").Append(a.Value).Append('"');
            }
            return sb.ToString();
        }

        /// <summary>One line for a tree: "state X", "action Y arg1=..", "→ Z".</summary>
        public string Label
        {
            get
            {
                string args = Args();
                switch (Kind)
                {
                    case TriggerKind.State: return "state " + (Name ?? "(no name)");
                    case TriggerKind.OnEnter: return "on enter";
                    case TriggerKind.OnExit: return "on exit";
                    case TriggerKind.Group: return "group";
                    case TriggerKind.Transition: return "→ " + (Name ?? "(no state)");
                    case TriggerKind.Condition:
                    case TriggerKind.Action:
                    {
                        string s = (Kind == TriggerKind.Condition ? "if " : "") + (Name ?? "(no name)") + (args.Length > 0 ? " " + args : "");
                        if (Kind == TriggerKind.Condition)
                        {
                            List<string> to = GoesTo();
                            if (to.Count > 0) s += "  → " + string.Join(", ", to.ToArray());
                        }
                        return s;
                    }
                    default: return "<" + (Elem == null ? "?" : Elem.Name) + ">" + (args.Length > 0 ? " " + args : "");
                }
            }
        }
    }

    public sealed class TriggerScript
    {
        /// <summary>The archive and the entry (trigger/&lt;xblock&gt;/&lt;file&gt;.xml, as the archive writes it).</summary>
        public string Archive, Entry;
        public byte[] Bytes;
        public XmlDoc Doc;
        /// <summary>Why the file could not be read; null when it could.</summary>
        public string ParseError;
        /// <summary>The script node: the states and whatever else stands under the root, in file order.</summary>
        public TriggerNode Root;

        public string XBlock { get { return Triggers.XBlockOf(Entry); } }
        public string FileName { get { return Entry.Substring(Entry.LastIndexOf('/') + 1); } }

        public List<TriggerNode> States
        {
            get
            {
                var l = new List<TriggerNode>();
                if (Root != null) foreach (TriggerNode n in Root.Children) if (n.Kind == TriggerKind.State) l.Add(n);
                return l;
            }
        }

        public TriggerNode State(string name)
        {
            foreach (TriggerNode n in States) if (n.Name == name) return n;
            return null;
        }

        /// <summary>The node of an element of the document, or null.</summary>
        public TriggerNode NodeAt(int line) { return NodeAt(line, null); }

        /// <summary>
        /// The node of the element named tag on the line (&lt;onExit&gt;&lt;transition/&gt;&lt;/onExit&gt; puts two on one
        /// line); the first node on the line when no tag is given or none has that name.
        /// </summary>
        public TriggerNode NodeAt(int line, string tag)
        {
            if (Root == null) return null;
            var on = new List<TriggerNode>();
            Find(Root, line, on);
            if (on.Count == 0) return null;
            if (tag != null)
                foreach (TriggerNode n in on)
                    if (string.Equals(n.Elem.Name, tag, StringComparison.OrdinalIgnoreCase)) return n;
            return on[0];
        }

        static void Find(TriggerNode n, int line, List<TriggerNode> on)
        {
            if (n.Elem != null && n.Kind != TriggerKind.Script && n.Line == line) on.Add(n);
            foreach (TriggerNode c in n.Children) Find(c, line, on);
        }

        public static TriggerScript Parse(string archive, string entry, byte[] bytes)
        {
            var s = new TriggerScript { Archive = archive, Entry = entry, Bytes = bytes };
            try { s.Doc = XmlDoc.Parse(bytes, archive + " " + entry); }
            catch (ModKitException e) { s.ParseError = e.Message; return s; }
            if (s.Doc.Root == null) { s.ParseError = "empty document"; return s; }
            s.Root = Build(s.Doc.Root, null, TriggerKind.Script);
            return s;
        }

        static TriggerNode Build(XmlElem e, TriggerNode parent, TriggerKind kind)
        {
            var n = new TriggerNode { Kind = kind, Elem = e, Parent = parent };
            n.Name = kind == TriggerKind.Transition ? e.Attr("state") : e.Attr("name");
            foreach (XmlElem c in e.Children) n.Children.Add(Build(c, n, KindOf(kind, c.Name)));
            return n;
        }

        static TriggerKind KindOf(TriggerKind parent, string tag)
        {
            switch (tag)
            {
                case "state": return parent == TriggerKind.Script ? TriggerKind.State : TriggerKind.Other;
                case "onEnter": return parent == TriggerKind.State ? TriggerKind.OnEnter : TriggerKind.Other;
                case "onExit": return parent == TriggerKind.State ? TriggerKind.OnExit : TriggerKind.Other;
                case "condition": return TriggerKind.Condition;
                case "group": return parent == TriggerKind.Condition ? TriggerKind.Group : TriggerKind.Other;
                case "action": return TriggerKind.Action;
                case "transition": return TriggerKind.Transition;
                default: return TriggerKind.Other;
            }
        }
    }

    /// <summary>One map file of a map with the scripts of its trigger folder.</summary>
    public sealed class TriggerFolder
    {
        /// <summary>The xblock name as the map row writes it; the feature and locale of its environment.</summary>
        public string XBlock, Feature = "", Locale = "";
        /// <summary>The script entries (trigger/&lt;xblock&gt;/&lt;file&gt;.xml) in archive order.</summary>
        public readonly List<string> Scripts = new List<string>();
    }

    public static class Triggers
    {
        public const string Folder = "trigger/";
        static readonly string[] Xmls = { EntityEditor.ServerXml, EntityEditor.GmsXml, EntityEditor.ClientXml };

        /// <summary>The xblock folder of a trigger entry (lower case as the archive has it), or null.</summary>
        public static string XBlockOf(string entry)
        {
            if (entry == null || !entry.StartsWith(Folder, StringComparison.OrdinalIgnoreCase)) return null;
            int slash = entry.IndexOf('/', Folder.Length);
            return slash < 0 ? null : entry.Substring(Folder.Length, slash - Folder.Length);
        }

        /// <summary>Is the entry a trigger script (trigger/&lt;xblock&gt;/&lt;file&gt;.xml, one folder deep)?</summary>
        public static bool IsScript(string entry)
        {
            string x = XBlockOf(entry);
            return x != null && x.Length > 0 && entry.IndexOf('/', Folder.Length + x.Length + 1) < 0
                && entry.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && entry.Length > Folder.Length + x.Length + 1 + 4;
        }

        /// <summary>The archive the scripts are read from: the first of server, gms-client, client that has any; null for none.</summary>
        public static string ArchiveOf(DataSet data)
        {
            foreach (string ar in Xmls)
            {
                if (!data.Exists(ar)) continue;
                M2dArchive a;
                try { a = data.Open(ar); } catch (ModKitException) { continue; }
                foreach (M2dEntry e in a.Entries) if (IsScript(e.Name)) return ar;
            }
            return null;
        }

        /// <summary>The Xml archives of the data set, the given one first.</summary>
        static List<string> Order(DataSet data, string first)
        {
            var l = new List<string>();
            if (first != null && data.Exists(first)) l.Add(first);
            foreach (string ar in Xmls) if (!l.Contains(ar) && data.Exists(ar)) l.Add(ar);
            return l;
        }

        /// <summary>
        /// The map files of a map (its row in the given archive, else in the other Xml archives) with the scripts
        /// of each one's folder in the given archive. An xblock named by several environments is listed once.
        /// </summary>
        public static List<TriggerFolder> FoldersOf(DataSet data, string archive, long mapId)
        {
            var list = new List<TriggerFolder>();
            foreach (string ar in Order(data, archive))
            {
                M2dArchive a;
                try { a = data.Open(ar); } catch (ModKitException) { continue; }
                XmlElem row = MapRow(a, ar, mapId);
                if (row == null) continue;
                foreach (XmlElem env in row.Children)
                {
                    if (env.Name != "environment") continue;
                    foreach (XmlElem x in env.Children)
                    {
                        string name = x.Name == "xblock" ? x.Attr("name") : null;
                        if (string.IsNullOrEmpty(name)) continue;
                        bool seen = false;
                        foreach (TriggerFolder f in list) if (string.Equals(f.XBlock, name, StringComparison.OrdinalIgnoreCase)) seen = true;
                        if (seen) continue;
                        list.Add(new TriggerFolder { XBlock = name, Feature = env.Attr("feature") ?? "", Locale = env.Attr("locale") ?? "" });
                    }
                }
                break;
            }
            if (list.Count == 0 || archive == null || !data.Exists(archive)) return list;
            M2dArchive sa;
            try { sa = data.Open(archive); } catch (ModKitException) { return list; }
            foreach (TriggerFolder f in list)
            {
                string prefix = Folder + f.XBlock.ToLowerInvariant() + "/";
                foreach (M2dEntry e in sa.Entries)
                    if (IsScript(e.Name) && e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) f.Scripts.Add(e.Name);
            }
            return list;
        }

        // the map's row: map/<8 digits>.xml, else <fieldData id> of table/fielddata.xml
        static XmlElem MapRow(M2dArchive a, string ar, long mapId)
        {
            string file = Planner.MapTableFolder + mapId.ToString("D8", CultureInfo.InvariantCulture) + ".xml";
            XmlDoc doc = ParseOrNull(a, ar, file);
            if (doc != null && doc.Root != null) return doc.Root;
            doc = ParseOrNull(a, ar, Planner.MapTableBundle);
            if (doc == null || doc.Root == null) return null;
            string id = mapId.ToString(CultureInfo.InvariantCulture);
            foreach (XmlElem r in doc.Root.Children)
                if (r.Name == "fieldData" && r.Attr("id") == id) return r;
            return null;
        }

        static XmlDoc ParseOrNull(M2dArchive a, string ar, string name)
        {
            M2dEntry e = a.Find(name);
            if (e == null) return null;
            try { return XmlDoc.Parse(a.Read(e), ar + " " + name); }
            catch (ModKitException) { return null; }
        }

        /// <summary>A shipped script; null when the archive has no such entry.</summary>
        public static TriggerScript Read(DataSet data, string archive, string entry)
        {
            M2dArchive a = data.Open(archive);
            M2dEntry e = a.Find(entry);
            return e == null ? null : TriggerScript.Parse(archive, e.Name, a.Read(e));
        }
    }
}
