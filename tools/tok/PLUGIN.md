# .tok plugin for Gamebryo Toolbench / World Builder: design

Status: the design is implemented in `tools/tok/plugin/`.
- Runtime tested on 2026-11-07/08: Win10 VM, .NET 3.5, Toolbench 3.2.0.661, on the HelloWorld sample and on a
  shipped MS2 map rebuilt as a block (see §5.3).

## 1. Host: Gamebryo LightSpeed 3.2.0.661 Toolbench + World Builder

Your own Gamebryo SDK (`MS2_GAMEBRYO_APPDIR`, stock 3.2.0.661) includes the
editor that MS2 maps were authored in:

- **Prebuilt:** `APPDIR/sdk/Win32/Bin/VC90/Shipping/ToolbenchWindowed.exe` (GUI) and `ToolbenchConsole.exe` (batch).
  Both are PE32 i386 .NET assemblies built for CLR v2.0.50727 (.NET 3.5, `PlatformTarget x86`, large-address-aware).
  The World Builder plugins and the native Gamebryo DLLs (`sdk/Win32/DLL/*32VC90S.dll`) sit next to them.
- **Source:** the full C# source is in `APPDIR/Source/Tools/Toolbench` (Platform, `Plugins/Emergent.Toolbench.WorldBuilder*`).
  VS2008 csproj files, ToolsVersion 3.5, x86.
- **It is MS2's editor.** World Builder's runtime exporter is `XmlBlockExporter`
  (`Plugins/Emergent.Toolbench.WorldBuilder/Exporters/XmlBlockExporter.cs`). Its `FileExtension()` returns `"xblock"`,
  and it writes `<game><entitySet><entity id modelName name>…` from a `.block` document plus its `.layer` files. That is the
  map format MS2 ships, the one the extractor (Maple2.File.Parser `XBlockParser`) reads. The entity models are LightSpeed
  entity models: `.model` files at design time, `.flat` at runtime. MS2's `physxmodellibrary` and `standardmodellibrary`
  carry the names of the stock Gamebryo `Media/PhysXModelLibrary` and `StandardModelLibrary` (contents not diffed). `maplestory2library` is Nexon's own
  library, and it already carries a PathEngine mixin: the extractor filters on
  `IMS2PathEngineTOK.doesMakeTOK`. Nexon ran the .tok input selection from this editor's model data.
- **32-bit host.** Toolbench is x86, so in principle it could load the 32-bit CP3D DLL in its own process. We still keep
  the DLL out of the process (§3).
- Not used: the 3ds Max/Maya/XSI art plugins and the `Source/Tools/ToolPlugins` (NIF asset-pipeline DLLs). Those work
  on single assets, not maps.

## 2. Plugin API (what a Toolbench plugin is)

A plugin is a folder `Plugins/<id>/` holding `plugin.xml` plus a .NET 3.5 assembly. `plugin.xml` names a `PluginBase`
subclass, `<requires><import plugin=…/>` dependencies, and `<extension point=…>` entries. Extension points we can use:

| Extension point | Use |
|-|-|
| `Emergent.Toolbench.WorldBuilder.Exporter` (`IWorldBuilderExporter`: `FileExtension()`, `List<Uri> Export(BlockDocument, Uri folder)`) | a runtime exporter |
| `BlockDocument.Exporting` / `Exported` events (C# events, `BlockExportEventArgs`: export path + exporter) | run after every export |
| Actions plus `menuitem` (an `IActionHandler.Execute`, as `DemoWorldAction` does: export the active block, then `Process.Start` an external app) | a "Build ground (.tok)" menu command |
| `Emergent.Toolbench.Settings.SettingsSet` (`ISettingService.GetSetting<T>`) | per-user paths: CP3D DLL, tokbuild.exe, extractor |
| `Emergent.Toolbench.Runnable` (`IRunnable.Run(Arguments)`, as `ContentExporter/CommandLineExporter`) | headless batch through ToolbenchConsole |
| `OutputView.OutputChannel` / `Logger` | build log |

**Stock bug: the exporter setting does not select an exporter.** `BlockDocument.Export(Uri)`
(`Documents/BlockDocument.cs`) reads the per-block "Run-time Exporter" setting, but a wrong null test makes it always fall
back to XmlBlockExporter. A custom `IWorldBuilderExporter` therefore only runs when we call `block.Export(ourExporter, path)`
ourselves. The plugin uses the `Exported` event and its own action instead. We don't patch the shipped DLL.

**Builds on Linux:** the plugin is a minimal SDK-style csproj (`TargetFramework net35`, x86,
`Microsoft.NETFramework.ReferenceAssemblies`). It references the shipped `Emergent.Toolbench.dll` and
`Emergent.Toolbench.WorldBuilder.dll` by HintPath with `Private=false`. It subclasses `XmlBlockExporter` and
`PluginBase`, and `dotnet build` (SDK 10.0.114) compiles it in 2.5 s with 0 warnings. The output targets CLR v2.0.50727.
No VS2008 is needed.

**Runtime host: Windows with .NET 3.5 (NetFx3), or Wine through `tools/run-toolbench.sh`.** Under plain Wine with
wine-mono the platform dies in native code in the mixed-mode C++/CLI `efd.NetService` ctor (Framework plugin).
`tools/run-toolbench.sh` works around that (a CLR4 shim `ToolbenchLauncher.exe` plus dotnet48 in the prefix). The tests
below ran on Win10 with NetFx3 enabled, where ToolbenchWindowed runs from the stock tree when `EMERGENT_PATH` is set to
the SDK root and `sdk/Win32/DLL` is on PATH.

Facts learned on Windows:
- **Opening content.** A `.block` or `.emtproj` given on the command line is refused ("can not be opened directly").
  Open the `.gsl` game solution instead, then the block from the Game Solution tree.
- **Plugins start lazily.** `PluginBase.Start()` runs on first use, not at launch.
- **Action sets need a mapping.** An action set (and its menu items) is shown only if a
  `Emergent.Toolbench.Workbench.PerspectiveMapping` extension maps it to
  `Emergent.Toolbench.WorldBuilder.UI.Gamebryo.Perspective`.
- **Headless runs need the solution.** A runnable must open the `.gsl` first (`-solution`, as `CommandLineExporter`
  does); a block opened without it is rejected with "A loaded Game Solution is needed".
- **Settings file.** Per-user settings are stored in
  `%LOCALAPPDATA%\Emergent Game Technologies\Toolbench\1.0\Toolbench.Settings.xml`. ToolbenchConsole does not see
  them (they load empty), so the headless runnable takes every setting as an argument.
- **Console working folder.** ToolbenchConsole changes to its own folder (`Shipping`), so give `-solution` as a full
  path. A relative `-block` is looked up in the solution's folder.

## 3. Recommended design: export, then build in an external process

```
World Builder (x86, CLR2)                         external processes (no PathEngine in Toolbench)
 ─────────────────────────                        ─────────────────────────────────────────────────
 [Build ground (.tok)] action / Exported event
   1. block.Export() → <Exported>/<map>.xblock     (stock XmlBlockExporter, unchanged)
   2. Process.Start  tokgeom  <ResourceDir> <map>.xblock <outPrefix> [--project <dir>]  → .geom/.solids   (extractor)
   3. Process.Start  tokbuild build --dll <user CP3D> --geom … --solids … --out <map>.tok      (the 32-bit helper)
   4. optional: IoU/face report into the Output view; optional m2d repack for testing
```

Why external rather than in-tool geometry:

- **Same code path as the batch tool.** The extractor and `tokbuild` are what we measure IoU with on shipped
  maps. The plugin is then a thin launcher, and a map built in the editor matches a map built from the command line.
- **Licensing and crash isolation.** No PathEngine code or header is linked into the plugin assembly. The CP3D DLL is
  loaded only by `tokbuild.exe` (32-bit, BYO, built only when `MS2_PATHENGINE_SDK` is set). A CP3D crash doesn't take
  the editor down.
- **In-tool geometry costs more and gains nothing.** World Builder's scene is a native ecr/egf scene behind mixed-mode
  C++/CLI. Reading PhysX shapes there means a new C++/CLI assembly built with VC90 against the Gamebryo 3.2 Win32 libs and
  the PhysX 2.8 SDK. We found no editor-side NxScene to read the way the client's
  `BuildCP3DSnapshotFromPhysX` does (not searched exhaustively). Byte identity (actor order) is out of scope anyway. The
  xblock plus the .flat/.nif files hold the same inputs: positions, PhysX mesh/convex/cube shapes, the `doesMakeTOK`
  flag and the MapProperties cubes.
- **CLR2 cannot host the extractor.** The extractor is modern .NET (Maple2.File.Parser, System.Numerics) and cannot run
  inside the CLR 2 Toolbench process, so it has to be a separate exe.

Plugin contents (`tools/tok/plugin/`, C#, net35; `TokPlugin.cs` is about 480 lines):

- `plugin.xml`: requires `Emergent.Toolbench.Settings`, `…ContentService`, `…WorldBuilder` and `…WorldBuilder.UI`. It
  contributes one action and menuitem, "World Builder → Build PathEngine ground (.tok)", a SettingsSet (including an
  optional "build after export" setting that subscribes to `BlockDocument.Exported`), and one Runnable `TokBuild` for
  headless runs through ToolbenchConsole (the same mechanism as `CommandLineExporter`):
  `-run TokBuild -solution <file.gsl> -block <file.block> [-cp3d <dll>] [-tokbuild <exe>] [-tokgeom <exe>]
  [-tokgeomargs <args>] [-resource <dir>] [-project <dir>] [-out <dir>] [-tokbuildargs <args>]`; the optional ones
  override the settings.
- Settings (per user), 9 in all: `CP3DDll`, `TokbuildExe`, `TokgeomExe`, `TokgeomArgs` (default
  `"{resource}" "{xblock}" "{out}" --project "{project}"`), `ResourceDir`, `ProjectDir` (empty = the block's folder),
  `OutputFolder` (empty = the block's Exported folder), `TokbuildArgs`, `BuildAfterExport` (default false). All others
  are empty by default.
- If `CP3DDll`, `TokbuildExe` or `TokgeomExe` is empty, the action logs "MS2 .tok: not configured, nothing built. Set
  (Settings > MS2 .tok): <the missing ones>" and does nothing. The plugin itself has no PathEngine dependency.

## 4. Gating, licensing, build

- Built with the .NET SDK (`dotnet build tools/tok/plugin -p:ToolbenchDir=<dir>`). The plugin has no PathEngine code and
  does not need `MS2_PATHENGINE_SDK`; only `tokbuild`, a separate program, does. `ToolbenchDir` is the folder of your own
  Gamebryo Toolbench holding `Emergent.Toolbench.dll`, for the reference assemblies. Those are referenced in place and never copied into the repository or the plugin output (`Private=false`).
- Install: copy `plugin.xml` and the dll into `<user Toolbench>/Plugins/MS2.Tok/`. The repository ships no Toolbench or
  Gamebryo binary.
- PathEngine stays BYO: the user's `ContentProcessing3D.dll` is used only through its exports, by
  `tokbuild`, and only by path. No SDK header text goes into this repository (tokbuild includes the user's SDK in place).

## 5. Dependencies and blockers

1. **Extractor input from a Toolbench project: done.** tokgeom takes `--project <dir>`; loose `.nif` files there take
   precedence over the client archives. It still needs the client's `Exported.m2d` in the resource folder (it says so
   and exits 1 when it is missing).
2. **MS2 `.model` / `.emtproj` library: done** (`tools/mapedit/flat2model.py`). The SDK has no flat→model converter
   (export is one-way, `ExportFlatAction`); flat2model regenerates the design-time library from the shipped flats
   (238 `.model`, 13,127 presets), and `mklayout.py` / `mkgsl` turn a shipped map into a `.block` plus a `.gsl`
   solution that World Builder opens.
3. **Windows runtime test (done 2026-11-07, Win10 VM).**
   - The plugin loads. Its settings page shows all its entries (8 then; `ProjectDir`, the 9th, came later).
   - With empty settings, the menu action warns "not configured" and builds nothing.
   - End to end in the GUI, with the extractor step replaced by copying a known .geom/.solids:
     World Builder → Build PathEngine ground (.tok) wrote a `.tok` byte-identical to a direct `tokbuild` run
     (516 faces, 616 connections).
   - Headless `ToolbenchConsole -run TokBuild -solution … -block …` builds nothing on the HelloWorld sample. The stock
     `-run ContentExporter -solution … -export …` fails on the same sample ("Error with content (skipped)"), so the
     console content loading itself looks at fault. In the console, Logger output goes nowhere; a runnable must use
     `Console.WriteLine` to report anything.
   - **Real MS2 map, GUI (2026-11-07):** `02000001_tw_tria` rebuilt with flat2model + mkgsl opens in World Builder
     (15,186 entities). Build PathEngine ground (.tok) ran with 0 errors: tokgeom read 14,875 entities (192/204 nifs;
     the missing ones are in Effect/Item/Npc.m2d, not copied to the VM) into 25,574 verts / 40,340 faces / 11,001 solids;
     tokbuild built 13,916 faces, 2,074 end points, 2,960 off-mesh connections (549,901 bytes, 35 s). The .geom/.solids
     equal a direct tokgeom run.
   - **Real MS2 map, headless (2026-11-08):** `ToolbenchConsole -run TokBuild`
     with all settings as arguments exports, extracts and writes the `.tok` (13,195 faces, 2,075 end points, 2,962
     connections; the change from the GUI run is tokgeom's new rotation order). Open issues:
     - the process did not exit after "wrote …tok" (100% CPU, killed after 300 s): `CloseContent` in
       `RunCore`'s `finally` hung. The plugin now skips `CloseContent`: 3 runs exit by themselves with rc 0
       (55–63 s), and a wrong `-cp3d` (tokbuild exit 2) exits with rc 1 after 23 s;
     - the headless export does not rewrite an existing `.xblock`, because the freshly opened block is unchanged
       (touching the `.block` file does not change that); the plugin warns and uses the old one. With the `.xblock`
       deleted first, the headless export writes it, byte-identical to the GUI export.
4. **Quality.** The plugin adds no geometric error of its own. IoU depends on the extractor (ellinia 1.000 2D /
   0.998 3D, see README.md).
