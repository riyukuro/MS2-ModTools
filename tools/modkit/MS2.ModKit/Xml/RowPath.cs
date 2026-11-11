// Row selectors: "npc[id=21000001]/environment[feature=]/basic".
//   step      = element name (or *) followed by tests
//   [a=v]     attribute a has the value v; [a=] : a is empty or absent; [a='x]y'] or [a="x'y"] quotes a value
//   [#2]      the second of the elements that pass the other tests
// Steps are separated by '/'. A selector starts at the children of the element it is resolved from
// (the document's root element for an op). The empty selector is that element itself.
using System;
using System.Collections.Generic;
using System.Text;

namespace MS2.ModKit.Xml
{
    public sealed class RowStep
    {
        public string Name;
        public readonly List<KeyValuePair<string, string>> Tests = new List<KeyValuePair<string, string>>();
        public int Nth;     // 0 = not given

        public bool Matches(XmlElem e)
        {
            if (Name != "*" && e.Name != Name) return false;
            foreach (KeyValuePair<string, string> t in Tests)
                if ((e.Attr(t.Key) ?? "") != t.Value) return false;
            return true;
        }

        public override string ToString()
        {
            var sb = new StringBuilder(Name);
            foreach (KeyValuePair<string, string> t in Tests) sb.Append('[').Append(t.Key).Append('=').Append(RowPath.Quote(t.Value)).Append(']');
            if (Nth > 0) sb.Append("[#").Append(Nth).Append(']');
            return sb.ToString();
        }
    }

    public sealed class RowPath
    {
        public readonly List<RowStep> Steps = new List<RowStep>();
        public string Source = "";

        public bool IsEmpty { get { return Steps.Count == 0; } }

        public override string ToString()
        {
            var parts = new string[Steps.Count];
            for (int i = 0; i < parts.Length; i++) parts[i] = Steps[i].ToString();
            return string.Join("/", parts);
        }

        internal static string Quote(string v)
        {
            if (v.Length > 0 && v.IndexOfAny(new[] { ']', '[', '/', '\'', '"', ' ' }) < 0) return v;
            if (v.Length == 0) return "";
            return v.IndexOf('\'') < 0 ? "'" + v + "'" : "\"" + v + "\"";
        }

        public static RowPath Parse(string text)
        {
            var p = new RowPath { Source = text ?? "" };
            string s = p.Source.Trim();
            int i = 0, n = s.Length;
            if (n == 0) return p;
            for (; ; )
            {
                var step = new RowStep();
                int st = i;
                while (i < n && s[i] != '[' && s[i] != '/') i++;
                step.Name = s.Substring(st, i - st).Trim();
                if (step.Name.Length == 0) throw Bad(s, i, "an element name is missing");
                while (i < n && s[i] == '[')
                {
                    i++;
                    if (i < n && s[i] == '#')
                    {
                        int e = s.IndexOf(']', i);
                        int nth;
                        if (e < 0 || !int.TryParse(s.Substring(i + 1, e - i - 1), out nth) || nth < 1) throw Bad(s, i, "[#n] needs a number from 1");
                        step.Nth = nth; i = e + 1; continue;
                    }
                    int eq = s.IndexOf('=', i);
                    int close = s.IndexOf(']', i);
                    if (eq < 0 || (close >= 0 && close < eq)) throw Bad(s, i, "a test is written [attribute=value]");
                    string name = s.Substring(i, eq - i).Trim();
                    if (name.Length == 0) throw Bad(s, i, "a test has no attribute name");
                    i = eq + 1;
                    string val;
                    if (i < n && (s[i] == '\'' || s[i] == '"'))
                    {
                        int q = s.IndexOf(s[i], i + 1);
                        if (q < 0) throw Bad(s, i, "the quote is not closed");
                        val = s.Substring(i + 1, q - i - 1);
                        i = q + 1;
                        if (i >= n || s[i] != ']') throw Bad(s, i, "']' expected after the quoted value");
                        i++;
                    }
                    else
                    {
                        if (close < 0) throw Bad(s, i, "']' is missing");
                        val = s.Substring(i, close - i);
                        i = close + 1;
                    }
                    step.Tests.Add(new KeyValuePair<string, string>(name, val));
                }
                p.Steps.Add(step);
                if (i >= n) break;
                if (s[i] != '/') throw Bad(s, i, "'/' or '[' expected");
                i++;
                if (i >= n) throw Bad(s, i, "the selector ends with '/'");
            }
            return p;
        }

        static ModKitException Bad(string s, int at, string what)
        {
            return new ModKitException("bad selector \"" + s + "\" at character " + (at + 1) + ": " + what);
        }

        static List<XmlElem> MatchStep(RowStep step, XmlElem parent)
        {
            var hits = new List<XmlElem>();
            // a test of the id (else of another key with a value): only the rows with that value are looked at (a
            // long table keeps its rows by the value of an attribute it is asked about)
            IList<XmlElem> among = parent.Children;
            string by = null, value = null;
            foreach (KeyValuePair<string, string> t in step.Tests)
            {
                if (t.Key == "id") { by = t.Key; value = t.Value; break; }
                if (by == null && t.Value.Length > 0 && t.Key != "feature" && t.Key != "locale") { by = t.Key; value = t.Value; }
            }
            if (by != null) among = parent.ChildrenWith(by, value) ?? among;
            foreach (XmlElem c in among) if (step.Matches(c)) hits.Add(c);
            if (step.Nth > 0)
            {
                var one = new List<XmlElem>();
                if (step.Nth <= hits.Count) one.Add(hits[step.Nth - 1]);
                return one;
            }
            return hits;
        }

        /// <summary>The one element the selector names. On failure returns null and says why.</summary>
        public XmlElem TryResolve(XmlElem from, out string error)
        {
            error = null;
            XmlElem cur = from;
            for (int i = 0; i < Steps.Count; i++)
            {
                List<XmlElem> hits = MatchStep(Steps[i], cur);
                if (hits.Count == 1) { cur = hits[0]; continue; }
                string upTo = PathUpTo(i);
                if (hits.Count == 0)
                {
                    error = "\"" + upTo + "\" matches nothing" + NearMiss(Steps[i], cur);
                    return null;
                }
                error = hits.Count + " elements match \"" + upTo + "\"" + Differences(hits) + "; add a test or [#n]";
                return null;
            }
            return cur;
        }

        public XmlElem Resolve(XmlElem from)
        {
            string err;
            XmlElem e = TryResolve(from, out err);
            if (e == null) throw new ModKitException(from.Doc.Label + ": " + err);
            return e;
        }

        /// <summary>Every element the selector matches (no step has to be unique).</summary>
        public List<XmlElem> ResolveAll(XmlElem from)
        {
            var cur = new List<XmlElem> { from };
            foreach (RowStep step in Steps)
            {
                var next = new List<XmlElem>();
                foreach (XmlElem e in cur) next.AddRange(MatchStep(step, e));
                cur = next;
                if (cur.Count == 0) break;
            }
            return cur;
        }

        string PathUpTo(int i)
        {
            var parts = new string[i + 1];
            for (int k = 0; k <= i; k++) parts[k] = Steps[k].ToString();
            return string.Join("/", parts);
        }

        static string NearMiss(RowStep step, XmlElem parent)
        {
            int sameName = 0;
            foreach (XmlElem c in parent.Children) if (step.Name == "*" || c.Name == step.Name) sameName++;
            if (sameName == 0)
            {
                var names = new List<string>();
                foreach (XmlElem c in parent.Children) { if (!names.Contains(c.Name)) names.Add(c.Name); if (names.Count == 6) break; }
                if (names.Count == 0) return " (<" + parent.Name + "> at line " + parent.Line + " has no child elements)";
                return " (<" + parent.Name + "> at line " + parent.Line + " has no <" + step.Name + ">; it has " + string.Join(", ", names.ToArray()) + ")";
            }
            if (step.Nth > 0) return " (only " + sameName + " <" + step.Name + "> there)";
            foreach (KeyValuePair<string, string> t in step.Tests)
            {
                bool any = false;
                foreach (XmlElem c in parent.Children)
                    if ((step.Name == "*" || c.Name == step.Name) && (c.Attr(t.Key) ?? "") == t.Value) { any = true; break; }
                if (!any) return " (no <" + step.Name + "> has " + t.Key + "=\"" + t.Value + "\")";
            }
            return " (" + sameName + " <" + step.Name + "> there, none passes all tests)";
        }

        static string Differences(List<XmlElem> hits)
        {
            var names = new List<string>();
            foreach (XmlElem e in hits) foreach (XmlAttr a in e.Attrs) if (!names.Contains(a.Name)) names.Add(a.Name);
            var differ = new List<string>();
            foreach (string n in names)
            {
                string first = hits[0].Attr(n) ?? "";
                foreach (XmlElem e in hits)
                    if ((e.Attr(n) ?? "") != first) { differ.Add(n); break; }
                if (differ.Count == 6) break;
            }
            if (differ.Count == 0) return " and their attributes are the same";
            return "; they differ in " + string.Join(", ", differ.ToArray());
        }

        /// <summary>One step that picks e among its siblings: id first, then other attributes, then [#n].</summary>
        public static string StepFor(XmlElem e)
        {
            var step = new RowStep { Name = e.Name };
            if (e.Parent == null) return e.Name;
            string id = e.Attr("id");
            // a large parent has an index: the same rows, in document order, without a scan of all children for
            // each test (the lists of the index are its own and are not changed here)
            XmlElem.ChildIndex ix = e.Parent.Index();
            List<XmlElem> left;
            if (ix != null) left = ix.Named(e.Name);
            else
            {
                left = new List<XmlElem>();
                foreach (XmlElem c in e.Parent.Children) if (c.Name == e.Name) left.Add(c);
            }
            if (id != null) { step.Tests.Add(new KeyValuePair<string, string>("id", id)); left = Filter(ix, left, "id", id); }
            // rows that share an id are variants of one another: tell them apart by what makes them variants
            // ("[feature=]" is the row without one) before any attribute a mod is likely to change
            foreach (string v in new[] { "feature", "locale" })
            {
                if (left.Count < 2) break;
                string val = e.Attr(v) ?? "";
                List<XmlElem> f = Filter(ix, left, v, val);
                if (f.Count == left.Count) continue;
                step.Tests.Add(new KeyValuePair<string, string>(v, val));
                left = f;
            }
            if (left.Count > 1)
            {
                foreach (XmlAttr a in e.Attrs)
                {
                    if (a.Name == "id" || a.Name == "feature" || a.Name == "locale") continue;
                    List<XmlElem> f = Filter(ix, left, a.Name, a.Value);
                    if (f.Count == left.Count) continue;
                    step.Tests.Add(new KeyValuePair<string, string>(a.Name, a.Value));
                    left = f;
                    if (left.Count == 1) break;
                }
            }
            if (left.Count > 1) step.Nth = left.IndexOf(e) + 1;
            return step.ToString();
        }

        static List<XmlElem> Filter(XmlElem.ChildIndex ix, List<XmlElem> list, string name, string value)
        {
            if (ix != null) return ix.With(list, name, value);
            var f = new List<XmlElem>();
            foreach (XmlElem c in list) if ((c.Attr(name) ?? "") == value) f.Add(c);
            return f;
        }

        /// <summary>A selector for e from the document's root element ("" for the root itself).</summary>
        public static string Canonical(XmlElem e)
        {
            var parts = new List<string>();
            for (XmlElem c = e; c != null && c.Parent != null; c = c.Parent) parts.Insert(0, StepFor(c));
            return string.Join("/", parts.ToArray());
        }

        /// <summary>Element names from the root's children down to e, joined by '/'.</summary>
        public static string NamePath(XmlElem e, bool withRoot)
        {
            var parts = new List<string>();
            for (XmlElem c = e; c != null; c = c.Parent) { if (c.Parent == null && !withRoot) break; parts.Insert(0, c.Name); }
            return string.Join("/", parts.ToArray());
        }
    }
}
