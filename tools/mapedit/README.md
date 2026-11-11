# tools/mapedit: convert and edit shipped maps

Only exported runtime maps ship (`.xblock` in `Resource/Exported.m2d`, entity models as `.flat`). These tools convert them back to
the design-time formats of Gamebryo Toolbench / World Builder, and back again. Needs `lxml`. Start by extracting the archives
with [tools/m2d](../m2d/README.md). Run the tools from the repo root: `out/maps/extracted` is the default location of the extracted
archives. Most tools also need the model index, built once from your extracted `flat/` files:

```sh
python3 tools/mapedit/flatindex.py out/maps/extracted/Exported out/maps/analysis/flatindex.json
```

| tool | what |
|---|---|
| `xblock2block.py` | `.xblock` -> editable `.block` |
| `block2xblock.py` | `.block` -> `.xblock` |
| `normalize_xblock.py` | puts an exported xblock in the shipped file's order (`--original`), so diffs show only real edits |
| `move_entity.py` | scripted edit: moves one entity in a `.block` |
| `roundtrip.py` | xblock -> block -> xblock check over maps |
| `flat2model.py` | `.flat` -> `.model` / `.preset` / `.emtproj` (the library Toolbench edits) |
| `knownguids.py`, `known_guids.json` | model GUIDs recovered from diagrams and the SDK (`MS2_GAMEBRYO_APPDIR`) |
| `mklayout.py` | assembles a Toolbench project root from the extracted archives |
| `lint_models.py`, `validate_models.py` | check generated models against Toolbench's load rules and the SDK's own models |
| `flatindex.py`, `msmap/` | shared code |
| `maptest.sh` | headless in-game screenshot (the header lists its settings) |

The conversions also exist in C#, in [tools/modkit](../modkit/README.md#maps), with the same output bytes and no Python or
`lxml` needed. The scripts stay here; the other tools of this folder have no C# counterpart.

| script | C# command |
|---|---|
| `flatindex.py` | `modkit map-index` |
| `xblock2block.py` | `modkit map-to-block` |
| `block2xblock.py` | `modkit map-from-block` |
| `normalize_xblock.py` | `modkit map-normalize` |

To open a shipped map in World Builder: `flat2model.py`, `mklayout.py`, then `tools/toolbench/mkgsl.py`
(see [tools/toolbench](../toolbench/README.md)).

## Editing a map and seeing it in the client

```sh
X=out/maps/extracted/Exported/xblock/<map>.xblock; W=out/edit
python3 tools/mapedit/xblock2block.py $X -o $W/block
python3 tools/mapedit/move_entity.py $W/block/<map>.block -o $W/edited.block --id <entity guid, dashed> --position "x, y, z"
python3 tools/mapedit/block2xblock.py $W/edited.block -o $W/exp
python3 tools/mapedit/normalize_xblock.py $W/exp/<map>.xblock -o $W/norm --original $X
diff $X $W/norm/<map>.xblock                     # should show only your edit
mkdir -p $W/src/xblock $W/out; cp $W/norm/<map>.xblock $W/src/xblock/
dotnet tools/m2d/bin/m2d.dll repack <client copy>/Data/Resource/Exported.m2d $W/src $W/out/Exported.m2d
cp $W/out/Exported.m2d $W/out/Exported.m2h <client copy>/Data/Resource/     # a client COPY, never your original install
```

Result of this pipeline (2026-10-13): moving one prop in `02000001_tw_tria` gave a one-line xblock diff, the repacked archive
loaded, and the prop was in its new place in the client. The client has no integrity check on `Exported.m2d`/`.m2h`. Moved
collision or walkable ground would also need a new `.tok` ([tools/tok](../tok/README.md)).
