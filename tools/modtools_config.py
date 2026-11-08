#!/usr/bin/env python3
"""modtools_config.py: where your own game data, SDKs and server live.

MS2-ModTools ships none of these, so every tool asks here.  Each setting comes from, in order:

  1. the environment variable of the same name
  2. modtools.toml in the repo root (gitignored; copy modtools.example.toml)

Relative paths in modtools.toml are relative to the repo root.

    python3 tools/modtools_config.py              # show every setting and whether its path exists
    python3 tools/modtools_config.py MS2_OUT      # print one value (exit 1 if it isn't set); for shell scripts
"""
import os, subprocess, sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
CONFIG_FILE = REPO / "modtools.toml"

SETTINGS = {
    "MS2_CLIENT_DATA": "client Data/ folder (holds Xml.m2d and Resource/), yours",
    "MS2_SERVER_DATA": "server data folder (holds Xml.m2d and Server.m2d, the MS2_DATA_FOLDER your server uses)",
    "MS2_SERVER_DIR": "Maple2 server checkout (ingest build, Navmeshes/, .env); only for the navmesh step",
    "MS2_DOTNET_ROOT": ".NET install to prefer over the dotnet on PATH (optional)",
    "MS2_TOKBUILD": "tokbuild.exe (default: build/tokbuild/tokbuild.exe, see tools/tok/README.md)",
    "MS2_CP3D": "your ContentProcessing3D.dll (32-bit, PathEngine interface 18)",
    "MS2_PATHENGINE_SDK": "your PathEngine SDK root, only to compile tokbuild",
    "MS2_MSVC_ROOT": "msvc-wine install, only to cross-compile tokbuild on Linux",
    "MS2_GAMEBRYO_APPDIR": "your Gamebryo LightSpeed SDK APPDIR (tools/mapedit, Toolbench)",
    "MS2_TRIGGER_CACHE": "xblock index cache of the trigger linter (default: <MS2_OUT>/trigger-cache)",
    "MS2_OUT": "where outputs go when no -o is given (default: <repo>/out)",
}


def _file_values():
    if not CONFIG_FILE.is_file():
        return {}
    import tomllib
    try:
        return tomllib.loads(CONFIG_FILE.read_text(encoding="utf-8"))
    except tomllib.TOMLDecodeError as e:
        raise SystemExit(f"{CONFIG_FILE}: {e}")


def get(name):
    """The setting's value as a string, or None if it isn't set."""
    if name not in SETTINGS:
        raise KeyError(name)
    v = os.environ.get(name)
    if v:
        return v
    v = _file_values().get(name)
    if not v:
        return None
    return str(v if os.path.isabs(v) else REPO / v)


def missing_message(name, why):
    return (f"{name} is not set: {why} needs it ({SETTINGS[name]}). "
            f"Set the environment variable or add {name} = \"...\" to modtools.toml (see modtools.example.toml).")


def require(name, why):
    """The setting as a Path; stops with a message naming the setting if it isn't set or doesn't exist."""
    v = get(name)
    if not v:
        raise SystemExit(missing_message(name, why))
    p = Path(v)
    if not p.exists():
        raise SystemExit(f"{name} = {v}: no such path ({why} needs it: {SETTINGS[name]})")
    return p


def out_dir():
    return Path(get("MS2_OUT") or REPO / "out")


def trigger_cache():
    return Path(get("MS2_TRIGGER_CACHE") or out_dir() / "trigger-cache")


def dotnet_env():
    """Environment for running dotnet: MS2_DOTNET_ROOT first if set; net8 apps may roll forward to a newer runtime."""
    env = dict(os.environ)
    d = get("MS2_DOTNET_ROOT")
    if d:
        env["DOTNET_ROOT"] = d
        env["PATH"] = f"{d}{os.pathsep}{env.get('PATH', '')}"
    env.setdefault("DOTNET_ROLL_FORWARD", "Major")
    return env


M2D_DLL = REPO / "tools/m2d/bin/m2d.dll"


def ensure_m2d():
    """tools/m2d/bin/m2d.dll, built with `dotnet build` on first use."""
    if M2D_DLL.is_file():
        return M2D_DLL
    print("building tools/m2d (first run only)", file=sys.stderr, flush=True)
    src = M2D_DLL.parent.parent
    try:
        r = subprocess.run(["dotnet", "build", str(src), "-c", "Release", "-o", str(M2D_DLL.parent)],
                           capture_output=True, text=True, env=dotnet_env())
    except FileNotFoundError:
        raise SystemExit("dotnet not found: tools/m2d needs a .NET SDK on PATH (or MS2_DOTNET_ROOT)")
    if r.returncode or not M2D_DLL.is_file():
        raise SystemExit(f"dotnet build tools/m2d failed:\n{r.stdout[-3000:]}{r.stderr[-2000:]}")
    return M2D_DLL


def main():
    if len(sys.argv) > 2:
        raise SystemExit(__doc__)
    if len(sys.argv) == 2:
        v = get(sys.argv[1]) if sys.argv[1] in SETTINGS else None
        if v is None:
            return 1
        print(v)
        return 0
    for name, what in SETTINGS.items():
        v = get(name)
        state = "not set" if not v else ("ok" if os.path.exists(v) else "MISSING PATH")
        print(f"{name:20} {state:13} {v or ''}\n    {what}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
