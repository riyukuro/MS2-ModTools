# tokgeom: map geometry for the .tok builder

`tokgeom` reads one map (an xblock plus the NIF models it places) and writes the ground input that the client's
`BuildCP3DSnapshotFromPhysX` hands to PathEngine's `buildGround`, as `<out>.geom` and `<out>.solids` (spec in
[../FORMAT.md](../FORMAT.md)). `tokbuild` (../builder) turns those files into a `.tok`. tokgeom has no PathEngine code
and needs no PathEngine files; it reads the client archives with `Maple2.File.Parser`.

```sh
dotnet build tools/tok/extract -c Release -o <bin>
tokgeom <client>/Data/Resource 02000023_tw_ellinia out/ellinia        # an xblock in Exported.m2d, by name
tokgeom <client>/Data/Resource <project>/x.xblock out/x --project <project>   # a loose Toolbench export
tokgeom <client>/Data/Resource --list ellinia                         # xblock names containing "ellinia"
```

- `<xblock>`: a `.xblock` file (as Toolbench exports it), or the name of one in `Exported.m2d`.
- `--project <dir>`: a Toolbench project folder. Its loose `.nif` files take precedence over the client archives. A
  file matches if its path, from `model/` or `library/` on, is the model's path (e.g. `<dir>/Model/Map/x/cube/a.nif`
  for `/model/map/x/cube/a.nif`; case does not matter). Other models still come from `Library.m2d` and `Model/*.m2d`.
- `--survey`: entity and shape statistics. `--dump`: also writes `<out>.ents.tsv`, one row per entity.
- The other options switch rules off or back to earlier spike behaviour, for experiments; see the header of `Program.cs`.
- Exit code: 0 ok; 1 xblock not found or project folder missing; 2 usage.

## Rules

These are what the shipped toks agree with (early experiments, then IoU tests):

- Entities: those with `IMS2PathEngineTOK.doesMakeTOK`, plus every `PhysXWhitebox` (it has no `doesMakeTOK` to filter on).
- `PhysXWhitebox`: a box of its dimension, centred on its position, written as a convex solid.
- MapProperties cubes (`GeneratePhysX`, default size 150) and NIF box shapes: convex solids, with z shrunk to 74/75
  about the box centre.
- NIF PhysX triangle meshes: meshes, with local z shrunk to 74/75. An entity with PhysX group 8
  (`PropCollisionGroup`) gives excluded faces. Each triangle is written clockwise (PhysX order v0, v2, v1): an
  unflipped closed mesh comes out hollow. Meshes are written in entity order.
- NIF convex meshes: convex solids (hull points).
- Entity rotation (degrees, x/y/z) applies z first, then y, then x (row vectors: Rz·Ry·Rx), with entries within 1e-6
  of 0 or ±1 snapped. Props rotated about z only can't tell the orders apart; 02020130_bf's pillar props
  (<90,45,0>, <270,315,180>) match only this order (3D IoU 0.911 → 0.985).
- x/y are written as integers: negative values move one unit toward zero, then are truncated. Mesh z is exact; solid z
  is truncated.

## Agreement with the shipped toks

The test: build each map with `tokgeom` + `tokbuild build`, then compare walkable cells (25-unit xy cells; in 3D also
50-unit height bins, matched within one bin) against the shipped `.tok` (both dumped with `tokbuild --faces`). That
gives a 2D (xy) and a 3D IoU.
2026-11-08, KMS2 client data, 30 maps of every kind (towns, fields, dungeons, PvP, housing, quest, old 0202 fields):

- 25 maps built. 3D IoU: min 0.468, median 0.997, max 1.000; 17 maps at or above 0.99. 2D: min 0.851, median 1.000.
- Exact (1.000): 02000305_bf, 52000125_qd, 65010001_bd, 51000001_dg, 84000000_wd. ellinia 1.000 / 0.998.
- 82000000_survival (154k faces, 32k solids) builds with the /LARGEADDRESSAWARE tokbuild: 1.000 / 0.990.
- Worst: 02020300_bf (0.85 / 0.47) and 02200001_dg (0.86 / 0.72). The built ground extends past the shipped footprint
  (02020300_bf: y ±4200 against ±2500), so the xblock probably changed after these toks were baked. Not verified.
- 02900049_ugc (1.00 / 0.91): built ground between stacked `pe_ground_rock_H0x` cubes. The housing grid places them
  every second cell (z 900, 1200, … 2400), leaving 150-unit gaps that build as ground. The shipped tok has none there,
  but it does keep the same 150 gaps in the wood/bridge ladders next to them. Neither the xblock entity, the item
  table (`physXdimension="150,150,150"`) nor the NIF (a 150 cube, z 0..150) makes H cubes taller. Making them 300 tall,
  centred, gives 0.987, but nothing in the data supports it, so it is not a rule (the user map may have changed after
  the tok was baked). No other test map uses H rocks.
- Not built: 00009002, 02031001, 02033005, 60010001, 60020001 (tok only, no xblock in this client).
