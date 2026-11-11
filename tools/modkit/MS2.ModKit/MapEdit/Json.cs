// A small JSON reader and the writer of the model index file (the library has no JSON dependency on .NET 3.5).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MS2.ModKit.MapEdit
{
    /// <summary>Values: Dictionary&lt;string, object&gt; (JsonObj keeps the key order), List&lt;object&gt;, string, decimal, bool, null.</summary>
    internal sealed class JsonObj
    {
        internal readonly List<string> Keys = new List<string>();
        internal readonly Dictionary<string, object> Map = new Dictionary<string, object>(StringComparer.Ordinal);

        internal void Set(string k, object v) { if (!Map.ContainsKey(k)) Keys.Add(k); Map[k] = v; }
        internal object Get(string k) { object v; return Map.TryGetValue(k, out v) ? v : null; }
    }

    internal static class Json
    {
        internal static object Parse(string text, string label)
        {
            int i = 0;
            object v = Value(text, ref i, label);
            Ws(text, ref i);
            if (i != text.Length) throw Bad(label, i, "text after the value");
            return v;
        }

        static ModKitException Bad(string label, int pos, string what) { return new ModKitException(label + ": not valid JSON at character " + pos + ": " + what); }

        static void Ws(string t, ref int i) { while (i < t.Length && (t[i] == ' ' || t[i] == '\t' || t[i] == '\r' || t[i] == '\n')) i++; }

        static object Value(string t, ref int i, string label)
        {
            Ws(t, ref i);
            if (i >= t.Length) throw Bad(label, i, "unexpected end");
            char c = t[i];
            if (c == '{')
            {
                var o = new JsonObj();
                i++; Ws(t, ref i);
                if (i < t.Length && t[i] == '}') { i++; return o; }
                for (; ; )
                {
                    Ws(t, ref i);
                    if (i >= t.Length || t[i] != '"') throw Bad(label, i, "a key was expected");
                    string k = Str(t, ref i, label);
                    Ws(t, ref i);
                    if (i >= t.Length || t[i] != ':') throw Bad(label, i, "':' was expected");
                    i++;
                    o.Set(k, Value(t, ref i, label));
                    Ws(t, ref i);
                    if (i < t.Length && t[i] == ',') { i++; continue; }
                    if (i < t.Length && t[i] == '}') { i++; return o; }
                    throw Bad(label, i, "',' or '}' was expected");
                }
            }
            if (c == '[')
            {
                var l = new List<object>();
                i++; Ws(t, ref i);
                if (i < t.Length && t[i] == ']') { i++; return l; }
                for (; ; )
                {
                    l.Add(Value(t, ref i, label));
                    Ws(t, ref i);
                    if (i < t.Length && t[i] == ',') { i++; continue; }
                    if (i < t.Length && t[i] == ']') { i++; return l; }
                    throw Bad(label, i, "',' or ']' was expected");
                }
            }
            if (c == '"') return Str(t, ref i, label);
            if (string.CompareOrdinal(t, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(t, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(t, i, "null", 0, 4) == 0) { i += 4; return null; }
            int s = i;
            while (i < t.Length && (char.IsDigit(t[i]) || t[i] == '-' || t[i] == '+' || t[i] == '.' || t[i] == 'e' || t[i] == 'E')) i++;
            decimal d;
            if (i == s || !decimal.TryParse(t.Substring(s, i - s), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) throw Bad(label, s, "a value was expected");
            return d;
        }

        static string Str(string t, ref int i, string label)
        {
            var sb = new StringBuilder();
            i++;
            for (; ; )
            {
                if (i >= t.Length) throw Bad(label, i, "unterminated string");
                char c = t[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= t.Length) throw Bad(label, i, "unterminated string");
                c = t[i++];
                switch (c)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > t.Length) throw Bad(label, i, "short \\u escape");
                        sb.Append((char)int.Parse(t.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4; break;
                    default: sb.Append(c); break;
                }
            }
        }

        /// <summary>A string as Python's json module writes it by default (ASCII only).</summary>
        internal static void Quote(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
