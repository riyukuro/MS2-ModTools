// Property values that are written differently in the game's map file (.xblock) and in the editor's (.block):
//   Color / ColorA   editor "R, G, B[, A]" as bytes     game: each byte / 255 as a 7-digit float
//   EntityRef        editor "id|name|"                  game: the id ("" in the editor = the all-zero id)
// Every other value is the same text in both files.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MS2.ModKit.MapEdit
{
    public static class MapValues
    {
        public const string ZeroGuid = "00000000-0000-0000-0000-000000000000";

        static readonly string[] ByteText = MakeTable();
        static readonly Dictionary<string, int> TextByte = MakeReverse();

        static string[] MakeTable()
        {
            var t = new string[256];
            for (int b = 0; b < 256; b++)
            {
                float f = (float)(b / 255.0);
                t[b] = f == 0 ? "0" : ((double)f).ToString("G7", CultureInfo.InvariantCulture);
            }
            return t;
        }

        static Dictionary<string, int> MakeReverse()
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int b = 0; b < 256; b++) d[ByteText[b]] = b;
            return d;
        }

        public static string ByteToFloatText(int b) { return ByteText[b]; }

        /// <summary>The byte a colour part text stands for when it is one of the 256 shipped texts, else -1.</summary>
        public static int ByteOfFloatText(string t) { int b; return TextByte.TryGetValue(t, out b) ? b : -1; }

        static List<string> Tokens(string value)
        {
            var res = new List<string>();
            int s = -1;
            for (int i = 0; i <= value.Length; i++)
            {
                bool sep = i == value.Length || value[i] == ',' || char.IsWhiteSpace(value[i]);
                if (sep) { if (s >= 0) res.Add(value.Substring(s, i - s)); s = -1; }
                else if (s < 0) s = i;
            }
            return res;
        }

        /// <summary>Game form to editor form. exact is false when a number is not a byte / 255 (it is rounded) or the
        /// value does not have n numbers (it is left as it is).</summary>
        public static string ColorToBlock(string value, int n, out bool exact)
        {
            exact = false;
            List<string> toks = Tokens(value);
            if (toks.Count != n) return value;
            exact = true;
            var sb = new StringBuilder();
            foreach (string t in toks)
            {
                int b;
                if (!TextByte.TryGetValue(t, out b))
                {
                    double d;
                    if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d) || double.IsNaN(d) || double.IsInfinity(d)) { exact = false; return value; }
                    double r = Math.Round(d * 255.0);
                    b = r < 0 ? 0 : r > 255 ? 255 : (int)r;
                    exact = false;
                }
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(b.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public static string ColorToXBlock(string value, int n)
        {
            List<string> toks = Tokens(value);
            if (toks.Count != n) return value;
            var sb = new StringBuilder();
            foreach (string t in toks)
            {
                int b;
                if (!int.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out b) || b < -256 || b > 255)
                    throw new ModKitException("colour value \"" + value + "\": " + t + " is not a byte");
                if (b < 0) b += 256;        // as the script this replaces: a negative number counts from the end
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(ByteText[b]);
            }
            return sb.ToString();
        }

        /// <summary>namesById: dashed id -> entity name. dangling: the id is not an entity of the map.</summary>
        public static string EntityRefToBlock(string guid, IDictionary<string, string> namesById, out bool dangling)
        {
            dangling = false;
            string g = guid.Trim().ToLowerInvariant();
            if (g == ZeroGuid) return "";
            string name;
            if (!namesById.TryGetValue(g, out name)) { dangling = true; return g + "|<unresolved>|"; }
            return g + "|" + name + "|";
        }

        public static string EntityRefToXBlock(string value)
        {
            if (string.IsNullOrEmpty(value)) return ZeroGuid;
            int bar = value.IndexOf('|');
            return bar < 0 ? value : value.Substring(0, bar);
        }

        // ---- ids ----

        static bool IsHex32(string s)
        {
            if (s == null || s.Length != 32) return false;
            foreach (char c in s) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            return true;
        }

        /// <summary>32 hex digits (dashes, braces and "urn:uuid:" allowed) to the lower-case dashed form.</summary>
        public static string Dashed(string id)
        {
            string h = (id ?? "").Replace("urn:", "").Replace("uuid:", "").Trim('{', '}').Replace("-", "");
            if (!IsHex32(h)) throw new ModKitException("\"" + id + "\" is not an entity id (32 hex digits)");
            h = h.ToLowerInvariant();
            return h.Substring(0, 8) + "-" + h.Substring(8, 4) + "-" + h.Substring(12, 4) + "-" + h.Substring(16, 4) + "-" + h.Substring(20);
        }

        public static string Undashed(string id) { return id.Replace("-", "").ToLowerInvariant(); }

        static string Hex(byte[] b, int n)
        {
            var sb = new StringBuilder(n * 2);
            for (int i = 0; i < n; i++) sb.Append(b[i].ToString("x2"));
            return sb.ToString();
        }

        /// <summary>The name-based id (version 5, URL namespace) of a text, dashed.</summary>
        public static string UrlUuid5(string name)
        {
            return Uuid5(new byte[] { 0x6b, 0xa7, 0xb8, 0x11, 0x9d, 0xad, 0x11, 0xd1, 0x80, 0xb4, 0x00, 0xc0, 0x4f, 0xd4, 0x30, 0xc8 }, name);
        }

        /// <summary>The name-based id (version 5) of a text in a namespace of 16 bytes, dashed.</summary>
        public static string Uuid5(byte[] ns, string name)
        {
            byte[] nm = new UTF8Encoding(false).GetBytes(name);
            var all = new byte[ns.Length + nm.Length];
            Buffer.BlockCopy(ns, 0, all, 0, ns.Length);
            Buffer.BlockCopy(nm, 0, all, ns.Length, nm.Length);
            byte[] h;
            using (SHA1 sha = SHA1.Create()) h = sha.ComputeHash(all);
            h[6] = (byte)((h[6] & 0x0f) | 0x50);
            h[8] = (byte)((h[8] & 0x3f) | 0x80);
            return Dashed(Hex(h, 16));
        }

        /// <summary>The id of an entity a mod adds: the same on every target and at every apply.</summary>
        public static string NewEntityId(string modId, string xblock, string opName)
        {
            byte[] src = new UTF8Encoding(false).GetBytes("ms2-modkit map entity\n" + modId + "\n" + xblock.ToLowerInvariant() + "\n" + opName);
            byte[] h;
            using (SHA1 sha = SHA1.Create()) h = sha.ComputeHash(src);
            return Hex(h, 16);
        }
    }
}
