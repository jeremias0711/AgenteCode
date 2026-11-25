# Phase 20.5: static, posed preview copy of a rigged character for the Collection ("coming soon" variants).
# Blender background only; the source .blend / .fbx is NEVER saved. Steps:
#   1. optionally imports an FBX (src=<file.fbx>); otherwise uses the opened .blend,
#   2. rest pose of the armature (no animation: the sources have no idle); optional arm lowering for T-pose rigs
#      (lower=<deg>: rotates the upper-arm bones in this session only, the rig file is untouched),
#   3. bakes every visible skinned mesh in that pose (evaluated mesh) into a static mesh, decimates (dec=<ratio>),
#   4. writes each material's base colour + normal map at <= tex px (PNG), exports a static FBX + manifest.json.
# Usage: blender -b --factory-startup --disable-autoexec [source.blend] -P export_preview_static.py --
#            out=<dir> name=<Name> [src=<file.fbx>] [tex=1024] [dec=1.0] [lower=0]
import bpy, json, os, sys, math
from mathutils import Matrix, Euler

args = dict(a.split("=", 1) for a in sys.argv[sys.argv.index("--") + 1:] if "=" in a)
OUT = os.path.abspath(args["out"]); NAME = args["name"]
TEX = int(args.get("tex", "1024")); DEC = float(args.get("dec", "1.0")); LOWER = float(args.get("lower", "0"))
os.makedirs(os.path.join(OUT, "Textures"), exist_ok=True)
report = {"name": NAME, "source": args.get("src") or bpy.data.filepath, "meshes": [], "textures": [], "actions": []}

if "src" in args:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=args["src"])
report["actions"] = [a.name for a in bpy.data.actions]

arm = max([o for o in bpy.data.objects if o.type == "ARMATURE"], key=lambda a: len(a.data.bones))
if arm.animation_data:
    arm.animation_data.action = None
for pb in arm.pose.bones:
    pb.matrix_basis = Matrix()
if LOWER:
    for pb in arm.pose.bones:
        n = pb.name.lower()
        if ("upper_arm" in n or "upperarm" in n) and "twist" not in n and "tweak" not in n:
            side = -1.0 if n.endswith((".r", "_r", "right")) else 1.0
            pb.rotation_mode = "XYZ"
            pb.rotation_euler = Euler((0.0, 0.0, side * math.radians(LOWER)))
            report.setdefault("lowered", []).append(pb.name)
bpy.context.view_layer.update()
deps = bpy.context.evaluated_depsgraph_get()

def save_image(img, path, colorspace):
    w, h = img.size
    s = min(1.0, TEX / max(w, h))
    copy = img.copy()
    if s < 1.0:
        copy.scale(max(1, int(w * s)), max(1, int(h * s)))
    copy.filepath_raw = path
    copy.file_format = "PNG"
    copy.colorspace_settings.name = colorspace
    copy.save()
    report["textures"].append({"file": os.path.basename(path), "size": list(copy.size)})

def linked_image(sock):
    for l in sock.links:
        n = l.from_node
        if n.type == "TEX_IMAGE" and n.image:
            return n.image
        for s in n.inputs:
            if s.is_linked:
                r = linked_image(s)
                if r: return r
    return None

baked = []
for ob in [o for o in bpy.data.objects if o.type == "MESH" and not o.hide_render]:
    ev = ob.evaluated_get(deps)
    me = bpy.data.meshes.new_from_object(ev, preserve_all_data_layers=True, depsgraph=deps)
    me.transform(ob.matrix_world)
    st = bpy.data.objects.new(ob.name, me)
    bpy.context.scene.collection.objects.link(st)
    if DEC < 1.0:
        d = st.modifiers.new("dec", "DECIMATE"); d.ratio = DEC
        bpy.context.view_layer.objects.active = st
        for o in bpy.context.selected_objects: o.select_set(False)
        st.select_set(True)
        bpy.ops.object.modifier_apply(modifier="dec")
    for slot in st.data.materials:
        if slot is None or not slot.use_nodes: continue
        bsdf = next((n for n in slot.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
        if bsdf is None: continue
        base = linked_image(bsdf.inputs["Base Color"])
        normal = linked_image(bsdf.inputs["Normal"])
        clean = "".join(c for c in (NAME + "_" + st.name) if c.isalnum() or c in "_-")
        if base: save_image(base, os.path.join(OUT, "Textures", clean + "_Albedo.png"), "sRGB")
        if normal: save_image(normal, os.path.join(OUT, "Textures", clean + "_Normal.png"), "Non-Color")
    report["meshes"].append({"name": st.name, "faces": len(st.data.polygons),
                             "materials": [m.name if m else None for m in st.data.materials]})
    baked.append(st)

# Feet on the ground, centred on the origin.
zs = [v.co.z for o in baked for v in o.data.vertices]; xs = [v.co.x for o in baked for v in o.data.vertices]; ys = [v.co.y for o in baked for v in o.data.vertices]
off = Matrix.Translation((-(min(xs) + max(xs)) / 2, -(min(ys) + max(ys)) / 2, -min(zs)))
for o in baked: o.data.transform(off)
report["height"] = max(zs) - min(zs)
for o in bpy.context.selected_objects: o.select_set(False)
for o in baked: o.select_set(True)
bpy.ops.export_scene.fbx(filepath=os.path.join(OUT, NAME + ".fbx"), use_selection=True, object_types={"MESH"},
                         mesh_smooth_type="FACE", bake_anim=False, path_mode="STRIP", embed_textures=False,
                         axis_forward="-Z", axis_up="Y", apply_scale_options="FBX_SCALE_ALL", apply_unit_scale=True)
with open(os.path.join(OUT, "manifest.json"), "w", encoding="utf-8") as f:
    json.dump(report, f, ensure_ascii=False, indent=1)
print("EXPORTED", NAME, json.dumps({k: report[k] for k in ("meshes", "height")}, ensure_ascii=False))
