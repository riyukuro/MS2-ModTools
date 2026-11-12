// The plan's checks of the attendance calendar (AttendGift rows of the server's table/Server/NA/GameEvent.xml, see
// AttendGifts.cs), on the rows as the mods leave them: the rows the mods add and the rows whose text they change.
// Errors: text the server's ingest stops on (value1, value2 or value3 that is not XML, two events of one id that
// have not ended), an event the ingest leaves out (value2 without <string>), and a day that gives nothing (an item
// id that is not a number or not in the server's items, a count or grade that is not a whole number). Warnings: an
// event that has ended or whose dates do not read, an event with no days, an eventType spelled otherwise than the
// server's, a requirement the server never meets, an event without play time (no <Config>, or 0 seconds: given at
// login), a <string> with no tab name (tabButton, else name).
using System;
using System.Collections.Generic;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public static partial class Planner
    {
        static void AttendChecks(Run run)
        {
            Plan plan = run.Plan;
            const string archive = AttendGifts.Archive, entry = AttendGifts.Entry;
            if (!run.Data.Exists(archive)) return;
            Mod mod = LevelMod(run, archive, entry);
            if (mod == null) return;
            XmlDoc fin = FinalDoc(run, archive, entry), was = OrigDoc(run, archive, entry);
            if (fin == null || fin.Root == null) return;
            var then = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (was != null && was.Root != null)
                foreach (XmlElem e in was.Root.Children)
                {
                    if (e.Name != AttendGifts.Row) continue;
                    string id = (e.Attr(AttendGifts.Key) ?? "").Trim();
                    List<string> l;
                    if (!then.TryGetValue(id, out l)) then[id] = l = new List<string>();
                    l.Add(e.OuterText);
                }
            DateTime now = DateTime.UtcNow;
            // the live (not ended) rows of each id, every event type: two stop the ingest
            var live = new Dictionary<string, int>(StringComparer.Ordinal);
            var all = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (XmlElem e in fin.Root.Children)
            {
                if (e.Name != AttendGifts.Row) continue;
                string id = (e.Attr(AttendGifts.Key) ?? "").Trim();
                int n;
                all[id] = all.TryGetValue(id, out n) ? n + 1 : 1;
                DateTime? end = AttendGifts.ParseTime(e.Attr("eventEnd"));
                if (end != null && end.Value >= now) live[id] = live.TryGetValue(id, out n) ? n + 1 : 1;
            }
            HashSet<long> items = null;
            bool itemsRead = false;
            var told = new HashSet<string>(StringComparer.Ordinal);
            foreach (XmlElem e in fin.Root.Children)
            {
                if (e.Name != AttendGifts.Row || !string.Equals((e.Attr("eventType") ?? "").Trim(), AttendGifts.Type, StringComparison.OrdinalIgnoreCase)) continue;
                string id = (e.Attr(AttendGifts.Key) ?? "").Trim();
                List<string> old;
                if (then.TryGetValue(id, out old) && old.Contains(e.OuterText)) continue;   // as shipped
                string what = "attendance event " + (id.Length == 0 ? "(no id)" : id) + ": ";
                int num;
                if (!int.TryParse(id, out num)) { plan.Msg(plan.Errors, mod, archive, entry, what + "gameEventID \"" + id + "\" is not a whole number: the server's ingest stops"); continue; }
                if (!itemsRead)
                {
                    itemsRead = true;
                    if (run.Data.Exists(Profile.Server.ArchiveIn("x:Xml.m2d"))) items = Items(run, Profile.Server);
                }
                var errors = new List<string>();
                var warnings = new List<string>();
                HashSet<long> known = items;
                AttendGifts.Check(e.Attr, now, known == null ? (Func<long, bool>)null : known.Contains, errors, warnings);
                int c;
                if (told.Add(id))
                {
                    if (live.TryGetValue(id, out c) && c > 1) errors.Add("the file has " + c + " events of id " + id + " that have not ended: the server's ingest stops");
                    else if (all.TryGetValue(id, out c) && c > 1) warnings.Add("the file has " + c + " events of id " + id + " (the others have ended): the server keeps the one that has not");
                }
                foreach (string s in errors) plan.Msg(plan.Errors, mod, archive, entry, what + s);
                foreach (string s in warnings) plan.Msg(plan.Warnings, mod, archive, entry, what + s);
            }
        }
    }
}
