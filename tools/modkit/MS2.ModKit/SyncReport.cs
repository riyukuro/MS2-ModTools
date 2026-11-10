// The sync report of one id of an entity that has a field map: every mapped field with the client's and the
// server's value, in the data as it is and as a plan leaves it, and whether the two correspond.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public sealed class SyncLine
    {
        /// <summary>feature and locale of the client's block ("" for the plain one).</summary>
        public string Variant = "";
        public FieldMapEntry Entry;
        /// <summary>Values before the mods; null = the attribute (or the whole side) is not there.</summary>
        public string ShippedClient, ShippedServer;
        /// <summary>Values after the mods of the plan.</summary>
        public string NowClient, NowServer;
        public bool ShippedAgree, NowAgree;
        /// <summary>One side has no such attribute and the other has one (and it is not the map's default). The
        /// map's evidence covers ids where both sides carry the field, so this is listed apart, not as a difference.</summary>
        public bool ShippedOmitted { get { return !ShippedAgree && (ShippedClient == null) != (ShippedServer == null); } }
        public bool NowOmitted { get { return !NowAgree && (NowClient == null) != (NowServer == null); } }
        public bool Changed { get { return ShippedClient != NowClient || ShippedServer != NowServer; } }
    }

    public sealed class SyncReport
    {
        public string Entity;
        public long Id;
        /// <summary>The entries the id lives in; null when a side does not hold it.</summary>
        public string ClientEntry, ServerEntry;
        public bool ClientShipped, ServerShipped;
        public readonly List<SyncLine> Lines = new List<SyncLine>();
        public readonly List<string> Notes = new List<string>();

        public int Disagreeing(bool now)
        {
            int n = 0;
            foreach (SyncLine l in Lines) if (!l.Entry.Candidate && !(now ? l.NowAgree : l.ShippedAgree) && !(now ? l.NowOmitted : l.ShippedOmitted)) n++;
            return n;
        }

        /// <summary>Mapped fields that one side does not carry at all.</summary>
        public int Omitted(bool now)
        {
            int n = 0;
            foreach (SyncLine l in Lines) if (!l.Entry.Candidate && (now ? l.NowOmitted : l.ShippedOmitted)) n++;
            return n;
        }

        sealed class Side
        {
            public string Entry;
            public XmlDoc Shipped, Now;
        }

        static Side Read(DataSet data, Plan plan, EntitySide es, long id, List<string> notes)
        {
            var s = new Side();
            if (!data.Exists(es.Archive)) { notes.Add(es.Archive + " is not in the data set"); return s; }
            M2dArchive arch = data.Open(es.Archive);
            var names = new List<string>();
            foreach (M2dEntry e in arch.Entries) names.Add(e.Name);
            PlanArchive pa = plan == null ? null : plan.Find(es.Archive);
            if (pa != null) foreach (KeyValuePair<string, byte[]> kv in pa.Put) names.Add(kv.Key);
            s.Entry = es.EntryOf(id, names);
            if (s.Entry == null) return s;
            M2dEntry en = arch.Find(s.Entry);
            if (en != null) s.Shipped = XmlDoc.Parse(arch.Read(en), es.Archive + " " + s.Entry);
            s.Now = s.Shipped;
            if (pa != null)
            {
                foreach (string d in pa.Delete) if (string.Equals(d, s.Entry, StringComparison.OrdinalIgnoreCase)) s.Now = null;
                foreach (KeyValuePair<string, byte[]> kv in pa.Put)
                    if (string.Equals(kv.Key, s.Entry, StringComparison.OrdinalIgnoreCase)) s.Now = XmlDoc.Parse(kv.Value, es.Archive + " " + s.Entry + " (planned)");
            }
            return s;
        }

        static string VariantOf(XmlElem under)
        {
            string f = under.Attr("feature") ?? "", l = under.Attr("locale") ?? "";
            return f.Length == 0 && l.Length == 0 ? "" : f + (l.Length > 0 ? "/" + l : "");
        }

        // one pass over a pair of documents; fills the shipped or the "now" half of the lines
        void Fill(EntityMap em, XmlDoc client, XmlDoc server, bool now)
        {
            List<XmlElem> cu = em.UnderElems(true, client == null ? null : client.Root, Id);
            List<XmlElem> su = em.UnderElems(false, server == null ? null : server.Root, Id);
            var used = new Dictionary<XmlElem, bool>();
            var pairs = new List<KeyValuePair<XmlElem, XmlElem>>();
            foreach (XmlElem c in cu)
            {
                string why;
                XmlElem s = EntityMap.Pair(c, cu.Count, su, out why);
                if (s != null) used[s] = true;
                else if (su.Count > 0) Note("client block \"" + VariantOf(c) + "\": the server file " + why);
                pairs.Add(new KeyValuePair<XmlElem, XmlElem>(c, s));
            }
            foreach (XmlElem s in su) if (!used.ContainsKey(s)) pairs.Add(new KeyValuePair<XmlElem, XmlElem>(null, s));
            foreach (KeyValuePair<XmlElem, XmlElem> p in pairs)
            {
                string variant = VariantOf(p.Key ?? p.Value) + (p.Key == null ? " (server only)" : "");
                foreach (FieldMapEntry e in em.Entries)
                {
                    string c = p.Key == null ? null : EntityMap.ValueAt(p.Key, e.Client);
                    string s = p.Value == null ? null : EntityMap.ValueAt(p.Value, e.Server);
                    SyncLine line = null;
                    foreach (SyncLine l in Lines) if (l.Entry == e && l.Variant == variant) { line = l; break; }
                    if (line == null)
                    {
                        if (c == null && s == null) continue;
                        Lines.Add(line = new SyncLine { Variant = variant, Entry = e, ShippedAgree = true, NowAgree = true });
                    }
                    string index = e.Index == null || p.Value == null ? null : EntityMap.ValueAt(p.Value, e.Index);
                    bool agree = e.Corresponds(c, s, index);
                    if (now) { line.NowClient = c; line.NowServer = s; line.NowAgree = agree; }
                    else { line.ShippedClient = c; line.ShippedServer = s; line.ShippedAgree = agree; }
                }
            }
        }

        void Note(string text) { if (!Notes.Contains(text)) Notes.Add(text); }

        /// <summary>The report of one id. plan may be null (the data as it is).</summary>
        public static SyncReport ForId(DataSet data, Plan plan, EntityMap em, long id)
        {
            if (em == null) throw new ArgumentNullException("em");
            var r = new SyncReport { Entity = em.Name, Id = id };
            Side c = Read(data, plan, em.Client, id, r.Notes), s = Read(data, plan, em.Server, id, r.Notes);
            r.ClientEntry = c.Entry; r.ServerEntry = s.Entry;
            r.ClientShipped = c.Shipped != null && em.UnderElems(true, c.Shipped.Root, id).Count > 0;
            r.ServerShipped = s.Shipped != null && em.UnderElems(false, s.Shipped.Root, id).Count > 0;
            bool clientNow = c.Now != null && em.UnderElems(true, c.Now.Root, id).Count > 0;
            bool serverNow = s.Now != null && em.UnderElems(false, s.Now.Root, id).Count > 0;
            if (!r.ClientShipped && !clientNow && !r.ServerShipped && !serverNow)
                throw new ModKitException(em.Name + " " + id.ToString(CultureInfo.InvariantCulture) + " is on neither side");
            if (!r.ClientShipped) r.Note(clientNow ? "the client row is added by a mod" : "the client has no row of this id");
            if (!r.ServerShipped) r.Note(serverNow ? "the server entry is added by a mod" : "the server has no entry of this id");
            if (r.ClientShipped || r.ServerShipped) r.Fill(em, r.ClientShipped ? c.Shipped : null, r.ServerShipped ? s.Shipped : null, false);
            r.Fill(em, clientNow ? c.Now : null, serverNow ? s.Now : null, true);
            return r;
        }

        static string V(string v) { return v == null ? "(none)" : "\"" + v + "\""; }

        /// <summary>The report as text. all = false: only the fields that disagree or that the plan changed.</summary>
        public string ToText(bool all)
        {
            var sb = new StringBuilder();
            sb.Append(Entity).Append(' ').Append(Id.ToString(CultureInfo.InvariantCulture)).Append(": client ").Append(ClientEntry ?? "(no entry)")
              .Append(", server ").Append(ServerEntry ?? "(no entry)").Append("\r\n");
            foreach (string n in Notes) sb.Append("note: ").Append(n).Append("\r\n");
            int mapped = 0, shown = 0;
            foreach (SyncLine l in Lines)
            {
                if (!l.Entry.Candidate) mapped++;
                bool differ = (!l.ShippedAgree && !l.ShippedOmitted) || (!l.NowAgree && !l.NowOmitted);
                if (!all && !l.Changed && (!differ || l.Entry.Candidate)) continue;
                shown++;
                string what = l.Entry.Candidate ? "candidate" : l.NowOmitted ? (l.NowClient == null ? "client omits it" : "server omits it") : !l.NowAgree ? "DIFFERS" : !l.ShippedAgree && !l.ShippedOmitted ? "differed, agrees now" : l.Changed ? "changed, agrees" : "agrees";
                sb.Append("  ").Append(what).Append(l.Variant.Length > 0 ? " [" + l.Variant + "]" : "").Append("  client ").Append(l.Entry.Client);
                if (l.Entry.Server != l.Entry.Client) sb.Append("  server ").Append(l.Entry.Server);
                sb.Append("  (").Append(l.Entry.Convert).Append(l.Entry.Confidence.Length > 0 ? ", " + l.Entry.Confidence : "").Append(")\r\n");
                if (l.Changed)
                {
                    sb.Append("      before: client ").Append(V(l.ShippedClient)).Append(", server ").Append(V(l.ShippedServer)).Append(l.ShippedAgree || l.ShippedOmitted ? "" : "  (differ)").Append("\r\n");
                    sb.Append("      after:  client ").Append(V(l.NowClient)).Append(", server ").Append(V(l.NowServer)).Append(l.NowAgree || l.NowOmitted ? "" : "  (differ)").Append("\r\n");
                }
                else sb.Append("      client ").Append(V(l.NowClient)).Append(", server ").Append(V(l.NowServer)).Append("\r\n");
            }
            sb.Append(mapped).Append(" mapped field(s) present; ").Append(Disagreeing(false)).Append(" differ before the mods, ")
              .Append(Disagreeing(true)).Append(" after");
            if (Omitted(true) > 0) sb.Append("; ").Append(Omitted(true)).Append(" carried by one side only").Append(all ? "" : " (listed with all fields)");
            if (!all && shown < Lines.Count) sb.Append("; ").Append(Lines.Count - shown).Append(" unchanged line(s) without a difference are not listed");
            sb.Append("\r\n");
            return sb.ToString();
        }
    }
}
