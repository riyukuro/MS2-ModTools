// The login screen's music as a mod edit: the kms client's table/loginbgbgm.xml (the LoginBgBgmTable of
// src/ms2/commonfiles/loginbgbgmdata.cpp). Each <login> row is a background (clientPropertyBgDay) for some months
// (month="9,10,11") with its music: bgmID is a list of cues, bgmParam the event parameter of each (a missing one is
// 5), priority the rank (default 0). When map 1 loads, the client takes the rows of this month (the server's time),
// keeps those of the highest priority and picks one at random, then one of its (cue, param) pairs at random. That cue
// plays on the login map (fielddata kind 1) instead of the map's own bgm id; a cue of 0 lets the map's own play. A row
// with no month, no clientPropertyBgDay or no bgmID is skipped; a month above 12 is skipped for that row.
//
// The edit is a <change> of the row with set attributes (month, bgmID, bgmParam, priority), on the kms client only
// (neither the server nor the global client has the table). Setting the shipped value back takes the line out again.
using System;
using System.Collections.Generic;
using System.Globalization;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    /// <summary>One &lt;login&gt; row of the table as shipped (or as a file the mod replaces whole has it).</summary>
    public sealed class LoginRow
    {
        public string At;           // the row op's selector: login[clientPropertyBgDay=X]
        public string Background;   // clientPropertyBgDay
        public readonly Dictionary<string, string> Shipped = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>A (cue, param) pair a row can play.</summary>
    public struct LoginBgm
    {
        public uint Cue, Param;
    }

    public sealed class LoginMusic
    {
        public const string Archive = EntityEditor.ClientXml, Entry = "table/loginbgbgm.xml";
        public const string Row = "login", Background = "clientPropertyBgDay";
        public const string Month = "month", Bgm = "bgmID", Param = "bgmParam", Priority = "priority";
        /// <summary>The attributes the editor sets, in the grid's order.</summary>
        public static readonly string[] Fields = { Month, Bgm, Param, Priority };
        /// <summary>The param the client gives a cue that has none in bgmParam.</summary>
        public const uint DefaultParam = 5;

        public readonly DataSet Data;
        public readonly Mod Mod;
        List<LoginRow> rows;

        public LoginMusic(DataSet data, Mod mod) { Data = data; Mod = mod; }

        /// <summary>Whether the data set has rows of the table (the kms client's Xml.m2d with table/loginbgbgm.xml).</summary>
        public bool Exists { get { return Rows().Count > 0; } }

        /// <summary>The table's rows, as shipped (empty when the kms client or the table is not there).</summary>
        public List<LoginRow> Rows()
        {
            if (rows != null) return rows;
            rows = new List<LoginRow>();
            byte[] b = Data.Exists(Archive) ? Shipped() : null;
            if (b == null) return rows;
            XmlDoc d = XmlDoc.Parse(b, Archive + " " + Entry);
            if (d.Root == null) return rows;
            foreach (XmlElem e in d.Root.Children)
            {
                if (e.Name != Row) continue;
                var r = new LoginRow { At = RowPath.Canonical(e), Background = e.Attr(Background) ?? "" };
                foreach (string f in Fields) r.Shipped[f] = e.Attr(f) ?? "";
                rows.Add(r);
            }
            return rows;
        }

        byte[] Shipped()
        {
            EntryOp op = new TriggerEditor(Data, Mod).OpOf(Archive, Entry);
            if (op != null) return new TriggerEditor(Data, Mod).ModBytes(op);
            M2dArchive a = Data.Open(Archive);
            M2dEntry e = a.Find(Entry);
            return e == null ? null : a.Read(e);
        }

        public LoginRow Find(string background)
        {
            return Rows().Find(r => string.Equals(r.Background, background, StringComparison.Ordinal));
        }

        // ---- values ----

        /// <summary>The row's value with the mod: the mod's set value, "" for an unset, else the shipped one.</summary>
        public string Get(LoginRow r, string field)
        {
            RowOp op = Change(r, false);
            if (op != null)
                for (int i = op.Attrs.Count - 1; i >= 0; i--)
                    if (op.Attrs[i].Name == field && string.IsNullOrEmpty(op.Attrs[i].At))
                        return op.Attrs[i].Kind == AttrOpKind.Set ? op.Attrs[i].Value ?? "" : "";
            string v;
            return r.Shipped.TryGetValue(field, out v) ? v : "";
        }

        public bool IsEdited(LoginRow r)
        {
            foreach (string f in Fields) if (Get(r, f) != r.Shipped[f]) return true;
            return false;
        }

        public bool AnyEdited()
        {
            foreach (LoginRow r in Rows()) if (IsEdited(r)) return true;
            return false;
        }

        /// <summary>
        /// Sets one attribute of a row ("" takes it out; for bgmParam and priority that is the client's default). The
        /// value is checked as the client reads it (Problem). The shipped value takes the mod's line out.
        /// Returns whether the mod changed.
        /// </summary>
        public bool Set(LoginRow r, string field, string value)
        {
            if (Array.IndexOf(Fields, field) < 0) throw new ModKitException("the login music has no field \"" + field + "\"");
            value = Normal(field, value);
            string bad = Problem(field, value);
            if (bad != null) throw new ModKitException(r.Background + ": " + bad);
            if (Get(r, field) == value) return false;
            RowOp op = Change(r, false);
            if (op != null) op.Attrs.RemoveAll(a => a.Name == field && string.IsNullOrEmpty(a.At));
            if (value == r.Shipped[field])
            {
                if (op != null && op.Attrs.Count == 0) Drop(op);
                return true;
            }
            if (op == null) op = Change(r, true);
            op.Attrs.Add(value.Length == 0 ? new AttrOp { Kind = AttrOpKind.Unset, Name = field }
                                           : new AttrOp { Kind = AttrOpKind.Set, Name = field, Value = value });
            return true;
        }

        /// <summary>Takes the mod's lines of the row out (of every field, also ones the editor does not set). Returns whether it had one.</summary>
        public bool Take(LoginRow r)
        {
            RowOp op = Change(r, false);
            if (op == null) return false;
            Drop(op);
            return true;
        }

        /// <summary>Takes the mod's lines of every row out. Returns the rows that had one.</summary>
        public int TakeAll()
        {
            int n = 0;
            foreach (LoginRow r in Rows()) if (Take(r)) n++;
            return n;
        }

        /// <summary>
        /// Sets every row's bgmID to 0 (and its bgmParam to 0), so the login map plays its own fielddata bgm id in any
        /// month. Returns the rows that changed.
        /// </summary>
        public int UseMapMusic()
        {
            int n = 0;
            foreach (LoginRow r in Rows())
            {
                bool a = Set(r, Bgm, "0"), b = Set(r, Param, "0");
                if (a || b) n++;
            }
            return n;
        }

        // ---- what the client does with the table ----

        /// <summary>The (cue, param) pairs of a row as the client builds them: a cue with no param gets 5.</summary>
        public static List<LoginBgm> Pairs(string bgm, string param)
        {
            var l = new List<LoginBgm>();
            List<uint> ids = List(bgm), ps = List(param);
            if (ids == null) return l;
            if (ps == null) ps = new List<uint>();
            for (int i = 0; i < ids.Count; i++) l.Add(new LoginBgm { Cue = ids[i], Param = i < ps.Count ? ps[i] : DefaultParam });
            return l;
        }

        /// <summary>The rows the client picks from in a month (1..12): the month's rows the client keeps, of the highest priority.</summary>
        public List<LoginRow> PickedIn(int month)
        {
            var of = new List<LoginRow>();
            uint top = 0;
            foreach (LoginRow r in Rows())
            {
                if (Get(r, Bgm).Length == 0 || Get(r, Month).Length == 0 || r.Background.Length == 0) continue;
                List<uint> ms = List(Get(r, Month));
                if (ms == null || !ms.Contains((uint)month)) continue;
                of.Add(r);
                top = Math.Max(top, PriorityOf(Get(r, Priority)));
            }
            return of.FindAll(r => PriorityOf(Get(r, Priority)) == top);
        }

        /// <summary>The cues the login map can play in a month: 0 in the list means the map's own bgm id; an empty list too.</summary>
        public List<uint> CuesIn(int month)
        {
            var l = new List<uint>();
            foreach (LoginRow r in PickedIn(month))
                foreach (LoginBgm p in Pairs(Get(r, Bgm), Get(r, Param)))
                    if (!l.Contains(p.Cue)) l.Add(p.Cue);
            return l;
        }

        static uint PriorityOf(string v)
        {
            uint n;
            return uint.TryParse((v ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        // ---- the checks ----

        /// <summary>A value as written: spaces out ("9, 10" is "9,10").</summary>
        public static string Normal(string field, string value)
        {
            return (value ?? "").Replace(" ", "").Replace("\t", "");
        }

        /// <summary>
        /// The comma list as the client splits it (XmlSplit: unsigned numbers between commas), or null when a part is
        /// not one. "" is an empty list.
        /// </summary>
        public static List<uint> List(string value)
        {
            var l = new List<uint>();
            string v = (value ?? "").Trim();
            if (v.Length == 0) return l;
            foreach (string part in v.Split(','))
            {
                uint n;
                if (!uint.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n)) return null;
                l.Add(n);
            }
            return l;
        }

        /// <summary>Why the client would not read a value as meant (null: it does).</summary>
        public static string Problem(string field, string value)
        {
            string v = (value ?? "").Trim();
            List<uint> l = List(v);
            switch (field)
            {
                case Month:
                    if (v.Length == 0) return "month is empty: the client skips the row (give the months as 1,2,...)";
                    if (l == null) return "month \"" + v + "\" is not a list of month numbers (1..12, split by commas)";
                    foreach (uint m in l) if (m < 1 || m > 12) return "month " + m + " is not 1..12: the client never picks the row for it";
                    return null;
                case Bgm:
                    if (v.Length == 0) return "bgmID is empty: the client skips the row (0 plays the map's own music)";
                    if (l == null) return "bgmID \"" + v + "\" is not a list of cue ids (numbers split by commas; 0 plays the map's own music)";
                    return null;
                case Param:
                    if (l == null) return "bgmParam \"" + v + "\" is not a list of numbers (one per cue, split by commas; a missing one is 5)";
                    return null;
                case Priority:
                    uint p;
                    if (v.Length > 0 && !uint.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out p)) return "priority \"" + v + "\" is not a number (the month's rows of the highest one are picked from)";
                    return null;
            }
            return null;
        }

        /// <summary>What the client does that the modder may not mean (null: nothing): more params than cues, fewer.</summary>
        public static string Note(string bgm, string param)
        {
            List<uint> ids = List(bgm), ps = List(param);
            if (ids == null || ps == null) return null;
            if (ps.Count < ids.Count) return ids.Count + " cue(s) but " + ps.Count + " bgmParam value(s): the client gives the others " + DefaultParam;
            if (ps.Count > ids.Count) return ids.Count + " cue(s) but " + ps.Count + " bgmParam value(s): the client ignores the extra ones";
            return null;
        }

        // ---- the mod's lines ----

        EditOp Edit(bool create)
        {
            foreach (ModOp o in Mod.Ops)
            {
                var ed = o as EditOp;
                if (ed != null && SameArchive(ed.Archive, Archive) && string.Equals((ed.Entry ?? "").Replace('\\', '/'), Entry, StringComparison.OrdinalIgnoreCase)) return ed;
            }
            if (!create) return null;
            var n = new EditOp { Archive = Archive, Entry = Entry };
            Mod.Ops.Add(n);
            return n;
        }

        RowOp Change(LoginRow r, bool create)
        {
            EditOp ed = Edit(create);
            if (ed == null) return null;
            foreach (RowOp o in ed.Rows)
                if (o.Kind == RowOpKind.Change && Same(o.At, r)) return o;
            if (!create) return null;
            var op = new RowOp { Kind = RowOpKind.Change, At = r.At };
            ed.Rows.Add(op);
            return op;
        }

        // a hand-written line may give the row as login[clientPropertyBgDay=X] with other spacing or quotes
        static bool Same(string at, LoginRow r)
        {
            if ((at ?? "") == r.At) return true;
            try
            {
                RowPath p = RowPath.Parse(at ?? "");
                return p.ToString() == RowPath.Parse(r.At).ToString();
            }
            catch (ModKitException) { return false; }
        }

        void Drop(RowOp op)
        {
            EditOp ed = Edit(false);
            if (ed == null || !ed.Rows.Remove(op)) return;
            if (ed.Rows.Count == 0) Mod.Ops.Remove(ed);
        }

        static bool SameArchive(string a, string b) { return string.Equals(DataSet.NormalizeRef(a), DataSet.NormalizeRef(b), StringComparison.OrdinalIgnoreCase); }
    }
}
