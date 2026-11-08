# tools/trigger — trigger script schema and linter

Tools for writing and checking map trigger scripts (custom dungeons), compared with the shipped scripts, client evidence, and the WIP Maple2 server. Python 3, stdlib only.

| file | what it does |
|---|---|
| `gen_schema.py` | reads Maple2 server (WIP) source and writes `schema.json`: every action and condition, its args, and how it is used in shipped scripts |
| `normalize.py` | Python port of the ingest's normalisation (`Maple2.File.Ingest/Mapper/TriggerMapper.cs`). It turns a shipped script into the form Maple2 server (WIP) reads |
| `xblockindex.py` | indexes map objects (spawns, boxes, meshes, …) per xblock, for ID checks |
| `lint.py` | linter for raw (shipped) or server-form scripts |
| `known-issues.json` | allowlist of errors that are really in the shipped data, each with a reason |

## Reference

Authority order: shipped scripts > client behaviour > Maple2 server. The client’s packet handlers show which effects actions can produce (`triggerservice.cpp`), and the housing trigger format (`triggercli.cpp`, `triggeractioncli.cpp`). Maple2 is a work-in-progress reimplementation, so its parser and runtime may be incomplete.

## Script format

A script is one XML file: `<ms2>` holding `<state name=...>` elements. The ingest stores it as
trigger `<file name without .xml>` in folder `<xblock>` (`trigger/<xblock>/<name>.xml` in
Xml.m2d). The server parses it in `Maple2.Server.Game/Util/TriggerStorage.cs`:

```xml
<ms2>
  <state name="Wait">                       <!-- the first state is the start state -->
    <onEnter>
      <action name="set_mesh" trigger_ids="3001" visible="false"/>
      <transition state="Next"/>            <!-- optional: go straight on -->
    </onEnter>
    <condition name="user_detected" box_ids="101">   <!-- checked on each tick, in order -->
      <action name="set_user_value" key="Go" value="1"/>
      <transition state="Next"/>
    </condition>
    <onExit> <action .../> </onExit>
  </state>
  <state name="Next"> ... </state>
</ms2>
```

- `TriggerState` starts at `trigger.States.First()`.
- A state change uses `States.FirstOrDefault(x => x.Name == name)`, an exact case-sensitive match.
- If a condition holds and its `<transition>` names a state that does not exist, its actions
  still run, but the trigger stays in the state. The condition is checked again on every tick,
  so its actions run on every tick while it holds. The linter reports this as `bad-transition` (error).
- Group conditions `all_of` / `any_one` (shipped `AllOf` / `AnyOne`, 265 uses) look like this:
  `<condition name="AllOf"><group><condition .../>...</group><transition .../></condition>`.
  Maple2 server (WIP) currently handles these incorrectly:
  - it never fills the group's `Conditions` list;
  - it reads the transition and actions from inside `<group>`, where there are none;
  - so `all_of` is always true and does nothing, and `any_one` is always false.

  The linter warns `maple2-gap` and still checks the inner conditions.
- Negation:
  - shipped scripts write `!Name`; the ingest turns that into `negate="true"`;
  - only conditions whose server class reads `negate` honour it (`negatable` in schema.json);
  - otherwise the linter warns `maple2-gap`.

### Raw (shipped) form vs server form

The shipped names are CamelCase or Korean (`몬스터소멸시킨다`). The ingest rewrites names in this order:
1. **Translate**: split on `_` and space, look each piece up in `TriggerTranslate`'s
   Action/ConditionLookup, then ToSnakeCase.
2. **Overrides**: per-function attribute renames, e.g. `arg1` → `spawn_ids`.
3. **Splitters**:
   - some actions pick an override with an attribute value (e.g. `side_npc_talk` by `type`);
   - if that attribute is missing, the first value of the outer `Types` is used;
   - a value with no mapping makes the ingest throw.
4. **FixClassName** on `state@name` and `transition@state`:
   - spaces become `_`;
   - a leading `1st` becomes `First`;
   - a blank name becomes `State`;
   - leading digits move to the end, e.g. `2000 끝` → `끝2000` (see `normalize.fix_class_name`).

   FixClassName is not applied to the state args of `set_skip` / `set_scene_skip`. Those are
   looked up by exact name, so a name that FixClassName would change never matches
   (`skip-state-unfixed`, 115 shipped).

`python3 normalize.py <script.xml> --root <dir with trigger/>` prints Maple2 server (WIP) form.

Schema entries carry `source: "maple2"`; the top-level note explains that names, types and implementation status describe Maple2 and are not verified against Nexon. Arguments found only in shipped scripts are marked `shipped-only`.

The `schema.json` fields per action/condition:
- `args` gives name, type, `required`, default, and `shipped_aliases` (the raw attribute names
  that map to the arg).
- `status`:
  - `implemented` — the context method does something;
  - `stub` — empty body or only a log;
  - `partial`, `log_only` or `builtin` (group conditions).
- `shipped_uses` / `shipped_scripts` count uses in the shipped scripts.
- `server_problems` lists Maple2 mapping problems found while generating.

### Imports

- `<import path="./Data/Xml/Trigger/<Folder>/<File>.xml"/>` merges the other file's states into
  this one. A state the file already has wins; if two imports both define a state, the later one wins.
- The path becomes `trigger/<folder>/<file>.xml`.
- `LoadImportedStates` looks it up in two steps:
  1. an exact ordinal match;
  2. otherwise the first pack entry, in archive order, whose name ends with `/<file>`
     (case-insensitive).
- Pack names are lowercase, so step 1 fails for CamelCase paths. For example
  `Dungeon_Common/CheckUserCount.xml` resolves to `trigger/50000001_dl/checkusercount.xml`,
  the first match in archive order, not to `dungeon_common/checkusercount.xml`. The two files differ.
- To reproduce this order, put `pack-order.txt` (the pack's `trigger/` names in archive order)
  in the `--root` dir. Without it, the sorted order is used.

### User values

- `set_user_value(trigger_id, key, value)` writes `Field.UserValues[key]`. This is one
  dictionary per field instance. `trigger_id` is only logged.
- Condition `user_value(key, value)` compares `UserValues.GetValueOrDefault(key, 0) == value`.
- So user values are how the triggers of one map talk to each other. An unset key reads as 0.
- NPC scripts can also set them (`NpcScriptManager`, `SetTriggerValueKey`).

### How scripts tie to map entities

- An xblock `MS2TriggerModel` entity becomes a `TriggerModel` (`MapEntityMapper`):
  - its id is `TriggerModelID`;
  - its name is `Path.GetFileNameWithoutExtension(XmlFilePath)`.
- When a field loads, `FieldManager` adds a `FieldTrigger` for every TriggerModel.
- `FieldTrigger` looks up `TriggerCache.TryGet(field XBlock, Name.ToLower())`. If no script has
  that name in the xblock's folder, it throws.
- So a new script `trigger/<xblock>/<name>.xml` runs only if the xblock has a TriggerModel
  whose XmlFilePath file name is `<name>`. `lint --xblock` warns `no-trigger-model` otherwise.
- Object IDs in args (spawn ids, box ids, trigger_ids of mesh/effect/…) refer to the xblock's
  entities. `xblockindex.py` lists the kinds.
- `spawn_monster` resolves its ids through EventSpawnPointNPC only.
- MS2BreakableNIF entities are not ingested (the case is commented out in MapEntityMapper), so
  `set_breakable` on them finds nothing (`ref-breakable-nif`).

## Testing a script on Maple2 (WIP) with Constant.DebugTriggers

`Constant.DebugTriggers` (`Maple2.Model/Metadata/Constants.cs`) is a `const bool`, default
`false`, so changing it needs a rebuild. When it is `true`:
- the ingest (`TriggerMapper`) also writes each normalised script to
  `Maple2.Server.Game/DebugTriggers/<xblock>/<name>.xml` (`Paths.DEBUG_TRIGGERS_DIR`);
- the game server (`TriggerCache.TryGet`) reads scripts from those files instead of the DB, and
  never caches them, so an edited file takes effect when the field next creates the trigger;
- a missing file throws `ArgumentException`.

These files are in server form (snake_case). Check them with:

```sh
python3 tools/trigger/lint.py <MS2_SERVER_DIR>/Maple2.Server.Game/DebugTriggers/<xblock> --xblock auto
```

## Usage

```sh
# one-time data: shipped scripts and the pack order (tools/m2d, needs dotnet; see tools/m2d/README.md to build it)
dotnet tools/m2d/bin/m2d.dll extract <server data>/Xml.m2d out/x trigger/
dotnet tools/m2d/bin/m2d.dll list <server data>/Xml.m2d | grep '^trigger/' > out/x/pack-order.txt
# object index (default cache <MS2_OUT>/trigger-cache, or MS2_TRIGGER_CACHE)
python3 tools/trigger/xblockindex.py build --exported <client data>/Resource/Exported.m2d
python3 tools/trigger/xblockindex.py show 02000387_bf

# lint
python3 tools/trigger/lint.py my_dungeon.xml --xblock 99999999_xx
# make known Maple2 gaps fail lint for custom scripts
python3 tools/trigger/lint.py my_dungeon.xml --xblock 99999999_xx --strict
python3 tools/trigger/lint.py out/x/trigger --format raw --xblock auto --summary out.txt -q

# regenerate schema.json after a server change
python3 tools/trigger/gen_schema.py --server <MS2_SERVER_DIR> --shipped out/x --tables <dir with table/feature*.xml>
```

(With an older dotnet, set `DOTNET_ROLL_FORWARD=Major`.)

How lint reports results:
- Exit status: 1 if any error is left after the allowlist; 2 on bad usage; otherwise 0.
- `--json` prints one finding per line.
- `--allow ''` turns the allowlist off.
- `--strict` treats `maple2-gap` warnings as errors.

**Errors** — shipped-data errors or ingest failures:
- parse errors;
- `unknown-action` / `unknown-condition` (Maple2 server (WIP) logs and skips it);
- `bad-transition`;
- duplicate state names;
- unresolved imports;
- splitter values with no mapping.

**Warnings** — Maple2 server (WIP) currently ignores, defaults, or rejects a feature:
- `missing-arg`; unknown or parser-rejected arguments are `maple2-gap`.
- `unreachable` states;
- `ref-missing` / `ref-kind`: an ID is not in the xblock, or is the wrong kind of object;
- `maple2-gap`: Maple2 server (WIP) currently ignores or mishandles a feature, including group conditions, missing
  transition targets (and reported as data errors), unnormalised skip state args, ignored comparisons,
  unread condition args, `DungeonVariable`, breakable NIF references, and unsupported negation.

## Shipped data result

Shipped lint summary (4,584 scripts, checked with their xblocks):
- 0 errors after `known-issues.json`: 359 findings allowed in 195 files (356 missing transition targets and 3 duplicate state names). Eleven Maple2 parser mismatches moved from errors to warnings.
- 12,018 warnings. The largest groups:

  | code | count |
  |---|---|
  | maple2-gap | 4,775 |
  | ref-missing | 4,684 |
  | unreachable | 1,351 |
  | ref-kind | 972 |

  `maple2-gap` cases: 265 `AllOf` / `AnyOne`, 356 missing transition targets, 115 unnormalised
  skip state args, 69 `CheckNpcHp` comparisons, 21 `UserValue` operators, 3 `npc_damage` args,
  48 `wedding_hall_state` args, 3,546 breakable NIF references, 336 `DungeonVariable` uses, and
  8 ignored negations, plus 7 shipped parser mismatch cases.
