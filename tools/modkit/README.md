# modkit: row-level mods for game tables

A mod is a folder with a `mod.xml` that lists rows to change, add and delete in the game's XML tables. modkit applies
any number of mods to **copies** of the archives (`Xml.m2d` of the client, `Xml.m2d` and `Server.m2d` of the server)
and leaves the originals alone. Entries and rows that no mod touches keep their bytes.

- `MS2.ModKit/` is the library (C#, built for .NET 3.5 and .NET 8). Everything is in it: the `.m2d`/`.m2h` reader and
  writer, the XML editing, the manifest, the checks, apply and revert. It prints nothing and never exits the process,
  so an editor (a Toolbench plugin) can load it in-process.
- `cli/` is a small command line tool over the library (.NET 8).
- `plugin/` is the table editor: a Gamebryo Toolbench plugin over the library (see
  [The table editor in Toolbench](#the-table-editor-in-toolbench)).
- `tests/` are the tests; `examples/pets/` is a made-up data set with three mods to try things on.

No game data is in this repo, and none is needed for the examples or the tests.

## Build

Needs the .NET SDK 8 or later (with a later SDK only, set `DOTNET_ROLL_FORWARD=Major` to run the net8.0 outputs).

```sh
cd tools/modkit
dotnet build cli -c Release -o cli/bin          # cli/bin/modkit.dll, run with: dotnet cli/bin/modkit.dll
dotnet build MS2.ModKit -c Release              # MS2.ModKit/bin/Release/net35/MS2.ModKit.dll for a plugin
```

Below, `modkit` stands for `dotnet tools/modkit/cli/bin/modkit.dll`.

The data folders come from `MS2_CLIENT_DATA` and `MS2_SERVER_DATA` (environment, then `modtools.toml`, as for the other
tools) or from `--client DIR` and `--server DIR`. Archives are named `<root>:<path>`: `client:Xml.m2d`,
`server:Xml.m2d`, `server:Server.m2d`.

## Try it on the example

The example data is plain files; `pack` makes two small archives from them.

```sh
D=/tmp/modkit-demo; E=tools/modkit/examples/pets
modkit pack $E/data/client/Xml $D/client/Xml.m2d NS2F
modkit pack $E/data/server/Xml $D/server/Xml.m2d
O="--client $D/client --server $D/server --schema $D/schema"

modkit rows client:Xml.m2d table/pet.xml $O            # how to name each row
modkit diff $E/mods/bigger-pets $E/mods/new-pet $O     # what would change, nothing is written
modkit apply -w $D/ws $E/mods/bigger-pets $E/mods/new-pet $O
modkit status -w $D/ws
modkit revert -w $D/ws example.new-pet $O              # the copies without that mod
modkit revert -w $D/ws $O                              # no mod left: the copies are removed
```

`diff` prints one line per change:

```
client:Xml.m2d table/pet.xml pet[id=100] @hp: "50" -> "65"
client:Xml.m2d table/pet.xml pet[id=100] @speed: "1.5" -> (removed)
client:Xml.m2d table/pet.xml pet[id=101]/skill[id=8] @level: "2" -> "3"
client:Xml.m2d table/pet.xml pet[id=900] + <pet id="900" name="Cinder" hp="70" speed="2"> ... </pet>
client:Xml.m2d pet/900.xml + entry (copy of pet/101.xml, 124 bytes)
2 mod(s): 13 change(s) in 4 entries of 2 archive(s); 0 error(s), 0 conflict(s), 0 warning(s)
```

and `validate` with the third example mod, which is made to collide with the other two:

```
warning: [example.rival-pets] id 905 of space "pet" is added in 1 of 2 places; nothing is added in server:Xml.m2d pet/{id}.xml
warning: [example.rival-pets] client:Xml.m2d table/pet.xml: attribute glow is never set on ms2/pet in the shipped table/pet.xml
conflict: [example.rival-pets] client:Xml.m2d table/pet.xml: pet[id=100] @hp: "80" here, "65" in mod example.bigger-pets
conflict: [example.rival-pets] client:Xml.m2d table/pet.xml: deletes the row pet[id=101] that mod example.bigger-pets changes
conflict: [example.rival-pets] ids space "pet": the range 905-915 overlaps 900-909 of mod example.new-pet
error: [example.rival-pets] client:Xml.m2d table/pet.xml: new id 905 of space "pet" is already used in the original data (server:Xml.m2d pet/{id}.xml)
```

## The manifest: mod.xml

```xml
<mod id="example.new-pet" name="A new pet" version="1.0">
  <description>free text</description>

  <ids space="pet" from="900" to="909">
    <where archive="client:Xml.m2d" entry="table/pet.xml" rows="pet"/>
    <where archive="server:Xml.m2d" entry="table/pet.xml" rows="pet"/>
    <where archive="client:Xml.m2d" entry="pet/{id}.xml"/>
    <where archive="server:Xml.m2d" entry="pet/{id}.xml"/>
  </ids>

  <edit archive="client:Xml.m2d" entry="table/pet.xml">
    <change at="pet[id=100]">
      <set name="hp" value="65"/>
      <unset name="speed"/>
    </change>
    <add copy="pet[id=101]">
      <set name="id" value="900"/>
      <set at="skill[id=7]" name="level" value="4"/>
    </add>
    <delete at="pet[id=102][feature=Alpha]"/>
  </edit>

  <entry archive="client:Xml.m2d" name="pet/900.xml" copy="pet/101.xml"/>
  <entry archive="server:Xml.m2d" name="pet/900.xml" file="data/pet-900.xml"/>
</mod>
```

**Every op names its archive.** The client and the server each have their own copy of most tables, and a mod says
which it changes. Nothing is mirrored for you: a row that both sides read is written twice, once per side.

| element | meaning |
|---|---|
| `<mod id name version>` | `id` is required (letters, digits, `.`, `-`, `_`) and is what `revert` and the reports use |
| `<edit archive entry>` | row ops on one entry (one XML file inside the archive) |
| `<change at>` | change the row at the selector; holds `<set name value>`, `<unset name>`, `<text value>` (the element's text). Each of them takes `at="..."` to reach an element under the row |
| `<add copy>` | a new row that is a copy of the row at the selector, changed by the `<set>`/`<unset>`/`<text>` inside |
| `<add><xml>…</xml></add>` | a new row written out (one element) |
| `<add after= before= in=>` | where the new row goes: after or before a row, or as the last child of one. Default: last in the parent of the copied row, or last in the root element |
| `<delete at>` | remove the row with everything in it |
| `<entry archive name copy>` | a new entry as a copy of another entry of the same archive; it can be edited by an `<edit>` further down |
| `<entry archive name file>` | a new entry, or a whole-entry replacement, from a file of the mod folder |
| `<entry archive name delete="true">` | remove an entry |
| `<ids space from to>` | the id range this mod reserves, and with `<where>` every place such an id lives (see below) |

Unknown elements and attributes in `mod.xml` are errors with a line number, so a typo does not pass silently.

### Row selectors

A selector is a path of steps from the entry's root element, joined by `/`:

```
pet[id=101]                       the <pet> whose id is 101
pet[id=101]/skill[id=8]           a row inside it
pet[id=102][feature=]             of the rows with id 102, the one with no (or an empty) feature
pet[id=102][feature=Alpha]        ... and the one for feature Alpha
drop[#3]                          the third <drop>, for rows that have nothing to tell them apart
*[id=5]                           any element name
key[name='a b']                   quotes for values with spaces, brackets or slashes
```

A selector must match exactly one element; none or several is an error that lists what it found. `modkit rows
<archive> <entry>` prints a selector for every row of an entry, and `modkit rows <archive> <entry> <selector>` for
the rows inside one.

### Reserved ids

`<ids>` says "this mod owns ids 900 to 909 of the space `pet`" and lists where a pet id lives: in rows of a table
(`rows="pet"`, id attribute `key="id"` by default) or in the name of an entry (`entry="pet/{id}.xml"`). Then:

- an id that the mod adds must be free in **all** the places, on both sides, in the original data: taken anywhere is
  an error (`strict="false"` on `<ids>` makes it a warning);
- an added id outside the reserved range is a warning;
- two mods whose ranges of one space overlap are a conflict;
- an id added in some of the places and not in others is a warning (often right, sometimes a forgotten side).

`modkit next-id <mod> <space> [count]` prints free ids of the range.

## Applying

```
modkit validate <mod>...                 errors, conflicts, warnings
modkit diff <mod>...                     the same, and every change
modkit apply -w <workspace> <mod>...     write the modded copies
modkit revert -w <workspace> [mod id]    without that mod / without any
modkit status -w <workspace>
```

- The workspace is a folder of your choice. `apply` writes `<workspace>/client/Xml.m2d` (+ `.m2h`),
  `<workspace>/server/Xml.m2d` and so on, only for archives that a mod touches (`--also-copy ARCHIVE` adds an
  untouched one), and `modkit-state.xml`, which lists the mods that are in. It refuses a workspace that is inside a
  data folder or holds one: originals are opened for reading only.
- Every `apply` starts from the originals, so applying a list of mods always gives the same result, whatever was in
  the workspace before. `revert` is the same thing with one mod less; with none left it removes the copies.
- Mods are applied in the order given. Two mods that set the same attribute of the same row to different values,
  a mod that deletes a row another one changes, two that add the same id or entry: these are **conflicts**. With a
  conflict or an error nothing is written (exit code 1). `--allow-conflicts` applies anyway, and the later mod wins.
- After writing, the changed entries are read back from the copy and compared. `--verify-all` also compares every
  untouched entry with the original.
- Point the game and the server at the copies yourself (or copy them over a **copy** of the game folder). That step,
  and proof in the running game, is not part of this tool yet (the table editor section below says where to put
  them).

What "kept" means: an entry that no mod touches is copied as stored (compressed and encrypted bytes, flags, order).
In an edited entry only the edited spans change: encoding, byte order mark, line ends, attribute order, quotes,
comments and indentation of everything else stay. New rows take the indentation and line ends of their neighbours.

## Schemas

modkit does not ship a description of the tables. `modkit schema build` derives one from **your** archives: for each
table, the elements, their attributes, how often each occurs, the kinds of value seen (empty, int, float, list, text)
and the numeric range. Entries that differ only by a number in the name are one table (`npc/21000001.xml` is
`npc/#.xml`). `modkit schema show <archive> [table]` prints it.

The cache (default `out/modkit/schema/`, or `--schema DIR`) holds sample values from the game data: it stays on your
machine and is never committed. Rebuild it when the archives change; modkit notices a stale one.

With a schema, `validate`/`diff`/`apply` warn about a new value that the shipped data never shows: an attribute that
is never set on that element, or a kind of value never seen for it (text where every shipped value is an int). These
are warnings, never refusals: the shipped data is the best evidence there is, but it does not show everything the game
accepts.

`schema-notes.xml` holds what the data cannot show. It names attributes only, never values:

```xml
<notes>
  <attr archive="client:Xml.m2d" table="table/pet.xml" element="ms2/pet" name="glow"
        source="client" ref="src/ms2/client/.../SomeLoader.cpp"/>
</notes>
```

`source="client"` means the client's loader reads the attribute (with the source path, as a citation, in `ref`);
then the "never set" warning becomes a note. `source="maple2"` means only the Maple2 server code says so: it is shown
as unverified and the warning stays. The order of trust is shipped data, then the client loader, then Maple2.

## Using the library

```csharp
using MS2.ModKit;

using (var data = new DataSet(clientDataDir, serverDataDir))
{
    var mods = new List<Mod> { Mod.Load(modFolder) };
    var schemas = SchemaSet.Load(schemaDir, data);                  // optional
    Plan plan = Planner.Build(data, mods, new PlanOptions { Schemas = schemas, Progress = p => ... });
    // plan.Changes, plan.Errors, plan.Conflicts, plan.Warnings, plan.Notes: show them

    var ws = new Workspace(workspaceDir);
    ApplyResult r = ws.Apply(data, mods, new ApplyOptions { Schemas = schemas, VerifyAll = true });
    if (!r.Applied) { /* r.Reason, r.Plan */ }
    ws.Revert(data, "some.mod.id", new ApplyOptions());
}
```

- Failures are `ModKitException` with a message meant for the user. No console output, no `Environment.Exit`.
- `M2dArchive` (read), `M2dWriter.Rewrite` / `M2dWriter.Create` (write), `Xml.XmlDoc` and `Xml.RowPath` are public
  for tools that need less than a whole mod.
- The net35 build has no dependency beyond the .NET Framework 3.5 class library. It was run as a 32-bit process on
  CLR 4 under Wine (the way Toolbench runs there): plan, apply, read back and revert of the example, and a cp949
  entry with a Korean name, with `Z:\...` paths.
- Archives written on .NET 8 are byte for byte what `tools/m2d` writes. On another runtime the compressed bytes of the
  entries that were changed can differ (its zlib is another one); the contents are the same and every reader reads
  them. Untouched entries are copied as stored on every runtime.

## The table editor in Toolbench

`plugin/` is a plugin for Gamebryo LightSpeed 3.2 Toolbench (your own copy, from your Gamebryo SDK; see
[tools/toolbench](../toolbench/README.md) for running it under Wine). It adds a view, **MS2 Tables**, that shows one
XML entry of an archive as a grid, keeps your edits as a `mod.xml`, and runs plan, apply and revert. It holds no logic
of its own: `MS2.ModKit.dll` does the work, as it does for the command line tool.

Build and install (the Toolbench assemblies are referenced in place, nothing of the SDK is copied):

```sh
dotnet build tools/modkit/plugin -c Release -p:ToolbenchDir=<Gamebryo SDK>/sdk/Win32/Bin/VC90/Shipping
```

Copy `MS2.Tables.dll`, `MS2.ModKit.dll` and `plugin.xml` from `tools/modkit/plugin/bin/Release/` into
`<Toolbench>/Plugins/MS2.Tables/`.

Settings (menu **Utilities > Settings...**, page "MS2 Mods"; per user, all empty by default). While a setting is empty its
environment variable is used, so a script can start Toolbench with the folders already set:

| Setting | Environment | Meaning |
|-|-|-|
| Client data folder | `MS2_CLIENT_DATA` | the `Data` folder of your client (holds `Xml.m2d`). Only read |
| Server data folder | `MS2_SERVER_DATA` | the data folder of your local server (holds its `Xml.m2d`). Only read |
| Mods folder | `MS2_MODS` | a folder of mod folders. Save mod writes `<mods>/<mod id>/mod.xml`; Plan and Apply use every mod in it (a folder whose name starts with `.` or `_` is left out) |
| Workspace folder | `MS2_MOD_WORKSPACE` | where Apply writes the patched copies |

One data folder is enough to look around; saving needs the mods folder, and apply the workspace folder as well. After
changing a setting press **Reload settings** in the view.

Using it:

1. The view opens docked at the right edge and is narrow at first: drag its left edge to make room for the grid
   (menu **MS2 Mods > Tables** shows the view again). Pick an archive, type part of an
   entry name into **Entry filter** and press Enter, or double-click an entry in the list. Each child element of the
   root element is a row and each attribute a column. For rows that sit deeper, put a selector of their parent into
   **Rows under** (for example `pet[id=101]` to see that pet's `<skill>` rows).
2. Edit cells. An edited cell turns yellow, and the **Pending changes** pane shows the `mod.xml` the edits make.
   Typing the original value back takes the change out again. **Clone row** adds a row that starts as a copy of the
   selected one (green; change its id), **Delete / restore row** deletes a row or takes the delete back, **Remove
   attribute** takes the attribute of the selected cell off its row, **Add column** adds a column for an attribute no
   row has yet.
3. A table that is in the client and in the server archive has to be changed in both. **Copy edits to other side**
   writes the edits of the open table into the mod for the other data root too.
4. **Save mod** writes the mod folder. **Load mod** reads one back into the grid: pick a folder of the mods folder
   or type a path. **New mod** starts an empty mod with the id in **Mod id** (the first mod is called `my.mod`).
5. **Plan** shows what all mods of the mods folder plus the pending edits would change, with errors, conflicts and
   warnings, and writes nothing. **Apply** saves the mod being edited and writes the patched copies into the
   workspace; with an error or a conflict it writes nothing and says why. **Revert** removes the copies again. The
   three are also in the **MS2 Mods** menu.

Where the copies go, and how the game gets them: Apply writes `<workspace>/client/Xml.m2d` and `Xml.m2h` and
`<workspace>/server/Xml.m2d` (only the archives a mod touches) and `modkit-state.xml`. The original data folders are
never written. To run with the mods:

- client: make a copy of your client folder once, and copy `<workspace>/client/*` over the files of the same name in
  the copy's `Data` folder after each apply. Start the copy.
- local server: point the server's data path at a folder that holds `<workspace>/server/Xml.m2d` in place of the
  original one (a copy of the server data folder with that file replaced), and restart it.

After Revert the copies are gone from the workspace; put the original files back the same way.

Not in this editor yet: schema checks (the command line `--schema`), `<ids>` ranges, `<entry>` operations and edits
of element text. A mod that holds them loads, plans and applies as it is; the grid just has no cell for them. Apply
runs on the window's thread, so Toolbench does not react while a large archive is written.

## Tests

```sh
cd tools/modkit/tests
dotnet test --filter "FullyQualifiedName!~RealArchive"     # made-up data only
MS2_CLIENT_DATA=... MS2_SERVER_DATA=... dotnet test        # also the checks on your archives
```

The real-archive tests copy your archives to a temporary folder (`MS2_MODKIT_TMP`, default the system one) and work
there: read every entry and compare with `tools/m2d`'s reader, rewrite and compare with its repack, and change one
attribute of one table on both sides and check that every other entry is unchanged. Without the two settings they
are skipped.

## Limits

- XML entries only. Other files in the archives (`.xblock`, images, scripts) can be added, replaced or deleted whole
  with `<entry>`, not edited.
- A mod is checked against the original data and the other mods given in the same command, not against mods you
  apply some other day.
- Nothing here knows what a table means: which tables must agree between client and server, or which other tables
  refer to an id, is for the mod author (and for editors built on this library) to know.
