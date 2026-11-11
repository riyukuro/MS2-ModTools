// Lossless text model of one XML file. The file is decoded to a string that encodes back to the same bytes, a
// small scanner records where every element and attribute sits in that string, and edits are span replacements
// applied to the original text. Whatever is not edited keeps its bytes: BOM, encoding, line ends, attribute
// order, quotes, whitespace, comments.
using System;
using System.Collections.Generic;
using System.Text;

namespace MS2.ModKit.Xml
{
    public sealed class XmlAttr
    {
        public string Name;
        public string Value;            // entities decoded
        internal int LeadStart;         // start of the whitespace before the name
        internal int ValueStart, ValueEnd;   // inside the quotes
        internal int End;               // after the closing quote
        internal char Quote;
    }

    public sealed class XmlElem
    {
        public string Name;
        public XmlElem Parent;          // null for a top-level element
        public XmlDoc Doc;
        public readonly List<XmlAttr> Attrs = new List<XmlAttr>();
        public readonly List<XmlElem> Children = new List<XmlElem>();
        internal int Start;             // '<'
        internal int NameEnd;
        internal int TagEnd;            // after '>' of the start tag
        internal int ContentEnd;        // '<' of the end tag (== TagEnd when self-closing)
        internal int End;               // after the element
        public bool SelfClosing;

        public XmlAttr FindAttr(string name)
        {
            foreach (XmlAttr a in Attrs) if (a.Name == name) return a;
            return null;
        }

        sealed class IdIndex { internal int Count; internal Dictionary<string, List<XmlElem>> By; }
        IdIndex ids;

        /// <summary>
        /// The children whose id attribute has the value, in their order; null for an element with few children
        /// (the caller then looks at all of them). The index is made at the first question, so that many
        /// selectors into one long table do not each walk all of its rows.
        /// </summary>
        internal List<XmlElem> ChildrenWithId(string id)
        {
            if (Children.Count < 64) return null;
            IdIndex ix = ids;
            if (ix == null || ix.Count != Children.Count)
            {
                var by = new Dictionary<string, List<XmlElem>>(StringComparer.Ordinal);
                foreach (XmlElem c in Children)
                {
                    string v = c.Attr("id") ?? "";
                    List<XmlElem> l;
                    if (!by.TryGetValue(v, out l)) by[v] = l = new List<XmlElem>(1);
                    l.Add(c);
                }
                ids = ix = new IdIndex { Count = Children.Count, By = by };
            }
            List<XmlElem> hits;
            return ix.By.TryGetValue(id, out hits) ? hits : NoChildren;
        }

        static readonly List<XmlElem> NoChildren = new List<XmlElem>();

        /// <summary>The attribute value, or null if the element does not have it.</summary>
        public string Attr(string name) { XmlAttr a = FindAttr(name); return a == null ? null : a.Value; }

        /// <summary>The element as it stands in the file.</summary>
        public string OuterText { get { return Doc.Text.Substring(Start, End - Start); } }

        /// <summary>Decoded text content; null when the element has child elements.</summary>
        public string InnerText
        {
            get
            {
                if (Children.Count > 0) return null;
                string raw = Doc.Text.Substring(TagEnd, ContentEnd - TagEnd);
                if (raw.StartsWith("<![CDATA[") && raw.EndsWith("]]>")) return raw.Substring(9, raw.Length - 12);
                return XmlDoc.Unescape(raw);
            }
        }

        public int Line { get { return Doc.LineOf(Start); } }
    }

    public sealed class XmlDoc
    {
        public string Label;                    // for messages: archive and entry
        public string Text;
        public XmlElem Root;                    // the first top-level element
        public readonly List<XmlElem> Top = new List<XmlElem>();
        public string NewLine = "\r\n";
        public string IndentUnit = "\t";

        /// <summary>Name of the encoding the text was decoded with ("utf-8", "utf-16", "ks_c_5601-1987", "iso-8859-1", ...).</summary>
        public string EncodingName = "utf-8";
        /// <summary>True when the bytes were not valid in the declared encoding and another one had to be taken.</summary>
        public bool EncodingFallback;
        Encoding encoding = new UTF8Encoding(false, true);
        byte[] bom = new byte[0];

#if !NET35
        static XmlDoc() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
#endif

        XmlDoc() { }

        // ---- decoding ----

        public static XmlDoc Parse(byte[] bytes, string label)
        {
            var d = new XmlDoc { Label = label };
            d.Decode(bytes);
            d.Scan(false);
            return d;
        }

        /// <summary>The text of the bytes as Parse decodes it (BOM, declared encoding, fallbacks), without the scan.</summary>
        internal static string DecodeText(byte[] bytes, string label)
        {
            var d = new XmlDoc { Label = label };
            d.Decode(bytes);
            return d.Text;
        }

        /// <summary>A fragment: any number of top-level elements, no encoding.</summary>
        public static XmlDoc ParseFragment(string text, string label)
        {
            var d = new XmlDoc { Label = label, Text = text };
            d.Scan(true);
            return d;
        }

        static Encoding Strict(string name)
        {
            try { return Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        static bool StartsWith(byte[] b, params byte[] p)
        {
            if (b.Length < p.Length) return false;
            for (int i = 0; i < p.Length; i++) if (b[i] != p[i]) return false;
            return true;
        }

        bool TryDecode(byte[] bytes, int skip, Encoding enc)
        {
            if (enc == null) return false;
            string s;
            try { s = enc.GetString(bytes, skip, bytes.Length - skip); }
            catch (DecoderFallbackException) { return false; }
            catch (ArgumentException) { return false; }
            byte[] back;
            try { back = enc.GetBytes(s); }
            catch (EncoderFallbackException) { return false; }
            catch (ArgumentException) { return false; }
            if (back.Length != bytes.Length - skip) return false;
            for (int i = 0; i < back.Length; i++) if (back[i] != bytes[skip + i]) return false;
            Text = s; encoding = enc;
            return true;
        }

        void Decode(byte[] bytes)
        {
            if (StartsWith(bytes, 0xEF, 0xBB, 0xBF))
            {
                bom = new byte[] { 0xEF, 0xBB, 0xBF };
                if (TryDecode(bytes, 3, new UTF8Encoding(false, true))) { EncodingName = "utf-8"; return; }
            }
            else if (StartsWith(bytes, 0xFF, 0xFE))
            {
                bom = new byte[] { 0xFF, 0xFE };
                if (TryDecode(bytes, 2, new UnicodeEncoding(false, false, true))) { EncodingName = "utf-16"; return; }
            }
            else if (StartsWith(bytes, 0xFE, 0xFF))
            {
                bom = new byte[] { 0xFE, 0xFF };
                if (TryDecode(bytes, 2, new UnicodeEncoding(true, false, true))) { EncodingName = "utf-16BE"; return; }
            }
            else
            {
                string declared = DeclaredEncoding(bytes);
                if (declared != null && !IsUtf8Name(declared))
                {
                    if (TryDecode(bytes, 0, Strict(declared))) { EncodingName = declared; return; }
                }
                else if (TryDecode(bytes, 0, new UTF8Encoding(false, true))) { EncodingName = "utf-8"; return; }
            }
            // not what it says it is: Korean code page, then a byte-transparent reading
            EncodingFallback = true;
            int skip = bom.Length;
            if (TryDecode(bytes, skip, Strict("ks_c_5601-1987"))) { EncodingName = "ks_c_5601-1987"; return; }
            if (TryDecode(bytes, skip, Strict("iso-8859-1"))) { EncodingName = "iso-8859-1"; return; }
            throw new ModKitException(Label + ": cannot decode the text");
        }

        static bool IsUtf8Name(string n) { return string.Equals(n, "utf-8", StringComparison.OrdinalIgnoreCase) || string.Equals(n, "utf8", StringComparison.OrdinalIgnoreCase); }

        static string DeclaredEncoding(byte[] bytes)
        {
            int n = Math.Min(bytes.Length, 200);
            var sb = new StringBuilder(n);
            for (int i = 0; i < n; i++) sb.Append((char)bytes[i]);
            string head = sb.ToString();
            if (!head.StartsWith("<?xml")) return null;
            int end = head.IndexOf("?>", StringComparison.Ordinal);
            if (end < 0) return null;
            int p = head.IndexOf("encoding", StringComparison.Ordinal);
            if (p < 0 || p > end) return null;
            p = head.IndexOfAny(new[] { '"', '\'' }, p);
            if (p < 0 || p > end) return null;
            int q = head.IndexOf(head[p], p + 1);
            if (q < 0 || q > end) return null;
            return head.Substring(p + 1, q - p - 1).Trim();
        }

        /// <summary>Text back to bytes in the file's encoding, with its BOM.</summary>
        public byte[] Encode(string text)
        {
            byte[] body;
            try { body = encoding.GetBytes(text); }
            catch (EncoderFallbackException e)
            {
                throw new ModKitException(Label + ": the new text has a character that the file's encoding (" + EncodingName + ") cannot hold", e);
            }
            if (bom.Length == 0) return body;
            var all = new byte[bom.Length + body.Length];
            Buffer.BlockCopy(bom, 0, all, 0, bom.Length);
            Buffer.BlockCopy(body, 0, all, bom.Length, body.Length);
            return all;
        }

        // ---- scanning ----

        // the positions after each line break of the text, made at the first question (a long file is asked for the
        // line of many places: every edit names its line). One object, so that two threads never see the starts
        // of one text beside another text
        sealed class LineIndex { internal string Of; internal int[] Starts; }
        LineIndex lines;

        public int LineOf(int pos)
        {
            string t = Text;
            LineIndex ix = lines;
            if (ix == null || !ReferenceEquals(ix.Of, t))
            {
                var starts = new List<int>();
                for (int i = 0; i < t.Length; i++) if (t[i] == '\n') starts.Add(i + 1);
                lines = ix = new LineIndex { Of = t, Starts = starts.ToArray() };
            }
            int[] lineStarts = ix.Starts;
            // the line breaks before pos: the starts that are <= pos
            int lo = 0, hi = lineStarts.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (lineStarts[mid] <= pos) lo = mid + 1; else hi = mid;
            }
            return lo + 1;
        }

        ModKitException Bad(int pos, string what)
        {
            return new ModKitException(Label + " line " + LineOf(pos) + ": " + what);
        }

        static bool IsSpace(char c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n'; }

        int Find(string what, int from, string forWhat)
        {
            int p = Text.IndexOf(what, from, StringComparison.Ordinal);
            if (p < 0) throw Bad(from, "unterminated " + forWhat);
            return p;
        }

        void Scan(bool fragment)
        {
            string t = Text;
            int nl = t.IndexOf('\n');
            if (nl >= 0) NewLine = nl > 0 && t[nl - 1] == '\r' ? "\r\n" : "\n";
            XmlElem cur = null;
            int i = 0, n = t.Length;
            bool unitKnown = false;
            while (i < n)
            {
                int lt = t.IndexOf('<', i);
                if (lt < 0) break;
                if (cur == null)
                    for (int k = i; k < lt; k++) if (!IsSpace(t[k]) && t[k] != '﻿') throw Bad(k, "text outside of an element");
                i = lt;
                if (i + 1 >= n) throw Bad(i, "stray '<' at the end");
                char c = t[i + 1];
                if (c == '?') { i = Find("?>", i + 2, "processing instruction") + 2; continue; }
                if (c == '!')
                {
                    if (string.CompareOrdinal(t, i, "<!--", 0, 4) == 0) { i = Find("-->", i + 4, "comment") + 3; continue; }
                    if (string.CompareOrdinal(t, i, "<![CDATA[", 0, 9) == 0)
                    {
                        if (cur == null) throw Bad(i, "CDATA outside of an element");
                        i = Find("]]>", i + 9, "CDATA section") + 3; continue;
                    }
                    int depth = 0, k = i + 2;      // <!DOCTYPE ... [ ... ]>
                    for (; k < n; k++)
                    {
                        if (t[k] == '[') depth++;
                        else if (t[k] == ']') depth--;
                        else if (t[k] == '>' && depth <= 0) break;
                    }
                    if (k >= n) throw Bad(i, "unterminated declaration");
                    i = k + 1; continue;
                }
                if (c == '/')
                {
                    int gt = Find(">", i + 2, "end tag");
                    string name = t.Substring(i + 2, gt - i - 2).Trim();
                    if (cur == null) throw Bad(i, "end tag </" + name + "> without a start tag");
                    if (name != cur.Name) throw Bad(i, "end tag </" + name + "> closes <" + cur.Name + "> (line " + LineOf(cur.Start) + ")");
                    cur.ContentEnd = i; cur.End = gt + 1;
                    cur = cur.Parent;
                    i = gt + 1; continue;
                }
                // start tag
                var e = new XmlElem { Doc = this, Parent = cur, Start = i };
                int p = i + 1;
                while (p < n && !IsSpace(t[p]) && t[p] != '/' && t[p] != '>') p++;
                if (p == i + 1 || p >= n) throw Bad(i, "bad start tag");
                e.Name = t.Substring(i + 1, p - i - 1);
                e.NameEnd = p;
                for (; ; )
                {
                    int lead = p;
                    while (p < n && IsSpace(t[p])) p++;
                    if (p >= n) throw Bad(i, "unterminated start tag <" + e.Name);
                    if (t[p] == '>') { e.TagEnd = p + 1; break; }
                    if (t[p] == '/')
                    {
                        if (p + 1 >= n || t[p + 1] != '>') throw Bad(p, "'/' inside the tag <" + e.Name);
                        e.SelfClosing = true; e.TagEnd = e.ContentEnd = e.End = p + 2; break;
                    }
                    int ns = p;
                    while (p < n && !IsSpace(t[p]) && t[p] != '=' && t[p] != '>' && t[p] != '/') p++;
                    var a = new XmlAttr { Name = t.Substring(ns, p - ns), LeadStart = lead };
                    while (p < n && IsSpace(t[p])) p++;
                    if (p >= n || t[p] != '=') throw Bad(ns, "attribute " + a.Name + " of <" + e.Name + "> has no value");
                    p++;
                    while (p < n && IsSpace(t[p])) p++;
                    if (p >= n || (t[p] != '"' && t[p] != '\'')) throw Bad(ns, "value of attribute " + a.Name + " of <" + e.Name + "> is not quoted");
                    a.Quote = t[p];
                    int q = t.IndexOf(a.Quote, p + 1);
                    if (q < 0) throw Bad(ns, "unterminated value of attribute " + a.Name);
                    a.ValueStart = p + 1; a.ValueEnd = q; a.End = q + 1;
                    a.Value = Unescape(t.Substring(p + 1, q - p - 1));
                    if (e.FindAttr(a.Name) != null) throw Bad(ns, "attribute " + a.Name + " twice in <" + e.Name + ">");
                    e.Attrs.Add(a);
                    p = q + 1;
                }
                if (cur == null)
                {
                    if (Top.Count > 0 && !fragment) throw Bad(i, "a second root element <" + e.Name + ">");
                    Top.Add(e);
                }
                else
                {
                    cur.Children.Add(e);
                    if (!unitKnown)
                    {
                        string a = IndentOf(cur.Start), b = IndentOf(e.Start);
                        if (IsFirstOnLine(e.Start) && b.Length > a.Length && b.StartsWith(a)) { IndentUnit = b.Substring(a.Length); unitKnown = true; }
                    }
                }
                if (!e.SelfClosing) cur = e;
                i = e.TagEnd;
            }
            if (cur != null) throw Bad(cur.Start, "<" + cur.Name + "> is never closed");
            if (Top.Count == 0) throw new ModKitException(Label + ": no element");
            Root = Top[0];
        }

        /// <summary>The whitespace at the start of the line that holds pos.</summary>
        internal string IndentOf(int pos)
        {
            int ls = pos;
            while (ls > 0 && Text[ls - 1] != '\n') ls--;
            int e = ls;
            while (e < pos && (Text[e] == ' ' || Text[e] == '\t')) e++;
            return Text.Substring(ls, e - ls);
        }

        internal bool IsFirstOnLine(int pos)
        {
            int k = pos;
            while (k > 0 && (Text[k - 1] == ' ' || Text[k - 1] == '\t')) k--;
            return k == 0 || Text[k - 1] == '\n';
        }

        // ---- entities ----

        public static string Unescape(string s)
        {
            if (s.IndexOf('&') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                int semi;
                if (c != '&' || (semi = s.IndexOf(';', i)) < 0 || semi - i > 10) { sb.Append(c); continue; }
                string ent = s.Substring(i + 1, semi - i - 1);
                string rep = null;
                switch (ent)
                {
                    case "amp": rep = "&"; break;
                    case "lt": rep = "<"; break;
                    case "gt": rep = ">"; break;
                    case "quot": rep = "\""; break;
                    case "apos": rep = "'"; break;
                    default:
                        if (ent.Length > 1 && ent[0] == '#')
                        {
                            int code;
                            bool ok = ent[1] == 'x' || ent[1] == 'X'
                                ? int.TryParse(ent.Substring(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out code)
                                : int.TryParse(ent.Substring(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out code);
                            if (ok && code > 0 && code <= 0x10FFFF && (code < 0xD800 || code > 0xDFFF)) rep = char.ConvertFromUtf32(code);
                        }
                        break;
                }
                if (rep == null) { sb.Append(c); continue; }
                sb.Append(rep);
                i = semi;
            }
            return sb.ToString();
        }

        public static string EscapeAttr(string s, char quote)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '\n': sb.Append("&#10;"); break;
                    case '\r': sb.Append("&#13;"); break;
                    case '\t': sb.Append("&#9;"); break;
                    case '"': if (quote == '"') sb.Append("&quot;"); else sb.Append(c); break;
                    case '\'': if (quote == '\'') sb.Append("&apos;"); else sb.Append(c); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        public static string EscapeText(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }

    /// <summary>Collects edits against one XmlDoc and applies them to its text in one pass.</summary>
    public sealed class XmlEditor
    {
        sealed class Edit
        {
            internal int Start, End, Seq, Rank;
            internal string Text, What;
            internal bool Dropped;
        }

        public readonly XmlDoc Doc;
        readonly List<Edit> edits = new List<Edit>();
        readonly Dictionary<string, Edit> attrEdits = new Dictionary<string, Edit>();
        sealed class Appended { internal string Text; internal int Rank; }
        readonly Dictionary<XmlElem, List<Appended>> appends = new Dictionary<XmlElem, List<Appended>>();
        readonly Dictionary<XmlElem, Edit> deletes = new Dictionary<XmlElem, Edit>();
        readonly List<XmlElem> appendOrder = new List<XmlElem>();
        int seq;

        /// <summary>
        /// The rank of the one who makes the next edits. Inserts at one place are written in the order of their
        /// ranks, the lowest first, whatever the order of the calls; inserts of one rank keep the call order.
        /// </summary>
        public int Rank;

        public XmlEditor(XmlDoc doc) { Doc = doc; }

        public bool IsEmpty { get { return edits.Count == 0 && appends.Count == 0; } }

        Edit Add(int start, int end, string text, string what)
        {
            var e = new Edit { Start = start, End = end, Text = text, What = what, Seq = seq++, Rank = Rank };
            edits.Add(e);
            return e;
        }

        static string Key(XmlElem e, string name) { return e.Start + "\n" + name; }

        /// <summary>Sets an attribute. A new attribute goes after the last one. Setting twice keeps the last value.</summary>
        public void SetAttr(XmlElem e, string name, string value)
        {
            if (name.Length == 0 || name.IndexOfAny(new[] { ' ', '\t', '=', '"', '\'', '<', '>', '/' }) >= 0)
                throw new ModKitException("bad attribute name \"" + name + "\"");
            string key = Key(e, name);
            Edit old;
            if (attrEdits.TryGetValue(key, out old)) { old.Dropped = true; attrEdits.Remove(key); }
            XmlAttr a = e.FindAttr(name);
            string what = "set " + name + " of <" + e.Name + "> (line " + e.Line + ")";
            if (a != null) attrEdits[key] = Add(a.ValueStart, a.ValueEnd, XmlDoc.EscapeAttr(value, a.Quote), what);
            else
            {
                int at = e.Attrs.Count > 0 ? e.Attrs[e.Attrs.Count - 1].End : e.NameEnd;
                attrEdits[key] = Add(at, at, " " + name + "=\"" + XmlDoc.EscapeAttr(value, '"') + "\"", what);
            }
        }

        public void RemoveAttr(XmlElem e, string name)
        {
            string key = Key(e, name);
            Edit old;
            if (attrEdits.TryGetValue(key, out old)) { old.Dropped = true; attrEdits.Remove(key); }
            XmlAttr a = e.FindAttr(name);
            if (a != null) attrEdits[key] = Add(a.LeadStart, a.End, "", "unset " + name + " of <" + e.Name + "> (line " + e.Line + ")");
        }

        /// <summary>Replaces the text of an element that has no child elements.</summary>
        public void SetText(XmlElem e, string text)
        {
            if (e.Children.Count > 0) throw new ModKitException(Doc.Label + " line " + e.Line + ": <" + e.Name + "> has child elements, its text cannot be set");
            string key = Key(e, "\ntext");
            Edit old;
            if (attrEdits.TryGetValue(key, out old)) { old.Dropped = true; attrEdits.Remove(key); }
            string what = "text of <" + e.Name + "> (line " + e.Line + ")";
            if (e.SelfClosing)
            {
                int slash = e.TagEnd - 2, s = slash;
                while (s > e.NameEnd && (Doc.Text[s - 1] == ' ' || Doc.Text[s - 1] == '\t')) s--;
                if (e.Attrs.Count > 0 && s < e.Attrs[e.Attrs.Count - 1].End) s = e.Attrs[e.Attrs.Count - 1].End;
                attrEdits[key] = Add(s, e.TagEnd, ">" + XmlDoc.EscapeText(text) + "</" + e.Name + ">", what);
            }
            else attrEdits[key] = Add(e.TagEnd, e.ContentEnd, XmlDoc.EscapeText(text), what);
        }

        // When the element is the only thing on its line(s): the start of its first line and the position after
        // the line break that follows it. Inserts beside it and its removal then work on whole lines, so that
        // "add before X", "add after X" and "delete X" never touch the same characters.
        bool OwnLines(XmlElem e, out int lineStart, out int afterBreak)
        {
            string t = Doc.Text;
            lineStart = afterBreak = 0;
            if (!Doc.IsFirstOnLine(e.Start)) return false;
            int p = e.End;
            while (p < t.Length && (t[p] == ' ' || t[p] == '\t')) p++;
            if (p < t.Length && t[p] == '\r') p++;
            if (p >= t.Length || t[p] != '\n') return false;
            afterBreak = p + 1;
            lineStart = e.Start;
            while (lineStart > 0 && t[lineStart - 1] != '\n') lineStart--;
            return true;
        }

        /// <summary>Removes an element, together with its line when it stands alone on it.</summary>
        public void Delete(XmlElem e)
        {
            int s, end;
            if (!OwnLines(e, out s, out end)) { s = e.Start; end = e.End; }
            deletes[e] = Add(s, end, "", "delete <" + e.Name + "> (line " + e.Line + ")");
        }

        /// <summary>Takes a Delete back. Edits inside the element that were dropped for it stay dropped.</summary>
        public void Undelete(XmlElem e)
        {
            Edit x;
            if (!deletes.TryGetValue(e, out x)) return;
            edits.Remove(x);
            deletes.Remove(e);
        }

        /// <summary>Takes back an insert; what is the value InsertAfter, InsertBefore or Append returned.</summary>
        public void Remove(object what)
        {
            var x = what as Edit;
            if (x != null) { edits.Remove(x); return; }
            var a = what as Appended;
            if (a == null) return;
            foreach (XmlElem p in new List<XmlElem>(appendOrder))
                if (appends[p].Remove(a) && appends[p].Count == 0) { appends.Remove(p); appendOrder.Remove(p); }
        }

        /// <summary>Forgets edits that lie inside the element (used when a later op deletes it).</summary>
        public void DropEditsWithin(XmlElem e)
        {
            foreach (Edit x in edits) if (x.Start >= e.Start && x.End <= e.End && !(x.Start == x.End && (x.Start == e.End || x.Start == e.Start))) x.Dropped = true;
        }

        /// <summary>Lines after the first get the indent toIndent in place of fromIndent; line ends become the document's.</summary>
        public string Reindent(string fragment, string fromIndent, string toIndent)
        {
            string[] lines = fragment.Replace("\r\n", "\n").Split('\n');
            // the indent step the fragment was written with: the shortest indent below its first line
            string[] rel = new string[lines.Length];
            string unit = null;
            for (int i = 1; i < lines.Length; i++)
            {
                string l = lines[i];
                if (!l.StartsWith(fromIndent)) continue;
                int k = fromIndent.Length;
                while (k < l.Length && (l[k] == ' ' || l[k] == '\t')) k++;
                if (k == l.Length) continue;
                rel[i] = l.Substring(fromIndent.Length, k - fromIndent.Length);
                if (rel[i].Length > 0 && (unit == null || rel[i].Length < unit.Length)) unit = rel[i];
            }
            var sb = new StringBuilder(fragment.Length + 16);
            sb.Append(lines[0]);
            for (int i = 1; i < lines.Length; i++)
            {
                sb.Append(Doc.NewLine);
                string l = lines[i];
                if (!l.StartsWith(fromIndent)) { sb.Append(l); continue; }
                sb.Append(toIndent);
                int skip = fromIndent.Length;
                // nested lines get the indent step of the document when theirs is a whole number of steps
                if (rel[i] != null && rel[i].Length > 0 && unit != Doc.IndentUnit && Doc.IndentUnit.Length > 0 && rel[i].Length % unit.Length == 0)
                {
                    int depth = rel[i].Length / unit.Length;
                    bool whole = true;
                    for (int d = 0; d < depth && whole; d++) whole = string.CompareOrdinal(rel[i], d * unit.Length, unit, 0, unit.Length) == 0;
                    if (whole)
                    {
                        for (int d = 0; d < depth; d++) sb.Append(Doc.IndentUnit);
                        skip += rel[i].Length;
                    }
                }
                sb.Append(l, skip, l.Length - skip);
            }
            return sb.ToString();
        }

        /// <summary>The indent a literal fragment was written with: the shortest indent of its lines after the first.</summary>
        public static string CommonIndent(string fragment)
        {
            string best = null;
            string[] lines = fragment.Replace("\r\n", "\n").Split('\n');
            for (int i = 1; i < lines.Length; i++)
            {
                string l = lines[i];
                int k = 0;
                while (k < l.Length && (l[k] == ' ' || l[k] == '\t')) k++;
                if (k == l.Length) continue;
                if (best == null || k < best.Length) best = l.Substring(0, k);
            }
            return best ?? "";
        }

        /// <summary>Inserts an element text as the next sibling of anchor. fromIndent is the indent the fragment has now.</summary>
        public object InsertAfter(XmlElem anchor, string fragment, string fromIndent)
        {
            string what = "add after <" + anchor.Name + "> (line " + anchor.Line + ")";
            if (!Doc.IsFirstOnLine(anchor.Start)) return Add(anchor.End, anchor.End, fragment, what);
            string ind = Doc.IndentOf(anchor.Start);
            int ls, after;
            if (OwnLines(anchor, out ls, out after)) return Add(after, after, ind + Reindent(fragment, fromIndent, ind) + Doc.NewLine, what);
            return Add(anchor.End, anchor.End, Doc.NewLine + ind + Reindent(fragment, fromIndent, ind), what);
        }

        public object InsertBefore(XmlElem anchor, string fragment, string fromIndent)
        {
            string what = "add before <" + anchor.Name + "> (line " + anchor.Line + ")";
            if (!Doc.IsFirstOnLine(anchor.Start)) return Add(anchor.Start, anchor.Start, fragment, what);
            string ind = Doc.IndentOf(anchor.Start);
            int ls, after;
            if (OwnLines(anchor, out ls, out after)) return Add(ls, ls, ind + Reindent(fragment, fromIndent, ind) + Doc.NewLine, what);
            return Add(anchor.Start, anchor.Start, Reindent(fragment, fromIndent, ind) + Doc.NewLine + ind, what);
        }

        /// <summary>Adds an element text as the last child of parent.</summary>
        public object Append(XmlElem parent, string fragment, string fromIndent)
        {
            if (parent.Children.Count > 0) return InsertAfter(parent.Children[parent.Children.Count - 1], fragment, fromIndent);
            string ind = Doc.IndentOf(parent.Start) + Doc.IndentUnit;
            List<Appended> list;
            if (!appends.TryGetValue(parent, out list)) { appends[parent] = list = new List<Appended>(); appendOrder.Add(parent); }
            var a = new Appended { Text = Reindent(fragment, fromIndent, ind), Rank = Rank };
            int at = list.Count;
            while (at > 0 && list[at - 1].Rank > a.Rank) at--;
            list.Insert(at, a);
            return a;
        }

        void FlushAppends()
        {
            string t = Doc.Text;
            int rank = Rank;
            Rank = int.MaxValue;    // after every other insert at the same place
            foreach (XmlElem p in appendOrder)
            {
                string pind = Doc.IndentOf(p.Start), ind = pind + Doc.IndentUnit;
                var sb = new StringBuilder();
                foreach (Appended f in appends[p]) sb.Append(Doc.NewLine).Append(ind).Append(f.Text);
                sb.Append(Doc.NewLine).Append(pind);
                string what = "add in <" + p.Name + "> (line " + p.Line + ")";
                if (p.SelfClosing)
                {
                    int s = p.TagEnd - 2;
                    while (s > p.NameEnd && (t[s - 1] == ' ' || t[s - 1] == '\t')) s--;
                    if (p.Attrs.Count > 0 && s < p.Attrs[p.Attrs.Count - 1].End) s = p.Attrs[p.Attrs.Count - 1].End;
                    // an insert, so that attribute edits at the same place stay in front of it
                    Add(s, p.TagEnd, ">" + sb + "</" + p.Name + ">", what);
                }
                else
                {
                    bool blank = true;
                    for (int k = p.TagEnd; k < p.ContentEnd; k++) if (" \t\r\n".IndexOf(t[k]) < 0) { blank = false; break; }
                    if (blank) Add(p.TagEnd, p.ContentEnd, sb.ToString(), what);
                    else
                    {
                        var raw = new StringBuilder();
                        foreach (Appended f in appends[p]) raw.Append(f.Text);
                        Add(p.ContentEnd, p.ContentEnd, raw.ToString(), what);
                    }
                }
            }
            appends.Clear(); appendOrder.Clear();
            Rank = rank;
        }

        /// <summary>The edited text. Throws when two edits touch the same span.</summary>
        public string Apply()
        {
            FlushAppends();
            var live = new List<Edit>();
            foreach (Edit e in edits) if (!e.Dropped) live.Add(e);
            live.Sort(delegate (Edit a, Edit b)
            {
                if (a.Start != b.Start) return a.Start.CompareTo(b.Start);
                bool ai = a.Start == a.End, bi = b.Start == b.End;
                if (ai != bi) return ai ? -1 : 1;
                if (ai && a.Rank != b.Rank) return a.Rank.CompareTo(b.Rank);
                return a.Seq.CompareTo(b.Seq);
            });
            for (int i = 1; i < live.Count; i++)
                if (live[i].Start < live[i - 1].End)
                    throw new XmlEditOverlapException(Doc.Label + ": \"" + live[i - 1].What + "\" and \"" + live[i].What + "\" change the same text");
            string t = Doc.Text;
            var sb = new StringBuilder(t.Length + 256);
            int pos = 0;
            foreach (Edit e in live)
            {
                sb.Append(t, pos, e.Start - pos);
                sb.Append(e.Text);
                pos = e.End;
            }
            sb.Append(t, pos, t.Length - pos);
            return sb.ToString();
        }

        public byte[] ApplyBytes() { return Doc.Encode(Apply()); }
    }

    public sealed class XmlEditOverlapException : ModKitException
    {
        public XmlEditOverlapException(string message) : base(message) { }
    }
}
