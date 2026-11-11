# MS2 Mods: design notes

Notes on how parts of the mod kit are built and why. One section per part.

## Model preview

The Items and NPCs views show a still picture of the model beside the form, the way they show the icon.
The picture is drawn by our own code from the game's files. No Gamebryo renderer and no graphics device is
used, so the same code runs in the net35 plugin and in the net8 tests.

### What the files are

- Models are Gamebryo `.nif` files. The `.nif` files in the client's `Resource/Model/Item.m2d` and `Npc.m2d`
  start with the line `Gamebryo File Format, Version 30.2.0.3`, but for a few item files that say `30.1.0.3`
  and have the same layout. The `[Explicit]` archive test counts the version lines of both archives, fails on
  any other version, and builds every file.
- This version keeps geometry in `NiMesh` blocks. A mesh names `NiDataStream` blocks, and each stream holds
  elements with a name (`POSITION`, `NORMAL`, `TEXCOORD`, `INDEX`, ...) and a format. Skinned meshes carry
  `POSITION_BP` and `NORMAL_BP` (bind pose), `BLENDINDICES`, `BLENDWEIGHT`, `BONE_PALETTE`, and a
  `NiSkinningMeshModifier` that lists the bone nodes and one skin-to-bone transform for each.
- The header lists the type and the byte size of every block. The reader uses the size to step over every
  block type it does not know, so particle systems, controllers and extra data never stop a read.
- The base texture is named by `NiTexturingProperty` -> `NiSourceTexture` as a bare file name
  (`11300001_C_AdventurerCap_D.dds`). The file is in `Resource/Model/Textures.m2d` under a folder
  (`item_cap/...`), so textures are found by file name without the folder and without case. A few NPC
  textures are in `Npc.m2d` beside the model; the model's own archive is the second place looked in.
- An NPC row names its model as `model@kfm` (`02010001_Stan`). The `.kfm` file is in `Npc.m2d`
  (`02/01/02010001/02010001_stan.kfm`); it is a small binary file whose first string is the `.nif` path.
- An item's models are slot assets (`Data/Resource/Model/Item/1/13/....nif`). The client keeps them in
  `itemmodel/<id / 100000>.xml` of `Xml.m2d` (`ItemModel/slots/slot/asset@name`), the server in the item's
  own file (`environment/slots/slot/asset@name`). `Data/Resource/Model/<Name>/<entry>` is entry `<entry>` of
  `Resource/Model/<Name>.m2d`.

### Parts

Library (`MS2.ModKit/Models`, no drawing library, so it also builds for net8 on any system):

- `NifFile`: header, block table, and the blocks a still picture needs: `NiNode` and its kin (transform,
  children, hidden flag), `NiMesh`, `NiDataStream`, `NiSkinningMeshModifier`, `NiTexturingProperty`,
  `NiSourceTexture`, `NiMaterialProperty`, `NiAlphaProperty`. A block that does not read inside its size is
  left out and noted. A file that is not a `.nif`, is older than 20.5 or is big-endian gives a
  `NifException` with a short reason.
- `ModelBuilder`: walks the scene from its roots, multiplies the transforms down, and gives a `Model`:
  parts with world-space positions, normals, texture coordinates, triangles, the texture name and the alpha
  mode. Hidden nodes (the bone boxes of NPCs) are left out; a switch or level-of-detail node gives one child.
  Skinned meshes are put in the bind pose: vertex = sum of weight x (bone world x skin-to-bone) x bind-pose
  vertex, with the bones where the file's nodes stand. No animation is read.
- `Dds`: the top level of DXT1, DXT3, DXT5 and of plain 32, 24, 16 and 8 bit pixels (also the DX10 header
  forms of these). Another format gives no texture and a reason.
- `ModelRenderer`: a depth-buffer triangle filler. The camera looks at the middle of the bounds from the
  front (the models stand on z and face -y), a little from the side and from above, and the picture is
  scaled so the model fills the frame. One light from the camera's upper left plus a high ambient part, both
  sides lit; textures are sampled with two-way blending from a mip level picked per triangle; alpha test and
  alpha blending follow the model's `NiAlphaProperty`. It draws at twice the size and averages down. The
  result is `int[]` 0xAARRGGBB on a clear background.
- `ModelSource`: opens the model archives of a data set, finds the files an item or an NPC draft names, and
  gives the built `Model` and a texture lookup. All failures come back as a short text.

Plugin: `ModelBox`, a control like `IconBox`. A `ModelCache` per data set renders on a worker thread and
keeps the last pictures; the box shows the picture when it is ready, and a newer request replaces an older
one that has not started. The Items view shows the box under the icon and asks again when the selected item
or one of its slot asset fields changes; the NPCs view does the same for `model@kfm`.

### When something cannot be shown

Nothing in this path may throw into the view.

- An unknown block: stepped over. A mesh that cannot be read: left out; the rest is drawn.
- A texture that is missing or in a format the decoder does not read: the mesh is drawn plain grey.
- No model named, a file that is not in the archives, an unreadable file, or no triangles: the box shows one
  short line (`no model`, `cannot show: <reason>`).

### Limits

- A still picture in bind pose: no animation, no morphs, no particles, no effects, no second texture layers
  (normal, specular, the game's colour masks), so dyed parts show the plain base texture.
- Item models are drawn as they are stored. Worn items are stored in the space of the bone they hang on, so a
  cap can lie on its side.
- Only the `NiMesh` generation (the client's 30.1.0.3 and 30.2.0.3) is read; `NiTriShape` files of older
  Gamebryo versions give `cannot show`.

### Tests

The reader, the builder, the decoder and the renderer are tested on small `.nif` and `.dds` bytes built in
the tests. One `[Explicit]` test reads the real archives named by `MS2_CLIENT_DATA`; no path is in the code,
and no game file is in the repository.
