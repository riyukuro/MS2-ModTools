// The plan's checks of images and item icons (see ItemIcon.cs). For every image a mod puts into Resource/Image.m2d
// as a file: an error when the bytes are not a PNG image; a warning for an item icon (item/icon/...) that is not
// 60x60, the size of the shipped icons, and for an image that replaces a shipped one (every item, skill or npc that
// names it shows the new image). For every item row or per-id item file of a client the mods change: a warning for a
// property@slotIcon the mods set that names an image neither the client's image archive nor a mod has (the item
// shows an empty slot then). The server draws no icons and is not checked.
using System;
using System.Collections.Generic;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public static partial class Planner
    {
        static bool IsImageArchive(string archive)
        {
            return archive != null && archive.IndexOf(':') > 0 && string.Equals(DataSet.PathOf(archive).Replace('\\', '/'), Icons.ImageArchive, StringComparison.OrdinalIgnoreCase);
        }

        static void IconChecks(Run run)
        {
            Plan plan = run.Plan;
            // the image entries the mods put, by client root
            var put = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (Mod mod in plan.Mods)
                foreach (ModOp o in mod.Ops)
                {
                    var op = o as EntryOp;
                    if (op == null || op.Delete || !IsImageArchive(op.Archive)) continue;
                    string archive = DataSet.NormalizeRef(op.Archive), name = (op.Name ?? "").Replace('\\', '/');
                    HashSet<string> set;
                    if (!put.TryGetValue(DataSet.RootOf(archive), out set)) put[DataSet.RootOf(archive)] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(name);
                    if (!run.Data.Exists(archive)) continue;
                    M2dArchive arch = run.Open(mod, archive);
                    if (arch == null) continue;
                    if (arch.Find(name) != null)
                        plan.Msg(plan.Warnings, mod, archive, name, "replaces a shipped image: every item, skill or npc that names " + name + " shows the new one");
                    if (op.File == null) continue;
                    byte[] b;
                    try { b = new TriggerEditor(run.Data, mod).ModBytes(op); }
                    catch (ModKitException) { continue; }   // a missing file is the entry op's own error
                    if (b == null) continue;
                    string problem;
                    PngInfo png = PngInfo.Read(b, out problem);
                    if (png == null) { plan.Msg(plan.Errors, mod, archive, name, problem + ": the client shows nothing for it"); continue; }
                    if (name.StartsWith("item/icon/", StringComparison.OrdinalIgnoreCase) && (png.Width != ItemIcon.Size || png.Height != ItemIcon.Size))
                        plan.Msg(plan.Warnings, mod, archive, name, "an item icon of " + png + ": the shipped item icons are " + ItemIcon.Size + "x" + ItemIcon.Size + " and the client draws it scaled");
                }
            // the slotIcon values the mods set in a client's item rows and files
            foreach (EntryState st in run.Order)
            {
                if (st.DeletedBy != null || st.Archive == null) continue;
                Profile p = Profile.Find(DataSet.RootOf(st.Archive));
                if (p == null || p.IsServer || !string.Equals(DataSet.PathOf(st.Archive), "Xml.m2d", StringComparison.OrdinalIgnoreCase)) continue;
                string entry = (st.Name ?? "").Replace('\\', '/');
                bool bundle = entry.StartsWith("itemdata/", StringComparison.OrdinalIgnoreCase), file = entry.StartsWith("item/", StringComparison.OrdinalIgnoreCase);
                if (!bundle && !file) continue;
                byte[] now = st.Final ?? st.Base;
                if (now == null || (st.Original != null && M2dWriter.BytesEqual(now, st.Original))) continue;
                long fileId = 0;
                if (file && !long.TryParse(System.IO.Path.GetFileNameWithoutExtension(entry), out fileId)) continue;
                var was = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    if (st.Original != null) SlotIcons(XmlDoc.Parse(st.Original, st.Archive + " " + st.Name).Root, fileId, was, null);
                    var found = new List<KeyValuePair<long, string>>();
                    SlotIcons((st.FinalDoc ?? XmlDoc.Parse(now, st.Archive + " " + st.Name)).Root, fileId, null, found);
                    // only the client's own archive counts: it does not read another root's images
                    string imageArchive = p.Root + ":" + Icons.ImageArchive;
                    if (!run.Data.Exists(imageArchive)) continue;   // no image archive here: nothing to check against
                    M2dArchive images = run.Data.Open(imageArchive);
                    HashSet<string> mine;
                    put.TryGetValue(p.Root, out mine);
                    foreach (KeyValuePair<long, string> f in found)
                    {
                        if (was.Contains(f.Key + "\n" + f.Value)) continue;
                        string image = Icons.ItemIconEntry(f.Key, f.Value);
                        if (image == null || (mine != null && mine.Contains(image))) continue;
                        if (images.Find(image) != null) continue;
                        plan.Msg(plan.Warnings, st.BaseMod ?? IconMod(run, st), st.Archive, st.Name, "item " + f.Key + ": slotIcon names " + image + ", which neither " + imageArchive + " nor a mod has: the item shows an empty slot");
                    }
                }
                catch (ModKitException) { }   // a file that does not parse is an error of its own
            }
        }

        // the last mod that edits the entry (the warning is blamed on it)
        static Mod IconMod(Run run, EntryState st)
        {
            Mod own = null;
            foreach (Mod m in run.Plan.Mods)
                foreach (ModOp op in m.Ops)
                {
                    var ed = op as EditOp;
                    if (ed != null && string.Equals(DataSet.NormalizeRef(ed.Archive), st.Archive, StringComparison.OrdinalIgnoreCase)
                        && string.Equals((ed.Entry ?? "").Replace('\\', '/'), (st.Name ?? "").Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) own = m;
                }
            return own;
        }

        // the (item id, slotIcon) pairs under the element: an <item id> row's, or the file's own id
        static void SlotIcons(XmlElem e, long id, HashSet<string> keys, List<KeyValuePair<long, string>> list)
        {
            if (e == null) return;
            if (e.Name == "item")
            {
                long rowId;
                if (long.TryParse(e.Attr("id"), out rowId)) id = rowId;
            }
            if (e.Name == "property" && id != 0)
            {
                string v = e.Attr("slotIcon");
                if (v != null)
                {
                    if (keys != null) keys.Add(id + "\n" + v);
                    if (list != null) list.Add(new KeyValuePair<long, string>(id, v));
                }
            }
            foreach (XmlElem c in e.Children) SlotIcons(c, id, keys, list);
        }
    }
}
