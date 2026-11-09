"""blender_tint.py: headless Blender edit of a nif2gltf .glb, exported back to .glb for gltf2nif.py (a test of the
Blender -> MS2 direction: Blender's own glTF importer and exporter on the MS2 rig).

  blender -b --factory-startup -P tools/nif/blender_tint.py -- in.glb out.glb [--tint R,G,B] [--only MAT,MAT]
        [--scale S]

--tint multiplies the base color images of the materials (all, or --only these material names) by R,G,B.
--scale scales the mesh vertices about the origin (rest pose; the skin keeps its bones).
"""
import sys

import bpy

argv = sys.argv[sys.argv.index('--') + 1:]
src, out = argv[0], argv[1]
opt = {'--tint': None, '--only': None, '--scale': None}
for i in range(2, len(argv), 2):
    opt[argv[i]] = argv[i + 1]

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
only = set(opt['--only'].split(',')) if opt['--only'] else None
done = set()
if opt['--tint']:
    t = [float(x) for x in opt['--tint'].split(',')]
    for m in bpy.data.materials:
        if only and m.name not in only or not m.node_tree:
            continue
        for n in m.node_tree.nodes:
            if n.type == 'TEX_IMAGE' and n.image and n.image.name not in done:
                img = n.image
                px = list(img.pixels)
                for k in range(0, len(px), 4):
                    px[k] = min(1.0, px[k] * t[0])
                    px[k + 1] = min(1.0, px[k + 1] * t[1])
                    px[k + 2] = min(1.0, px[k + 2] * t[2])
                img.pixels = px
                img.pack()
                done.add(img.name)
                print('tinted', m.name, img.name, img.size[:])
if opt['--scale']:
    s = float(opt['--scale'])
    for o in bpy.data.objects:
        if o.type == 'MESH':
            for v in o.data.vertices:
                v.co *= s
bpy.ops.export_scene.gltf(filepath=out, export_format='GLB', export_animations=False, export_extras=True)
print('wrote', out)
