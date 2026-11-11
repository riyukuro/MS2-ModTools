// A project folder the map editor can open, built from a game's own files: the model library with rebuilt model and
// preset files, the preset project files, the chosen maps as editor maps, the solution file, and two lists (what the
// game does not ship, which asset files the maps use). The folder holds copied game files, so it is a local working
// folder and never part of a repository. The outputs are the same bytes as tools/mapedit/flat2model.py, mklayout.py
// and tools/toolbench/mkgsl.py give.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using MS2.ModKit.M2d;

namespace MS2.ModKit.MapEdit
{
    /// <summary>Finds a path whose parts may differ in letter case from what is on disk (the library archive has
    /// lower-case names, the project files name the same files in mixed case).</summary>
    public sealed class CasePaths
    {
        readonly Dictionary<string, Dictionary<string, string>> dirs = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        Dictionary<string, string> Entries(string dir)
        {
            Dictionary<string, string> d;
            if (dirs.TryGetValue(dir, out d)) return d;
            d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Directory.Exists(dir))
            {
                // sorted, so that of two names that differ only in case the same one is found on every run
                var names = new List<string>();
                foreach (string e in Directory.GetFileSystemEntries(dir)) names.Add(Path.GetFileName(e));
                names.Sort(StringComparer.Ordinal);
                foreach (string n in names)
                {
                    d["=" + n] = n;
                    string low = "~" + n.ToLowerInvariant();
                    if (!d.ContainsKey(low)) d[low] = n;
                }
            }
            dirs[dir] = d;
            return d;
        }

        /// <summary>rel: parts joined by '/' or '\'. create: the caller is about to make the path, so parts that are
        /// not there yet are remembered.</summary>
        public string Resolve(string baseDir, string rel, bool create)
        {
            string cur = baseDir;
            foreach (string comp in rel.Split('/', '\\'))
            {
                if (comp.Length == 0 || comp == ".") continue;
                if (comp == "..") { cur = Path.GetDirectoryName(cur); continue; }
                Dictionary<string, string> d = Entries(cur);
                string real;
                if (d.TryGetValue("=" + comp, out real) || d.TryGetValue("~" + comp.ToLowerInvariant(), out real)) cur = Path.Combine(cur, real);
                else
                {
                    if (create) { d["=" + comp] = comp; d["~" + comp.ToLowerInvariant()] = comp; }
                    cur = Path.Combine(cur, comp);
                }
            }
            return cur;
        }

        public bool Exists(string baseDir, string rel)
        {
            string p = Resolve(baseDir, rel, false);
            return File.Exists(p) || Directory.Exists(p);
        }
    }

    public sealed class WbBuildResult
    {
        public readonly List<KeyValuePair<string, int>> Stats = new List<KeyValuePair<string, int>>();
        public List<string> Warnings = new List<string>();
        public readonly List<string> Log = new List<string>();
        /// <summary>Names that were not written because they could leave the output folder (see OutPath).</summary>
        public readonly List<string> Refused = new List<string>();

        internal void Count(string key)
        {
            for (int i = 0; i < Stats.Count; i++)
                if (Stats[i].Key == key) { Stats[i] = new KeyValuePair<string, int>(key, Stats[i].Value + 1); return; }
            Stats.Add(new KeyValuePair<string, int>(key, 1));
        }

        public int Stat(string key) { foreach (KeyValuePair<string, int> s in Stats) if (s.Key == key) return s.Value; return 0; }
    }

    public sealed class WbProjectOptions
    {
        /// <summary>The folder to build. Its Library folder is made from LibrarySource when it is not there yet.</summary>
        public string Root;
        /// <summary>The model library: a folder, or the Library archive (.m2d).</summary>
        public string LibrarySource;
        /// <summary>Model name to id for the models whose real id is known; may be null.</summary>
        public IDictionary<string, string> KnownIds;
        /// <summary>Map name to the bytes of the game map.</summary>
        public List<KeyValuePair<string, byte[]>> Maps = new List<KeyValuePair<string, byte[]>>();
        /// <summary>false: only the presets the maps use (and the presets those are made from).</summary>
        public bool AllPresets;
        public bool LayersByCategory, FoldersByCategory;
        /// <summary>null: the root folder's name + ".gsl".</summary>
        public string SolutionName;
        /// <summary>Asset id (without "urn:llid:") to the file path in the game's asset list; may be null.</summary>
        public IDictionary<string, string> AssetPaths;
        /// <summary>Every path of the asset list, also those without an id (textures are found by file name here).
        /// Null: the paths of AssetPaths.</summary>
        public IList<string> AssetFiles;
        /// <summary>The game's Resource folder. When set (and AssetPaths too), the model files the maps use and the
        /// textures those name are copied into the project.</summary>
        public string ResourceDir;
        /// <summary>Filled by Build: names that were not written because they could leave the project folder. A
        /// caller that finds any must report failure.</summary>
        public readonly List<string> Refused = new List<string>();
    }

    public static class WbProject
    {
        const string Setting = "Emergent.Toolbench.EntityModeling.";
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        /// <summary>The text of a model project file (with its byte order mark as the first character).</summary>
        public static string ProjectText(IList<string> files, IList<string> refs, string flatOut, string modelsOut)
        {
            var w = new NetXmlWriter("  ", true);
            w.Start("Project", "version", "5");
            w.Start("tool-settings", "version", "1");
            string[,] st = { { "BehaviorScriptsOutput", "" }, { "DiagramsOutput", "" }, { "EnumsOutput", "..\\..\\Enums" },
                             { "FlatModelsOutput", flatOut }, { "ModelsAndPresetsOutput", modelsOut } };
            for (int i = 0; i < st.GetLength(0); i++)
            {
                w.Start("setting", "id", Setting + st[i, 0], "property-type", "String");
                w.Leaf("value", st[i, 1]);
                w.End();
            }
            w.End();
            w.Start("Files");
            foreach (string f in files) w.Empty("File", "path", f);
            w.End();
            w.Start("References");
            foreach (string r in refs) w.Empty("Project", "path", r);
            w.End();
            w.Empty("virtual-folders", "version", "1");
            w.End();
            return w.Finish();
        }

        /// <summary>Adds one file to the Files list of a project file text, in the layout the text already has.</summary>
        public static string AddFileEntry(string text, string rel)
        {
            string nl = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            Match m = Regex.Match(text, "^([ \\t]*)</Files>", RegexOptions.Multiline);
            if (!m.Success) throw new ModKitException("the project file has no closing Files tag");
            string ind = m.Groups[1].Value, entry;
            rel = NetXmlWriter.EscapeAttr(rel);
            if (text.IndexOf("<File path=\"", StringComparison.Ordinal) >= 0)
            {
                Match mi = Regex.Match(text, "^([ \\t]*)<File path=\"", RegexOptions.Multiline);
                entry = (mi.Success ? mi.Groups[1].Value : ind + "  ") + "<File path=\"" + rel + "\" />" + nl;
            }
            else
            {
                Match mi = Regex.Match(text, "^([ \\t]*)<File[ \\t\\r]*$", RegexOptions.Multiline);
                string i1 = mi.Success ? mi.Groups[1].Value : ind + "  ";
                entry = i1 + "<File" + nl + i1 + "  path=\"" + rel + "\" />" + nl;
            }
            return text.Substring(0, m.Index) + entry + text.Substring(m.Index);
        }

        static string ReadText(string path)
        {
            string t = Utf8.GetString(File.ReadAllBytes(path));
            if (t.Length > 0 && t[0] == '\uFEFF') t = t.Substring(1);
            return t.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        /// <summary>The path to write rel at below baseDir (a folder inside root), with the letter case of what is
        /// on disk; null, and a line in refused, when the name is unsafe or the path is not inside root.</summary>
        static string SafePath(CasePaths cp, string root, string baseDir, string rel, string what, List<string> refused)
        {
            string why = OutPath.Why(rel);
            if (why == null)
            {
                string path = cp.Resolve(baseDir, rel, false);
                why = OutPath.WhyNotUnder(root, path);
                if (why == null) return cp.Resolve(baseDir, rel, true);
            }
            refused.Add(OutPath.Line(what, rel, why));
            return null;
        }

        static void WriteModel(string path, WbModel m)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, Utf8.GetBytes(m.Text()));
        }

        static string BaseName(string relFile)
        {
            string n = relFile.Replace('\\', '/');
            n = n.Substring(n.LastIndexOf('/') + 1);
            if (n.EndsWith(".model", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 6);
            else if (n.EndsWith(".preset", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 7);
            return n;
        }

        static string FlatOutName(string cat)
        {
            var sb = new StringBuilder(cat);
            for (int i = 0; i < sb.Length; i++)
                if ((i == 0 || sb[i - 1] == ' ') && (char.IsLetterOrDigit(sb[i]) || sb[i] == '_')) sb[i] = char.ToUpperInvariant(sb[i]);
            return sb.ToString();
        }

        /// <summary>The presets a set of models needs: the presets among them and the presets those are made from.</summary>
        public static Dictionary<string, bool> PresetClosure(WbFlats flats, IEnumerable<string> models)
        {
            var res = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (string m in models)
            {
                string n = m;
                while (n != null && !res.ContainsKey(n))
                {
                    WbFlat f = flats.Get(n);
                    if (f == null || !f.IsPreset) break;
                    res[n] = true;
                    n = f.Mixins.Count > 0 ? f.Mixins[0] : null;
                }
            }
            return res;
        }

        /// <summary>Writes the model files of every library project and the preset files and preset project files
        /// into root/Library, which must hold the game's model library. presets: null for all, else the ones to write
        /// (a preset folder with none of them gets no project file).</summary>
        public static WbBuildResult BuildModels(string root, WbFlats flats, IDictionary<string, string> known, IDictionary<string, bool> presets)
        {
            var res = new WbBuildResult();
            var rec = new WbRecon(flats, known);
            var cp = new CasePaths();
            string lib = Path.Combine(root, "Library");
            if (!Directory.Exists(lib)) throw new ModKitException(root + " has no Library folder");
            var libOfName = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (string libdir in MapConvert.Libraries)
            {
                string rel = MapConvert.LibraryProject(libdir);
                string ep = Path.Combine(lib, rel.Replace('\\', Path.DirectorySeparatorChar));
                if (!File.Exists(ep)) continue;
                string epDir = Path.GetDirectoryName(ep);
                string text = ReadText(ep);
                var listed = new SortedDictionary<string, string[]>(StringComparer.Ordinal);       // lower name -> name, file
                foreach (Match m in Regex.Matches(text, "<File\\s+path=\"([^\"]+\\.(?:model|preset))\"", RegexOptions.IgnoreCase))
                {
                    string relF = Unescape(m.Groups[1].Value), nm = BaseName(relF);
                    listed[nm.ToLowerInvariant()] = new[] { nm, relF };
                }
                var byLower = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (string n in flats.Names)
                {
                    WbFlat f = flats.Get(n);
                    if (f.Kind == "library" && f.Cat == libdir) byLower[n.ToLowerInvariant()] = n;
                }
                bool changed = false;
                foreach (KeyValuePair<string, string[]> l in listed)
                {
                    string fname;
                    byLower.TryGetValue(l.Key, out fname);
                    libOfName[fname ?? l.Value[0]] = libdir;
                    if (fname == null) { res.Count("listed-without-flat"); continue; }
                    string relF = l.Value[1];
                    if (relF.ToLowerInvariant().EndsWith(".preset", StringComparison.Ordinal))
                    {
                        if (OutPath.Why(relF) == null && cp.Exists(epDir, relF)) { res.Count("library-preset-kept(shipped)"); continue; }
                        string path = SafePath(cp, root, epDir, relF, rel + " lists", res.Refused);
                        if (path == null) continue;
                        WriteModel(path, rec.BuildPreset(fname));
                        res.Count("library-preset-generated");
                    }
                    else
                    {
                        string path = SafePath(cp, root, epDir, relF, rel + " lists", res.Refused);
                        if (path == null) continue;
                        WriteModel(path, rec.Build(fname));
                        res.Count("library-model-generated");
                    }
                }
                foreach (KeyValuePair<string, string> b in byLower)
                {
                    libOfName[b.Value] = libdir;
                    if (listed.ContainsKey(b.Key)) continue;
                    string relF = b.Value + ".model";
                    string path = b.Value.IndexOfAny(new[] { '/', '\\' }) >= 0 ? null : SafePath(cp, root, epDir, relF, "model name in " + flats.Get(b.Value).File + ":", res.Refused);
                    if (path == null)
                    {
                        if (b.Value.IndexOfAny(new[] { '/', '\\' }) >= 0) res.Refused.Add(OutPath.Line("model name in " + flats.Get(b.Value).File + ":", b.Value, "more than one part"));
                        continue;
                    }
                    WriteModel(path, rec.Build(b.Value));
                    text = AddFileEntry(text, relF);
                    changed = true;
                    res.Count("library-model-generated-and-added-to-project");
                    res.Log.Add(libdir + ": added " + relF + " to " + Path.GetFileName(ep));
                }
                if (changed) File.WriteAllBytes(ep, Utf8.GetBytes("\uFEFF" + text));
            }

            var cats = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            var presetLabel = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string n in flats.Names)
            {
                WbFlat f = flats.Get(n);
                if (!f.IsPreset) continue;
                List<string> l;
                if (!cats.TryGetValue(f.Cat, out l)) cats[f.Cat] = l = new List<string>();
                l.Add(n);
                presetLabel[n] = MapConvert.CategoryLabel(new[] { "presets", f.Cat });
            }
            string pdir = Path.Combine(lib, "Presets");
            foreach (KeyValuePair<string, List<string>> c in cats)
            {
                string label = MapConvert.CategoryLabel(new[] { "presets", c.Key });
                int us = label.IndexOf('_');
                string sub = us >= 0 ? label.Substring(us + 1) : label;
                var names = new List<string>();
                foreach (string n in c.Value) if (presets == null || presets.ContainsKey(n)) names.Add(n);
                if (names.Count == 0 && presets != null) continue;
                Stable.Sort(names, delegate (string a, string b) { return string.CompareOrdinal(a.ToLowerInvariant(), b.ToLowerInvariant()); });
                var files = new List<string>();
                var refs = new SortedDictionary<string, bool>(StringComparer.Ordinal);
                // the folder name of the listing becomes a folder and a project file name: one part, or nothing is written
                string projPath, bad = sub.IndexOfAny(new[] { '/', '\\' }) >= 0 ? "more than one part" : OutPath.Why(sub);
                if (bad == null) OutPath.TryUnder(root, pdir, label + ".emtproj", out projPath, out bad); else projPath = null;
                if (bad != null) { res.Refused.Add(OutPath.Line("preset folder", c.Key, bad)); continue; }
                foreach (string n in names)
                {
                    if (n.IndexOfAny(new[] { '/', '\\' }) >= 0) { res.Refused.Add(OutPath.Line("model name in " + flats.Get(n).File + ":", n, "more than one part")); continue; }
                    string path = SafePath(cp, root, pdir, sub + "/" + n + ".preset", "model name in " + flats.Get(n).File + ":", res.Refused);
                    if (path == null) continue;
                    WbModel m = rec.BuildPreset(n);
                    WriteModel(path, m);
                    files.Add(sub + "\\" + n + ".preset");
                    string bas = m.Mixins.Count > 0 ? m.Mixins[0].Value : null;
                    string ld = null, bl;
                    if (bas != null) libOfName.TryGetValue(bas, out ld);
                    if (bas != null && presetLabel.TryGetValue(bas, out bl))
                    {
                        if (bl != label) refs[bl + ".emtproj"] = true;
                    }
                    else if (ld != null) refs["..\\" + MapConvert.LibraryProject(ld)] = true;
                    else res.Count("preset-base-without-library");
                    res.Count("preset-generated");
                }
                Directory.CreateDirectory(pdir);
                File.WriteAllBytes(projPath,
                    Utf8.GetBytes(ProjectText(files, new List<string>(refs.Keys), "..\\..\\Exported\\Flat\\Presets\\" + FlatOutName(c.Key), sub)));
                res.Count("preset-projects");
                res.Log.Add(label + ": " + names.Count + " presets, " + refs.Count + " references");
            }
            res.Warnings = rec.Warnings;
            if (rec.Warnings.Count > 0)
            {
                Directory.CreateDirectory(Path.Combine(root, "logs"));
                File.WriteAllBytes(OutPath.Under(root, "logs/flat2model-warnings.txt"), Utf8.GetBytes(string.Join("\n", rec.Warnings.ToArray()) + "\n"));
            }
            return res;
        }

        static readonly byte[] SolutionSpace = { 0x6f, 0x1b, 0x8c, 0x6e, 0x9b, 0x1e, 0x4c, 0x0f, 0x9a, 0x59, 0x6d, 0x53, 0x32, 0x00, 0x00, 0x01 };

        static void WalkItems(string root, string dir, string rel, List<string[]> items)
        {
            string[] files = Directory.GetFiles(dir);
            var names = new List<string>();
            foreach (string f in files) names.Add(Path.GetFileName(f));
            names.Sort(StringComparer.Ordinal);
            foreach (string f in names)
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                string type = ext == ".emtproj" ? "Emergent.Toolbench.EntityModeling.EntityModelingProject"
                    : ext == ".block" ? "Emergent.Toolbench.WorldBuilder.BlockDocument" : null;
                if (type == null) continue;
                string r = rel.Length == 0 ? f : rel + "\\" + f;
                items.Add(new[] { type, f, MapValues.Uuid5(SolutionSpace, r), r });
            }
            var subs = new List<string>();
            foreach (string d in Directory.GetDirectories(dir))
                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0) subs.Add(Path.GetFileName(d));     // a linked folder is not walked
            subs.Sort(StringComparer.Ordinal);
            foreach (string d in subs) WalkItems(root, Path.Combine(dir, d), rel.Length == 0 ? d : rel + "\\" + d, items);
        }

        /// <summary>The text of a solution file (with its byte order mark) that lists every model project and every
        /// editor map below root. name: the solution's file name.</summary>
        public static string SolutionText(string root, string name)
        {
            var items = new List<string[]>();
            WalkItems(root, root, "", items);
            string id = MapValues.Uuid5(SolutionSpace, name);
            var o = new List<string>
            {
                NetXmlWriter.Head, "<Solution", "  version=\"10\">", "  <tool-settings", "    version=\"1\">", "    <setting",
                "      id=\"Emergent.Toolbench.GameSolution.ContentRoot\"", "      property-type=\"String\">", "      <value>.</value>",
                "    </setting>", "  </tool-settings>", "  <Hierarchy", "    version=\"1\">", "    <Item",
                "      content-type=\"Emergent.Toolbench.GameSolution.GameSolutionDocument\"", "      name=\"" + name + "\"",
                "      id=\"" + id + "\"", "      filepath=\"" + name + "\" />"
            };
            foreach (string[] i in items)
            {
                o.Add("    <Item"); o.Add("      content-type=\"" + i[0] + "\""); o.Add("      name=\"" + i[1] + "\"");
                o.Add("      id=\"" + i[2] + "\""); o.Add("      parent=\"" + id + "\""); o.Add("      filepath=\"" + i[3] + "\" />");
            }
            o.Add("  </Hierarchy>"); o.Add("</Solution>"); o.Add("");
            return string.Join("\r\n", o.ToArray());
        }

        /// <summary>Where the solution file goes: a plain file name in root, nothing else.</summary>
        public static string SolutionPath(string root, string name)
        {
            if (name != null && name.IndexOfAny(new[] { '/', '\\' }) >= 0) throw new ModKitException("refused to write \"" + name + "\": the solution is a file name in the project folder, not a path");
            return OutPath.Under(root, name);
        }

        /// <summary>The text of an XML attribute value: the five named references and the numeric ones. A reference
        /// that is none of them stays as written.</summary>
        public static string Unescape(string s)
        {
            if (s.IndexOf('&') < 0) return s;
            return Regex.Replace(s, "&(#[0-9]+|#x[0-9A-Fa-f]+|lt|gt|quot|apos|amp);", delegate (Match m)
            {
                string r = m.Groups[1].Value;
                if (r == "lt") return "<";
                if (r == "gt") return ">";
                if (r == "quot") return "\"";
                if (r == "apos") return "'";
                if (r == "amp") return "&";
                int c;
                bool ok = r[1] == 'x'
                    ? int.TryParse(r.Substring(2), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out c)
                    : int.TryParse(r.Substring(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out c);
                if (!ok || c <= 0 || c > 0x10FFFF || (c >= 0xD800 && c <= 0xDFFF)) return m.Value;
                return char.ConvertFromUtf32(c);
            });
        }

        /// <summary>The files of a folder whose extension is ext (".flat") in any letter case, the same on every
        /// system; a search pattern is matched with letter case on some systems and without on others.</summary>
        public static List<string> FilesOf(string dir, string ext, bool deep)
        {
            var res = new List<string>();
            foreach (string f in Directory.GetFiles(dir, "*", deep ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                if (string.Equals(Path.GetExtension(f), ext, StringComparison.OrdinalIgnoreCase)) res.Add(f);
            return res;
        }

        static void ProjectFiles(string dir, List<string> res)
        {
            foreach (string f in FilesOf(dir, ".emtproj", false)) res.Add(f);
            foreach (string d in Directory.GetDirectories(dir)) ProjectFiles(d, res);
        }

        /// <summary>What the project names but the folder does not hold: projects the maps refer to, and files the
        /// project files list. maps: the Maps folder; refs: the references of the editor maps.</summary>
        public static List<string> Missing(string root, IEnumerable<string> refs)
        {
            var cp = new CasePaths();
            var missing = new List<string>();
            var need = new SortedDictionary<string, bool>(StringComparer.Ordinal);
            foreach (string r in refs) need[r] = true;
            string maps = Path.Combine(root, "Maps");
            foreach (string r in need.Keys)
                if (!cp.Exists(maps, r)) missing.Add("project not shipped (must be synthesised): " + r);
            var emts = new List<string>();
            ProjectFiles(Path.Combine(root, "Library"), emts);
            // listed by the name below root with '/' between the parts, in ordinal order of that name: the same list on
            // every system
            string pre = Path.Combine(root, "Library");
            pre = pre.Substring(0, pre.Length - "Library".Length);
            var byRel = new List<KeyValuePair<string, string>>();
            foreach (string emt in emts)
                byRel.Add(new KeyValuePair<string, string>(emt.Substring(pre.Length).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/'), emt));
            byRel.Sort(delegate (KeyValuePair<string, string> x, KeyValuePair<string, string> y) { return string.CompareOrdinal(x.Key, y.Key); });
            foreach (KeyValuePair<string, string> e in byRel)
            {
                string txt = ReadText(e.Value);
                foreach (Match m in Regex.Matches(txt, "<File\\s+path=\"([^\"]+)\""))
                {
                    string f = Unescape(m.Groups[1].Value);
                    // a name that could leave the folder is not looked for on disk
                    if (OutPath.Why(f) != null || !cp.Exists(Path.GetDirectoryName(e.Value), f)) missing.Add(e.Key + ": " + f);
                }
            }
            return missing;
        }

        /// <summary>The flat files of a game: a folder that holds flat/, or the Exported archive.</summary>
        public static List<KeyValuePair<string, byte[]>> ReadFlatFiles(string path)
        {
            var files = new List<KeyValuePair<string, byte[]>>();
            if (Directory.Exists(path))
            {
                string flat = Path.Combine(path, "flat");
                if (!Directory.Exists(flat)) throw new ModKitException(path + " has no flat folder");
                string root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                foreach (string f in FilesOf(flat, ".flat", true))
                    files.Add(new KeyValuePair<string, byte[]>(Path.GetFullPath(f).Substring(root.Length).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes(f)));
                return files;
            }
            if (!File.Exists(path)) throw new ModKitException("no flat files at " + path + " (a folder with flat/, or the Exported archive)");
            using (M2dArchive a = M2dArchive.Open(path))
                foreach (M2dEntry e in a.Entries)
                    if (e.Name.StartsWith("flat/", StringComparison.Ordinal) && e.Name.EndsWith(".flat", StringComparison.OrdinalIgnoreCase))
                        files.Add(new KeyValuePair<string, byte[]>(e.Name, a.Read(e)));
            return files;
        }

        static void CopyTree(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), false);
            foreach (string d in Directory.GetDirectories(src)) CopyTree(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        /// <summary>Copies a folder, or the entries of an archive, into dst (a folder inside root that is not there
        /// yet). The copy is made beside dst under another name and gets its name only when every file is there, so
        /// a copy that stopped half way is never taken for a whole one. An archive entry whose name is unsafe or
        /// names a folder stops the copy: nothing is left at dst. Entries whose folders differ only in letter case
        /// go into one folder.</summary>
        static void CopyWhole(string root, string source, string dst, string what)
        {
            string tmp = dst + ".copying";
            if (OutPath.WhyNotUnder(root, dst) != null) throw new ModKitException("refused to write " + dst + ": outside the output folder");
            // what a run that stopped left behind
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            if (Directory.Exists(source)) CopyTree(source, tmp);
            else
            {
                var bad = new List<string>();
                var cp = new CasePaths();
                using (M2dArchive a = M2dArchive.Open(source))
                {
                    Directory.CreateDirectory(tmp);
                    foreach (M2dEntry e in a.Entries)
                    {
                        string why = e.Name.EndsWith("/", StringComparison.Ordinal) || e.Name.EndsWith("\\", StringComparison.Ordinal) ? "names a folder, not a file" : OutPath.Why(e.Name);
                        if (why == null) why = OutPath.WhyNotUnder(tmp, cp.Resolve(tmp, e.Name, false));
                        if (why != null) { bad.Add(OutPath.Line(what + " entry", e.Name, why)); continue; }
                        if (bad.Count > 0) continue;
                        string p = cp.Resolve(tmp, e.Name, true);
                        Directory.CreateDirectory(Path.GetDirectoryName(p));
                        File.WriteAllBytes(p, a.Read(e));
                    }
                }
                if (bad.Count > 0)
                {
                    Directory.Delete(tmp, true);
                    throw new ModKitException("refused, nothing copied from " + source + ":\n  " + string.Join("\n  ", bad.ToArray()));
                }
            }
            Directory.Move(tmp, dst);
        }

        static void CopyLibrary(string root, string source, string lib)
        {
            if (!Directory.Exists(source) && !File.Exists(source)) throw new ModKitException("no model library at " + source + " (a folder, or the Library archive)");
            CopyWhole(root, source, lib, "library archive");
        }

        /// <summary>The folder of the game's asset settings (three small text files), which the map editor needs
        /// beside the solution file before it opens the project: Resource/asset-web-config as a folder or as an
        /// archive. False when the Resource folder has neither.</summary>
        static bool CopyAssetConfig(string root, string resourceDir)
        {
            const string name = "asset-web-config";
            string dst = Path.Combine(root, name);
            if (Directory.Exists(dst)) return true;
            var cp = new CasePaths();
            string dir = cp.Resolve(resourceDir, name, false), arch = cp.Resolve(resourceDir, name + ".m2d", false);
            if (Directory.Exists(dir)) CopyWhole(root, dir, dst, name);
            else if (File.Exists(arch) && File.Exists(M2dArchive.HeaderPathOf(arch))) CopyWhole(root, arch, dst, name + " archive");
            else return false;
            return true;
        }

        /// <summary>The asset ids ("urn:llid:" + id) in the values of the maps' entities and of the models they use,
        /// in order of first use.</summary>
        public static List<string> AssetIds(WbFlats flats, IEnumerable<XEntity> ents)
        {
            var res = new List<string>();
            var seen = new Dictionary<string, bool>(StringComparer.Ordinal);
            var doneModels = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (XEntity e in ents)
            {
                foreach (XProp p in e.Props) foreach (XSet s in p.Items) Scan(s.Value, seen, res);
                if (e.Model == null || doneModels.ContainsKey(e.Model)) continue;
                doneModels[e.Model] = true;
                var todo = new List<string> { e.Model };
                for (int i = 0; i < todo.Count; i++)
                {
                    WbFlat f = flats.Get(todo[i]);
                    if (f == null) continue;
                    foreach (WbFlatProp p in f.Props) foreach (XSet s in p.Sets) Scan(s.Value, seen, res);
                    foreach (string m in f.Mixins) if (!doneModels.ContainsKey(m)) { doneModels[m] = true; todo.Add(m); }
                }
            }
            return res;
        }

        static void Scan(string v, Dictionary<string, bool> seen, List<string> res)
        {
            const string pre = "urn:llid:";
            if (v == null) return;
            for (int i = v.IndexOf(pre, StringComparison.Ordinal); i >= 0; i = v.IndexOf(pre, i + 1, StringComparison.Ordinal))
            {
                int s = i + pre.Length, e = s;
                while (e < v.Length && (Uri.IsHexDigit(v[e]) || v[e] == '-')) e++;
                if (e == s) continue;
                string id = v.Substring(s, e - s).ToLowerInvariant();
                if (!seen.ContainsKey(id)) { seen[id] = true; res.Add(id); }
            }
        }

        /// <summary>Asset id to file path from the game's asset list: a folder with llid.nt and relpath.nt, or the
        /// asset-web-metadata archive.</summary>
        public static Dictionary<string, string> ReadAssetPaths(string path) { return ReadAssetPaths(path, null); }

        /// <summary>The same; every path of the list, with or without an id, is added to allPaths.</summary>
        public static Dictionary<string, string> ReadAssetPaths(string path, List<string> allPaths)
        {
            byte[] llid = null, rel = null;
            if (Directory.Exists(path))
            {
                llid = File.ReadAllBytes(Path.Combine(path, "llid.nt"));
                rel = File.ReadAllBytes(Path.Combine(path, "relpath.nt"));
            }
            else
            {
                if (!File.Exists(path)) throw new ModKitException("no asset list at " + path);
                using (M2dArchive a = M2dArchive.Open(path))
                    foreach (M2dEntry e in a.Entries)
                    {
                        string n = e.Name.Substring(e.Name.LastIndexOf('/') + 1).ToLowerInvariant();
                        if (n == "llid.nt") llid = a.Read(e);
                        else if (n == "relpath.nt") rel = a.Read(e);
                    }
                if (llid == null || rel == null) throw new ModKitException(path + " has no llid.nt and relpath.nt");
            }
            string relText = Utf8.GetString(rel);
            if (allPaths != null) foreach (KeyValuePair<string, string> t in Triples(relText)) allPaths.Add(t.Value);
            return AssetPaths(Utf8.GetString(llid), relText);
        }

        static IEnumerable<KeyValuePair<string, string>> Triples(string text)
        {
            foreach (string line in text.Split('\n'))
            {
                // <urn:uuid:U> <predicate> "value".
                int a = line.IndexOf('>'), q = line.IndexOf('"'), z = line.LastIndexOf('"');
                if (!line.StartsWith("<", StringComparison.Ordinal) || a < 0 || q < 0 || z <= q) continue;
                yield return new KeyValuePair<string, string>(line.Substring(1, a - 1), line.Substring(q + 1, z - q - 1));
            }
        }

        public static Dictionary<string, string> AssetPaths(string llidText, string relpathText)
        {
            var relOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> t in Triples(relpathText)) relOf[t.Key] = t.Value;
            var res = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> t in Triples(llidText))
            {
                string r;
                if (relOf.TryGetValue(t.Key, out r)) res[t.Value.ToLowerInvariant()] = r;
            }
            return res;
        }

        /// <summary>Builds the whole folder for the chosen maps. Returns the lines to show.</summary>
        public static List<string> Build(WbProjectOptions o, List<KeyValuePair<string, byte[]>> flatFiles)
        {
            var log = new List<string>();
            string root = Path.GetFullPath(o.Root);
            Directory.CreateDirectory(root);
            string lib = Path.Combine(root, "Library");
            if (!Directory.Exists(lib))
            {
                if (o.LibrarySource == null) throw new ModKitException(root + " has no Library folder and no model library was given");
                CopyLibrary(root, o.LibrarySource, lib);
                log.Add("copied the model library into " + lib);
            }
            WbFlats flats = WbFlats.FromFiles(flatFiles);
            FlatIndex index = FlatIndex.FromFlats(flatFiles);
            var rec = new WbRecon(flats, o.KnownIds);

            var maps = new List<KeyValuePair<string, List<XEntity>>>();
            var all = new List<XEntity>();
            foreach (KeyValuePair<string, byte[]> m in o.Maps)
            {
                List<XEntity> ents = XBlock.Read(m.Value, m.Key);
                maps.Add(new KeyValuePair<string, List<XEntity>>(m.Key, ents));
                all.AddRange(ents);
            }
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            var used = new List<string>();
            foreach (XEntity e in all)
                if (e.Model != null && !ids.ContainsKey(e.Model)) { ids[e.Model] = rec.Id(e.Model); used.Add(e.Model); }

            Dictionary<string, bool> presets = o.AllPresets ? null : PresetClosure(flats, used);
            WbBuildResult br = BuildModels(root, flats, o.KnownIds, presets);
            foreach (KeyValuePair<string, int> s in br.Stats) log.Add(s.Key + " " + s.Value);
            o.Refused.AddRange(br.Refused);

            string mapDir = Path.Combine(root, "Maps");
            Directory.CreateDirectory(mapDir);
            Directory.CreateDirectory(Path.Combine(Path.Combine(root, "Exported"), "xblock"));
            var refs = new List<string>();
            var rep = new ConvertReport();
            foreach (KeyValuePair<string, List<XEntity>> m in maps)
            {
                var opt = new ToBlockOptions { LayersByCategory = o.LayersByCategory, FoldersByCategory = o.FoldersByCategory, ModelIds = ids, ExportFolder = "..\\Exported\\xblock" };
                BlockDoc blk = MapConvert.ToBlock(m.Value, m.Key, index, opt, rep);
                List<string> unsafeNames = blk.UnsafeNames();
                if (unsafeNames.Count > 0) { foreach (string u in unsafeNames) o.Refused.Add("map " + m.Key + ": " + u); continue; }
                blk.Write(Path.Combine(mapDir, blk.Name + ".block"));
                refs.AddRange(blk.References);
                log.Add("map " + blk.Name + ": " + m.Value.Count + " entities");
            }
            log.Add(rep.Line());

            List<string> missing = Missing(root, refs);
            log.Add(missing.Count + " missing project items, listed in MISSING.txt");

            List<string> assets = AssetIds(flats, all);
            var sb = new StringBuilder();
            int unresolved = 0;
            var resolved = new List<KeyValuePair<string, string>>();
            foreach (string a in assets)
            {
                string p = null;
                if (o.AssetPaths != null && !o.AssetPaths.TryGetValue(a, out p)) unresolved++;
                resolved.Add(new KeyValuePair<string, string>(a, p));
                sb.Append(a).Append('\t').Append(p ?? "").Append('\n');
            }
            if (o.ResourceDir != null)
            {
                if (CopyAssetConfig(root, o.ResourceDir)) log.Add("asset settings in asset-web-config");
                else missing.Add("asset-web-config: not in the Resource folder (the map editor reports an error for a project without it)");
            }
            if (o.ResourceDir != null && o.AssetPaths != null)
                using (var src = new WbAssetSource(o.ResourceDir))
                {
                    WbAssetCopy c = WbAssets.Copy(root, src, resolved, o.AssetFiles ?? new List<string>(o.AssetPaths.Values));
                    missing.AddRange(c.Missing);
                    o.Refused.AddRange(c.Refused);
                    log.Add("copied " + c.Models + " model files, " + c.Textures + " texture files and " + c.Others + " other files (" + c.Bytes + " bytes), " + c.Missing.Count + " not found, listed in MISSING.txt");
                }
            File.WriteAllBytes(OutPath.Under(root, "MISSING.txt"), Utf8.GetBytes(string.Join("\n", missing.ToArray()) + "\n"));
            File.WriteAllBytes(OutPath.Under(root, "ASSETS.txt"), Utf8.GetBytes(sb.ToString()));
            log.Add(assets.Count + " asset files used by the maps, listed in ASSETS.txt" +
                (o.AssetPaths == null ? " (ids only: no asset list was given)" : " (" + unresolved + " not in the asset list)"));

            string sol = o.SolutionName ?? Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)) + ".gsl";
            File.WriteAllBytes(SolutionPath(root, sol), Utf8.GetBytes(SolutionText(root, sol)));
            log.Add("solution " + sol);
            if (br.Warnings.Count > 0) log.Add(br.Warnings.Count + " notes on models, in logs/flat2model-warnings.txt");
            log.Add("This folder holds copied game files. Keep it local; do not put it in a repository.");
            return log;
        }
    }
}
