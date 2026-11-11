// modkit field-rows and text-import: the scans of the Field rows and Text import views on the command line, and
// their Put and Take out into a mod folder. The library does the work (FieldRows.cs, TextImport.cs).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MS2.ModKit.Cli
{
    static partial class Program
    {
        static string N(int n) { return n.ToString("N0", CultureInfo.InvariantCulture); }

        /// <summary>The mod of --put or --take (null without either): loaded, or with --id a new one for an empty folder.</summary>
        static Mod ImportMod(Ctx x, out string dir, out bool put)
        {
            string p = x.A.Get("--put"), t = x.A.Get("--take");
            dir = null; put = p != null;
            if (p != null && t != null) throw new UsageException("give --put or --take, not both");
            string path = p ?? t;
            if (path == null)
            {
                if (x.A.Has("--id")) throw new UsageException("--id names a new mod for --put");
                return null;
            }
            string file = Directory.Exists(path) ? Path.Combine(path, "mod.xml") : path;
            if (File.Exists(file))
            {
                if (x.A.Has("--id")) throw new UsageException("--id is for a new mod; " + file + " is there already");
                Mod m = Mod.Load(path);
                dir = m.Dir;
                return m;
            }
            if (t != null) throw new ModKitException("no mod manifest at " + file + ": there is nothing to take out");
            if (!x.A.Has("--id")) throw new ModKitException("no mod manifest at " + file + "; give --id <mod id> to start a new mod there");
            if (string.Equals(Path.GetFileName(path), "mod.xml", StringComparison.OrdinalIgnoreCase)) path = Path.GetDirectoryName(Path.GetFullPath(path));
            dir = Path.GetFullPath(path);
            return Mod.Parse("<mod id=\"" + Xml.XmlDoc.EscapeAttr(x.A.Get("--id"), '"') + "\" name=\"\" version=\"1\"/>", "the new mod");
        }

        static string Yes(bool? b) { return b == null ? "-" : b.Value ? "yes" : "no"; }

        static int FieldRowsCmd(Ctx x)
        {
            if (x.A.Pos.Count != 1) throw new UsageException("field-rows needs the source archive (the client whose map/*.xml has the maps, e.g. gms-client:Xml.m2d)");
            string source = x.A.Pos[0], target = x.A.Get("--target") ?? "client:Xml.m2d";
            string pack = x.A.Get("--pack") ?? "server:Xml.m2d";
            if (pack == "none") pack = null;
            else if (x.A.Has("--pack") && !x.Data.Exists(pack)) throw new ModKitException("--pack " + pack + ": no such archive in the data set");
            string dir; bool put;
            Mod mod = ImportMod(x, out dir, out put);
            FieldRows fr = FieldRows.Scan(x.Data, target, source, pack, x.A.Get("--locale") ?? "KR");

            // the rows to put or take: --rows default | ticked | id,id,...; put takes the default ticks, take every listed row
            string which = x.A.Get("--rows") ?? (mod == null || put ? "default" : "all");
            List<FieldRow> rows;
            if (which == "default") rows = fr.Rows.FindAll(r => r.Default);
            else if (which == "ticked") rows = fr.Rows.FindAll(r => r.Ticked);
            else if (which == "all") rows = new List<FieldRow>(fr.Rows);
            else
            {
                rows = new List<FieldRow>();
                var bad = new List<string>();
                foreach (string s in which.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int id;
                    FieldRow r = int.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id) ? fr.Rows.Find(f => f.Id == id) : null;
                    if (r == null) bad.Add(s.Trim());
                    else if (put && !r.Ticked) bad.Add(s.Trim() + " (" + r.Kind + ")");
                    else if (!rows.Contains(r)) rows.Add(r);
                }
                if (bad.Count > 0) throw new UsageException("--rows: " + string.Join(", ", bad) + (put ? " is not a Fill, Add or FillBeside row of the scan" : " is not a row of the scan"));
            }

            string done = null;
            int changed = 0;
            if (mod != null)
            {
                int had = fr.InMod(mod, rows);
                if (put)
                {
                    changed = fr.Put(mod, rows.FindAll(r => r.Ticked));
                    done = "put " + N(changed) + " field rows into mod " + mod.Id + " (" + N(rows.Count) + " chosen, " + N(had) + " in the mod already)";
                }
                else
                {
                    changed = fr.Take(mod, rows);
                    done = "took " + N(changed) + " field rows out of mod " + mod.Id;
                }
                mod.Save(dir);
                done += "; saved " + Path.Combine(dir, "mod.xml");
            }

            if (x.Json)
            {
                var chosen = new HashSet<FieldRow>(rows);
                WriteJson(new
                {
                    target = fr.Target, source = fr.Source, locale = fr.Locale, open = fr.Open, noSource = fr.NoSource.ToArray(),
                    rows = fr.Rows.Select(r => new
                    {
                        kind = r.Kind.ToString(), id = r.Id, file = r.File, xblock = r.XBlock, feature = r.Feature, featureOn = r.FeatureOn,
                        featureLevel = r.FeatureLevel, onServer = r.OnServer, attrs = r.Attrs, @default = r.Default, chosen = chosen.Contains(r),
                        inMod = mod != null && r.Xml != null && fr.InMod(mod, new[] { r }) > 0, error = r.Error, xml = r.Xml,
                    }).ToArray(),
                    mod = mod == null ? null : new { id = mod.Id, dir, put, changed },
                });
                return 0;
            }
            var o = new StringBuilder();
            o.Append(fr.Summary("\n")).Append('\n');
            if (fr.Rows.Count > 0)
            {
                o.Append("kind\tid\txblock\tfeature\ton\tlevel\tserver\tattrs\tdefault").Append(mod != null ? "\tin mod" : "").Append('\n');
                foreach (FieldRow r in fr.Rows)
                {
                    o.Append(r.Kind).Append('\t').Append(r.Id.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(r.XBlock ?? "-").Append('\t')
                     .Append(string.IsNullOrEmpty(r.Feature) ? "-" : r.Feature).Append('\t').Append(Yes(r.FeatureOn)).Append('\t')
                     .Append(r.FeatureLevel == 0 ? "-" : r.FeatureLevel.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Yes(r.OnServer)).Append('\t')
                     .Append(r.Attrs.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(r.Default ? "yes" : "no");
                    if (mod != null) o.Append('\t').Append(r.Xml != null && fr.InMod(mod, new[] { r }) > 0 ? "yes" : "no");
                    o.Append('\n');
                }
            }
            if (done != null) o.Append(done).Append('\n');
            Console.Write(o);
            return 0;
        }

        static int TextImportCmd(Ctx x)
        {
            if (x.A.Pos.Count != 1) throw new UsageException("text-import needs the source archive (the client whose string/<language>/*.xml has the text, e.g. gms-client:Xml.m2d)");
            string source = x.A.Pos[0], target = x.A.Get("--target") ?? "client:Xml.m2d";
            string lang = x.A.Get("--lang") ?? "kr", sourceLang = x.A.Get("--source-lang") ?? "en";
            string dir; bool put;
            Mod mod = ImportMod(x, out dir, out put);
            TextImport ti = TextImport.Scan(x.Data, mod, target, lang, source, sourceLang);

            // the rows: of the files of --files (our file names), the fills, and with --plain the plainly different ones too
            HashSet<string> files = null;
            if (x.A.Has("--files"))
            {
                files = new HashSet<string>(x.A.Get("--files").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim()), StringComparer.OrdinalIgnoreCase);
                var unknown = files.Where(f => !ti.Files.Exists(s => string.Equals(s.File, f, StringComparison.OrdinalIgnoreCase))).ToArray();
                if (unknown.Length > 0) throw new UsageException("--files: " + string.Join(", ", unknown) + " is not one of our files in string/" + lang + "/");
            }
            bool plain = x.A.Has("--plain");
            List<TextRow> rows = ti.Rows.FindAll(r => (files == null || files.Contains(r.File)) && (r.Kind == TextKind.Fill || plain));

            string done = null;
            int changed = 0;
            if (mod != null)
            {
                int had = ti.InMod(mod, rows);
                if (put)
                {
                    changed = ti.Put(mod, rows);
                    done = "put " + N(changed) + " attributes into mod " + mod.Id + " (" + N(rows.Count) + " chosen, " + N(had) + " in the mod already"
                        + (rows.Count - had - changed > 0 ? ", " + N(rows.Count - had - changed) + " set to another text by the mod" : "") + ")";
                }
                else
                {
                    changed = ti.Take(mod, rows);
                    done = "took " + N(changed) + " attributes out of mod " + mod.Id + " (the sets that still had the source text)";
                }
                mod.Save(dir);
                done += "; saved " + Path.Combine(dir, "mod.xml");
            }

            bool list = x.A.Has("--list");
            if (x.Json)
            {
                WriteJson(new
                {
                    target = ti.Target, lang = ti.TargetLang, source = ti.Source, sourceLang = ti.SourceLang,
                    files = ti.Files.Select(f => new
                    {
                        file = f.File, sources = f.SourceFiles.ToArray(), keys = f.Keys, has = f.Has, fill = f.Fill, plain = f.Plain, near = f.Near, same = f.Same,
                        korean = f.Korean, placeholder = f.Placeholder, form = f.Form, missing = f.Missing, ambiguous = f.Ambiguous, kept = f.Kept, sourceOnly = f.SourceOnly, error = f.Error,
                    }).ToArray(),
                    chosen = rows.Count,
                    rows = list ? rows.Select(r => new { kind = r.Kind.ToString(), file = r.File, entry = r.Entry, at = r.At, attr = r.Attr, ours = r.Ours, source = r.Source, overlap = r.Overlap }).ToArray() : null,
                    mod = mod == null ? null : new { id = mod.Id, dir, put, changed },
                });
                return 0;
            }
            var o = new StringBuilder();
            o.Append(ti.Summary("\n")).Append('\n');
            o.Append("file\tkeys\thas\tfill\tplain\tnear\tsame\tkorean\tplaceholder\tform\tmissing\tambiguous\tkept\tsources\n");
            foreach (TextFileStat f in ti.Files)
            {
                if (files != null && !files.Contains(f.File)) continue;
                o.Append(f.File);
                foreach (int v in new[] { f.Keys, f.Has, f.Fill, f.Plain, f.Near, f.Same, f.Korean, f.Placeholder, f.Form, f.Missing, f.Ambiguous, f.Kept })
                    o.Append('\t').Append(v.ToString(CultureInfo.InvariantCulture));
                o.Append('\t').Append(f.Error != null ? "cannot read: " + f.Error : f.SourceFiles.Count == 0 ? "-" : string.Join(",", f.SourceFiles)).Append('\n');
            }
            if (list)
                foreach (TextRow r in rows)
                    o.Append(r.Kind == TextKind.Fill ? "fill" : "plain").Append('\t').Append(r.Entry).Append('\t').Append(r.At).Append("\t@").Append(r.Attr).Append('\t')
                     .Append(OneLine(r.Ours)).Append("\t-> ").Append(OneLine(r.Source)).Append('\n');
            o.Append(N(rows.Count)).Append(" attributes chosen (").Append(plain ? "fills and plainly different" : "fills").Append(files != null ? ", of " + files.Count + " files" : "").Append(")\n");
            if (done != null) o.Append(done).Append('\n');
            Console.Write(o);
            return 0;
        }

        static string OneLine(string s) { return s == null ? "" : s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t"); }
    }
}
