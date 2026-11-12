// The attendance calendar (AttendGift events) as a mod edit.
//
// The server's Server.m2d has table/Server/NA/GameEvent.xml: one <gameEvent> row per event, of every event type. The
// server's ingest (ParseGameEventTable, ServerTableMapper.cs) reads every row of the entry (no locale or feature
// pick) and keeps the events that have not ended. An AttendGift row is a login calendar: a player who plays long
// enough on a day gets that day's item by mail, the first item on the first day attended, the next one on the
// next day, until the items run out. The row:
//
// - gameEventID: the event's id, one id space with every other event type of the file. Two events of one id that
//   have not ended stop the ingest (the events are kept by id).
// - eventStart, eventEnd: yyyy-MM-dd-HH-mm (or yyyy-MM-dd-HH-mm-ss). An event whose end has passed is left out, so
//   the client shows no calendar. Every shipped event has ended.
// - value1: the days, as XML text: <items><v itemID count grade /> ...</items>, one v per day. A count or grade that
//   is not a whole number is read as 1 and -1.
// - value2: the texts and the play time, as XML text: <ms2><string name mailTitle mailContents detailUrl />
//   <Config requirePlaySeconds /></ms2>. The server reads name (the shipped rows have tabButton instead, which
//   nothing reads), the mail's title and text, and the event page link. Without Config (and without payAttend, the
//   paid catch-up of missed days) the event is left out.
// - value3: a requirement, optional: <ms2><require type value1 value2 /></ms2>. Shown, not edited here.
//
// value1, value2 and value3 must parse as XML: text that does not stops the ingest. The server sends the items to
// the client with the event, so there is no client copy to keep in step.
//
// A shipped event is edited with a <change> of its row (gameEvent[gameEventID=N]) with set attributes; setting the
// shipped value back takes the line out. A new event (New, Copy) is an <add> of the row's text, rewritten on each
// edit; Take of a new event takes it out of the mod.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    /// <summary>One day of an attendance event: the item mailed that day, as the row writes it.</summary>
    public sealed class AttendDay
    {
        public string ItemId = "", Count = "1", Grade = "1";

        public AttendDay() { }
        public AttendDay(string itemId, string count, string grade) { ItemId = itemId ?? ""; Count = count ?? ""; Grade = grade ?? ""; }

        public override string ToString() { return ItemId + " x" + Count + " grade " + Grade; }
    }

    /// <summary>One AttendGift event: a shipped row or a row the mod adds.</summary>
    public sealed class AttendEvent
    {
        public int Id;
        /// <summary>The shipped row's path (null: a row the mod adds).</summary>
        public string At;
        /// <summary>The mod's add of the row (null: a shipped row).</summary>
        public RowOp Added;
        /// <summary>The rows of the id in the file, of every event type, with the mod's adds.</summary>
        public int Rows = 1;
        public readonly List<KeyValuePair<string, string>> Shipped = new List<KeyValuePair<string, string>>();

        public bool IsAdded { get { return Added != null; } }

        internal string ShippedAttr(string name)
        {
            foreach (KeyValuePair<string, string> kv in Shipped) if (kv.Key == name) return kv.Value;
            return null;
        }
    }

    public sealed class AttendGifts
    {
        public const string Archive = "server:Server.m2d";
        public const string Entry = "table/Server/NA/GameEvent.xml";
        public const string Row = "gameEvent", Key = "gameEventID", Type = "AttendGift";
        /// <summary>The texts of value2's string element the server reads.</summary>
        public static readonly string[] TextFields = { "name", "mailTitle", "mailContents", "detailUrl" };
        static readonly string[] TimeFormats = { "yyyy-MM-dd-HH-mm", "yyyy-MM-dd-HH-mm-ss" };

        public readonly DataSet Data;
        public readonly Mod Mod;
        List<AttendEvent> shipped;
        Dictionary<int, int> shippedIds;   // rows by id, every event type
        bool has;

        public AttendGifts(DataSet data, Mod mod) { Data = data; Mod = mod; }

        /// <summary>Whether the data set has the server's event table.</summary>
        public bool Has { get { Read(); return has; } }

        void Read()
        {
            if (shipped != null) return;
            shipped = new List<AttendEvent>();
            shippedIds = new Dictionary<int, int>();
            byte[] b = Data.Exists(Archive) ? ShippedBytes() : null;
            if (b == null) return;
            XmlDoc doc = XmlDoc.Parse(b, Archive + " " + Entry);
            if (doc.Root == null) return;
            has = true;
            foreach (XmlElem e in doc.Root.Children)
            {
                int id;
                if (e.Name != Row || !TryId(e.Attr(Key), out id)) continue;
                int n;
                shippedIds[id] = shippedIds.TryGetValue(id, out n) ? n + 1 : 1;
                if (e.Attr("eventType") != Type) continue;
                var ev = new AttendEvent { Id = id, At = RowPath.Canonical(e) };
                foreach (XmlAttr a in e.Attrs) ev.Shipped.Add(new KeyValuePair<string, string>(a.Name, a.Value));
                shipped.Add(ev);
            }
        }

        byte[] ShippedBytes()
        {
            var trig = new TriggerEditor(Data, Mod);
            EntryOp op = trig.OpOf(Archive, Entry);
            if (op != null) return trig.ModBytes(op);
            M2dArchive a = Data.Open(Archive);
            M2dEntry e = a.Find(Entry);
            return e == null ? null : a.Read(e);
        }

        static bool TryId(string s, out int id)
        {
            return int.TryParse((s ?? "").Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out id);
        }

        // the mod's added gameEvent rows: (id, eventType, the op)
        List<KeyValuePair<RowOp, XmlElem>> AddedRows()
        {
            var l = new List<KeyValuePair<RowOp, XmlElem>>();
            EditOp ed = Edit(false);
            if (ed == null) return l;
            foreach (RowOp r in ed.Rows)
            {
                if (r.Kind != RowOpKind.Add || r.Xml == null || r.Copy != null) continue;
                XmlElem e = AddedElem(r);
                if (e != null && e.Name == Row) l.Add(new KeyValuePair<RowOp, XmlElem>(r, e));
            }
            return l;
        }

        static XmlElem AddedElem(RowOp r)
        {
            try
            {
                XmlDoc d = XmlDoc.ParseFragment(r.Xml.Trim(), "added row");
                return d.Root;
            }
            catch (ModKitException) { return null; }
        }

        /// <summary>The AttendGift events, shipped and added, by id.</summary>
        public List<AttendEvent> Events()
        {
            Read();
            var ids = new Dictionary<int, int>(shippedIds);
            var l = new List<AttendEvent>(shipped);
            foreach (KeyValuePair<RowOp, XmlElem> kv in AddedRows())
            {
                int id, n;
                if (!TryId(kv.Value.Attr(Key), out id)) continue;
                ids[id] = ids.TryGetValue(id, out n) ? n + 1 : 1;
                if (kv.Value.Attr("eventType") != Type) continue;
                var ev = new AttendEvent { Id = id, Added = kv.Key };
                l.Add(ev);
            }
            foreach (AttendEvent ev in l) ev.Rows = ids[ev.Id];
            l.Sort(delegate (AttendEvent a, AttendEvent b) { return a.Id != b.Id ? a.Id.CompareTo(b.Id) : (a.IsAdded ? 1 : 0).CompareTo(b.IsAdded ? 1 : 0); });
            return l;
        }

        /// <summary>The event of an id (the shipped one first), or null.</summary>
        public AttendEvent Find(int id)
        {
            foreach (AttendEvent ev in Events()) if (ev.Id == id) return ev;
            return null;
        }

        /// <summary>Whether any row of the file (of any event type) or of the mod has the id.</summary>
        public bool IdUsed(int id)
        {
            Read();
            if (shippedIds.ContainsKey(id)) return true;
            foreach (KeyValuePair<RowOp, XmlElem> kv in AddedRows())
            {
                int n;
                if (TryId(kv.Value.Attr(Key), out n) && n == id) return true;
            }
            return false;
        }

        // ---- attributes ----

        /// <summary>An attribute of the event's row with the mod (null: the row has none).</summary>
        public string Get(AttendEvent ev, string attr)
        {
            if (ev.IsAdded)
            {
                XmlElem e = AddedElem(ev.Added);
                return e == null ? null : e.Attr(attr);
            }
            RowOp op = Change(ev, false);
            if (op != null)
                for (int i = op.Attrs.Count - 1; i >= 0; i--)
                    if (op.Attrs[i].Name == attr && string.IsNullOrEmpty(op.Attrs[i].At))
                    {
                        if (op.Attrs[i].Kind == AttrOpKind.Set) return op.Attrs[i].Value ?? "";
                        if (op.Attrs[i].Kind == AttrOpKind.Unset) return null;
                    }
            return ev.ShippedAttr(attr);
        }

        public bool IsEdited(AttendEvent ev)
        {
            return ev.IsAdded || Change(ev, false) != null;
        }

        public int EditedCount()
        {
            int n = 0;
            foreach (AttendEvent ev in Events()) if (IsEdited(ev)) n++;
            return n;
        }

        void Single(AttendEvent ev)
        {
            if (ev.Rows > 1) throw new ModKitException("event " + ev.Id + " has " + ev.Rows + " rows: the server stops on two events of one id that have not ended; edit them by hand");
        }

        // sets one attribute of the row; the shipped value of a shipped row takes its line out
        bool SetAttr(AttendEvent ev, string attr, string value)
        {
            if (Get(ev, attr) == value) return false;
            if (ev.IsAdded)
            {
                XmlDoc d = XmlDoc.ParseFragment(ev.Added.Xml.Trim(), "added row");
                var ed = new XmlEditor(d);
                ed.SetAttr(d.Root, attr, value);
                ev.Added.Xml = ed.Apply();
                return true;
            }
            RowOp op = Change(ev, false);
            if (op != null) op.Attrs.RemoveAll(x => x.Name == attr && string.IsNullOrEmpty(x.At));
            if (value == ev.ShippedAttr(attr))
            {
                if (op != null && op.Attrs.Count == 0) Drop(op);
                return true;
            }
            if (op == null) op = Change(ev, true);
            op.Attrs.Add(new AttrOp { Kind = AttrOpKind.Set, Name = attr, Value = value });
            return true;
        }

        // ---- dates ----

        /// <summary>A time as the server reads eventStart and eventEnd (null: not one).</summary>
        public static DateTime? ParseTime(string text)
        {
            DateTime t;
            return DateTime.TryParseExact((text ?? ""), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out t) ? t : (DateTime?)null;
        }

        public static string FormatTime(DateTime t) { return t.ToString("yyyy-MM-dd-HH-mm", CultureInfo.InvariantCulture); }

        public string Start(AttendEvent ev) { return Get(ev, "eventStart") ?? ""; }
        public string End(AttendEvent ev) { return Get(ev, "eventEnd") ?? ""; }

        /// <summary>Sets the start and the end (yyyy-MM-dd-HH-mm). Returns whether anything changed.</summary>
        public bool SetDates(AttendEvent ev, string start, string end)
        {
            start = (start ?? "").Trim();
            end = (end ?? "").Trim();
            DateTime? s = ParseTime(start), e = ParseTime(end);
            if (s == null) throw new ModKitException("event " + ev.Id + " start \"" + start + "\" is not a time the server reads (yyyy-MM-dd-HH-mm)");
            if (e == null) throw new ModKitException("event " + ev.Id + " end \"" + end + "\" is not a time the server reads (yyyy-MM-dd-HH-mm)");
            if (e.Value <= s.Value) throw new ModKitException("event " + ev.Id + " ends at or before its start");
            Single(ev);
            bool a = SetAttr(ev, "eventStart", start);
            bool b = SetAttr(ev, "eventEnd", end);
            return a || b;
        }

        // ---- value2: texts and play time ----

        static XmlDoc Fragment(string text, string what)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try { return XmlDoc.ParseFragment(text, what); }
            catch (ModKitException) { return null; }
        }

        static XmlElem Child(XmlElem parent, string name)
        {
            if (parent == null) return null;
            foreach (XmlElem c in parent.Children) if (c.Name == name) return c;
            return null;
        }

        XmlElem Value2Child(AttendEvent ev, string name)
        {
            XmlDoc d = Fragment(Get(ev, "value2"), "value2");
            return d == null || d.Root == null || d.Root.Name != "ms2" ? null : Child(d.Root, name);
        }

        /// <summary>A text of value2's string element; name falls back to tabButton (what the shipped rows have).</summary>
        public string Text(AttendEvent ev, string field)
        {
            XmlElem s = Value2Child(ev, "string");
            if (s == null) return "";
            string v = s.Attr(field);
            if (v == null && field == "name") v = s.Attr("tabButton");
            return v ?? "";
        }

        /// <summary>Whether value2 names the server's name text (not only tabButton).</summary>
        public bool HasName(AttendEvent ev)
        {
            XmlElem s = Value2Child(ev, "string");
            return s != null && s.Attr("name") != null;
        }

        /// <summary>The play time asked for, in seconds, as written (null: no Config element).</summary>
        public string PlaySeconds(AttendEvent ev)
        {
            XmlElem c = Value2Child(ev, "Config");
            return c == null ? null : c.Attr("requirePlaySeconds") ?? "";
        }

        public bool HasPay(AttendEvent ev) { return Value2Child(ev, "payAttend") != null; }

        // edits value2 in place: the element of the name under ms2 (made when missing), one attribute
        string EditValue2(AttendEvent ev, string element, string attr, string value, string alsoAttr)
        {
            string text = Get(ev, "value2") ?? "";
            XmlDoc d = Fragment(text, "value2");
            if (d == null || d.Root == null || d.Root.Name != "ms2")
                throw new ModKitException("event " + ev.Id + " value2 is not <ms2>...</ms2> text: edit it by hand");
            var ed = new XmlEditor(d);
            XmlElem e = Child(d.Root, element);
            if (e == null)
            {
                ed.Append(d.Root, "<" + element + " " + attr + "=\"" + XmlDoc.EscapeAttr(value, '"') + "\" />", "");
                return Inline(ed.Apply());
            }
            ed.SetAttr(e, attr, value);
            if (alsoAttr != null && e.Attr(alsoAttr) != null) ed.SetAttr(e, alsoAttr, value);
            return ed.Apply();
        }

        // value2 is one line in the shipped rows: keep it so when an element is appended
        static string Inline(string s) { return s.Replace("\r", "").Replace("\n", "").Replace("\t", ""); }

        /// <summary>
        /// Sets a text of value2 (name, mailTitle, mailContents, detailUrl). Setting name also sets tabButton when the
        /// row has it, so the row reads the same to a reader of either.
        /// </summary>
        public bool SetText(AttendEvent ev, string field, string value)
        {
            if (Array.IndexOf(TextFields, field) < 0) throw new ModKitException("an event has no text \"" + field + "\"");
            value = value ?? "";
            Single(ev);
            if (Text(ev, field) == value && (field != "name" || HasName(ev))) return false;
            return SetAttr(ev, "value2", EditValue2(ev, "string", field, value, field == "name" ? "tabButton" : null));
        }

        /// <summary>Sets the play time asked for (whole seconds, 0 or more); adds a Config element when there is none.</summary>
        public bool SetPlaySeconds(AttendEvent ev, string seconds)
        {
            seconds = (seconds ?? "").Trim();
            int n;
            if (!int.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out n)) throw new ModKitException("event " + ev.Id + " play time \"" + seconds + "\" is not a whole number of seconds");
            seconds = n.ToString(CultureInfo.InvariantCulture);
            Single(ev);
            if (PlaySeconds(ev) == seconds) return false;
            return SetAttr(ev, "value2", EditValue2(ev, "Config", "requirePlaySeconds", seconds, null));
        }

        // ---- value1: the days ----

        /// <summary>The days as value1 has them (null: value1 is not XML text the server reads).</summary>
        public List<AttendDay> Days(AttendEvent ev) { return ParseDays(Get(ev, "value1")); }

        public static List<AttendDay> ParseDays(string value1)
        {
            XmlDoc d = Fragment(value1, "value1");
            if (d == null || d.Root == null) return null;
            var l = new List<AttendDay>();
            foreach (XmlElem v in d.Root.Children) l.Add(new AttendDay(v.Attr("itemID"), v.Attr("count"), v.Attr("grade")));
            return l;
        }

        /// <summary>value1's text for the days, as the shipped rows write it.</summary>
        public static string DaysText(IList<AttendDay> days)
        {
            var sb = new StringBuilder("<items>");
            foreach (AttendDay d in days)
                sb.Append("<v itemID=\"").Append(XmlDoc.EscapeAttr(d.ItemId, '"')).Append("\" count=\"").Append(XmlDoc.EscapeAttr(d.Count, '"'))
                  .Append("\" grade=\"").Append(XmlDoc.EscapeAttr(d.Grade, '"')).Append("\" />");
            return sb.Append("</items>").ToString();
        }

        /// <summary>Why a day is refused (null: the server reads it as written).</summary>
        public static string DayProblem(AttendDay d)
        {
            int n;
            short g;
            if (!int.TryParse(d.ItemId.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n) || n <= 0) return "item id \"" + d.ItemId + "\" is not a whole number above 0";
            if (!int.TryParse(d.Count.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n) || n <= 0) return "count \"" + d.Count + "\" is not a whole number above 0";
            if (!short.TryParse(d.Grade.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out g)) return "grade \"" + d.Grade + "\" is not a whole number";
            return null;
        }

        static bool SameDays(IList<AttendDay> a, IList<AttendDay> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].ItemId != b[i].ItemId || a[i].Count != b[i].Count || a[i].Grade != b[i].Grade) return false;
            return true;
        }

        /// <summary>Sets the days. The shipped days give the shipped value1 back. Returns whether anything changed.</summary>
        public bool SetDays(AttendEvent ev, IList<AttendDay> days)
        {
            var clean = new List<AttendDay>();
            for (int i = 0; i < days.Count; i++)
            {
                var d = new AttendDay(days[i].ItemId.Trim(), days[i].Count.Trim(), days[i].Grade.Trim());
                string bad = DayProblem(d);
                if (bad != null) throw new ModKitException("event " + ev.Id + " day " + (i + 1) + ": " + bad);
                clean.Add(d);
            }
            Single(ev);
            if (SameDays(Days(ev), clean)) return false;
            string was = ev.IsAdded ? null : ev.ShippedAttr("value1");
            return SetAttr(ev, "value1", was != null && SameDays(ParseDays(was), clean) ? was : DaysText(clean));
        }

        /// <summary>value3's requirement in words ("": none).</summary>
        public string Requirement(AttendEvent ev)
        {
            XmlDoc d = Fragment(Get(ev, "value3"), "value3");
            XmlElem r = d == null || d.Root == null || d.Root.Name != "ms2" ? null : Child(d.Root, "require");
            if (r == null) return "";
            return (r.Attr("type") ?? "") + " " + (r.Attr("value1") ?? "") + (string.IsNullOrEmpty(r.Attr("value2")) ? "" : " " + r.Attr("value2"));
        }

        // ---- new events ----

        static string RowText(IList<KeyValuePair<string, string>> attrs)
        {
            var sb = new StringBuilder("<" + Row);
            foreach (KeyValuePair<string, string> kv in attrs) sb.Append(' ').Append(kv.Key).Append("=\"").Append(XmlDoc.EscapeAttr(kv.Value ?? "", '"')).Append('"');
            return sb.Append(" />").ToString();
        }

        void NewId(int id)
        {
            if (id <= 0) throw new ModKitException("the event id must be above 0");
            if (IdUsed(id)) throw new ModKitException("event id " + id + " is in use (the ids are one space with every event type of the file)");
        }

        /// <summary>Adds an event with no days, empty texts and no play time asked, from start to end.</summary>
        public AttendEvent New(int id, string start, string end)
        {
            NewId(id);
            if (ParseTime(start) == null || ParseTime(end) == null) throw new ModKitException("give the start and the end as yyyy-MM-dd-HH-mm");
            var attrs = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(Key, id.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("eventType", Type),
                new KeyValuePair<string, string>("eventStart", start.Trim()),
                new KeyValuePair<string, string>("eventEnd", end.Trim()),
                new KeyValuePair<string, string>("value1", "<items></items>"),
                new KeyValuePair<string, string>("value2", "<ms2><string name=\"\" mailTitle=\"\" mailContents=\"\" detailUrl=\"\" /><Config requirePlaySeconds=\"0\" /></ms2>"),
            };
            return AddRow(id, attrs);
        }

        /// <summary>Adds a copy of an event (as the mod has it) under a new id, with the start and end given.</summary>
        public AttendEvent Copy(int fromId, int id, string start, string end)
        {
            AttendEvent from = Find(fromId);
            if (from == null) throw new ModKitException("there is no AttendGift event " + fromId);
            NewId(id);
            if (ParseTime(start) == null || ParseTime(end) == null) throw new ModKitException("give the start and the end as yyyy-MM-dd-HH-mm");
            var attrs = new List<KeyValuePair<string, string>>();
            List<KeyValuePair<string, string>> source = from.Shipped;
            if (from.IsAdded)
            {
                source = new List<KeyValuePair<string, string>>();
                XmlElem e = AddedElem(from.Added);
                if (e != null) foreach (XmlAttr a in e.Attrs) source.Add(new KeyValuePair<string, string>(a.Name, a.Value));
            }
            foreach (KeyValuePair<string, string> kv in source)
            {
                string v = kv.Key == Key ? id.ToString(CultureInfo.InvariantCulture)
                    : kv.Key == "eventStart" ? start.Trim()
                    : kv.Key == "eventEnd" ? end.Trim()
                    : Get(from, kv.Key) ?? kv.Value;
                attrs.Add(new KeyValuePair<string, string>(kv.Key, v));
            }
            return AddRow(id, attrs);
        }

        AttendEvent AddRow(int id, List<KeyValuePair<string, string>> attrs)
        {
            var op = new RowOp { Kind = RowOpKind.Add, Xml = RowText(attrs) };
            Edit(true).Rows.Add(op);
            foreach (AttendEvent ev in Events()) if (ev.Added == op) return ev;
            throw new ModKitException("the new event could not be read back");
        }

        // ---- taking edits out ----

        /// <summary>Takes the mod's lines of an event out (a new event: takes it out of the mod). Returns whether it had any.</summary>
        public bool Take(AttendEvent ev)
        {
            EditOp ed = Edit(false);
            if (ed == null) return false;
            RowOp op = ev.IsAdded ? ev.Added : Change(ev, false);
            if (op == null || !ed.Rows.Contains(op)) return false;
            Drop(op);
            return true;
        }

        public int TakeAll()
        {
            int n = 0;
            foreach (AttendEvent ev in Events()) if (Take(ev)) n++;
            return n;
        }

        // ---- checks (the plan's, on a row as the mods leave it) ----

        /// <summary>
        /// The problems of an AttendGift row as the server's ingest reads it. errors: the ingest stops, leaves the
        /// event out, or a day gives nothing; warnings: the event will not run as it reads. itemKnown is null when
        /// the server's items are not known.
        /// </summary>
        public static void Check(Func<string, string> attr, DateTime now, Func<long, bool> itemKnown, List<string> errors, List<string> warnings)
        {
            string type = attr("eventType") ?? "";
            if (type != Type) warnings.Add("eventType \"" + type + "\" is not \"" + Type + "\" as the server writes it: the server does not read the row as an attendance event");
            string bad;
            System.Xml.XmlDocument v1 = Load(attr("value1"), out bad);
            if (bad != null) errors.Add("value1 is not XML text: " + bad + " (the server's ingest stops)");
            System.Xml.XmlDocument v2 = Load(attr("value2"), out bad);
            if (bad != null) errors.Add("value2 is not XML text: " + bad + " (the server's ingest stops)");
            string v3text = attr("value3");
            if (!string.IsNullOrEmpty(v3text))
            {
                System.Xml.XmlDocument v3 = Load(v3text, out bad);
                if (bad != null) errors.Add("value3 is not XML text: " + bad + " (the server's ingest stops)");
                else if (v3.FirstChild != null && v3.FirstChild.Name == "ms2")
                {
                    System.Xml.XmlNode req = v3.FirstChild.SelectSingleNode("require");
                    string rt = req == null || req.Attributes["type"] == null ? "" : req.Attributes["type"].Value;
                    if (string.Equals(rt, "ItemID", StringComparison.OrdinalIgnoreCase))
                        warnings.Add("value3 asks for an item to start the event (require type ItemID): the server never lets a player attend it");
                }
            }
            if (v1 != null)
            {
                if (v1.FirstChild == null) errors.Add("value1 has no element: the server leaves the event out");
                else
                {
                    int day = 0;
                    foreach (System.Xml.XmlNode n in v1.FirstChild.ChildNodes)
                    {
                        if (n.NodeType != System.Xml.XmlNodeType.Element) continue;
                        day++;
                        string id = AttrOf(n, "itemID"), count = AttrOf(n, "count"), grade = AttrOf(n, "grade");
                        int i;
                        short g;
                        if (!int.TryParse(id, out i) || i <= 0) errors.Add("day " + day + " item id \"" + (id ?? "") + "\" is not a whole number above 0: the server gives nothing that day");
                        else if (itemKnown != null && !itemKnown(i)) errors.Add("day " + day + " item " + i + " is not in the server's items: the server gives nothing that day, and the player stays on it");
                        if (count != null && (!int.TryParse(count, out i) || i <= 0)) errors.Add("day " + day + " count \"" + count + "\" is not a whole number above 0" + (int.TryParse(count, out i) ? "" : ": the server reads 1"));
                        if (grade != null && !short.TryParse(grade, out g)) errors.Add("day " + day + " grade \"" + grade + "\" is not a whole number: the server reads -1");
                    }
                    if (day == 0) warnings.Add("the event has no days: there is nothing to attend for");
                }
            }
            if (v2 != null)
            {
                System.Xml.XmlNode root = v2.FirstChild;
                if (root == null || root.Name != "ms2") errors.Add("value2 is not <ms2>...</ms2>: the server leaves the event out");
                else
                {
                    System.Xml.XmlNode str = root.SelectSingleNode("string"), cfg = root.SelectSingleNode("Config"), pay = root.SelectSingleNode("payAttend");
                    if (str == null) errors.Add("value2 has no <string> element: the server leaves the event out");
                    if (cfg == null && pay == null) errors.Add("value2 has neither <Config> nor <payAttend>: the server leaves the event out");
                    string secs = cfg == null ? null : AttrOf(cfg, "requirePlaySeconds");
                    int s;
                    if (secs != null && !int.TryParse(secs, out s)) errors.Add("requirePlaySeconds \"" + secs + "\" is not a whole number: the server asks for no play time");
                    if (str != null && AttrOf(str, "name") == null && AttrOf(str, "tabButton") != null)
                        warnings.Add("value2's <string> has tabButton but no name: the server reads name, so the event shows no name");
                }
            }
            string start = attr("eventStart"), end = attr("eventEnd");
            DateTime? st = ParseTime(start), en = ParseTime(end);
            if (st == null) warnings.Add("eventStart \"" + (start ?? "") + "\" is not a time the server reads (yyyy-MM-dd-HH-mm): it reads no start");
            if (en == null) warnings.Add("eventEnd \"" + (end ?? "") + "\" is not a time the server reads (yyyy-MM-dd-HH-mm): the event counts as ended and is left out");
            else if (en.Value < now) warnings.Add("the event ended " + FormatTime(en.Value) + ": the server leaves it out");
            if (st != null && en != null && en.Value <= st.Value) warnings.Add("the event ends at or before its start");
        }

        static string AttrOf(System.Xml.XmlNode n, string name)
        {
            System.Xml.XmlAttribute a = n.Attributes == null ? null : n.Attributes[name];
            return a == null ? null : a.Value;
        }

        // the text as the ingest loads it (XmlDocument.LoadXml); null with why when it does not load
        static System.Xml.XmlDocument Load(string text, out string why)
        {
            why = null;
            var d = new System.Xml.XmlDocument();
            try { d.LoadXml(text ?? ""); return d; }
            catch (System.Xml.XmlException ex) { why = ex.Message; return null; }
        }

        // ---- the mod's lines ----

        EditOp Edit(bool create)
        {
            foreach (ModOp o in Mod.Ops)
            {
                var ed = o as EditOp;
                if (ed != null && string.Equals(DataSet.NormalizeRef(ed.Archive), DataSet.NormalizeRef(Archive), StringComparison.OrdinalIgnoreCase)
                    && string.Equals((ed.Entry ?? "").Replace('\\', '/'), Entry, StringComparison.OrdinalIgnoreCase)) return ed;
            }
            if (!create) return null;
            var n = new EditOp { Archive = Archive, Entry = Entry, Mirror = false };
            Mod.Ops.Add(n);
            return n;
        }

        RowOp Change(AttendEvent ev, bool create)
        {
            if (ev.IsAdded) return null;
            EditOp ed = Edit(create);
            if (ed == null) return null;
            foreach (RowOp o in ed.Rows)
                if (o.Kind == RowOpKind.Change && Same(o.At, ev.At)) return o;
            if (!create) return null;
            var op = new RowOp { Kind = RowOpKind.Change, At = ev.At };
            ed.Rows.Add(op);
            return op;
        }

        static bool Same(string at, string want)
        {
            if ((at ?? "") == want) return true;
            try { return RowPath.Parse(at ?? "").ToString() == RowPath.Parse(want).ToString(); }
            catch (ModKitException) { return false; }
        }

        void Drop(RowOp op)
        {
            EditOp ed = Edit(false);
            if (ed == null || !ed.Rows.Remove(op)) return;
            if (ed.Rows.Count == 0) Mod.Ops.Remove(ed);
        }
    }
}
