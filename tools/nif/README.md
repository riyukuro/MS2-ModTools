# tools/nif: MapleStory 2 models ↔ glTF / Blender

These tools read and write Gamebryo 30.2.0.3 NIF/KF/KFM files, the format of the MS2 client's `Resource/Model/*.m2d`.
No game files, models or textures are in this repository: every input comes from your own client.

## nif.xml

`nifxml.py` reads the format from niftools' `nif.xml` (BSD, https://github.com/niftools/nifxml) at run time.
The file is not vendored here. Download it and either set `NIFXML=/path/to/nif.xml` or put it at `tools/nif/nif.xml`
(or set `MS2_NIFXML` in `modtools.toml`, see `modtools.example.toml`). The tools need `numpy` (`requirements.txt`).
Nothing in these tools comes from the Gamebryo SDK.

Blender is only needed for `blender_render.py` and `blender_tint.py`. Nothing here starts it: you run your own Blender
with `-b --factory-startup -P <script> -- <args>`, as in the example below.

## Tools

| tool | what it does |
|---|---|
| `nif2gltf.py` | NIF (+ KF sequences) → .glb: skeleton, skinned meshes, textures, animations. Writes glTF Y-up (MS2 is Z-up) and keeps the units 1:1. |
| `gltf2nif.py` | .glb → NIF, spliced into a shipped template nif (its skeleton, materials and block layout). `--compare` checks the result semantically against a reference nif. |
| `mkmodel.py` | Builds an NPC model folder: kfm, nif, one kf per sequence (shipped kfs renamed), the per-model xml, the anikeytext row and the dds files. |
| `blender_render.py` | Headless Blender check render of a .glb (bind pose or an animation frame). |
| `blender_tint.py` | Headless Blender round trip: import → tint textures / scale → export .glb (the Blender → MS2 test). |
| `ms2nif.py`, `nifxml.py` | The libraries: the MS2 scene-graph/NiMesh conventions and the nif.xml reader/writer. |

## Model → boss

Blender (or any glTF source) → `gltf2nif.py` → `mkmodel.py` → `tools/dungeon` spec with
`"bosses": [{..., "model": {"kfm": "<name>"}, "model_dir": "<mkmodel output>"}]`.
`boss.py` packs the folder into the scratch build:

- `Model/Npc.m2d <id[:2]>/<id>/`;
- the textures into `Model/Textures.m2d npc_custom/`;
- the anikeytext row into both `Xml.m2d`;
- the asset-web urns.

Example:

```
blender -b --factory-startup -P tools/nif/blender_tint.py -- f_body.glb blue.glb --tint 0.4,0.7,1.6
tools/nif/gltf2nif.py blue.glb f_body.nif blue.nif --tex-out tex --tex-prefix myblue
tools/nif/mkmodel.py --name 23009901_MyBoss --nif blue.nif --ref-dir <a shipped NPC model folder> \
    --kf-dir <female kf folder> --map Attack_01_A=largesword_attack_01_a.kf ... \
    --anikeytext <client anikeytext.xml> --tex tex/*.dds -o model/23009901_MyBoss
```

## Input contract for a new rigged mesh (glTF 2.0 .glb)

- **Skeleton.** Use the joint names of the template nif, e.g. the female body's `Bip01 ...` bones. Export the
  skeleton from `nif2gltf.py f_body.nif` and skin to it. The bind pose must be that skeleton's rest pose.
- **Axes and scale.** Y-up glTF, as `nif2gltf.py` writes it. The units are MS2 units: the female body is about 100
  tall. To resize, use the spec's model scale or `gltf2nif.py --scale`.
- **Skinning.** At most 4 influences per vertex. Each primitive needs at most 65535 vertices (UINT16 indices).
  Every primitive needs POSITION, NORMAL, TEXCOORD_0, JOINTS_0 and WEIGHTS_0.
- **Materials.** One base-color image per primitive. It is written as an uncompressed A8R8G8B8 DDS (keep it a power
  of two, 512 or smaller is typical), plus a flat normal map. `--dds dxt1|dxt5` writes block-compressed DDS with a
  full mip chain instead (dxt5 keeps alpha). `--normal-tex` puts the material's glTF `normalTexture` into the
  template's Normal slot instead of the flat map (`--normal-flip-g` for OpenGL-convention maps), and
  `--gloss-from-roughness S` fills the Gloss slot with `(1 - roughness) * S` from the metallic-roughness texture
  instead of the base color.
- **Budget.** About 4-8k triangles for an NPC.
- **Extra bones.** Joints the template doesn't have are not added as nodes. Their weights fold into the nearest
  ancestor joint the template has, so a tail skinned to new bones moves rigidly with, for example, the pelvis.
  Gamebryo itself is fine with nodes no kf animates (they keep their rest transform), so this is a writer limit,
  not a format limit. The fix is to clone a NiNode per new joint, append it to its parent's children and to the
  skin's bone list. Until then, skin such parts to existing bones.
- The female `Tail_Point01..08` nodes are not a tail chain. All eight are children of `Bip01 Head`, in front of and
  above it (hair points).
