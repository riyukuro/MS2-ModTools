# tools/dungeon: custom maps and dungeons, the data side

## Quick start

One command takes a spec to a ready-to-test custom dungeon (settings first: see the top-level README):

```
python3 tools/dungeon/build.py tools/dungeon/examples/custom-warehouse/spec.json    # -> out/dungeon/52900001_cd/
```

The output folder holds `packed/` (repacked archive copies), `navmesh/`, `INSTALL.txt` (where each piece goes),
`BUILD-MANIFEST.json` (each step's result, what was generated vs copied, every archive entry added or replaced) and
`logs/`. A build takes about 1 minute, or about 5 with the navmesh, and needs about 1.7 GB. Your originals are only read.

To make your own, copy an example under `tools/dungeon/examples/` (a spec and a `trigger/` folder):

- `custom-warehouse`: 52900001, a clone of 52000125 with a five-state trigger.
- `combat-short`: 52900002, a clone of the dungeon map 2000312 with a two-state trigger that spawns its first wave.
- `new-arena`: 52900010, a new map with its own layout and a clear-the-yard trigger; see "New map from scratch".

A trigger file's name must match a TriggerModel in the xblock (`xblockindex.py show <xblock>`): the server runs one script
per model, by file name. The build lists models that have no script. With no `trigger_dir`, the reference's triggers are copied.

What `build.py` does, in order. A failed step stops the build, and the manifest says why.

| step | what | stops the build when |
|---|---|---|
| layout | only with a spec `layout`: the map's xblock (`mkxblock.py`), used as `xblock_file` | the layout is invalid, or the spec also has `xblock_file` |
| newmap | all data rows, triggers, asset-web-metadata triples, xblock, minimap | the spec is invalid |
| lint | `tools/trigger/lint.py --strict` on your `trigger_dir` (copied triggers: reported only) | lint errors |
| tok | `.tok` from tokgeom + tokbuild when the spec has `xblock_file` and tools/tok is configured (`--tokbuild`/`MS2_TOKBUILD`, `--cp3d`/`MS2_CP3D`), otherwise the reference's copy, with a warning if the xblock is new. Spec `"tok"`: `auto`, `build`, `copy` | `"tok": "build"` without tools/tok, or tokbuild fails |
| pack | repacked copies of each touched archive | m2d fails |
| verify | every new or replaced entry reads back byte-identical; entry counts add up; the triples are in the packed archive | any mismatch |
| navmesh | a scratch ingest of `packed/server-data` (`--run-navmesh`) with copies of the ingest and of Navmeshes/; its scratch DB is dropped afterwards | the ingest fails. Skipped with a note if `MS2_SERVER_DIR` isn't set, the ingest or the DB can't be reached, or with `--no-navmesh` |

The minimap is part of newmap because its camera goes into the mapxblock xml. Navmesh comes after pack because the ingest
reads the packed folder.

Options: `-o <dir>`, `--no-navmesh`, `--keep-db`, `--keep-work`, `--refresh`. Exit code 0 means built.

**Hand work left:** the install itself (`INSTALL.txt`) and, for new geometry that a `layout` can't describe, the xblock export
from Toolbench (`xblock_file`, see below). Server and client runs are in "Server" and "Client" below. No server code changes are
needed, and none are shipped here.

## New map from scratch

A spec with a `layout` gets a map of its own instead of a copy of the reference's geometry. No Toolbench is needed:

```
MS2_TOKBUILD=<tokbuild.exe> MS2_CP3D=<ContentProcessing3D.dll> \
  python3 tools/dungeon/build.py tools/dungeon/examples/new-arena/spec.json -o <absolute out dir>
```

The new xblock needs its own `.tok`, so tokbuild has to be configured (PathEngine is bring-your-own, see tools/tok). The
build took about 4 minutes. `clone_from` still supplies the data rows (names, field settings), not the geometry. Lights and
PhysX scenes come from the `template` xblock.

All positions are grid cells `[col, row]`. A cell is 150 units, and the floor is centred on the origin, so with 15 columns cell
7 is x = 0. A cube at level L stands on z = 150 * L and its top is z = 150 * (L + 1).

| key | fields (default) | meaning |
|---|---|---|
| `floor` | `model`, `cols`, `rows`, `level` (2) | one layer of cubes |
| `perimeter` | `model`, `height` | wall on the floor's outer ring (optional) |
| `blocks` | `model`, `at`, `size` ([1, 1]), `height` (1), `level` (floor + 1) | platforms and pillars, stacked upward from `level` |
| `pc_spawn` | `at`, `facing` (0) | stands on the top cube of its cell, as do the portal and the NPCs |
| `portal` | `at`, `id`, `target_map`, `target_portal`, `facing` (0), `enabled` (false), `visible` (false), `model` (Portal_entrance) | the target portal must exist in the target map |
| `npcs` | `at`, `npc`, `count` (1), `id`, `facing` (0) | see below |
| `triggers` | `file`, `id` | one MS2TriggerModel per script; `file` is the script's name in `trigger_dir` |
| `template` | xblock name (99999911) | source of the lights and PhysX scenes |
| `seed` | any value | repeatable entity ids (default: fresh random ones) |

Cube and portal models must be shipped presets, and NPC ids must exist in the server's Xml.m2d; the build stops otherwise.
`mkxblock.py <spec> -o <out.xblock>` generates only the xblock.

Things that are easy to get wrong:

- **Trigger-spawned mobs need an `id` in `npcs`.** With one you get an EventSpawnPointNPC, which `SpawnMonster` looks up by
  that id. Without one you get a plain SpawnPointNPC, which spawns when the field starts and which triggers can't reach.
- **Keep spawns within view of `pc_spawn`,** or the first screenshot shows an empty map.
- **A portal needs the Space key.** Standing on it only shows the prompt (`Space Move - <map>`), and while it is disabled there
  is no prompt at all. `SetPortal <id> 1 1 1` in the trigger opens it.
- **Warping in reuses a running field.** A trigger that has finished stays finished: no mobs, portal open. Use
  `trigger reset <name>` to run it again.
- **`DungeonClear` does nothing after a warp.** A warp gives a plain field, and the action only works in a dungeon field.

To script input, hold keys (`xdotool keydown`, 0.3 s, `keyup`): the client missed most `xdotool key` taps.

## Custom bosses

`examples/boss-arena/` adds a boss to the new-arena layout. The spec keys `skills` and `bosses` are handled by `boss.py`
(its docstring lists every key), which newmap.py runs after the dungeon room:

- **Boss NPC**: `bosses[]` clones a shipped NPC (here Hemokan 23000039) under a new id (23009901). The clone gets its own
  name in each language, level, stats, EXP, skill list and model (`kfm` and `scale`: a shipped model, or a new one from
  `model_dir`, see below). It is written to both sides: the server `npc/23/00/23009901.xml` and the client
  `npcdata/230.xml` row. The boss HP bar at the top of the screen comes from the clone source's class 6 / rankIcon "a",
  so clone a boss and not a field mob. mkxblock.py accepts the spec's boss ids as NPC spawns.
- **Custom skill**: `skills[]` builds a new skill id (50109901) from motions of shipped skill levels:
  - The `from` value is [skill, level, motion]. Here the motions are the Hemokan charge (Attack_01_B) and slam
    (Attack_02_B).
  - The `motion`, `attack`, `range` and `damage` attributes on each motion override the shipped values (range distance
    600, targetCount 10, damage rate 2.0). `"hit": false` drops a motion's attacks (a telegraph or wind-up motion).
  - It has its own effect file. `effects` merges the `<effect>` elements of shipped effect files, with per-part scale,
    delay, duration, translate and an `only` filter. A part's `duration` also ends a looping effect (the ground guide
    circles `Eff_NPC_CircleGuide_*` loop until the motion ends otherwise).
  - Attribute names are checked against the shipped rows, so a typo fails the build instead of being ignored.
- **AI**: generated (`ai.melee` and `ai.specials`), or the spec's own file (`ai.file`, next to the spec). It is written
  to Server.m2d `AI/Custom/AI_<id>.xml`. Skill `idx` is 1-based into the boss's skill list.
- **Hit keys**: the server fires an NPC skill's attack only when the playing sequence reaches the anikeytext key named by
  `<attack point="p0">` (Attack_02_B p0 at 0.725 s, so the damage lands with the slam). boss.py checks every motion of
  the boss's skills against its kfm's anikeytext row: a missing sequence, or a missing hit key in a listed skill, stops
  the build with the list of sequences the model has.
- **New model** (`model_dir`, optional): a folder made by `tools/nif/mkmodel.py`, next to the spec or absolute. boss.py
  packs it into `Model/Npc.m2d` and `Model/Textures.m2d` `npc_custom/`, merges its anikeytext row into both Xml.m2d and
  clones the asset-web urns. See [tools/nif](../nif/README.md).
- **Trigger** (`trigger/main.xml`): the trash wave (101-103), then on MonsterDead the banner "The Maid of the Abyss
  appears!" and spawn 201 (the boss). On the boss's MonsterDead it runs SetPortal, shows "The way out is open." and
  calls DungeonClear.

## The two underlying scripts

`build.py` runs these; they can also be run alone:

```
python3 tools/dungeon/newmap.py tools/dungeon/example-spec.json -o out/mymap    # loose files + manifest.json
python3 tools/dungeon/pack.py out/mymap                                          # -> out/mymap/packed/
```

`newmap.py` clones a shipped map (`clone_from`) under a new map id and xblock name. It never invents a field:
each row is the reference row's own text with the new ids put in. A spec override can only set an attribute that
some shipped row of the same tag already uses. Every id-valued attribute is checked against the shipped tables
(map ids, dungeonRoom unionRewardID, seasonRankRewardID, missions, roundID, playerCountFactorID, drop boxes). Run
`newmap.py -h` for the spec keys. `pack.py` runs `tools/m2d repack <orig> <loose> <copy> --add` for each touched
archive. It refuses to write over an original.

## What a custom dungeon needs

The tested setup: the client reads KMS dev client data (`MS2_CLIENT_DATA`, Xml.m2d is NS2F). The server's ingest reads
a GMS2 v1.3.2 data folder (`MS2_SERVER_DATA`: Xml.m2d + Server.m2d, MS2F). Both share `Resource/`, because the server's
`Resource` is a symlink to the client's. The two Xml.m2d use **different formats** for the same map, so newmap writes
both:

| archive | file | made by | notes |
|---|---|---|---|
| client Xml.m2d | `table/fielddata.xml` `<fieldData id=>` | newmap | the KMS client has no `map/<id>.xml`; the xblock name is uppercase here |
| client Xml.m2d | `table/dungeonroom.xml` | newmap | groupType="1", fieldIDs unpadded |
| client Xml.m2d | `string/{cn,de,en,jp,kr,pt}/mapname.xml` | newmap | spec `names`; a missing language falls back to en |
| both Xml.m2d | `mapxblock/<xb>.xml` | newmap | clientProperty (bgDay, ...); the `<minimap>` camera node when the minimap is generated |
| both Xml.m2d | `trigger/<xb>/*.xml` | newmap copies them, or spec `trigger_dir` | write new ones with tools/trigger |
| server Xml.m2d | `map/<id:08d>.xml` | newmap | full property set; the server reads this, not fielddata |
| server Xml.m2d | `table/na/dungeonroom.xml`, `string/en/mapname.xml` | newmap | groupType="normal", fieldIDs padded to 8 digits |
| Server.m2d | `table/Server/InstanceField.xml` | newmap | only if the reference has a row (solo instances); id = next after the max of its type |
| Resource/asset-web-metadata.m2d | `{emergent-world,x-world,name,canonical,relpath}.nt` | newmap | one new line each, cloned from the reference xblock's triples; without them the client fails to load the map |
| Resource/Exported.m2d | `xblock/<xb>.xblock` | **Toolbench / World Builder export**, spec `xblock_file` | default: a copy of the reference |
| Resource/PrecomputedTerrain.m2d | `<xb>.tok` | **tools/tok** (tokgeom + tokbuild), spec `tok_file` | default: a copy of the reference |
| Resource/Image.m2d | `map/minimap/<id:08d>.dds` | **minimap.py** (newmap default), spec `minimap_file` | see "Minimap" below |
| server (runtime) | `Maple2.Server.Game/Navmeshes/<xb>.navmesh` | ingest `--run-navmesh` | not an archive; see "Server" below |

Not written: `table/kr/fieldmetadata.xml` (an npc → field index the client builds for quest guides; a map with no
quest NPCs needs no row), quest/npc/item tables (spawns are in the xblock), and the server's `table/na/*` reward
tables (reference the existing ones by id).

## What has been proven

Each result below comes from a real run on the client and the server (dates are 2026). Nothing here is a claim about other
setups.

**Client load, triggers and minimap (`custom-warehouse`, 11-08).** The clone loads, its custom trigger shows the banners and
spawns the thugs, and the generated minimap is drawn in the corner.

**Build from spec alone (`combat-short`, 11-08).** The map loads with its name and minimap, the `fight` banner shows, the
server spawns all three monsters, and `trigger reset MobSpawn_11` runs the two states again. The client stayed up for the
full 480 s run. The monsters' spawn points are off-screen from the arrival point, so the spawns were checked in the server
log, not seen on screen.

**New map, collision and portal (`new-arena`, 11-08, warp entry).** New map and its collision, banner, three mobs walking on
the generated navmesh, portal hidden and unusable before the clear, open after it, and Space moving the player to 2000001.

**Data and ingest (`example-spec.json`, 11-07).** It clones 52000125 (`52000125_qd`, a small solo instance) as **52900001 /
`52900001_cd`**, and adds dungeonRoom 29900001 (cloned from 20006001) on both sides and InstanceField 1100016. Output: 11
client-xml files, 6 server-xml files, 1 Server.m2d file, xblock and tok. Diffs against the originals show only the new rows.
Repack: client Xml.m2d 46108 kept / 8 replaced / 3 added. Every new entry reads back byte-identical (`m2d cat | cmp`), and
untouched entries still match the originals.

The full ingest of `packed/server-data` into a scratch DB exits 0 and adds: map 52900001 "Custom Warehouse (test)" with xblock
52900001_cd (1184 → 1185 maps), dungeonroom 29900001 (FieldIds [52900001], Level 30, UnionRewardId 1006), InstanceField
52900001 → 1100016 (SaveField), 41 map-entity rows (the same as 52000125_qd), and map-data for 52900001_cd. A second run with
`--run-navmesh`, on a scratch copy of `Navmeshes/`, built only `52900001_cd.navmesh` (8568 bytes, the same size as
52000125_qd's; the 1179 others were already valid). The game DB was not touched.

**Dungeon-room entry and a real clear (`new-arena`, 11-08).** The spec's `dungeon_room` makes the `table/dungeonroom.xml`
row (id 29900010 for new-arena). That row is all the server needs: no InstanceField row is added. The level check is the
row's `limitPlayerLevel`, which the clone copies from the reference (28 for 20012001), so new-arena sets
`"limitPlayerLevel": "10"` and `"dungeonLevel": "10"`.

The server flow: RoomDungeon Create runs `DungeonManager.CreateDungeonRoom(id, false)`, which moves the player to the lobby
field 50000001. RoomDungeon EnterField runs `DungeonManager.EnterInitField()`, which goes to the room's first field
(52900010) as a dungeon field, so `DungeonClear` works there (unlike after a warp). In the test the lobby's entry portal did
not react to Space, so a local GM chat command stood in for the two packets the client's dungeon UI sends: `dungeon <roomId>`
calling `session.Dungeon.CreateDungeonRoom(id, false)` and `dungeon enter` calling `session.Dungeon.EnterInitField()`.
It is not shipped here; if you want it, add it as a `GameCommand` next to the server's other commands, with
AdminPermissions.Warp.

With a Knight's basic attacks all three mobs die, the trigger shows "The way out is open.", opens the portal, and
`DungeonClear` brings up the Results screen (time, mesos, EXP, rewards).

**Custom boss (`boss-arena`, 11-08).** See "Custom bosses" below. Trash wave, then the boss spawns with its name and the
boss HP bar; telegraph and void blast effects, the player takes 37 per blast; the boss is killed with real skills, the
Results screen shows and the portal opens. Real basic attacks hit this boss for about 5-7, so the example uses hp 400
and par/mar 10, a kill in about 1-2 min. The same run with a new model (`model_dir`): a shipped body re-encoded by
`tools/nif/gltf2nif.py` plays idle, walk and the void cast, and so did a Blender-tinted variant.

**Not proven.**

- Any stock client: the client runs used a client built from [MS2Recomp](https://github.com/riyukuro/MS2Recomp) (see
  "Client"), and the dungeon-room and boss runs also the original exe with its Nexon service calls stubbed out.

## Server: ingest and navmesh

The ingest takes a whole data folder. `<out>/packed/server-data` is one: the repacked Xml.m2d/Server.m2d, plus
`Resource/` symlinks to the repacked Exported/Terrain copies and the original Resource entries. To ingest into a
scratch DB without touching the game DB, the server tree, or its `.env`:

1. Copy `<MS2_SERVER_DIR>/Maple2.File.Ingest/bin/Release/net8.0` to `S/Maple2.File.Ingest/bin/Release/net8.0`.
   `SOLUTION_DIR` is the bin dir's 4th parent, so `S/.env` is read and **overrides** the environment. Create the
   empty dir `S/Maple2.Server.World`.
2. In `S/.env`, set `MS2_DATA_FOLDER=<out>/packed/server-data/`, `DATA_DB_NAME=maple-data-<scratch>`, `LANGUAGE=EN`,
   and the DB_* lines from `<MS2_SERVER_DIR>/.env`.
3. Put a `dotnet` shim first on PATH. It exits 0 for `dotnet ef ...` and execs the real dotnet otherwise. The ingest
   always runs `dotnet ef database update` on the **game** DB, and the shim skips that.
4. `cd S/Maple2.File.Ingest/bin/Release/net8.0 && DOTNET_ROLL_FORWARD=Major dotnet Maple2.File.Ingest.dll`. Use
   roll-forward only if no .NET 8 runtime is installed.
5. Check the result: `select * from \`map\` where Id=52900001` (the table is `map`, not `map-metadata`) (and dungeon-room / instance-field), then
   drop the scratch DB.

For real use, point the live `.env`'s MS2_DATA_FOLDER at the packed folder instead and run the ingest normally.
The game server loads `Navmeshes/<xblock>.navmesh` at field creation, so a new xblock needs one. `--run-navmesh`
generates navmeshes for every map that has none (it checks `NavmeshHash`). Copy the existing `Navmeshes/` and its
hash dir into a scratch `S/Maple2.Server.Game/` first, and only the new map is built. Then copy that one
`.navmesh` and its hash next to the others.

## Client

**Two things the client and the game server need, which `build.py` takes care of:**

- **asset-web-metadata entries.** `Data/Resource/asset-web-metadata.m2d` resolves `urn:emergent-world:<XBLOCK>`.
  Without an entry for the new xblock, the client shows "Map Loading Failed: urn:emergent-world:52900001_CD". A world
  is 5 triples under one `<urn:uuid:U>`, one per .nt file, with predicates `<http://emergent.net/aweb/1.0/...>`:
  `emergent-world.nt` tag "emergent-world", `x-world.nt` tag "x-world", `name.nt` name "52900001_CD",
  `canonical.nt` canonical "/Exported/XBlock", and `relpath.nt` relpath "/Exported/XBlock/52900001_CD.xblock".
  newmap clones the reference's own triples under uuid5(URL, "ms2-custom-xblock:<xb>"), into `awm/`.
- **The navmesh goes in the game server's bin dir.** Navigation.cs loads
  `AppDomain.CurrentDomain.BaseDirectory/Navmeshes/<xblock>.navmesh`, i.e. `Maple2.Server.Game/bin/Release/net8.0/Navmeshes/`.
  It does not use Paths.NAVMESH_DIR. If the file is missing, the warp fails with nothing in the log.
  `INSTALL.txt` gives the copy commands.

**Steps:**

1. Run `build.py` on a spec whose `"trigger_dir"` points at your trigger folder (`examples/custom-warehouse/trigger`:
   SetEventUI type 1 banners and SpawnMonster 601-603). Ingest into a scratch data DB ("Server").
2. To run a second server stack without touching a running one (its ports are hardcoded), use a private user+net namespace:
   `unshare -r -n`, a socat unix-socket bridge to the DB on 127.0.0.1:3306, and an `S/.env` naming the scratch DATA_DB_NAME
   and GAME_DB_NAME. Start world, login, game, and web with a .NET 8 runtime.
3. **Give the netns a non-loopback NIC**, or the client goes black after the splash (wininet 12029, then an AV):
   `ip link add eth0 type veth peer name eth1; ip link set eth0 address 02:42:ac:11:00:07; ip addr add 10.77.0.2/24 dev eth0;
   ip link set eth0 up; ip link set eth1 up; ip route add default via 10.77.0.1 dev eth0` (run inside the ns; the
   dummy module is not available).
4. Client copy: symlink `packed/client/Data/Xml.m2d/.m2h` and
   `Resource/{Exported,PrecomputedTerrain,Image,asset-web-metadata}.m2d/.m2h` into a client copy (never over your original install),
   plus `Resource/Model/{Npc,Textures}.m2d/.m2h` when a boss has a `model_dir`.
5. Use a client built from [MS2Recomp](https://github.com/riyukuro/MS2Recomp). The stock exe shows an empty 확인 box at
   field entry (null CNXMan), and clicking it exits the client.
6. Log in with a GM character and enter the map with the chat command `warp 52900001`; run a trigger again with
   `trigger reset main`. Once the character has been warped, it logs straight back into 52900001.
   The command matches the model name exactly as the xblock spells it (`MobSpawn_11`), while the script file is found in
   lower case (`mobspawn_11.xml`). With the wrong case, the client chat says `Trigger ... not found`.
7. Afterwards, stop the namespace's processes and the outer socat, then drop the scratch DBs.

minimap.py needs the .NET 10 SDK for tokgeom; .NET 8 fails.
The world map (M) does not show the map; it is only listed in the search pane as "커스텀 창고 (테스트) / Lv. 20".

## Minimap

`minimap.py` renders a flat, stylised minimap from the map's geometry and writes it in the shipped format. newmap
runs it by default (spec `"minimap": "generate"`; `"reference"` copies the reference map's DDS, `false` writes
none, and `minimap_file` uses your own DDS).

    python3 tools/dungeon/minimap.py --map-id 52900001 --xblock 52000125_qd -o 52900001.dds --xml node.xml --png prev.png
    python3 tools/dungeon/minimap.py --map-id 2000062 --xblock 02000062_tw_lith -o g.dds --camera <shipped mapxblock>.xml

**Shipped format.** `Image.m2d map/minimap/<id:08d>.dds` is 512x512 DXT5 with 10 mips (349,680 bytes). The client
does not read any bounds from fielddata. The world mapping is the `<minimap>` node of `mapxblock/<xb>.xml`
(CFieldDataCli::LoadXBlock):

    <minimap>
      <image name="./Data/Resource/Image/Map/Minimap/<id08>.dds" left= right= top= bottom= icon=".\Data\Resource\Image\Map\Icon\..." />
      <frustum left right top bottom near="10" far="100000" ortho="true" />
      <screen left="0" right="1920" top="0" bottom="1080" />
      <camera position="x, y, z" rotation="90.000000, 45.000000, -45.000000" />
      [<edit zoomfactor=>]   editor only
    </minimap>

- The image rect is a 1024x1024 screen crop (1524 of the 1551 nodes), and the DDS is that crop at half scale.
- 1310 nodes use the isometric rotation (90,45,-45); only 2 are not ortho.
- The camera axes are the columns of Gamebryo's FromEulerAnglesXYZ (clockwise, i.e. transposed, Rx·Ry·Rz):
  col0 = view direction, col1 = up, col2 = right. When rotation z is non-zero, up and right are negated.
  For (90,45,-45), right = (0.707,-0.707,0) and up = (0.5,0.5,0.707).
- A world point p maps to the DDS pixel ((sx·W − image.left)/2, ((1−sy)·H − image.top)/2), where
  sx = (right·(p−cam) + R)/(R−L), sy = (up·(p−cam) + T)/(T−B), and W×H is the screen size.
- With no `<minimap>` node there is no minimap. The client also hides it when fielddata `minimapInvisible` is
  non-zero, or when the image name is empty. 52000125_qd has no node and minimapInvisible="1".

**What it draws.** Ground comes from tokgeom (`tools/tok/extract`; built on demand with the system `dotnet`, about
6 s per map). That covers triangle meshes plus PhysX box/convex solids. They are drawn back to front in the same
isometric view, shaded by height (green to sand) and by face normal, with darker outlines, then supersampled 2x.
Solids taller than 300 units (whitebox walls) are skipped (`--keep-tall` keeps them). The camera is fitted so that
the drawn geometry fills the crop with a 6% margin. `--faces` (a tokbuild `--faces` dump) also outlines the
walkable boundary. newmap writes the node into both mapxblock xmls and removes `minimapInvisible` from the new
fieldData row and server map, unless the spec sets it. A preview PNG is left in `<out>/minimap-work/`.

**Alignment check.** On 20 shipped maps, using each map's shipped camera, the tok walkable faces
were projected onto the shipped DDS alpha.

- On well-matched maps, 0.4–1.6% of walkable pixels fall outside the art, at most 2–8 px away (02000062 lith,
  ellinia, perion, 02000203, 02000305, 02020130).
- The generated silhouette against the shipped alpha gives IoU 0.85–0.99 on most maps.
- A few maps have walkable areas that the art leaves out, such as upper or hidden floors and other layouts:
  02000027_in, 02200001_dg, 65010001_bd, 02020140_bf. These are not projection errors.


