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
                foreach (string e in Directory.GetFileSystemEntries(dir))
                {
                    string n = Path.GetFileName(e);
                    d["=" + n] = n;
                    string low = "~" + n.ToLowerInvariant();
                    if (!d.ContainsKey(low)) d[low] = n;
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

        static void WriteModel(CasePaths cp, WbModel m, string baseDir, string rel)
        {
            string path = cp.Resolve(baseDir, rel, true);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, Utf8.GetBytes(m.Text()));
        }

        static string BaseName(string relFile)
        {
            string n = relFile.Replace('\\', '/');
            n = n.Substring(n.LastIndexOf('/') + 1);
            if (n.EndsWith(".model", StringComparison.Ordinal)) n = n.Substring(0, n.Length - 6);
            else if (n.EndsWith(".preset", StringComparison.Ordinal)) n = n.Substring(0, n.Length - 7);
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
                foreach (Match m in Regex.Matches(text, "<File\\s+path=\"([^\"]+\\.(?:model|preset))\""))
                {
                    string relF = m.Groups[1].Value, nm = BaseName(relF);
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
                        if (cp.Exists(epDir, relF)) { res.Count("library-preset-kept(shipped)"); continue; }
                        WriteModel(cp, rec.BuildPreset(fname), epDir, relF);
                        res.Count("library-preset-generated");
                    }
                    else
                    {
                        WriteModel(cp, rec.Build(fname), epDir, relF);
                        res.Count("library-model-generated");
                    }
                }
                foreach (KeyValuePair<string, string> b in byLower)
                {
                    libOfName[b.Value] = libdir;
                    if (listed.ContainsKey(b.Key)) continue;
                    string relF = b.Value + ".model";
                    WriteModel(cp, rec.Build(b.Value), epDir, relF);
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
                foreach (string n in names)
                {
                    WbModel m = rec.BuildPreset(n);
                    WriteModel(cp, m, pdir, sub + "/" + n + ".preset");
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
                File.WriteAllBytes(Path.Combine(pdir, label + ".emtproj"),
                    Utf8.GetBytes(ProjectText(files, new List<string>(refs.Keys), "..\\..\\Exported\\Flat\\Presets\\" + FlatOutName(c.Key), sub)));
                res.Count("preset-projects");
                res.Log.Add(label + ": " + names.Count + " presets, " + refs.Count + " references");
            }
            res.Warnings = rec.Warnings;
            if (rec.Warnings.Count > 0)
            {
                Directory.CreateDirectory(Path.Combine(root, "logs"));
                File.WriteAllBytes(Path.Combine(Path.Combine(root, "logs"), "flat2model-warnings.txt"), Utf8.GetBytes(string.Join("\n", rec.Warnings.ToArray()) + "\n"));
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

        static void ProjectFiles(string dir, List<string> res)
        {
            foreach (string f in Directory.GetFiles(dir, "*.emtproj")) res.Add(f);
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
            emts.Sort(StringComparer.Ordinal);
            string pre = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (string emt in emts)
            {
                string txt = ReadText(emt);
                foreach (Match m in Regex.Matches(txt, "<File\\s+path=\"([^\"]+)\""))
                    if (!cp.Exists(Path.GetDirectoryName(emt), m.Groups[1].Value))
                        missing.Add(emt.Substring(pre.Length).Replace(Path.DirectorySeparatorChar, '/') + ": " + m.Groups[1].Value);
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
                foreach (string f in Directory.GetFiles(flat, "*.flat", SearchOption.AllDirectories))
                    files.Add(new KeyValuePair<string, byte[]>(Path.GetFullPath(f).Substring(root.Length).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes(f)));
                return files;
            }
            if (!File.Exists(path)) throw new ModKitException("no flat files at " + path + " (a folder with flat/, or the Exported archive)");
            using (M2dArchive a = M2dArchive.Open(path))
                foreach (M2dEntry e in a.Entries)
                    if (e.Name.StartsWith("flat/", StringComparison.Ordinal) && e.Name.EndsWith(".flat", StringComparison.Ordinal))
                        files.Add(new KeyValuePair<string, byte[]>(e.Name, a.Read(e)));
            return files;
        }

        static void CopyTree(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), false);
            foreach (string d in Directory.GetDirectories(src)) CopyTree(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        static void CopyLibrary(string source, string lib)
        {
            if (Directory.Exists(source)) { CopyTree(source, lib); return; }
            if (!File.Exists(source)) throw new ModKitException("no model library at " + source + " (a folder, or the Library archive)");
            string full = Path.GetFullPath(lib).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (M2dArchive a = M2dArchive.Open(source))
                foreach (M2dEntry e in a.Entries)
                {
                    string p = Path.GetFullPath(Path.Combine(lib, e.Name.Replace('/', Path.DirectorySeparatorChar)));
                    if (!p.StartsWith(full, StringComparison.Ordinal)) throw new ModKitException("the library archive has an entry outside its folder: " + e.Name);
                    Directory.CreateDirectory(Path.GetDirectoryName(p));
                    File.WriteAllBytes(p, a.Read(e));
                }
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
                WbFlat f = flats.Get(e.Model);
                if (f != null) foreach (WbFlatProp p in f.Props) foreach (XSet s in p.Sets) Scan(s.Value, seen, res);
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
        public static Dictionary<string, string> ReadAssetPaths(string path)
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
            return AssetPaths(Utf8.GetString(llid), Utf8.GetString(rel));
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
                CopyLibrary(o.LibrarySource, lib);
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

            string mapDir = Path.Combine(root, "Maps");
            Directory.CreateDirectory(mapDir);
            Directory.CreateDirectory(Path.Combine(Path.Combine(root, "Exported"), "xblock"));
            var refs = new List<string>();
            var rep = new ConvertReport();
            foreach (KeyValuePair<string, List<XEntity>> m in maps)
            {
                var opt = new ToBlockOptions { LayersByCategory = o.LayersByCategory, FoldersByCategory = o.FoldersByCategory, ModelIds = ids, ExportFolder = "..\\Exported\\xblock" };
                BlockDoc blk = MapConvert.ToBlock(m.Value, m.Key, index, opt, rep);
                blk.Write(Path.Combine(mapDir, blk.Name + ".block"));
                refs.AddRange(blk.References);
                log.Add("map " + blk.Name + ": " + m.Value.Count + " entities");
            }
            log.Add(rep.Line());

            List<string> missing = Missing(root, refs);
            File.WriteAllBytes(Path.Combine(root, "MISSING.txt"), Utf8.GetBytes(string.Join("\n", missing.ToArray()) + "\n"));
            log.Add(missing.Count + " missing project items, listed in MISSING.txt");

            List<string> assets = AssetIds(flats, all);
            var sb = new StringBuilder();
            int unresolved = 0;
            foreach (string a in assets)
            {
                string p = null;
                if (o.AssetPaths != null && !o.AssetPaths.TryGetValue(a, out p)) unresolved++;
                sb.Append(a).Append('\t').Append(p ?? "").Append('\n');
            }
            File.WriteAllBytes(Path.Combine(root, "ASSETS.txt"), Utf8.GetBytes(sb.ToString()));
            log.Add(assets.Count + " asset files used by the maps, listed in ASSETS.txt" +
                (o.AssetPaths == null ? " (ids only: no asset list was given)" : " (" + unresolved + " not in the asset list)"));

            string sol = o.SolutionName ?? Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)) + ".gsl";
            File.WriteAllBytes(Path.Combine(root, sol), Utf8.GetBytes(SolutionText(root, sol)));
            log.Add("solution " + sol);
            if (br.Warnings.Count > 0) log.Add(br.Warnings.Count + " notes on models, in logs/flat2model-warnings.txt");
            log.Add("This folder holds copied game files. Keep it local; do not put it in a repository.");
            return log;
        }
    }
}
