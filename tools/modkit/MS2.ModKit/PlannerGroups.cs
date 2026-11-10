// The planner part for group entities (Groups.cs): shops and drop boxes. Each op of a group is turned into row ops
// (DoChange, DoDelete, DoAdd of Planner.cs) on every target where its row is found, so that the diff lines,
// claims and conflicts are the ones of any other row edit. A row that a target does not have gives a warning for
// that target and is skipped there. A new or cloned group is built whole (in memory) and added to every target that
// has a place for it.
//
// Targets:
//   shops      server:Server.m2d table/Server/shop_game.xml (stock) and shop_game_info.xml (header); server only.
//   drop boxes nested: server:Server.m2d table/Server/individualItemDrop_Final.xml (every field),
//                      client:Xml.m2d table/individualitemdrop_final.xml (the fields that file carries);
//              flat:   server:Xml.m2d and gms-client:Xml.m2d table/**individualitemdrop*.xml (the splits), with the
//                      names of the drop field map (DropMap.cs).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    /// <summary>How sn values of shop_game.xml are numbered, as measured on the shipped file.</summary>
    public enum SnScope { PerShop, File }

    /// <summary>A row the group planner wrote or changed; the checks run on these after the plan is built.</summary>
    internal sealed class GroupRow
    {
        internal Mod Mod;
        internal GroupOp Group;
        internal string Archive, Entry, Name;
        internal Dictionary<string, string> Attrs;
        internal int Line;
    }

    internal sealed class GroupRun
    {
        internal readonly List<GroupRow> Rows = new List<GroupRow>();
        internal readonly Dictionary<string, long> NextSn = new Dictionary<string, long>(StringComparer.Ordinal);
        internal readonly Dictionary<string, Mod> Created = new Dictionary<string, Mod>(StringComparer.Ordinal);
        internal readonly Dictionary<string, HashSet<string>[]> Carried = new Dictionary<string, HashSet<string>[]>(StringComparer.Ordinal);
        internal readonly Dictionary<string, XmlDoc> FinalDocs = new Dictionary<string, XmlDoc>(StringComparer.Ordinal);
        internal readonly List<KeyValuePair<Mod, GroupOp>> Touched = new List<KeyValuePair<Mod, GroupOp>>();
        internal SnScope? Scope;
    }

    public static partial class Planner
    {
        public const string ShopStockEntry = "table/Server/shop_game.xml", ShopInfoEntry = "table/Server/shop_game_info.xml";
        public const string ShopCategoryEntry = "table/shopcategory.xml";
        public const string ServerDropEntry = "table/Server/individualItemDrop_Final.xml", KmsDropEntry = "table/individualitemdrop_final.xml";
        /// <summary>The flat split a new drop box goes into when the mod does not name one: the generic, mixed split.</summary>
        public const string DefaultDropSplit = "table/individualitemdrop.xml";
        public const string ServerTables = "server:Server.m2d";

        static readonly string[] FlatOrder =
        {
            "individualDropBoxID", "dropGroup", "item", "minCount", "maxCount", "socketDataID", "smartDropRate", "enchantLevel",
            "PackageUIShowGrade", "showTooltip", "isApplySmartGenderDrop", "item2", "showIconOnGachaUI", "locale", "feature",
        };

        static string Inv(long v) { return v.ToString(CultureInfo.InvariantCulture); }

        // ---- sinks: where the edits of one target go ----

        /// <summary>Edits either through the planner (a shipped row) or into an in-memory fragment (a new or cloned group).</summary>
        abstract class Sink
        {
            internal Run Run;
            internal Mod Mod;
            internal GroupOp Group;
            internal string Archive, Entry;     // null: nothing is recorded for the checks
            internal abstract void Change(XmlElem e, List<AttrOp> attrs, int line);
            internal abstract void Delete(XmlElem e, int line);
            /// <summary>Adds the element text as the last child of its kind in parent.</summary>
            internal abstract void AddIn(XmlElem parent, string frag, int line, bool newId);
            internal abstract void AddAfter(XmlElem anchor, string frag, int line, bool newId);

            internal void Record(string name, Dictionary<string, string> attrs, int line)
            {
                if (Archive == null) return;
                Run.Groups.Rows.Add(new GroupRow { Mod = Mod, Group = Group, Archive = Archive, Entry = Entry, Name = name, Attrs = attrs, Line = line });
            }

            internal void RecordChange(XmlElem e, List<AttrOp> attrs, int line)
            {
                Dictionary<string, string> d = AttrsOf(e);
                foreach (AttrOp a in attrs) { if (a.Kind == AttrOpKind.Set) d[a.Name] = a.Value; else if (a.Kind == AttrOpKind.Unset) d.Remove(a.Name); }
                Record(e.Name, d, line);
            }

            internal void RecordTree(string frag, int line)
            {
                if (Archive == null) return;
                XmlDoc d = XmlDoc.ParseFragment(frag, "the new row");
                foreach (XmlElem t in d.Top) RecordElem(t, line);
            }

            void RecordElem(XmlElem e, int line)
            {
                Record(e.Name, AttrsOf(e), line);
                foreach (XmlElem c in e.Children) RecordElem(c, line);
            }
        }

        sealed class PlanSink : Sink
        {
            internal EntryState St;

            void Guard(int line, Action a)
            {
                try { a(); }
                catch (ModKitException x)
                {
                    string msg = x.Message;
                    if (St.Doc != null && msg.StartsWith(St.Doc.Label + ": ", StringComparison.Ordinal)) msg = msg.Substring(St.Doc.Label.Length + 2);
                    Run.Plan.Msg(Run.Plan.Errors, Mod, St.Archive, St.Name, Group + ": " + msg + " (mod.xml line " + line + ")");
                }
            }

            internal override void Change(XmlElem e, List<AttrOp> attrs, int line)
            {
                if (attrs.Count == 0) return;
                var r = new RowOp { Kind = RowOpKind.Change, At = RowPath.Canonical(e), Line = line, Sync = false };
                r.Attrs.AddRange(attrs);
                Guard(line, delegate { DoChange(Run, Mod, St, r); });
                RecordChange(e, attrs, line);
            }

            internal override void Delete(XmlElem e, int line)
            {
                var r = new RowOp { Kind = RowOpKind.Delete, At = RowPath.Canonical(e), Line = line, Sync = false };
                Guard(line, delegate { DoDelete(Run, Mod, St, r); });
            }

            internal override void AddIn(XmlElem parent, string frag, int line, bool newId)
            {
                frag = Layout(frag);
                var r = new RowOp { Kind = RowOpKind.Add, Xml = frag, In = RowPath.Canonical(parent), Line = line, Sync = false, Grouped = true, FromIndent = XmlEditor.CommonIndent(frag), NoNewId = !newId };
                Guard(line, delegate { DoAdd(Run, Mod, St, r); });
                RecordTree(frag, line);
            }

            internal override void AddAfter(XmlElem anchor, string frag, int line, bool newId)
            {
                frag = Layout(frag);
                var r = new RowOp { Kind = RowOpKind.Add, Xml = frag, After = RowPath.Canonical(anchor), Line = line, Sync = false, Grouped = true, FromIndent = XmlEditor.CommonIndent(frag), NoNewId = !newId };
                Guard(line, delegate { DoAdd(Run, Mod, St, r); });
                RecordTree(frag, line);
            }
        }

        sealed class FragSink : Sink
        {
            internal XmlEditor Ed;

            internal override void Change(XmlElem e, List<AttrOp> attrs, int line)
            {
                foreach (AttrOp a in attrs)
                {
                    if (a.Kind == AttrOpKind.Set) Ed.SetAttr(e, a.Name, a.Value);
                    else if (a.Kind == AttrOpKind.Unset) Ed.RemoveAttr(e, a.Name);
                }
                RecordChange(e, attrs, line);
            }

            internal override void Delete(XmlElem e, int line) { Ed.DropEditsWithin(e); Ed.Delete(e); }

            internal override void AddIn(XmlElem parent, string frag, int line, bool newId)
            {
                frag = Layout(frag);
                XmlElem last = null;
                string name = XmlDoc.ParseFragment(frag, "the new row").Root.Name;
                foreach (XmlElem c in parent.Children) if (c.Name == name) last = c;
                if (last != null) Ed.InsertAfter(last, frag, XmlEditor.CommonIndent(frag));
                else Ed.Append(parent, frag, XmlEditor.CommonIndent(frag));
                RecordTree(frag, line);
            }

            internal override void AddAfter(XmlElem anchor, string frag, int line, bool newId)
            {
                frag = Layout(frag);
                Ed.InsertAfter(anchor, frag, XmlEditor.CommonIndent(frag));
                RecordTree(frag, line);
            }
        }

        static PlanSink SinkOn(Run run, Mod mod, GroupOp g, EntryState st)
        {
            return new PlanSink { Run = run, Mod = mod, Group = g, St = st, Archive = st.Archive, Entry = st.Name };
        }

        /// <summary>A fragment to edit in memory, with the layout of the document it goes into.</summary>
        static FragSink FragOn(Run run, Mod mod, GroupOp g, string text, XmlDoc like, EntryState recordAs, out XmlDoc doc)
        {
            doc = XmlDoc.ParseFragment(text, "the " + g.Noun + " " + Inv(g.Id));
            if (like != null) { doc.IndentUnit = like.IndentUnit; doc.NewLine = like.NewLine; }
            return new FragSink
            {
                Run = run, Mod = mod, Group = g, Ed = new XmlEditor(doc),
                Archive = recordAs == null ? null : recordAs.Archive, Entry = recordAs == null ? null : recordAs.Name,
            };
        }

        /// <summary>A one-line fragment with nested elements, laid out one element per line (tab steps, made relative to indent "").
        /// A fragment that already spans lines is left as its author wrote it.</summary>
        internal static string Layout(string frag)
        {
            if (frag.IndexOf('\n') >= 0) return frag;
            XmlDoc d = XmlDoc.ParseFragment(frag, "the new rows");
            bool nested = false;
            foreach (XmlElem t in d.Top) if (t.Children.Count > 0) nested = true;
            if (!nested) return frag;
            var sb = new StringBuilder();
            for (int i = 0; i < d.Top.Count; i++) { if (i > 0) sb.Append('\n'); LayoutElem(sb, d.Top[i], 0); }
            return sb.ToString();
        }

        static void LayoutElem(StringBuilder sb, XmlElem e, int depth)
        {
            sb.Append('<').Append(e.Name);
            foreach (XmlAttr a in e.Attrs) sb.Append(' ').Append(a.Name).Append("=\"").Append(XmlDoc.EscapeAttr(a.Value, '"')).Append('"');
            if (e.Children.Count == 0) { sb.Append(" />"); return; }
            sb.Append('>');
            foreach (XmlElem c in e.Children)
            {
                sb.Append('\n').Append('\t', depth + 1);
                LayoutElem(sb, c, depth + 1);
            }
            sb.Append('\n').Append('\t', depth).Append("</").Append(e.Name).Append('>');
        }

        /// <summary>A new group's own element without its id attribute gets it as the first attribute, as in the shipped rows.</summary>
        static string IdFirst(string frag, string name, string id)
        {
            XmlDoc d = XmlDoc.ParseFragment(frag, "the new element");
            if (d.Top.Count != 1 || d.Root.Attr(name) != null) return frag;
            int at = frag.IndexOf('<') + 1 + d.Root.Name.Length;
            return frag.Substring(0, at) + " " + name + "=\"" + XmlDoc.EscapeAttr(id, '"') + "\"" + frag.Substring(at);
        }

        static Dictionary<string, string> AttrsOf(XmlElem e)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (XmlAttr a in e.Attrs) d[a.Name] = a.Value;
            return d;
        }

        static List<XmlElem> Children(XmlElem parent, string name, string key, string value)
        {
            var list = new List<XmlElem>();
            if (parent == null) return list;
            foreach (XmlElem c in parent.Children)
                if (c.Name == name && (key == null || (c.Attr(key) ?? "") == value)) list.Add(c);
            return list;
        }

        /// <summary>Text of one element with attribute ops applied (an override of a new row).</summary>
        static string Edited(string text, List<AttrOp> attrs, string label)
        {
            XmlDoc d = XmlDoc.ParseFragment(text.Trim(), label);
            if (d.Top.Count != 1) throw new ModKitException(label + ": <xml> holds " + d.Top.Count + " elements, one is needed");
            if (attrs == null || attrs.Count == 0) return d.Root.OuterText;
            var ed = new XmlEditor(d);
            foreach (AttrOp a in attrs)
            {
                if (a.Kind == AttrOpKind.Set) ed.SetAttr(d.Root, a.Name, a.Value);
                else if (a.Kind == AttrOpKind.Unset) ed.RemoveAttr(d.Root, a.Name);
            }
            return ed.Apply();
        }

        static string WithAttr(string text, string name, string value, string label)
        {
            var a = new List<AttrOp> { new AttrOp { Kind = AttrOpKind.Set, Name = name, Value = value } };
            return Edited(text, a, label);
        }

        static void Warn(Run run, Mod mod, string archive, string entry, GroupOp g, string text, int line)
        {
            run.Plan.Msg(run.Plan.Warnings, mod, archive, entry, g + ": " + text + " (mod.xml line " + line + ")");
        }

        static void Err(Run run, Mod mod, string archive, string entry, GroupOp g, string text, int line)
        {
            run.Plan.Msg(run.Plan.Errors, mod, archive, entry, g + ": " + text + " (mod.xml line " + line + ")");
        }

        /// <summary>The state of an entry, ready to edit; null (with a message) when it cannot be.</summary>
        static EntryState Editable(Run run, Mod mod, GroupOp g, string archive, string entry, bool quietIfMissing)
        {
            M2dArchive arch = run.Open(mod, archive);
            if (arch == null) return null;
            if (quietIfMissing && arch.Find(entry) == null) return null;
            EntryState st = run.State(arch, archive, entry);
            if (st.DeletedBy != null)
            {
                run.Plan.Msg(run.Plan.Conflicts, mod, archive, st.Name, g + ": edits an entry that " + Who(st.DeletedBy, mod) + " deletes");
                return null;
            }
            if (st.Base == null) { Err(run, mod, archive, entry, g, "the archive has no such entry", g.Line); return null; }
            if (!EnsureDoc(run.Plan, mod, st)) return null;
            return st;
        }

        static bool ClaimNew(Run run, Mod mod, GroupOp g, string where)
        {
            string key = g.Tag + "\n" + where + "\n" + Inv(g.Id);
            Mod other;
            if (run.Groups.Created.TryGetValue(key, out other))
            {
                run.Plan.Msg(run.Plan.Conflicts, mod, null, null, g + ": " + Who(other, mod) + " also makes a " + g.Noun + " with this id (" + where + ")");
                return false;
            }
            run.Groups.Created[key] = mod;
            return true;
        }

        static void DoGroup(Run run, Mod mod, GroupOp g)
        {
            run.Groups.Touched.Add(new KeyValuePair<Mod, GroupOp>(mod, g));
            if (g.Kind == GroupKind.Shop) DoShop(run, mod, g);
            else DoBox(run, mod, g);
        }

        // ================= shops =================

        /// <summary>sn of shop_game.xml: unique per shop when some sn value is used by two shops, else unique in the file.</summary>
        public static SnScope MeasureSnScope(XmlElem root, out int repeated, out int values)
        {
            var shopsOf = new Dictionary<string, string>(StringComparer.Ordinal);
            var rep = new HashSet<string>(StringComparer.Ordinal);
            foreach (XmlElem shop in Children(root, "shop", null, null))
            {
                string id = shop.Attr("shopID") ?? "";
                foreach (XmlElem it in shop.Children)
                {
                    string sn = it.Attr("sn");
                    if (sn == null) continue;
                    string had;
                    if (!shopsOf.TryGetValue(sn, out had)) shopsOf[sn] = id;
                    else if (had != id) rep.Add(sn);
                }
            }
            repeated = rep.Count; values = shopsOf.Count;
            return rep.Count > 0 ? SnScope.PerShop : SnScope.File;
        }

        static SnScope Scope(Run run, EntryState stock)
        {
            if (run.Groups.Scope.HasValue) return run.Groups.Scope.Value;
            int rep, vals;
            SnScope s = MeasureSnScope(stock.Doc.Root, out rep, out vals);
            run.Groups.Scope = s;
            run.Plan.Msg(run.Plan.Notes, null, stock.Archive, stock.Name, s == SnScope.PerShop
                ? "sn is numbered per shop (" + rep + " of " + vals + " sn values are used by more than one shop): a new stock row gets the next sn of its shop"
                : "sn is unique in the whole file: a new stock row gets the next sn of the file");
            return s;
        }

        static long MaxSn(IEnumerable<XmlElem> items)
        {
            long max = 0;
            foreach (XmlElem it in items)
            {
                long v;
                if (long.TryParse(it.Attr("sn") ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v > max) max = v;
            }
            return max;
        }

        static long NextSn(Run run, EntryState stock, XmlElem shipped, string shopId)
        {
            SnScope scope = Scope(run, stock);
            string key = stock.Archive + "\n" + stock.Name + "\n" + (scope == SnScope.File ? "*" : shopId);
            long last;
            if (!run.Groups.NextSn.TryGetValue(key, out last))
            {
                if (scope == SnScope.File)
                {
                    foreach (XmlElem shop in Children(stock.Doc.Root, "shop", null, null)) last = Math.Max(last, MaxSn(shop.Children));
                }
                else if (shipped != null) last = MaxSn(shipped.Children);
            }
            last++;
            run.Groups.NextSn[key] = last;
            return last;
        }

        static bool VariantMatches(XmlElem e, string feature, string locale)
        {
            return (feature == null || (e.Attr("feature") ?? "") == feature) && (locale == null || (e.Attr("locale") ?? "") == locale);
        }

        static void DoShop(Run run, Mod mod, GroupOp g)
        {
            Plan plan = run.Plan;
            if (!run.Data.Has(Profile.Server))
            {
                plan.Msg(plan.Notes, mod, null, null, g + ": shop stock and headers are server data, and the data set has no server; nothing is planned for it");
                return;
            }
            EntryState stock = Editable(run, mod, g, ServerTables, ShopStockEntry, false);
            EntryState info = Editable(run, mod, g, ServerTables, ShopInfoEntry, false);
            if (stock == null || info == null) return;
            string id = Inv(g.Id);
            List<XmlElem> shops = Children(stock.Doc.Root, "shop", "shopID", id), heads = Children(info.Doc.Root, "shop", "shopID", id);

            if (!g.New && !g.IsClone)
            {
                if (shops.Count == 0 && heads.Count == 0) { Warn(run, mod, stock.Archive, stock.Name, g, "this target has no such shop; its changes are skipped here", g.Line); return; }
                if (g.Header.Count > 0)
                {
                    PlanSink hs = SinkOn(run, mod, g, info);
                    bool any = false;
                    foreach (XmlElem h in heads)
                        if (VariantMatches(h, g.HeaderFeature, g.HeaderLocale)) { hs.Change(h, g.Header, g.Line); any = true; }
                    if (!any) Warn(run, mod, info.Archive, info.Name, g, "no header row" + (g.HeaderFeature != null || g.HeaderLocale != null ? " of that feature and locale" : "") + " here; the header changes are skipped", g.Line);
                }
                if (shops.Count > 1) Warn(run, mod, stock.Archive, stock.Name, g, "the shop has " + shops.Count + " stock elements here; the rows are looked up in all of them, new rows go into the last", g.Line);
                if (shops.Count == 0)
                {
                    foreach (GroupRowOp r in g.Rows) Warn(run, mod, stock.Archive, stock.Name, g, "no stock element here, " + r.Describe(g.Kind) + " is skipped", r.Line);
                    return;
                }
                ShopRows(run, mod, g, stock, SinkOn(run, mod, g, stock), shops, shops[shops.Count - 1], id);
                return;
            }

            // a new shop or a clone: the header row(s) and the stock element are written whole
            if (shops.Count > 0 || heads.Count > 0) { Err(run, mod, stock.Archive, stock.Name, g, "the shop id is already used on this target; a new or cloned shop needs a free id", g.Line); return; }
            if (!ClaimNew(run, mod, g, ServerTables)) return;
            string src = Inv(g.Clone);
            List<XmlElem> srcShops = g.IsClone ? Children(stock.Doc.Root, "shop", "shopID", src) : new List<XmlElem>();
            List<XmlElem> srcHeads = g.IsClone ? Children(info.Doc.Root, "shop", "shopID", src) : new List<XmlElem>();
            if (g.IsClone && srcShops.Count == 0 && srcHeads.Count == 0) { Warn(run, mod, stock.Archive, stock.Name, g, "the shop " + src + " to clone is not on this target; nothing is written here", g.Line); return; }

            PlanSink infoSink = SinkOn(run, mod, g, info), stockSink = SinkOn(run, mod, g, stock);
            try
            {
                var headTexts = new List<KeyValuePair<string, XmlElem>>();
                if (g.New) headTexts.Add(new KeyValuePair<string, XmlElem>(g.Xml, null));
                foreach (XmlElem h in srcHeads) headTexts.Add(new KeyValuePair<string, XmlElem>(info.Doc.IndentOf(h.Start) + h.OuterText, h));
                foreach (KeyValuePair<string, XmlElem> ht in headTexts)
                {
                    XmlDoc hd;
                    FragSink hf = FragOn(run, mod, g, IdFirst(ht.Key.Trim(), "shopID", id), info.Doc, null, out hd);
                    if (hd.Top.Count != 1 || hd.Root.Name != "shop") throw new ModKitException("the header row of a shop is one <shop .../> element (as in " + ShopInfoEntry + ")");
                    var set = new List<AttrOp> { new AttrOp { Kind = AttrOpKind.Set, Name = "shopID", Value = id } };
                    if (VariantMatches(hd.Root, g.HeaderFeature, g.HeaderLocale)) set.AddRange(g.Header);
                    hf.Change(hd.Root, set, g.Line);
                    infoSink.AddIn(info.Doc.Root, hf.Ed.Apply(), g.Line, true);
                }
                if (g.New && g.Header.Count > 0 && headTexts.Count == 0) Warn(run, mod, info.Archive, info.Name, g, "no header row to change", g.Line);
                if (srcShops.Count > 1) Warn(run, mod, stock.Archive, stock.Name, g, "the shop to clone has " + srcShops.Count + " stock elements here; the first is cloned", g.Line);

                string text = srcShops.Count > 0 ? srcShops[0].OuterText : "<shop shopID=\"" + id + "\"></shop>";
                XmlDoc sd;
                FragSink sf = FragOn(run, mod, g, text, stock.Doc, stock, out sd);
                sf.Change(sd.Root, new List<AttrOp> { new AttrOp { Kind = AttrOpKind.Set, Name = "shopID", Value = id } }, g.Line);
                // a clone numbers its new rows after the source's rows; a new shop from 1 (or the file's next sn)
                ShopRows(run, mod, g, stock, sf, new List<XmlElem> { sd.Root }, sd.Root, id);
                string made = sf.Ed.Apply();
                string from = srcShops.Count > 0 ? stock.Doc.IndentOf(srcShops[0].Start) : "";
                var r = new RowOp { Kind = RowOpKind.Add, Xml = made, In = "", Line = g.Line, Sync = false, Grouped = true, FromIndent = from };
                try { DoAdd(run, mod, stock, r); }
                catch (ModKitException x) { Err(run, mod, stock.Archive, stock.Name, g, x.Message, g.Line); }
            }
            catch (ModKitException x) { Err(run, mod, stock.Archive, stock.Name, g, x.Message, g.Line); }
        }

        /// <summary>The stock row ops of one shop, on the shipped rows (PlanSink) or on a new shop's element (FragSink).</summary>
        static void ShopRows(Run run, Mod mod, GroupOp g, EntryState stock, Sink sink, List<XmlElem> shops, XmlElem addTo, string shopId)
        {
            foreach (GroupRowOp r in g.Rows)
            {
                try
                {
                    if (r.Kind == GroupRowKind.Add)
                    {
                        string text = Edited(r.Xml, r.Attrs, "the new stock row");
                        XmlDoc fd = XmlDoc.ParseFragment(text, "the new stock row");
                        if (fd.Root.Name != "item") throw new ModKitException("a stock row is one <item .../> element");
                        string sn = r.Sn ?? fd.Root.Attr("sn");
                        if (sn == null)
                        {
                            sn = Inv(NextSn(run, stock, addTo, shopId));
                            text = WithAttr(text, "sn", sn, "the new stock row");
                            run.Plan.Msg(run.Plan.Notes, mod, stock.Archive, stock.Name, g + ": the new stock row of item " + (fd.Root.Attr("id") ?? "?") + " gets sn " + sn + " (mod.xml line " + r.Line + ")");
                        }
                        else
                        {
                            if (r.Sn != null && fd.Root.Attr("sn") != r.Sn) text = WithAttr(text, "sn", sn, "the new stock row");
                            foreach (XmlElem s in shops)
                                foreach (XmlElem it in s.Children)
                                    if (it.Attr("sn") == sn && VariantMatches(it, fd.Root.Attr("feature") ?? "", fd.Root.Attr("locale") ?? ""))
                                        Warn(run, mod, stock.Archive, stock.Name, g, "the new row's sn " + sn + " is already used in this shop (line " + it.Line + ")", r.Line);
                        }
                        sink.AddIn(addTo, text, r.Line, false);
                        continue;
                    }
                    var hits = new List<XmlElem>();
                    foreach (XmlElem s in shops)
                        foreach (XmlElem it in s.Children)
                            if (it.Name == "item" && it.Attr("sn") == r.Sn && VariantMatches(it, r.Feature, r.Locale)) hits.Add(it);
                    if (hits.Count == 0) { Warn(run, mod, stock.Archive, stock.Name, g, "no stock row " + r.Describe(g.Kind) + " here; skipped", r.Line); continue; }
                    if (hits.Count > 1)
                    {
                        Err(run, mod, stock.Archive, stock.Name, g, r.Describe(g.Kind) + " is " + hits.Count + " rows here (variants of one sn); give feature=\"...\" and locale=\"...\" to pick one", r.Line);
                        continue;
                    }
                    if (r.Kind == GroupRowKind.Set) sink.Change(hits[0], r.Attrs, r.Line);
                    else sink.Delete(hits[0], r.Line);
                }
                catch (ModKitException x) { Err(run, mod, stock.Archive, stock.Name, g, x.Message, r.Line); }
            }
        }

        // ================= drop boxes =================

        sealed class DropTarget
        {
            internal Profile Profile;
            internal string Archive;
            internal bool Flat;
            /// <summary>Nested only: every field is written (server); else only the fields the shipped file carries.</summary>
            internal bool Full;
            internal EntryState Nested;
            internal List<EntryState> Splits;       // flat: every split, the default split first
            internal readonly SortedDictionary<string, bool> Skipped = new SortedDictionary<string, bool>(StringComparer.Ordinal);
            internal string Title { get { return Profile.Title + " (" + (Flat ? "flat" : "nested") + ")"; } }
        }

        sealed class FlatRow { internal XmlElem Row; internal Sink Sink; internal EntryState St; }

        /// <summary>The flat drop splits of an archive: table/**individualitemdrop*.xml, the default split first, region folders last.</summary>
        public static List<string> FlatSplits(M2dArchive arch)
        {
            var top = new List<string>();
            var region = new List<string>();
            foreach (M2dEntry e in arch.Entries)
            {
                string n = e.Name.Replace('\\', '/');
                string low = n.ToLowerInvariant();
                if (!low.StartsWith("table/", StringComparison.Ordinal) || !low.EndsWith(".xml", StringComparison.Ordinal)) continue;
                string file = low.Substring(low.LastIndexOf('/') + 1);
                if (!file.StartsWith("individualitemdrop", StringComparison.Ordinal) || file.Contains("_final")) continue;
                if (low.IndexOf('/', 6) >= 0) region.Add(n); else top.Add(n);
            }
            top.Sort(delegate (string a, string b)
            {
                bool da = string.Equals(a, DefaultDropSplit, StringComparison.OrdinalIgnoreCase), db = string.Equals(b, DefaultDropSplit, StringComparison.OrdinalIgnoreCase);
                if (da != db) return da ? -1 : 1;
                return string.CompareOrdinal(a, b);
            });
            region.Sort(StringComparer.Ordinal);
            top.AddRange(region);
            return top;
        }

        /// <summary>A split in a region folder (table/na/..., table/kr/...): never chosen for a new box by the tool.</summary>
        public static bool IsRegionSplit(string entry)
        {
            string n = entry.Replace('\\', '/');
            return n.StartsWith("table/", StringComparison.OrdinalIgnoreCase) && n.IndexOf('/', 6) >= 0;
        }

        static List<DropTarget> DropTargets(Run run, Mod mod, GroupOp g)
        {
            var list = new List<DropTarget>();
            if (run.Data.Has(Profile.Server))
            {
                EntryState st = Editable(run, mod, g, ServerTables, ServerDropEntry, false);
                if (st != null) list.Add(new DropTarget { Profile = Profile.Server, Archive = ServerTables, Full = true, Nested = st });
            }
            if (run.Data.Has(Profile.KmsClient))
            {
                string a = Profile.KmsClient.ArchiveIn("x:Xml.m2d");
                EntryState st = Editable(run, mod, g, a, KmsDropEntry, false);
                if (st != null) list.Add(new DropTarget { Profile = Profile.KmsClient, Archive = a, Nested = st });
            }
            foreach (Profile p in new[] { Profile.Server, Profile.GmsClient })
            {
                if (!run.Data.Has(p)) continue;
                string a = p.ArchiveIn("x:Xml.m2d");
                M2dArchive arch = run.Open(mod, a);
                if (arch == null) continue;
                var t = new DropTarget { Profile = p, Archive = a, Flat = true, Splits = new List<EntryState>() };
                foreach (string n in FlatSplits(arch))
                {
                    EntryState st = Editable(run, mod, g, a, n, true);
                    if (st != null) t.Splits.Add(st);
                }
                if (t.Splits.Count == 0) { run.Plan.Msg(run.Plan.Warnings, mod, a, null, g + ": this archive has no table/individualitemdrop*.xml; the box is not written here"); continue; }
                list.Add(t);
            }
            return list;
        }

        /// <summary>Attribute names per level (box, group, row) that the shipped nested file uses.</summary>
        static HashSet<string>[] Carried(Run run, EntryState st)
        {
            string key = st.Archive + "\n" + st.Name;
            HashSet<string>[] c;
            if (run.Groups.Carried.TryGetValue(key, out c)) return c;
            c = new[] { new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal) };
            c[0].Add("dropBoxID"); c[1].Add("dropGroupID"); c[2].Add("itemID");
            foreach (XmlElem box in st.Doc.Root.Children)
            {
                foreach (XmlAttr a in box.Attrs) c[0].Add(a.Name);
                foreach (XmlElem grp in box.Children)
                {
                    foreach (XmlAttr a in grp.Attrs) c[1].Add(a.Name);
                    foreach (XmlElem v in grp.Children) foreach (XmlAttr a in v.Attrs) c[2].Add(a.Name);
                }
            }
            run.Groups.Carried[key] = c;
            return c;
        }

        /// <summary>The attribute ops a nested target writes: all on the server, the carried ones on the Korean client.</summary>
        static List<AttrOp> NestedAttrs(Run run, DropTarget t, int level, List<AttrOp> attrs)
        {
            if (t.Full) return attrs;
            HashSet<string> keep = Carried(run, t.Nested)[level];
            var list = new List<AttrOp>();
            foreach (AttrOp a in attrs)
                if (keep.Contains(a.Name)) list.Add(a); else t.Skipped[a.Name] = true;
            return list;
        }

        /// <summary>A nested element text with the attributes the target does not carry taken out (Korean client).</summary>
        static string NestedText(Run run, DropTarget t, string text)
        {
            if (t.Full) return text;
            HashSet<string>[] keep = Carried(run, t.Nested);
            XmlDoc d = XmlDoc.ParseFragment(text, "the new rows");
            d.IndentUnit = t.Nested.Doc.IndentUnit; d.NewLine = t.Nested.Doc.NewLine;
            var ed = new XmlEditor(d);
            StripLevel(t, ed, d.Root, keep);
            return ed.IsEmpty ? text : ed.Apply();
        }

        static void StripLevel(DropTarget t, XmlEditor ed, XmlElem e, HashSet<string>[] keep)
        {
            int level = e.Name == DropMap.BoxNested ? 0 : e.Name == DropMap.GroupNested ? 1 : 2;
            foreach (XmlAttr a in e.Attrs)
                if (!keep[level].Contains(a.Name)) { ed.RemoveAttr(e, a.Name); t.Skipped[a.Name] = true; }
            foreach (XmlElem c in e.Children) StripLevel(t, ed, c, keep);
        }

        /// <summary>Flat names and values for nested attribute ops of a level; the rest goes into Skipped.</summary>
        static List<AttrOp> FlatAttrs(DropTarget t, int level, List<AttrOp> attrs)
        {
            DropMap map = DropMap.Default;
            var list = new List<AttrOp>();
            foreach (AttrOp a in attrs)
            {
                DropField f = map.ByNested(level, a.Name);
                if (f == null || f.Level == 0) { t.Skipped[a.Name] = true; continue; }
                list.Add(new AttrOp { Kind = a.Kind, Name = f.Flat, Value = f.ToFlat(a.Value), Line = a.Line });
            }
            return list;
        }

        static void DoBox(Run run, Mod mod, GroupOp g)
        {
            List<DropTarget> targets = DropTargets(run, mod, g);
            if (targets.Count == 0)
            {
                run.Plan.Msg(run.Plan.Notes, mod, null, null, g + ": the data set has no drop table; nothing is planned for it");
                return;
            }
            // a new box is built once in the nested model with every field; each target takes its part of it
            string model = null;
            if (g.New)
            {
                try
                {
                    XmlDoc md;
                    FragSink ms = FragOn(run, mod, g, IdFirst(g.Xml.Trim(), "dropBoxID", Inv(g.Id)), null, null, out md);
                    if (md.Top.Count != 1 || md.Root.Name != DropMap.BoxNested) throw new ModKitException("a new drop box is one <dropBox> element with its <group> and <v> rows");
                    var set = new List<AttrOp> { new AttrOp { Kind = AttrOpKind.Set, Name = "dropBoxID", Value = Inv(g.Id) } };
                    set.AddRange(g.Header);
                    ms.Change(md.Root, set, g.Line);
                    var fake = new DropTarget { Full = true };
                    NestedRows(run, mod, g, fake, ms, new List<XmlElem> { md.Root }, null, null);
                    model = ms.Ed.Apply();
                }
                catch (ModKitException x) { Err(run, mod, null, null, g, x.Message, g.Line); return; }
            }
            foreach (DropTarget t in targets)
            {
                try
                {
                    if (t.Flat) FlatBox(run, mod, g, t, model);
                    else NestedBox(run, mod, g, t, model);
                }
                catch (ModKitException x) { Err(run, mod, t.Archive, null, g, x.Message, g.Line); }
                if (t.Skipped.Count > 0)
                {
                    var names = new List<string>(t.Skipped.Keys);
                    string entry = t.Flat ? null : t.Nested.Name;
                    run.Plan.Msg(run.Plan.Notes, mod, t.Archive, entry, g + ": " + names.Count + " field(s) have no place in this layout and are not written here: " + string.Join(", ", names.ToArray()));
                }
            }
        }

        // ---- nested ----

        static List<XmlElem> Boxes(XmlElem root, long id)
        {
            return Children(root, DropMap.BoxNested, "dropBoxID", Inv(id));
        }

        static void NestedBox(Run run, Mod mod, GroupOp g, DropTarget t, string model)
        {
            EntryState st = t.Nested;
            List<XmlElem> boxes = Boxes(st.Doc.Root, g.Id);
            if (!g.New && !g.IsClone)
            {
                if (boxes.Count == 0) { Warn(run, mod, st.Archive, st.Name, g, "this target has no such box; its changes are skipped here", g.Line); return; }
                PlanSink s = SinkOn(run, mod, g, st);
                if (g.Header.Count > 0) foreach (XmlElem b in boxes) s.Change(b, NestedAttrs(run, t, 0, g.Header), g.Line);
                NestedRows(run, mod, g, t, s, boxes, st.Archive, st.Name);
                return;
            }
            if (boxes.Count > 0) { Err(run, mod, st.Archive, st.Name, g, "the box id is already used on this target; a new or cloned box needs a free id", g.Line); return; }
            if (!ClaimNew(run, mod, g, st.Archive)) return;
            var texts = new List<KeyValuePair<string, string>>();       // text, the indent it was cut with
            if (g.New)
            {
                string nt = Layout(NestedText(run, t, model));
                texts.Add(new KeyValuePair<string, string>(nt, XmlEditor.CommonIndent(nt)));
            }
            else
            {
                List<XmlElem> src = Boxes(st.Doc.Root, g.Clone);
                if (src.Count == 0) { Warn(run, mod, st.Archive, st.Name, g, "the box " + Inv(g.Clone) + " to clone is not on this target; nothing is written here", g.Line); return; }
                XmlDoc cd;
                var whole = new StringBuilder("<clone>");
                foreach (XmlElem b in src) whole.Append(st.Doc.NewLine).Append(st.Doc.IndentOf(b.Start)).Append(b.OuterText);
                whole.Append(st.Doc.NewLine).Append("</clone>");
                FragSink cs = FragOn(run, mod, g, whole.ToString(), st.Doc, st, out cd);
                var set = new List<AttrOp> { new AttrOp { Kind = AttrOpKind.Set, Name = "dropBoxID", Value = Inv(g.Id) } };
                set.AddRange(NestedAttrs(run, t, 0, g.Header));
                foreach (XmlElem b in cd.Root.Children) cs.Change(b, set, g.Line);
                NestedRows(run, mod, g, t, cs, cd.Root.Children, st.Archive, st.Name);
                XmlDoc done = XmlDoc.ParseFragment(cs.Ed.Apply(), "the cloned box");
                string ind = st.Doc.IndentOf(src[0].Start);
                foreach (XmlElem b in done.Root.Children) texts.Add(new KeyValuePair<string, string>(b.OuterText, ind));
            }
            foreach (KeyValuePair<string, string> tx in texts)
            {
                var r = new RowOp { Kind = RowOpKind.Add, Xml = tx.Key, In = "", Line = g.Line, Sync = false, Grouped = true, FromIndent = tx.Value };
                DoAdd(run, mod, st, r);
                if (g.New) SinkOn(run, mod, g, st).RecordTree(tx.Key, g.Line);
            }
        }

        /// <summary>The n-th row (from 1) of an item in a group, over every element of the box.</summary>
        static XmlElem NthRow(List<XmlElem> boxes, string group, string item, int n)
        {
            int seen = 0;
            foreach (XmlElem b in boxes)
                foreach (XmlElem grp in Children(b, DropMap.GroupNested, "dropGroupID", group))
                    foreach (XmlElem v in Children(grp, DropMap.RowNested, "itemID", item))
                        if (++seen == n) return v;
            return null;
        }

        static void NestedRows(Run run, Mod mod, GroupOp g, DropTarget t, Sink sink, List<XmlElem> boxes, string archive, string entry)
        {
            foreach (GroupRowOp r in g.Rows)
            {
                try
                {
                    switch (r.Kind)
                    {
                        case GroupRowKind.Set:
                        {
                            XmlElem v = NthRow(boxes, r.Group, r.Item, r.N);
                            if (v == null) { Warn(run, mod, archive, entry, g, "no row " + r.Describe(g.Kind) + " here; skipped", r.Line); break; }
                            sink.Change(v, NestedAttrs(run, t, 2, r.Attrs), r.Line);
                            break;
                        }
                        case GroupRowKind.GroupSet:
                        {
                            bool any = false;
                            foreach (XmlElem b in boxes)
                                foreach (XmlElem grp in Children(b, DropMap.GroupNested, "dropGroupID", r.Group)) { sink.Change(grp, NestedAttrs(run, t, 1, r.Attrs), r.Line); any = true; }
                            if (!any) Warn(run, mod, archive, entry, g, "no group " + r.Group + " here; skipped", r.Line);
                            break;
                        }
                        case GroupRowKind.Remove:
                        {
                            if (r.Item != null)
                            {
                                XmlElem v = NthRow(boxes, r.Group, r.Item, r.N);
                                if (v == null) { Warn(run, mod, archive, entry, g, "no row " + r.Describe(g.Kind) + " here; skipped", r.Line); break; }
                                sink.Delete(v, r.Line);
                                break;
                            }
                            bool any = false;
                            foreach (XmlElem b in boxes)
                                foreach (XmlElem grp in Children(b, DropMap.GroupNested, "dropGroupID", r.Group)) { sink.Delete(grp, r.Line); any = true; }
                            if (!any) Warn(run, mod, archive, entry, g, "no group " + r.Group + " here; skipped", r.Line);
                            break;
                        }
                        default:
                        {
                            string text = Edited(r.Xml, r.Attrs, "the new drop row");
                            string name = XmlDoc.ParseFragment(text, "the new drop row").Root.Name;
                            if (r.Group != null)
                            {
                                if (name != DropMap.RowNested) throw new ModKitException("<add group=\"...\"> takes one <v .../> row; to add a whole group leave group=\"\" out");
                                XmlElem grp = null;
                                foreach (XmlElem b in boxes) foreach (XmlElem x in Children(b, DropMap.GroupNested, "dropGroupID", r.Group)) grp = x;
                                if (grp == null) { Warn(run, mod, archive, entry, g, "no group " + r.Group + " here; the new row is skipped", r.Line); break; }
                                sink.AddIn(grp, sink is FragSink ? text : NestedText(run, t, text), r.Line, false);
                            }
                            else
                            {
                                if (name != DropMap.GroupNested) throw new ModKitException("<add> without group=\"...\" takes one whole <group dropGroupID=\"...\"> element; a single row needs group=\"...\"");
                                string gid = XmlDoc.ParseFragment(text, "the new group").Root.Attr("dropGroupID");
                                foreach (XmlElem b in boxes)
                                    if (Children(b, DropMap.GroupNested, "dropGroupID", gid ?? "").Count > 0)
                                        Warn(run, mod, archive, entry, g, "the box has a group " + gid + " already; the new group is added beside it", r.Line);
                                sink.AddIn(boxes[boxes.Count - 1], sink is FragSink ? text : NestedText(run, t, text), r.Line, false);
                            }
                            break;
                        }
                    }
                }
                catch (ModKitException x) { Err(run, mod, archive, entry, g, x.Message, r.Line); }
            }
        }

        // ---- flat ----

        static List<FlatRow> FlatRows(Run run, Mod mod, GroupOp g, DropTarget t, long id)
        {
            var list = new List<FlatRow>();
            string ids = Inv(id);
            foreach (EntryState st in t.Splits)
            {
                PlanSink s = null;
                foreach (XmlElem e in Children(st.Doc.Root, DropMap.RowFlat, "individualDropBoxID", ids))
                {
                    if (s == null) s = SinkOn(run, mod, g, st);
                    list.Add(new FlatRow { Row = e, Sink = s, St = st });
                }
            }
            return list;
        }

        /// <summary>Does the split write socketDataID on its rows (most of its rows carry it)?</summary>
        static bool SplitUsesSocket(XmlElem root)
        {
            int with = 0, all = 0;
            foreach (XmlElem e in root.Children)
                if (e.Name == DropMap.RowFlat) { all++; if (e.Attr("socketDataID") != null) with++; }
            return all > 0 && with * 2 > all;
        }

        /// <summary>One flat row for a nested &lt;v&gt;: box and group fields, then the row's mapped fields in the shipped order.</summary>
        static string FlatRowText(DropTarget t, string boxId, string groupId, Dictionary<string, string> groupFlat, XmlElem v, bool socket)
        {
            DropMap map = DropMap.Default;
            var vals = new Dictionary<string, string>(StringComparer.Ordinal);
            vals["individualDropBoxID"] = boxId;
            vals["dropGroup"] = groupId;
            foreach (KeyValuePair<string, string> kv in groupFlat) vals[kv.Key] = kv.Value;
            foreach (XmlAttr a in v.Attrs)
            {
                DropField f = map.ByNested(2, a.Name);
                if (f == null) { t.Skipped[a.Name] = true; continue; }
                vals[f.Flat] = f.ToFlat(a.Value);
            }
            // nested rows without counts mean one item (an assumption; see the README)
            if (!vals.ContainsKey("minCount")) vals["minCount"] = "1";
            if (!vals.ContainsKey("maxCount")) vals["maxCount"] = vals["minCount"];
            if (socket && !vals.ContainsKey("socketDataID")) vals["socketDataID"] = "";
            var sb = new StringBuilder("<").Append(DropMap.RowFlat);
            var done = new HashSet<string>(StringComparer.Ordinal);
            foreach (string n in FlatOrder)
            {
                string val;
                if (!vals.TryGetValue(n, out val)) continue;
                sb.Append(' ').Append(n).Append("=\"").Append(XmlDoc.EscapeAttr(val, '"')).Append('"');
                done.Add(n);
            }
            foreach (KeyValuePair<string, string> kv in vals)
                if (!done.Contains(kv.Key)) sb.Append(' ').Append(kv.Key).Append("=\"").Append(XmlDoc.EscapeAttr(kv.Value, '"')).Append('"');
            return sb.Append(" />").ToString();
        }

        /// <summary>The flat fields of a nested group element (level 1, without its id); unmapped ones into Skipped.</summary>
        static Dictionary<string, string> GroupFlat(DropTarget t, XmlElem grp)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (XmlAttr a in grp.Attrs)
            {
                if (a.Name == "dropGroupID") continue;
                DropField f = DropMap.Default.ByNested(1, a.Name);
                if (f == null) { t.Skipped[a.Name] = true; continue; }
                d[f.Flat] = f.ToFlat(a.Value);
            }
            return d;
        }

        /// <summary>The group fields a flat row carries (copied to a new row of the same group).</summary>
        static Dictionary<string, string> GroupFlatOfRow(XmlElem row)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (row == null) return d;
            foreach (DropField f in DropMap.Default.Fields)
            {
                if (f.Level != 1 || f.Flat == "dropGroup") continue;
                string v = row.Attr(f.Flat);
                if (v != null) d[f.Flat] = v;
            }
            return d;
        }

        static void FlatBox(Run run, Mod mod, GroupOp g, DropTarget t, string model)
        {
            List<FlatRow> rows = FlatRows(run, mod, g, t, g.Id);
            if (!g.New && !g.IsClone)
            {
                if (rows.Count == 0) { Warn(run, mod, t.Archive, null, g, "this target has no rows of the box; its changes are skipped here", g.Line); return; }
                foreach (AttrOp a in g.Header) t.Skipped[a.Name] = true;
                FlatOps(run, mod, g, t, rows, Inv(g.Id));
                return;
            }
            if (rows.Count > 0) { Err(run, mod, t.Archive, rows[0].St.Name, g, "the box id is already used on this target; a new or cloned box needs a free id", g.Line); return; }
            if (!ClaimNew(run, mod, g, t.Archive)) return;

            List<FlatRow> src = g.IsClone ? FlatRows(run, mod, g, t, g.Clone) : null;
            if (g.IsClone && src.Count == 0) { Warn(run, mod, t.Archive, null, g, "the box " + Inv(g.Clone) + " to clone has no rows on this target; nothing is written here", g.Line); return; }

            // the split: the mod's, else the clone source's (when not a region folder), else the default split
            EntryState split = null;
            string why;
            if (g.Split != null)
            {
                foreach (EntryState st in t.Splits) if (string.Equals(st.Name, g.Split, StringComparison.OrdinalIgnoreCase)) split = st;
                if (split == null) { Warn(run, mod, t.Archive, g.Split, g, "this target has no split " + g.Split + "; the box is not written here (a split file is never created)", g.Line); return; }
                why = "the mod names it";
            }
            else if (src != null && !IsRegionSplit(src[0].St.Name)) { split = src[0].St; why = "it holds the box " + Inv(g.Clone) + " that is cloned"; }
            else
            {
                foreach (EntryState st in t.Splits) if (string.Equals(st.Name, DefaultDropSplit, StringComparison.OrdinalIgnoreCase)) split = st;
                if (split == null) { Warn(run, mod, t.Archive, DefaultDropSplit, g, "this target has no " + DefaultDropSplit + "; name a split with split=\"...\"", g.Line); return; }
                why = src != null ? "the box to clone is in a region folder, which the tool does not add to" : "it is the general split (the other splits are themed: charge, event, gacha, pet, quest)";
            }
            run.Plan.Msg(run.Plan.Notes, mod, t.Archive, split.Name, g + ": the box goes into " + split.Name + ": " + why);

            var texts = new List<string>();
            bool socket = SplitUsesSocket(split.Doc.Root);
            if (g.New)
            {
                XmlDoc md = XmlDoc.ParseFragment(model, "the new box");
                foreach (XmlAttr a in md.Root.Attrs) if (a.Name != "dropBoxID") t.Skipped[a.Name] = true;
                foreach (XmlElem grp in md.Root.Children)
                {
                    Dictionary<string, string> gf = GroupFlat(t, grp);
                    foreach (XmlElem v in grp.Children) texts.Add(FlatRowText(t, Inv(g.Id), grp.Attr("dropGroupID") ?? "", gf, v, socket));
                }
                if (texts.Count == 0) Warn(run, mod, t.Archive, split.Name, g, "the new box has no rows, so the flat layout has nothing to hold it", g.Line);
            }
            else
            {
                var whole = new StringBuilder("<clone>");
                foreach (FlatRow fr in src) whole.Append(split.Doc.NewLine).Append(fr.St.Doc.IndentOf(fr.Row.Start)).Append(fr.Row.OuterText);
                whole.Append(split.Doc.NewLine).Append("</clone>");
                XmlDoc cd;
                FragSink cs = FragOn(run, mod, g, whole.ToString(), split.Doc, split, out cd);
                var crow = new List<FlatRow>();
                var set = new List<AttrOp> { new AttrOp { Kind = AttrOpKind.Set, Name = "individualDropBoxID", Value = Inv(g.Id) } };
                foreach (XmlElem e in cd.Root.Children) { cs.Change(e, set, g.Line); crow.Add(new FlatRow { Row = e, Sink = cs, St = split }); }
                foreach (AttrOp a in g.Header) t.Skipped[a.Name] = true;
                FlatOps(run, mod, g, t, crow, Inv(g.Id));
                XmlDoc done = XmlDoc.ParseFragment(cs.Ed.Apply(), "the cloned box");
                foreach (XmlElem e in done.Root.Children) texts.Add(e.OuterText);
            }
            PlanSink s = SinkOn(run, mod, g, split);
            foreach (string tx in texts)
            {
                var r = new RowOp { Kind = RowOpKind.Add, Xml = tx, In = "", Line = g.Line, Sync = false, Grouped = true, FromIndent = "" };
                DoAdd(run, mod, split, r);
                if (g.New) s.RecordTree(tx, g.Line);
            }
        }

        static FlatRow NthFlat(List<FlatRow> rows, string group, string item, int n)
        {
            int seen = 0;
            foreach (FlatRow fr in rows)
                if ((fr.Row.Attr("dropGroup") ?? "") == group && (fr.Row.Attr("item") ?? "") == item && ++seen == n) return fr;
            return null;
        }

        /// <summary>A flat row carries its group's fields, so a new row also gets the mod's group changes (in any op order).</summary>
        static void WithGroupSets(DropTarget t, GroupOp g, string group, Dictionary<string, string> gf)
        {
            foreach (GroupRowOp o in g.Rows)
            {
                if (o.Kind != GroupRowKind.GroupSet || o.Group != group) continue;
                foreach (AttrOp a in FlatAttrs(t, 1, o.Attrs))
                {
                    if (a.Kind == AttrOpKind.Set) gf[a.Name] = a.Value;
                    else if (a.Kind == AttrOpKind.Unset) gf.Remove(a.Name);
                }
            }
        }

        static void FlatOps(Run run, Mod mod, GroupOp g, DropTarget t, List<FlatRow> rows, string boxId)
        {
            string entry0 = rows.Count > 0 && rows[0].St != null ? rows[0].St.Name : null;
            foreach (GroupRowOp r in g.Rows)
            {
                try
                {
                    switch (r.Kind)
                    {
                        case GroupRowKind.Set:
                        {
                            FlatRow fr = NthFlat(rows, r.Group, r.Item, r.N);
                            if (fr == null) { Warn(run, mod, t.Archive, entry0, g, "no row " + r.Describe(g.Kind) + " here; skipped", r.Line); break; }
                            fr.Sink.Change(fr.Row, FlatAttrs(t, 2, r.Attrs), r.Line);
                            break;
                        }
                        case GroupRowKind.GroupSet:
                        {
                            List<AttrOp> attrs = FlatAttrs(t, 1, r.Attrs);
                            bool any = false;
                            foreach (FlatRow fr in rows) if ((fr.Row.Attr("dropGroup") ?? "") == r.Group) { fr.Sink.Change(fr.Row, attrs, r.Line); any = true; }
                            if (!any) Warn(run, mod, t.Archive, entry0, g, "no group " + r.Group + " here; skipped", r.Line);
                            break;
                        }
                        case GroupRowKind.Remove:
                        {
                            if (r.Item != null)
                            {
                                FlatRow fr = NthFlat(rows, r.Group, r.Item, r.N);
                                if (fr == null) { Warn(run, mod, t.Archive, entry0, g, "no row " + r.Describe(g.Kind) + " here; skipped", r.Line); break; }
                                fr.Sink.Delete(fr.Row, r.Line);
                                break;
                            }
                            bool any = false;
                            foreach (FlatRow fr in rows) if ((fr.Row.Attr("dropGroup") ?? "") == r.Group) { fr.Sink.Delete(fr.Row, r.Line); any = true; }
                            if (!any) Warn(run, mod, t.Archive, entry0, g, "no group " + r.Group + " here; skipped", r.Line);
                            break;
                        }
                        default:
                        {
                            if (rows.Count == 0) break;
                            string text = Edited(r.Xml, r.Attrs, "the new drop row");
                            XmlElem made = XmlDoc.ParseFragment(text, "the new drop row").Root;
                            var add = new List<KeyValuePair<string, XmlElem>>();      // group id, row
                            Dictionary<string, string> gf;
                            if (r.Group != null)
                            {
                                FlatRow last = null;
                                foreach (FlatRow fr in rows) if ((fr.Row.Attr("dropGroup") ?? "") == r.Group) last = fr;
                                if (last == null) { Warn(run, mod, t.Archive, entry0, g, "no group " + r.Group + " here; the new row is skipped", r.Line); break; }
                                gf = GroupFlatOfRow(last.Row);
                                WithGroupSets(t, g, r.Group, gf);
                                string row = FlatRowText(t, boxId, r.Group, gf, made, SplitUsesSocket(last.St.Doc.Root));
                                last.Sink.AddAfter(last.Row, row, r.Line, false);
                            }
                            else
                            {
                                FlatRow last = rows[rows.Count - 1];
                                string gid = made.Attr("dropGroupID") ?? "";
                                foreach (FlatRow fr in rows)
                                    if ((fr.Row.Attr("dropGroup") ?? "") == gid) { Warn(run, mod, t.Archive, fr.St.Name, g, "the box has a group " + gid + " already; the new group is added beside it", r.Line); break; }
                                gf = GroupFlat(t, made);
                                WithGroupSets(t, g, gid, gf);
                                bool socket = SplitUsesSocket(last.St.Doc.Root);
                                foreach (XmlElem v in made.Children)
                                    last.Sink.AddAfter(last.Row, FlatRowText(t, boxId, gid, gf, v, socket), r.Line, false);
                            }
                            break;
                        }
                    }
                }
                catch (ModKitException x) { Err(run, mod, t.Archive, entry0, g, x.Message, r.Line); }
            }
        }

        // ================= checks =================

        static XmlDoc FinalDoc(Run run, string archive, string entry)
        {
            string key = archive + "\n" + entry.ToLowerInvariant();
            XmlDoc d;
            if (run.Groups.FinalDocs.TryGetValue(key, out d)) return d;
            d = null;
            try
            {
                M2dArchive arch = run.Data.Open(archive);
                if (arch.Find(entry) != null)
                {
                    EntryState st = run.State(arch, archive, entry);
                    byte[] b = st.DeletedBy != null ? null : st.Final ?? st.Base;
                    if (b != null) d = XmlDoc.Parse(b, archive + " " + entry);
                }
            }
            catch (ModKitException) { d = null; }
            run.Groups.FinalDocs[key] = d;
            return d;
        }

        /// <summary>The item ids of a target, as shipped plus the items the mods add.</summary>
        static HashSet<long> Items(Run run, Profile p)
        {
            string key = "groupcheck-items\n" + p.Root;
            object o;
            if (run.Data.Cache.TryGetValue(key + "\n" + run.Plan.GetHashCode(), out o)) return (HashSet<long>)o;
            var place = p == Profile.KmsClient
                ? new IdPlace { Archive = p.ArchiveIn("x:Xml.m2d"), Entry = "itemdata/*.xml", Rows = "item", Key = "id" }
                : new IdPlace { Archive = p.ArchiveIn("x:Xml.m2d"), Entry = "item/**/{id}.xml" };
            var set = new HashSet<long>();
            try { foreach (long id in IdSpaces.UsedIds(run.Data, place, run.Options.Progress)) set.Add(id); }
            catch (ModKitException) { return null; }
            foreach (NewId n in run.Plan.NewIds)
            {
                string text;
                long id;
                if (IdSpaces.Match(place, n, out text) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id)) set.Add(id);
            }
            run.Data.Cache[key + "\n" + run.Plan.GetHashCode()] = set;
            return set;
        }

        static string ItemNum(string v)
        {
            if (v == null) return null;
            int c = v.IndexOf(':');
            return (c >= 0 ? v.Substring(0, c) : v).Trim();
        }

        static void CheckItem(Run run, GroupRow row, string what, string value, List<Profile> on)
        {
            string num = ItemNum(value);
            if (string.IsNullOrEmpty(num) || num == "0") return;
            long id;
            if (!long.TryParse(num, NumberStyles.None, CultureInfo.InvariantCulture, out id))
            {
                run.Plan.Msg(run.Plan.Warnings, row.Mod, row.Archive, row.Entry, row.Group + ": " + what + " \"" + value + "\" is not an item id (mod.xml line " + row.Line + ")");
                return;
            }
            foreach (Profile p in on)
            {
                HashSet<long> items = Items(run, p);
                if (items != null && !items.Contains(id))
                    run.Plan.Msg(run.Plan.Warnings, row.Mod, row.Archive, row.Entry, row.Group + ": " + what + " " + id + " is not an item of the " + p.Title + " (mod.xml line " + row.Line + ")");
            }
        }

        static void GroupChecks(Run run)
        {
            if (run.Groups.Touched.Count == 0 && !TouchesNpcs(run)) return;
            Plan plan = run.Plan;
            var clients = new List<Profile>();
            foreach (Profile p in run.Data.Profiles) if (!p.IsServer) clients.Add(p);
            List<Profile> all = run.Data.Profiles;

            foreach (GroupRow row in run.Groups.Rows)
            {
                string v;
                Profile target = Profile.OfArchive(row.Archive);
                if (row.Group.Kind == GroupKind.Shop && row.Name == "item")
                {
                    string sn = row.Attrs.TryGetValue("sn", out v) ? v : "?";
                    if (row.Attrs.TryGetValue("id", out v)) CheckItem(run, row, "sn " + sn + ": item", v, all);
                    string pt = row.Attrs.TryGetValue("paymentType", out v) ? v : "0";
                    int pti;
                    if (!int.TryParse(pt, NumberStyles.None, CultureInfo.InvariantCulture, out pti) || pti > 14)
                        plan.Msg(plan.Warnings, row.Mod, row.Archive, row.Entry, row.Group + ": sn " + sn + ": paymentType \"" + pt + "\" is not a known currency (0 to 14) (mod.xml line " + row.Line + ")");
                    else if (pti == 1)
                    {
                        string pay = row.Attrs.TryGetValue("paymentItemID", out v) ? v : null;
                        if (string.IsNullOrEmpty(ItemNum(pay)) || ItemNum(pay) == "0")
                            plan.Msg(plan.Warnings, row.Mod, row.Archive, row.Entry, row.Group + ": sn " + sn + ": paymentType 1 is paid with an item, and paymentItemID is not set (mod.xml line " + row.Line + ")");
                        else CheckItem(run, row, "sn " + sn + ": the payment item", pay, all);
                    }
                    if (row.Attrs.TryGetValue("category", out v)) CheckStockCategory(run, row, sn, v, clients);
                }
                else if (row.Group.Kind == GroupKind.Shop && row.Name == "shop" && row.Entry == ShopInfoEntry)
                {
                    if (row.Attrs.TryGetValue("categoryID", out v))
                        foreach (Profile p in clients)
                        {
                            XmlDoc cat = FinalDoc(run, p.ArchiveIn("x:Xml.m2d"), ShopCategoryEntry);
                            if (cat == null) continue;
                            if (Children(cat.Root, "category", "id", v).Count == 0)
                                plan.Msg(plan.Warnings, row.Mod, row.Archive, row.Entry, row.Group + ": categoryID " + v + " is not a category of the " + p.Title + "'s " + ShopCategoryEntry + " (mod.xml line " + row.Line + ")");
                        }
                }
                else if (row.Group.Kind == GroupKind.DropBox && target != null)
                {
                    bool flat = row.Name == DropMap.RowFlat;
                    if (row.Name == DropMap.RowNested || flat)
                    {
                        var on = new List<Profile> { target };
                        foreach (string a in flat ? new[] { "item", "item2" } : new[] { "itemID", "itemID2" })
                            if (row.Attrs.TryGetValue(a, out v)) CheckItem(run, row, "the drop item (" + a + ")", v, on);
                    }
                    if (row.Attrs.TryGetValue("isApplySmartGenderDrop", out v))
                    {
                        bool ok = flat ? (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)) : (v == "0" || v == "1");
                        if (!ok)
                            plan.Msg(plan.Warnings, row.Mod, row.Archive, row.Entry, row.Group + ": isApplySmartGenderDrop is \"" + v + "\"; this layout writes " + (flat ? "True or False" : "1 or 0") + " (mod.xml line " + row.Line + ")");
                    }
                }
            }

            // the boxes as the mods leave them: empty groups, groups whose weights are all 0
            foreach (KeyValuePair<Mod, GroupOp> kv in run.Groups.Touched)
            {
                GroupOp g = kv.Value;
                if (g.Kind != GroupKind.DropBox) continue;
                foreach (Profile p in run.Data.Profiles)
                {
                    if (p == Profile.GmsClient) continue;
                    string archive = p == Profile.Server ? ServerTables : p.ArchiveIn("x:Xml.m2d");
                    string entry = p == Profile.Server ? ServerDropEntry : KmsDropEntry;
                    XmlDoc d = FinalDoc(run, archive, entry);
                    if (d == null) continue;
                    List<XmlElem> boxes = Boxes(d.Root, g.Id);
                    if (boxes.Count == 0) continue;
                    int groups = 0;
                    foreach (XmlElem b in boxes)
                        foreach (XmlElem grp in Children(b, DropMap.GroupNested, null, null))
                        {
                            groups++;
                            string gid = grp.Attr("dropGroupID") ?? "?";
                            var rows = Children(grp, DropMap.RowNested, null, null);
                            if (rows.Count == 0) { plan.Msg(plan.Warnings, kv.Key, archive, entry, g + ": group " + gid + " has no rows after the mod (mod.xml line " + g.Line + ")"); continue; }
                            if (p != Profile.Server) continue;
                            bool allZero = true;
                            foreach (XmlElem v in rows)
                            {
                                double w;
                                string ws = v.Attr("weight");
                                if (ws == null || !double.TryParse(ws, NumberStyles.Float, CultureInfo.InvariantCulture, out w) || w != 0) { allZero = false; break; }
                            }
                            if (allZero) plan.Msg(plan.Warnings, kv.Key, archive, entry, g + ": every row of group " + gid + " has weight 0, so the group cannot drop anything (mod.xml line " + g.Line + ")");
                        }
                    if (groups == 0) plan.Msg(plan.Warnings, kv.Key, archive, entry, g + ": the box has no groups after the mod (mod.xml line " + g.Line + ")");
                }
            }
            CheckNpcRefs(run);
        }

        static void CheckStockCategory(Run run, GroupRow row, string sn, string category, List<Profile> clients)
        {
            // the tabs of the shop's category list the stock categories a client shows
            XmlDoc info = FinalDoc(run, ServerTables, ShopInfoEntry);
            if (info == null) return;
            var catIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (XmlElem h in Children(info.Root, "shop", "shopID", Inv(row.Group.Id))) { string c = h.Attr("categoryID"); if (c != null) catIds.Add(c); }
            foreach (Profile p in clients)
            {
                XmlDoc cat = FinalDoc(run, p.ArchiveIn("x:Xml.m2d"), ShopCategoryEntry);
                if (cat == null) continue;
                foreach (string cid in catIds)
                {
                    List<XmlElem> cats = Children(cat.Root, "category", "id", cid);
                    if (cats.Count == 0) continue;     // the header check reports it
                    var tabs = new HashSet<string>(StringComparer.Ordinal);
                    foreach (XmlElem c in cats) CollectTabs(c, tabs);
                    if (!tabs.Contains(category))
                        run.Plan.Msg(run.Plan.Warnings, row.Mod, row.Archive, row.Entry, row.Group + ": sn " + sn + ": category \"" + category + "\" is in no tab of category " + cid + " of the " + p.Title + "'s " + ShopCategoryEntry + " (mod.xml line " + row.Line + ")");
                }
            }
        }

        static void CollectTabs(XmlElem e, HashSet<string> tabs)
        {
            foreach (XmlElem c in e.Children)
            {
                string list = c.Attr("category");
                if (list != null) foreach (string s in list.Split(',')) if (s.Trim().Length > 0) tabs.Add(s.Trim());
                CollectTabs(c, tabs);
            }
        }

        // ---- npc references: shopId and the individual drop box ids, after the mods ----

        static readonly string[] BoxRefs = { "individualDropBoxId", "individualHitDropBoxId" };

        static bool IsNpcEntry(EntryState st)
        {
            string n = st.Name.Replace('\\', '/').ToLowerInvariant();
            return n.StartsWith("npc/", StringComparison.Ordinal) || n.StartsWith("npcdata/", StringComparison.Ordinal);
        }

        static bool TouchesNpcs(Run run)
        {
            foreach (EntryState st in run.Order) if (IsNpcEntry(st) && st.Final != null && (!st.Existed || !M2dWriter.BytesEqual(st.Final, st.Original))) return true;
            return false;
        }

        static void CollectRefs(XmlElem e, string attr, HashSet<string> into)
        {
            string v = e.Attr(attr);
            if (v != null)
                foreach (string s in v.Split(','))
                {
                    string t = s.Trim();
                    if (t.Length > 0 && t != "0") into.Add(t);
                }
            foreach (XmlElem c in e.Children) CollectRefs(c, attr, into);
        }

        static HashSet<string> RefsOf(byte[] bytes, string label, string attr)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (bytes == null) return set;
            try
            {
                XmlDoc d = XmlDoc.Parse(bytes, label);
                foreach (XmlElem t in d.Top) CollectRefs(t, attr, set);
            }
            catch (ModKitException) { }
            return set;
        }

        static HashSet<string> ShopIdsAfter(Run run)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (string entry in new[] { ShopStockEntry, ShopInfoEntry })
            {
                XmlDoc d = FinalDoc(run, ServerTables, entry);
                if (d != null) foreach (XmlElem s in Children(d.Root, "shop", null, null)) { string id = s.Attr("shopID"); if (id != null) set.Add(id); }
            }
            return set;
        }

        static HashSet<string> BoxIdsAfter(Run run, Profile p)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (p == Profile.GmsClient)
            {
                string a = p.ArchiveIn("x:Xml.m2d");
                M2dArchive arch;
                try { arch = run.Data.Open(a); } catch (ModKitException) { return null; }
                foreach (string n in FlatSplits(arch))
                {
                    XmlDoc d = FinalDoc(run, a, n);
                    if (d != null) foreach (XmlElem e in Children(d.Root, DropMap.RowFlat, null, null)) { string id = e.Attr("individualDropBoxID"); if (id != null) set.Add(id); }
                }
                return set;
            }
            XmlDoc nd = p == Profile.Server ? FinalDoc(run, ServerTables, ServerDropEntry) : FinalDoc(run, p.ArchiveIn("x:Xml.m2d"), KmsDropEntry);
            if (nd == null) return null;
            foreach (XmlElem b in Children(nd.Root, DropMap.BoxNested, null, null)) { string id = b.Attr("dropBoxID"); if (id != null) set.Add(id); }
            return set;
        }

        static void CheckNpcRefs(Run run)
        {
            Plan plan = run.Plan;
            HashSet<string> shops = null;
            var boxes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (EntryState st in new List<EntryState>(run.Order))
            {
                if (!IsNpcEntry(st) || st.DeletedBy != null || st.Final == null) continue;
                if (st.Existed && M2dWriter.BytesEqual(st.Final, st.Original)) continue;
                Profile p = Profile.OfArchive(st.Archive);
                if (p == null) continue;
                string label = st.Archive + " " + st.Name;
                HashSet<string> before = RefsOf(st.Existed ? st.Original : null, label, "shopId"), after = RefsOf(st.Final, label, "shopId");
                foreach (string v in after)
                {
                    if (before.Contains(v)) continue;
                    if (!run.Data.Has(Profile.Server)) { plan.Msg(plan.Notes, null, st.Archive, st.Name, "shopId " + v + " is not checked: shops are server data and the data set has no server"); continue; }
                    if (shops == null) shops = ShopIdsAfter(run);
                    if (!shops.Contains(v)) plan.Msg(plan.Warnings, null, st.Archive, st.Name, "shopId " + v + " is not a shop on the server after the mods");
                }
                foreach (string attr in BoxRefs)
                {
                    before = RefsOf(st.Existed ? st.Original : null, label, attr);
                    after = RefsOf(st.Final, label, attr);
                    foreach (string v in after)
                    {
                        if (before.Contains(v)) continue;
                        HashSet<string> set;
                        if (!boxes.TryGetValue(p.Root, out set)) boxes[p.Root] = set = BoxIdsAfter(run, p);
                        if (set != null && !set.Contains(v))
                            plan.Msg(plan.Warnings, null, st.Archive, st.Name, attr + " " + v + " is not a drop box of the " + p.Title + " after the mods");
                    }
                }
            }
        }
    }
}
