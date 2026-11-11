// The well-formedness errors of expat, as the trigger tools' parse_xml (tools/trigger/normalize.py) reports them:
// expat's error string and line. XmlDoc is lenient (it keeps files byte-exact for editing and reads values with a
// '<' or a bare '&'); the lint checks a script with this first, so its parse findings are the ones lint.py gives.
using System;
using System.Collections.Generic;

namespace MS2.ModKit.Xml
{
    public sealed class ExpatError
    {
        public string Message;      // expat's ErrorString, e.g. "mismatched tag"
        public int Line;
    }

    public static class ExpatCheck
    {
        const string Invalid = "not well-formed (invalid token)", Unclosed = "unclosed token";

        sealed class Scan
        {
            internal string T;
            internal int N;
            readonly List<int> starts = new List<int> { 0 };

            internal Scan(string t)
            {
                T = t; N = t.Length;
                for (int i = 0; i < N; i++)
                {
                    if (t[i] == '\n') starts.Add(i + 1);
                    else if (t[i] == '\r' && (i + 1 >= N || t[i + 1] != '\n')) starts.Add(i + 1);
                }
            }

            internal int LineOf(int pos)
            {
                int lo = 0, hi = starts.Count;
                while (lo < hi) { int mid = (lo + hi) / 2; if (starts[mid] <= pos) lo = mid + 1; else hi = mid; }
                return lo;
            }
        }

        sealed class Fail : Exception
        {
            internal readonly string What; internal readonly int Pos;
            internal Fail(string what, int pos) { What = what; Pos = pos; }
        }

        static bool IsSpace(char c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n'; }
        static bool NameStart(char c) { return char.IsLetter(c) || c == '_' || c == ':' || c > 127; }
        static bool NameChar(char c) { return NameStart(c) || char.IsDigit(c) || c == '.' || c == '-'; }
        static bool BadChar(char c) { return c < 0x20 && c != '\t' && c != '\n' && c != '\r' || c == '￾' || c == '￿'; }

        /// <summary>Null when expat reads the text; else its error and line.</summary>
        public static ExpatError Check(string text)
        {
            var s = new Scan(text ?? "");
            try { Run(s); return null; }
            catch (Fail f) { return new ExpatError { Message = f.What, Line = s.LineOf(f.Pos) }; }
        }

        static int Name(Scan s, int p)
        {
            if (p >= s.N) throw new Fail(Unclosed, p);
            if (!NameStart(s.T[p])) throw new Fail(Invalid, p);
            while (p < s.N && NameChar(s.T[p])) p++;
            return p;
        }

        /// <summary>A reference at p ('&amp;'); returns the position after its ';'.</summary>
        static int Reference(Scan s, int p, int tokenStart)
        {
            string t = s.T;
            int q = p + 1;
            if (q >= s.N) throw new Fail(Unclosed, tokenStart);
            if (t[q] == '#')
            {
                q++;
                bool hex = q < s.N && t[q] == 'x';
                if (hex) q++;
                int ds = q;
                long v = 0;
                while (q < s.N && (hex ? Uri.IsHexDigit(t[q]) : char.IsDigit(t[q])))
                {
                    v = v * (hex ? 16 : 10) + Convert.ToInt32(t[q].ToString(), hex ? 16 : 10);
                    if (v > 0x10FFFF) v = 0x110000;
                    q++;
                }
                if (q >= s.N) throw new Fail(Unclosed, tokenStart);
                if (q == ds || t[q] != ';') throw new Fail(Invalid, q);
                if (v == 0 || v > 0x10FFFF || (v < 0x20 && v != 9 && v != 10 && v != 13) || (v >= 0xD800 && v <= 0xDFFF) || v == 0xFFFE || v == 0xFFFF)
                    throw new Fail("reference to invalid character number", p);
                return q + 1;
            }
            int e = Name(s, q);
            if (e >= s.N) throw new Fail(Unclosed, tokenStart);
            if (t[e] != ';') throw new Fail(Invalid, e);
            string n = t.Substring(q, e - q);
            if (n != "amp" && n != "lt" && n != "gt" && n != "quot" && n != "apos") throw new Fail("undefined entity", p);
            return e + 1;
        }

        static void Run(Scan s)
        {
            string t = s.T;
            int n = s.N, i = 0;
            if (n > 0 && t[0] == '﻿') i = 1;
            var open = new List<string>();
            bool rootSeen = false;
            int textStart = -1;
            while (i < n)
            {
                char c = t[i];
                if (c != '<')
                {
                    if (BadChar(c)) throw new Fail(Invalid, i);
                    if (open.Count == 0)
                    {
                        if (!IsSpace(c)) throw new Fail(rootSeen ? "junk after document element" : "syntax error", i);
                        i++; continue;
                    }
                    if (c == '&') { i = Reference(s, i, i); continue; }
                    if (c == ']' && string.CompareOrdinal(t, i, "]]>", 0, 3) == 0) throw new Fail(Invalid, i);
                    if (textStart < 0) textStart = i;
                    i++; continue;
                }
                textStart = -1;
                int lt = i;
                if (i + 1 >= n) throw new Fail(Unclosed, lt);
                char d = t[i + 1];
                if (d == '?')
                {
                    int ne = Name(s, i + 2);
                    string target = t.Substring(i + 2, ne - i - 2);
                    if (string.Equals(target, "xml", StringComparison.OrdinalIgnoreCase) && lt != (t[0] == '﻿' ? 1 : 0))
                        throw new Fail("XML or text declaration not at start of entity", lt);
                    int pe = t.IndexOf("?>", ne, StringComparison.Ordinal);
                    if (pe < 0) throw new Fail(Unclosed, lt);
                    for (int k = ne; k < pe; k++) if (BadChar(t[k])) throw new Fail(Invalid, k);
                    i = pe + 2; continue;
                }
                if (d == '!')
                {
                    if (string.CompareOrdinal(t, i, "<!--", 0, 4) == 0)
                    {
                        int k = i + 4;
                        for (; ; k++)
                        {
                            if (k + 1 >= n) throw new Fail(Unclosed, lt);
                            if (BadChar(t[k])) throw new Fail(Invalid, k);
                            if (t[k] == '-' && t[k + 1] == '-')
                            {
                                if (k + 2 >= n) throw new Fail(Unclosed, lt);
                                if (t[k + 2] != '>') throw new Fail(Invalid, k);
                                break;
                            }
                        }
                        i = k + 3; continue;
                    }
                    if (string.CompareOrdinal(t, i, "<![CDATA[", 0, 9) == 0 && open.Count > 0)
                    {
                        int ce = t.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                        if (ce < 0) throw new Fail(Unclosed, lt);
                        i = ce + 3; continue;
                    }
                    if (open.Count > 0 || rootSeen || string.CompareOrdinal(t, i, "<!DOCTYPE", 0, 9) != 0) throw new Fail(open.Count > 0 ? Invalid : rootSeen ? "junk after document element" : "syntax error", lt);
                    int depth = 0, m = i + 2;
                    for (; m < n; m++)
                    {
                        if (t[m] == '[') depth++;
                        else if (t[m] == ']') depth--;
                        else if (t[m] == '>' && depth <= 0) break;
                    }
                    if (m >= n) throw new Fail(Unclosed, lt);
                    i = m + 1; continue;
                }
                if (d == '/')
                {
                    if (open.Count == 0) throw new Fail(rootSeen ? Invalid : "syntax error", lt);
                    int ne = Name(s, i + 2);
                    int p = ne;
                    while (p < n && IsSpace(t[p])) p++;
                    if (p >= n) throw new Fail(Unclosed, lt);
                    if (t[p] != '>') throw new Fail(Invalid, p);
                    if (t.Substring(i + 2, ne - i - 2) != open[open.Count - 1]) throw new Fail("mismatched tag", lt + 2);
                    open.RemoveAt(open.Count - 1);
                    i = p + 1; continue;
                }
                // a start tag
                if (open.Count == 0 && rootSeen) throw new Fail("junk after document element", lt);
                int nameEnd = Name(s, i + 1);
                string name = t.Substring(i + 1, nameEnd - i - 1);
                var attrs = new HashSet<string>(StringComparer.Ordinal);
                int q2 = nameEnd;
                bool self = false;
                for (; ; )
                {
                    int ws = q2;
                    while (q2 < n && IsSpace(t[q2])) q2++;
                    if (q2 >= n) throw new Fail(Unclosed, lt);
                    if (t[q2] == '>') { q2++; break; }
                    if (t[q2] == '/')
                    {
                        if (q2 + 1 >= n) throw new Fail(Unclosed, lt);
                        if (t[q2 + 1] != '>') throw new Fail(Invalid, q2 + 1);
                        self = true; q2 += 2; break;
                    }
                    if (q2 == ws) throw new Fail(Invalid, q2);      // no space before an attribute
                    int an = q2;
                    q2 = Name(s, q2);
                    string aname = t.Substring(an, q2 - an);
                    while (q2 < n && IsSpace(t[q2])) q2++;
                    if (q2 >= n) throw new Fail(Unclosed, lt);
                    if (t[q2] != '=') throw new Fail(Invalid, q2);
                    q2++;
                    while (q2 < n && IsSpace(t[q2])) q2++;
                    if (q2 >= n) throw new Fail(Unclosed, lt);
                    char quote = t[q2];
                    if (quote != '"' && quote != '\'') throw new Fail(Invalid, q2);
                    q2++;
                    for (; ; )
                    {
                        if (q2 >= n) throw new Fail(Unclosed, lt);
                        char v = t[q2];
                        if (v == quote) { q2++; break; }
                        if (v == '<' || BadChar(v)) throw new Fail(Invalid, q2);
                        if (v == '&') { q2 = Reference(s, q2, lt); continue; }
                        q2++;
                    }
                    if (!attrs.Add(aname)) throw new Fail("duplicate attribute", an);
                }
                rootSeen = true;
                if (!self) open.Add(name);
                i = q2;
            }
            if (open.Count > 0 || !rootSeen) throw new Fail("no element found", n);
        }
    }
}
