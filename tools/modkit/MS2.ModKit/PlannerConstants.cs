// The plan's checks of the game constants (see Constants.cs), on every copy of table/constants.xml the mods change,
// as the copy ends up with it. Errors: a changed value of a row whose shipped value is a whole number, a number or a
// truth value and the new one is not. Warnings: a row the mods add whose key no copy ships (nothing reads it), a
// shipped row the mods leave out (the game takes its built-in value), and a row the copies shipped alike that now
// differs between them (the client shows one value and the server uses another, or the two clients disagree).
using System;
using System.Collections.Generic;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public static partial class Planner
    {
        // the rows by id (the first row of an id; rows without a key are left out)
        static Dictionary<ConstId, XmlElem> ConstRows(XmlDoc d)
        {
            var s = new Dictionary<ConstId, XmlElem>();
            if (d == null || d.Root == null) return s;
            foreach (XmlElem e in d.Root.Children)
            {
                if (e.Name != "v" || string.IsNullOrEmpty(e.Attr("key"))) continue;
                ConstId id = ConstId.Of(e);
                if (!s.ContainsKey(id)) s[id] = e;
            }
            return s;
        }

        static string SideName(ConstSide s) { return s.Archive + (s == ConstSide.ServerTable ? " (" + s.Entry + ")" : ""); }

        static void ConstantChecks(Run run)
        {
            Plan plan = run.Plan;
            var sides = new List<ConstSide>();
            var finals = new List<Dictionary<ConstId, XmlElem>>();
            var origs = new List<Dictionary<ConstId, XmlElem>>();
            var mods = new List<Mod>();
            Mod any = null;
            var shippedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (ConstSide side in ConstSide.All)
            {
                if (!run.Data.Exists(side.Archive)) continue;
                XmlDoc fin = FinalDoc(run, side.Archive, side.Entry), was = OrigDoc(run, side.Archive, side.Entry);
                Dictionary<ConstId, XmlElem> then = ConstRows(was);
                foreach (ConstId id in then.Keys) shippedKeys.Add(id.Key);
                if (fin == null || fin.Root == null) continue;
                Mod mod = LevelMod(run, side.Archive, side.Entry);
                sides.Add(side);
                finals.Add(ConstRows(fin));
                origs.Add(then);
                mods.Add(mod);
                if (mod != null) any = mod;
            }
            if (any == null) return;
            for (int i = 0; i < sides.Count; i++)
            {
                Mod mod = mods[i];
                if (mod == null) continue;
                ConstSide side = sides[i];
                foreach (KeyValuePair<ConstId, XmlElem> kv in finals[i])
                {
                    XmlElem old;
                    string v = kv.Value.Attr("value");
                    if (origs[i].TryGetValue(kv.Key, out old))
                    {
                        string was = old.Attr("value") ?? "";
                        if (v == null || v == was) continue;
                        string bad = Constants.Problem(kv.Key, was, v);
                        if (bad != null) plan.Msg(plan.Errors, mod, side.Archive, side.Entry, bad);
                    }
                    else if (!shippedKeys.Contains(kv.Key.Key))
                        plan.Msg(plan.Warnings, mod, side.Archive, side.Entry, "constant " + kv.Key + " is added, and no copy of the constants ships the key: the game reads only the keys it knows");
                }
                foreach (ConstId id in origs[i].Keys)
                    if (!finals[i].ContainsKey(id))
                        plan.Msg(plan.Warnings, mod, side.Archive, side.Entry, "constant " + id + " has no row after the mods: the game takes its built-in value");
            }
            // the copies: a row the copies shipped alike that now differs
            for (int i = 0; i < sides.Count; i++)
                foreach (KeyValuePair<ConstId, XmlElem> kv in finals[i])
                    for (int j = i + 1; j < sides.Count; j++)
                    {
                        if (mods[i] == null && mods[j] == null) continue;
                        XmlElem other, oi, oj;
                        if (!finals[j].TryGetValue(kv.Key, out other)) continue;
                        string a = kv.Value.Attr("value") ?? "", b = other.Attr("value") ?? "";
                        if (Constants.Alike(a, b)) continue;
                        bool alike = !origs[i].TryGetValue(kv.Key, out oi) || !origs[j].TryGetValue(kv.Key, out oj) || Constants.Alike(oi.Attr("value") ?? "", oj.Attr("value") ?? "");
                        if (!alike) continue;
                        string why = sides[i].IsServer && sides[j].IsServer ? "the server's two copies disagree"
                            : sides[i].IsServer || sides[j].IsServer ? "the client shows one value and the server uses another"
                            : "the two clients disagree";
                        Mod mod = mods[j] ?? mods[i];
                        plan.Msg(plan.Warnings, mod, sides[j].Archive, sides[j].Entry, "constant " + kv.Key + ": " + SideName(sides[i]) + " has \"" + a + "\", " + SideName(sides[j]) + " has \"" + b + "\": " + why);
                    }
        }
    }
}
