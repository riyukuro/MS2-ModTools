// Entity layouts: where a profile keeps the rows of one entity (item, npc). Two shapes exist:
//
//   bundle           one file for many ids, one row per id: itemdata/<id/100000>.xml with <item id="..."> rows
//   one file per id  item/<a>/<bb>/<id>.xml, the entity's element is the file's root
//
// The layout replaces the single "bundles" flag a profile had: each entity of each profile names its own entry
// pattern (the syntax of FieldMap.cs: "*", "{id}", "{id/N}"), its row element and key (bundles only) and the
// element under which the fields sit. Two profiles mirror each other one to one when every entity has the same
// shape in both (Profile.Mirrors); a field map pairs two different shapes.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MS2.ModKit
{
    public sealed class EntityLayout
    {
        /// <summary>The entity: "item", "npc".</summary>
        public readonly string Entity;
        /// <summary>The entry pattern, as in a field map: "itemdata/{id/100000}.xml", "item/*/*/{id}.xml".</summary>
        public readonly string Entry;
        /// <summary>The row element of a bundle ("item"); null for one file per id.</summary>
        public readonly string Row;
        /// <summary>The attribute of a bundle row that holds the id.</summary>
        public readonly string Key;
        /// <summary>The element the fields sit under ("environment").</summary>
        public readonly string Under;
        /// <summary>One file per id: how many leading digits of the 8-digit id name each folder (item: 1, 2; npc: 2, 2).</summary>
        public readonly int[] Folders;
        /// <summary>The digits of the id in a one-file-per-id name (zero padded).</summary>
        public const int IdWidth = 8;

        readonly EntitySide side;

        public EntityLayout(string entity, string entry, string row, string key, string under, params int[] folders)
        {
            Entity = entity; Entry = entry; Row = row; Key = key ?? "id"; Under = under; Folders = folders ?? new int[0];
            side = new EntitySide { EntryPattern = entry, Row = row, Key = Key, Under = under };
            side.Compile();
        }

        public bool Bundle { get { return Row != null; } }

        /// <summary>The same entry pattern, row, key and element: the two profiles keep this entity the same way.</summary>
        public bool SameShape(EntityLayout o)
        {
            return o != null && o.Entity == Entity && string.Equals(o.Entry, Entry, StringComparison.OrdinalIgnoreCase)
                && o.Row == Row && (Row == null || o.Key == Key) && o.Under == Under;
        }

        /// <summary>The same shape as one side of a field map.</summary>
        public bool SameShape(EntitySide s)
        {
            return s != null && string.Equals(s.EntryPattern, Entry, StringComparison.OrdinalIgnoreCase)
                && s.Row == Row && (Row == null || s.Key == Key) && s.Under == Under;
        }

        /// <summary>Does the entry belong to this layout; id is the file's number (bundle) or the id (one file per id).</summary>
        public bool Match(string entry, out long id) { return side.Match(entry, out id); }

        /// <summary>The bundle file of an id among the given entry names, or null.</summary>
        public string EntryOf(long id, IEnumerable<string> names) { return side.EntryOf(id, names); }

        /// <summary>The digits of a bundle file's number ("001.xml").</summary>
        public const int BundleWidth = 3;

        /// <summary>
        /// The entry that holds an id: the bundle file ("npcdata/001.xml"), or for one file per id a new file's name
        /// ("npc/12/34/12345678.xml"); null when the pattern cannot name it.
        /// </summary>
        public string EntryOf(long id)
        {
            if (Bundle)
            {
                int open = Entry.IndexOf('{'), close = Entry.IndexOf('}');
                if (open < 0 || close < open) return null;
                return Entry.Substring(0, open) + side.NameNumber(id).ToString(CultureInfo.InvariantCulture).PadLeft(BundleWidth, '0') + Entry.Substring(close + 1);
            }
            string s = id.ToString(CultureInfo.InvariantCulture).PadLeft(IdWidth, '0');
            int slash = Entry.LastIndexOf('/');
            string head = Entry.Substring(0, Entry.IndexOf('/') + 1);
            var b = new StringBuilder(head);
            int at = 0;
            foreach (int n in Folders)
            {
                if (at + n > s.Length) return null;
                b.Append(s, at, n).Append('/');
                at += n;
            }
            if (slash < 0) return null;
            b.Append(s).Append(".xml");
            return b.ToString();
        }
    }

    public static class Layouts
    {
        public static readonly EntityLayout ItemBundle = new EntityLayout("item", "itemdata/{id/100000}.xml", "item", "id", "environment");
        public static readonly EntityLayout ItemFiles = new EntityLayout("item", "item/*/*/{id}.xml", null, null, "environment", 1, 2);
        public static readonly EntityLayout NpcBundle = new EntityLayout("npc", "npcdata/{id/100000}.xml", "npc", "id", "environment");
        public static readonly EntityLayout NpcFiles = new EntityLayout("npc", "npc/*/*/{id}.xml", null, null, "environment", 2, 2);

        /// <summary>The entities every profile has a layout for.</summary>
        public static readonly string[] Entities = { "item", "npc" };

        internal static Dictionary<string, EntityLayout> Of(params EntityLayout[] list)
        {
            var d = new Dictionary<string, EntityLayout>();
            foreach (EntityLayout l in list) d[l.Entity] = l;
            return d;
        }
    }
}
