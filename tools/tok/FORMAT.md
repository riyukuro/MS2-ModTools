# Geometry dump format (.geom / .solids)

The contract between the map geometry extractor (`tools/tok/extract`) and the .tok builder (`tools/tok/builder`,
`tokbuild build`). A dump is exactly the input the client hands ContentProcessing3D's `buildGround` in
`BuildAndSaveGround` (glue/pathengine/buildfromphysx.cpp, kept for reference): face/vertex meshes plus convex solids, already
in PathEngine coordinates and with every MS2-specific rule applied. The builder does no geometry work of its own; it only
converts numbers as described below.

The extractor writes this format and the builder reads it; change both together.

## Common rules

- Plain ASCII text, any line endings. Tokens are separated by spaces, tabs or newlines.
- `#` starts a comment that runs to the end of the line.
- Coordinates are PathEngine world units = MS2 world units, z up (the coordinates of the shipped .tok files; the client
  gets them from PhysX with scale 100 through cCoordinateMapping).
- Numbers may be written as integers or decimals (`-1424`, `-1424.0`, `450.6666666666667`). They are read into a 32-bit
  `float` (`strtof`) first.
- Integer conversion is C truncation toward zero (`(tSigned32)value`). The builder does not round. If a rule needs
  another rounding (negative x/y move one unit toward zero, -1425 -> -1424, matching all 1042 negative coordinates
  in the shipped toks), the extractor applies it and should then write integers.

## .geom: one face/vertex mesh

```
v <x> <y> <z>
f <a> <b> <c> [<exclude>]
section <n>
```

- `v`: a vertex. x and y become integers (truncation), z stays a float.
- `f`: a triangle by 0-based indices into this file's `v` records. Records may come in any order, but every index
  must be a vertex of the same file.
- `exclude` (optional, default 0): the face's `PE_FaceAttribute_CP3D_ExcludeFromGroundResult`. 1 = the face blocks and
  supports like any other but is not itself kept as ground (the client sets it on PhysX group 8 triangle meshes through
  cPhysXTriangleMeshWrapper::SetFlag). Must be on the same line as the `f`.
- `section` (optional, at most once, anywhere in the file): the `PE_FaceAttribute_SectionID` of every face in the file.
  Without it SectionID reads as -1 (unset), as for the client's triangle meshes. The client's heightfield wrapper
  (cPhysXTerrainWrapper::faceAttribute) puts every terrain face in section 0, so a heightfield dump writes `section 0`.
  It changes the .tok (the attribute is stored), not the walkable area.
- Every other face attribute reads as -1 (unset). Face winding is passed through unchanged; with the client's option
  `excludeDownwardFacingFromGroundResult`, faces whose normal points down are not kept as ground.
- One file is one `iFaceVertexMesh`. `tokbuild build` takes `--geom` more than once and passes the meshes in the given
  order (the client passes one per PhysX triangle mesh / heightfield). Splitting or merging files should not change the
  ground area, but the triangulation (and so the bytes) can change.
- An empty file is allowed (no meshes from it).

## .solids: convex solids

```
s <n> <x1> <y1> <z1> ... <xn> <yn> <zn>
```

- One record per convex solid; it may wrap over several lines. `n` >= 4.
- The solid is the convex hull of the n points. All three coordinates become integers (truncation).
- Solid attributes all read as -1.
- The client writes the 8 corners of each PhysX box and the vertices of each convex mesh (AddPhysXConvexShape,
  in MS2Recomp src/ms2/client/ms2/pathengine/addphysxconvexshape.cpp), after shrinking boxes vertically by 74/75 about their centre.

## Rules the extractor applies

These belong to the extractor, not to the builder (details and IoU results in [extract/README.md](extract/README.md)):

- Entities: those with `doesMakeTOK`, plus every `PhysXWhitebox` (it has no `doesMakeTOK`).
- `PhysXWhitebox`: a box of its dimension centred on its position -> convex solid.
- MapProperties cubes (`GeneratePhysX`, default 150) and NIF boxes -> convex solids, z shrunk by 74/75 about the
  centre (the client's CopyScene).
- NIF triangle meshes: local z shrunk by 74/75; an entity with PropCollisionGroup 8 -> `exclude` = 1 (the client's PhysX
  group 8 -> SetFlag); each triangle written clockwise (PhysX v0,v2,v1; unflipped closed meshes come out hollow); in
  entity order.
- NIF convex meshes -> convex solids.
- x/y: negative values +1, then truncated (see above); mesh z exact, solid z truncated.

## File names and examples

`<mapid>_<name>.geom` and `<mapid>_<name>.solids`, named like the .tok they build (`02000305_bf.tok`). Examples from
an early experiment: `bf.excl2.geom` + `bf.noMP.solids` build the shipped 02000305_bf ground
(516 faces, 616 connections, 37842 bytes, the shipped size).
