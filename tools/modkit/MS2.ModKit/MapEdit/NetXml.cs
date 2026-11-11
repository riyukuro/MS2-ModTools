// XML text the way the .NET XmlWriter of the map tools writes it: BOM, declaration, CRLF, indent, self-closed empty
// elements, no line end after the last tag. Two forms are in use: one-line attributes with a tab indent, and one
// attribute per line with a two-space indent.
using System;
using System.Collections.Generic;
using System.Text;

namespace MS2.ModKit.MapEdit
{
    public sealed class NetXmlWriter
    {
        public const string NewLine = "\r\n";
        public const string Head = "﻿<?xml version=\"1.0\" encoding=\"utf-8\"?>";

        sealed class Open { internal string Name; internal bool Children, Text, Pending; }

        readonly StringBuilder sb;
        readonly string indent;
        readonly bool attrLines;
        readonly List<Open> stack = new List<Open>();

        public NetXmlWriter(string indent, bool attributesOnOwnLines) : this(indent, attributesOnOwnLines, true) { }

        /// <summary>head false: no BOM and no declaration (a piece that goes into an existing file).</summary>
        public NetXmlWriter(string indent, bool attributesOnOwnLines, bool head)
        {
            this.indent = indent; attrLines = attributesOnOwnLines;
            sb = new StringBuilder(1 << 16);
            if (head) sb.Append(Head);
        }

        public static string EscapeAttr(string s)
        {
            int i = 0;
            for (; i < s.Length; i++) { char c = s[i]; if (c == '&' || c == '<' || c == '>' || c == '"' || c < 0x20) break; }
            if (i == s.Length) return s;
            var b = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '&': b.Append("&amp;"); break;
                    case '<': b.Append("&lt;"); break;
                    case '>': b.Append("&gt;"); break;
                    case '"': b.Append("&quot;"); break;
                    default:
                        if (c < 0x20) b.Append("&#x").Append(((int)c).ToString("X")).Append(';');
                        else b.Append(c);
                        break;
                }
            }
            return b.ToString();
        }

        public static string EscapeText(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\r", "&#xD;");
        }

        void ClosePending()
        {
            if (stack.Count > 0 && stack[stack.Count - 1].Pending) { sb.Append('>'); stack[stack.Count - 1].Pending = false; }
        }

        void Indent(int depth) { for (int i = 0; i < depth; i++) sb.Append(indent); }

        /// <summary>attrs: name, value, name, value, ...</summary>
        public void Start(string name, params string[] attrs)
        {
            ClosePending();
            if (stack.Count > 0) stack[stack.Count - 1].Children = true;
            int depth = stack.Count;
            sb.Append(NewLine); Indent(depth); sb.Append('<').Append(name);
            for (int i = 0; i + 1 < attrs.Length; i += 2)
            {
                if (attrs[i + 1] == null) throw new ModKitException("<" + name + "> has no value for " + attrs[i]);
                if (attrLines) { sb.Append(NewLine); Indent(depth + 1); } else sb.Append(' ');
                sb.Append(attrs[i]).Append("=\"").Append(EscapeAttr(attrs[i + 1])).Append('"');
            }
            stack.Add(new Open { Name = name, Pending = true });
        }

        public void Text(string s)
        {
            ClosePending();
            stack[stack.Count - 1].Text = true;
            sb.Append(EscapeText(s));
        }

        public void End()
        {
            Open o = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            if (o.Pending) sb.Append(" />");
            else if (o.Children && !o.Text) { sb.Append(NewLine); Indent(stack.Count); sb.Append("</").Append(o.Name).Append('>'); }
            else sb.Append("</").Append(o.Name).Append('>');
        }

        public void Empty(string name, params string[] attrs) { Start(name, attrs); End(); }

        public void Leaf(string name, string text, params string[] attrs) { Start(name, attrs); Text(text); End(); }

        public string Finish()
        {
            if (stack.Count > 0) throw new InvalidOperationException("unclosed <" + stack[stack.Count - 1].Name + ">");
            return sb.ToString();
        }

        /// <summary>The text so far, open elements left open (for cutting a piece out).</summary>
        internal string Finish0() { return sb.ToString(); }

        public static byte[] Bytes(string text) { return new UTF8Encoding(false).GetBytes(text); }
    }

    internal static class Stable
    {
        /// <summary>List.Sort is not stable; the map files need equal keys to keep their order.</summary>
        internal static void Sort<T>(List<T> list, Comparison<T> cmp)
        {
            var idx = new int[list.Count];
            for (int i = 0; i < idx.Length; i++) idx[i] = i;
            T[] items = list.ToArray();
            Array.Sort(idx, delegate (int a, int b) { int c = cmp(items[a], items[b]); return c != 0 ? c : a.CompareTo(b); });
            for (int i = 0; i < idx.Length; i++) list[i] = items[idx[i]];
        }
    }
}
