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

The data folders come from `MS2_CLIENT_DATA`, `MS2_GMS_CLIENT_DATA` and `MS2_SERVER_DATA` (environment, then
`modtools.toml`, as for the other tools) or from `--client DIR`, `--gms DIR` and `--server DIR`. Set the ones you have.
Archives are named `<root>:<path>`: `client:Xml.m2d` (the Korean client), `gms-client:Xml.m2d` (the global client),
`server:Xml.m2d`, `server:Server.m2d`. Which client is which: [Profiles](#profiles-which-data-you-have).

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
conflict, example.rival-pets wins: [example.bigger-pets] client:Xml.m2d table/pet.xml: pet[id=100] @hp: "65" here, "80" in mod example.rival-pets
conflict, example.rival-pets wins: [example.bigger-pets] client:Xml.m2d table/pet.xml: changes the row pet[id=101]/skill[id=8] that mod example.rival-pets deletes
conflict, no winner: [example.rival-pets] ids space "pet": the range 905-915 overlaps 900-909 of mod example.new-pet
error: [example.rival-pets] client:Xml.m2d table/pet.xml: new id 905 of space "pet" is already used in the original data (server:Xml.m2d pet/{id}.xml)
priority, the highest first: example.rival-pets > example.new-pet > example.bigger-pets
example.rival-pets wins 2 parts of example.bigger-pets
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
which it changes. A table that both sides hold in the same format is written twice, once per side. For items,
whose two sides are different files, modkit writes the other side itself; see
[One mod, both sides](#one-mod-both-sides). `sync="false"` on an `<edit>`, a `<change>` or a `<set>` keeps it on its own side.

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
modkit apply --game <game folder> <mod>...   the client's changed tables as plain files under <game folder>/Data/Xml
modkit revert --game <game folder>           remove the files that apply --game wrote
modkit status --game <game folder>
```

- The workspace is a folder of your choice. `apply` writes `<workspace>/client/Xml.m2d` (+ `.m2h`),
  `<workspace>/server/Xml.m2d` and so on, only for archives that a mod touches (`--also-copy ARCHIVE` adds an
  untouched one), and `modkit-state.xml`, which lists the mods that are in. It refuses a workspace that is inside a
  data folder or holds one: originals are opened for reading only.
- Every `apply` starts from the originals, so applying a list of mods always gives the same result, whatever was in
  the workspace before. `revert` is the same thing with one mod less; with none left it removes the copies.
- Two mods that set the same attribute of the same row to different values, a mod that deletes a row another one
  changes, two that add the same id or entry: these are **conflicts**. With a conflict or an error nothing is
  written (exit code 1). `--allow-conflicts` applies anyway, and the mod of the higher priority wins.
- **Priority.** Of the mods given, a later one is higher than an earlier one. `--priority ID,ID,...` names mod ids
  with the highest first: the named mods are above all others, the others keep their order below them. An id
  that none of the given mods has is a usage error. Without the option the list is read from the setting
  `MS2_MOD_PRIORITY` (environment, then `modtools.toml`), which may also name mods that are not given this time.
  Every conflict line begins with the outcome (`conflict, X wins:` or `conflict, no winner:`), a line
  `priority, the highest first: c > a > b` follows the conflicts, then one line per pair of mods where
  something is left out (`c wins 2 parts of b`: two things mod b asks for are not in the result, because mod c
  has them; the conflict lines of mod b say which). `--json` has `priority`, and a `winner` and a `loser` per
  conflict. The mod a line names as the winner is the one whose part is written: its value, its delete, its new
  row or its entry; the lower mod's part of that conflict is left out, and the rest of the lower mod still
  applies. The mods are planned from the highest down (edit lines, groups, skill lines and maps alike), so what a
  mod gets never depends on a third mod that loses, and new rows that two mods add at one place are written
  with the lower mod's rows first: where the highest of three changes a row that the middle one deletes, the row stays and the lowest
  one's change of another field of it is written too. A whole entry (`<entry file=...>`) against an edit of
  that entry goes the same way: the higher mod's entry is written as that mod has it, and the higher mod's edit
  is made in the shipped entry, without the lower mod's file (in the lower mod's entry only where that mod adds
  an entry the game does not ship). A line that is mirrored to another target is decided against the lines
  for that target like any other line, and such a conflict stops `apply` like any other.
  A few conflicts have no winner and name none: two mods whose id ranges overlap, a new id that two mods add
  in different places, a mod that contradicts itself (it changes a row and deletes it, sets one value twice, or adds one row twice;
  this is found also where a higher mod has both of its lines left out),
  and a file in the game folder that this tool did not write. No priority decides these, so they stop `apply`
  with `--allow-conflicts` as well, until the mods are changed.
  A mod's own contradictions and errors are the same in every plan, whatever the other mods do. Looking for
  them costs plan time only for a mod that has a conflict with another mod in an entry where it has two or more
  lines, or whose shop, drop box or skill lines meet another mod (measured with one large mod under a delete:
  up to about twice the plan time, and none where the mods do not meet).
- After writing, the changed entries are read back from the copy and compared. `--verify-all` also compares every
  untouched entry with the original.
- Point the game at the copies yourself (or copy them over a **copy** of the game folder). For the server see
  [How the game gets the changes](#how-the-game-gets-the-changes).
- `--game` takes the folder of a **copy** of the client (the folder that holds `Data`) and writes the client's part
  as loose files there, which the client reads without anything else being done; see
  [How the game gets the changes](#how-the-game-gets-the-changes). The archives of the game folder are not changed.
  With `--game` alone the server's part of the mods is planned and reported as left out; give `-w` as well and the
  server archive is written to the workspace in the same run.

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
        source="client" ref="ms2/commonfiles/petloader.cpp:120"
        use="whole number kept with the pet; the pet window reads it"/>
</notes>
```

`source="client"` means the client's loader reads the attribute (`ref` cites the file and line of the read,
`use` says in our own words what the loader does with the value; neither copies code or data);
then the "never set" warning becomes a note that shows both. `source="maple2"` means only the Maple2 server code says so: it is shown
as unverified and the warning stays. The order of trust is shipped data, then the client loader, then Maple2.

`modkit schema cite <archive> <table> <element> <attr> --src DIR` helps to write such a line. It lists the lines of
a source tree that hold the attribute name as a string, the files that also name the table first (`table`), the
rest after them (`other`), and prints a draft `<attr>` line for the first `table` hit. It only searches text: read the
cited line before you add the draft, because the same attribute name is read for many tables.

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

Copy `MS2.Tables.dll`, `MS2.ModKit.dll`, `plugin.xml` and `schema-notes.xml` from `tools/modkit/plugin/bin/Release/` into
`<Toolbench>/Plugins/MS2.Tables/`.

Settings (menu **Utilities > Settings...**, page "MS2 Mods"; per user, all empty by default). While a setting is empty its
environment variable is used, so a script can start Toolbench with the folders already set:

| Setting | Environment | Meaning |
|-|-|-|
| Korean client data folder | `MS2_CLIENT_DATA` | the `Data` folder of a Korean client (`kms-client`, holds `Xml.m2d`). Only read. Empty when you have none |
| Global client data folder | `MS2_GMS_CLIENT_DATA` | the `Data` folder of a global client (`gms-client`). Only read. Empty when you have none |
| Server data folder | `MS2_SERVER_DATA` | the data folder of your local server (holds its `Xml.m2d`). Only read |
| Mods folder | `MS2_MODS` | a folder of mod folders. Save mod writes `<mods>/<mod id>/mod.xml`; the view lists its mods (a folder whose name starts with `.` or `_` is left out) |
| Workspace folder | `MS2_MOD_WORKSPACE` | where Apply writes patched copies of archives: the server's always, the client's when you apply to the workspace |
| Game folder | `MS2_GAME_DIR` | the folder of a **copy** of your client that holds `Data`. Apply writes the client's changed tables there as plain files |
| Schema cache folder | `MS2_MOD_SCHEMA` | optional: the folder `modkit schema build` wrote (`out/modkit/schema` by default). When set, Plan and Apply check the mods against it; both empty = no check |

One data folder is enough to look around; saving needs the mods folder, and apply the game folder or the workspace
folder as well. After changing a setting press **Reload settings** in the view.

Using it:

1. The view opens docked at the right edge, at 60 % of the window (menu **MS2 Mods > Tables** shows the view
   again). Pick an archive, type part of an
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
4. **Save mod** writes the mod folder. **New mod** starts an empty mod with the id in **Mod id** (the first mod is
   called `my.mod`).
5. The **Mods** list shows the mods of the mods folder. A checked mod is applied, an unchecked one stays in the
   folder and is left out. The list is the priority: the numbers start at the top with 1, the highest, and where
   two mods set the same thing the higher one wins. **Up** and **Down** move the selected mod, or drag it to its
   place with the mouse. A mod folder that is new goes to the end of the list; the mod being edited, when it is
   not saved yet, is above all. **Higher mod wins a conflict** under the list decides what a conflict does: off,
   Apply writes nothing while mods conflict; on, Apply goes on and the higher mod's change is written (a conflict with no winner stops Apply
   either way). Either way
   Plan shows the order first, then how many parts of a mod are left out and for which mod (`c wins 2 parts of b`), then the
   conflicts, every conflict line beginning with its winner (`conflict, X wins:`). A click on a mod's box
   checks or unchecks it; a click on its name selects it. The box is kept in the Toolbench settings and
   holds for the other MS2 views too. **Load mod** (or a double-click) loads the selected mod into the
   grid. The mod being edited is marked and always takes part, checked or not. The checks and the order are kept in
   `modkit-mods.xml` in the mods folder; the mod folders themselves are not changed by them. (A list saved by an
   earlier version had the winner at the bottom; it is turned round when it is read, so the same mod still wins.) When the view opens and
   the mods folder already has a mod with the id being edited (`my.mod` at first), that saved mod is loaded, so work
   goes on where it was saved. When Toolbench is closed with changes that were not saved, it asks first: Yes saves
   the mod, No drops the changes, Cancel keeps Toolbench open. Closing only the view's pane hides it and keeps the
   changes.
6. **Apply to** chooses where the client's tables go: **game folder (loose files)**, the default once a game folder is
   set, or **workspace (patched archives)**.
7. **Plan** shows what the checked mods plus the pending edits would change, with errors, conflicts and
   warnings, and writes nothing. **Apply** saves the mod being edited and writes; with an error or a conflict it
   writes nothing and says why. **Revert** removes what Apply wrote. The three are also in the **MS2 Mods** menu.
   They run in the background: the view is greyed out and shows the progress beside the buttons until the result
   is in the **Output** pane.

### How the game gets the changes

**Client: loose files.** The client looks for a data file as a plain file under its `Data` folder before it looks
into the archives: `Data/Xml/table/pet.xml` is used in place of the entry `table/pet.xml` of `Data/Xml.m2d` (a file in
a region sub-folder of `Data/Xml`, if there is one, comes before both). So with **Apply to: game folder**, Apply writes
each client entry that a mod changes, whole and merged, to `<game folder>/Data/Xml/<entry>`, and the archives of the
game folder stay as they are. Start the copy of the client; nothing else is to be done.

- `modkit-loose.xml` in the game folder records every file Apply wrote, with its hash. **Revert** deletes exactly
  those files (and the folders Apply made, when they are empty) and nothing else. A recorded file that was changed
  since is left where it is and reported.
- A file that is already there and is not in the record is never overwritten. Plan lists it as a conflict and Apply
  writes nothing, also when conflicts between mods are allowed; move the file away yourself if the mod should win.
- A mod that deletes a client entry cannot be applied this way (a loose file can replace an entry, not hide it);
  Plan says so. Use the workspace target for such a mod.
- Use a copy of the client, as with every change to a game folder.

**Client: patched archives.** With **Apply to: workspace**, Apply writes `<workspace>/client/Xml.m2d` and `Xml.m2h`
(only the archives a mod touches). Copy them over the files of the same name in the `Data` folder of a copy of the
client after each apply; after Revert put the original files back the same way.

**Local server.** The server has no loose files. When a mod touches a server archive, Apply always writes the
changed archive to the workspace (`<workspace>/server/Xml.m2d`), whichever target the client's tables go to. What
is left to do depends on the server:

- A server that reads the archives directly takes the changed archive by copy: put it in place of the file of the
  same name in a copy of the server's data folder.
- A server that builds a database from the archives needs its ingest step run again on the changed archive before
  a change shows. modkit does not touch a database.

The original data folders in the settings are never written. Switching **Apply to** and applying again removes what
the other target left behind.

With the **Schema cache folder** set, Plan and Apply print the lines the command line prints with `--schema` (the view
goes through the same check): `warning: ... attribute glow is never set on ms2/pet in the shipped table/pet.xml` for
an attribute or element the shipped table never has, and a `note:` with the place and the use where
`schema-notes.xml` says the client reads the attribute. The notes file is the `schema-notes.xml` next to the plugin,
or the one of the checkout the plugin runs from. The view does not build the cache; `modkit schema build` does, and a
folder without one gives the note "no schema is cached for this archive". A warning never stops an apply.

Not in this editor yet: `<ids>` ranges, `<entry>` operations and edits
of element text. A mod that holds them loads, plans and applies as it is; the grid just has no cell for them.

## The item editor in Toolbench

The same plugin has a second view, **MS2 Items** (menu **MS2 Mods > Items**). It is for one thing: looking up an
item, changing it, and making a new item from a shipped one. It writes into the mod that the MS2 Tables view is
editing, so everything above holds: the same `mod.xml`, the same Save, mod list, Plan, Apply, Revert and the same
question when Toolbench is closed. An edit made in one view shows in the other.

### What one item is made of

An item is not one row. The view collects the pieces and shows them as sections of one grid:

| Section | Where it is | What it holds |
|-|-|-|
| client item | client `Xml.m2d`, `itemdata/NNN.xml`, one `<item id>` row | everything the client knows about the item: slot, icon, model, limits, the stat row it names. `NNN` is the id divided by 100000, and the client looks for an id only in the file of its block |
| name | client, `string/<language>/itemname.xml` | the name, once per language folder |
| plural | client, `string/<language>/itemnameplural.xml` | the plural name, where the language has one |
| description | client, `string/<language>/koritemdescription.xml` | the tooltip text and a second line |
| stat row | client, `table/itemoptionconstant.xml` | the fixed stats the item names by a code. **Shared**: many items name the same row |
| server item | server `Xml.m2d`, `item/<d>/<dd>/<id>.xml` | the server's own file for the item, in its own format |

A section that the data does not have for an item is simply not there. Without a server data folder the server
section is missing and the mod holds the client half only.

### Using it

1. Type an id, or part of a name, into **Item id or name** and press Enter. **Names in** picks the language folder
   whose names are searched and listed. The list shows shipped names; items the mod adds come first, marked `(new)`.
2. Pick an item. The grid shows every field with its value and the shipped value. Edit the value: the cell turns
   yellow, and typing the shipped value back takes the edit out of the mod.
3. **Client and server together** (on by default): an edit of a field that both sides hold is written to both, in
   each side's own format. Fields that exist on one side only are changed on that side only. With the box off, the
   edit stays on the side you typed it in and Plan warns about it. **Sync report**, **All fields** and **Server
   rows** are described under [One mod, both sides](#one-mod-both-sides).
4. **Name**, **Description**, **Second line** and **Set texts** write the texts into every language folder at
   once, so the item reads the same whichever one your client uses.
5. The stat row is shared. Changing it changes every item that names its code. **Own stat row** copies the row to
   the code you type (one that is not used yet) and points this item at the copy, so the edit stays with this item.
6. **Save mod** is the Tables view's Save. **Apply to**, **Plan**, **Apply** and **Revert** at the top of the view
   are the ones of the Tables view (the same target, mods and background run), with the result in the Items view's
   own output box; the Tables view does not have to be open. The **MS2 Mods** menu commands write to the output
   box of the view you used last.

### A new item: Clone to new id

Select a shipped item and press **Clone to new id**. With the id box empty the next free id after the source is
taken; or type one.

- The new id must be in the same block of 100000 as the source (`1200001` can be cloned to `1200004`, not to
  `1300000`). The client finds an item by the file of its block, and the block also says what kind of item it is.
- The id must not be used by the data or by the mod. It is written into the mod's `<ids>` so that two mods that
  take the same id are reported as a conflict.
- The clone gets its own copy of every piece: the client row, a name, plural and description row in every
  language, and the server's file under the new id. It keeps the source's icon, model and stat row until you change
  them.
- A clone cannot be cloned again; clone the shipped item once more. **Remove clone** takes a clone out of the mod.

### What is client-side and what the server decides

- **Client only, visible at once**: names, tooltip texts, icon, model and whatever else the client draws from its
  own tables. Apply to the game folder, start the client, and they show for every such item you meet, on any
  server. The server never sees these files.
- **Decided by the server**: whether an item id exists at all, who gets it, what it sells for, its level and job
  limits when you try to equip it, and the stats an item is rolled with. The server works from its own tables. A
  changed number in the client's tables alone changes at most what a tooltip says, not what happens.
- **How the server gets its half**: Apply writes the changed server archive to the workspace, and the server
  takes it by copy or by its ingest step, as said under
  [How the game gets the changes](#how-the-game-gets-the-changes). Until then the server does not know a changed
  or new item.
- **A new item shows nowhere by itself.** After Apply the client knows the clone, and nothing more happens until
  the server knows the id too and gives the item to a character (a shop, a drop, a reward or a server command).

## The NPC editor in Toolbench

A third view, **MS2 NPCs** (menu **MS2 Mods > NPCs**), does for npcs what MS2 Items does for items: look one up,
change it, or make a new npc from a shipped one. It writes into the same mod as the other two views, with the same
Save, Plan, Apply, Revert and the same question when Toolbench is closed.

One mod covers all three targets (see [Profiles](#profiles-which-data-you-have)). You edit an npc once; Apply
writes the Korean client's bundle row, the server's per-id file and the global client's per-id file in one go, and
Revert takes all of it back.

### What one npc is made of

| Piece | Where it is | What it holds |
|-|-|-|
| npc row | `kms-client`: `npcdata/NNN.xml`, one `<npc id>` row | what the Korean client knows about the npc: model, level, stats, name tag, effects. `NNN` is the id divided by 100000, and the client looks for an id only in the file of its block |
| npc file | `server` and `gms-client`: `npc/<aa>/<bb>/<id>.xml`, the id written with 8 digits | the same npc in the per-id format, plus fields only the server reads (for example `basic@difficulty`) |
| name, plural | `string/<language>/npcname.xml`, `npcnameplural.xml` | the name over the npc's head, once per language folder |
| title | `string/<language>/npctitle.xml` | the line under the name. Many npcs have none, or have it in a few languages only |
| function name | `string/<language>/npcfunctionname.xml` | the label of what the npc does (shop, quest, ...) |
| face | `emotion/npc/<id>.xml` | the npc's face animations, where it has them |
| talk | `server` and `gms-client`: `script/npc/<id>.xml` | the npc's talk script, where it has one |

Each target has its own set of language folders, and the editor writes to each target in the languages it ships.

### Using it

1. Type an id, or part of a name, into **NPC id or name** and press Enter or **Find**. **Names in** picks the
   language whose names are searched and listed. Npcs the mod adds come first.
2. Pick an npc. The grid starts with **Fields on every target (field map)**: the fields the Korean client and the
   server both hold, under the Korean client's names (`@level`, `stat@hp`, `model@kfm`, ...). Editing one of these
   writes it to every target, each in its own format. Below come the sections of each piece, with every field as
   that piece stores it; an edit there stays in that piece.
3. The value and the shipped value are side by side. An edited row turns yellow; typing the shipped value back
   takes the edit out of the mod. A new npc's rows are green.
4. The level is special in one way: where the server's `basic@difficulty` equalled the level (as most shipped npcs
   have it), it follows the new level. Where a Korean row has no level at all (the client then takes 1), the form
   shows the server's level, and an edit writes it to both.
5. **Name** and **Title** with **Set texts** write the texts into every language of every target. With **only in
   this language** ticked they go to the language chosen under **Names in** only. A language that has a title
   file but no title row for this npc gets one, so the title shows whichever language your client uses.
6. **Plan** lists every change, entry by entry, under one line per target. **Apply** writes them, **Revert** takes
   them back. **Sync report** and **All fields** show the mapped fields where the Korean client and the server
   disagree (see [The sync report](#the-sync-report)).

### A new npc: Clone to new id

Select a shipped npc, fill the boxes beside **Clone to new id** and press it. Empty boxes keep the source's values;
an empty id box takes the next free id of the source's block.

- The new id must be in the same block of 100000 as the source: the client reads an npc from the file of its block.
- The id must be free in the data and in the mod. An id counts as used when the data ships any piece of it,
  including a face or talk file of an id that has no npc (shipped data has some). A clone would replace such a
  file, so the editor refuses the id and the next free id skips it.
- The id is written into the mod's `<ids>`, so two mods that take the same id are reported as a conflict.
- The clone gets its own copy of every piece the source has: the npc row, the npc file, the name, plural, title and
  function name rows in every language of every target, and the face and talk files. It keeps the source's model
  and everything else until you change it.
- The copied rows are the source's rows as shipped, not as your mod has changed them: a name or title you gave the
  source does not carry over. Set the clone's own texts with its Name and Title boxes.
- A clone cannot be cloned again; clone the shipped npc once more. **Remove clone** takes a clone out of the mod.

Where a new id is written, per target (the plan view also lists this for a new npc):

| Target | Places |
|-|-|
| `kms-client` | the `<npc id>` row in `npcdata/NNN.xml`; a row in each string file of each language where the source has one; `emotion/npc/<id>.xml` where the source has one |
| `server` | `npc/<aa>/<bb>/<id>.xml`; the string rows as above; `emotion/npc/<id>.xml` and `script/npc/<id>.xml` where the source has them |
| `gms-client` | the server's lines, mirrored: the same files and rows, without server-only fields and without languages it does not ship |

Nothing else names an npc that merely exists. Map spawn lists, finder lists and quest tables name npcs that a
feature uses. A new npc appears where the server spawns it: by a GM spawn command, or by a spawn or quest table
that a further mod changes.

### What the client shows and what the server decides

- **Client side**: the name, title and model the client draws, and the level and HP bar it shows, come from the
  client's own tables. Apply to the game folder, start the client, and they show.
- **Server side**: whether the npc exists in a map, its real level and HP, how hard it hits and how much
  experience it gives are decided by the server's per-id file. The server takes its half from the workspace by copy
  or by its ingest step (see [How the game gets the changes](#how-the-game-gets-the-changes)).
- The field map keeps the two sides together, so a mod normally changes both. A field changed on one side only is
  a warning in Plan.

## Skills and effects

A fourth view, **MS2 Skills** (menu **MS2 Mods > Skills**), changes player skills and their effects (buffs,
damage over time and the like). It writes into the same mod as the other views, with the same Save, Plan, Apply,
Revert and the same question when Toolbench is closed. The ids and names in this section are made up.

### What one skill is made of

| Piece | Where it is | What it holds |
|-|-|-|
| skill | `kms-client`: a `<skill id>` row in one of the `skilldata/*.xml` files | what the Korean client knows: every level, each level's motions (the moves the character makes) and each motion's attacks |
| skill file | `gms-client` and `server`: `skill/<id / 1000000>/<id>.xml`, one file per skill | the same skill in the per-id format; the server decides the real damage and cooldown from it |
| effect | `kms-client`: an `<additional id>` row in `additional/*.xml`; `gms-client` and `server`: `additionaleffect/<id>.xml` | a buff or debuff a skill puts on: how long it lasts, how often it ticks, how many times it stacks |
| names, tooltips | `string/<language>/...` files of each target | the skill's name and the text of its tooltip, per level |
| job tree | `table/job.xml` on each target | which skills a job has, and where each sits in the skill window |

A level can be written more than once in one file (for one game feature or another, or for one country or another).
The game reads one of them; the view shows that one, and Plan warns that the others are left as they are.

### Using it

1. Pick **Skills** or **Effects** at the top left. Type an id, or part of a name, and press Enter or **Find**. The
   skills and effects the mod adds come first; a `*` marks the ones the mod changes. **Names in** picks the language
   of the names: **own language** (the default) shows Korean names from the Korean client and English names from the
   global client and the server. A target that has no names in the chosen language shows its own, and the line on the
   right always says which language each target gave.
2. Pick one. The icon, the name and a **Level** list appear above the grid. The grid shows the level's fields, then
   each motion (`Motion 1`, `Motion 2`, ...) with its fields, then each attack (`Motion 1 attack 1`, ...).
3. Each target has its own column of shipped values. They are shown in the same units everywhere: seconds for times,
   `x` for multipliers (a damage of `1.5` is 150 %). `-` means the target does not have that field there.
   `1 (default)` means the target leaves the field out and the game uses its default (a motion's speed is 1 when it
   is not written). Setting such a field in the mod writes it.
4. Type into **mod value** to change a field for that level. A number sets it; `*1.2` multiplies each target's
   shipped value by 1.2. An empty cell takes the change out of the mod. Changed rows turn yellow.
5. Under the grid, **All levels** shows every level of the skill at once, one row per level, for the target picked
   in **values of**. A cell the mod changes is yellow and shows the result (a `*0.5` on a cooldown of 8 shows 4);
   its tip gives the shipped and the mod value. `(d)` marks a default. A click on a row shows that level above.
6. **Tooltip of the level** sets the tooltip text of the chosen level: **kms param** is the value list the Korean
   client fills into its tooltip, **gms text** the whole text the global client and the server show, in the
   language picked in **lang** (`en` when empty; the other languages keep their text). When you
   change a number the tooltip shows (damage, cooldown, cost, duration), Plan warns until the tooltip is set too.
7. **Plan**, **Apply** and **Revert** work as in the other views, one line per target in the box at the bottom.

The fields the view offers:

| Skills | Effects |
|-|-|
| level: `cooldown` (s), `sp`, `ep` (the spirit and stamina cost) | level: `duration` (s), `interval` (s, time between ticks), `stacks` |
| motion: `speed` (x, how fast the move plays) | |
| attack: `damage` (x), `distance`, `width`, `height` (the area hit), `targets` (how many it hits) | |

Motions and attacks have no names in the data; they are told apart by their place (the first, the second, ...). If
the targets do not have the same number of motions or attacks in a level, Plan warns, because "motion 2" may then
be a different move on each target.

### A new skill: Clone to new id

Select a shipped skill, fill the boxes beside **Clone to new id** and press it:

- **id**: the new id. New skills belong in 78000000-78099999 and new effects in 77100000-77199999, the ranges kept
  free for mods; another id works but Plan warns. A target that already ships the id is skipped with a warning.
- **levels**: which levels to copy (`1-4`, `1,3`); empty copies all of them.
- **name**: the new skill's name; empty keeps the source's.
- **own effects**: `source=new` pairs, comma separated (for example `71000001=77100001`). Each named effect of the
  source is copied to the new id too, and the clone points to its own copy, so changing the copy leaves the
  original skill alone. Effects not named here stay shared with the source.
- **own cooldown group** (skills only): skills of one cooldown group share their cooldown, so using one starts the
  other's. A clone keeps the source's group unless this is ticked; then it gets a group of its own (its new id).
  Plan warns when a clone shares a group with its source.

**Remove clone** takes a clone out of the mod.

### Job skill tree

**Job**, **replace skill** or **position x,y**, **tier** and **Set job row** put a skill into a job's skill window:

- With **replace skill**, the new skill takes that skill's slot (place, tier and requirements stay).
- With **position x,y** (and tier 1 or 2), it gets a new slot at that place. Plan warns if the place is taken. How
  large the window's grid is, is not known yet: a position far outside the shipped ones may not show.

### What the client shows and what the server decides

- The client draws the icon, name, tooltip and the move itself from its own files.
- How much damage a hit does, how long a cooldown really is and how long an effect lasts are decided by the server
  from its own files. The view changes both sides together; a field changed on one side only is a warning.

### mod.xml for skills

The view writes these elements; they can be written by hand too:

```xml
<skill id="70000001">
  <level value="1-4" cooldown="6" sp="*0.5"/>
  <motion level="1" index="1" speed="1.2"/>
  <attack level="*" motion="1" index="1" damage="*1.2" distance="900"/>
  <tooltip level="1" kms-param="..." gms-text="..." lang="en"/>
</skill>
<skill id="78000001" clone="70000001" levels="1-4" name="My Slash" cooldown-group="own">
  <own effect="71000001" as="77100001"/>
</skill>
<effect id="71000001">
  <level value="1" duration="20" stacks="3"/>
</effect>
<jobskill job="10" skill="78000001" replace="70000001"/>
<jobskill job="10" skill="78000002" tier="1" position="5,6"/>
```

`cooldown-group` takes `own` (the clone's id) or a group number. `lang` on `<tooltip>` names the one language the
gms text is written to (`en` when left out).

`level` takes one level, a list (`1,3`), a range (`2-4`) or `*` for all. A level, motion or attack a target does
not have is skipped there with a warning.

Two more elements are for hand-written mods only (the view does not offer them):

- `<raw side="server" level="1" at="..."><set name="..." value="..."/></raw>` sets an attribute the field list does
  not cover, on one target, at an element path inside the level. The value is written as it is, in that target's
  own units.
- `<swap level="1" motion="1" from-skill="70000002" from-level="2" from-motion="1"/>` replaces a motion (the move
  and its attacks) by a copy of another skill's motion.

## Shops and drops

A fourth view, **MS2 Shops and Drops** (menu **MS2 Mods > Shops and Drops**), edits shops and drop boxes. A shop
or a box is one id with many rows: a shop is one header row and its stock rows, and a drop box is groups of item
rows. The view writes into the same mod as the other three, with the same Save, Plan, Apply and Revert. The NPC view
has **Open shop** and **Open drop box** buttons that open the shop or box an npc points at.

### What a shop or drop mod looks like

A mod does not hold a copy of the shop or the box. It holds the changes, row by row: "set the price of row 3",
"add this row", "remove row 4". So a mod still applies where the copies you have ship slightly different rows,
and two mods that change different rows of one shop do not get in each other's way.

```xml
<shop id="1234">
  <header><set name="iconName" value="my_icon" /></header>
  <row sn="3"><set name="price" value="500" /></row>
  <add><xml><item id="50001234" price="100" paymentType="5" category="Etc" /></xml></add>
  <remove sn="4" />
</shop>

<dropbox id="5678">
  <row group="1" item="50001234"><set name="itemID" value="50004321" /></row>
  <add group="1"><xml><v itemID="50001111" minCount="1" maxCount="1" weight="10" /></xml></add>
</dropbox>
```

- **Which row is meant.** A shop row is named by its `sn`, the row number inside that shop. Numbers are counted per
  shop: row 3 of one shop has nothing to do with row 3 of another (most numbers are used by many shops). A new row
  without an `sn` gets the next free number of its shop; Plan says which. A drop row is named by its group, its item
  and, when one group lists the same item more than once, which of them it is (`n="2"` for the second). Two rows with
  the same item are never treated as one.
- **A row that is not there.** When a target does not have the row a change names, Plan shows a warning and Apply
  leaves that target out for this change. Everything else is still written.
- **New and cloned shops and boxes** are written whole: `new="true"` with the complete header or box in `<xml>`, or
  **Clone to new id** in the view, which copies a shipped one (or one your mod adds) under a new id. New ids come from
  the mod's reserved range: shops from 900000000 to 900009999, drop boxes from 900010000 to 900019999. The ids go into
  the mod's `<ids>`, so two mods that take the same id are reported as a conflict.

### Shop stock is server only

What a shop sells, at which price and in which currency is decided by the server alone. The server ships the shop
tables; the clients do not, and Apply writes shop rows to the server target only. On the client targets Plan only
checks things: that the items a shop sells exist in each client, and that each stock row's category is one of the
client's shop tabs. To try a shop change, the server has to take the new data (see
[How the game gets the changes](#how-the-game-gets-the-changes)).

Which shop an npc opens is a field of the npc (`shopId`), changed in the NPC view. Plan checks that the shop exists on
the server once the mods are applied.

### Drops: weights are shares

A drop box holds groups, and each group holds rows. Each row's `weight` is its share of the group, not a percentage:
weights 1 and 3 mean one time in four and three times in four. Making every weight bigger by the same factor changes
nothing. To make an item drop every time, give it a group of its own with that one row.

The game data stores drops in two layouts. The server and the Korean client keep one box with its groups inside
(the "nested" layout). The global client, and the server in a second copy, keep one flat row per item that names its
box and group (the "flat" layout), spread over several files. A mod is written against the nested layout; modkit
writes the same change into the flat copies. The view's **Show** box lets you look at each copy. A new box goes into
the server's nested table, the Korean client's nested table and, in the flat copies, the file
`table/individualitemdrop.xml`; a mod can name another of the flat files with `split="..."`. Region folders of the
flat layout are never created.

The global client's copy of the drops only shows a preview: what a box may drop, in the item's tooltip or a
drop-info window. What really drops is decided by the server.

### What the warnings mean

| Warning | Meaning | What to do |
|-|-|-|
| `this target has no such shop` / `box; its changes are skipped here` | This copy of the data does not have that shop or box | Nothing, if the copy is not meant to have it (shops are never in a client) |
| `no stock row sn N here; skipped` / `no row group 1 item ... here; skipped` | The row the change names is not in this copy | Check the sn, group and item. The other copies still get the change |
| `... is not an item id` / `is not an item of the ...` | A stock or drop row names an item that does not exist in that target | Fix the item id, or add the item in the same mod |
| `paymentType "N" is not a known currency (0 to 14)` | The currency number is not one the game uses | Use a known currency |
| `paymentType 1 is paid with an item, and paymentItemID is not set` | Currency 1 means "pay with an item", and no item is named | Set `paymentItemID`. Only the item id before an optional `:` suffix is checked |
| `categoryID N is not a category of the ...` / `category "X" is in no tab ...` | The client's shop tab list does not know the shop's category or the row's tab | Use a category and tab the client ships |
| `every row of group N has weight 0` / `group N has no rows` / `the box has no groups` | The group (or box) can drop nothing | Give a row a weight above 0, or remove the group |
| `isApplySmartGenderDrop is "..."; this layout writes ...` | Yes/no fields are spelled `1`/`0` in one layout and `True`/`False` in the other | Use the spelling the message names |
| `shopId N is not a shop on the server after the mods` | An npc points at a shop that does not exist | Fix the npc's `shopId`, or add the shop |
| `... is not a drop box of the ... after the mods` | An npc's drop box id is not in that target | Fix the id, or add the box |
| `the box id is already used on this target` | A new box takes an id that is taken | Use a free id from the reserved range |

### What Revert does not undo

Revert puts the data files back. It does not touch the game's database. When a character has bought from a shop,
the server can keep per-character shop state there, such as how much of a limited stock row is bought. That stays
after Revert, and also after the shop is removed. Clear it with your server's own tools if you need a clean state.

## Quests

The **MS2 Quests** view (menu **MS2 Mods > Quests**) edits one quest on every target at once: the Korean client,
the global client and the server. It writes into the same mod as the other views, with the same Save, Plan, Apply,
Revert and the same question when Toolbench is closed.

### What one quest is made of

| Piece | Where it is | Who reads it |
|-|-|-|
| table row | `kms-client`: `questdata/questdata.xml`, one `<quest id>` row | the Korean client. The row is sparse: an attribute at its default and an empty element are left out |
| quest file | every target: `quest/<id>.xml`, the id written with 8 digits, `<environment>/<quest>` | the server and the global client. The Korean client ships these files too, beside its table |
| texts | `string/<language>/questdescription*.xml`, one `<quest questID>` row with `name`, `desc`, `manual`, `complete` | the client shows all four; the server uses the name. Some languages have one file, others one file per quest group; the editor finds the row in any of them |

The fields of the view, with the element and attribute they are in the table row and in the quest file alike:

| Field | Where |
|-|-|
| level requirement | `require@level` |
| start NPC, complete NPC | `start@npc`, `complete@npc` |
| reward exp, reward meso | `completeReward@exp`, `completeReward@money` |
| reward items | rows with `code`, `rank`, `count`: `completeReward/v` in the table row; `essentialItem`, `globalEssentialItem` and the two job lists (`essentialJobItem`, `globalEssentialJobItem`) in the quest files |
| items given on accept | the same lists under `acceptReward` |
| condition type, code, count | `condition@type`, `@code`, `@value` of the n-th `<condition>`; an empty or 0 count means once |
| texts | the four attributes of the text row, per language. `manual` is a list with `|` between the lines, one per condition |

### Using it

1. Type an id, or part of a name, into **Quest id or name** and press Enter or **Find**. **Names in** picks the
   language whose names are searched and listed. A quest your mod has renamed is listed under its new name.
   Quests the mod adds come first.
2. Pick a quest. Three grids fill the view: **Fields and texts** at the left, **Conditions** and **Reward items**
   at the right. Each line has the value, the shipped value and where the value is: the number of places, or each
   place with its own value when the targets do not agree.
3. Type into a value. The edit goes to every place of the quest: the table row and the quest file of each target.
   Where a sparse row has no such element or attribute yet, it is added. An edited line turns yellow and gets a
   `*`; typing the shipped value back takes the edit out of the mod. A new quest's lines are green.
4. A text goes to the language of its line, on every target that has that language. With **texts to every
   language** ticked it goes to the quest's row in every language.
5. In **Conditions** the type, the code and the count can be typed. The type must be one that a shipped quest
   uses (the tooltip of the cell lists them). The column **code is** says what the code stands for:
   - `free text`: a trigger name, a word or a number such as an amount. It can be typed.
   - `<table> id, read-only`: the code is an id of another table, and the view has no list of that table to pick
     from, so it is not edited here. Which table follows from the type: **npc** (`npc`, `talk_in`, `dialogue`,
     `skill_damage_npc`), **item** (`item_add`, `item_exist`, `item_inven`, `item_move`, `item_pickup`,
     `openitembox`, `equip_exist`, `install_item`, `uninstall_item`), **map** (`map`, `stay_map`, `survive_map`),
     **skill** (`skill`, `buff`), **quest** (`quest`, `quest_daily`, `quest_clear`), **interact object**
     (`interact_object`, `interact_object_rep`), **dungeon** (`dungeon_clear`, `dungeon_rank`,
     `dungeon_rank_clear`).

   Only the type decides this, by its full name. A type that is not in the list takes free text, also when its
   name starts like a listed one or its code is a long number.

   A type can only be changed to one whose code is of the same kind (`npc` to `talk_in` is taken, `npc` to `item`
   is not). To change such an id, edit the `<condition>` in the MS2 Tables view.
6. **Reward items** shows each list under its own name, first the lists given on completion, then those given
   when the quest is accepted (**On accept**). Shipped data does not always hold the same rows in the table's `v`
   list and in the files' lists, and not always the same rows on every target. A row of the grid is one item: the
   places are paired by item id, never by position. Where a place has no such row, or another rank or count, the
   last column says what each place holds (`-`: no such row there). Typing into a row changes it in every place
   that has it. **Add row** and **Remove row** work on the list chosen in **List**; a removed row is gone from
   the list, and removing a row your mod added leaves no line in the mod. **Same on every target** writes the
   chosen list's rows to `v`, `essentialItem` and `globalEssentialItem` everywhere, so the client shows what the
   server gives; the two job lists and the accept lists stay as they are.
7. **Plan** lists every change, entry by entry, under one line per target. **Apply** writes them, **Revert** takes
   them back.

### A new quest: Clone to new id

Select a shipped quest and press **Clone to new id**. An empty id box takes the next free id after the source.

- The id must be free in the data and in the mod, and at most 8 digits. It is written into the mod's
  `<ids space="quest">`, so two mods that take the same id are reported as a conflict. An id that another enabled
  mod reserves in its `<ids space="quest">` is not offered and not taken. When the `mod.xml` of an enabled mod
  folder cannot be read, the other mods still count, and the output starts with a warning that names the folder:
  the ids of that mod are not known.
- Rows of the source that are equal to each other (two equal lines in a server script table) are copied as they
  are: the clone gets two rows too.
- The clone gets a copy of the source's table row, of its quest file on every target (with `basic@questID` set to
  the new id), and of its text row in every language, in the file the source's row is in.
- The copies are the source as shipped, not as your mod has changed it.
- A clone cannot be cloned again; clone the shipped quest once more. **Remove clone** takes a clone out of the mod.
- With **with talk script and list rows** ticked (the default) the clone also gets a copy of every row that lists
  the source by its id, so that an npc offers the new quest as it offers the source:

  | Rows | Where |
  |-|-|
  | talk script, `<quest id>` with its scripts | `script/quest/questscript*.xml` on every target |
  | fields of the quest marker, `<quest id fields>` | `table/questtofieldtable.xml` on every target |
  | special type name, `<key id name>` | `string/<language>/specialquesttype.xml` on every target |
  | script conditions and functions, rows with `questID` | `table/Server/QuestScriptCondition*.xml` and `QuestScriptFunction*.xml` in the server's `Server.m2d` |

  The lines under the fields list what was copied. **Remove clone** takes these copies out too.
- The copied talk script speaks the source's lines at first: its texts (`string/<language>/scriptquest*.xml`,
  `<key id name>` rows) have ids of their own, and the copy names the same ids as the source. The grid lists them
  under **Talk texts**, one section per language, read only: changing them would change what the source quest
  says.
- **Own talk texts** gives the new quest texts of its own. Every text its talk script names is copied under a new
  text id, the quest id followed by `001`, `002`, ... (an id the data, the mod or another enabled mod already has or
  reserves is skipped), in every
  language and for every feature row, on every target; the copied script then names the new ids. Quest 705 cloned
  from a quest whose script names three texts gets the texts 705001 to 705003. After that the Talk texts lines can
  be edited like the other texts (one language, or every language with **texts to every language** ticked). A
  text with a row per feature has one line per feature, named first in the **where** column (`F1, 2 row(s)`), and each line sets its own rows. The
  **shipped value** column of an own text shows the source quest's text, so a changed line is marked and the
  original stays in view. The new text ids are recorded in the mod as id ranges, each on the targets that got a
  row, so Plan reports another mod that adds the same ids.
  **Shared talk texts** undoes it: the copies leave the mod and the script names the source's texts again. A
  text name set by hand in the MS2 Tables view is left as it is. **Remove clone** takes the copies out too. Both
  are refused while another quest of the mod names one of these texts by hand: give that quest another text
  first.
- Limits: a text the script names but no file has a row for stays the source's id. A quest holds at most 999
  texts of its own; when the ids do not suffice nothing is copied. The talk texts of a shipped quest are not
  edited here; use the MS2 Tables view.
- Tables that name a quest without being keyed by it (quest masks of npcs and portals, guide and learning
  entries) are not copied; add such rows in the MS2 Tables view.

### What the client shows and what the server decides

- **Client side**: the quest's texts, the level, npcs and rewards shown in the quest window come from the client's
  own table (Korean client) or quest file (global client).
- **Server side**: whether a quest can be taken, what counts towards a condition and what is given on completion
  are decided by the server's quest file. For reward items the server gives `globalEssentialItem` when that list
  is not empty and the global feature is on, else `essentialItem`.
- The Korean client's table has no field map to the server's quest files; the view keeps the sides together by
  writing each edit to all of them. A quest edit made by hand on one side only is a warning in Plan (see
  [What the warnings mean](#what-the-warnings-mean-2)).

## Maps

A map mod changes a map the game already ships: it adds, moves or removes props, monster spawn places, NPC spawn
places and portals. There is no view for it yet; a map mod is written by hand in `mod.xml` and applied with the
command line. All names and numbers below are made up.

### What a shipped map is

Each map is one file, `xblock/<map name>.xblock`, inside the archive `Resource/Exported.m2d`. The file is a list of
entities. An entity has an id (32 hex digits), a model name (what kind of thing it is), a display name, and only the
properties that differ from the model's defaults, such as its position. The defaults of every model are in the
`.flat` files of the same archive.

Which map file the game loads for a map id is said by the map table in `Xml.m2d`: the rows of
`table/fielddata.xml`, or one file `map/<id>.xml` per map. A row can name more than one map file, one per feature
(`<environment feature="..."><xblock name="..."/>`), and the features that are on decide which one is loaded. The
archive may also hold map files that no row names; the game never loads those. A plan warns when a mod changes such
a file, and when the changed file is one of several of its map but not the one the target loads (see the warnings
below). Before you write a map mod, look the map id up in the map table and take the file name from there.

Which features are on is worked out per target from its own `Xml.m2d`:
`table/feature_setting.xml` gives a level for a server type and a locale (`<setting type="Live" KR="10" NA="5"/>`),
`table/feature.xml` gives the level each feature switches on at (`<feature name="MadeUp" KR="8" NA="7"/>`). The
client and the server's data import do not pick the same way, so each target is worked out by its own rule:

| | A client (`kms-client`, `gms-client`) | The server (`server`) |
|---|---|---|
| A feature is on | its level is not 0 and not above the level of the server type | its level is not above the level of the server type; a feature with level 0 or with no level for the locale is on |
| Server type | any letter case | written exactly as in the `type` of the setting row (`Live`, not `live`) |
| Locale | any locale the tables have a column for | one of TW, TH, NA, CN, JP, KR |
| Rows with the target's locale and rows with no locale | compared by feature like any two rows; for the same feature the row with the locale wins | when a row with the locale can be taken, rows with no locale are not looked at |
| Two rows whose features are both on | the higher level wins; at the same level the feature whose name sorts later | the higher level wins; at the same level the row written first |
| A row with a feature and a row with none | the row with the feature | the row with the feature |
| Two rows with no feature | the first | the last |

The tool takes locale `KR` for `kms-client`, `NA` for `gms-client` and `server`, and server type `Live`. If your
game runs with another one, say so with the setting `MS2_CLIENT_FEATURES`, `MS2_GMS_CLIENT_FEATURES` or
`MS2_SERVER_FEATURES`: `LOCALE/TYPE`, for example `NA/Dev`, `KR`, or `/Dev`. The setting can be in the environment,
in `modtools.toml`, or in the Toolbench view: the view has one field per target under its top bar, and what you type
there is saved with the other MS2 Mods settings. The environment wins over `modtools.toml`, and both win over the
field; the field is then grey and its tooltip says where the value comes from.

When to pick something other than `Live`:

- A client takes its server type from the `-env` start argument, or else from the server list the login server
  sends; its locale from `-locale`. Set the client's setting when your clients are started with another `-env` or
  `-locale`, or when your server's list names another type.
- The server takes locale and type when its data import runs; a stock import uses `NA` and `Live`. Set
  `MS2_SERVER_FEATURES` only when you changed that in your import (for example to a development type, to switch on
  features whose level is above the `Live` level, or to another locale's content). Write the type exactly as the
  setting row does, and run the import again after applying a mod, since the server reads its database and not the
  archive.
- Keep the two sides the same. A client that loads one map file while the server loaded another one shows a map
  that does not fit what the server knows about it.

Each client has its own copy of the archive and the server reads one too. The Korean and the global client do not
always hold the same bytes for a map, so a map mod is never a whole replacement file. It is a list of small
operations, and each copy gets the same operations applied to its own file.

### What a map mod looks like

```xml
<mod id="my-map-mod" name="A crate, some wolves and a door" version="1">
  <map xblock="90000001_ab">
    <add name="crate" model="Crate_A" entity-name="my_crate">
      <property name="Position" value="150, 300, 450" />
    </add>
    <change id="0123456789abcdef0123456789abcdef" model="Crate_A" position="0, 150, 300">
      <set name="Position" old="0, 150, 300" value="0, 150, 450" />
    </change>
    <remove id="fedcba9876543210fedcba9876543210" model="Crate_A" position="300, 300, 300" />
    <spawn name="wolves" model="SpawnPoint_A" entity-name="my_wolves" point="140000">
      <property name="Position" value="450, 300, 450" />
      <row difficulty="1" tag="wolf" coolTime="20" population="3" />
    </spawn>
    <portal name="east" model="Portal_A" entity-name="my_door" id="50000" to-map="90000002" to-portal="50000">
      <property name="Position" value="600, 300, 450" />
    </portal>
  </map>
  <map xblock="90000002_ab">
    <portal name="west" model="Portal_A" entity-name="my_door_back" id="50000" to-map="90000001" to-portal="50000">
      <property name="Position" value="0, 300, 450" />
    </portal>
  </map>
</mod>
```

- `<add>` puts a new entity into the map. `name` is your own name for the operation and must be unique inside the
  `<map>`. The new entity's id is worked out from the mod id, the map name and that name, so it is the same every
  time and on every copy. Copy the model name and the properties from an entity of the kind you want that is already
  in the map.
  The order in which you list the properties does not matter: they are written in the order the shipped
  entities of the same model in that map file use, and in the order the model declares them when the file has no
  such entity.
- `<change>` sets or removes properties of an existing entity: `<set name="..." old="..." value="..."/>` and
  `<unset name="..." old="..."/>`. A property with several values takes `index="..."` to name one of them.
  `iterations="..."` on the `<change>` sets the entity's edit counter (`map-diff` writes it for an entity the map
  editor touched; by hand you leave it out).
- `<remove>` takes an existing entity out.
- `<spawn>` adds a place where monsters appear. `point` is its number on this map. The place alone spawns nothing:
  what appears there is decided by a row in the table `table/mapspawntag.xml`, keyed by the map and that number.
  The server reads it; the clients ship a copy of it too. Each `<row>` inside the `<spawn>` becomes such a row in
  every copy of the table you have set up (server, Korean client, global client), so the copies stay equal. The map
  and the number are filled in for you, and the rows are put right after the map's last existing row (at the end
  of the table when the map has none). Copy the attributes from the row of a shipped spawn place whose monsters
  you want.
- `<portal>` adds a portal. `id` is its number on this map, `to-map` and `to-portal` say where it leads. A portal
  works one way; for a way back, add a second portal on the other map that points here, as in the example.
- An NPC spawn place is a plain `<add>` of the map's NPC spawn model with its `NpcList` property:
  `<property name="NpcList"><set index="99000001" value="1" /></property>`.

### Expected old values

`<change>` and `<remove>` say what you expect to find: the entity's model, its position, and the old value of each
property you change. The tool compares that with each copy before it touches it. When a copy holds something else
(a different game version, or another mod was there first), you get a warning and the operation is skipped on that
copy only. This is what keeps a mod written against one client from damaging another whose map is not the same.
The tool never falls back to replacing the whole file.

### Reserved numbers

Numbers inside a map must not collide with the game's own or with another mod's.

| number | range kept for mods | why this range |
|---|---|---|
| portal `id` | 50000-59999 per map | no shipped map uses a portal number that high |
| spawn `point` | 140000-149999 per map | shipped maps already use spawn numbers in 50000-59999, and the server's spawn table goes up into the 90000s |

Both are read by the client as whole numbers of 32 bits, so the ranges fit. A number outside the range is allowed
and gives a warning. A number that is already used on the map is an error.

### Applying: each client and the server

```sh
modkit validate my-map-mod                 # errors, conflicts and warnings; nothing is written
modkit diff my-map-mod                     # the same, and every entity and row it would change
modkit apply -w workspace my-map-mod       # modded copies of the touched archives, in the workspace
modkit revert -w workspace                 # remove them again
```

One mod is applied to every copy you have set up (see [Profiles](#profiles-which-data-you-have)): the Korean
client, the global client and the server. Your original archives are only read. Apply writes modded copies into
the workspace, `Resource/Exported.m2d` for each client and for the server, plus the `Xml.m2d` of every copy that
ships the spawn table when the mod has spawn rows. Every other entry of an archive is carried over exactly as stored, and every entity you did not
name keeps its bytes. Revert removes the copies; the originals were never changed.

Many private servers do not keep their own map archive: their `Resource` folder is a link to a client's. The tool
sees that, writes the map once, and says so in a line of the plan. (The command line sees links; the build of the
library that runs inside Toolbench only sees that two folders have the same path.)

### The server must ingest again

The server does not read map files while it runs. It reads them once, during its ingest step, and stores what it
needs (portals, spawn places, the ground monsters walk on) in its database and in stored navigation meshes. So
after every apply or revert of a map mod:

1. put the modded archives where the server's ingest reads them,
2. remove the stored navigation meshes of the changed maps,
3. run the server's ingest again, then start the server.

The tool does none of these three; every plan of a map mod ends with a line that names the changed maps and says
so. Until it is done the clients show the change and the server does not know about it: a new portal does nothing,
a new spawn place stays empty, and monsters walk through a new prop.

Known limit: added blocks change what players stand on, not where monsters walk, until the map's navigation mesh is
rebuilt. Rebuilding needs PathEngine, which is not included here.

The `.tok` files (the client's own walkable-ground data) are not touched.

### What the warnings mean

| warning | meaning | what to do |
|---|---|---|
| `has no such map` | This copy has no such map | Nothing, if that game version does not have the map |
| `has no entity ...`, `is a "..." here, the mod expects "..."`, `stands at "..." here`, `property ... is ... here, the mod expects ...` | The copy does not hold what the mod was written against; the operation is skipped there | Look at that copy's map, then adjust or split the mod |
| `is outside the range kept for mods` | A portal or spawn number is not from the reserved range | Use a reserved number unless you need a low one on purpose |
| `is not in the model listing of this target` | No `.flat` file defines that model on this copy | Check the spelling; copy the name from a shipped entity |
| `has no property ...`, `is a list`, `is a plain value`, `is not a ...` | The model has no such property, or the value does not fit the property's type | Compare with a shipped entity of the same model |
| `NPC ... of the NPC list is not in the NPC data of this target` | No NPC with that id on this copy | Use an existing NPC id, or add the NPC in a mod of the same apply |
| `has no <row>` | A spawn place without a server row spawns nothing | Add a `<row>` |
| `the server has no such archive, the spawn rows are not written` | The server data has no `Xml.m2d` | Set the server data folder up first |
| `was put into no map file, so its spawn rows are not written either` | No copy took the spawn place (for example no copy has that map), so no row is written for it | Fix the map name, or what the other warning of that spawn names |
| `this target has no such table, the spawn rows are not written there` | That copy does not ship `table/mapspawntag.xml`. A warning on the server, a note on a client | On the server: set the data folder up first. On a client: nothing to do |
| `no map of this target's map table uses this map file, so the game does not load it` | No row of this copy's map table names the changed file. The file is still changed, and nothing shows in the game. When the table has a row for the map id (the leading digits of the file name, or `<map id="...">`), the warning lists the files that row names | Change the file the map table names |
| `with this target's features (locale ..., server type ..., level ...) the game loads ... for map ..., not this map file` (`the server loads` for the server target) | The map's row names several files, one per feature, and under this target's features the game (or the server) loads another one than the mod changes. The file is still changed, and nothing shows in the game | Change the file the warning names, or set the target's `..._FEATURES` setting when the locale or server type in the warning is not the one your game runs with |
| `this target's map table has no map ...` | The portal leads to a map id this copy does not know | Fix `to-map` |
| `that map has no such portal after this plan` | With all mods applied, the target map has no portal with the number of `to-portal` | Add the portal on the other map, or fix the number |
| `no portal of that map leads back to map ...` | There is no way back | Fine for a one-way door; else add one |

When the map's row names several files and the target's features are known, there is no message for the file the
target loads. When they are not known (no feature tables in the target's `Xml.m2d`, or no level for the locale and
server type), a note says so instead: `map ... has more than one map file in the map table` lists the files, each
with its feature. Put the same `<map>` block in for the other file too when both should change.

Errors stop the apply: an entity id or a number that is already in the map, a spawn row that already exists for
that map and number. Two mods that touch the same entity, or bring the same number, are a conflict.

### Converting maps for the map editor

Four commands convert between the game's map files and the files the Gamebryo map editor works on. They replace
the Python scripts of [tools/mapedit](../mapedit/README.md). modkit is the reference from now on: the files it
writes are the same bytes on every system (lines end with a line feed where the editor does not ask for another
end, names in lists have `/` between their parts, lists are in ordinal order of the names). What the map
commands print ends its lines with a line feed too. The Python scripts gave the same bytes for the shipped data on Linux; they are not kept in step.

```sh
modkit map-index <Exported.m2d or folder> index.json                # the model listing, once per game version
modkit map-to-block <map.xblock or folder> -o blocks --index index.json    # game map -> editor map
modkit map-from-block blocks/<map>.block -o exported --index index.json    # editor map -> game map
modkit map-normalize exported/<map>.xblock -o final --original <shipped map>   # put it in the shipped file's order and layout
```

### A map editor project for chosen maps

The map editor opens a project folder: the game's library of models, one preset per model the game ships, the maps
as editor files, and a solution file. One command builds that folder from the game's archives for the maps you name:

```sh
modkit map-project <map.xblock>... -o project --resource <the game's Data/Resource folder> \
    [--known-ids known_guids.json] [--all-presets] [--no-asset-files]
```

`--resource` names the folder that holds `Exported.m2d`, `Library.m2d`, `asset-web-metadata.m2d` and the `Model`
archives. Each of the first three can also be given alone, as an archive or an unpacked folder: `--flats`,
`--library`, `--assets`.

It copies the library, writes the model and preset files the game's flat files describe, and keeps only the presets
your maps use (and the ones those are made from) unless you pass `--all-presets`. `ASSETS.txt` lists the asset ids
found in the values of the maps' entities and of their models, with the file each one names. It does not list the
textures that those model files pull in: they are copied (next sentence) but have no line there. With `--resource`
those files (the model files of the props) are copied into the project at the path the game gives them, together
with the texture files each model file names; `--no-asset-files` leaves them out. `MISSING.txt` lists what was not
found: project entries whose file does not exist, asset ids without a path, files that are in no archive. The folder holds
copied game files: keep it local and out of any repository.

With `--resource` the game's `asset-web-config` (a folder or an archive in the Resource folder: three small settings
files) is copied beside the solution file as well. The map editor reports an error for a project that has none;
`MISSING.txt` says so when the Resource folder does not hold it. The last line of the copy reads
`copied N model files, N texture files and N other files`: other files are assets a map names that are not model
files. Asset paths that differ only in letter case (`/Model/x` and `/model/y`) go into one folder, as they do on
Windows; the letter case of a folder is that of the first path that names it. Two files whose paths differ only in
letter case are one file there: the one copied later takes the place of the other, without a message.

The library and the settings folder are copied under another name (`Library.copying`) and get their name only when
every file is there, so a copy that was stopped is not taken for a whole one: the next run starts it again. An
archive entry with an unsafe name, or one that names a folder instead of a file, stops that copy with an error and
nothing of it is kept.

Every name that comes out of a game file (a path in a project file, a model, map or layer name, an archive entry,
an asset path) is checked before anything is written under it. A name that is rooted, has a drive letter or a `:`,
has a `..` part, or would end outside the project folder is refused: it is listed on the error output as
`refused, not written: ...`, that one file is not written, the rest is, and the command ends with exit code 1.
A refused asset path is not read either, and the textures of a refused model file are not copied.
`map-models`, `map-solution` and `map-from-block` check the names they write the same way.

The two steps also exist alone: `modkit map-models <project folder> --flats SRC [--known-ids FILE]` writes the
model, preset and project files into a folder that already holds the library, and
`modkit map-solution <project folder> [NAME.gsl]` writes the solution file for what the folder holds. They take
the place of `flat2model.py` and `tools/toolbench/mkgsl.py`. The file of known model ids (`known_guids.json`) comes from
`tools/mapedit/knownguids.py`, which reads your own copy of the SDK and has no C# counterpart; without it a model the library does
not ship gets an id derived from its name.

### Editing the map in World Builder

Open the solution file of the project, open the map's block file, edit, and save with Save All.
- **Move, turn, change a value:** pick the entity in the view or in the Entity View list and type into the Entity Editor
  (Placeable holds Position and Rotation), or drag its gizmo.
- **Delete:** the Delete entry of the entity's context menu.
- **Add:** in the Palette View open a library with a double click (or use its Search tab) and drag a model from
  Contents into the view. The new entity is named after its model with a number (`Chair0`) and is selected; set its
  values in the Entity Editor. Under Wine this needs the launcher fix described in
  [tools/toolbench](../toolbench/README.md).
- **A second copy of an entity:** Cut, Copy and Paste of the Edit menu stay grey for entities on every system; that
  menu only serves text boxes. World Builder's own way: select the entity, switch on the Translation Gizmo in the
  toolbar, hold Shift and drag one of the gizmo's arrows. That makes a copy with all the values of the entity, named
  after it with a number (`Chair0` gives `Chair00`), and selects it. The copy is saved and converted like an entity
  added from the palette.

### From an edited map to a mod

A mod never carries a whole map file; it carries the edits. When you have the shipped map file and an edited copy
of it (from the map editor, put in the shipped file's order with `map-normalize --original`), this writes the mod:

```sh
modkit map-diff --id my-map-mod -o my-map-mod <shipped map>.xblock final/<map>.xblock
```

The map's name is the shipped file's name; give more pairs for more maps. An entity only in the shipped file becomes
`<remove>`, a new one `<add>` (`<portal>` or `<spawn>` when it carries those numbers), a changed one `<change>` with
the shipped values as the expected old ones. New entities keep their id and the order of their properties
(`entity-id="..."` and `order="..."`, which you do not write by hand). Before the mod is written the command
applies it to the shipped file and compares the result with your edited file, byte for byte; when they differ it
says so and writes nothing. The map editor counts an entity's `iterations` number up each time you edit the entity;
the `<change>` carries the new number as `iterations="..."`. Things a mod cannot say: another model or display name of a shipped
entity, the same properties in another order, another file layout. A new spawn point comes without server rows,
because a map file does not hold them: add its `<row .../>` lines by hand.

## One mod, both sides

The game's data exists in more than one copy. Your client has its own copy, and the server has its own, in different
files and partly in different formats. A mod that changes only the client's copy changes what a tooltip says. A mod
that changes only the server's copy changes what happens, while the tooltip keeps the old number. modkit keeps the
copies together: you change a field once, and every other copy you have is written for you.

### Profiles: which data you have

A **profile** is one known layout of the game data. modkit knows three:

| Profile | Archive name | What it is | Item data | Tables | Languages |
|-|-|-|-|-|-|
| `kms-client` | `client:Xml.m2d` | the Korean client | in bundles: `itemdata/<id / 100000>.xml` holds many items | `table/kr/...` | cn, de, en, jp, kr, pt |
| `gms-client` | `gms-client:Xml.m2d` | the global (North America / Europe) client | one file per item: `item/<a>/<bc>/<id>.xml` | `table/na/...` | de, en, pt |
| `server` | `server:Xml.m2d`, `server:Server.m2d` | the server's data | one file per item, as the global client | `table/na/...` | de, en, pt |

**Which one is your client?** Open your client's `Data/Xml.m2d` in the Tables view (or `modkit ls client:Xml.m2d
"itemdata/*"`). If it has an `itemdata` folder, it is a `kms-client`. If it has an `item` folder with one file per
item instead, it is a `gms-client`: set it as the global client folder, not as the Korean one.

A **workspace** works on the targets you have: any of the three, one, two or all of them. Set a folder for each one
you have and leave the others empty:

| Profile | Setting (page "MS2 Mods") | Environment | Command line |
|-|-|-|-|
| `kms-client` | Korean client data folder | `MS2_CLIENT_DATA` | `--client DIR` (or `--kms DIR`) |
| `gms-client` | Global client data folder | `MS2_GMS_CLIENT_DATA` | `--gms DIR` |
| `server` | Server data folder | `MS2_SERVER_DATA` | `--server DIR` |

`modkit targets` lists the targets that are set, with their folders. Each mod is applied to every target that is
set, in one Apply. Plan and Apply print a `targets:` line and then one line per target, for example:

```
targets: kms-client (Korean client), gms-client (global client), server
  kms-client: 1 change(s) (1 synced by the field maps) in 1 entry of 1 archive(s)
  gms-client: 1 change(s) (1 mirrored) in 1 entry of 1 archive(s)
  server: 1 change(s) in 1 entry of 1 archive(s)
```

After Apply and Revert, the lines say which file was written or removed for each target. The Tables view shows the
`targets:` line when it loads its settings, and the Items and NPCs views show the per-target lines of Plan, Apply and Revert
in its output box. A target that is not set is named on the `targets:` line as "not in this data set", and nothing
is written for it.

In a mod, `kms-client:` and `kms:` may be written for `client:`, and `gms:` for `gms-client:`. modkit stores them as
`client:` and `gms-client:`.

**Older workspaces and mods.** Before profiles, a workspace had a client and a server. That client is a `kms-client`,
and its archive name `client:` is still the name of the `kms-client` profile. Existing mods, the workspace's
`client` and `server` folders and its record of what was applied keep working as they are, with nothing to convert.
A global client is added by setting its folder. Its patched archives then go to a `gms-client` folder in the same
workspace.

### What is mirrored, and between which profiles

| From | To | How |
|-|-|-|
| `server` | `gms-client` | **mirrored**: the same entry, the same rows, the same values. Both use one file per item and the same table names |
| `gms-client` | `server` | mirrored the same way |
| `kms-client` | `gms-client` and `server` | **synced by the field map** (below): the value goes to the corresponding field of each item file |
| `gms-client` or `server` | `kms-client` | synced by the field map into the item's row in its bundle |

The global client and the server hold the same layout, but not the same fields. The server's files carry attributes
and tables that the client never reads. What the global client lacks is read from the global client's own shipped
archive, not from a fixed list. An attribute or element that never occurs in the global client's shipped copy of that
table is left out of the mirror, with a warning (see below). Whole files and new rows are copied without those
attributes. A change in the mod that has a place on both sides is mirrored. A table that only the server has is
changed on the server only, without a warning.

A mod that writes the target entry itself is not mirrored there: what the mod says for that side stands.

### The field map

modkit carries a **field map** for items (`MS2.ModKit/Maps/item.fieldmap.xml`). Each line pairs one attribute of the
Korean client's item row with one attribute of the item file of the global client and the server, and says how the
value is converted, for example:

| Conversion | Korean client holds | Item file holds |
|-|-|-|
| same | a value | the same text |
| bool | `true` / `false` | `1` / `0` |
| numlist | numbers separated by commas | the same numbers, written differently (`0.5, 1` and `0.500000,1.000000`) |
| affix | a bare name, or the full path | folder + name + extension |
| pick | one number | a list of numbers; the client's is the one at the position the item's rank gives |

When a mod changes a mapped field in one profile, Plan adds the change for each other profile that is set, converted,
and lists it like any other change. This happens for every mod at Plan time, whichever tool wrote the mod. Nothing
extra is stored in `mod.xml`. Four cases are left alone:

- The mod sets the other side itself. Your value stands, and if the two do not correspond Plan warns.
- The op carries `sync="false"`. It stays on its side, and Plan warns (see below).
- The two sides did not correspond for this item before the mod (shipped data has such items). modkit cannot tell
  what the other side should become, leaves it, and Plan warns.
- The other side has no row, file or element for that id. Plan warns.

The map was made by comparing the same ids of a Korean client and a server, field by field. Every line records how
many ids were compared and how many agreed, and a confidence: **high** (97 % or more agree), **medium** (90 % or
more) or **low**. A low line is still synced, because for most items the two sides are equal there. The sync report
shows the confidence, so check a low field in the report before you rely on it. Pairs with different names that
agree too rarely are kept as **candidates**: they are shown in the report and never written. Fields with no
counterpart are listed in the map as client-only or server-only and are changed only where they are.

Only items have a map so far. Tables that the Korean client and the server both hold in the same format (most of
`table/`) are not mapped. For those, use **Copy edits to other side** in the Tables view, or write both `<edit>`s.
Region tables are a separate case: the Korean client keeps them under `table/kr`, the others under `table/na`, and
modkit does not carry a change from one folder to the other.

The map names files, elements and attributes only. `--map FILE` (command line) uses another map file in place of the
built-in one, and `--no-sync` turns the field-map sync off for one run. Mirroring between the global client and the
server stays on.

### What the warnings mean

| Plan says | Meaning | What to do |
|-|-|-|
| `not mirrored from server:Xml.m2d: environment/drop @rate has no place in gms-client (its shipped ... never has it); N row(s) of the mod set it` | The field exists on the server but never in the global client's shipped copy of that table. It is changed on the server only | Nothing: the global client does not read it |
| `not mirrored from ...: ...` followed by another message | The mirrored change could not be made on the other side (the entry or a row is not there). The first side is still changed | Look at the entry on the other side. Write the change there yourself if it belongs there |
| `explicit one-sided override: ... on the client only (sync is off for it); server ... stays "..."` | A mapped field is changed on one side, and the op says `sync="false"` (or `--no-sync` is given). The two sides will differ | Nothing, if you meant it. Otherwise remove `sync="false"` |
| `@x is changed in N row(s) (first: ...) on this side only; ... holds the same table and there is no field map for it, so that side is not written` | An entry that exists on both sides, with no field map, is changed on one side only | Change the other side in the mod too. If the one side is meant, put `sync="false"` on the op; then Plan says nothing |
| `... is changed on the client only: the field map of "item" has no entry for it` | The changed field is in no line of the map, not even as client-only. modkit does not know whether the other side has a counterpart | Look the field up on the other side (Tables view, or **All fields**). If it is there under another name, change it there too |
| `explicit one-sided override: ... is set to "x" on the client and ... to "y" on the server; the two do not correspond` | The mods set both sides of a mapped field, to values that do not match under the conversion | Check which one is meant |
| `... is changed on the client only: the shipped values do not correspond (...), so the server is left as it is` | The two sides already differed for this item, so nothing was synced | Set the other side in the mod if it should change |
| `... has no <...> on the server ...; changed on the client only` | The other side has no place for the value for this id | Nothing, or add the element on the other side yourself |

A note (not a warning), `gms-client is not a target of this data set`, says when a mod names a target that is not
set. Its lines are left out there and the rest of the mod is applied. When the other per-id profile is set (a
`gms-client:` edit with only a server set, or the other way round), the note says the lines are planned for that
target only, and the change reaches it the same way as a mirrored one.

A warning never stops Apply.

### The sync report

The report answers "do the two sides agree for this item?", before and after the mods:

```
modkit sync <id>... [mod...]        the fields that differ or that the mods change
modkit sync --all <id> [mod...]     every mapped field
```

In the Items view, select an item and press **Sync report** or **All fields**; the report covers the checked mods
and the edits that are not saved yet. Each line names the field on both sides, the conversion and the confidence,
and one of:

- `agrees`, `changed, agrees`: the two sides correspond.
- `DIFFERS`: they do not. Shipped data has such items too (the report says how many fields differed before the
  mods), so a difference is not always a mistake of the mod.
- `client omits it` / `server omits it`: one side has no such attribute for this item. Counted apart from the
  differences.
- `candidate`: a pair the map does not trust; shown for information.

### Server-only tables

Shops, drops, item options and NPC behaviour exist only on the server. They are tables like any other: in the Tables
view pick `server:Xml.m2d` or `server:Server.m2d` in **Archive**, open the entry, and edit, Plan, Apply and Revert as
for a client table. Rows that sit inside another row (the items of one shop) are reached with **Rows under**. For one shop or one drop box,
the [Shops and Drops view](#shops-and-drops) is easier.

To find the rows that belong to one item, press **Server rows** in the Items view, or run `modkit refs <id>... [mod...]`. It
lists the option rows the item's server file points at, and the drop and shop rows that hold its id, each with
archive, entry and row selector, ready to be opened in the Tables view. The rows are shown as the mods leave them
(in the view: the enabled mods and the mod being edited; on the command line: the mods named after the ids). The list is for looking up; edit the rows in
the Tables view.

### Server target folder and the command after apply

Apply writes the changed server archives to the workspace. Two settings carry them on from there. Both are empty by
default, and then nothing more happens.

| Setting (page "MS2 Mods") | Environment | Command line | Meaning |
|-|-|-|-|
| Server target folder | `MS2_SERVER_TARGET` | `--server-target DIR` | after a successful apply, every server archive of the workspace (with its `.m2h`) is copied into this folder. This is the data folder your (private) server runs on. The folder modkit reads the originals from (`--server`, `MS2_SERVER_DATA`) must be another one: an untouched **copy** of that data, which modkit only reads and from which a revert puts the files back. One folder for both is refused (as is a folder inside the workspace) with exit code 3, and nothing in it is changed |
| Command after apply | `MS2_AFTER_APPLY` | `--after-apply COMMAND` | a command line that is started when the copy is done, for example your server's ingest step |

- **Plan shows both before anything runs**: `after a successful apply server:Xml.m2d would be copied to ...` and
  `... this command would be run in ...: ...`, with the command exactly as it will be started.
- The command is started directly, without a shell: the first word (or the first part in double quotes) is the
  program, the rest are its arguments. For shell features name the shell yourself (`cmd /c ...`, `sh -c "..."`).
- `{target}` and `{workspace}` in the command are replaced by the two folders. The command runs in the target
  folder (in the workspace when no target is set) and gets `MODKIT_WORKSPACE`, `MODKIT_SERVER_TARGET` and
  `MODKIT_SERVER_ARCHIVES` (the archives that were written, separated by the system's path separator, `;` on
  Windows; empty when a revert left none) in its environment.
- Its output goes line by line into the view's output box (the command line tool prints it), followed by
  `exit code N`. An exit code other than 0, or a failed copy, is reported as a failure of the step; the applied
  files stay (command line exit code 3).
- It runs only when a server archive changed. A mod that touches the client alone starts nothing.
- **Revert** puts the original archives back into the target folder (modkit records what it copied there in
  `modkit-target.xml`) and runs the command again, so the server can take the original data back.
- The command is yours: modkit runs what the setting says, with your rights. If the command writes a database, point
  it at a database you can lose.

### Testing the server half on a private server

To see what the server does with a mod, use a server of your own that nothing else depends on. That means a copy of the server, with its own data folder (a copy of the shipped server archives), its own database instance and its own ports. Point the copy's settings at that database and check them before every ingest. Set the server target folder to the copy's data folder and the command after apply to the copy's ingest step. A Maple2 server reads its game data from the database when it starts, so stop it before apply, run the ingest, and start it again. A server that keeps running still uses the data it loaded at start. The ingest re-reads all the tables but rewrites only those whose content changed, so one changed item file still takes a full ingest run. Check which server the client talks to before you log in, because a client can carry a built-in default address that points at another server. Then test one thing on both sides: the client's display (a tooltip, for example) and what the server does (the meso a sale credits, for example). Record each of three states: as shipped, with the mod, and after revert. Revert puts the original archives back into the target folder and runs the ingest again, and the copy should then match the shipped files byte for byte.

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
- Maps: added blocks change what players stand on, not where monsters walk, until the map's navigation mesh is
  rebuilt (needs PathEngine, not included).
- A mod is checked against the original data and the other mods given in the same command, not against mods you
  apply some other day.
- Nothing here knows what a table means: which tables must agree between client and server, or which other tables
  refer to an id, is for the mod author (and for editors built on this library) to know.
