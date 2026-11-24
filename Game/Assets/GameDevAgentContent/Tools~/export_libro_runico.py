# Phase 20.1: game-ready copy of the Sacred Book ("Libro y base animacion.blend", Meshy AI book + pedestal).
# The source meshes ("Retopo_Base", "Retopo_Libro", ~160k faces) have NO UVs, so their packed Meshy textures cannot be
# mapped. This script (Blender background, the .blend is never saved):
#   1. evaluates both meshes at frame 1 (the book's lowest float position) in world space,
#   2. decimates a copy of each (target faces below), smart-UV-unwraps it,
#   3. bakes a tangent-space normal map and an ambient-occlusion map from the high-poly mesh onto it (Cycles),
#   4. exports Pedestal + Libro to one FBX (pedestal bottom centred at the origin) and the maps as PNG,
#   5. prints the float animation of the book (amplitude / period) measured from the armature action.
# Run: blender -b "<source.blend>" --python export_libro_runico.py -- <out_dir>
import bpy, bmesh, sys, os, mathutils

out = sys.argv[sys.argv.index("--") + 1]
os.makedirs(out, exist_ok=True)
TARGET = {"Retopo_Base": 7000, "Retopo_Libro": 7000}
NAMES = {"Retopo_Base": "Pedestal", "Retopo_Libro": "Libro"}
RES = 1024

scene = bpy.context.scene
scene.frame_set(1)
deps = bpy.context.evaluated_depsgraph_get()

# ---- float animation of the book, measured in world space
book = bpy.data.objects["Retopo_Libro"]
def book_center(frame):
    scene.frame_set(frame)
    d = bpy.context.evaluated_depsgraph_get()
    ev = book.evaluated_get(d)
    me = ev.to_mesh()
    c = sum((ev.matrix_world @ v.co for v in me.vertices), mathutils.Vector()) / len(me.vertices)
    ev.to_mesh_clear()
    return c
centers = {f: book_center(f) for f in range(1, 82)}
zs = [centers[f].z for f in range(1, 82)]
lo, hi = min(zs), max(zs)
peaks = [f for f in range(2, 81) if zs[f - 1] > zs[f - 2] and zs[f - 1] >= zs[f]]
print("FLOAT amplitude_z=%.4f min=%.4f max=%.4f peaks=%s fps=%d dx=%.4f dy=%.4f" % (
    hi - lo, lo, hi, peaks, scene.render.fps, centers[14].x - centers[1].x, centers[14].y - centers[1].y))
scene.frame_set(1)
deps = bpy.context.evaluated_depsgraph_get()

# ---- high-poly copies (world space, frame 1) and decimated low-poly copies
highs, lows = {}, {}
for src_name in TARGET:
    src = bpy.data.objects[src_name]
    ev = src.evaluated_get(deps)
    me = bpy.data.meshes.new_from_object(ev)
    me.transform(src.matrix_world)
    hi_obj = bpy.data.objects.new(NAMES[src_name] + "_High", me)
    scene.collection.objects.link(hi_obj)
    highs[src_name] = hi_obj
    lo_me = me.copy()
    lo_obj = bpy.data.objects.new(NAMES[src_name], lo_me)
    scene.collection.objects.link(lo_obj)
    bpy.context.view_layer.objects.active = lo_obj
    for o in bpy.context.selected_objects: o.select_set(False)
    lo_obj.select_set(True)
    dec = lo_obj.modifiers.new("dec", "DECIMATE")
    dec.ratio = min(1.0, TARGET[src_name] / max(1, len(me.polygons)))
    bpy.ops.object.modifier_apply(modifier="dec")
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.smart_project(angle_limit=1.15, island_margin=0.006)
    bpy.ops.object.mode_set(mode="OBJECT")
    for p in lo_obj.data.polygons: p.use_smooth = True
    lows[src_name] = lo_obj
    print("LOW", lo_obj.name, len(lo_obj.data.polygons), "faces")

# Hide the source objects (armature included) from the bake.
for o in bpy.data.objects:
    if o not in highs.values() and o not in lows.values():
        o.hide_render = True

# ---- bake normal + AO
scene.render.engine = "CYCLES"
scene.cycles.device = "CPU"
scene.cycles.samples = 32
scene.render.bake.use_selected_to_active = True
scene.render.bake.cage_extrusion = 0.06
scene.render.bake.max_ray_distance = 0.3
scene.render.bake.margin = 6
for src_name, lo_obj in lows.items():
    name = NAMES[src_name]
    imgs = {}
    for kind in ("Normal", "AO"):
        img = bpy.data.images.new(name + "_" + kind, RES, RES, alpha=False, float_buffer=False)
        if kind == "Normal": img.colorspace_settings.name = "Non-Color"
        imgs[kind] = img
    mat = bpy.data.materials.new(name + "_Mat")
    mat.use_nodes = True
    lo_obj.data.materials.clear()
    lo_obj.data.materials.append(mat)
    node = mat.node_tree.nodes.new("ShaderNodeTexImage")
    mat.node_tree.nodes.active = node
    for kind in ("Normal", "AO"):
        node.image = imgs[kind]
        for o in bpy.context.selected_objects: o.select_set(False)
        highs[src_name].select_set(True)
        lo_obj.select_set(True)
        bpy.context.view_layer.objects.active = lo_obj
        if kind == "Normal":
            bpy.ops.object.bake(type="NORMAL", normal_space="TANGENT")
        else:
            bpy.ops.object.bake(type="AO")
        imgs[kind].filepath_raw = os.path.join(out, name + "_" + kind + ".png")
        imgs[kind].file_format = "PNG"
        imgs[kind].save()
        print("BAKED", imgs[kind].filepath_raw)

# ---- place: pedestal bottom centred at the origin, export both
base = lows["Retopo_Base"]
mn = mathutils.Vector((min(v.co.x for v in base.data.vertices), min(v.co.y for v in base.data.vertices), min(v.co.z for v in base.data.vertices)))
mx = mathutils.Vector((max(v.co.x for v in base.data.vertices), max(v.co.y for v in base.data.vertices), max(v.co.z for v in base.data.vertices)))
offset = mathutils.Vector(((mn.x + mx.x) / 2, (mn.y + mx.y) / 2, mn.z))
for lo_obj in lows.values():
    lo_obj.data.transform(mathutils.Matrix.Translation(-offset))
    lo_obj.data.update()
bk = lows["Retopo_Libro"]
bmn = min(v.co.z for v in bk.data.vertices); bmx = max(v.co.z for v in bk.data.vertices)
print("SIZE pedestal %.3f x %.3f x %.3f; book z %.3f..%.3f" % (mx.x - mn.x, mx.y - mn.y, mx.z - mn.z, bmn, bmx))
for o in bpy.context.selected_objects: o.select_set(False)
for lo_obj in lows.values(): lo_obj.select_set(True)
bpy.ops.export_scene.fbx(
    filepath=os.path.join(out, "LibroRunico.fbx"), use_selection=True, object_types={"MESH"}, use_mesh_modifiers=True,
    mesh_smooth_type="FACE", add_leaf_bones=False, bake_anim=False, path_mode="STRIP", embed_textures=False,
    axis_forward="-Z", axis_up="Y", apply_scale_options="FBX_SCALE_ALL", apply_unit_scale=True, global_scale=1.0)
print("EXPORTED", os.path.join(out, "LibroRunico.fbx"))
