// Mastery recipes (table/masteryreceipe.xml): the gathering and crafting recipes of the life skills. The client
// and the server read the same table, each from its own Xml.m2d, so a recipe is edited as one thing that is written
// to every side that ships the table (the server's lines are planned for the global client too, see PlannerMirror).
//
// A row, as the client reads it (CMasteryReceipe::Load):
//   id                       the recipe id; MasteryReceipe/{id}.png in Image.m2d is its picture in the mastery window
//   masteryType              3 mining, 4 gathering, 5 breeding, 6 farming (gathering types: habitatMapId and the
//                            prop limits count), 7 blacksmithing, 8 engraving, 9 alchemist, 10 cooking (crafting
//                            types: materials, meso and time count), 11 pettaming (its level is fixed at 50)
//   requireMastery           the mastery value (not the grade) the player needs
//   requireMeso              meso the recipe costs (int64)
//   requireItem1..5          materials, "itemId[:tag],rank,count" (exactly three parts; the rank is not read)
//   rewardItem1..5           results, "itemId,rank,count"
//   rewardMastery, rewardExp mastery and experience the player gets; exceptRewardExp leaves the experience out
//   gatheringTime            the time a craft takes
//   requireQuest, habitatMapId  token lists
//   feature, locale          rows with one id for other features or locales; the client keeps the last row of
//                            an id whose feature and locale are on
// An item part whose id is 0 or empty is no item.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    /// <summary>One material or result of a recipe: "id[:tag],rank,count".</summary>
    public sealed class RecipeItem
    {
        public long Id;
        /// <summary>The ":tag" of a material id (a tag name the client reads with it), without the ':'; null for none.</summary>
        public string Tag;
        public int Rank = 1;
        public long Count = 1;

        public override string ToString()
        {
            return Id.ToString(CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(Tag) ? "" : ":" + Tag) + "," +
                Rank.ToString(CultureInfo.InvariantCulture) + "," + Count.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Reads "id[:tag],rank,count". Empty text and id 0 are no item (true, item null).
        /// False for text the client does not read as an item.</summary>
        public static bool TryParse(string text, out RecipeItem item)
        {
            item = null;
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return true;
            string[] p = text.Split(',');
            if (p.Length != 3) return false;
            string idPart = p[0].Trim(), tag = null;
            int colon = idPart.IndexOf(':');
            if (colon >= 0) { tag = idPart.Substring(colon + 1); idPart = idPart.Substring(0, colon); }
            long id, count;
            int rank;
            if (!long.TryParse(idPart, NumberStyles.None, CultureInfo.InvariantCulture, out id) || id > int.MaxValue) return false;
            if (!int.TryParse(p[1].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out rank)) return false;
            if (!long.TryParse(p[2].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out count)) return false;
            if (id == 0) return true;
            item = new RecipeItem { Id = id, Tag = tag, Rank = rank, Count = count };
            return true;
        }
    }

    /// <summary>The grades of the mastery types (table/mastery.xml): the value each grade starts at.</summary>
    public sealed class MasteryTable
    {
        readonly Dictionary<int, SortedDictionary<int, long>> grades = new Dictionary<int, SortedDictionary<int, long>>();

        public static MasteryTable Parse(XmlDoc doc)
        {
            var t = new MasteryTable();
            if (doc == null || doc.Root == null) return t;
            foreach (XmlElem m in doc.Root.Children)
            {
                int type;
                if (m.Name != "mastery" || !int.TryParse(m.Attr("type") ?? "", NumberStyles.None, CultureInfo.InvariantCulture, out type)) continue;
                SortedDictionary<int, long> g;
                if (!t.grades.TryGetValue(type, out g)) t.grades[type] = g = new SortedDictionary<int, long>();
                foreach (XmlElem v in m.Children)
                {
                    int grade;
                    long value;
                    if (v.Name != "v" || !int.TryParse(v.Attr("grade") ?? "", NumberStyles.None, CultureInfo.InvariantCulture, out grade)) continue;
                    if (!long.TryParse(v.Attr("value") ?? "", NumberStyles.None, CultureInfo.InvariantCulture, out value)) continue;
                    long had;
                    if (!g.TryGetValue(grade, out had) || value > had) g[grade] = value;
                }
            }
            return t;
        }

        public bool Has(int type) { return grades.ContainsKey(type) && grades[type].Count > 0; }

        /// <summary>The value of the top grade of a type (-1 when the table has no such type).</summary>
        public long MaxValue(int type)
        {
            SortedDictionary<int, long> g;
            if (!grades.TryGetValue(type, out g)) return -1;
            long max = -1;
            foreach (long v in g.Values) if (v > max) max = v;
            return max;
        }

        /// <summary>The grade a mastery value reaches (0 when the type is unknown or the value is below every grade).</summary>
        public int GradeOf(int type, long value)
        {
            SortedDictionary<int, long> g;
            if (!grades.TryGetValue(type, out g)) return 0;
            int best = 0;
            foreach (KeyValuePair<int, long> kv in g) if (value >= kv.Value && kv.Key > best) best = kv.Key;
            return best;
        }
    }

    /// <summary>The checks of one recipe row, shared by the editor (values as shown) and the plan (final rows).</summary>
    public static class RecipeRules
    {
        public const string Entry = "table/masteryreceipe.xml", MasteryEntry = "table/mastery.xml", Rows = "receipe";
        public const string Space = "recipe";
        /// <summary>The recipe ids of mods: no shipped recipe uses this band.</summary>
        public const long From = 99000000, To = 99009999;
        public const int Slots = 5;
        /// <summary>The mastery types a recipe can have (the client names these; 1 and 2 assert).</summary>
        public const int FirstType = 3, LastType = 11;
        /// <summary>Pettaming: the client sets the level of these recipes to 50 whatever the row says.</summary>
        public const int PetTaming = 11;
        public const string NoResult = "the recipe has no result";

        public static string TypeName(int type)
        {
            switch (type)
            {
                case 3: return "mining";
                case 4: return "gathering";
                case 5: return "breeding";
                case 6: return "farming";
                case 7: return "blacksmithing";
                case 8: return "engraving";
                case 9: return "alchemist";
                case 10: return "cooking";
                case 11: return "pettaming";
                default: return null;
            }
        }

        /// <summary>Gathering types (3 to 6) take their results from props on habitat maps; the others are crafts.</summary>
        public static bool IsGathering(int type) { return type >= 3 && type <= 6; }

        public static string Material(int slot) { return "requireItem" + slot; }
        public static string Result(int slot) { return "rewardItem" + slot; }

        /// <summary>
        /// The problems of a recipe, one text each (empty: none). get gives the row's values; mastery may be null
        /// (no level check); missingItem may be null (no item check): it gives where an item id is missing, or null.
        /// </summary>
        public static List<string> Check(Func<string, string> get, MasteryTable mastery, Func<long, string> missingItem)
        {
            var l = new List<string>();
            int type;
            string ts = get("masteryType") ?? "";
            if (!int.TryParse(ts, NumberStyles.None, CultureInfo.InvariantCulture, out type) || TypeName(type) == null)
                l.Add("masteryType \"" + ts + "\" is not a recipe type (" + FirstType + " to " + LastType + ")");
            else
            {
                string rm = get("requireMastery");
                long need = 0;
                if (!string.IsNullOrEmpty(rm) && !long.TryParse(rm, NumberStyles.None, CultureInfo.InvariantCulture, out need))
                    l.Add("requireMastery \"" + rm + "\" is not a mastery value");
                else if (type != PetTaming && mastery != null && mastery.Has(type) && need > mastery.MaxValue(type))
                    l.Add("requireMastery " + need + " is above the top " + TypeName(type) + " value " + mastery.MaxValue(type) + ": no player can use the recipe");
            }
            foreach (string f in new[] { "requireMeso", "rewardMastery", "rewardExp", "gatheringTime" })
            {
                string v = get(f);
                long n;
                if (!string.IsNullOrEmpty(v) && (!long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out n)))
                    l.Add(f + " \"" + v + "\" is not a number of 0 or more");
            }

            var results = new HashSet<long>();
            int nResults = 0;
            for (int pass = 0; pass < 2; pass++)
                for (int i = 1; i <= Slots; i++)
                {
                    string f = pass == 0 ? Result(i) : Material(i);
                    string v = get(f);
                    RecipeItem it;
                    if (!RecipeItem.TryParse(v, out it)) { l.Add(f + " \"" + v + "\" is not \"id" + (pass == 0 ? "" : "[:tag]") + ",rank,count\""); continue; }
                    if (it == null) continue;
                    if (it.Count <= 0) l.Add(f + ": count " + it.Count + " of item " + it.Id + " is not above 0");
                    if (pass == 0 && !string.IsNullOrEmpty(it.Tag)) l.Add(f + ": the client reads no \":tag\" on a result (id,rank,count)");
                    if (pass == 0) { results.Add(it.Id); nResults++; }
                    else if (results.Contains(it.Id)) l.Add(f + ": item " + it.Id + " is a material and a result of the recipe");
                    if (missingItem != null)
                    {
                        string where = missingItem(it.Id);
                        if (where != null) l.Add(f + ": item " + it.Id + " is not an item of the " + where);
                    }
                }
            if (nResults == 0) l.Add(NoResult + " (rewardItem1 to " + Slots + ")");
            return l;
        }
    }

    /// <summary>A recipe as the editor shows it: its row on every side that has it (null where a side has none).</summary>
    public sealed class Recipe
    {
        public readonly TableRow[] Rows;
        internal Recipe(int sides) { Rows = new TableRow[sides]; }

        /// <summary>The first side's row that the recipe has.</summary>
        public TableRow Shown { get { foreach (TableRow r in Rows) if (r != null) return r; return null; } }

        public bool IsAdded { get { TableRow r = Shown; return r != null && r.IsAdded; } }
        public bool IsDeleted { get { foreach (TableRow r in Rows) if (r != null && !r.IsDeleted) return false; return true; } }
        public bool IsChanged { get { foreach (TableRow r in Rows) if (r != null && r.IsChanged) return true; return false; } }
    }

    /// <summary>
    /// The recipe editor of one mod: masteryreceipe.xml of every side that ships it (kms client, server; the global
    /// client when there is no server), read through TableDraft and written to every side at once.
    /// </summary>
    public sealed class RecipeEditor
    {
        public readonly DataSet Data;
        public readonly Mod Mod;
        public readonly List<TableDraft> Sides = new List<TableDraft>();
        public readonly List<Recipe> Recipes = new List<Recipe>();
        /// <summary>The mastery grades, of the first side that ships table/mastery.xml (empty when none does).</summary>
        public readonly MasteryTable Mastery;

        public RecipeEditor(DataSet data, Mod mod)
        {
            Data = data;
            Mod = mod;
            foreach (string a in SideArchives(data, mod)) Sides.Add(new TableDraft(data, mod, a, RecipeRules.Entry, null));
            if (Sides.Count == 0) throw new ModKitException("no target of the data set has " + RecipeRules.Entry);
            Mastery = new MasteryTable();
            foreach (TableDraft s in Sides)
            {
                M2dArchive arch = data.Open(s.Archive);
                M2dEntry e = arch.Find(RecipeRules.MasteryEntry);
                if (e == null) continue;
                Mastery = MasteryTable.Parse(XmlDoc.Parse(arch.Read(e), s.Archive + " " + RecipeRules.MasteryEntry));
                break;
            }
            Pair();
        }

        /// <summary>The archives a recipe is written to: the kms client's and the server's Xml.m2d, and the global
        /// client's when the data set has no server (else the server's lines are planned for the global client too)
        /// or when the mod has lines of its own there (the plan carries no server line over those).</summary>
        public static List<string> SideArchives(DataSet data, Mod mod)
        {
            var l = new List<string>();
            foreach (Profile p in new[] { Profile.KmsClient, Profile.Server, Profile.GmsClient })
            {
                if (!data.Has(p)) continue;
                if (p == Profile.GmsClient && data.Has(Profile.Server) && !HasLines(mod, p.ArchiveIn("x:Xml.m2d"))) continue;
                string a = p.ArchiveIn("x:Xml.m2d");
                M2dArchive arch;
                try { arch = data.Open(a); }
                catch (ModKitException) { continue; }
                if (arch.Find(RecipeRules.Entry) != null) l.Add(a);
            }
            return l;
        }

        static bool HasLines(Mod mod, string archive)
        {
            if (mod == null) return false;
            foreach (ModOp op in mod.Ops)
            {
                var ed = op as EditOp;
                if (ed != null && string.Equals(ed.Archive, archive, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(ed.Entry.Replace('\\', '/'), RecipeRules.Entry, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        string KeyOf(TableDraft s, TableRow r)
        {
            return (s.Get(r, "id") ?? "") + "\n" + (s.Get(r, "feature") ?? "") + "\n" + (s.Get(r, "locale") ?? "");
        }

        void Pair()
        {
            Recipes.Clear();
            // rows that share id, feature and locale on one side pair in their order: the n-th with the n-th
            var byKey = new Dictionary<string, List<Recipe>>();
            for (int i = 0; i < Sides.Count; i++)
            {
                var seen = new Dictionary<string, int>();
                foreach (TableRow r in Sides[i].Rows)
                {
                    if (r.Base.Name != RecipeRules.Rows || string.IsNullOrEmpty(Sides[i].Get(r, "id"))) continue;
                    string k = KeyOf(Sides[i], r);
                    List<Recipe> list;
                    if (!byKey.TryGetValue(k, out list)) byKey[k] = list = new List<Recipe>();
                    int n;
                    seen.TryGetValue(k, out n);
                    seen[k] = n + 1;
                    if (n >= list.Count)
                    {
                        var made = new Recipe(Sides.Count);
                        list.Add(made);
                        Recipes.Add(made);
                    }
                    list[n].Rows[i] = r;
                }
            }
        }

        // ---- reading ----

        public long Id(Recipe r) { long id; return long.TryParse(Get(r, "id"), NumberStyles.None, CultureInfo.InvariantCulture, out id) ? id : 0; }

        public int Type(Recipe r) { int t; return int.TryParse(Get(r, "masteryType"), NumberStyles.None, CultureInfo.InvariantCulture, out t) ? t : 0; }

        /// <summary>The value of the first side that has the recipe.</summary>
        public string Get(Recipe r, string field)
        {
            for (int i = 0; i < Sides.Count; i++) if (r.Rows[i] != null) return Sides[i].Get(r.Rows[i], field);
            return null;
        }

        /// <summary>The shipped value of the first side that has the recipe (null for a recipe the mod adds).</summary>
        public string Shipped(Recipe r, string field)
        {
            if (r.IsAdded) return null;
            for (int i = 0; i < Sides.Count; i++) if (r.Rows[i] != null) return Sides[i].GetOriginal(r.Rows[i], field);
            return null;
        }

        /// <summary>The key a recipe keeps across a new editor: id, feature and locale.</summary>
        public string Key(Recipe r) { return (Get(r, "id") ?? "") + "\n" + (Get(r, "feature") ?? "") + "\n" + (Get(r, "locale") ?? ""); }

        /// <summary>True when the sides that have the recipe give the field different values.</summary>
        public bool Differs(Recipe r, string field)
        {
            bool any = false;
            string first = null;
            for (int i = 0; i < Sides.Count; i++)
            {
                if (r.Rows[i] == null) continue;
                string v = Sides[i].Get(r.Rows[i], field);
                if (!any) { first = v; any = true; }
                else if (v != first) return true;
            }
            return false;
        }

        /// <summary>The sides (archive names) that do not have the recipe.</summary>
        public List<string> MissingOn(Recipe r)
        {
            var l = new List<string>();
            for (int i = 0; i < Sides.Count; i++) if (r.Rows[i] == null) l.Add(Sides[i].Archive);
            return l;
        }

        /// <summary>The items of slots 1 to 5 (null where a slot is empty or unreadable).</summary>
        public RecipeItem[] Items(Recipe r, bool results)
        {
            var a = new RecipeItem[RecipeRules.Slots];
            for (int i = 0; i < a.Length; i++)
            {
                RecipeItem it;
                if (RecipeItem.TryParse(Get(r, results ? RecipeRules.Result(i + 1) : RecipeRules.Material(i + 1)), out it)) a[i] = it;
            }
            return a;
        }

        /// <summary>Recipes whose mastery type is type (0: any) and whose id, or a result id, holds text.</summary>
        public List<Recipe> Find(int type, string text)
        {
            text = (text ?? "").Trim();
            var l = new List<Recipe>();
            foreach (Recipe r in Recipes)
            {
                if (type != 0 && Type(r) != type) continue;
                if (text.Length > 0 && (Get(r, "id") ?? "").IndexOf(text, StringComparison.Ordinal) < 0)
                {
                    bool hit = false;
                    foreach (RecipeItem it in Items(r, true))
                        if (it != null && it.Id.ToString(CultureInfo.InvariantCulture).IndexOf(text, StringComparison.Ordinal) >= 0) hit = true;
                    if (!hit) continue;
                }
                l.Add(r);
            }
            return l;
        }

        /// <summary>The problems of the recipe as shown (the plan checks the final rows the same way, and items).</summary>
        public List<string> Check(Recipe r)
        {
            List<string> l = RecipeRules.Check(f => Get(r, f), Mastery, null);
            // a shipped row the mod leaves alone is not the mod's problem
            if (!r.IsAdded && !r.IsChanged) l.RemoveAll(t => t.StartsWith(RecipeRules.NoResult, StringComparison.Ordinal));
            if (r.IsAdded)
            {
                long id = Id(r);
                if (id < RecipeRules.From || id > RecipeRules.To)
                    l.Add("new id " + id + " is outside of " + RecipeRules.From + "-" + RecipeRules.To + ", the range of new recipes");
                if (!HasPicture(id))
                    l.Add("no picture: the mastery window shows MasteryReceipe/" + id + ".png, which only shipped recipes have");
            }
            List<string> missing = MissingOn(r);
            if (missing.Count > 0 && !r.IsDeleted) l.Add("only on " + string.Join(", ", Have(r).ToArray()) + "; not on " + string.Join(", ", missing.ToArray()));
            return l;
        }

        /// <summary>True when the mod adds the picture of a recipe (an entry MasteryReceipe/{id}.png of any archive).</summary>
        public bool HasPicture(long id)
        {
            string want = "masteryreceipe/" + id.ToString(CultureInfo.InvariantCulture) + ".png";
            foreach (ModOp op in Mod.Ops)
            {
                var en = op as EntryOp;
                if (en == null || en.Delete || en.Name == null) continue;
                string n = en.Name.Replace('\\', '/').ToLowerInvariant();
                if (n == want || n.EndsWith("/" + want, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        List<string> Have(Recipe r)
        {
            var l = new List<string>();
            for (int i = 0; i < Sides.Count; i++) if (r.Rows[i] != null) l.Add(Sides[i].Archive);
            return l;
        }

        // ---- editing ----

        /// <summary>Sets a field on every side that has the recipe (null removes the attribute).</summary>
        public void Set(Recipe r, string field, string value)
        {
            if (field == "id" || field == "feature" || field == "locale")
            {
                if (!r.IsAdded) throw new ModKitException(field + " of a shipped recipe is its key; clone the recipe for a new one");
                if (field == "id") CheckNewId(ParseId(value), r);
            }
            for (int i = 0; i < Sides.Count; i++)
                if (r.Rows[i] != null && r.Rows[i].IsDeleted)
                    throw new ModKitException("recipe " + Get(r, "id") + " is removed on " + Sides[i].Archive + "; restore it before a change");
            long old = Id(r);
            for (int i = 0; i < Sides.Count; i++)
                if (r.Rows[i] != null) Sides[i].Set(r.Rows[i], field, value);
            if (field == "id")
            {
                // the new id is the mod's now; the old one is free unless another new recipe still has it
                long now = Id(r);
                if (now != old)
                {
                    bool still = false;
                    foreach (Recipe o in Recipes) if (o != r && o.IsAdded && Id(o) == old) still = true;
                    if (!still) IdSpaces.Unreserve(Mod, RecipeRules.Space, old);
                    IdSpaces.Reserve(Mod, RecipeRules.Space, now);
                }
            }
        }

        /// <summary>Sets slot 1 to 5 of the materials or the results (null empties it).</summary>
        public void SetItem(Recipe r, bool result, int slot, RecipeItem item)
        {
            if (slot < 1 || slot > RecipeRules.Slots) throw new ArgumentOutOfRangeException("slot");
            if (item != null && item.Id <= 0) throw new ModKitException("item id " + item.Id + " is not an item");
            if (item != null && item.Id > int.MaxValue) throw new ModKitException("item id " + item.Id + " is above " + int.MaxValue + ", the largest the client reads");
            if (item != null && result && !string.IsNullOrEmpty(item.Tag)) throw new ModKitException("a result takes no \":tag\"");
            if (item != null && item.Tag != null && (item.Tag.IndexOf(',') >= 0 || item.Tag.IndexOf(':') >= 0))
                throw new ModKitException("the tag \"" + item.Tag + "\" holds a ',' or ':', which the row cannot hold");
            Set(r, result ? RecipeRules.Result(slot) : RecipeRules.Material(slot), item == null ? "" : item.ToString());
        }

        static long ParseId(string v)
        {
            long id;
            if (!long.TryParse(v ?? "", NumberStyles.None, CultureInfo.InvariantCulture, out id) || id <= 0) throw new ModKitException("\"" + v + "\" is not a recipe id");
            return id;
        }

        /// <summary>The other enabled mods, read when a new id is checked or asked for (null: none). Their added
        /// recipes and their recipe id ranges are not offered and are refused for a new recipe.</summary>
        public Func<IEnumerable<Mod>> Others;

        List<Mod> OtherMods()
        {
            var l = new List<Mod>();
            IEnumerable<Mod> mods = Others == null ? null : Others();
            if (mods != null) foreach (Mod m in mods) if (m != null && m != Mod && m.Id != Mod.Id) l.Add(m);
            return l;
        }

        void CheckNewId(long id, Recipe self)
        {
            if (id < RecipeRules.From || id > RecipeRules.To)
                throw new ModKitException("new recipe ids are " + RecipeRules.From + " to " + RecipeRules.To + "; " + id + " is outside");
            foreach (Recipe o in Recipes)
                if (o != self && Id(o) == id) throw new ModKitException("recipe " + id + " is there already");
            foreach (Mod m in OtherMods())
            {
                if (AddedIds(m).Contains(id)) throw new ModKitException("the enabled mod " + m.Id + " adds a recipe " + id + " already");
                foreach (IdRange r in m.Ids)
                    if (r.Space == RecipeRules.Space && id >= r.From && id <= r.To)
                        throw new ModKitException("the recipe id " + id + " is reserved by the enabled mod " + m.Id);
            }
        }

        /// <summary>A new recipe that copies source (its shown values), on every side, with the new id.</summary>
        public Recipe Clone(Recipe source, long newId)
        {
            CheckNewId(newId, null);
            if (source.IsDeleted) throw new ModKitException("recipe " + Get(source, "id") + " is removed; restore it before copying");
            var made = new Recipe(Sides.Count);
            TableRow shown = source.Shown;
            TableDraft shownSide = Sides[Array.IndexOf(source.Rows, shown)];
            for (int i = 0; i < Sides.Count; i++)
            {
                TableRow from = source.Rows[i];
                made.Rows[i] = from != null && !from.IsDeleted ? Sides[i].Clone(from) : Sides[i].AddRow(RowXml(shownSide, shown));
                Sides[i].Set(made.Rows[i], "id", newId.ToString(CultureInfo.InvariantCulture));
                foreach (string c in shownSide.Columns)
                {
                    string v = shownSide.Get(shown, c);
                    if (c != "id" && Sides[i].Get(made.Rows[i], c) != v) Sides[i].Set(made.Rows[i], c, v);
                }
            }
            IdSpaces.Reserve(Mod, RecipeRules.Space, newId);
            Recipes.Add(made);
            return made;
        }

        /// <summary>A new, empty recipe of a mastery type on every side.</summary>
        public Recipe Add(long newId, int masteryType)
        {
            CheckNewId(newId, null);
            if (RecipeRules.TypeName(masteryType) == null) throw new ModKitException("mastery type " + masteryType + " is not a recipe type");
            var sb = new StringBuilder("<" + RecipeRules.Rows);
            foreach (KeyValuePair<string, string> kv in NewRow(newId, masteryType))
                sb.Append(' ').Append(kv.Key).Append("=\"").Append(XmlEscape(kv.Value)).Append('"');
            sb.Append(" />");
            var made = new Recipe(Sides.Count);
            for (int i = 0; i < Sides.Count; i++) made.Rows[i] = Sides[i].AddRow(sb.ToString());
            IdSpaces.Reserve(Mod, RecipeRules.Space, newId);
            Recipes.Add(made);
            return made;
        }

        // the attributes of a shipped row, in their order, for a new recipe
        static List<KeyValuePair<string, string>> NewRow(long id, int type)
        {
            var l = new List<KeyValuePair<string, string>>();
            Action<string, string> a = (k, v) => l.Add(new KeyValuePair<string, string>(k, v));
            a("id", id.ToString(CultureInfo.InvariantCulture));
            a("masteryType", type.ToString(CultureInfo.InvariantCulture));
            a("exceptRewardExp", "0"); a("requireMastery", "0"); a("requireMeso", ""); a("requireQuest", "");
            a("rewardExp", ""); a("rewardMastery", "1"); a("gatheringTime", "");
            a("highPropLimitCount", ""); a("normalPropLimitCount", "");
            for (int i = 1; i <= RecipeRules.Slots; i++) a(RecipeRules.Material(i), "");
            a("habitatMapId", "");
            for (int i = 1; i <= RecipeRules.Slots; i++) a(RecipeRules.Result(i), "");
            a("feature", "");
            return l;
        }

        static string RowXml(TableDraft s, TableRow r)
        {
            var sb = new StringBuilder("<" + r.Base.Name);
            foreach (string c in s.Columns)
            {
                string v = s.Get(r, c);
                if (v != null) sb.Append(' ').Append(c).Append("=\"").Append(XmlEscape(v)).Append('"');
            }
            return sb.Append(" />").ToString();
        }

        static string XmlEscape(string v)
        {
            return v.Replace("&", "&amp;").Replace("<", "&lt;").Replace("\"", "&quot;");
        }

        /// <summary>Removes a recipe: a new one goes away (and its id is free again), a shipped one is deleted on every side.</summary>
        public void Remove(Recipe r)
        {
            bool added = r.IsAdded;
            long id = Id(r);
            for (int i = 0; i < Sides.Count; i++) if (r.Rows[i] != null) Sides[i].Delete(r.Rows[i]);
            if (added)
            {
                Recipes.Remove(r);
                bool still = false;
                foreach (Recipe o in Recipes) if (Id(o) == id) still = true;
                if (!still) IdSpaces.Unreserve(Mod, RecipeRules.Space, id);
            }
        }

        /// <summary>The recipe ids a mod's &lt;add&gt; rows of the table give (the id it sets, else the id of its row text).</summary>
        public static List<long> AddedIds(Mod mod)
        {
            var l = new List<long>();
            var inRow = new Regex(@"\sid\s*=\s*[""'](\d+)[""']");
            foreach (ModOp op in mod.Ops)
            {
                var ed = op as EditOp;
                if (ed == null || !string.Equals(ed.Entry.Replace('\\', '/'), RecipeRules.Entry, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (RowOp r in ed.Rows)
                {
                    if (r.Kind != RowOpKind.Add) continue;
                    string id = null;
                    foreach (AttrOp a in r.Attrs) if (a.Kind == AttrOpKind.Set && a.Name == "id" && a.At == null) id = a.Value;
                    if (id == null && r.Xml != null) { Match mt = inRow.Match(r.Xml); if (mt.Success) id = mt.Groups[1].Value; }
                    long n;
                    if (id != null && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out n)) l.Add(n);
                }
            }
            return l;
        }

        /// <summary>Takes back the removal of a shipped recipe.</summary>
        public void Restore(Recipe r)
        {
            for (int i = 0; i < Sides.Count; i++) if (r.Rows[i] != null) Sides[i].Restore(r.Rows[i]);
        }

        /// <summary>The lowest free ids of the range of new recipes: not shipped on any side and not taken by the mods.</summary>
        public List<long> NextIds(IEnumerable<Mod> mods, int count, Action<ProgressInfo> progress)
        {
            var all = new List<Mod>(mods ?? new Mod[0]);
            if (!all.Contains(Mod)) all.Add(Mod);
            var taken = new HashSet<long>();
            foreach (Recipe r in Recipes) taken.Add(Id(r));
            // recipes other mods add without an <ids> range of their own
            foreach (Mod m in all) if (m != Mod) foreach (long id in AddedIds(m)) taken.Add(id);
            // every taken id may sit in the free list, so ask for that many more
            List<long> free = IdSpaces.NextFreeGroupIds(Data, all, RecipeRules.Space, count + taken.Count, progress);
            free.RemoveAll(taken.Contains);
            if (free.Count > count) free.RemoveRange(count, free.Count - count);
            return free;
        }
    }
}
