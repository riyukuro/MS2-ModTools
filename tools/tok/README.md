# tools/tok: PathEngine ground meshes (.tok) for MS2 maps

MS2 keeps one PathEngine ground mesh per map in `Data/Resource/PrecomputedTerrain.m2d` (`<mapid>_<name>.tok`). These
tools rebuild such a mesh for a map, so that custom maps can have one:

1. `extract/` (`tokgeom`, see [extract/README.md](extract/README.md)): map geometry -> geometry dump (`.geom` +
   `.solids`, spec in [FORMAT.md](FORMAT.md)).
2. `builder/` (`tokbuild`): dump -> `.tok`, with the PathEngine content processing DLL and the client's own settings.
3. `tools/m2d repack`: puts the `.tok` into `PrecomputedTerrain.m2d`.

`plugin/` runs steps 1 and 2 from the Gamebryo map editor (Toolbench / World Builder); [PLUGIN.md](PLUGIN.md) describes its
design, implementation status and test results.

## Bring your own PathEngine

**Nothing PathEngine ships with this repository**: no DLL, no SDK header, no SDK source. PathEngine is commercial
software. To build and run `tokbuild` you supply:

- **`ContentProcessing3D.dll`** from your own MapleStory2 (GMS2) client install. It is the only content-processing DLL
  MS2 shipped: 32-bit, PathEngine release 5.34, iPathEngine interface 18.0, content processing interface 4.0. The x64
  clients have none, which is why `tokbuild` is a 32-bit program.
- **PathEngine SDK headers** with interface 18 (`code/externalAPI/i_pathengine.h`), from your own SDK. They are included
  in place from where you keep them; the build writes only small forwarding headers into its build directory.

`tokbuild` uses the DLL only through its exported interfaces.

## Building tokbuild

Settings: `MS2_PATHENGINE_SDK`, and on Linux `MS2_MSVC_ROOT` (an [msvc-wine](https://github.com/mstorsjo/msvc-wine) install of
MSVC 14.16 + Windows SDK 10.0.19041; override with `MS2_MSVC_VC_VER`, `MS2_WINSDK_VER`). Pass them in the environment or with `-D`.
Configure stops with an error naming whichever is missing. Output: `build/tokbuild/tokbuild.exe`, where `build.py` looks by default.

```sh
# Linux: clang-cl + lld-link, against your msvc-wine install
cmake -G Ninja -S tools/tok/builder -B build/tokbuild -DCMAKE_BUILD_TYPE=Release \
      -DCMAKE_TOOLCHAIN_FILE=$PWD/cmake/toolchain-clangcl-x86.cmake -DMS2_MSVC_ROOT=<msvc-wine dir> -DMS2_PATHENGINE_SDK=<your SDK root>
cmake --build build/tokbuild
# Windows / Visual Studio (not tested yet):
cmake -S tools/tok/builder -B build/tokbuild -A Win32 -DMS2_PATHENGINE_SDK=<your SDK root>
cmake --build build/tokbuild --config Release
```

The program is `tools/tok/builder/tokbuild.cpp` plus the client's recovered `cBlockConnections`
(`glue/pathengine/blockconnections.cpp`, a frozen copy from [MS2Recomp](https://github.com/riyukuro/MS2Recomp), compiled unchanged). The CRT is linked statically.

## Running tokbuild

On Linux, run it with Wine (`wine tokbuild.exe ...`); Windows paths (`Z:\...`) and Unix paths both work for `--dll`.

```sh
tokbuild info  --dll <ContentProcessing3D.dll> [--in <map.tok>]
tokbuild build --dll <ContentProcessing3D.dll> --geom <map.geom> [--geom <more.geom>] --solids <map.solids> \
               --out <map.tok> [--faces <map.faces>] [--no-connections] [--option <name>=<value>]
tokbuild reconnect --dll <ContentProcessing3D.dll> --in <in.tok> --out <out.tok>
```

`build` does what the client's (unused) `BuildAndSaveGround` does: `buildGround` with extrude height 128, max step
145, max slope `tan(1.553343) * 100`, options `method=bsp` and `excludeDownwardFacingFromGroundResult=true`, no
optimisation; then the client's block connections (off-mesh connections between the 150-unit block layers); then
`saveGround("tok")`. `--faces` also writes one text line per 3D face (vertices and connections) for comparisons.
`reconnect` drops a .tok's off-mesh connections and reruns the block connections; on the shipped 5.34 toks this
gives back the shipped file byte for byte.

Then into the client archive (`tools/m2d`, .NET):

```sh
mkdir src && cp <map.tok> src/<mapid>_<name>.tok
m2d repack <client>/Data/Resource/PrecomputedTerrain.m2d src <out>/PrecomputedTerrain.m2d   # also writes the .m2h
```

`repack` replaces entries whose names match files in `src` (`--add` adds new map entries) and copies the rest unchanged.

## Toolbench plugin (plugin/)

`MS2.Tok` is a .NET 3.5 x86 plugin for Gamebryo LightSpeed 3.2 Toolbench / World Builder. It is a thin launcher: it
exports the active block with the stock xblock exporter, runs the extractor, then runs `tokbuild` with your
`ContentProcessing3D.dll`. It contains no PathEngine code and does not load the DLL itself. It needs your own Toolbench
(its assemblies are referenced in place, never copied) and the .NET SDK.

```sh
dotnet build tools/tok/plugin -c Release -p:ToolbenchDir=<Gamebryo SDK>/sdk/Win32/Bin/VC90/Shipping -o <out>
```

Install: copy `MS2.Tok.dll` and `plugin.xml` into `<Toolbench>/Plugins/MS2.Tok/`. Settings (per user, page "MS2 .tok"),
all empty by default; while the DLL, tokbuild or extractor path is empty the plugin builds nothing and logs what is missing:

| Setting | Meaning |
|-|-|
| ContentProcessing3D.dll | your own DLL (passed to `tokbuild --dll`) |
| tokbuild.exe, Extractor (tokgeom) | the two programs |
| Extractor arguments | default `"{resource}" "{xblock}" "{out}" --project "{project}"` |
| Resource folder | `{resource}`, e.g. a client's `Data/Resource` |
| Project folder | `{project}`, the Toolbench project whose loose `.nif` files override the client's (default: the block's folder) |
| Output folder | where `<block>.geom/.solids/.tok` go (default: the block's export folder) |
| Extra tokbuild arguments | appended to `tokbuild build` (e.g. `--faces "{out}.faces"`) |
| Build .tok after export | also build whenever a block is exported |

Placeholders: `{resource}`, `{xblock}` (the exported file), `{out}` (output path without extension), `{project}` (the
project folder), `{block}` (block name). Use: World Builder menu > "Build PathEngine ground (.tok)"; output goes to the
Output view. Headless (messages go to the console):

```
ToolbenchConsole -run TokBuild -solution <file.gsl> -block <file.block> -cp3d <dll> -tokbuild <exe> -tokgeom <exe>
                 [-resource <dir>] [-project <dir>] [-out <dir>] [-tokgeomargs <args>] [-tokbuildargs <args>]
```

ToolbenchConsole does not see the per-user settings, so the headless run takes them as arguments. Each argument
overrides the matching setting, and an empty extractor-arguments setting falls back to the default. Give `-solution`
as a full path (ToolbenchConsole runs in its own folder); a relative `-block` is looked up in the solution's folder.
The run ends the ToolbenchConsole process with its exit code (0 = built); ToolbenchConsole does not exit by itself
after opening content.

The block's export does not rewrite an `.xblock` that is already there (seen both in the editor and headless); the
plugin then warns and uses the existing file. If the block changed outside this Toolbench session, delete the old
`.xblock` first (touching the `.block` is not enough); the export then writes it again, headless too.

A self-contained `tokgeom.exe` built without `CETCompat false` can stop on a Windows 10 without
current updates with "Your Windows doesn't fully support CET" (0x80131506); rebuild it from this tree, or run
`Set-ProcessMitigation -Name tokgeom.exe -Disable UserShadowStack` (PowerShell, as administrator).

## Status (2026-11-08)

- 02000305_bf rebuilds the shipped ground (516 faces, 616 connections, walkable-area IoU 1.000). With tokgeom's current
  rules ellinia is at IoU 1.000 (2D) / 0.998 (3D), and the median over 24 built maps is 0.997 (3D); details in
  [extract/README.md](extract/README.md).
- `tokbuild` is linked `/LARGEADDRESSAWARE` (4 GB on 64-bit Windows/Wine): 82000000_survival (154061 input faces,
  32518 solids) ran out of address space in `buildGround` without it; with it, it builds in about 90 s.
- The plugin works in Toolbench 3.2 on Windows 10 (.NET 3.5), in the editor and headless: it built
  02000001_tw_tria from its .block (15186 entities) with the real tokgeom (13916 faces, 2960 connections, 35 s), and the
  editor and headless .geom/.solids/.tok matched each other and a direct tokgeom run. Wine cannot host Toolbench.
- Not byte-identical to shipped toks in general: triangulation order follows input order, and the client's input order
  is PhysX actor order. The quality bar is walkable-area IoU.
