// A file written beside its target (name.new, name.tmp) takes the target's place in one step.
using System.IO;

namespace MS2.ModKit
{
    static class FileSwap
    {
        /// <summary>
        /// Puts the file tmp in the place of path. An existing path is replaced by File.Replace, so a stop half way
        /// leaves the old file or the new one, never only tmp (as a delete and a move could); a new path is a move.
        /// </summary>
        public static void Into(string tmp, string path)
        {
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }
}
