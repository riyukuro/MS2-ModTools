// Dungeon rooms: the row that makes one or more maps a dungeon players enter through the dungeon window.
//
// One row per dungeon, <dungeonRoom dungeonRoomID ... />, in table/dungeonroom.xml of the KMS client
// (DungeonRoomTable::LoadRooms, DungeonRoom::Load: a later row of an id replaces the earlier one) and in
// table/na/dungeonroom.xml of the server and the global client (the server's data ingest fails on an id that is
// there twice). Most attributes have one name and one notation everywhere. These differ:
//
//   editor field      kms client                 server and global client
//   groupType         a number (1)               a word (normal, raid, chaosRaid, ...)
//   fieldIDs          2000335,2000336            02000335,02000336 (8 digits)
//   durationSeconds   durationSeconds="900"      durationTick="900000"
//   limitEnter        limitEnter="1"             playType="limitEnter" (else "limitReward")
//   clearKillNpc      clearKillNpc="N"           clearType="monster" clearValue="N" (else clearType="trigger")
//
// cooldownType, rewardUnlimitedDropBoxIds and isDisableFindHelper are on the server side only;
// extraRewardTicketTableID, extraRewardTicketBaseCount, findHelperRewardDropBoxIds and rewardLimitedDropBoxIds2 on
// the KMS client only. The dungeon's name and description are the rows <key id name uiDescription /> of
// string/{lang}/stringfieldenterance.xml (several rows of one id where features differ: all are written).
//
// The two sides ship different content for many dungeons (drop boxes, gear score, missions): the editor shows a
// value per side and writes a field to every side or to one.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    /// <summary>The fields of a dungeon room and how each side writes them; the checks the editor and the plan share.</summary>
    public static class DungeonRules
    {
        public const string Rows = "dungeonRoom", Key = "dungeonRoomID", NameFile = "stringfieldenterance.xml";
        public const string KmsEntry = "table/dungeonroom.xml";
        public const string Space = "dungeon";
        /// <summary>The ids of new dungeons.</summary>
        public const long From = 98000000, To = 98009999;
        public const string Group = "groupType", Fields = "fieldIDs", Duration = "durationSeconds", LimitEnter = "limitEnter", ClearKill = "clearKillNpc";

        /// <summary>The group types: the word of the server at the number of the kms client.</summary>
        public static readonly string[] GroupTypes =
        {
            "none", "normal", "raid", "chaosRaid", "reverseRaid", "lapenta", "guildRaid", "darkStream",
            "worldBossDungeon", "item", "vip", "event", "fameChallenge", "colosseum", "turka",
        };

        static readonly string[] ServerOnly = { "cooldownType", "rewardUnlimitedDropBoxIds", "isDisableFindHelper" };
        static readonly string[] KmsOnly = { "extraRewardTicketTableID", "extraRewardTicketBaseCount", "findHelperRewardDropBoxIds", "rewardLimitedDropBoxIds2" };
        // attributes a field above stands for, and those that are not changed here
        static readonly string[] Hidden = { Key, "durationTick", "playType", "clearType", "clearValue" };
        public static readonly string[] Fixed = { Key, "feature", "locale" };

        /// <summary>The fields in the order the editor lists them; a table's other attributes follow.</summary>
        public static readonly string[] Known =
        {
            Group, Fields, "lobbyFieldID", "dungeonLevel", "minUserCount", "maxUserCount", "isPartyOnly", "limitPlayerLevel", "gearScore",
            Duration, "timerType", "isExpireTimeOut", LimitEnter, ClearKill, "cooldownType", "cooldownValue", "limitDayOfWeeks",
            "rewardCount", "subRewardCount", "rewardExp", "rewardMeso", "rewardLimitedDropBoxIds", "rewardUnlimitedDropBoxIds",
            "userMissions", "partyMissions", "rankTableID", "roundID", "dungeonBanner", "bossIcon", "sortOrder", "feature", "locale",
        };

        /// <summary>The room table of a target: table/{region}/dungeonroom.xml, else table/dungeonroom.xml; null for none.</summary>
        public static string EntryOf(DataSet data, string archive)
        {
            Profile p = Profile.OfArchive(archive);
            M2dArchive arch;
            try { arch = data.Open(archive); }
            catch (ModKitException) { return null; }
            M2dEntry e = p == null ? null : arch.Find("table/" + p.Region + "/dungeonroom.xml");
            if (e == null) e = arch.Find(KmsEntry);
            return e == null ? null : e.Name.Replace('\\', '/');
        }

        public static bool IsEntry(string entry)
        {
            return Regex.IsMatch(entry.Replace('\\', '/'), "^table/([a-z]{2}/)?dungeonroom\\.xml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        /// <summary>True when the side reads the field.</summary>
        public static bool OnSide(string field, bool kms)
        {
            return Array.IndexOf(kms ? ServerOnly : KmsOnly, field) < 0;
        }

        public static bool IsHidden(string attr) { return Array.IndexOf(Hidden, attr) >= 0; }

        static string Num(string v) { return v == null || v == "0" ? "" : v; }

        /// <summary>A field in the editor's notation, read from a row of a side ("" for none).</summary>
        public static string Read(Func<string, string> get, bool kms, string field)
        {
            if (!OnSide(field, kms)) return "";
            if (field == Group)
            {
                string g = get(Group) ?? "";
                int n;
                if (kms && int.TryParse(g, NumberStyles.None, CultureInfo.InvariantCulture, out n) && n < GroupTypes.Length) return GroupTypes[n];
                return g;
            }
            if (field == Fields) return Ids(get(Fields), 0);
            if (kms) return field == LimitEnter || field == ClearKill ? Num(get(field)) : get(field) ?? "";
            if (field == Duration)
            {
                string t = get("durationTick");
                long ms;
                if (t == null) return get(Duration) ?? "";
                if (!long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out ms)) return t;
                return ms % 1000 == 0 ? (ms / 1000).ToString(CultureInfo.InvariantCulture) : (ms / 1000m).ToString(CultureInfo.InvariantCulture);
            }
            if (field == LimitEnter) return get("playType") == null ? Num(get(LimitEnter)) : get("playType") == "limitEnter" ? "1" : "";
            if (field == ClearKill) return get("clearType") == "monster" ? Num(get("clearValue")) : "";
            return get(field) ?? "";
        }

        // ids of a list without (width 0) or with leading zeros up to a width; other tokens stay
        static string Ids(string list, int width)
        {
            if (string.IsNullOrEmpty(list)) return "";
            string[] t = list.Split(',');
            for (int i = 0; i < t.Length; i++)
            {
                string s = t[i].Trim();
                long n;
                if (long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n))
                    s = width == 0 ? n.ToString(CultureInfo.InvariantCulture) : n.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
                t[i] = s;
            }
            return string.Join(",", t);
        }

        /// <summary>Writes a field given in the editor's notation to a row of a side ("" removes it); nothing on a side that does not read it.</summary>
        public static void Write(Func<string, string> get, Action<string, string> set, bool kms, string field, string value)
        {
            if (!OnSide(field, kms)) return;
            value = (value ?? "").Trim();
            string v = value.Length == 0 ? null : value;
            if (field == Group)
            {
                int n;
                int at = Array.IndexOf(GroupTypes, value);
                if (kms && at >= 0) v = at.ToString(CultureInfo.InvariantCulture);
                else if (!kms && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out n) && n < GroupTypes.Length) v = GroupTypes[n];
                set(Group, v);
            }
            else if (field == Fields) set(Fields, v == null ? null : Ids(value, kms ? 0 : 8));
            else if (kms) set(field, (field == LimitEnter || field == ClearKill) && value == "0" ? null : v);
            else if (field == Duration)
            {
                decimal sec;
                if (v == null) set("durationTick", null);
                else if (decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out sec))
                    set("durationTick", decimal.Truncate(sec * 1000).ToString(CultureInfo.InvariantCulture));
                else set("durationTick", value);
                set(Duration, null);
            }
            else if (field == LimitEnter)
            {
                if (v != null && value != "0") set("playType", "limitEnter");
                else if (get("playType") == "limitEnter") set("playType", "limitReward");
                set(LimitEnter, null);
            }
            else if (field == ClearKill)
            {
                if (v != null && value != "0") { set("clearType", "monster"); set("clearValue", v); }
                else if (get("clearType") == "monster") { set("clearType", "trigger"); set("clearValue", null); }
            }
            else set(field, v);
        }

        static bool IsNumber(string v) { long n; return long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out n); }

        /// <summary>What is wrong with a room row of a side (texts without the room's label).</summary>
        public static List<string> CheckRoom(Func<string, string> get, bool kms)
        {
            var l = new List<string>();
            string id = get(Key) ?? "";
            long n;
            if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out n) || n <= 0) l.Add("id \"" + id + "\" is not a number above 0");
            string fields = get(Fields) ?? "";
            if (fields.Trim().Length == 0) l.Add("no field: the dungeon has no map to enter");
            else
                foreach (string t in fields.Split(','))
                    if (!IsNumber(t.Trim())) l.Add("fieldIDs: \"" + t.Trim() + "\" is not a map id");
            foreach (string f in new[] { "lobbyFieldID", "dungeonLevel", "minUserCount", "maxUserCount", "limitPlayerLevel", "gearScore", "rewardCount", "rankTableID", "roundID", kms ? Duration : "durationTick" })
            {
                string v = get(f);
                if (!string.IsNullOrEmpty(v) && !IsNumber(v)) l.Add(f + " \"" + v + "\" is not a number");
            }
            long min, max;
            if (long.TryParse(get("minUserCount") ?? "", NumberStyles.None, CultureInfo.InvariantCulture, out min)
                && long.TryParse(get("maxUserCount") ?? "", NumberStyles.None, CultureInfo.InvariantCulture, out max))
            {
                if (max == 0) l.Add("maxUserCount is 0: nobody can enter");
                else if (min > max) l.Add("minUserCount " + min + " is above maxUserCount " + max);
            }
            string g = get(Group);
            if (!string.IsNullOrEmpty(g))
            {
                int gn;
                if (kms ? !(int.TryParse(g, NumberStyles.None, CultureInfo.InvariantCulture, out gn) && gn < GroupTypes.Length) : Array.IndexOf(GroupTypes, g) < 0)
                    l.Add("groupType \"" + g + "\" is not a group type of the " + (kms ? "kms client (0 to " + (GroupTypes.Length - 1) + ")" : "server (" + string.Join(", ", GroupTypes) + ")"));
            }
            string timer = get("timerType");
            if (!string.IsNullOrEmpty(timer) && timer != "clock" && timer != "gauge") l.Add("timerType \"" + timer + "\" is not clock or gauge");
            if (!kms)
            {
                if (get("clearType") == "monster" && !IsNumber(get("clearValue") ?? "")) l.Add("clearType is monster and clearValue names no npc");
                string play = get("playType");
                if (!string.IsNullOrEmpty(play) && play != "limitEnter" && play != "limitReward") l.Add("playType \"" + play + "\" is not limitEnter or limitReward");
                string cool = get("cooldownType");
                if (!string.IsNullOrEmpty(cool) && cool != "nextDay" && cool != "dayOfWeeks") l.Add("cooldownType \"" + cool + "\" is not nextDay or dayOfWeeks");
            }
            return l;
        }
    }

    /// <summary>One side a room is written to: the room table of an archive.</summary>
    public sealed class DungeonSide
    {
        public string Archive, Entry;
        public bool Kms;
        public TableDraft Table;
    }

    /// <summary>A dungeon: its row on each side (null where a side does not have it).</summary>
    public sealed class DungeonRoom
    {
        public readonly TableRow[] Rows;
        internal DungeonRoom(int sides) { Rows = new TableRow[sides]; }

        public TableRow Shown { get { foreach (TableRow r in Rows) if (r != null) return r; return null; } }
        /// <summary>True when the mod adds the room on every side that has it (a room one side ships is not new).</summary>
        public bool IsAdded { get { bool any = false; foreach (TableRow r in Rows) if (r != null) { if (!r.IsAdded) return false; any = true; } return any; } }
        public bool IsDeleted { get { foreach (TableRow r in Rows) if (r != null && !r.IsDeleted) return false; return true; } }
    }

    /// <summary>Edits dungeon rooms of every side in one mod.</summary>
    public sealed class DungeonEditor
    {
        public readonly DataSet Data;
        public readonly Mod Mod;
        public readonly List<DungeonSide> Sides = new List<DungeonSide>();
        public readonly List<DungeonRoom> Rooms = new List<DungeonRoom>();
        /// <summary>The other enabled mods (for the ids they take); null for none.</summary>
        public Func<IEnumerable<Mod>> Others;

        static string Esc(string v) { return v.Replace("&", "&amp;").Replace("<", "&lt;").Replace("\"", "&quot;"); }

        public DungeonEditor(DataSet data, Mod mod)
        {
            Data = data;
            Mod = mod;
            foreach (KeyValuePair<string, string> a in SideArchives(data, mod))
                Sides.Add(new DungeonSide { Archive = a.Key, Entry = a.Value, Kms = Profile.OfArchive(a.Key) == Profile.KmsClient, Table = new TableDraft(data, mod, a.Key, a.Value, null) });
            if (Sides.Count == 0) throw new ModKitException("no target of the data set has a dungeon room table (" + DungeonRules.KmsEntry + ")");
            Pair();
        }

        /// <summary>The archives a room is written to and their room table: the kms client's and the server's Xml.m2d, and
        /// the global client's when the data set has no server (else the server's lines are planned for the global
        /// client too) or when the mod has lines of its own there.</summary>
        public static List<KeyValuePair<string, string>> SideArchives(DataSet data, Mod mod)
        {
            var l = new List<KeyValuePair<string, string>>();
            foreach (Profile p in new[] { Profile.KmsClient, Profile.Server, Profile.GmsClient })
            {
                if (!data.Has(p)) continue;
                string a = p.ArchiveIn("x:Xml.m2d");
                if (p == Profile.GmsClient && data.Has(Profile.Server) && !HasLines(mod, a)) continue;
                string entry = DungeonRules.EntryOf(data, a);
                if (entry != null) l.Add(new KeyValuePair<string, string>(a, entry));
            }
            return l;
        }

        static bool HasLines(Mod mod, string archive)
        {
            if (mod == null) return false;
            foreach (ModOp op in mod.Ops)
            {
                var ed = op as EditOp;
                if (ed != null && string.Equals(ed.Archive, archive, StringComparison.OrdinalIgnoreCase) && DungeonRules.IsEntry(ed.Entry)) return true;
            }
            return false;
        }

        void Pair()
        {
            Rooms.Clear();
            // rows that share an id on one side pair in their order: the n-th with the n-th
            var byId = new Dictionary<string, List<DungeonRoom>>();
            for (int i = 0; i < Sides.Count; i++)
            {
                var seen = new Dictionary<string, int>();
                foreach (TableRow r in Sides[i].Table.Rows)
                {
                    string k = Sides[i].Table.Get(r, DungeonRules.Key);
                    if (r.Base.Name != DungeonRules.Rows || string.IsNullOrEmpty(k)) continue;
                    List<DungeonRoom> list;
                    if (!byId.TryGetValue(k, out list)) byId[k] = list = new List<DungeonRoom>();
                    int n;
                    seen.TryGetValue(k, out n);
                    seen[k] = n + 1;
                    if (n >= list.Count)
                    {
                        var made = new DungeonRoom(Sides.Count);
                        list.Add(made);
                        Rooms.Add(made);
                    }
                    list[n].Rows[i] = r;
                }
            }
        }

        // ---- reading ----

        Func<string, string> Raw(int side, DungeonRoom d) { TableDraft t = Sides[side].Table; TableRow r = d.Rows[side]; return a => t.Get(r, a); }

        /// <summary>A field in the editor's notation on one side ("" when the side has no such room or does not read the field).</summary>
        public string Get(DungeonRoom d, int side, string field)
        {
            if (d.Rows[side] == null) return "";
            if (field == "id") field = DungeonRules.Key;
            return DungeonRules.Read(Raw(side, d), Sides[side].Kms, field);
        }

        /// <summary>A field in the editor's notation, of the first side that has the room and reads the field.</summary>
        public string Get(DungeonRoom d, string field)
        {
            for (int i = 0; i < Sides.Count; i++)
                if (d.Rows[i] != null && DungeonRules.OnSide(field, Sides[i].Kms)) return Get(d, i, field);
            return "";
        }

        public long Id(DungeonRoom d) { long id; return long.TryParse(Get(d, DungeonRules.Key), NumberStyles.None, CultureInfo.InvariantCulture, out id) ? id : 0; }

        /// <summary>True when the sides that have the room and read the field give it different values.</summary>
        public bool Differs(DungeonRoom d, string field)
        {
            bool any = false;
            string first = null;
            for (int i = 0; i < Sides.Count; i++)
            {
                if (d.Rows[i] == null || !DungeonRules.OnSide(field, Sides[i].Kms)) continue;
                string v = Get(d, i, field);
                if (!any) { first = v; any = true; }
                else if (v != first) return true;
            }
            return false;
        }

        /// <summary>The fields of a room: the known ones, then every other attribute its rows or tables have.</summary>
        public List<string> Fields(DungeonRoom d)
        {
            var l = new List<string>(DungeonRules.Known);
            foreach (DungeonSide s in Sides)
                foreach (string c in s.Table.Columns)
                    if (!DungeonRules.IsHidden(c) && !l.Contains(c)) l.Add(c);
            return l;
        }

        /// <summary>The maps of the room.</summary>
        public List<long> Maps(DungeonRoom d)
        {
            var l = new List<long>();
            foreach (string t in Get(d, DungeonRules.Fields).Split(','))
            {
                long n;
                if (long.TryParse(t.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n)) l.Add(n);
            }
            return l;
        }

        /// <summary>The sides (archive names) that do not have the room.</summary>
        public List<string> MissingOn(DungeonRoom d)
        {
            var l = new List<string>();
            for (int i = 0; i < Sides.Count; i++) if (d.Rows[i] == null) l.Add(Sides[i].Archive);
            return l;
        }

        public bool IsChanged(DungeonRoom d)
        {
            foreach (TableRow r in d.Rows) if (r != null && (r.IsChanged || r.IsDeleted)) return true;
            string id = Get(d, DungeonRules.Key);
            foreach (NameTable n in names.Values)
            {
                List<TableRow> rows;
                if (n == null || !n.ById.TryGetValue(id, out rows)) continue;
                foreach (TableRow r in rows) if (r.IsAdded || r.IsChanged) return true;
            }
            return false;
        }

        /// <summary>Rooms whose id, name or a map id holds text.</summary>
        public List<DungeonRoom> Find(string text)
        {
            text = (text ?? "").Trim();
            var l = new List<DungeonRoom>();
            foreach (DungeonRoom d in Rooms)
            {
                if (text.Length > 0 && Get(d, DungeonRules.Key).IndexOf(text, StringComparison.Ordinal) < 0
                    && Text(d, "name").IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0
                    && ("," + Get(d, DungeonRules.Fields)).IndexOf("," + text, StringComparison.Ordinal) < 0) continue;
                l.Add(d);
            }
            return l;
        }

        // ---- name and description ----

        sealed class NameTable
        {
            internal TableDraft Draft;
            internal readonly Dictionary<string, List<TableRow>> ById = new Dictionary<string, List<TableRow>>();
        }

        readonly Dictionary<string, NameTable> names = new Dictionary<string, NameTable>();
        /// <summary>The language files that could not be read so far, one line each (Check lists them too).</summary>
        public readonly List<string> TextProblems = new List<string>();
        readonly Dictionary<string, List<string>> langs = new Dictionary<string, List<string>>();

        /// <summary>The language folders of an archive that hold string/{lang}/stringfieldenterance.xml.</summary>
        public List<string> Languages(string archive)
        {
            List<string> list;
            if (langs.TryGetValue(archive, out list)) return list;
            list = new List<string>();
            var rx = new Regex("^string/([^/]+)/" + Regex.Escape(DungeonRules.NameFile) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            foreach (M2dEntry e in Data.Open(archive).Entries)
            {
                Match m = rx.Match(e.Name.Replace('\\', '/'));
                if (m.Success && !list.Contains(m.Groups[1].Value)) list.Add(m.Groups[1].Value);
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            langs[archive] = list;
            return list;
        }

        NameTable Names(string archive, string lang)
        {
            string key = archive + "\n" + lang;
            NameTable n;
            if (names.TryGetValue(key, out n)) return n;
            try
            {
                n = new NameTable { Draft = new TableDraft(Data, Mod, archive, "string/" + lang + "/" + DungeonRules.NameFile, null) };
                foreach (TableRow r in n.Draft.Rows)
                {
                    if (r.Base.Name != "key" || r.IsDeleted) continue;
                    string id = n.Draft.Get(r, "id") ?? "";
                    List<TableRow> rows;
                    if (!n.ById.TryGetValue(id, out rows)) n.ById[id] = rows = new List<TableRow>();
                    rows.Add(r);
                }
            }
            catch (ModKitException ex)
            {
                n = null;
                TextProblems.Add(archive + ": string/" + lang + "/" + DungeonRules.NameFile + " could not be read (" + ex.Message + "): names are not written there");
            }
            names[key] = n;
            return n;
        }

        /// <summary>The room's name or uiDescription: the English one of the first side that has a text file (else its
        /// first language), of the first row of the id that has it; "" for none.</summary>
        public string Text(DungeonRoom d, string field)
        {
            string id = Get(d, DungeonRules.Key);
            foreach (DungeonSide side in Sides)
            {
                List<string> l = Languages(side.Archive);
                if (l.Count == 0) continue;
                NameTable n = Names(side.Archive, l.Contains("en") ? "en" : l[0]);
                List<TableRow> rows;
                if (n == null || !n.ById.TryGetValue(id, out rows)) continue;
                foreach (TableRow r in rows)
                {
                    string v = n.Draft.Get(r, field);
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            return "";
        }

        /// <summary>Sets the room's name or uiDescription in every language of every side that has the room: on every
        /// row of the id, or on a new row where a text file has none.</summary>
        public void SetText(DungeonRoom d, string field, string value)
        {
            if (field != "name" && field != "uiDescription") throw new ModKitException("a dungeon has a name and a uiDescription, no text \"" + field + "\"");
            value = value ?? "";
            string id = Get(d, DungeonRules.Key);
            for (int i = 0; i < Sides.Count; i++)
            {
                if (d.Rows[i] == null) continue;
                foreach (string lang in Languages(Sides[i].Archive)) SetTextAt(i, lang, id, field, value);
            }
        }

        /// <summary>The text of an id in one language file of a side: of the first row that has it; "" for none.</summary>
        string TextAt(int side, string lang, string id, string field)
        {
            NameTable n = Names(Sides[side].Archive, lang);
            List<TableRow> rows;
            if (n == null || !n.ById.TryGetValue(id, out rows)) return "";
            foreach (TableRow r in rows)
            {
                string v = n.Draft.Get(r, field);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return "";
        }

        void SetTextAt(int i, string lang, string id, string field, string value)
        {
            NameTable n = Names(Sides[i].Archive, lang);
            if (n == null) return;
            List<TableRow> rows;
            if (n.ById.TryGetValue(id, out rows))
            {
                foreach (TableRow r in rows.ToArray())
                {
                    n.Draft.Set(r, field, value.Length == 0 ? null : value);
                    if (r.IsAdded && string.IsNullOrEmpty(n.Draft.Get(r, "name")) && string.IsNullOrEmpty(n.Draft.Get(r, "uiDescription")))
                    {
                        n.Draft.Delete(r);
                        rows.Remove(r);
                    }
                }
                if (rows.Count == 0) n.ById.Remove(id);
            }
            else if (value.Length > 0)
                n.ById[id] = new List<TableRow> { n.Draft.AddRow("<key id=\"" + Esc(id) + "\" " + field + "=\"" + Esc(value) + "\" />") };
        }

        // ---- checks ----

        /// <summary>What is wrong with the room as the mod leaves it, side by side.</summary>
        public List<string> Check(DungeonRoom d)
        {
            var l = new List<string>();
            if (d.IsDeleted) return l;
            for (int i = 0; i < Sides.Count; i++)
            {
                if (d.Rows[i] == null || d.Rows[i].IsDeleted) continue;
                foreach (string t in DungeonRules.CheckRoom(Raw(i, d), Sides[i].Kms))
                {
                    string line = Sides.Count > 1 ? Sides[i].Archive + ": " + t : t;
                    if (!l.Contains(line)) l.Add(line);
                }
                int same = 0;
                string id = Sides[i].Table.Get(d.Rows[i], DungeonRules.Key);
                // rows of one id with another feature or locale are variants, not duplicates
                string feature = Sides[i].Table.Get(d.Rows[i], "feature") ?? "", locale = Sides[i].Table.Get(d.Rows[i], "locale") ?? "";
                foreach (TableRow r in Sides[i].Table.Rows)
                    if (!r.IsDeleted && r.Base.Name == DungeonRules.Rows && Sides[i].Table.Get(r, DungeonRules.Key) == id
                        && (Sides[i].Table.Get(r, "feature") ?? "") == feature && (Sides[i].Table.Get(r, "locale") ?? "") == locale) same++;
                if (same > 1)
                    l.Add(Sides[i].Archive + ": the id is in the table " + same + " times" + (Sides[i].Kms ? ": the client reads the last row" : ": the server's data ingest fails"));
            }
            foreach (string a in MissingOn(d)) l.Add("not in " + a + ": client and server read dungeons from their own table");
            foreach (string t in TextProblems) if (!l.Contains(t)) l.Add(t);
            return l;
        }

        // ---- editing ----

        void NotDeleted(DungeonRoom d)
        {
            if (d.IsDeleted) throw new ModKitException("dungeon " + Get(d, DungeonRules.Key) + " is removed; restore it first");
        }

        static void CanSet(DungeonRoom d, string field)
        {
            if (field == "id" || Array.IndexOf(DungeonRules.Fixed, field) >= 0)
                throw new ModKitException(field + " of a dungeon is not changed here; " + (d.IsAdded ? "remove the new dungeon and add it with the other id" : "clone the dungeon for a new one"));
            if (DungeonRules.IsHidden(field))
                throw new ModKitException(field + " is set through its field (" + DungeonRules.Duration + ", " + DungeonRules.LimitEnter + ", " + DungeonRules.ClearKill + ")");
            if (!TableDraft.IsAttrName(field)) throw new ModKitException("\"" + field + "\" is not an attribute name");
        }

        /// <summary>Sets a field, given in the editor's notation, on one side.</summary>
        public void Set(DungeonRoom d, int side, string field, string value)
        {
            CanSet(d, field);
            NotDeleted(d);
            if (d.Rows[side] == null) throw new ModKitException(Sides[side].Archive + " has no dungeon " + Get(d, DungeonRules.Key));
            if (!DungeonRules.OnSide(field, Sides[side].Kms)) throw new ModKitException(Sides[side].Archive + " does not read " + field);
            TableDraft t = Sides[side].Table;
            TableRow r = d.Rows[side];
            DungeonRules.Write(a => t.Get(r, a), (a, v) => t.Set(r, a, v), Sides[side].Kms, field, value);
        }

        /// <summary>Sets a field, given in the editor's notation, on every side that has the room and reads the field.</summary>
        public void Set(DungeonRoom d, string field, string value)
        {
            CanSet(d, field);
            NotDeleted(d);
            for (int i = 0; i < Sides.Count; i++)
                if (d.Rows[i] != null && DungeonRules.OnSide(field, Sides[i].Kms)) Set(d, i, field, value);
        }

        IEnumerable<Mod> OtherMods()
        {
            var l = new List<Mod>();
            if (Others != null) foreach (Mod m in Others()) if (m != Mod && m.Id != Mod.Id) l.Add(m);
            return l;
        }

        /// <summary>Throws when a new dungeon cannot take the id.</summary>
        public void CheckNewId(long id)
        {
            if (id < DungeonRules.From || id > DungeonRules.To)
                throw new ModKitException("new dungeon ids are " + DungeonRules.From + " to " + DungeonRules.To + "; " + id + " is outside");
            foreach (DungeonRoom o in Rooms) if (Id(o) == id) throw new ModKitException("dungeon " + id + " is there already");
            foreach (Mod m in OtherMods())
            {
                if (AddedIds(m).Contains(id)) throw new ModKitException("the enabled mod " + m.Id + " adds a dungeon " + id + " already");
                foreach (IdRange r in m.Ids)
                    if (r.Space == DungeonRules.Space && id >= r.From && id <= r.To)
                        throw new ModKitException("the dungeon id " + id + " is reserved by the enabled mod " + m.Id);
            }
        }

        static string RowXml(List<KeyValuePair<string, string>> attrs)
        {
            var sb = new StringBuilder("<" + DungeonRules.Rows);
            foreach (KeyValuePair<string, string> kv in attrs) sb.Append(' ').Append(kv.Key).Append("=\"").Append(Esc(kv.Value)).Append('"');
            return sb.Append(" />").ToString();
        }

        static string BagGet(List<KeyValuePair<string, string>> attrs, string a)
        {
            foreach (KeyValuePair<string, string> kv in attrs) if (kv.Key == a) return kv.Value;
            return null;
        }

        static void BagSet(List<KeyValuePair<string, string>> attrs, string a, string v)
        {
            for (int i = 0; i < attrs.Count; i++)
                if (attrs[i].Key == a)
                {
                    if (v == null) attrs.RemoveAt(i); else attrs[i] = new KeyValuePair<string, string>(a, v);
                    return;
                }
            if (v != null) attrs.Add(new KeyValuePair<string, string>(a, v));
        }

        DungeonRoom Make(long newId, DungeonRoom source)
        {
            CheckNewId(newId);
            string id = newId.ToString(CultureInfo.InvariantCulture);
            var made = new DungeonRoom(Sides.Count);
            for (int i = 0; i < Sides.Count; i++)
            {
                DungeonSide side = Sides[i];
                var row = new List<KeyValuePair<string, string>>();
                Func<string, string> get = a => BagGet(row, a);
                Action<string, string> set = (a, v) => BagSet(row, a, v);
                set(DungeonRules.Key, id);
                if (source == null)
                {
                    // the least a room needs to show up and be entered alone or in a party
                    DungeonRules.Write(get, set, side.Kms, DungeonRules.Group, "normal");
                    foreach (string[] kv in new[] { new[] { "dungeonLevel", "1" }, new[] { "minUserCount", "1" }, new[] { "maxUserCount", "4" }, new[] { "limitPlayerLevel", "1" }, new[] { "timerType", "clock" } })
                        set(kv[0], kv[1]);
                    if (!side.Kms) { set("cooldownType", "nextDay"); set("playType", "limitReward"); set("clearType", "trigger"); }
                }
                else if (source.Rows[i] != null)
                {
                    // the side's own row with every attribute, in its notation
                    TableRow from = source.Rows[i];
                    foreach (string c in side.Table.Columns)
                    {
                        string v = c == DungeonRules.Key ? null : side.Table.Get(from, c);
                        if (v != null) set(c, v);
                    }
                }
                else
                {
                    // a side that did not have the room takes the fields it reads, in its notation
                    int at = Array.IndexOf(source.Rows, source.Shown);
                    TableRow from = source.Rows[at];
                    Func<string, string> raw = a => Sides[at].Table.Get(from, a);
                    foreach (string c in Fields(source))
                    {
                        if (Array.IndexOf(DungeonRules.Fixed, c) >= 0 || !DungeonRules.OnSide(c, Sides[at].Kms)) continue;
                        string v = DungeonRules.Read(raw, Sides[at].Kms, c);
                        if (v.Length > 0) DungeonRules.Write(get, set, side.Kms, c, v);
                    }
                    if (!side.Kms)
                    {
                        if (get("cooldownType") == null) set("cooldownType", "nextDay");
                        if (get("playType") == null) set("playType", "limitReward");
                        if (get("clearType") == null) set("clearType", "trigger");
                    }
                }
                made.Rows[i] = side.Table.AddRow(RowXml(row));
            }
            IdSpaces.Reserve(Mod, DungeonRules.Space, newId);
            Rooms.Add(made);
            return made;
        }

        /// <summary>A new dungeon on every side: a normal one for 1 to 4 players, without a map yet.</summary>
        public DungeonRoom Add(long newId) { return Make(newId, null); }

        /// <summary>A new dungeon that copies source as it is shown (each side its own row; name and description in each language), with the new id.</summary>
        public DungeonRoom Clone(DungeonRoom source, long newId)
        {
            if (source.IsDeleted) throw new ModKitException("dungeon " + Get(source, DungeonRules.Key) + " is removed; restore it before copying");
            DungeonRoom made = Make(newId, source);
            // each language file gets the source's own text of that language; where the source has none there, the
            // text the source is shown with
            string from = Get(source, DungeonRules.Key), to = Get(made, DungeonRules.Key);
            foreach (string f in new[] { "name", "uiDescription" })
            {
                string shown = Text(source, f);
                for (int i = 0; i < Sides.Count; i++)
                {
                    if (made.Rows[i] == null) continue;
                    foreach (string lang in Languages(Sides[i].Archive))
                    {
                        string t = source.Rows[i] == null ? "" : TextAt(i, lang, from, f);
                        if (t.Length == 0) t = shown;
                        if (t.Length > 0) SetTextAt(i, lang, to, f, t);
                    }
                }
            }
            return made;
        }

        /// <summary>Removes a dungeon: a new one (added on every side that has it) goes away with its texts and its id
        /// is free again; one that a side ships is deleted on every side and can be restored (its text rows stay:
        /// nothing reads them without the room).</summary>
        public void Remove(DungeonRoom d)
        {
            bool added = d.IsAdded;
            long id = Id(d);
            if (added) { SetText(d, "name", ""); SetText(d, "uiDescription", ""); }
            for (int i = 0; i < Sides.Count; i++)
            {
                if (d.Rows[i] == null) continue;
                bool mine = d.Rows[i].IsAdded;
                Sides[i].Table.Delete(d.Rows[i]);
                // a row the mod added on one side of a shipped room goes away; the shipped rows stay, as deleted
                if (mine && !added) d.Rows[i] = null;
            }
            if (!added) return;
            Rooms.Remove(d);
            bool still = false;
            foreach (DungeonRoom o in Rooms) if (Id(o) == id) still = true;
            if (!still) IdSpaces.Unreserve(Mod, DungeonRules.Space, id);
        }

        /// <summary>Takes back the removal of a shipped dungeon.</summary>
        public void Restore(DungeonRoom d)
        {
            for (int i = 0; i < Sides.Count; i++) if (d.Rows[i] != null) Sides[i].Table.Restore(d.Rows[i]);
        }

        /// <summary>Drops every change the mod makes to a shipped dungeon: its rows and its texts.</summary>
        public void Revert(DungeonRoom d)
        {
            if (d.IsAdded) throw new ModKitException("dungeon " + Get(d, DungeonRules.Key) + " is new; remove it instead");
            string id = Get(d, DungeonRules.Key);
            for (int i = 0; i < Sides.Count; i++)
            {
                if (d.Rows[i] == null) continue;
                Sides[i].Table.Restore(d.Rows[i]);
                Sides[i].Table.RevertCells(d.Rows[i]);
            }
            foreach (NameTable n in names.Values)
            {
                List<TableRow> rows;
                if (n == null || !n.ById.TryGetValue(id, out rows)) continue;
                foreach (TableRow r in rows.ToArray())
                {
                    if (r.IsAdded) { n.Draft.Delete(r); rows.Remove(r); }
                    else n.Draft.RevertCells(r);
                }
                if (rows.Count == 0) n.ById.Remove(id);
            }
        }

        /// <summary>The dungeon ids a mod's &lt;add&gt; rows of a room table give (the id it sets, else the id of its row text).</summary>
        public static List<long> AddedIds(Mod mod)
        {
            var l = new List<long>();
            var inRow = new Regex(@"^\s*<\w+[^>]*?\s" + DungeonRules.Key + @"\s*=\s*[""'](\d+)[""']");
            foreach (ModOp op in mod.Ops)
            {
                var ed = op as EditOp;
                if (ed == null || !DungeonRules.IsEntry(ed.Entry)) continue;
                foreach (RowOp r in ed.Rows)
                {
                    if (r.Kind != RowOpKind.Add) continue;
                    string id = null;
                    foreach (AttrOp a in r.Attrs) if (a.Kind == AttrOpKind.Set && a.Name == DungeonRules.Key && a.At == null) id = a.Value;
                    if (id == null && r.Xml != null) { Match mt = inRow.Match(r.Xml); if (mt.Success) id = mt.Groups[1].Value; }
                    long n;
                    if (id != null && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out n) && !l.Contains(n)) l.Add(n);
                }
            }
            return l;
        }

        /// <summary>The lowest free ids of the range of new dungeons: not shipped on any side and not taken by the mods.</summary>
        public List<long> NextIds(IEnumerable<Mod> mods, int count, Action<ProgressInfo> progress)
        {
            var all = new List<Mod>(mods ?? new Mod[0]);
            if (!all.Contains(Mod)) all.Add(Mod);
            var taken = new HashSet<long>();
            foreach (DungeonRoom d in Rooms) taken.Add(Id(d));
            foreach (Mod m in all) if (m != Mod) foreach (long id in AddedIds(m)) taken.Add(id);
            List<long> free = IdSpaces.NextFreeGroupIds(Data, all, DungeonRules.Space, count + taken.Count, progress);
            free.RemoveAll(taken.Contains);
            if (free.Count > count) free.RemoveRange(count, free.Count - count);
            return free;
        }
    }
}
