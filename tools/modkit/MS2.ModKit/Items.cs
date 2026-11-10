// The item editor: the entity editor (Entities.cs) for items, plus the item's description text and its stat rows.
//
// What makes up an item:
//   client:Xml.m2d      itemdata/NNN.xml                  <item id> rows; NNN is the id divided by 100000. The client
//   (kms-client)                                          looks an id up in the file of its own block only.
//                       table/itemoptionconstant.xml      <option code> rows: stat rows; an item names one by
//                                                         <option constantID>, and many items share one row
//   server:Xml.m2d,     item/<a>/<bb>/<id>.xml            one file per item, the id is the file name
//   gms-client:Xml.m2d
//   every target        string/<lang>/itemname.xml        <key id name>            the name, one file per language
//                       string/<lang>/itemnameplural.xml  <key id name>            the plural name
//                       string/<lang>/koritemdescription.xml  <key id tooltipDescription guideDescription>
using System;
using System.Collections.Generic;
using System.Globalization;

namespace MS2.ModKit
{
    public sealed class ItemDraft : EntityDraft
    {
        internal ItemDraft(ItemEditor editor, long id) : base(editor, id) { }

        public new ItemEditor Editor { get { return (ItemEditor)base.Editor; } }

        public string Tooltip { get { return First("description", "tooltipDescription"); } }
        public string Guide { get { return First("description", "guideDescription"); } }

        /// <summary>
        /// Sets the tooltip text in every language file of every target. An item without a description row gets one
        /// in every language that has a description file. Returns the number of rows.
        /// </summary>
        public int SetTooltip(string value) { return SetDescription("tooltipDescription", value); }

        /// <summary>The second description line (the guide text), handled like the tooltip.</summary>
        public int SetGuide(string value) { return SetDescription("guideDescription", value); }

        int SetDescription(string field, string value)
        {
            if (value == null) throw new ArgumentNullException("value");
            bool any = false;
            foreach (EntityPart p in Parts) if (p.Kind == "description") any = true;
            if (!any)
            {
                if (value.Length == 0) return 0;
                Editor.AddStringRows(2, "<key id=\"" + Id.ToString(CultureInfo.InvariantCulture) + "\" tooltipDescription=\"\" guideDescription=\"\" />");
                Editor.Fill(this);
            }
            return SetAll("description", field, value);
        }

        // ---- stat rows ----

        /// <summary>The codes of the stat rows (option table) the item names.</summary>
        public List<string> OptionCodes()
        {
            var list = new List<string>();
            foreach (EntityPart p in Parts) if (p.Kind == "option") list.Add(Get(RootSection(p), "code"));
            return list;
        }

        /// <summary>
        /// Gives the item a stat row of its own: the shared row <paramref name="code"/> is copied to
        /// <paramref name="newCode"/> and the item's constantID is pointed at the copy, so that changing the
        /// stats no longer changes every item that uses the shared row. The option table is the kms-client's.
        /// </summary>
        public void GiveOwnOption(string code, string newCode)
        {
            Editor.OwnOption(this, code, newCode);
            Editor.Fill(this);
        }
    }

    public sealed class ItemEditor : EntityEditor
    {
        public const string OptionTable = "table/itemoptionconstant.xml";
        public const string IdSpace = "item";

        public ItemEditor(DataSet data, Mod mod)
            : base(IdSpace, data, mod, new[] { "itemname.xml", "itemnameplural.xml", "koritemdescription.xml" }, new[] { "name", "plural", "description" })
        {
        }

        /// <summary>The item file of an id: the client reads an id from the file of its own block of 100000 only.</summary>
        public static string BucketEntry(long id) { return "itemdata/" + (id / 100000).ToString("000", CultureInfo.InvariantCulture) + ".xml"; }

        protected override EntityDraft NewDraft(long id) { return new ItemDraft(this, id); }

        internal override string KeyOf(EntityPart p) { return p.Kind == "option" ? "code" : base.KeyOf(p); }

        /// <summary>The item of an id, as the original data and the mod have it.</summary>
        public ItemDraft Open(long id) { return (ItemDraft)OpenAny(id); }

        // the stat rows the item names
        protected override void MoreParts(EntityDraft d)
        {
            if (!HasBundleEntry(OptionTable)) return;
            var codes = new List<string>();
            foreach (EntitySection s in d.Sections.ToArray())
            {
                if (s.Part.Kind != Entity || s.Elem.Name != "option" || s.Elem.Attr("constantID") == null) continue;
                string code = d.Get(s, "constantID");
                if (string.IsNullOrEmpty(code) || code == "0" || codes.Contains(code)) continue;
                codes.Add(code);
                RowParts(d, "option", "stat row " + code, BundleXml, OptionTable, "code", code, true);
            }
        }

        internal void OwnOption(ItemDraft d, string code, string newCode)
        {
            long n;
            if (!TryId(newCode, out n) || n <= 0) throw new ModKitException("the new stat row code must be a number above 0");
            string docName;
            BundleRow(OptionTable, "code", newCode, out docName);
            if (docName == null) throw new ModKitException("the data has no kms-client " + OptionTable);
            if (BundleRow(OptionTable, "code", newCode, out docName) != null || Adds(BundleXml, docName, "code", newCode).Count > 0)
                throw new ModKitException("the stat row code " + newCode + " is already used");
            EntityPart src = null;
            foreach (EntityPart p in d.Parts)
                if (p.Kind == "option" && (p.Add != null ? KeyValue(p.Add, "code") : p.Root.Attr("code")) == code) { src = p; break; }
            if (src == null) throw new ModKitException("item " + d.Id + " names no stat row " + code);
            var add = new RowOp { Kind = RowOpKind.Add };
            if (src.Add != null)
            {
                add.Copy = src.Add.Copy; add.Xml = src.Add.Xml;
                foreach (AttrOp a in src.Add.Attrs)
                    if (!(string.IsNullOrEmpty(a.At) && a.Name == "code")) add.Attrs.Add(new AttrOp { Kind = a.Kind, Name = a.Name, Value = a.Value, At = a.At });
            }
            else add.Copy = Xml.RowPath.Canonical(src.Root);
            add.Attrs.Add(new AttrOp { Kind = AttrOpKind.Set, Name = "code", Value = newCode });
            Edit(BundleXml, docName).Rows.Add(add);
            foreach (EntitySection s in d.Sections.ToArray())
                if (s.Part.Kind == Entity && s.Elem.Name == "option" && d.Get(s, "constantID") == code) d.Set(s, "constantID", newCode);
        }

        /// <summary>
        /// A new item that starts as a copy of a shipped one (EntityEditor.CloneAny): its item row, its name, plural
        /// name and description rows in every language of every target, and its per-id item file. name and tooltip
        /// may be null to keep the source's.
        /// </summary>
        public ItemDraft Clone(long sourceId, long newId, string name, string tooltip)
        {
            var d = (ItemDraft)CloneAny(sourceId, newId);
            if (name != null) d.SetName(name);
            if (tooltip != null) d.SetTooltip(tooltip);
            return d;
        }
    }
}
