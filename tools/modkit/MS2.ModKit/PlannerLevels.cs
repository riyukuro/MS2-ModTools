// The plan's checks of the level tables (see LevelExp.cs), on every side whose table/nextexp.xml or the server's
// monsterExpTable.xml the mods change, as the side ends up with it. Errors: a value of a row the mods change that is
// not a whole number of 0 or more. Warnings, nextexp: a level outside 1..99 on a client (the client reads only
// those), a level the shipped table has that the mods leave without a row, a value of 0 (the level is passed at
// once), a value below the level before it, and a level whose value the sides shipped alike and now differ in (the
// client's experience bar and the server's level-up disagree).
using System;
using System.Collections.Generic;
using System.Globalization;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public static partial class Planner
    {
        // the last mod that edits or replaces the entry of the archive (null: none)
        static Mod LevelMod(Run run, string archive, string entry)
        {
            Mod own = null;
            foreach (Mod m in run.Plan.Mods)
                foreach (ModOp op in m.Ops)
                {
                    string a = null, e = null;
                    var ed = op as EditOp;
                    var en = op as EntryOp;
                    if (ed != null) { a = ed.Archive; e = ed.Entry; }
                    else if (en != null) { a = en.Archive; e = en.Name; }
                    else continue;
                    if (string.Equals(DataSet.NormalizeRef(a), DataSet.NormalizeRef(archive), StringComparison.OrdinalIgnoreCase)
                        && string.Equals((e ?? "").Replace('\\', '/'), entry, StringComparison.OrdinalIgnoreCase)) own = m;
                }
            return own;
        }

        // the rows by level (the first row of a level; rows without a whole-number level are left out)
        static SortedDictionary<int, XmlElem> LevelRows(XmlDoc d, LevelTable t)
        {
            var s = new SortedDictionary<int, XmlElem>();
            if (d == null || d.Root == null) return s;
            foreach (XmlElem e in d.Root.Children)
            {
                int level;
                if (e.Name == t.Row && int.TryParse(e.Attr(t.Key), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out level) && !s.ContainsKey(level)) s[level] = e;
            }
            return s;
        }

        static void LevelChecks(Run run)
        {
            Plan plan = run.Plan;
            foreach (LevelTable t in LevelTable.All)
            {
                // final values by side, for the sides-differ check: side -> level -> value of the first field
                var finals = new List<KeyValuePair<string, SortedDictionary<int, XmlElem>>>();
                var origs = new List<SortedDictionary<int, XmlElem>>();
                Mod any = null;
                foreach (string archive in t.Archives)
                {
                    if (!run.Data.Exists(archive)) continue;
                    Mod mod = LevelMod(run, archive, t.Entry);
                    XmlDoc fin = FinalDoc(run, archive, t.Entry), was = OrigDoc(run, archive, t.Entry);
                    if (fin == null || fin.Root == null) continue;
                    SortedDictionary<int, XmlElem> now = LevelRows(fin, t), then = LevelRows(was, t);
                    finals.Add(new KeyValuePair<string, SortedDictionary<int, XmlElem>>(archive, now));
                    origs.Add(then);
                    if (mod == null) continue;
                    any = mod;
                    Profile p = Profile.Find(DataSet.RootOf(DataSet.NormalizeRef(archive)));
                    bool client = p != null && !p.IsServer && t.ClientMax > 0;
                    long? before = null;
                    int beforeLevel = 0;
                    foreach (KeyValuePair<int, XmlElem> kv in now)
                    {
                        int level = kv.Key;
                        XmlElem e = kv.Value, old;
                        bool changed = !then.TryGetValue(level, out old) || RowText(old) != RowText(e);
                        long? first = null;
                        foreach (string f in t.Fields)
                        {
                            string v = e.Attr(f);
                            long? n = LevelExp.Number(v);
                            if (f == t.Fields[0]) first = n;
                            if (!changed || v == null) continue;
                            string bad = LevelExp.Problem(t, level, f, v);
                            if (bad != null) plan.Msg(plan.Errors, mod, archive, t.Entry, bad);
                        }
                        if (changed && client && (level < 1 || level > t.ClientMax))
                            plan.Msg(plan.Warnings, mod, archive, t.Entry, t.Name + " level " + level + ": the client reads levels 1.." + t.ClientMax + " only and skips the row");
                        if (t == LevelTable.NextExp && changed && first != null)
                        {
                            if (first.Value == 0) plan.Msg(plan.Warnings, mod, archive, t.Entry, t.Name + " level " + level + " needs 0 experience: a character passes the level at once");
                            else if (before != null && first.Value > 0 && first.Value < before.Value)
                                plan.Msg(plan.Warnings, mod, archive, t.Entry, t.Name + " level " + level + " needs " + first.Value + ", less than level " + beforeLevel + " (" + before.Value + ")");
                        }
                        if (first != null) { before = first; beforeLevel = level; }
                    }
                    if (t == LevelTable.NextExp)
                        foreach (int level in then.Keys)
                            if (!now.ContainsKey(level) && (!client || (level >= 1 && level <= t.ClientMax)))
                                plan.Msg(plan.Warnings, mod, archive, t.Entry, t.Name + " level " + level + " has no row after the mods: " + (client ? "the client's experience bar never fills at that level" : "a character cannot level up past it"));
                }
                if (any == null || finals.Count < 2 || t.Fields.Length == 0) continue;
                // the sides: a level the sides shipped alike and now differ in
                string field = t.Fields[0];
                foreach (KeyValuePair<int, XmlElem> kv in finals[0].Value)
                {
                    int level = kv.Key;
                    long? a = LevelExp.Number(kv.Value.Attr(field));
                    for (int i = 1; i < finals.Count; i++)
                    {
                        XmlElem other, o0, oi;
                        if (!finals[i].Value.TryGetValue(level, out other)) continue;
                        long? b = LevelExp.Number(other.Attr(field));
                        if (a == b) continue;
                        // shipped alike? (a level a side did not ship counts as alike)
                        bool alike = !origs[0].TryGetValue(level, out o0) || !origs[i].TryGetValue(level, out oi) || LevelExp.Number(o0.Attr(field)) == LevelExp.Number(oi.Attr(field));
                        if (!alike) continue;
                        Mod mod = LevelMod(run, finals[i].Key, t.Entry) ?? LevelMod(run, finals[0].Key, t.Entry) ?? any;
                        plan.Msg(plan.Warnings, mod, finals[i].Key, t.Entry, t.Name + " level " + level + ": " + finals[0].Key + " has " + Shown(a) + ", " + finals[i].Key + " has " + Shown(b)
                            + (t == LevelTable.NextExp ? (IsServerSide(finals[0].Key) || IsServerSide(finals[i].Key) ? ": the client's experience bar and the server's level-up disagree" : ": the two clients' experience bars disagree") : ""));
                    }
                }
            }
        }

        static bool IsServerSide(string archive)
        {
            Profile p = Profile.Find(DataSet.RootOf(DataSet.NormalizeRef(archive)));
            return p != null && p.IsServer;
        }

        static string Shown(long? n) { return n == null ? "no number" : n.Value.ToString(CultureInfo.InvariantCulture); }
    }
}
