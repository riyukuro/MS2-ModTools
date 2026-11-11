// modkit: the command line over the MS2.ModKit library. Everything it does is a library call; this file only
// reads arguments and settings and prints.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit.Cli
{
    static partial class Program
    {
        const string Usage = @"modkit: row-level mods for MapleStory2 game tables. Originals are only read.

  modkit targets                               the targets (data profiles) of the data set and their folders
  modkit archives                              the .m2d archives of the data roots
  modkit ls <archive> [pattern]                entries (pattern: * within a folder, ** across folders)
  modkit cat <archive> <entry>                 one entry to stdout, as stored in the archive
  modkit rows <archive> <entry> [selector]     selectors of the rows (or of the children of one row)
  modkit schema build [archive...]             derive the table schemas from your data into the local cache
  modkit schema show <archive> [table]         tables of an archive, or the elements and attributes of one
  modkit validate <mod>...                     check mods against the data: errors, conflicts, warnings
  modkit diff <mod>...                         the same and every row change, nothing is written
  modkit apply -w <workspace> <mod>...         write modded copies of the touched archives into the workspace
  modkit revert -w <workspace> [mod id]        rebuild the copies without one mod, or remove them all
  modkit status -w <workspace>                 what is applied in a workspace
  modkit apply --game <game folder> <mod>...   write the client's changed tables as plain files under
                                               <game folder>/Data (the game reads them before its archives);
                                               with -w too, the server's archives go into the workspace
  modkit revert --game <game folder>           remove the files that apply --game wrote (with -w: the copies too)
  modkit status --game <game folder>           the files that apply --game wrote
  modkit sync <id>... [mod...]                 the fields of an item that the client and the server both hold:
                                               where the two sides differ, as shipped and after the mods
                                               (--all: every mapped field)
  modkit refs <id>... [mod...]                 the rows of server-only tables that belong to an item (option,
                                               drop and shop rows), as shipped or as the mods leave them
  modkit next-id <mod> <space> [count]         free ids of a range the mod reserves
  modkit pack <folder> <out.m2d> [version]     a new archive from the files of a folder (MS2F, NS2F, OS2F or PS2F)

  modkit map-index <Exported.m2d or folder> <out.json>   the model listing of a game (its .flat files) as one file
  modkit map-to-block <map.xblock or folder>... -o DIR --index FILE     game maps as editor maps (.block)
  modkit map-from-block <map.block>... -o DIR --index FILE              editor maps as game maps (.xblock)
  modkit map-normalize <map.xblock>... -o DIR [--original FILE or DIR]  an exported game map in the shipped form
  modkit map-diff --id MODID -o MODDIR <shipped.xblock> <edited.xblock>...  the edits of a map as a mod (add, remove, change)
  modkit map-project <map.xblock>... -o DIR --resource <Data/Resource>    a folder the map editor can open (local only)
  modkit map-models <project folder> --flats SRC                         only its model and preset files, for a whole game
  modkit map-solution <project folder> [NAME.gsl]                        only its solution file
                                               (see Maps in the README for the options of these three)

  <archive> is <root>:<path>, for example client:Xml.m2d, gms-client:Xml.m2d, server:Xml.m2d, server:Server.m2d.
  The roots are the targets: client (the Korean client, also written kms-client), gms-client (the global
  client, also gms) and server. One mod is applied to every target the data set has (see Profiles in the README).
  <mod> is a folder with a mod.xml (or the mod.xml itself). Where two mods set the same thing the mod of the
  higher priority wins: the later one, or the order of --priority.

options
  --client DIR (or --kms DIR)    the Korean client's data folder (default: MS2_CLIENT_DATA, then modtools.toml)
  --gms DIR                      the global client's data folder (default: MS2_GMS_CLIENT_DATA, then modtools.toml)
  --server DIR                   the server's data folder (default: MS2_SERVER_DATA, then modtools.toml)
  --schema DIR                   schema cache (default: <MS2_OUT>/modkit/schema)
  --notes FILE                   schema notes (default: tools/modkit/schema-notes.xml)
  --allow-conflicts              apply although mods conflict: the mod of the higher priority wins, and the
                                 conflict line names it (a conflict with no winner, like two mods with
                                 overlapping id ranges, still stops the apply)
  --priority ID,ID,...           mod ids, the highest priority first; mods not named come below them, a later
                                 one above an earlier one (default: MS2_MOD_PRIORITY, then modtools.toml)
  --verify-all                   apply: compare every untouched entry with the original after writing
  --also-copy ARCHIVE            apply: copy this archive into the workspace even if nothing touches it
  --no-sync                      do not write the other side of a field the mods change on one side (see
                                 One mod, both sides in the README); the plan warns instead
  --map FILE                     field maps to use instead of the built-in ones
  --server-target DIR            apply, revert with -w: copy the server's archives from the workspace to DIR
  --after-apply COMMAND          apply, revert with -w: run this command line when a server archive changed
                                 ({target} and {workspace} are replaced by the folders); validate and diff
                                 show it, its output is printed and its exit code reported
  --json                         validate, diff, apply, revert: print the result as JSON
  -q                             no progress on stderr

exit code: 0 done, 1 the mods have errors or conflicts (nothing written), 2 bad usage or a failure,
           3 applied, but the copy to the server target or the command after it failed";

        sealed class Args
        {
            public readonly List<string> Pos = new List<string>();
            public readonly Dictionary<string, string> Opt = new Dictionary<string, string>();
            public readonly List<string> AlsoCopy = new List<string>();
            public bool Has(string k) { return Opt.ContainsKey(k); }
            public string Get(string k) { string v; return Opt.TryGetValue(k, out v) ? v : null; }
        }

        static readonly string[] ValueOpts = { "--client", "--kms", "--gms", "--server", "--schema", "--notes", "-w", "--game", "--also-copy", "--map", "--server-target", "--after-apply",
            "-o", "--id", "--index", "--layers", "--folders", "--lib-root", "--model-ids", "--report", "--dialect", "--order", "--original", "--flats", "--library", "--known-ids", "--assets", "--resource", "--priority" };
        static readonly string[] FlagOpts = { "--allow-conflicts", "--verify-all", "--json", "-q", "-h", "--help", "--no-sync", "--all", "--all-presets", "--no-asset-files" };

        static int Main(string[] argv)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            try { return Run(argv); }
            catch (UsageException x) { Console.Error.WriteLine("modkit: " + x.Message + "\n(modkit --help shows the commands)"); return 2; }
            catch (ModKitException x) { Console.Error.WriteLine("modkit: " + x.Message); return 2; }
            catch (IOException x) { Console.Error.WriteLine("modkit: " + x.Message); return 2; }
            catch (UnauthorizedAccessException x) { Console.Error.WriteLine("modkit: " + x.Message); return 2; }
        }

        sealed class UsageException : Exception { public UsageException(string m) : base(m) { } }

        static Args Parse(string[] argv)
        {
            var a = new Args();
            for (int i = 0; i < argv.Length; i++)
            {
                string s = argv[i];
                if (Array.IndexOf(ValueOpts, s) >= 0)
                {
                    if (i + 1 >= argv.Length) throw new UsageException(s + " needs a value");
                    if (s == "--also-copy") a.AlsoCopy.Add(argv[++i]); else a.Opt[s] = argv[++i];
                }
                else if (Array.IndexOf(FlagOpts, s) >= 0) a.Opt[s] = "";
                else if (s.StartsWith("-") && s.Length > 1 && a.Pos.Count > 0 && a.Pos[0] != "rows") throw new UsageException("unknown option " + s);
                else a.Pos.Add(s);
            }
            return a;
        }

        static int Run(string[] argv)
        {
            Args a = Parse(argv);
            if (a.Pos.Count == 0 || a.Has("-h") || a.Has("--help")) { Console.WriteLine(Usage); return a.Pos.Count == 0 && !a.Has("-h") && !a.Has("--help") ? 2 : 0; }
            string cmd = a.Pos[0];
            a.Pos.RemoveAt(0);
            if (cmd == "pack") return Pack(a);
            if (cmd.StartsWith("map-")) return MapCmd(cmd, a);
            RepoSettings set = RepoSettings.Load(AppContext.BaseDirectory, Directory.GetCurrentDirectory());
            using (var data = new DataSet())
            {
                if (a.Has("--client") && a.Has("--kms")) throw new UsageException("--client and --kms name the same folder; give one");
                string c = a.Get("--kms") ?? a.Get("--client") ?? set.Path(Profile.KmsClient.EnvVar),
                       g = a.Get("--gms") ?? set.Path(Profile.GmsClient.EnvVar),
                       s = a.Get("--server") ?? set.Path(Profile.Server.EnvVar);
                if (!string.IsNullOrEmpty(c)) data.AddTarget(Profile.KmsClient, c);
                if (!string.IsNullOrEmpty(g)) data.AddTarget(Profile.GmsClient, g);
                if (!string.IsNullOrEmpty(s)) data.AddTarget(Profile.Server, s);
                foreach (Profile p in data.Profiles) data.SetFeatures(p, set.Value(p.FeaturesVar));
                if (data.Roots.Count == 0)
                    throw new ModKitException("no data root: set MS2_CLIENT_DATA, MS2_GMS_CLIENT_DATA and/or MS2_SERVER_DATA (environment or modtools.toml), or pass --client / --gms / --server");
                var ctx = new Ctx { A = a, Data = data, Set = set, Json = a.Has("--json"), Quiet = a.Has("-q") || Console.IsErrorRedirected };
                switch (cmd)
                {
                    case "targets": return TargetsCmd(ctx);
                    case "archives": return Archives(ctx);
                    case "ls": return Ls(ctx);
                    case "cat": return Cat(ctx);
                    case "rows": return Rows(ctx);
                    case "schema": return Schema(ctx);
                    case "validate": return PlanCmd(ctx, false);
                    case "diff": return PlanCmd(ctx, true);
                    case "apply": return Apply(ctx);
                    case "revert": return Revert(ctx);
                    case "status": return Status(ctx);
                    case "sync": return Sync(ctx);
                    case "refs": return Refs(ctx);
                    case "next-id": return NextId(ctx);
                    default: throw new UsageException("unknown command " + cmd);
                }
            }
        }

        sealed class Ctx
        {
            public Args A; public DataSet Data; public RepoSettings Set; public bool Json, Quiet;
            string last = "";

            public void Progress(ProgressInfo p)
            {
                if (Quiet) return;
                string line = p.Total > 0 ? p.Stage + " " + p.Done + "/" + p.Total : p.Stage + " " + p.Item;
                if (p.Total > 0 && p.Done != p.Total && p.Done % 2000 != 0) return;
                if (line == last) return;
                last = line;
                Console.Error.Write("\r" + line.PadRight(60));
                if (p.Total > 0 && p.Done == p.Total) Console.Error.Write("\r" + new string(' ', 60) + "\r");
            }

            public string SchemaDir { get { return A.Get("--schema") ?? System.IO.Path.Combine(Set.OutDir, "modkit", "schema"); } }

            public SchemaSet Schemas()
            {
                SchemaSet s = SchemaSet.Load(SchemaDir, Data);
                string notes = A.Get("--notes") ?? (Set.Repo == null ? null : System.IO.Path.Combine(Set.Repo, "tools", "modkit", "schema-notes.xml"));
                if (notes != null && File.Exists(notes)) s.LoadNotes(notes);
                else if (A.Has("--notes")) throw new ModKitException("no notes file " + notes);
                return s;
            }

            public FieldMaps Maps() { return A.Has("--map") ? FieldMaps.Load(A.Get("--map")) : null; }

            public PlanOptions PlanOpt(bool schemas)
            {
                return new PlanOptions { Schemas = schemas ? Schemas() : null, Progress = Progress, Maps = Maps(), NoSync = A.Has("--no-sync") };
            }

            /// <summary>The server target folder and the command of the options; both off without them.</summary>
            public ServerStep Step()
            {
                return new ServerStep { TargetDir = A.Get("--server-target"), Command = A.Get("--after-apply") };
            }

            public string WsDir { get { return A.Has("-w") ? System.IO.Path.GetFullPath(A.Get("-w")) : null; } }

            public string Need(int i, string what)
            {
                if (A.Pos.Count <= i) throw new UsageException("missing " + what);
                return A.Pos[i];
            }

            public List<Mod> Mods(int from)
            {
                var mods = new List<Mod>();
                for (int i = from; i < A.Pos.Count; i++) mods.Add(Mod.Load(A.Pos[i]));
                if (mods.Count == 0) throw new UsageException("missing <mod>");
                // the priority list: of the option (an id no mod has is a mistake) or of the setting (may name mods that are not given this time)
                var unknown = new List<string>();
                if (A.Has("--priority"))
                {
                    mods = ModOrder.Arrange(mods, ModOrder.Parse(A.Get("--priority")), unknown);
                    if (unknown.Count > 0) throw new UsageException("--priority names " + string.Join(", ", unknown.ToArray()) + ", which is not the id of a mod given here");
                }
                else mods = ModOrder.Arrange(mods, ModOrder.Parse(Set.Value(ModOrder.Setting)), null);
                return mods;
            }

            public Workspace Ws()
            {
                string w = A.Get("-w");
                if (w == null) throw new UsageException("missing -w <workspace> (or --game <game folder>)");
                return new Workspace(w);
            }

            /// <summary>The loose-file target of --game, or null without the option.</summary>
            public LooseTarget Game()
            {
                string g = A.Get("--game");
                if (g == null) return null;
                if (!Directory.Exists(System.IO.Path.Combine(g, "Data"))) throw new ModKitException(g + " has no Data folder; --game takes the folder that holds the game's Data");
                return new LooseTarget(g);
            }
        }

        // A new archive from loose files: for test data sets, and for archives that do not exist in the game.
        static int Pack(Args a)
        {
            if (a.Pos.Count < 2) throw new UsageException("pack <folder> <out.m2d> [version]");
            string dir = Path.GetFullPath(a.Pos[0]), dst = Path.GetFullPath(a.Pos[1]);
            if (!Directory.Exists(dir)) throw new ModKitException("no folder " + dir);
            if (File.Exists(dst) || File.Exists(M2dArchive.HeaderPathOf(dst))) throw new ModKitException(dst + " exists; pack does not write over an archive");
            PackVersion v = PackVersion.MS2F;
            if (a.Pos.Count > 2 && !Enum.TryParse(a.Pos[2], true, out v)) throw new UsageException("version is MS2F, NS2F, OS2F or PS2F");
            string[] files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            var list = new List<KeyValuePair<string, byte[]>>();
            foreach (string f in files) list.Add(new KeyValuePair<string, byte[]>(f.Substring(dir.Length).TrimStart('/', '\\').Replace('\\', '/'), File.ReadAllBytes(f)));
            list.Sort((p, q) => string.CompareOrdinal(p.Key, q.Key));
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            M2dWriter.Create(dst, v, M2dFlag.AesZlib, list);
            Console.WriteLine(dst + ": " + list.Count + " entries, " + v);
            return 0;
        }

        // ---- looking at the data ----

        static int TargetsCmd(Ctx x)
        {
            Console.WriteLine(Targets.Line(x.Data));
            foreach (string r in Targets.Roots(x.Data))
            {
                Profile p = Profile.Find(r);
                string what = p == null || p.Root != r ? "" : "\t" + p.LayoutText() + ", table/" + p.Region + ", strings " + string.Join(" ", p.Languages);
                Console.WriteLine(Targets.NameOf(r) + "\t" + r + ":\t" + x.Data.RootFolder(r) + what);
            }
            return 0;
        }

        static int Archives(Ctx x)
        {
            foreach (string r in x.Data.ListArchives())
            {
                var fi = new FileInfo(x.Data.Resolve(r));
                Console.WriteLine(r + "\t" + fi.Length);
            }
            return 0;
        }

        static int Ls(Ctx x)
        {
            M2dArchive arc = x.Data.Open(x.Need(0, "<archive>"));
            Regex re = x.A.Pos.Count > 1 ? IdSpaces.EntryRegex(x.A.Pos[1]) : null;
            var o = new StringBuilder();
            foreach (M2dEntry e in arc.Entries)
                if (re == null || re.IsMatch(e.Name)) o.Append(e.Name).Append('\t').Append(e.Size).Append('\n');
            Console.Write(o);
            return 0;
        }

        static int Cat(Ctx x)
        {
            M2dArchive arc = x.Data.Open(x.Need(0, "<archive>"));
            M2dEntry e = arc.Find(x.Need(1, "<entry>"));
            if (e == null) throw new ModKitException(x.A.Pos[0] + " has no entry " + x.A.Pos[1]);
            byte[] b = arc.Read(e);
            using (Stream so = Console.OpenStandardOutput()) so.Write(b, 0, b.Length);
            return 0;
        }

        static int Rows(Ctx x)
        {
            M2dArchive arc = x.Data.Open(x.Need(0, "<archive>"));
            string entry = x.Need(1, "<entry>");
            M2dEntry e = arc.Find(entry);
            if (e == null) throw new ModKitException(x.A.Pos[0] + " has no entry " + entry);
            XmlDoc doc = XmlDoc.Parse(arc.Read(e), entry);
            if (doc.Root == null) throw new ModKitException(entry + " has no element");
            XmlElem from = x.A.Pos.Count > 2 ? RowPath.Parse(x.A.Pos[2]).Resolve(doc.Root) : doc.Root;
            var o = new StringBuilder();
            foreach (XmlElem ch in from.Children)
                o.Append(RowPath.Canonical(ch)).Append("\tline ").Append(ch.Line).Append('\t').Append(ch.Attrs.Count).Append(" attributes, ").Append(ch.Children.Count).Append(" children\n");
            Console.Write(o);
            return 0;
        }

        static int Schema(Ctx x)
        {
            string sub = x.Need(0, "build or show");
            if (sub == "build")
            {
                var names = new List<string>();
                for (int i = 1; i < x.A.Pos.Count; i++) names.Add(DataSet.NormalizeRef(x.A.Pos[i]));
                if (names.Count == 0)
                    foreach (string d in new[] { "client:Xml.m2d", "gms-client:Xml.m2d", "server:Xml.m2d", "server:Server.m2d" })
                        if (x.Data.Roots.Contains(DataSet.RootOf(d)) && x.Data.Exists(d)) names.Add(d);
                if (names.Count == 0) throw new ModKitException("no archive to read");
                foreach (string n in names)
                {
                    var set = new SchemaSet();
                    var sw = Stopwatch.StartNew();
                    ArchiveSchema s = set.Build(x.Data, n, x.Progress);
                    set.Save(x.SchemaDir);
                    Console.WriteLine(n + ": " + s.Tables.Count + " tables from " + s.Entries + " entries (" + s.Skipped + " skipped) in " + sw.ElapsedMilliseconds + " ms -> " + Path.Combine(x.SchemaDir, SchemaSet.FileNameOf(n)));
                }
                return 0;
            }
            if (sub != "show") throw new UsageException("schema build or schema show");
            string arc = DataSet.NormalizeRef(x.Need(1, "<archive>"));
            ArchiveSchema a = x.Schemas().Archive(arc);
            if (a == null) throw new ModKitException("no schema is cached for " + arc + " in " + x.SchemaDir + "; run: modkit schema build " + arc);
            if (a.Stale) Console.Error.WriteLine("modkit: the archive has changed since this schema was built; run modkit schema build " + arc);
            var o = new StringBuilder();
            if (x.A.Pos.Count < 3)
            {
                foreach (TableSchema t in a.Tables)
                    o.Append(t.Name).Append('\t').Append(t.Files).Append(t.Files == 1 ? " file" : " files").Append("\troot ").Append(t.Root)
                     .Append(t.Row.Length > 0 ? "\trow " + t.Row : "").Append(t.Key.Length > 0 ? "\tkey " + t.Key : "").Append(t.Variants.Length > 0 ? "\tvariants " + t.Variants : "").Append('\n');
                Console.Write(o);
                return 0;
            }
            TableSchema tb = a.Table(x.A.Pos[2]) ?? a.Table(SchemaSet.TableOf(x.A.Pos[2]));
            if (tb == null) throw new ModKitException("the schema of " + arc + " has no table " + x.A.Pos[2] + " (modkit schema show " + arc + " lists them)");
            o.Append(tb.Name).Append(": ").Append(tb.Files).Append(" file(s), root <").Append(tb.Root).Append(">, row <").Append(tb.Row).Append(">, key ").Append(tb.Key.Length > 0 ? tb.Key : "(none)")
             .Append(tb.Variants.Length > 0 ? ", variants " + tb.Variants : "").Append('\n');
            foreach (ElemSchema el in tb.Elements)
            {
                o.Append(el.Path).Append("  x").Append(el.Count).Append(el.HasText ? "  has text" : "").Append('\n');
                foreach (AttrSchema at in el.Attrs)
                    o.Append("    @").Append(at.Name).Append("  x").Append(at.Count).Append("  ").Append(at.TypeText).Append("  e.g. ").Append(string.Join(" | ", at.Samples.Take(4).Select(v => "\"" + v + "\""))).Append('\n');
            }
            Console.Write(o);
            return 0;
        }

        // ---- mods ----

        static void PrintPlan(Ctx x, Plan p, bool changes)
        {
            var o = new StringBuilder();
            if (changes) foreach (PlanChange c in p.Changes) o.Append(c).Append('\n');
            foreach (PlanMessage m in p.Notes) o.Append("note: ").Append(m).Append('\n');
            foreach (PlanMessage m in p.Warnings) o.Append("warning: ").Append(m).Append('\n');
            foreach (PlanMessage m in p.Conflicts) o.Append(m.ConflictLine()).Append('\n');
            foreach (PlanMessage m in p.Errors) o.Append("error: ").Append(m).Append('\n');
            if (p.Conflicts.Count > 0 && p.Mods.Count > 1) o.Append("priority, the highest first: ").Append(ModOrder.Text(p.Mods)).Append('\n');
            int entries = 0;
            foreach (PlanArchive a in p.Archives) entries += a.Put.Count + a.Delete.Count;
            o.Append(p.Mods.Count).Append(" mod(s): ").Append(p.Changes.Count).Append(" change(s) in ").Append(entries).Append(" entr").Append(entries == 1 ? "y" : "ies").Append(" of ")
             .Append(p.Archives.Count).Append(" archive(s); ").Append(p.Errors.Count).Append(" error(s), ").Append(p.Conflicts.Count).Append(" conflict(s), ").Append(p.Warnings.Count).Append(" warning(s)\n");
            o.Append(Targets.Line(x.Data)).Append('\n');
            foreach (string l in Targets.PlanLines(p, x.Data)) o.Append("  ").Append(l).Append('\n');
            Console.Write(o);
        }

        static object Msgs(List<PlanMessage> l) { return l.Select(m => new { mod = m.ModId, archive = m.Archive, entry = m.Entry, text = m.Text }).ToArray(); }

        static string TargetOf(string archive) { return archive == null || archive.IndexOf(':') <= 0 ? null : Targets.NameOf(DataSet.RootOf(DataSet.NormalizeRef(archive))); }

        static object PlanJson(Plan p)
        {
            return new
            {
                mods = p.Mods.Select(m => new { id = m.Id, name = m.Name, version = m.Version, dir = m.Dir }).ToArray(),
                priority = p.Mods.Select(m => m.Id).Reverse().ToArray(),      // the highest first
                changes = p.Changes.Select(c => new { kind = c.Kind.ToString(), mod = c.ModId, target = TargetOf(c.Archive), synced = c.Synced, mirrored = c.Mirrored, archive = c.Archive, entry = c.Entry, row = c.Row, name = c.Name, old = c.OldValue, @new = c.NewValue }).ToArray(),
                errors = Msgs(p.Errors), conflicts = p.Conflicts.Select(m => new { mod = m.ModId, archive = m.Archive, entry = m.Entry, text = m.Text, winner = m.WinnerId }).ToArray(), warnings = Msgs(p.Warnings), notes = Msgs(p.Notes),
                archives = p.Archives.Select(a => new { archive = a.Archive, put = a.Put.Select(k => k.Key).ToArray(), delete = a.Delete.ToArray() }).ToArray(),
            };
        }

        static void WriteJson(object o) { Console.WriteLine(JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })); }

        static int PlanCmd(Ctx x, bool changes)
        {
            Plan p = Planner.Build(x.Data, x.Mods(0), x.PlanOpt(true));
            ServerStep step = x.Step();
            if (step.Enabled && !x.A.Has("-w")) p.Note("--server-target and --after-apply need -w <workspace>: the server's archives are written there first");
            else foreach (string line in step.Describe(p, x.WsDir)) p.Note(line);
            if (x.Json) WriteJson(PlanJson(p)); else PrintPlan(x, p, changes);
            return p.HasErrors || p.Undecided > 0 || (p.HasConflicts && !x.A.Has("--allow-conflicts")) ? 1 : 0;
        }

        static object ResultJson(ApplyResult r)
        {
            return new
            {
                applied = r.Applied, reason = r.Reason, plan = PlanJson(r.Plan),
                outputs = r.Outputs.Select(o => new { target = TargetOf(o.Archive), archive = o.Archive, path = o.Path, header = o.HeaderPath, kept = o.Kept, replaced = o.Replaced, added = o.Added, deleted = o.Deleted, copied = o.Copied, verified = o.Verified, ms = o.Milliseconds }).ToArray(),
                removed = r.Removed.ToArray(), written = r.Written.ToArray(), left = r.Left.ToArray(),
                server = r.Server == null ? null : new { copied = r.Server.Copied.ToArray(), restored = r.Server.Restored.ToArray(), command = r.Server.Command, workDir = r.Server.WorkDir, ran = r.Server.Ran, exitCode = r.Server.ExitCode, output = r.Server.Output, problem = r.Server.Problem },
            };
        }

        static int PrintResult(Ctx x, ApplyResult r, long ms)
        {
            int code = r.Server != null && !r.Server.Ok ? 3 : 0;
            if (x.Json) { WriteJson(ResultJson(r)); return r.Applied ? code : 1; }
            PrintPlan(x, r.Plan, false);
            if (!r.Applied) { Console.WriteLine("nothing written: " + r.Reason); return 1; }
            foreach (ApplyOutput o in r.Outputs)
                Console.WriteLine(o.Copied
                    ? o.Archive + " -> " + o.Path + " (copied as it is)"
                    : o.Archive + " -> " + o.Path + ": " + o.Replaced + " replaced, " + o.Added + " added, " + o.Deleted + " deleted, " + o.Kept + " kept as stored; read back " + o.Verified + "; " + o.Milliseconds + " ms");
            foreach (string f in r.Written) Console.WriteLine("wrote " + f);
            foreach (string f in r.Removed) Console.WriteLine("removed " + f);
            foreach (string f in r.Left) Console.WriteLine("left as it is (changed since it was written): " + f);
            Console.WriteLine("per target:");
            foreach (string l in Targets.ResultLines(r, x.Data, x.A.Has("-w") ? x.Ws().Dir : null)) Console.WriteLine("  " + l);
            Console.Write(ServerStep.Report(r.Server).Replace("\r\n", "\n"));
            Console.WriteLine("done in " + ms + " ms");
            return code;
        }

        // the server's side after an apply or a revert that went through; says on stderr what it is about to do
        static ServerStepResult Follow(Ctx x, ApplyResult r)
        {
            ServerStep step = x.Step();
            if (!step.Enabled || r == null) return null;
            if (!x.A.Has("-w")) throw new UsageException("--server-target and --after-apply need -w <workspace>");
            bool changed = r.Outputs.Any(o => !LooseTarget.Serves(o.Archive));
            foreach (string root in x.Data.Roots.Where(n => n != LooseTarget.Root))
                changed |= r.Removed.Any(f => f.StartsWith(System.IO.Path.Combine(x.WsDir, root) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            {
                if (step.HasTarget) Console.Error.WriteLine("modkit: copying the server's archives to " + System.IO.Path.GetFullPath(step.TargetDir));
                if (step.HasCommand && changed) Console.Error.WriteLine("modkit: then running: " + step.Command.Trim());
            }
            return step.Run(x.Data, x.WsDir, changed);
        }

        static int Apply(Ctx x)
        {
            var opt = new ApplyOptions { AllowConflicts = x.A.Has("--allow-conflicts"), VerifyAll = x.A.Has("--verify-all"), Schemas = x.Schemas(), Progress = x.Progress, Maps = x.Maps(), NoSync = x.A.Has("--no-sync") };
            opt.AlsoCopy.AddRange(x.A.AlsoCopy);
            var sw = Stopwatch.StartNew();
            LooseTarget game = x.Game();
            if (x.Step().Enabled && !x.A.Has("-w")) throw new UsageException("--server-target and --after-apply need -w <workspace>");
            if (game == null)
            {
                ApplyResult only = x.Ws().Apply(x.Data, x.Mods(0), opt);
                if (only.Applied) only.Server = Follow(x, only);
                return PrintResult(x, only, sw.ElapsedMilliseconds);
            }

            // the client's part as plain files; what the mods change in other roots goes to the workspace, if one is given
            if (opt.AlsoCopy.Count > 0) throw new UsageException("--also-copy goes with -w, not with --game");
            List<Mod> mods = x.Mods(0);
            Plan plan = Planner.Build(x.Data, mods, opt.PlanOptions());
            bool other = plan.Archives.Any(pa => !LooseTarget.Serves(pa.Archive));
            if (other && !x.A.Has("-w"))
                Console.Error.WriteLine("modkit: the mods also change archives that are not the client's; those are not written without -w <workspace>");
            ApplyResult r = game.Apply(mods, plan, opt);
            if (r.Applied && x.A.Has("-w"))
            {
                Workspace ws = x.Ws();
                if (other || ws.ReadState() != null)
                {
                    opt.Roots = x.Data.Roots.Where(root => root != LooseTarget.Root).ToList();     // gms-client and server
                    ApplyResult w = ws.Apply(x.Data, mods, plan, opt);
                    r.Outputs.AddRange(w.Outputs);
                    r.Removed.AddRange(w.Removed);
                }
            }
            if (r.Applied) r.Server = Follow(x, r);
            return PrintResult(x, r, sw.ElapsedMilliseconds);
        }

        static int Revert(Ctx x)
        {
            LooseTarget game = x.Game();
            if (game != null)
            {
                if (x.A.Pos.Count > 0) throw new UsageException("revert --game removes every file apply --game wrote; to drop one mod, apply --game again without it");
                ApplyResult g = game.Revert();
                if (x.A.Has("-w") && x.Ws().ReadState() != null)
                {
                    ApplyResult w = x.Ws().Revert(x.Data, null, null);
                    if (w != null) g.Removed.AddRange(w.Removed);
                }
                if (x.A.Has("-w")) g.Server = Follow(x, g);
                if (x.Json) WriteJson(new { applied = true, removedAll = true, removed = g.Removed.ToArray(), left = g.Left.ToArray() });
                else
                {
                    foreach (string f in g.Removed) Console.WriteLine("removed " + f);
                    foreach (string f in g.Left) Console.WriteLine("left as it is (changed since it was written): " + f);
                    Console.WriteLine("the loose files are removed from " + game.GameDir);
                    Console.Write(ServerStep.Report(g.Server).Replace("\r\n", "\n"));
                }
                return g.Server != null && !g.Server.Ok ? 3 : 0;
            }
            Workspace ws = x.Ws();
            var opt = new ApplyOptions { AllowConflicts = x.A.Has("--allow-conflicts"), VerifyAll = x.A.Has("--verify-all"), Schemas = x.Schemas(), Progress = x.Progress, Maps = x.Maps(), NoSync = x.A.Has("--no-sync") };
            var sw = Stopwatch.StartNew();
            ApplyResult r = ws.Revert(x.Data, x.A.Pos.Count > 0 ? x.A.Pos[0] : null, opt);
            if (r != null && (r.Plan == null || r.Applied)) r.Server = Follow(x, r);
            if (r == null || r.Plan == null)
            {
                if (x.Json) WriteJson(new { applied = true, removedAll = true, removed = r == null ? new string[0] : r.Removed.ToArray() });
                else
                {
                    if (r != null) foreach (string l in Targets.ResultLines(r, x.Data, ws.Dir)) Console.WriteLine(l);
                    Console.WriteLine("no mod is left: the copies are removed from " + ws.Dir);
                    if (r != null) Console.Write(ServerStep.Report(r.Server).Replace("\r\n", "\n"));
                }
                return r != null && r.Server != null && !r.Server.Ok ? 3 : 0;
            }
            return PrintResult(x, r, sw.ElapsedMilliseconds);
        }

        static int Status(Ctx x)
        {
            LooseTarget game = x.Game();
            if (game != null)
            {
                LooseState ls = game.ReadState();
                if (ls == null) Console.WriteLine("no loose files of this tool in " + game.GameDir);
                else
                {
                    Console.WriteLine("applied " + ls.Applied + " (loose files in " + game.GameDir + ")");
                    foreach (WorkspaceMod m in ls.Mods) Console.WriteLine("mod " + m.Id + " " + m.Version + "\t" + m.Path);
                    foreach (LooseFile f in ls.Files) Console.WriteLine("file " + f.Archive + " " + f.Entry + "\t" + game.FullPath(f.Path) + (File.Exists(game.FullPath(f.Path)) ? "" : "\tMISSING"));
                }
                if (!x.A.Has("-w")) return 0;
            }
            Workspace ws = x.Ws();
            WorkspaceState st = ws.ReadState();
            if (st == null) { Console.WriteLine("nothing is applied in " + ws.Dir); return 0; }
            Console.WriteLine("applied " + st.Applied);
            foreach (WorkspaceMod m in st.Mods) Console.WriteLine("mod " + m.Id + " " + m.Version + "\t" + m.Path);
            foreach (WorkspaceArchive a in st.Archives)
                Console.WriteLine("archive " + a.Archive + "\t" + Path.Combine(ws.Dir, a.File) + (a.Copied ? "\t(copied as it is)" : "") + (File.Exists(Path.Combine(ws.Dir, a.File)) ? "" : "\tMISSING"));
            return 0;
        }

        // modkit sync <id>... [mod...]: numbers are ids, the rest are mods
        static int Sync(Ctx x)
        {
            var ids = new List<long>();
            var mods = new List<Mod>();
            foreach (string a in x.A.Pos)
            {
                long id;
                if (long.TryParse(a, out id)) ids.Add(id); else mods.Add(Mod.Load(a));
            }
            if (ids.Count == 0) throw new UsageException("sync <id>... [mod...]");
            EntityMap em = (x.Maps() ?? FieldMaps.Default).Entity("item");
            if (em == null) throw new ModKitException("the field maps hold no entity \"item\"");
            Plan plan = mods.Count == 0 ? null : Planner.Build(x.Data, mods, x.PlanOpt(false));
            if (plan != null && plan.HasErrors) { PrintPlan(x, plan, false); return 1; }
            foreach (long id in ids)
            {
                SyncReport r = SyncReport.ForId(x.Data, plan, em, id);
                Console.Write(r.ToText(x.A.Has("--all")).Replace("\r\n", "\n"));
            }
            return 0;
        }

        static int Refs(Ctx x)
        {
            EntityMap em = (x.Maps() ?? FieldMaps.Default).Entity("item");
            if (em == null) throw new ModKitException("the field maps hold no entity \"item\"");
            if (x.A.Pos.Count == 0) throw new UsageException("refs <id>... [mod...]");
            var ids = new List<long>();
            var mods = new List<Mod>();
            foreach (string a in x.A.Pos)
            {
                long id;
                if (long.TryParse(a, out id)) ids.Add(id); else mods.Add(Mod.Load(a));
            }
            if (ids.Count == 0) throw new UsageException("refs <id>... [mod...]");
            // with mods: the tables as the mods leave them
            Plan plan = mods.Count == 0 ? null : Planner.Build(x.Data, mods, x.PlanOpt(false));
            if (plan != null && plan.HasErrors) { PrintPlan(x, plan, false); return 1; }
            foreach (long id in ids)
            {
                Console.WriteLine("item " + id + (plan != null ? " (after the mods):" : ":"));
                Console.Write(ServerRefs.Find(x.Data, em, id, 50, plan).ToText().Replace("\r\n", "\n"));
            }
            return 0;
        }

        static int NextId(Ctx x)
        {
            Mod m = Mod.Load(x.Need(0, "<mod>"));
            int count = 1;
            if (x.A.Pos.Count > 2 && (!int.TryParse(x.A.Pos[2], out count) || count < 1)) throw new UsageException("bad count " + x.A.Pos[2]);
            foreach (long id in IdSpaces.NextFree(x.Data, m, x.Need(1, "<space>"), count, x.Progress)) Console.WriteLine(id);
            return 0;
        }
    }
}
