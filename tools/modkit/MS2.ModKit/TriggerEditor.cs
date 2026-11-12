// Edits of trigger scripts for a mod. A trigger file has no rows: each edit is an XmlEditor change of the current
// text of the script (the mod's file when it has one, else the shipped entry), byte-exact outside of the change,
// and the result is a whole-file entry of the mod: <entry archive name file="files/<root>/trigger/<xblock>/<file>">.
// The bytes live in Mod.Files until the mod is saved. A script edited back to the shipped bytes leaves the mod.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public sealed class TriggerEditor
    {
        public readonly DataSet Data;
        public readonly Mod Mod;

        public TriggerEditor(DataSet data, Mod mod)
        {
            if (data == null) throw new ArgumentNullException("data");
            if (mod == null) throw new ArgumentNullException("mod");
            Data = data; Mod = mod;
        }

        /// <summary>The path of the mod's file for a script: files/&lt;root&gt;/&lt;entry&gt; (root: server, gms-client, client).</summary>
        public static string FileOf(string archive, string entry)
        {
            int colon = archive.IndexOf(':');
            return "files/" + (colon > 0 ? archive.Substring(0, colon) : archive) + "/" + entry;
        }

        /// <summary>The mod's whole-file op of the script, or null.</summary>
        public EntryOp OpOf(string archive, string entry)
        {
            foreach (ModOp o in Mod.Ops)
            {
                var e = o as EntryOp;
                if (e != null && e.Archive == archive && string.Equals(e.Name, entry, StringComparison.OrdinalIgnoreCase)) return e;
            }
            return null;
        }

        public bool IsModded(string archive, string entry) { return OpOf(archive, entry) != null; }

        internal byte[] Shipped(string archive, string entry, out string name)
        {
            name = entry;
            if (!Data.Exists(archive)) return null;
            M2dArchive a = Data.Open(archive);
            M2dEntry e = a.Find(entry);
            if (e == null) return null;
            name = e.Name;
            return a.Read(e);
        }

        /// <summary>The bytes of the mod's op: its pending file, its file in the folder, or the copied entry; null for a delete.</summary>
        internal byte[] ModBytes(EntryOp op)
        {
            if (op.Delete) return null;
            if (op.File != null)
            {
                byte[] b;
                if (Mod.Files.TryGetValue(op.File.Replace('\\', '/'), out b)) return b;
                if (Mod.Dir == null) throw new ModKitException("mod " + Mod.Id + ": file=\"" + op.File + "\" and the mod has no folder");
                string full = Mod.PathIn(Mod.Dir, op.File);
                if (full == null) throw new ModKitException("mod " + Mod.Id + ": file=\"" + op.File + "\" is outside of the mod folder");
                if (!File.Exists(full)) throw new ModKitException("mod " + Mod.Id + ": file=\"" + op.File + "\": no such file in the mod folder");
                return File.ReadAllBytes(full);
            }
            string n;
            return op.Copy == null ? null : Shipped(op.Archive, op.Copy, out n);
        }

        /// <summary>The script as the mod has it (else as shipped); null when neither has it or the mod deletes it.</summary>
        public TriggerScript Current(string archive, string entry)
        {
            EntryOp op = OpOf(archive, entry);
            string name = entry;
            byte[] b;
            if (op != null) { b = ModBytes(op); name = op.Name; }
            else b = Shipped(archive, entry, out name);
            if (b == null) return null;
            return TriggerScript.Parse(archive, name, b);
        }

        /// <summary>The scripts the mod adds under trigger/&lt;xblock&gt;/ that the archive does not have, in mod order.</summary>
        public List<string> Added(string archive, string xblock)
        {
            var l = new List<string>();
            string prefix = Triggers.Folder + xblock.ToLowerInvariant() + "/";
            M2dArchive a = Data.Exists(archive) ? Data.Open(archive) : null;
            foreach (ModOp o in Mod.Ops)
            {
                var e = o as EntryOp;
                if (e == null || e.Delete || e.Archive != archive || !Triggers.IsScript(e.Name)) continue;
                if (!e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (a != null && a.Find(e.Name) != null) continue;
                l.Add(e.Name);
            }
            return l;
        }

        /// <summary>
        /// Puts the bytes into the mod as the script's whole-file entry; bytes equal to the shipped entry take the
        /// script out of the mod. Returns the script as it is now.
        /// </summary>
        public TriggerScript Store(string archive, string entry, byte[] bytes)
        {
            return TriggerScript.Parse(archive, Put(archive, entry, bytes), bytes);
        }

        /// <summary>Store without reading the bytes as a script; returns the entry's name (as shipped, else as given).</summary>
        internal string Put(string archive, string entry, byte[] bytes)
        {
            string name;
            byte[] shipped = Shipped(archive, entry, out name);
            EntryOp op = OpOf(archive, entry);
            if (shipped != null && M2dWriter.BytesEqual(shipped, bytes))
            {
                Revert(archive, entry);
                return name;
            }
            if (op == null)
            {
                op = new EntryOp { Archive = archive, Name = name };
                Mod.Ops.Add(op);
            }
            op.Copy = null; op.Delete = false;
            // a file="" outside of the mod folder (a hand-written mod.xml) is not written there: the edit goes to files/
            if (op.File == null || Mod.PathIn(Mod.Dir ?? ".", op.File) == null)
            {
                if (op.File != null) Mod.Files.Remove(op.File.Replace('\\', '/'));
                op.File = FileOf(archive, op.Name);
            }
            Mod.Files[op.File.Replace('\\', '/')] = bytes;
            return op.Name;
        }

        /// <summary>Takes the script out of the mod (an added script is gone then).</summary>
        public void Revert(string archive, string entry)
        {
            EntryOp op = OpOf(archive, entry);
            if (op == null) return;
            Mod.Ops.Remove(op);
            if (op.File != null) Mod.Files.Remove(op.File.Replace('\\', '/'));
        }

        // ---- edits; the node is one of the script that Current gave ----

        static void Check(TriggerScript s, TriggerNode n)
        {
            if (s == null || s.Doc == null) throw new ModKitException("the script could not be read; it cannot be edited here");
            if (n == null || n.Elem == null) throw new ModKitException("no element to edit");
        }

        /// <summary>Sets an attribute of a state, condition, action or transition; a null or empty value removes it.</summary>
        public TriggerScript SetAttr(TriggerScript s, TriggerNode n, string attr, string value)
        {
            Check(s, n);
            var ed = new XmlEditor(s.Doc);
            if (string.IsNullOrEmpty(value)) ed.RemoveAttr(n.Elem, attr);
            else ed.SetAttr(n.Elem, attr, value);
            return Store(s.Archive, s.Entry, ed.ApplyBytes());
        }

        /// <summary>Removes a state, condition, action, transition, group or on enter / on exit block.</summary>
        public TriggerScript Remove(TriggerScript s, TriggerNode n)
        {
            Check(s, n);
            if (n.Kind == TriggerKind.Script) throw new ModKitException("the script itself cannot be removed here; revert it instead");
            var ed = new XmlEditor(s.Doc);
            ed.Delete(n.Elem);
            return Store(s.Archive, s.Entry, ed.ApplyBytes());
        }

        /// <summary>
        /// Adds an element (literal XML, one element) as the last child of the parent, or after the anchor when
        /// one is given (a sibling under the same parent).
        /// </summary>
        public TriggerScript Insert(TriggerScript s, TriggerNode parent, TriggerNode after, string xml)
        {
            Check(s, parent);
            XmlDoc frag;
            try { frag = XmlDoc.Parse(new UTF8Encoding(false).GetBytes(xml), "the new element"); }
            catch (ModKitException e) { throw new ModKitException("the new element is not well-formed XML: " + e.Message); }
            if (frag.Root == null) throw new ModKitException("the new element is empty");
            var ed = new XmlEditor(s.Doc);
            string text = xml.Trim();
            string from = XmlEditor.CommonIndent(text);
            if (after != null)
            {
                if (after.Parent != parent) throw new ModKitException("the place to add after is not under the parent");
                ed.InsertAfter(after.Elem, text, from);
            }
            else ed.Append(parent.Elem, text, from);
            return Store(s.Archive, s.Entry, ed.ApplyBytes());
        }

        /// <summary>The XML of a new action, condition (with a transition when goTo is given) or state.</summary>
        public static string NewXml(TriggerKind kind, string name, string goTo)
        {
            string n = XmlDoc.EscapeAttr(name ?? "", '"');
            switch (kind)
            {
                case TriggerKind.Action: return "<action name=\"" + n + "\" />";
                case TriggerKind.Transition: return "<transition state=\"" + n + "\" />";
                case TriggerKind.Condition:
                    if (string.IsNullOrEmpty(goTo)) return "<condition name=\"" + n + "\" />";
                    return "<condition name=\"" + n + "\">\n    <transition state=\"" + XmlDoc.EscapeAttr(goTo, '"') + "\" />\n</condition>";
                case TriggerKind.State: return "<state name=\"" + n + "\">\n    <onEnter />\n</state>";
                case TriggerKind.OnEnter: return "<onEnter />";
                case TriggerKind.OnExit: return "<onExit />";
                default: throw new ModKitException("cannot make a new " + kind);
            }
        }

        /// <summary>Where a new element of the kind goes under the node: the node itself or, for an action under a state, its on enter block.</summary>
        public static bool CanHold(TriggerNode parent, TriggerKind kind)
        {
            if (parent == null) return false;
            switch (kind)
            {
                case TriggerKind.State: return parent.Kind == TriggerKind.Script;
                case TriggerKind.OnEnter: case TriggerKind.OnExit: return parent.Kind == TriggerKind.State && Child(parent, kind) == null;
                case TriggerKind.Condition: return parent.Kind == TriggerKind.State || parent.Kind == TriggerKind.Group;
                case TriggerKind.Action:
                case TriggerKind.Transition:
                    return parent.Kind == TriggerKind.OnEnter || parent.Kind == TriggerKind.OnExit || parent.Kind == TriggerKind.Condition || parent.Kind == TriggerKind.Group;
                default: return false;
            }
        }

        static TriggerNode Child(TriggerNode n, TriggerKind kind)
        {
            foreach (TriggerNode c in n.Children) if (c.Kind == kind) return c;
            return null;
        }

        /// <summary>
        /// Adds a new action, condition, transition, state, on enter or on exit under the parent (CanHold). A
        /// state goes after the last state, an on enter before the first other child, the rest at the end.
        /// </summary>
        public TriggerScript Add(TriggerScript s, TriggerNode parent, TriggerKind kind, string name, string goTo)
        {
            Check(s, parent);
            if (!CanHold(parent, kind)) throw new ModKitException("a " + kind.ToString().ToLowerInvariant() + " cannot go under " + parent.Label);
            if (kind != TriggerKind.OnEnter && kind != TriggerKind.OnExit && string.IsNullOrEmpty(name))
                throw new ModKitException("the new " + kind.ToString().ToLowerInvariant() + " needs a name");
            if (kind == TriggerKind.State && s.State(name) != null) throw new ModKitException("the script has a state " + name + " already");
            string xml = NewXml(kind, name, goTo);
            if (kind == TriggerKind.OnEnter && parent.Children.Count > 0)
            {
                var ed = new XmlEditor(s.Doc);
                ed.InsertBefore(parent.Children[0].Elem, xml, "");
                return Store(s.Archive, s.Entry, ed.ApplyBytes());
            }
            TriggerNode after = null;
            if (kind == TriggerKind.State) foreach (TriggerNode c in parent.Children) if (c.Kind == TriggerKind.State) after = c;
            if (kind == TriggerKind.Condition && parent.Kind == TriggerKind.State)
            {
                // conditions go before the on exit block, as the shipped scripts have them
                TriggerNode exit = Child(parent, TriggerKind.OnExit);
                if (exit != null)
                {
                    var ed = new XmlEditor(s.Doc);
                    ed.InsertBefore(exit.Elem, xml, "");
                    return Store(s.Archive, s.Entry, ed.ApplyBytes());
                }
            }
            return Insert(s, parent, after, xml);
        }

        /// <summary>The text of a new script: one state, the start state.</summary>
        public static string NewScriptText(string firstState)
        {
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ms2>\n    <state name=\"" + XmlDoc.EscapeAttr(firstState, '"') + "\">\n        <onEnter />\n    </state>\n</ms2>\n";
        }

        /// <summary>Adds a new script trigger/&lt;xblock&gt;/&lt;file&gt; to the mod. The file name gets .xml when it has none.</summary>
        public TriggerScript NewScript(string archive, string xblock, string file, string firstState)
        {
            if (string.IsNullOrEmpty(xblock)) throw new ModKitException("the map file (xblock) is not set");
            if (string.IsNullOrEmpty(file)) throw new ModKitException("the new script needs a file name");
            if (file.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0) throw new ModKitException("the file name \"" + file + "\" may not hold / \\ : * ? \" < > |");
            if (!file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) file += ".xml";
            string entry = Triggers.Folder + xblock.ToLowerInvariant() + "/" + file;
            string n;
            if (Shipped(archive, entry, out n) != null || IsModded(archive, entry)) throw new ModKitException(entry + " is there already");
            return Store(archive, entry, new UTF8Encoding(false).GetBytes(NewScriptText(string.IsNullOrEmpty(firstState) ? "Start" : firstState)));
        }
    }
}
