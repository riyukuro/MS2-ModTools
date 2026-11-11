// The one place that turns a name read from a file (a project file's File path, a model's name, a folder name of
// the model listing, an archive entry, an asset path, a map or layer name) into a path that is written. A name that
// could leave the output folder is refused: the caller lists it and does not write that file.
using System;
using System.Collections.Generic;
using System.IO;

namespace MS2.ModKit.MapEdit
{
    public static class OutPath
    {
        /// <summary>Why a relative name is not safe to write, or null when it is. Both '/' and '\' separate parts on
        /// every system. Refused: an empty name, a name that starts with a separator, any ':' (a drive letter, or a
        /// second stream of a file), a ".." part (also written with trailing dots or spaces, which Windows drops),
        /// and control characters.</summary>
        public static string Why(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return "empty name";
            if (rel[0] == '/' || rel[0] == '\\') return "rooted name";
            bool any = false;
            foreach (char c in rel)
            {
                if (c == ':') return "drive letter or ':' in the name";
                if (c < 0x20) return "control character in the name";
            }
            foreach (string part in Parts(rel))
            {
                if (part.Length == 0 || part == ".") continue;
                if (part.Trim(' ', '.').Length == 0) return "'..' part";
                any = true;
            }
            return any ? null : "empty name";
        }

        static string[] Parts(string rel) { return rel.Split('/', '\\'); }

        static string WithSep(string dir)
        {
            return Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }

        /// <summary>Why a finished path is not inside root (after both are made full paths), or null when it is.</summary>
        public static string WhyNotUnder(string root, string path)
        {
            string r = WithSep(root), p;
            try { p = Path.GetFullPath(path); }
            catch (Exception e) { if (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) return "not a usable path"; throw; }
            return p.StartsWith(r, StringComparison.Ordinal) && p.Length > r.Length ? null : "outside the output folder";
        }

        /// <summary>The path of rel below baseDir, which is root or a folder inside it. False (and why) when the name
        /// is unsafe or the result is not inside root.</summary>
        public static bool TryUnder(string root, string baseDir, string rel, out string full, out string why)
        {
            full = null;
            why = Why(rel);
            if (why != null) return false;
            string p = baseDir;
            foreach (string part in Parts(rel))
                if (part.Length != 0 && part != ".") p = Path.Combine(p, part);
            why = WhyNotUnder(root, p);
            if (why != null) return false;
            full = Path.GetFullPath(p);
            return true;
        }

        /// <summary>The same for callers that cannot go on without the file.</summary>
        public static string Under(string root, string baseDir, string rel)
        {
            string full, why;
            if (!TryUnder(root, baseDir, rel, out full, out why)) throw new ModKitException("refused to write " + Show(rel) + ": " + why);
            return full;
        }

        public static string Under(string root, string rel) { return Under(root, root, rel); }

        /// <summary>The line for the list of refused names.</summary>
        public static string Line(string what, string rel, string why) { return what + " " + Show(rel) + ": " + why; }

        static string Show(string rel)
        {
            if (rel == null) return "(none)";
            var sb = new System.Text.StringBuilder("\"");
            foreach (char c in rel) sb.Append(c < 0x20 ? '?' : c);
            return sb.Append('"').ToString();
        }
    }
}
