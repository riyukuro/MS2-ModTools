// A large flat table (a string file: a root element whose children are all self-closing rows) held as the bytes
// of the file plus where each row starts and ends, instead of as a parsed document. The parsed form of such a
// file is more than ten times its bytes, and an entity reads one per language and target; rows are parsed when
// they are asked for, as a small document of the root's start tag and those rows.
using System;
using System.Collections.Generic;
using System.Text;

namespace MS2.ModKit.Xml
{
    internal sealed class XmlRows
    {
        byte[] b;
        string label, rootOpen, rootName;
        int[] starts, ends;     // '<' of a row and the '>' that ends it
        WeakReference full;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal int Count { get { return starts.Length; } }
        /// <summary>The bytes this holds on to.</summary>
        internal long Cost { get { return b.Length + 8L * starts.Length; } }

        static bool Ws(byte c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n'; }

        static bool At(byte[] b, int p, string s)
        {
            if (p + s.Length > b.Length) return false;
            for (int i = 0; i < s.Length; i++) if (b[p + i] != s[i]) return false;
            return true;
        }

        static int Find(byte[] b, int p, string s)
        {
            for (; p + s.Length <= b.Length; p++) if (At(b, p, s)) return p;
            return -1;
        }

        // the '>' that ends the tag at p (quotes may hold one), -1 when there is none
        static int TagEnd(byte[] b, int p)
        {
            byte q = 0;
            for (p++; p < b.Length; p++)
            {
                byte c = b[p];
                if (q != 0) { if (c == q) q = 0; }
                else if (c == '"' || c == '\'') q = c;
                else if (c == '>') return p;
                else if (c == '<') return -1;
            }
            return -1;
        }

        /// <summary>Null when the file is not a UTF-8 table of self-closing rows (it is parsed whole then).</summary>
        internal static XmlRows TryScan(byte[] raw, string label)
        {
            int p = 0, n = raw.Length;
            if (n >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) p = 3;
            else if (n >= 2 && (raw[0] == 0xFF && raw[1] == 0xFE || raw[0] == 0xFE && raw[1] == 0xFF)) return null;
            for (; ; )
            {
                while (p < n && Ws(raw[p])) p++;
                if (At(raw, p, "<?"))
                {
                    int e = Find(raw, p, "?>");
                    if (e < 0) return null;
                    string decl = Encoding.ASCII.GetString(raw, p, e - p).ToLowerInvariant();
                    int at = decl.IndexOf("encoding", StringComparison.Ordinal);
                    if (at >= 0 && decl.IndexOf("utf-8", at, StringComparison.Ordinal) < 0) return null;
                    p = e + 2;
                }
                else if (At(raw, p, "<!--"))
                {
                    int e = Find(raw, p, "-->");
                    if (e < 0) return null;
                    p = e + 3;
                }
                else break;
            }
            if (p >= n || raw[p] != '<' || At(raw, p, "<!") || At(raw, p, "</")) return null;
            int rootEnd = TagEnd(raw, p);
            if (rootEnd < 0 || raw[rootEnd - 1] == '/') return null;
            int ne = p + 1;
            while (ne < rootEnd && !Ws(raw[ne])) ne++;
            var s = new List<int>();
            var en = new List<int>();
            int rootAt = p;
            p = rootEnd + 1;
            for (; ; )
            {
                while (p < n && Ws(raw[p])) p++;
                if (p >= n) return null;
                if (At(raw, p, "<!--"))
                {
                    int e = Find(raw, p, "-->");
                    if (e < 0) return null;
                    p = e + 3;
                    continue;
                }
                if (At(raw, p, "</")) break;
                if (raw[p] != '<' || At(raw, p, "<!") || At(raw, p, "<?")) return null;
                int end = TagEnd(raw, p);
                if (end < 0 || raw[end - 1] != '/') return null;
                s.Add(p); en.Add(end);
                p = end + 1;
            }
            // what follows the root's end tag must be nothing a parse would mind
            int close = TagEnd(raw, p);
            if (close < 0) return null;
            for (int t = close + 1; t < n; t++) if (!Ws(raw[t])) return null;
            var r = new XmlRows { b = raw, label = label, starts = s.ToArray(), ends = en.ToArray() };
            try
            {
                Utf8.GetCharCount(raw);
                r.rootOpen = Utf8.GetString(raw, rootAt, rootEnd + 1 - rootAt);
                r.rootName = Utf8.GetString(raw, rootAt + 1, ne - rootAt - 1);
            }
            catch (ArgumentException) { return null; }
            return r;
        }

        /// <summary>The value of an attribute of a row, null when the row has none of that name.</summary>
        internal string Attr(int row, string name)
        {
            int p = starts[row] + 1, e = ends[row];
            while (p < e && !Ws(b[p]) && b[p] != '/') p++;
            for (; ; )
            {
                while (p < e && Ws(b[p])) p++;
                if (p >= e || b[p] == '/') return null;
                int ns = p;
                while (p < e && b[p] != '=' && !Ws(b[p])) p++;
                int nl = p - ns;
                while (p < e && Ws(b[p])) p++;
                if (p >= e || b[p] != '=') return null;
                p++;
                while (p < e && Ws(b[p])) p++;
                if (p >= e) return null;
                byte q = b[p];
                if (q != '"' && q != '\'') return null;
                int vs = ++p;
                while (p < e && b[p] != q) p++;
                if (nl == name.Length && At(b, ns, name))
                {
                    string v = Utf8.GetString(b, vs, p - vs);
                    return v.IndexOf('&') >= 0 ? XmlDoc.Unescape(v) : v;
                }
                p++;
            }
        }

        /// <summary>The rows parsed, as the children of a root like the file's (so a row knows its parent).</summary>
        internal List<XmlElem> Parse(List<int> rows)
        {
            var sb = new StringBuilder(rootOpen);
            foreach (int r in rows) sb.Append('\n').Append(Utf8.GetString(b, starts[r], ends[r] + 1 - starts[r]));
            sb.Append("\n</").Append(rootName).Append('>');
            XmlDoc d = XmlDoc.ParseFragment(sb.ToString(), label);
            return new List<XmlElem>(d.Top[0].Children);
        }

        /// <summary>How often a table held as rows was parsed whole (tests count it; no editor path should need it).</summary>
        internal static long FullParses;

        /// <summary>The rows whose attributes have these values, parsed (Parse); every row when there is no test.</summary>
        internal List<XmlElem> Where(List<KeyValuePair<string, string>> tests)
        {
            var rows = new List<int>();
            for (int i = 0; i < Count; i++)
            {
                bool ok = true;
                foreach (KeyValuePair<string, string> t in tests)
                    if (Attr(i, t.Key) != t.Value) { ok = false; break; }
                if (ok) rows.Add(i);
            }
            return rows.Count > 0 ? Parse(rows) : new List<XmlElem>();
        }

        /// <summary>The whole file parsed; held only as long as someone uses it.</summary>
        internal XmlDoc Full()
        {
            XmlDoc d = full == null ? null : (XmlDoc)full.Target;
            if (d == null) { full = new WeakReference(d = XmlDoc.Parse(b, label)); System.Threading.Interlocked.Increment(ref FullParses); }
            return d;
        }
    }
}
