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
- Mods are applied in the order given. Two mods that set the same attribute of the same row to different values,
  a mod that deletes a row another one changes, two that add the same id or entry: these are **conflicts**. With a
  conflict or an error nothing is written (exit code 1). `--allow-conflicts` applies anyway, and the later mod wins.
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
| Korean client data folder | `MS2_CLIENT_DATA` | the `Data` folder of a Korean client (`kms-client`, holds `Xml.m2d`). Only read. Empty when you have none |
| Global client data folder | `MS2_GMS_CLIENT_DATA` | the `Data` folder of a global client (`gms-client`). Only read. Empty when you have none |
| Server data folder | `MS2_SERVER_DATA` | the data folder of your local server (holds its `Xml.m2d`). Only read |
| Mods folder | `MS2_MODS` | a folder of mod folders. Save mod writes `<mods>/<mod id>/mod.xml`; the view lists its mods (a folder whose name starts with `.` or `_` is left out) |
| Workspace folder | `MS2_MOD_WORKSPACE` | where Apply writes patched copies of archives: the server's always, the client's when you apply to the workspace |
| Game folder | `MS2_GAME_DIR` | the folder of a **copy** of your client that holds `Data`. Apply writes the client's changed tables there as plain files |

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
   folder and is left out. Mods are applied from the top down, which matters when you allow conflicts (the later mod
   wins); **Up** and **Down** move the selected mod. **Load mod** (or a double-click) loads the selected mod into the
   grid. The mod being edited is marked and always takes part, checked or not. The checks and the order are kept in
   `modkit-mods.xml` in the mods folder; the mod folders themselves are not changed by them. When the view opens and
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

Not in this editor yet: schema checks (the command line `--schema`), `<ids>` ranges, `<entry>` operations and edits
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
for a client table. Rows that sit inside another row (the items of one shop) are reached with **Rows under**.

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
| Server target folder | `MS2_SERVER_TARGET` | `--server-target DIR` | after a successful apply, every server archive of the workspace (with its `.m2h`) is copied into this folder. Use the data folder of a **copy** of your server, never the folder modkit reads the originals from (it refuses that, and a folder inside the workspace) |
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
- A mod is checked against the original data and the other mods given in the same command, not against mods you
  apply some other day.
- Nothing here knows what a table means: which tables must agree between client and server, or which other tables
  refer to an id, is for the mod author (and for editors built on this library) to know.
