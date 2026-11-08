# Notice

MS2-ModTools is an unofficial fan project. It is not affiliated with or endorsed by Nexon. MapleStory2 is a trademark of Nexon.

This repository contains no game assets, executables or engine source, and no PathEngine, Gamebryo, PhysX or Toolbench
binaries or headers. You supply your own copies; the tools read them from where you keep them.

- `glue/pathengine/` holds recovered client code, a frozen copy from [MS2Recomp](https://github.com/riyukuro/MS2Recomp).
- `tools/trigger/` and `tools/mapedit/known_guids.json` hold short identifiers from the shipped data (script paths, state
  names, model names, GUIDs, usage counts), because the lint rules and the converters check against them.
- `tools/m2d` and `tools/tok/extract` download the NuGet package `Maple2.File.Parser.Tadeucci` when built (GPL-3.0-or-later,
  <https://github.com/AngeloTadeucci/Maple2.File>). It is not included here.
