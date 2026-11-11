// Help for a line of schema-notes.xml: where a source tree of the client reads an attribute of a table. The shipped
// data shows most attributes; for one that no shipped row sets, the loader is the evidence, and this finds the lines
// to look at: the attribute name as a string literal, first in the files that also name the table's file. It reads
// the user's own source tree and writes nothing; the note line it drafts carries names and a path only.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MS2.ModKit
{
    public sealed class CiteHit
    {
        /// <summary>Path below the source folder, with '/'.</summary>
        public string File;
        public int Line;
        public string Text;
        /// <summary>The file also names the table's file (or, for a table of numbered files, its folder).</summary>
        public bool NamesTable;

        public string Where { get { return File + ":" + Line; } }
    }

    public static class SchemaCite
    {
        static readonly string[] Kinds = { ".cpp", ".h", ".hpp", ".inl", ".c", ".cc" };
        const int MaxBytes = 8 << 20;

        /// <summary>
        /// The word of a table name that a loader's source names: the file name ("pet.xml" of table/pet.xml), or
        /// for a table of numbered files (npc/#.xml) the folder with its slash ("npc/").
        /// </summary>
        public static string TableWord(string table)
        {
            string t = (table ?? "").Replace('\\', '/').Trim('/');
            int cut = t.LastIndexOf('/');
            string file = t.Substring(cut + 1);
            if (file.IndexOf('#') < 0) return file;
            if (cut < 0) return "";
            string dir = t.Substring(0, cut);
            while (dir.Length > 0)
            {
                int c = dir.LastIndexOf('/');
                string last = dir.Substring(c + 1);
                if (last.IndexOf('#') < 0) return last + "/";
                dir = c < 0 ? "" : dir.Substring(0, c);
            }
            return "";
        }

        /// <summary>
        /// The lines of the source tree that hold the attribute name as a string literal. Files that name the table
        /// come first; within each group the order is by path, then line. At most <paramref name="max"/> lines.
        /// </summary>
        public static List<CiteHit> Find(string srcDir, string table, string attr, int max)
        {
            if (!Directory.Exists(srcDir)) throw new ModKitException("no such source folder: " + srcDir);
            if (string.IsNullOrEmpty(attr)) throw new ModKitException("an attribute name is needed");
            string root = Path.GetFullPath(srcDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string word = TableWord(table), literal = "\"" + attr + "\"";
            var files = new List<string>();
            foreach (string f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                if (Array.IndexOf(Kinds, Path.GetExtension(f).ToLowerInvariant()) >= 0) files.Add(f);
            files.Sort(StringComparer.Ordinal);
            var named = new List<CiteHit>(); var rest = new List<CiteHit>();
            foreach (string f in files)
            {
                if (new FileInfo(f).Length > MaxBytes) continue;
                string text = File.ReadAllText(f, Encoding.UTF8);
                if (text.IndexOf(literal, StringComparison.Ordinal) < 0) continue;
                bool names = word.Length > 0 && text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
                string rel = f.Substring(root.Length).Replace(Path.DirectorySeparatorChar, '/');
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                    if (lines[i].IndexOf(literal, StringComparison.Ordinal) >= 0)
                        (names ? named : rest).Add(new CiteHit { File = rel, Line = i + 1, Text = lines[i].Trim(), NamesTable = names });
            }
            named.AddRange(rest);
            if (max > 0 && named.Count > max) named.RemoveRange(max, named.Count - max);
            return named;
        }

        /// <summary>A line for schema-notes.xml that cites the hit. The user checks the source line before using it.</summary>
        public static string NoteLine(string archive, string table, string element, string attr, CiteHit hit)
        {
            var sb = new StringBuilder("<attr");
            if (!string.IsNullOrEmpty(archive) && archive != "*") sb.Append(" archive=\"").Append(Esc(archive)).Append('"');
            sb.Append(" table=\"").Append(Esc(table)).Append("\" element=\"").Append(Esc(element)).Append("\" name=\"").Append(Esc(attr))
              .Append("\" source=\"client\" ref=\"").Append(Esc(hit.Where)).Append("\"/>");
            return sb.ToString();
        }

        static string Esc(string s) { return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace("\"", "&quot;"); }
    }
}
