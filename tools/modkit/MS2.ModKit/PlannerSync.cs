// Two-sided sync: after the mods' own edits are resolved, every attribute a mod changed in an entity that has a
// field map (FieldMap.cs) is carried to the entity's other side, with the map's conversion. A map pairs the
// kms-client (bundle rows) with the per-id layout; that side is every per-id target of the data set (server and
// gms-client, see Profile.cs): a kms-client change goes to each of them, a server or gms-client change to the
// kms-client. A change the mirror wrote (PlannerMirror.cs) is not synced again. The other side is
// written only when (a) no mod sets it itself, (b) the row is not marked sync="false" and (c) the two sides
// corresponded before the change. Everything that stays one-sided is a warning line of the plan.
using System;
using System.Collections.Generic;
using System.Globalization;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public static partial class Planner
    {
        sealed class SyncRun
        {
            internal Run Run;
            internal FieldMaps Maps;
            internal bool NoSync;
            internal readonly Dictionary<string, bool> Twin = new Dictionary<string, bool>();
            internal readonly Dictionary<string, int> LooseCount = new Dictionary<string, int>();
            internal readonly Dictionary<string, PlanChange> LooseFirst = new Dictionary<string, PlanChange>();
            internal readonly Dictionary<string, string> LooseOther = new Dictionary<string, string>();
            internal readonly List<string> LooseOrder = new List<string>();
            internal readonly Dictionary<string, string> EntryOf = new Dictionary<string, string>();
        }

        // the name of a side in the messages: the data root ("client", "server", "gms-client")
        static string SideName(string archive) { return DataSet.RootOf(archive); }

        // the archives a change of this side is synced to: every profile of the data set whose layout of the entity
        // is the map's other side (Layout.cs): a kms-client change goes to the server and the gms-client, a server
        // or gms-client change to the kms-client
        static List<string> SyncTargets(Run run, EntityMap em, bool client)
        {
            var list = new List<string>();
            EntitySide other = client ? em.Server : em.Client;
            foreach (Profile p in run.Data.Profiles)
                if (p.Holds(em, other)) list.Add(p.ArchiveIn(other.Archive));
            if (list.Count == 0) list.Add(other.Archive);
            return list;
        }

        static string Val(string v) { return v == null ? "(none)" : "\"" + v + "\""; }

        static void SyncPass(Run run, PlanOptions options)
        {
            var sr = new SyncRun { Run = run, Maps = options.Maps ?? FieldMaps.Default, NoSync = options.NoSync };
            Plan plan = run.Plan;
            PlanChange[] snapshot = plan.Changes.ToArray();
            foreach (PlanChange ch in snapshot)
            {
                if (ch.State == null || ch.Mod == null || ch.Mirrored) continue;
                if (!plan.Changes.Contains(ch)) continue;
                bool attr = ch.Kind == ChangeKind.SetAttr || ch.Kind == ChangeKind.RemoveAttr;
                if (!attr && ch.Kind != ChangeKind.AddRow) continue;
                bool client;
                EntityMap em = sr.Maps.Find(ch.Archive, ch.Entry, out client);
                try
                {
                    if (em == null) { if (attr) Loose(sr, ch); }
                    else
                        foreach (string target in SyncTargets(run, em, client))
                        {
                            if (attr) SyncAttr(sr, em, client, ch, target);
                            else SyncAdd(sr, em, client, ch, target);
                        }
                }
                catch (ModKitException x) { plan.Msg(plan.Warnings, ch.Mod, ch.Archive, ch.Entry, "two-sided sync: " + x.Message); }
            }
            foreach (string key in sr.LooseOrder)
            {
                PlanChange first = sr.LooseFirst[key];
                int n = sr.LooseCount[key];
                plan.Msg(plan.Warnings, first.Mod, first.Archive, first.Entry,
                    "@" + first.Name + " is changed in " + n + " row(s) (first: " + first.Row + ") on this side only; " + sr.LooseOther[key]
                    + " holds the same table and there is no field map for it, so that side is not written");
            }
        }

        // ---- a table with the same name in both roots and no field map ----

        static void Loose(SyncRun sr, PlanChange ch)
        {
            Run run = sr.Run;
            string root = DataSet.RootOf(ch.Archive), path = DataSet.PathOf(ch.Archive);
            Profile from = Profile.Find(root);
            foreach (string r in run.Data.Roots)
            {
                if (r == root) continue;
                Profile to = Profile.Find(r);
                if (from != null && from.Mirrors(to)) continue;     // the mirror carries it
                string other = r + ":" + path;
                string entry = from != null ? from.EntryIn(ch.Entry, to) : ch.Entry;
                string tk = other + "\n" + entry.ToLowerInvariant();
                bool twin;
                if (!sr.Twin.TryGetValue(tk, out twin))
                {
                    twin = false;
                    if (run.Data.Exists(other))
                    {
                        M2dArchive arch = run.Open(ch.Mod, other);
                        twin = arch != null && arch.Find(entry) != null;
                    }
                    sr.Twin[tk] = twin;
                }
                if (!twin) continue;

                // the mod (or another one) sets the same attribute of the same row there: both sides are explicit
                EntryState st;
                if (run.States.TryGetValue(tk, out st) && st.Doc != null)
                {
                    string err;
                    XmlElem el = ch.Row.Length == 0 ? st.Doc.Root : RowPath.Parse(ch.Row).TryResolve(st.Doc.Root, out err);
                    Claim c;
                    if (el != null && st.Claims.TryGetValue(el.Start + "\n@" + ch.Name, out c) && c.Value == ch.NewValue) continue;
                }
                string key = ch.ModId + "\n" + ch.Archive + "\n" + ch.Entry + "\n" + ch.Name + "\n" + other;
                int n;
                if (!sr.LooseCount.TryGetValue(key, out n)) { sr.LooseOrder.Add(key); sr.LooseFirst[key] = ch; sr.LooseOther[key] = other; }
                sr.LooseCount[key] = n + 1;
            }
        }

        // ---- an entity with a field map ----

        static void SyncAttr(SyncRun sr, EntityMap em, bool client, PlanChange ch, string target)
        {
            FieldLoc loc = em.Locate(client, ch.Entry, ch.Elem);
            EntitySide side = em.Side(client);
            if (loc == null)
            {
                // the key of a row, or something outside of the mapped part
                if (side.Row != null && ch.Elem.Name == side.Row && ch.Name == side.Key) return;
                Unmapped(sr, em, client, ch.Mod, ch.Archive, ch.Entry, ch.Row, RowPath.NamePath(ch.Elem, false) + "@" + ch.Name, FieldKind.Unknown, null, target);
                return;
            }
            SyncField(sr, em, client, target, ch.Mod, ch.Archive, ch.Entry, ch.Row, loc, ch.Elem, ch.Name, ch.OldValue, ch.NewValue, (ch.Op == null || ch.Op.Sync) && (ch.AttrOp == null || ch.AttrOp.Sync));
        }

        // a row added as a copy of another one, with <set> lines: each set is a change of the copy
        static void SyncAdd(SyncRun sr, EntityMap em, bool client, PlanChange ch, string target)
        {
            EntitySide side = em.Side(client);
            if (side.Row == null || ch.Elem == null || ch.Elem.Name != side.Row || ch.Op == null || ch.Source == null) return;
            foreach (AttrOp a in ch.Op.Attrs)
            {
                if (a.Kind == AttrOpKind.Text) continue;
                XmlElem el = Sub(ch.Elem, a.At);
                FieldLoc loc = em.Locate(client, ch.Entry, el);
                if (loc == null) continue;
                string old = null;
                string err;
                XmlElem was = string.IsNullOrEmpty(a.At) ? ch.Source : RowPath.Parse(a.At).TryResolve(ch.Source, out err);
                if (was != null) old = was.Attr(a.Name);
                string now = el.Attr(a.Name);
                if (old == now) continue;
                SyncField(sr, em, client, target, ch.Mod, ch.Archive, ch.Entry, ch.Row + (string.IsNullOrEmpty(a.At) ? "" : "/" + a.At), loc, el, a.Name, old, now, ch.Op.Sync && a.Sync);
            }
        }

        static void Unmapped(SyncRun sr, EntityMap em, bool client, Mod mod, string archive, string entry, string row, string field, FieldKind kind, FieldMapEntry cand, string target)
        {
            Plan plan = sr.Run.Plan;
            if (!sr.Run.Data.Exists(target)) return;
            string text = row + " " + field + " is changed on the " + SideName(archive) + " only: the field map of \"" + em.Name + "\" has no entry for it";
            if (kind == FieldKind.Candidate && cand != null)
                text += " (" + (client ? SideName(target) + " " + cand.Server : "client " + cand.Client) + " is a candidate that agreed on " + cand.Agree + " of " + cand.Compared + " compared ids; it is not written)";
            plan.Msg(plan.Warnings, mod, archive, entry, text);
        }

        // a client (gms-client) table that never has the attribute the field map names on the per-id side
        static bool NoPlaceIn(Run run, string archive, string entry, string path, string attr)
        {
            string table = SchemaSet.TableOf(entry);
            SchemaSet cached = run.Options != null ? run.Options.Schemas : null;
            ArchiveSchema a = cached != null ? cached.Archive(archive) : null;
            TableSchema t = a != null && !a.Stale ? a.Table(table) : SchemaSet.ScanTable(run.Data, archive, table);
            if (t == null) return false;
            ElemSchema el = t.Element(path);
            return el == null || el.Attr(attr) == null;
        }

        static List<string> Names(SyncRun sr, M2dArchive arch, string archive)
        {
            var names = new List<string>();
            foreach (M2dEntry e in arch.Entries) names.Add(e.Name);
            foreach (EntryState st in sr.Run.Order)
                if (st.Archive == archive && !st.Existed && st.Base != null) names.Add(st.Name);
            return names;
        }

        // the row a mod adds for an id on the client side (the row element of the new text)
        static XmlElem AddedRow(SyncRun sr, EntityMap em, long id)
        {
            string want = id.ToString(CultureInfo.InvariantCulture);
            foreach (PlanChange c in sr.Run.Plan.Changes)
            {
                if (c.Kind != ChangeKind.AddRow || c.Elem == null || c.Archive != em.Client.Archive) continue;
                long n;
                if (c.Elem.Name == em.Client.Row && c.Elem.Attr(em.Client.Key) == want && em.Client.Match(c.Entry, out n) && n == em.Client.NameNumber(id)) return c.Elem;
            }
            return null;
        }

        static void SyncField(SyncRun sr, EntityMap em, bool client, string otherArchive, Mod mod, string archive, string entry, string row, FieldLoc loc, XmlElem el,
                              string attr, string oldValue, string newValue, bool rowSync)
        {
            Run run = sr.Run;
            Plan plan = run.Plan;
            string field = loc.Path + "@" + attr;
            FieldMapEntry e;
            FieldKind kind = em.Classify(client, field, out e);
            if (kind == FieldKind.ClientOnly || kind == FieldKind.ServerOnly) return;
            if (kind != FieldKind.Mapped) { Unmapped(sr, em, client, mod, archive, entry, row, field, kind, e, otherArchive); return; }

            EntitySide other = em.Side(!client);
            string here = SideName(archive), there = SideName(otherArchive);
            string perId = client ? there : here;
            string what = row + " @" + attr;
            if (!run.Data.Exists(otherArchive))
            {
                plan.Msg(plan.Warnings, mod, otherArchive, null, "is not in the data set: the mapped fields this mod changes are written on the " + here + " side only");
                return;
            }
            M2dArchive arch = run.Open(mod, otherArchive);
            if (arch == null) return;
            string otherField = client ? e.Server : e.Client;
            string otherPath = FieldMapEntry.ElemOf(otherField), otherAttr = FieldMapEntry.AttrOf(otherField);
            string idText = loc.Id.ToString(CultureInfo.InvariantCulture);

            string otherEntry;
            if (!sr.EntryOf.TryGetValue(otherArchive + "\n" + idText, out otherEntry))
                sr.EntryOf[otherArchive + "\n" + idText] = otherEntry = other.EntryOf(loc.Id, Names(sr, arch, otherArchive));
            if (otherEntry == null)
            {
                plan.Msg(plan.Warnings, mod, archive, entry, what + ": " + em.Name + " " + idText + " has no entry in " + otherArchive + "; changed on the " + here + " only");
                return;
            }
            EntryState st = run.State(arch, otherArchive, otherEntry);
            if (st.DeletedBy != null || st.Base == null)
            {
                plan.Msg(plan.Warnings, mod, archive, entry, what + ": " + otherArchive + " " + st.Name + " is deleted; changed on the " + here + " only");
                return;
            }
            if (!EnsureDoc(plan, mod, st)) return;

            int likeCount = em.UnderElems(client, loc.Under.Parent != null && loc.Under.Parent.Parent != null ? loc.Under.Parent.Parent : loc.Under.Parent, loc.Id).Count;
            List<XmlElem> unders = em.UnderElems(!client, st.Doc.Root, loc.Id);
            XmlElem addedRow = null;
            if (unders.Count == 0 && client == false && (addedRow = AddedRow(sr, em, loc.Id)) != null)
                unders = em.UnderElems(true, addedRow, loc.Id);
            string why;
            XmlElem otherUnder = EntityMap.Pair(loc.Under, likeCount, unders, out why);
            if (otherUnder == null)
            {
                plan.Msg(plan.Warnings, mod, archive, entry, what + ": " + em.Name + " " + idText + " in " + otherArchive + " " + st.Name + " " + why + "; changed on the " + here + " only");
                return;
            }
            XmlElem otherEl = EntityMap.Walk(otherUnder, otherPath, el, loc.Under);
            XmlElem serverUnder = client ? otherUnder : loc.Under;
            string index = e.Index == null ? null : EntityMap.ValueAt(serverUnder, e.Index);

            if (addedRow != null)
            {
                // the client row is new text of a mod: it cannot be edited here, only compared
                string has = otherEl == null ? null : otherEl.Attr(otherAttr);
                if (!e.Corresponds(has, newValue, index))
                    plan.Msg(plan.Warnings, mod, archive, entry, what + " = " + Val(newValue) + " on the " + here + " only: the client row of " + idText
                        + " is added by a mod with " + otherField + " = " + Val(has) + "; set it there too");
                return;
            }
            if (otherEl == null)
            {
                plan.Msg(plan.Warnings, mod, archive, entry, what + ": " + em.Name + " " + idText + " has no <" + otherPath + "> on the " + there + " (" + st.Name + "); changed on the " + here + " only");
                return;
            }
            if (client && otherEl.Attr(otherAttr) == null && !Profile.OfArchive(otherArchive).IsServer && NoPlaceIn(run, otherArchive, st.Name, RowPath.NamePath(otherEl, true), otherAttr))
            {
                plan.Msg(plan.Warnings, mod, archive, entry, what + ": " + otherField + " has no place in " + there + " (its shipped " + SchemaSet.TableOf(st.Name) + " never has it); changed on the "
                    + here + (run.Data.Has(Profile.Server) ? " and the server" : "") + " only");
                return;
            }
            Mod del = DeletedBy(st, otherEl);
            if (del != null)
            {
                plan.Msg(plan.Warnings, mod, archive, entry, what + ": the " + there + " row is deleted by mod " + del.Id + "; changed on the " + here + " only");
                return;
            }

            string otherOld = otherEl.Attr(otherAttr);
            string key = otherEl.Start + "\n@" + otherAttr;
            Claim claim;
            st.Claims.TryGetValue(key, out claim);
            if (claim != null && !claim.Synced)
            {
                bool ok = client ? e.Corresponds(newValue, claim.Value, index) : e.Corresponds(claim.Value, newValue, index);
                if (!ok)
                {
                    string c = client ? newValue : claim.Value, s = client ? claim.Value : newValue;
                    plan.Msg(plan.Warnings, null, em.Client.Archive, client ? entry : st.Name,
                        "explicit one-sided override: " + em.Name + " " + idText + " " + e.Client + " is set to " + Val(c) + " on the client and " + e.Server + " to " + Val(s)
                        + " on the " + perId + "; the two do not correspond (" + e.Convert + ")");
                }
                return;
            }

            bool agreed = client ? e.Corresponds(oldValue, otherOld, index) : e.Corresponds(otherOld, oldValue, index);
            if (sr.NoSync || !rowSync)
            {
                plan.Msg(plan.Warnings, mod, archive, entry, "explicit one-sided override: " + what + " = " + Val(newValue) + " on the " + here + " only (sync is off for it); "
                    + there + " " + otherField + " stays " + Val(otherOld));
                return;
            }
            if (!agreed)
            {
                plan.Msg(plan.Warnings, mod, archive, entry, what + " is changed on the " + here + " only: the shipped values do not correspond (" + here + " " + Val(oldValue) + ", "
                    + there + " " + otherField + " " + Val(otherOld) + "), so the " + there + " is left as it is; set it in the mod if it should change");
                return;
            }
            string conv;
            bool can = client ? e.TryToServer(newValue, otherOld, index, out conv, out why) : e.TryToClient(newValue, otherOld, index, out conv, out why);
            if (!can)
            {
                plan.Msg(plan.Warnings, mod, archive, entry, what + " is changed on the " + here + " only: " + why);
                return;
            }
            if (claim != null)
            {
                // an earlier synced value of another mod: the later one wins, as on the side the mods wrote
                for (int i = plan.Changes.Count - 1; i >= 0; i--)
                {
                    PlanChange c = plan.Changes[i];
                    if (c.Synced && ReferenceEquals(c.State, st) && c.Elem == otherEl && c.Name == otherAttr) { plan.Changes.RemoveAt(i); break; }
                }
            }
            else if (conv == otherOld) return;
            st.Claims[key] = new Claim { Mod = mod, Elem = otherEl, What = "@" + otherAttr, Value = conv, Synced = true };
            st.Ed.SetAttr(otherEl, otherAttr, conv);
            plan.Changes.Add(new PlanChange
            {
                Kind = ChangeKind.SetAttr, ModId = mod.Id, Archive = st.Archive, Entry = st.Name, Row = RowPath.Canonical(otherEl), Name = otherAttr,
                OldValue = otherOld, NewValue = conv, Synced = true, Elem = otherEl, State = st, Mod = mod,
            });
            plan.Probes.Add(new SchemaProbe { Mod = mod, Archive = st.Archive, Entry = st.Name, ElementPath = RowPath.NamePath(otherEl, true), Attr = otherAttr, Value = conv });
        }
    }
}
