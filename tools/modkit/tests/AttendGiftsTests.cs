// AttendGifts on a made-up event table: a table/Server/NA/GameEvent.xml in the server's Server.m2d (made-up ids,
// dates, items and texts), with the attributes quoted as the shipped file quotes them.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MS2.ModKit;
using MS2.ModKit.M2d;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    sealed class FakeEvents : IDisposable
    {
        public readonly string Dir;
        public readonly DataSet Data;

        const string Head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";
        public const string Days1 = "&lt;items&gt;&lt;v itemID=&quot;39000001&quot; count=&quot;2&quot; grade=&quot;1&quot;/&gt;&lt;v itemID=&quot;39000002&quot; count=&quot;1&quot; grade=&quot;3&quot;/&gt;&lt;/items&gt;";
        public const string Value2One = "&lt;ms2&gt;&lt;string tabButton=&quot;Test Days&quot; mailTitle=&quot;Day gift&quot; mailContents=&quot;Thanks&quot; detailUrl=&quot;&quot;/&gt;&lt;Config requirePlaySeconds=&quot;600&quot;/&gt;&lt;/ms2&gt;";

        public const string Table = Head + "<ms2>\n" +
            "\t<gameEvent gameEventID='1' eventType='AttendGift' eventStart='2001-01-01-00-00' eventEnd='2001-02-01-23-59' value1='" + Days1 + "' value2='" + Value2One + "' value3='' />\n" +
            "\t<gameEvent gameEventID='2' eventType='AttendGift' eventStart='2001-03-01-00-00' eventEnd='2001-04-01-23-59' value1='&lt;items&gt;&lt;v itemID=&quot;39000001&quot; count=&quot;1&quot; grade=&quot;1&quot;/&gt;&lt;/items&gt;' value2='&lt;ms2&gt;&lt;string tabButton=&quot;Paid&quot;/&gt;&lt;payAttend type=&quot;meso&quot; price=&quot;100&quot; maxCount=&quot;3&quot;/&gt;&lt;/ms2&gt;' />\n" +
            "\t<gameEvent gameEventID='3' eventType='StringBoard' eventStart='2001-01-01-00-00' eventEnd='2099-01-01-00-00' value1='x' />\n" +
            "\t<gameEvent gameEventID='4' eventType='AttendGift' eventStart='2001-01-01-00-00' eventEnd='2001-02-01-00-00' value1='&lt;items&gt;&lt;/items&gt;' value2='&lt;ms2&gt;&lt;string tabButton=&quot;No config&quot;/&gt;&lt;/ms2&gt;' />\n" +
            "\t<gameEvent gameEventID='5' eventType='AttendGift' eventStart='2001-01-01-00-00' eventEnd='2001-02-01-00-00' value1='&lt;items&gt;&lt;/items&gt;' value2='&lt;ms2&gt;&lt;string name=&quot;&quot;/&gt;&lt;Config requirePlaySeconds=&quot;0&quot;/&gt;&lt;/ms2&gt;' />\n" +
            "\t<gameEvent gameEventID='5' eventType='StringBanner' eventStart='2001-01-01-00-00' eventEnd='2001-02-01-00-00' value1='y' />\n" +
            "\t<gameEvent gameEventID='6' eventType='NpcShopShowItem' eventStart='2001-01-01-00-00' eventEnd='2001-02-01-00-00' value1='z' />\n" +
            "\t<gameEvent gameEventID='11' eventType='AttendGift' eventStart='2001-01-01-00-00' eventEnd='2001-02-01-00-00' value1='&lt;items&gt;&lt;v itemID=&quot;39000001&quot; grade=&quot;-1&quot;/&gt;&lt;v itemID=&quot;39000002&quot; count=&quot;1&quot; grade=&quot;1&quot;/&gt;&lt;/items&gt;' value2='&lt;ms2&gt;&lt;string tabButton=&quot;Odd days&quot;/&gt;&lt;Config requirePlaySeconds=&quot;60&quot;/&gt;&lt;/ms2&gt;' />\n" +
            "\t<gameEvent gameEventID='12' eventType='AttendGift' eventStart='2001-01-01-00-00' eventEnd='2001-02-01-00-00' value1='&lt;items&gt;&lt;/items&gt;' value2='&lt;ms2&gt;&lt;string tabButton=&quot;Lines&quot; mailContents=&quot;a&#10;b&quot;/&gt;&lt;/ms2&gt;' />\n" +
            "</ms2>\n";

        static KeyValuePair<string, byte[]> E(string name, string text) { return new KeyValuePair<string, byte[]>(name, Fake.Crlf(text)); }

        public FakeEvents()
        {
            Dir = Path.Combine(Path.GetTempPath(), "modkit-events-" + Guid.NewGuid().ToString("N"));
            string k = Path.Combine(Dir, "kms"), g = Path.Combine(Dir, "gms"), s = Path.Combine(Dir, "server");
            foreach (string d in new[] { k, g, s }) Directory.CreateDirectory(d);
            string item = Head + "<ms2>\n\t<item />\n</ms2>\n";
            var items = new List<KeyValuePair<string, byte[]>> { E("item/39/39000001.xml", item), E("item/39/39000002.xml", item), E("item/39/39000003.xml", item) };
            M2dWriter.Create(Path.Combine(k, "Xml.m2d"), PackVersion.NS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>> { E("itemdata/39.xml", Head + "<ms2>\n\t<item id=\"39000001\" />\n</ms2>\n") });
            M2dWriter.Create(Path.Combine(g, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, items);
            M2dWriter.Create(Path.Combine(s, "Xml.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, items);
            M2dWriter.Create(Path.Combine(s, "Server.m2d"), PackVersion.MS2F, M2dFlag.AesZlib, new List<KeyValuePair<string, byte[]>> { E(AttendGifts.Entry, Table) });
            Data = new DataSet(k, g, s);
        }

        public static Mod NewMod() { return Mod.Parse("<mod id=\"attend.test\" name=\"t\" version=\"1\" />", "test mod"); }

        public Plan Plan(Mod m) { return Planner.Build(Data, new[] { Mod.Parse(m.ToXml(), "round trip") }, new PlanOptions()); }

        public void Dispose()
        {
            Data.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }

    [TestFixture]
    public class AttendGiftsTests
    {
        static List<string> Texts(List<PlanMessage> l, string part) { return l.Select(m => m.Text).Where(t => t.Contains(part)).ToList(); }

        static List<AttendDay> D(params string[] ids) { return ids.Select(i => new AttendDay(i, "1", "1")).ToList(); }

        [Test]
        public void ReadsTheEvents()
        {
            using (var f = new FakeEvents())
            {
                var a = new AttendGifts(f.Data, FakeEvents.NewMod());
                Assert.That(a.Has, Is.True);
                List<int> ids = a.Events().Select(e => e.Id).ToList();
                Assert.That(ids, Is.EqualTo(new[] { 1, 2, 4, 5, 11, 12 }));
                AttendEvent one = a.Find(1);
                Assert.That(a.Start(one), Is.EqualTo("2001-01-01-00-00"));
                Assert.That(a.Text(one, "name"), Is.EqualTo("Test Days"));
                Assert.That(a.HasName(one), Is.True);
                Assert.That(a.Text(one, "mailTitle"), Is.EqualTo("Day gift"));
                Assert.That(a.PlaySeconds(one), Is.EqualTo("600"));
                List<AttendDay> days = a.Days(one);
                Assert.That(days.Count, Is.EqualTo(2));
                Assert.That(days[1].ItemId + "/" + days[1].Count + "/" + days[1].Grade, Is.EqualTo("39000002/1/3"));
                AttendEvent two = a.Find(2);
                Assert.That(a.HasPay(two), Is.True);
                Assert.That(a.PlaySeconds(two), Is.Null);
                Assert.That(a.Find(5).Rows, Is.EqualTo(2));
                Assert.That(a.IdUsed(3), Is.True);
                Assert.That(a.IdUsed(6), Is.True);
                Assert.That(a.IdUsed(7), Is.False);
            }
        }

        [Test]
        public void EditsAShippedEventAndPlansIt()
        {
            using (var f = new FakeEvents())
            {
                Mod m = FakeEvents.NewMod();
                var a = new AttendGifts(f.Data, m);
                AttendEvent one = a.Find(1);
                Assert.That(a.SetDates(one, "2001-01-01-00-00", "2099-12-31-23-59"), Is.True);
                Assert.That(a.SetText(one, "name", "Spring Days"), Is.True);
                Assert.That(a.SetPlaySeconds(one, "1200"), Is.True);
                Assert.That(a.SetDays(one, new List<AttendDay> { new AttendDay("39000003", "5", "2"), new AttendDay("39000001", "1", "1") }), Is.True);
                Assert.That(a.IsEdited(one), Is.True);

                var back = new AttendGifts(f.Data, Mod.Parse(m.ToXml(), "round trip"));
                AttendEvent b = back.Find(1);
                Assert.That(back.End(b), Is.EqualTo("2099-12-31-23-59"));
                Assert.That(back.Text(b, "name"), Is.EqualTo("Spring Days"));
                Assert.That(back.HasName(b), Is.True);
                Assert.That(back.Get(b, "value2"), Does.Contain("tabButton=\"Spring Days\"").And.Not.Contain(" name="));
                Assert.That(back.Text(b, "mailTitle"), Is.EqualTo("Day gift"));
                Assert.That(back.PlaySeconds(b), Is.EqualTo("1200"));
                Assert.That(back.Days(b).Select(d => d.ItemId), Is.EqualTo(new[] { "39000003", "39000001" }));

                Plan p = f.Plan(m);
                Assert.That(Texts(p.Errors, "attendance"), Is.Empty);
                Assert.That(Texts(p.Warnings, "attendance"), Is.Empty);
            }
        }

        [Test]
        public void TheShippedValueTakesTheLineOut()
        {
            using (var f = new FakeEvents())
            {
                Mod m = FakeEvents.NewMod();
                var a = new AttendGifts(f.Data, m);
                AttendEvent one = a.Find(1);
                a.SetPlaySeconds(one, "30");
                a.SetDays(one, D("39000003"));
                Assert.That(m.Ops.Count, Is.EqualTo(1));
                a.SetPlaySeconds(one, "600");
                a.SetDays(one, new List<AttendDay> { new AttendDay("39000001", "2", "1"), new AttendDay("39000002", "1", "3") });
                Assert.That(m.Ops, Is.Empty);
                Assert.That(a.IsEdited(one), Is.False);
            }
        }

        [Test]
        public void RefusesWhatTheServerDoesNotRead()
        {
            using (var f = new FakeEvents())
            {
                var a = new AttendGifts(f.Data, FakeEvents.NewMod());
                AttendEvent one = a.Find(1);
                Assert.Throws<ModKitException>(() => a.SetDates(one, "2001-01-01", "2099-01-01-00-00"));
                Assert.Throws<ModKitException>(() => a.SetDates(one, "2099-01-01-00-00", "2001-01-01-00-00"));
                Assert.Throws<ModKitException>(() => a.SetPlaySeconds(one, "1.5"));
                Assert.Throws<ModKitException>(() => a.SetDays(one, D("abc")));
                Assert.Throws<ModKitException>(() => a.SetDays(one, new List<AttendDay> { new AttendDay("39000001", "0", "1") }));
                Assert.Throws<ModKitException>(() => a.SetText(a.Find(5), "name", "x"));
                Assert.Throws<ModKitException>(() => a.New(3, "2099-01-01-00-00", "2099-02-01-00-00"));
                Assert.Throws<ModKitException>(() => a.SetText(one, "tabButton", "x"));
            }
        }

        [Test]
        public void NewAndCopiedEvents()
        {
            using (var f = new FakeEvents())
            {
                Mod m = FakeEvents.NewMod();
                var a = new AttendGifts(f.Data, m);
                AttendEvent n = a.New(7, "2099-01-01-00-00", "2099-02-01-00-00");
                Assert.That(n.IsAdded, Is.True);
                Assert.That(a.Days(n), Is.Empty);
                Plan p = f.Plan(m);
                Assert.That(Texts(p.Errors, "attendance event 7"), Is.Empty);
                Assert.That(Texts(p.Warnings, "attendance event 7: the event has no days"), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Warnings, "attendance event 7: value2's <string> has no tabButton or name text"), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Warnings, "attendance event 7: requirePlaySeconds is 0: no play time, the day's item is given at login"), Has.Count.EqualTo(1));

                a.SetDays(n, D("39000001", "39000002"));
                a.SetText(n, "name", "New <one> & \"two\"");
                a.SetPlaySeconds(n, "900");
                AttendEvent c = a.Copy(1, 8, "2099-03-01-00-00", "2099-04-01-00-00");
                Assert.That(a.Days(c).Count, Is.EqualTo(2));
                Assert.That(a.PlaySeconds(c), Is.EqualTo("600"));

                var back = new AttendGifts(f.Data, Mod.Parse(m.ToXml(), "round trip"));
                AttendEvent b = back.Find(7);
                Assert.That(back.Text(b, "name"), Is.EqualTo("New <one> & \"two\""));
                Assert.That(back.PlaySeconds(b), Is.EqualTo("900"));
                Assert.That(back.Days(b).Count, Is.EqualTo(2));
                Assert.That(back.Find(8).IsAdded, Is.True);

                p = f.Plan(m);
                Assert.That(Texts(p.Errors, "attendance"), Is.Empty);
                Assert.That(Texts(p.Warnings, "attendance event 7"), Is.Empty);
                Assert.That(Texts(p.Warnings, "attendance event 8"), Is.Empty);

                Assert.That(a.Take(a.Find(8)), Is.True);
                Assert.That(a.Find(8), Is.Null);
                Assert.That(a.TakeAll(), Is.EqualTo(1));
                Assert.That(m.Ops, Is.Empty);
            }
        }

        [Test]
        public void PlanErrorsOnHandWrittenRows()
        {
            using (var f = new FakeEvents())
            {
                Mod m = Mod.Parse("<mod id=\"attend.hand\" name=\"t\" version=\"1\">" +
                    "<edit archive=\"server:Server.m2d\" entry=\"" + AttendGifts.Entry + "\">" +
                    "<change at=\"gameEvent[gameEventID=1]\"><set name=\"value1\" value=\"&lt;items&gt;\" /></change>" +
                    "<change at=\"gameEvent[gameEventID=4]\"><set name=\"eventEnd\" value=\"2099-01-01-00-00\" /></change>" +
                    "<add><xml><gameEvent gameEventID=\"3\" eventType=\"AttendGift\" eventStart=\"2001-01-01-00-00\" eventEnd=\"2099-01-01-00-00\" value1=\"&lt;items&gt;&lt;v itemID=&quot;39000009&quot; count=&quot;x&quot; grade=&quot;1&quot;/&gt;&lt;v itemID=&quot;39000001&quot; count=&quot;1&quot; grade=&quot;y&quot;/&gt;&lt;v itemID=&quot;39000001&quot; count=&quot;0&quot; grade=&quot;1&quot;/&gt;&lt;/items&gt;\" value2=\"&lt;ms2&gt;&lt;string name=&quot;a&quot;/&gt;&lt;Config requirePlaySeconds=&quot;1.5&quot;/&gt;&lt;/ms2&gt;\" value3=\"&lt;ms2&gt;&lt;require type=&quot;ItemID&quot; value1=&quot;1&quot;/&gt;&lt;/ms2&gt;\" /></xml></add>" +
                    "<add><xml><gameEvent gameEventID=\"9\" eventType=\"attendGift\" eventStart=\"2099-01-01\" eventEnd=\"2001-01-01-00-00\" value1=\"&lt;items&gt;&lt;/items&gt;\" value2=\"&lt;ms2&gt;&lt;string name=&quot;a&quot;/&gt;&lt;Config/&gt;&lt;/ms2&gt;\" /></xml></add>" +
                    "<add><xml><gameEvent gameEventID=\"6\" eventType=\"AttendGift\" eventStart=\"2001-01-01-00-00\" eventEnd=\"2099-01-01-00-00\" value1=\"&lt;items&gt;&lt;v itemID=&quot;39000001&quot;/&gt;&lt;/items&gt;\" value2=\"&lt;ms2&gt;&lt;string name=&quot;a&quot;/&gt;&lt;Config requirePlaySeconds=&quot;60&quot;/&gt;&lt;/ms2&gt;\" /></xml></add>" +
                    "<add><xml><gameEvent gameEventID=\"10\" eventType=\"AttendGift\" eventStart=\"2001-01-01-00-00\" eventEnd=\"2099-01-01-00-00\" value1=\"&lt;items&gt;&lt;v itemID=&quot;39000001&quot;/&gt;&lt;/items&gt;\" value2=\"&lt;x/&gt;\" /></xml></add>" +
                    "<add><xml><gameEvent gameEventID=\"10\" eventType=\"AttendGift\" eventStart=\"2001-01-01-00-00\" eventEnd=\"2099-01-01-00-00\" value1=\"&lt;items&gt;&lt;v itemID=&quot;39000001&quot;/&gt;&lt;/items&gt;\" value2=\"&lt;ms2&gt;&lt;string name=&quot;a&quot;/&gt;&lt;Config requirePlaySeconds=&quot;60&quot;/&gt;&lt;/ms2&gt;\" /></xml></add>" +
                    "</edit></mod>", "hand mod");
                Plan p = f.Plan(m);
                Assert.That(Texts(p.Errors, "attendance event 1: value1 is not XML text"), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Errors, "attendance event 4"), Is.Empty);
                Assert.That(Texts(p.Warnings, "attendance event 4: value2 has no <Config>: no play time, the day's item is given at login"), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Warnings, "attendance event 9: <Config> has no requirePlaySeconds: no play time"), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Errors, "attendance event 3: the server's ingest keeps 2 events of id 3"), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Errors, "attendance event 3: day 1 item 39000009 is not in the server's items"), Has.Count.EqualTo(1));
                // a count, grade or play time that is not a number: the ingest reads 1 / -1 / 0 and keeps the event
                Assert.That(Texts(p.Errors, "attendance event 3: day 1 count"), Is.Empty);
                Assert.That(Texts(p.Warnings, "attendance event 3: day 1 count \"x\" is not a whole number: the server reads 1"), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Warnings, "attendance event 3: day 2 grade \"y\" is not a whole number: the server reads -1"), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Errors, "attendance event 3: day 3 count 0 is not above 0"), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Errors, "attendance event 3: requirePlaySeconds"), Is.Empty);
                Assert.That(Texts(p.Warnings, "attendance event 3: requirePlaySeconds \"1.5\" is not a whole number: the server reads 0"), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Warnings, "attendance event 3: value3 asks for an item"), Has.Count.EqualTo(1));
                // the ingest's type parse is case-sensitive: the row is left out
                Assert.That(Texts(p.Errors, "attendance event 9: eventType \"attendGift\" is not \"AttendGift\""), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Warnings, "attendance event 9: eventType"), Is.Empty);
                // an ended NpcShopShowItem row stays in the ingest's events: a live AttendGift row of its id stops it
                Assert.That(Texts(p.Errors, "attendance event 6: the server's ingest keeps 2 events of id 6"), Has.Count.EqualTo(1));
                // a live row the ingest leaves out (value2 not <ms2>) is not counted
                Assert.That(Texts(p.Errors, "attendance event 10: the server's ingest keeps"), Is.Empty);
                Assert.That(Texts(p.Errors, "attendance event 10: value2 is not <ms2>"), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Warnings, "attendance event 9: eventStart \"2099-01-01\""), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Warnings, "attendance event 9: the event ended 2001-01-01-00-00"), Has.Count.EqualTo(1));
                Assert.That(Texts(p.Warnings, "attendance event 2"), Is.Empty);
            }
        }
        [Test]
        public void DaysTheServerReadsItsOwnWayDoNotBlockAnEdit()
        {
            using (var f = new FakeEvents())
            {
                Assert.That(AttendGifts.DayProblem(new AttendDay("39000001", "1", "-1")), Is.Null);
                var a = new AttendGifts(f.Data, FakeEvents.NewMod());
                AttendEvent ev = a.Find(11);
                List<AttendDay> days = a.Days(ev);
                Assert.That(days[0].Count, Is.EqualTo(""));
                days[1] = new AttendDay("39000003", "2", "1");
                Assert.That(a.SetDays(ev, days), Is.True);
                Assert.That(a.Days(ev)[0].Grade, Is.EqualTo("-1"));
                Assert.That(a.Days(ev)[1].ItemId, Is.EqualTo("39000003"));
                // a changed day is still checked
                days[1] = new AttendDay("39000003", "", "1");
                Assert.Throws<ModKitException>(() => a.SetDays(ev, days));
            }
        }

        [Test]
        public void AppendingConfigKeepsLineBreaksInOtherTexts()
        {
            using (var f = new FakeEvents())
            {
                var a = new AttendGifts(f.Data, FakeEvents.NewMod());
                AttendEvent ev = a.Find(12);
                Assert.That(a.Get(ev, "value2"), Does.Contain("a\nb"));
                Assert.That(a.SetPlaySeconds(ev, "300"), Is.True);
                string v2 = a.Get(ev, "value2");
                Assert.That(v2, Does.Contain("a\nb"));
                Assert.That(v2, Does.Contain("<Config requirePlaySeconds=\"300\" /></ms2>"));
                Assert.That(v2.Replace("a\nb", ""), Does.Not.Contain("\n"));
                Assert.That(AttendGifts.Inline("<a x=\"1\n2\"></a>", "<a x=\"1\n2\">\n\t<b /></a>"), Is.EqualTo("<a x=\"1\n2\"><b /></a>"));
            }
        }

        [Test]
        public void RealServerEvents()
        {
            string srv = RealData.Server;
            if (string.IsNullOrEmpty(srv) || !File.Exists(Path.Combine(srv, "Server.m2d"))) Assert.Ignore("MS2_SERVER_DATA is not set");
            using (var data = new DataSet(null, null, srv))
            {
                Mod m = FakeEvents.NewMod();
                var a = new AttendGifts(data, m);
                List<AttendEvent> evs = a.Events();
                Assert.That(evs, Is.Not.Empty);
                foreach (AttendEvent ev in evs) Assert.That(a.Days(ev), Is.Not.Null, "event " + ev.Id + " days");
                // the last event that asks for a play time, run again: dates, a name and one more day
                AttendEvent e = evs.Last(x => a.PlaySeconds(x) != null && a.Days(x).Count > 0 && x.Rows == 1);
                List<AttendDay> days = a.Days(e);
                days.Add(days[0]);
                a.SetDates(e, "2001-01-01-00-00", "2099-01-01-00-00");
                a.SetText(e, "name", "Again");
                a.SetDays(e, days);
                int free = 1;
                while (a.IdUsed(free)) free++;
                a.Copy(e.Id, free, "2099-02-01-00-00", "2099-03-01-00-00");
                Plan p = Planner.Build(data, new[] { Mod.Parse(m.ToXml(), "round trip") }, new PlanOptions());
                Assert.That(Texts(p.Errors, "attendance event " + e.Id + ":"), Is.Empty);
                Assert.That(Texts(p.Warnings, "attendance event " + e.Id + ":").Where(t => !t.Contains("value3")), Is.Empty);
                Assert.That(Texts(p.Errors, "attendance event " + free + ":"), Is.Empty);
                TestContext.Progress.WriteLine(evs.Count + " attendance events; event " + e.Id + " edited and copied");
            }
        }
    }
}
