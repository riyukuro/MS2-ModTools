// Levels and experience as a mod edit: two tables of rows by level.
//
// - nextexp: table/nextexp.xml of Xml.m2d, <exp level value>, the experience from a level to the next one. Every
//   side has it (the kms client, the global client and the server's pack), as shipped with the same values. The
//   client (CExpTable::LoadNextExp, src/ms2/commonfiles/exptable.cpp) reads the levels 1..99 only and draws the
//   experience bar from it; the server decides when a character levels up. An edit is written to every side the
//   data set has, so the bar and the level-up agree.
// - monster exp: table/Server/monsterExpTable.xml of the server's Server.m2d, <exp level common elite boss
//   dungeonElite dungeonBoss>, the experience a monster of that level and kind gives. Server only.
//
// An edit is a <change> of the row (exp[level=N]) with set attributes, one edit block per archive, not mirrored
// (each side gets its own lines). Setting the shipped value back takes the line out again. Scale multiplies a range
// of levels by a percentage.
using System;
using System.Collections.Generic;
using System.Globalization;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    /// <summary>One table of rows by level: where it is and the number fields of a row.</summary>
    public sealed class LevelTable
    {
        public string Name, Entry, Row = "exp", Key = "level";
        public string[] Archives, Fields;
        /// <summary>The levels the client reads (0: no client reads the table).</summary>
        public int ClientMax;

        public static readonly LevelTable NextExp = new LevelTable
        {
            Name = "nextexp", Entry = "table/nextexp.xml", Fields = new[] { "value" }, ClientMax = 99,
            Archives = new[] { EntityEditor.ClientXml, EntityEditor.GmsXml, EntityEditor.ServerXml },
        };

        public static readonly LevelTable MonsterExp = new LevelTable
        {
            Name = "monster exp", Entry = "table/Server/monsterExpTable.xml",
            Fields = new[] { "common", "elite", "boss", "dungeonElite", "dungeonBoss" },
            Archives = new[] { "server:Server.m2d" },
        };

        public static readonly LevelTable[] All = { NextExp, MonsterExp };
    }

    /// <summary>One row of a table on one side, as shipped (or as a file the mod replaces whole has it).</summary>
    public sealed class LevelRow
    {
        public int Level;
        public string At;   // exp[level=N]
        public readonly Dictionary<string, string> Shipped = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public sealed class LevelExp
    {
        public readonly DataSet Data;
        public readonly Mod Mod;
        readonly Dictionary<string, Dictionary<int, LevelRow>> rows = new Dictionary<string, Dictionary<int, LevelRow>>(StringComparer.Ordinal);

        public LevelExp(DataSet data, Mod mod) { Data = data; Mod = mod; }

        static string Key(LevelTable t, string archive) { return t.Entry + "\n" + archive; }

        /// <summary>The archives of the table the data set has (with the entry).</summary>
        public List<string> Sides(LevelTable t)
        {
            var l = new List<string>();
            foreach (string a in t.Archives) if (Rows(t, a).Count > 0) l.Add(a);
            return l;
        }

        /// <summary>The rows of one side by level, as shipped (empty when the side or the entry is not there).</summary>
        public Dictionary<int, LevelRow> Rows(LevelTable t, string archive)
        {
            Dictionary<int, LevelRow> d;
            if (rows.TryGetValue(Key(t, archive), out d)) return d;
            d = new Dictionary<int, LevelRow>();
            rows[Key(t, archive)] = d;
            byte[] b = Data.Exists(archive) ? Shipped(archive, t.Entry) : null;
            if (b == null) return d;
            XmlDoc doc = XmlDoc.Parse(b, archive + " " + t.Entry);
            if (doc.Root == null) return d;
            foreach (XmlElem e in doc.Root.Children)
            {
                int level;
                if (e.Name != t.Row || !int.TryParse(e.Attr(t.Key), NumberStyles.Integer, CultureInfo.InvariantCulture, out level) || d.ContainsKey(level)) continue;
                var r = new LevelRow { Level = level, At = RowPath.Canonical(e) };
                foreach (string f in t.Fields) r.Shipped[f] = e.Attr(f) ?? "";
                d[level] = r;
            }
            return d;
        }

        /// <summary>The levels of the table, of every side, in order.</summary>
        public List<int> Levels(LevelTable t)
        {
            var s = new SortedDictionary<int, bool>();
            foreach (string a in t.Archives) foreach (int l in Rows(t, a).Keys) s[l] = true;
            return new List<int>(s.Keys);
        }

        byte[] Shipped(string archive, string entry)
        {
            var trig = new TriggerEditor(Data, Mod);
            EntryOp op = trig.OpOf(archive, entry);
            if (op != null) return trig.ModBytes(op);
            M2dArchive a = Data.Open(archive);
            M2dEntry e = a.Find(entry);
            return e == null ? null : a.Read(e);
        }

        // ---- values ----

        LevelRow Row(LevelTable t, string archive, int level)
        {
            LevelRow r;
            return Rows(t, archive).TryGetValue(level, out r) ? r : null;
        }

        /// <summary>The shipped value of a level on the table's first side that has the row (null: no side has it).</summary>
        public string GetShipped(LevelTable t, int level, string field)
        {
            foreach (string a in Sides(t))
            {
                LevelRow r = Row(t, a, level);
                if (r != null) return r.Shipped[field];
            }
            return null;
        }

        /// <summary>The value of a level on one side with the mod (null: the side has no such row).</summary>
        public string Get(LevelTable t, string archive, int level, string field)
        {
            LevelRow r = Row(t, archive, level);
            if (r == null) return null;
            RowOp op = Change(t, archive, r, false);
            if (op != null)
                for (int i = op.Attrs.Count - 1; i >= 0; i--)
                    if (op.Attrs[i].Name == field && string.IsNullOrEmpty(op.Attrs[i].At))
                        return op.Attrs[i].Kind == AttrOpKind.Set ? op.Attrs[i].Value ?? "" : "";
            return r.Shipped[field];
        }

        /// <summary>The value of a level with the mod on the first side that has the row (null: no side has it).</summary>
        public string Get(LevelTable t, int level, string field)
        {
            foreach (string a in Sides(t))
            {
                string v = Get(t, a, level, field);
                if (v != null) return v;
            }
            return null;
        }

        /// <summary>The value as the tables' readers take it (null: not a whole number).</summary>
        public static long? Number(string v)
        {
            long n;
            return long.TryParse((v ?? "").Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n) ? n : (long?)null;
        }

        public bool IsEdited(LevelTable t, int level)
        {
            foreach (string a in Sides(t))
            {
                LevelRow r = Row(t, a, level);
                if (r == null) continue;
                foreach (string f in t.Fields) if (Get(t, a, level, f) != r.Shipped[f]) return true;
            }
            return false;
        }

        public int EditedCount(LevelTable t)
        {
            int n = 0;
            foreach (int l in Levels(t)) if (IsEdited(t, l)) n++;
            return n;
        }

        /// <summary>Why a value is refused (null: it is a whole number of 0 or more).</summary>
        public static string Problem(LevelTable t, int level, string field, string value)
        {
            long? n = Number(value);
            if (n == null) return t.Name + " level " + level + " " + field + " \"" + (value ?? "").Trim() + "\" is not a whole number";
            if (n.Value < 0) return t.Name + " level " + level + " " + field + " " + n.Value + " is below 0";
            return null;
        }

        /// <summary>
        /// Sets a field of a level on every side that has the row. The shipped value of a side takes its line out.
        /// Returns the sides that changed.
        /// </summary>
        public int Set(LevelTable t, int level, string field, string value)
        {
            if (Array.IndexOf(t.Fields, field) < 0) throw new ModKitException(t.Name + " has no field \"" + field + "\"");
            value = (value ?? "").Trim();
            string bad = Problem(t, level, field, value);
            if (bad != null) throw new ModKitException(bad);
            value = Number(value).Value.ToString(CultureInfo.InvariantCulture);
            int n = 0, had = 0;
            foreach (string a in Sides(t))
            {
                LevelRow r = Row(t, a, level);
                if (r == null) continue;
                had++;
                if (SetOne(t, a, r, field, value)) n++;
            }
            if (had == 0) throw new ModKitException(t.Name + " has no row of level " + level);
            return n;
        }

        bool SetOne(LevelTable t, string archive, LevelRow r, string field, string value)
        {
            if (Get(t, archive, r.Level, field) == value) return false;
            RowOp op = Change(t, archive, r, false);
            if (op != null) op.Attrs.RemoveAll(x => x.Name == field && string.IsNullOrEmpty(x.At));
            if (value == r.Shipped[field])
            {
                if (op != null && op.Attrs.Count == 0) Drop(t, archive, op);
                return true;
            }
            if (op == null) op = Change(t, archive, r, true);
            op.Attrs.Add(new AttrOp { Kind = AttrOpKind.Set, Name = field, Value = value });
            return true;
        }

        /// <summary>
        /// Multiplies the shipped values of the levels from..to (of one field, or of every field when field is null)
        /// by percent/100, rounded to the nearest whole number; a value above 0 stays at least 1. Returns the levels
        /// that changed.
        /// </summary>
        public int Scale(LevelTable t, int from, int to, string field, double percent)
        {
            if (from > to) { int x = from; from = to; to = x; }
            if (double.IsNaN(percent) || double.IsInfinity(percent) || percent <= 0) throw new ModKitException("the percentage must be above 0");
            if (field != null && Array.IndexOf(t.Fields, field) < 0) throw new ModKitException(t.Name + " has no field \"" + field + "\"");
            int n = 0;
            foreach (int level in Levels(t))
            {
                if (level < from || level > to) continue;
                bool changed = false;
                foreach (string f in t.Fields)
                {
                    if (field != null && f != field) continue;
                    long? was = Number(GetShipped(t, level, f));
                    if (was == null) continue;
                    if (Set(t, level, f, Scaled(was.Value, percent).ToString(CultureInfo.InvariantCulture)) > 0) changed = true;
                }
                if (changed) n++;
            }
            return n;
        }

        /// <summary>A value times percent/100, rounded half away from zero; above 0 stays at least 1; long.MaxValue at most.</summary>
        public static long Scaled(long value, double percent)
        {
            double d = Math.Round(value * percent / 100.0, MidpointRounding.AwayFromZero);
            if (d >= long.MaxValue) return long.MaxValue;
            long n = (long)d;
            return value > 0 && n < 1 ? 1 : n;
        }

        /// <summary>Takes the mod's lines of a level out on every side. Returns whether it had one.</summary>
        public bool Take(LevelTable t, int level)
        {
            bool any = false;
            foreach (string a in t.Archives)
            {
                LevelRow r = Row(t, a, level);
                if (r == null) continue;
                RowOp op = Change(t, a, r, false);
                if (op == null) continue;
                Drop(t, a, op);
                any = true;
            }
            return any;
        }

        /// <summary>Takes the mod's lines of every level of the table out. Returns the levels that had one.</summary>
        public int TakeAll(LevelTable t)
        {
            int n = 0;
            foreach (int l in Levels(t)) if (Take(t, l)) n++;
            return n;
        }

        /// <summary>The levels whose values differ between the sides with the mod (the first side against each other).</summary>
        public List<int> Differ(LevelTable t)
        {
            var l = new List<int>();
            List<string> sides = Sides(t);
            if (sides.Count < 2) return l;
            foreach (int level in Levels(t))
                foreach (string f in t.Fields)
                {
                    string first = Get(t, sides[0], level, f);
                    bool differs = false;
                    for (int i = 1; i < sides.Count; i++)
                    {
                        string v = Get(t, sides[i], level, f);
                        if (v != null && first != null && Number(v) != Number(first)) differs = true;
                    }
                    if (differs) { l.Add(level); break; }
                }
            return l;
        }

        // ---- the mod's lines ----

        EditOp Edit(LevelTable t, string archive, bool create)
        {
            foreach (ModOp o in Mod.Ops)
            {
                var ed = o as EditOp;
                if (ed != null && SameArchive(ed.Archive, archive) && string.Equals((ed.Entry ?? "").Replace('\\', '/'), t.Entry, StringComparison.OrdinalIgnoreCase)) return ed;
            }
            if (!create) return null;
            var n = new EditOp { Archive = archive, Entry = t.Entry, Mirror = false };
            Mod.Ops.Add(n);
            return n;
        }

        RowOp Change(LevelTable t, string archive, LevelRow r, bool create)
        {
            EditOp ed = Edit(t, archive, create);
            if (ed == null) return null;
            foreach (RowOp o in ed.Rows)
                if (o.Kind == RowOpKind.Change && Same(o.At, r)) return o;
            if (!create) return null;
            var op = new RowOp { Kind = RowOpKind.Change, At = r.At };
            ed.Rows.Add(op);
            return op;
        }

        static bool Same(string at, LevelRow r)
        {
            if ((at ?? "") == r.At) return true;
            try { return RowPath.Parse(at ?? "").ToString() == RowPath.Parse(r.At).ToString(); }
            catch (ModKitException) { return false; }
        }

        void Drop(LevelTable t, string archive, RowOp op)
        {
            EditOp ed = Edit(t, archive, false);
            if (ed == null || !ed.Rows.Remove(op)) return;
            if (ed.Rows.Count == 0) Mod.Ops.Remove(ed);
        }

        static bool SameArchive(string a, string b) { return string.Equals(DataSet.NormalizeRef(a), DataSet.NormalizeRef(b), StringComparison.OrdinalIgnoreCase); }
    }
}
