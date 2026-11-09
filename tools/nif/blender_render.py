"""blender_render.py: headless Blender check render of a .glb (bind pose or an animation frame).

  blender -b --factory-startup -P tools/nif/blender_render.py -- in.glb out.png [--anim NAME] [--frame F]
        [--view front|side|back|threequarter] [--res 768] [--engine CYCLES|BLENDER_WORKBENCH|BLENDER_EEVEE]
        [--bones]

Imports the glTF with Blender's own importer, frames the camera on the evaluated (posed) meshes and renders.
"""
import math
import sys

import bpy
from mathutils import Vector

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
src, out = argv[0], argv[1]
opt = {'--anim': None, '--frame': '0', '--view': 'threequarter', '--res': '768', '--engine': 'CYCLES'}
flags = set()
i = 2
while i < len(argv):
    if argv[i] in opt:
        opt[argv[i]] = argv[i + 1]
        i += 2
    else:
        flags.add(argv[i])
        i += 1

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
scene = bpy.context.scene

arm = next((o for o in scene.objects if o.type == 'ARMATURE'), None)
if arm is not None:
    if opt['--anim']:
        act = bpy.data.actions.get(opt['--anim'])
        if act is None:
            cands = [a for a in bpy.data.actions if opt['--anim'] in a.name]
            act = cands[0] if cands else None
        if act is None:
            print('actions:', [a.name for a in bpy.data.actions])
            raise SystemExit('no action ' + opt['--anim'])
        arm.animation_data_create()
        arm.animation_data.action = act
        if hasattr(arm.animation_data, 'action_slot') and act.slots:
            arm.animation_data.action_slot = act.slots[0]
    else:
        if arm.animation_data:
            arm.animation_data.action = None
        for pb in arm.pose.bones:
            pb.matrix_basis.identity()
    arm.show_in_front = '--bones' in flags
    arm.hide_render = '--bones' not in flags
scene.frame_set(int(opt['--frame']))
bpy.context.view_layer.update()

dg = bpy.context.evaluated_depsgraph_get()
lo = Vector((1e9, 1e9, 1e9))
hi = -lo
for o in scene.objects:
    if o.type != 'MESH':
        continue
    oe = o.evaluated_get(dg)
    me = oe.to_mesh()
    for v in me.vertices:
        p = oe.matrix_world @ v.co
        lo = Vector(map(min, lo, p))
        hi = Vector(map(max, hi, p))
    oe.to_mesh_clear()
ctr = (lo + hi) / 2
size = max((hi - lo).length, 1e-3)
print('bbox', tuple(lo), tuple(hi))

az = {'front': 0, 'threequarter': 35, 'side': 90, 'back': 180}[opt['--view']]
# glTF -Z forward -> Blender: model faces -Y in Blender after import of a Y-up glTF built from MS2 Z-up
cam_d = bpy.data.cameras.new('cam')
cam_d.lens = 50
cam_d.clip_start = size * 0.01
cam_d.clip_end = size * 20
cam = bpy.data.objects.new('cam', cam_d)
scene.collection.objects.link(cam)
dist = size * 1.6
a = math.radians(az)
cam.location = ctr + Vector((math.sin(a) * dist, -math.cos(a) * dist, size * 0.15))
cam.rotation_euler = (ctr - cam.location).to_track_quat('-Z', 'Y').to_euler()
scene.camera = cam
for k, (rx, rz, e) in enumerate(((50, 30, 3.0), (60, -120, 1.5))):
    ld = bpy.data.lights.new('sun%d' % k, 'SUN')
    ld.energy = e
    lo_ = bpy.data.objects.new('sun%d' % k, ld)
    lo_.rotation_euler = (math.radians(rx), 0, math.radians(rz))
    scene.collection.objects.link(lo_)
world = bpy.data.worlds.new('w')
world.use_nodes = True
bg = world.node_tree.nodes.get('Background')
if bg:
    bg.inputs[0].default_value = (0.35, 0.38, 0.45, 1)
    bg.inputs[1].default_value = 0.6
scene.world = world
scene.render.engine = opt['--engine']
if opt['--engine'] == 'CYCLES':
    scene.cycles.device = 'CPU'
    scene.cycles.samples = 24
    try:
        scene.cycles.use_denoising = False
    except Exception:
        pass
elif opt['--engine'] == 'BLENDER_WORKBENCH':
    scene.display.shading.color_type = 'TEXTURE'
r = int(opt['--res'])
scene.render.resolution_x = r
scene.render.resolution_y = r
scene.render.filepath = out
scene.render.image_settings.file_format = 'PNG'
bpy.ops.render.render(write_still=True)
print('wrote', out)
