#!/usr/bin/env python3
"""build.py: one command from a spec to a ready-to-test custom dungeon.

    python3 tools/dungeon/build.py <spec.json|spec.toml> [-o <outdir>]     (default <MS2_OUT>/dungeon/<xblock>)

Steps, in order (each one is recorded in <outdir>/BUILD-MANIFEST.json):

  layout    only with a spec "layout": the map's xblock from the layout (mkxblock.py), used as xblock_file
  newmap    all data rows, triggers, the asset-web-metadata triples, xblock (newmap.py); this includes the minimap
            (minimap.py), because the <minimap> camera goes into the mapxblock xmls
  lint      tools/trigger/lint.py --strict on the spec's trigger_dir; errors stop the build.  Triggers copied from
            the reference are linted without --strict and only reported
  tok       tokgeom -> .geom/.solids -> tokbuild -> .tok when the spec has a new xblock (xblock_file) and tools/tok is
            configured; otherwise the reference's .tok stays, with a note
  pack      repacked archive copies (pack.py); originals are only read
  verify    every new or replaced entry reads back byte-identical from the repacked archive
  navmesh   server navmesh from a scratch ingest (--run-navmesh) of packed/server-data, on a copy of the ingest and of
            Navmeshes/ and a scratch data DB that is dropped afterwards.  It comes after pack because the ingest
            reads the packed folder.  Skipped with a note if the ingest or the DB can't be reached

Results in <outdir>: packed/ (archives), navmesh/ (server navmesh and its hash), BUILD-MANIFEST.json (what was generated
vs copied, every archive entry added or replaced), INSTALL.txt (where each piece goes), logs/.

Spec keys beyond newmap.py's (see newmap.py -h):
  tok   "auto" (default; build when there is an xblock_file) | "build" (always; fails if not configured) | "copy"

tools/tok configuration (bring your own PathEngine, see tools/tok/README.md):
  --tokbuild / MS2_TOKBUILD     tokbuild.exe (default build/tokbuild/tokbuild.exe)
  --cp3d / MS2_CP3D            your ContentProcessing3D.dll

All settings are in tools/modtools_config.py (environment or modtools.toml).  MS2_SERVER_DIR is your Maple2 server
checkout, for the navmesh step only.

Exit code: 0 built; 1 a step failed (see the step's note in the manifest).
"""
import argparse, json, os, shutil, socket, subprocess, sys, time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from newmap import Gen, REPO, SpecError, cfg, load_spec, dotnet_env, m2d, M2D_DLL  # noqa: E402
from pack import TARGET  # noqa: E402

LINT = REPO / "tools/trigger/lint.py"
XBINDEX = REPO / "tools/trigger/xblockindex.py"
SCRATCH_PREFIX = "maple-data-dbuild-"
INGEST_TIMEOUT = 45 * 60


class Stop(Exception):
    """A step failed; the manifest is still written."""


class Build:
    def __init__(self, spec_path, spec, out, args):
        self.spec_path, self.spec, self.out, self.args = spec_path, spec, out, args
        self.xb = str(spec.get("xblock", "")).lower()
        self.mid = spec.get("map_id")
        self.steps = []
        self.origin = {}      # (area, entry) -> origin text
        self.archives = {}
        self.navmesh = None
        self.gen = None
        self.logs = out / "logs"

    def step(self, name, fn):
        t0 = time.time()
        rec = {"step": name, "status": "ok", "notes": []}
        self.steps.append(rec)
        print(f"== {name}", flush=True)
        try:
            fn(rec)
        except Stop as e:
            rec["status"] = "failed"
            rec["notes"].append(str(e))
            raise
        finally:
            rec["seconds"] = round(time.time() - t0, 1)
            for n in rec["notes"]:
                print(f"   {name}: {n}", flush=True)

    # -- layout
    def layout(self, rec):
        import mkxblock
        if self.spec.get("xblock_file"):
            raise Stop('spec: "layout" and "xblock_file" both give the xblock; keep one')
        dst = self.out / "layout" / f"{self.xb}.xblock"
        try:
            n, kinds = mkxblock.generate(self.spec, dst)
        except (SpecError, SystemExit) as e:
            raise Stop(str(e))
        self.spec["xblock_file"] = str(dst)
        rec["notes"].append(f"{dst.name}: {n} entities, " + ", ".join(f"{k} {v}" for k, v in sorted(kinds.items())))

    # -- newmap
    def newmap(self, rec):
        for area in ("client-xml", "server-xml", "server", "exported", "terrain", "image", "awm"):
            shutil.rmtree(self.out / area, ignore_errors=True)
        try:
            g = Gen(self.spec, self.out, self.args.refresh)
            ok = g.run()
        except SpecError as e:
            raise Stop(f"spec: {e}")
        except SystemExit as e:
            raise Stop(str(e))
        self.gen = g
        rec["notes"] += g.notes
        if not ok:
            raise Stop("; ".join(g.errors))
        sp = self.spec
        trig = "copied from spec trigger_dir" if sp.get("trigger_dir") else "copied from the reference's triggers"
        mm = g.minimap_mode()
        image = {"generate": "generated by minimap.py" if g.minimap else "copied from the reference (generation failed)",
                 "reference": "copied from the reference", "file": "copied from spec minimap_file"}.get(mm, "?")
        for area, names in g.written.items():
            for n in names:
                if n.startswith("trigger/"):
                    o = trig
                elif area == "exported":
                    o = ("generated by mkxblock.py" if sp.get("layout") else
                         "copied from spec xblock_file" if sp.get("xblock_file") else "copied from the reference")
                elif area == "terrain":
                    o = "copied from spec tok_file" if sp.get("tok_file") else "copied from the reference"
                elif area == "image":
                    o = image
                else:
                    o = "generated (reference table + cloned rows)"
                self.origin[(area, n)] = o
        if mm == "generate" and not g.minimap:
            rec["notes"].append("WARNING: minimap generation failed; see the note above")

    # -- lint
    def lint(self, rec):
        tdir = self.out / "client-xml/trigger" / self.xb
        if not tdir.is_dir():
            rec["status"] = "skipped"
            rec["notes"].append("no triggers")
            return
        cache = cfg.trigger_cache()
        if not (cache / "models.json").exists():
            rec["notes"].append(f"building the xblock index in {cache} (first run only)")
            r = subprocess.run([sys.executable, str(XBINDEX), "build", "--exported", str(self.gen.sources["exported"]),
                                "--cache", str(cache)], capture_output=True, text=True, env=dotnet_env())
            if r.returncode:
                raise Stop(f"xblockindex build failed: {r.stderr[-500:]}")
        custom = bool(self.spec.get("trigger_dir"))
        self.trigger_models(rec, cache, tdir)
        cmd = [sys.executable, str(LINT), str(tdir), "--root", str(self.out / "client-xml"), "--cache", str(cache),
               "--xblock", str(self.out / "exported/xblock" / f"{self.xb}.xblock"), "-q"]
        if custom:
            cmd.append("--strict")
        r = subprocess.run(cmd, capture_output=True, text=True)
        self.logs.mkdir(exist_ok=True)
        (self.logs / "lint.log").write_text(r.stdout + r.stderr)
        sumline = next((l for l in r.stderr.splitlines() if l.startswith("errors:")), "?")
        warn = next((l for l in r.stderr.splitlines() if l.startswith("warnings:")), "")
        rec["notes"].append(f"{'--strict' if custom else 'copied triggers, not strict'}: {sumline}, {warn}")
        if r.returncode:
            if custom:
                print(r.stdout)
                raise Stop(f"trigger lint: {sumline} (logs/lint.log)")
            rec["notes"].append("copied triggers have lint errors; not stopping (logs/lint.log)")

    def trigger_models(self, rec, cache, tdir):
        """The server runs one script per TriggerModel in the xblock, by file name; a model without a script only logs."""
        xbf = self.out / "exported/xblock" / f"{self.xb}.xblock"
        r = subprocess.run([sys.executable, str(XBINDEX), "show", str(xbf), "--cache", str(cache)],
                           capture_output=True, text=True)
        models = {l.split()[1].lower() for l in r.stdout.splitlines() if l.startswith("trigger_model ") and "->" in l}
        scripts = {p.stem.lower() for p in tdir.glob("*.xml")}
        if models - scripts:
            rec["notes"].append(f"xblock trigger models with no script (they don't run): {', '.join(sorted(models - scripts))}")

    # -- tok
    def tok_config(self):
        a = self.args
        tb = a.tokbuild or cfg.get("MS2_TOKBUILD")
        if not tb and (REPO / "build/tokbuild/tokbuild.exe").is_file():
            tb = str(REPO / "build/tokbuild/tokbuild.exe")
        cp = a.cp3d or cfg.get("MS2_CP3D")
        if not tb or not Path(tb).is_file():
            return None, None, "tokbuild.exe not found (--tokbuild or MS2_TOKBUILD; build it per tools/tok/README.md)"
        if not cp or not Path(cp).is_file():
            return None, None, "ContentProcessing3D.dll not found (--cp3d or MS2_CP3D)"
        return tb, cp, None

    def tok(self, rec):
        sp = self.spec
        mode = sp.get("tok", "auto")
        if mode not in ("auto", "build", "copy"):
            raise Stop(f'spec tok: "auto", "build" or "copy", not {mode!r}')
        if sp.get("tok_file"):
            rec["notes"].append("using spec tok_file")
            return
        if mode == "copy" or (mode == "auto" and not sp.get("xblock_file")):
            rec["notes"].append("the xblock is the reference's, so its .tok is copied too" if mode == "auto"
                                else 'spec "tok": "copy": the reference\'s .tok is copied')
            return
        tb, cp, why = self.tok_config()
        if why:
            if mode == "build":
                raise Stop(why)
            rec["status"] = "copied"
            rec["notes"].append(f"WARNING: new xblock but tools/tok is not configured ({why}): the reference's .tok "
                                "was copied and will NOT match the new geometry")
            return
        import minimap
        work = self.out / "tok-work"
        work.mkdir(exist_ok=True)
        stem = work / self.xb
        xbf = self.out / "exported/xblock" / f"{self.xb}.xblock"
        try:
            geom, solids = minimap.run_tokgeom(self.gen.sources["exported"].parent, xbf, stem, minimap.default_cache(),
                                               sp.get("minimap_project"))
        except SystemExit as e:
            raise Stop(str(e))
        tok = work / f"{self.xb}.tok"
        cmd = ([] if os.name == "nt" else ["wine"]) + [tb, "build", "--dll", cp, "--geom", str(geom), "--solids", str(solids),
                                                       "--out", str(tok), "--faces", str(work / f"{self.xb}.faces")]
        env = {k: v for k, v in os.environ.items() if k not in ("DISPLAY", "WAYLAND_DISPLAY")}  # no window
        env["WINEDEBUG"] = "-all"
        self.logs.mkdir(exist_ok=True)
        with open(self.logs / "tokbuild.log", "w") as lf:
            r = subprocess.run(cmd, stdout=lf, stderr=subprocess.STDOUT, env=env)
        if r.returncode or not tok.is_file() or tok.stat().st_size == 0:
            raise Stop(f"tokbuild failed (rc {r.returncode}, logs/tokbuild.log); spec \"tok\": \"copy\" skips this")
        dst = self.out / "terrain" / f"{self.xb}.tok"
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(tok, dst)
        names = self.gen.written.setdefault("terrain", [])
        if dst.name not in names:
            names.append(dst.name)
        self.origin[("terrain", dst.name)] = "generated by tokgeom + tokbuild"
        man = json.loads((self.out / "manifest.json").read_text())
        man["written"] = self.gen.written
        (self.out / "manifest.json").write_text(json.dumps(man, indent=1, ensure_ascii=False))
        rec["notes"].append(f"built {dst.name} ({dst.stat().st_size} bytes) with {Path(tb).name} and {Path(cp).name}")

    # -- pack
    def pack(self, rec):
        pdir = self.out / "packed"
        shutil.rmtree(pdir, ignore_errors=True)
        self.logs.mkdir(exist_ok=True)
        with open(self.logs / "pack.log", "w") as lf:
            r = subprocess.run([sys.executable, str(HERE / "pack.py"), str(self.out)], stdout=lf, stderr=subprocess.STDOUT)
        if r.returncode:
            raise Stop(f"pack.py failed (rc {r.returncode}, logs/pack.log)")

    # -- verify
    def verify(self, rec):
        man = json.loads((self.out / "manifest.json").read_text())
        bad = []
        env = dotnet_env()
        for area, names in man["written"].items():
            if not names:
                continue
            packed = self.out / "packed" / TARGET[area]
            orig = man["archives"][area]
            before = m2d("list", orig).split()
            after = m2d("list", packed).split()
            low_before, low_after = {n.lower() for n in before}, {n.lower() for n in after}
            added = [n for n in names if n.lower() not in low_before]
            replaced = [n for n in names if n.lower() in low_before]
            missing = [n for n in names if n.lower() not in low_after]
            if missing:
                bad.append(f"{area}: missing after repack: {missing}")
            if len(after) != len(before) + len(added):
                bad.append(f"{area}: {len(before)} + {len(added)} added != {len(after)} entries")
            mismatch = []
            for n in names:
                if n in missing:
                    continue
                r = subprocess.run(["dotnet", str(M2D_DLL), "cat", str(packed), n], capture_output=True, env=env)
                if r.returncode or r.stdout != (self.out / area / n).read_bytes():
                    mismatch.append(n)
            if mismatch:
                bad.append(f"{area}: read back differs: {mismatch}")
            self.archives[area] = {"source": orig, "packed": str(packed.relative_to(self.out)),
                                   "entries_before": len(before), "entries_after": len(after),
                                   "added": added, "replaced": replaced,
                                   "readback": "byte-identical" if not (missing or mismatch) else "FAILED"}
            rec["notes"].append(f"{area}: {len(before)} -> {len(after)} entries, {len(added)} added, "
                                f"{len(replaced)} replaced, {self.archives[area]['readback']}")
        # the client's world lookup: the new triples must read back from the repacked archive
        aw = man.get("asset_web")
        if aw:
            packed = self.out / "packed" / TARGET["awm"]
            for t in aw["triples"]:
                r = subprocess.run(["dotnet", str(M2D_DLL), "cat", str(packed), t["file"]], capture_output=True, env=env)
                if t["triple"].encode() not in r.stdout.split(b"\n"):
                    bad.append(f"awm: {t['file']} has no {t['triple']}")
            rec["notes"].append(f"asset-web-metadata: {len(aw['triples'])} triples under {aw['urn']}")
        if bad:
            raise Stop("; ".join(bad))

    # -- navmesh
    def db_env(self, server):
        env = {}
        p = server / ".env"
        if p.is_file():
            for line in p.read_text().splitlines():
                if "=" in line and not line.lstrip().startswith("#"):
                    k, v = line.split("=", 1)
                    env[k.strip()] = v.strip()
        return env

    def sql(self, db, stmt):
        """Run one statement in the MySQL container; returns stdout, or None if that is not possible."""
        names = subprocess.run(["docker", "ps", "--filter", "ancestor=mysql:8.0", "--format", "{{.Names}}"],
                               capture_output=True, text=True)
        if names.returncode or not names.stdout.strip():
            return None
        env = dict(os.environ, MYSQL_PWD=db.get("DB_PASSWORD", ""))
        r = subprocess.run(["docker", "exec", "-e", "MYSQL_PWD", names.stdout.split()[0], "mysql",
                            f"-u{db.get('DB_USER', 'root')}", "-N", "-e", stmt], capture_output=True, text=True, env=env)
        return r.stdout if r.returncode == 0 else None

    def run_navmesh(self, rec):
        def skip(why):
            rec["status"] = "skipped"
            rec["notes"].append(f"{why}: no navmesh built; INSTALL.txt says how to make one")
        if self.args.no_navmesh:
            return skip("--no-navmesh")
        server = cfg.get("MS2_SERVER_DIR")
        if not server:
            return skip("MS2_SERVER_DIR is not set (your Maple2 server checkout)")
        server = Path(server)
        ingest = server / "Maple2.File.Ingest/bin/Release/net8.0"
        navsrc = server / "Maple2.Server.Game/Navmeshes"
        d = cfg.get("MS2_DOTNET_ROOT")
        found = Path(d) / "dotnet" if d else shutil.which("dotnet")
        if not found:
            return skip("no dotnet on PATH (or set MS2_DOTNET_ROOT)")
        real_dotnet = Path(found).resolve()
        if not (ingest / "Maple2.File.Ingest.dll").is_file():
            return skip(f"no ingest build at {ingest}")
        if not navsrc.is_dir():
            return skip(f"no {navsrc}")
        if not real_dotnet.is_file():
            return skip(f"no {real_dotnet}")
        db = self.db_env(server)
        try:
            socket.create_connection((db.get("DB_IP", "localhost"), int(db.get("DB_PORT", 3306))), timeout=5).close()
        except OSError as e:
            return skip(f"DB not reachable ({e})")
        if not db.get("DB_PASSWORD"):
            return skip(f"no DB_PASSWORD in {server}/.env")
        work = self.out / "navmesh-work"
        shutil.rmtree(work, ignore_errors=True)
        S = work / "S"
        bin_dir = S / "Maple2.File.Ingest/bin/Release/net8.0"
        shutil.copytree(ingest, bin_dir)
        (S / "Maple2.Server.World").mkdir(parents=True)
        navdir = S / "Maple2.Server.Game/Navmeshes"
        shutil.copytree(navsrc, navdir)
        before = {p.name for p in navdir.glob("*.navmesh")}
        scratch = f"{SCRATCH_PREFIX}{self.mid}"
        dotenv = {"MS2_DATA_FOLDER": f"{(self.out / 'packed/server-data').resolve()}/", "LANGUAGE": "EN",
                  "DATA_DB_NAME": scratch, "GAME_DB_NAME": scratch,
                  **{k: db[k] for k in ("DB_IP", "DB_PORT", "DB_USER", "DB_PASSWORD") if k in db}}
        (S / ".env").write_text("".join(f"{k}={v}\n" for k, v in dotenv.items()))
        shim = work / "shim"
        shim.mkdir()
        # the ingest runs `dotnet ef database update` on the game DB and, if that fails, `dotnet tool install --global`
        (shim / "dotnet").write_text(f'#!/bin/sh\ncase "$1" in ef|tool) echo "shim: skip dotnet $*"; exit 0;; esac\n'
                                     f'exec "{real_dotnet}" "$@"\n')
        (shim / "dotnet").chmod(0o755)
        env = dict(os.environ, DOTNET_ROOT=str(real_dotnet.parent), PATH=f"{shim}:{os.environ['PATH']}",
                   DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1", DOTNET_ROLL_FORWARD="Major")
        self.logs.mkdir(exist_ok=True)
        try:
            with open(self.logs / "ingest.log", "w") as lf:
                r = subprocess.run(["dotnet", "Maple2.File.Ingest.dll", "--run-navmesh"], cwd=bin_dir, env=env,
                                   stdout=lf, stderr=subprocess.STDOUT, timeout=INGEST_TIMEOUT)
            if r.returncode:
                raise Stop(f"scratch ingest failed (rc {r.returncode}, logs/ingest.log)")
            built = sorted(p.name for p in navdir.glob("*.navmesh") if p.name not in before)
            mine = next((n for n in built if n.lower() == f"{self.xb}.navmesh"), None)
            if not mine:
                raise Stop(f"ingest finished but built no {self.xb}.navmesh (new files: {built}; logs/ingest.log)")
            dst = self.out / "navmesh"
            shutil.rmtree(dst, ignore_errors=True)
            (dst / "Hashes").mkdir(parents=True)
            shutil.copyfile(navdir / mine, dst / mine)
            hash_name = f"{Path(mine).stem}-hash"
            if (navdir / "Hashes" / hash_name).is_file():
                shutil.copyfile(navdir / "Hashes" / hash_name, dst / "Hashes" / hash_name)
            self.navmesh = {"file": f"navmesh/{mine}", "bytes": (dst / mine).stat().st_size, "other_built": [n for n in built if n != mine]}
            rec["notes"].append(f"built {mine} ({self.navmesh['bytes']} bytes)")
            if self.navmesh["other_built"]:
                rec["notes"].append(f"the ingest also built {len(built) - 1} navmesh(es) the server tree lacks; not kept")
            n = self.sql(db, f"select count(*) from `{scratch}`.`map` where Id={int(self.mid)}")
            if n is not None:
                rec["notes"].append(f"scratch DB: {n.strip()} map row(s) for {self.mid}")
                self.navmesh["scratch_map_rows"] = n.strip()
                if n.strip() != "1":
                    raise Stop(f"the scratch ingest has {n.strip()} map rows for {self.mid}, expected 1")
        finally:
            if self.args.keep_db:
                rec["notes"].append(f"scratch DB kept: {scratch}")
            elif scratch.startswith(SCRATCH_PREFIX):
                if self.sql(db, f"drop database if exists `{scratch}`") is None:
                    rec["notes"].append(f"WARNING: could not drop the scratch DB {scratch}; drop it by hand")
                else:
                    rec["notes"].append(f"scratch DB {scratch} dropped")
            if not self.args.keep_work:
                shutil.rmtree(work, ignore_errors=True)

    # -- results
    def write_manifest(self):
        g = self.gen
        files = [{"area": a, "entry": n, "origin": o} for (a, n), o in sorted(self.origin.items())]
        man = {"map_id": self.mid, "xblock": self.xb, "clone_from": self.spec.get("clone_from"),
               "reference_xblock": getattr(g, "refxb", None), "spec": str(self.spec_path),
               "steps": self.steps, "files": files, "archives": self.archives, "navmesh": self.navmesh,
               "asset_web": getattr(g, "asset_web_info", None), "install": "INSTALL.txt"}
        (self.out / "BUILD-MANIFEST.json").write_text(json.dumps(man, indent=1, ensure_ascii=False))

    def write_install(self):
        o = self.out.resolve()
        arch = [TARGET[a] for a in self.archives if TARGET[a].startswith("client/")]
        lines = [f"Install {self.xb} (map {self.mid}). Paths below are from this build; originals were not touched.", "",
                 "1. Client: put these into a client COPY's Data/ (never over your original install). Each has its .m2h next to it.",
                 "   Symlinks are enough:"]
        for rel in arch:
            sub = rel[len("client/Data/"):]
            lines.append(f"     ln -sf {o}/packed/{rel} <client copy>/Data/{sub}")
            lines.append(f"     ln -sf {o}/packed/{Path(rel).with_suffix('.m2h')} <client copy>/Data/{Path(sub).with_suffix('.m2h')}")
        lines += ["   Use a client built from MS2Recomp: the stock exe stops at field entry on an empty 확인 box.", "",
                  f"2. Server data: {o}/packed/server-data/ is a whole MS2_DATA_FOLDER. Point MS2_DATA_FOLDER at it (with a",
                  "   trailing /) and run the ingest. To keep the shared server as it is, use a scratch DB and a private",
                  "   stack instead (tools/dungeon/README.md \"Server\" and \"Client\").", "",
                  "3. Navmesh: the game server loads it from its bin dir, Navigation.cs:",
                  "   AppDomain.CurrentDomain.BaseDirectory/Navmeshes/<xblock>.navmesh (not Paths.NAVMESH_DIR). If it is missing,",
                  "   the warp fails with nothing in the log."]
        if self.navmesh:
            nm = Path(self.navmesh["file"]).name
            lines += [f"     cp {o}/{self.navmesh['file']} <Maple2.Server.Game>/bin/Release/net8.0/Navmeshes/",
                      "   And so that a later --run-navmesh ingest treats it as up to date (Paths.NAVMESH_DIR):",
                      f"     cp {o}/{self.navmesh['file']} <Maple2.Server.Game>/Navmeshes/",
                      f"     cp {o}/navmesh/Hashes/{Path(nm).stem}-hash <Maple2.Server.Game>/Navmeshes/Hashes/"]
        else:
            lines += ["   This build made none. Run the ingest with --run-navmesh (tools/dungeon/README.md \"Server\"), then copy",
                      f"   Maple2.Server.Game/Navmeshes/{self.xb}.navmesh into the bin dir above."]
        lines += ["", "4. Restart the game server, then warp there:", f"     warp {self.mid}", "",
                  "See BUILD-MANIFEST.json for what was generated vs copied and every archive entry added.", ""]
        (self.out / "INSTALL.txt").write_text("\n".join(lines))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("spec")
    ap.add_argument("-o", "--out", help="output folder (default <MS2_OUT>/dungeon/<xblock>)")
    ap.add_argument("--refresh", action="store_true", help="re-read the reference archives")
    ap.add_argument("--tokbuild", help="tokbuild.exe")
    ap.add_argument("--cp3d", help="your ContentProcessing3D.dll")
    ap.add_argument("--no-navmesh", action="store_true", help="skip the scratch ingest and the navmesh")
    ap.add_argument("--keep-db", action="store_true", help="keep the scratch data DB")
    ap.add_argument("--keep-work", action="store_true", help="keep navmesh-work/ (scratch ingest and Navmeshes copy)")
    a = ap.parse_args()
    try:
        spec = load_spec(a.spec)
    except SpecError as e:
        print(f"error: {e}", file=sys.stderr)
        return 1
    if not spec.get("xblock"):
        print("error: spec: missing required key 'xblock'", file=sys.stderr)
        return 1
    # absolute: the scratch ingest runs from another cwd, and a relative shim on PATH would let it reach the real dotnet
    out = (Path(a.out) if a.out else cfg.out_dir() / "dungeon" / str(spec["xblock"]).lower()).resolve()
    out.mkdir(parents=True, exist_ok=True)
    (out / "spec.json").write_text(json.dumps(spec, indent=1, ensure_ascii=False))
    b = Build(Path(a.spec).resolve(), spec, out, a)
    rc = 0
    try:
        if spec.get("layout"):
            b.step("layout", b.layout)
        b.step("newmap", b.newmap)
        b.step("lint", b.lint)
        b.step("tok", b.tok)
        b.step("pack", b.pack)
        b.step("verify", b.verify)
        b.step("navmesh", b.run_navmesh)
        b.write_install()
    except Stop as e:
        print(f"error: {e}", file=sys.stderr)
        rc = 1
    b.write_manifest()
    print(f"{'built' if rc == 0 else 'FAILED'}: {out}  ({', '.join(s['step'] + '=' + s['status'] for s in b.steps)})")
    return rc


if __name__ == "__main__":
    sys.exit(main())
