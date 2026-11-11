// The map conversion commands. They work on files and need no data set.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MS2.ModKit.MapEdit;

namespace MS2.ModKit.Cli
{
    static partial class Program
    {
        static int MapCmd(string cmd, Args a)
        {
            switch (cmd)
            {
                case "map-index": return MapIndex(a);
                case "map-to-block": return MapToBlock(a);
                case "map-from-block": return MapFromBlock(a);
                case "map-normalize": return MapNormalize(a);
                default: throw new UsageException("unknown command " + cmd);
            }
        }

        static string OutDir(Args a)
        {
            string o = a.Get("-o");
            if (o == null) throw new UsageException("missing -o <output folder>");
            Directory.CreateDirectory(o);
            return o;
        }

        static FlatIndex Index(Args a)
        {
            string i = a.Get("--index");
            if (i == null) throw new UsageException("missing --index <model index file> (modkit map-index makes it)");
            return FlatIndex.Load(i);
        }

        static string Choice(Args a, string opt, string def, params string[] allowed)
        {
            string v = a.Get(opt) ?? def;
            if (Array.IndexOf(allowed, v) < 0) throw new UsageException(opt + " is one of: " + string.Join(", ", allowed));
            return v;
        }

        static int MapIndex(Args a)
        {
            if (a.Pos.Count != 2) throw new UsageException("map-index <Exported.m2d or folder with flat/> <out.json>");
            FlatIndex ix = a.Pos[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? null : FlatIndex.Load(a.Pos[0]);
            if (ix == null) throw new UsageException("map-index reads an archive or a folder, not an index file");
            File.WriteAllBytes(a.Pos[1], new UTF8Encoding(false).GetBytes(ix.ToJson()));
            Console.WriteLine("models " + ix.Count + " errors " + ix.Errors.Count);
            return 0;
        }

        static int MapToBlock(Args a)
        {
            if (a.Pos.Count == 0) throw new UsageException("map-to-block <map.xblock or folder>... -o DIR --index FILE [--layers none|category] [--folders none|category] [--lib-root PATH] [--model-ids none|FILE] [--report FILE]");
            string outDir = OutDir(a);
            FlatIndex ix = Index(a);
            var opt = new ToBlockOptions
            {
                LayersByCategory = Choice(a, "--layers", "none", "none", "category") == "category",
                FoldersByCategory = Choice(a, "--folders", "none", "none", "category") == "category",
                LibRoot = a.Get("--lib-root") ?? "..\\Library"
            };
            string ids = a.Get("--model-ids") ?? "none";
            if (ids != "none") opt.ModelIds = ReadStringMap(ids);
            var files = new List<string>();
            foreach (string i in a.Pos)
            {
                if (Directory.Exists(i))
                {
                    string[] fs = Directory.GetFiles(i, "*.xblock");
                    Array.Sort(fs, StringComparer.Ordinal);
                    files.AddRange(fs);
                }
                else files.Add(i);
            }
            var rep = new ConvertReport();
            foreach (string f in files)
            {
                string name = Path.GetFileNameWithoutExtension(f);
                BlockDoc blk = MapConvert.ToBlock(XBlock.Read(File.ReadAllBytes(f), f), name, ix, opt, rep);
                blk.Write(Path.Combine(outDir, blk.Name + ".block"));
                Console.Error.WriteLine(f + " -> " + blk.Name + ".block");
            }
            Console.WriteLine(rep.Line());
            if (a.Has("--report"))
            {
                var sb = new StringBuilder();
                foreach (string k in rep.Keys)
                {
                    sb.Append(k).Append('\t').Append(rep.Counts[k]).Append('\n');
                    foreach (string s in rep.Samples[k]) sb.Append('\t').Append(s).Append('\n');
                }
                File.WriteAllText(a.Get("--report"), sb.ToString(), new UTF8Encoding(false));
            }
            return 0;
        }

        static Dictionary<string, string> ReadStringMap(string path)
        {
            // {"model name": "id", ...}
            var res = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> kv in FlatIndex.ReadStringMap(File.ReadAllText(path, new UTF8Encoding(false)), path)) res[kv.Key] = kv.Value;
            return res;
        }

        static int MapFromBlock(Args a)
        {
            if (a.Pos.Count == 0) throw new UsageException("map-from-block <map.block>... -o DIR --index FILE [--dialect tab|toolbench] [--order block|ms2|toolbench]");
            string outDir = OutDir(a);
            FlatIndex ix = Index(a);
            XDialect d = XBlock.ParseDialect(Choice(a, "--dialect", "tab", "tab", "toolbench"));
            string o = Choice(a, "--order", "block", "block", "ms2", "toolbench");
            PropOrder order = o == "block" ? PropOrder.Block : o == "ms2" ? PropOrder.Shipped : PropOrder.Model;
            foreach (string f in a.Pos)
                foreach (KeyValuePair<string, List<XEntity>> x in MapConvert.ToXBlocks(BlockDoc.Read(f), ix, order))
                {
                    string dst = Path.Combine(outDir, x.Key);
                    File.WriteAllBytes(dst, XBlock.Bytes(x.Value, d));
                    Console.WriteLine(dst);
                }
            return 0;
        }

        static int MapNormalize(Args a)
        {
            if (a.Pos.Count == 0) throw new UsageException("map-normalize <map.xblock>... -o DIR [--original FILE or DIR] [--order ms2|keep] [--dialect tab|toolbench|auto]");
            string outDir = OutDir(a);
            string order = Choice(a, "--order", "ms2", "ms2", "keep");
            string dialect = Choice(a, "--dialect", "tab", "tab", "toolbench", "auto");
            string original = a.Get("--original");
            foreach (string f in a.Pos)
            {
                string name = Path.GetFileName(f), orig = null;
                if (original != null)
                {
                    string op = Directory.Exists(original) ? Path.Combine(original, name) : original;
                    if (File.Exists(op)) orig = op;
                }
                List<XEntity> ents = XBlock.Read(File.ReadAllBytes(f), f);
                Dictionary<string, List<string>> origOrder = null;
                byte[] ob = orig == null ? null : File.ReadAllBytes(orig);
                if (ob != null)
                {
                    origOrder = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    foreach (XEntity e in XBlock.Read(ob, orig))
                    {
                        var names = new List<string>();
                        foreach (XProp p in e.Props) names.Add(p.Name);
                        origOrder[e.Id] = names;
                    }
                }
                XBlock.ReorderProps(ents, order == "ms2", origOrder);
                var sorted = new List<XEntity>(ents);
                MapConvert.SortById(sorted);
                XDialect d = dialect == "auto" ? (ob != null ? XBlock.DetectDialect(ob) : XDialect.Tab) : XBlock.ParseDialect(dialect);
                string dst = Path.Combine(outDir, name);
                File.WriteAllBytes(dst, XBlock.Bytes(sorted, d));
                Console.WriteLine(dst + " " + sorted.Count + " entities " + (orig != null ? "order from original" : "order=" + order) + " " + (d == XDialect.Tab ? "tab" : "toolbench"));
            }
            return 0;
        }
    }
}
