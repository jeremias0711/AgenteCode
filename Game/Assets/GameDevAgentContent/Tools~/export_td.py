# GameDevAgent - Fase 11. Exports Unity-ready copies of the eligible "Tower Defense" Blender sources.
# NEVER saves the .blend: every change below (decimation, re-parenting, texture resizing) lives only in this
# Blender session and is discarded when the process exits. The source files stay untouched.
#
# Usage (one source file per process):
#   blender -b --factory-startup --disable-autoexec <source.blend> -P export_td.py -- mode=<character|enemies|props>
#           out=<dir> [name=<Name>] [tex=1024] [dec=meshSubstring:ratio,...] [fisga=1] [actions=all|active]
import bpy, json, os, sys, math
import numpy as np
from mathutils import Matrix, Vector

args = dict(a.split("=", 1) for a in sys.argv[sys.argv.index("--") + 1:] if "=" in a)
MODE = args["mode"]
OUT = os.path.abspath(args["out"])
NAME = args.get("name", "Export")
TEX = int(args.get("tex", "1024"))
DEC = {}
for part in filter(None, args.get("dec", "").split(",")):
    k, v = part.rsplit(":", 1)
    DEC[k] = float(v)
report = {"source": bpy.data.filepath, "mode": MODE, "name": NAME, "warnings": [], "meshes": [], "textures": [],
          "materials": {}, "actions": []}


def warn(msg):
    report["warnings"].append(msg)
    print("WARN", msg)


def tris_of(ob):
    dg = bpy.context.evaluated_depsgraph_get()
    ev = ob.evaluated_get(dg)
    m = ev.to_mesh()
    m.calc_loop_triangles()
    n = len(m.loop_triangles)
    ev.to_mesh_clear()
    return n


def world_size(ob):
    """World-space size of the evaluated (posed) mesh, to compare the parts of one character."""
    dg = bpy.context.evaluated_depsgraph_get()
    ev = ob.evaluated_get(dg)
    m = ev.to_mesh()
    if len(m.vertices) == 0:
        ev.to_mesh_clear()
        return [0, 0, 0]
    co = np.empty(len(m.vertices) * 3, dtype=np.float32)
    m.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3)
    mw = np.array(ob.matrix_world)
    w = co @ mw[:3, :3].T + mw[:3, 3]
    ev.to_mesh_clear()
    return [round(float(v), 3) for v in (w.max(0) - w.min(0))] + [round(float(v), 3) for v in w.mean(0)]


# ---------------------------------------------------------------- textures
def find_principled(mat):
    if not (mat and mat.use_nodes and mat.node_tree):
        return None
    for n in mat.node_tree.nodes:
        if n.type == "BSDF_PRINCIPLED":
            return n
    return None


def trace(sock, depth=0):
    """Returns (image, channel) feeding a socket. channel: 'A' alpha output, 0/1/2 a separated channel, None colour."""
    if sock is None or not sock.is_linked or depth > 8:
        return None, None
    link = sock.links[0]
    n = link.from_node
    if n.type == "TEX_IMAGE":
        return n.image, ("A" if link.from_socket.name == "Alpha" else None)
    if n.type in ("SEPARATE_COLOR", "SEPRGB", "SEPARATE_RGB"):
        ch = {"Red": 0, "R": 0, "Green": 1, "G": 1, "Blue": 2, "B": 2}.get(link.from_socket.name, 0)
        img, _ = trace(n.inputs[0], depth + 1)
        return img, ch
    if n.type == "NORMAL_MAP":
        return trace(n.inputs.get("Color"), depth + 1)
    if n.type == "BUMP":
        return trace(n.inputs.get("Normal"), depth + 1)
    for inp in n.inputs:
        if inp.is_linked:
            img, ch = trace(inp, depth + 1)
            if img:
                return img, ch
    return None, None


def pixels(img, w, h):
    c = img.copy()
    if tuple(c.size) != (w, h):
        c.scale(w, h)
    a = np.empty(w * h * 4, dtype=np.float32)
    c.pixels.foreach_get(a)
    bpy.data.images.remove(c)
    return a.reshape(h, w, 4)


def save(arr, path, colorspace):
    h, w = arr.shape[:2]
    im = bpy.data.images.new("gda_out", w, h, alpha=True)
    im.colorspace_settings.name = colorspace
    im.pixels.foreach_set(np.ascontiguousarray(arr, dtype=np.float32).ravel())
    im.filepath_raw = path
    im.file_format = "PNG"
    im.save()
    bpy.data.images.remove(im)
    report["textures"].append(os.path.relpath(path, OUT).replace("\\", "/"))


def target_size(img):
    w, h = img.size
    if w == 0 or h == 0:
        return 0, 0
    s = min(1.0, TEX / float(max(w, h)))
    return max(4, int(round(w * s))), max(4, int(round(h * s)))


def export_material(mat, texdir):
    if mat is None or mat.name in report["materials"]:
        return
    entry = {"albedo": None, "normal": None, "ms": None, "cutout": False, "color": list(mat.diffuse_color)}
    report["materials"][mat.name] = entry
    p = find_principled(mat)
    safe = "".join(ch if ch.isalnum() or ch in "-_" else "_" for ch in mat.name)
    base_img = alpha_img = norm_img = rough_img = metal_img = None
    alpha_ch = rough_ch = metal_ch = None
    if p:
        base_img, _ = trace(p.inputs.get("Base Color"))
        alpha_img, alpha_ch = trace(p.inputs.get("Alpha"))
        norm_img, _ = trace(p.inputs.get("Normal"))
        rough_img, rough_ch = trace(p.inputs.get("Roughness"))
        metal_img, metal_ch = trace(p.inputs.get("Metallic"))
        if base_img is None:
            entry["color"] = list(p.inputs["Base Color"].default_value)
    elif mat.use_nodes and mat.node_tree:
        imgs = [n.image for n in mat.node_tree.nodes if n.type == "TEX_IMAGE" and n.image]
        base_img = imgs[0] if imgs else None
        warn("material %s has no Principled BSDF; used first image" % mat.name)

    def usable(img):
        if img is None:
            return False
        if img.packed_file is None and not os.path.exists(bpy.path.abspath(img.filepath)):
            warn("material %s: image %s is missing (not packed, file not found)" % (mat.name, img.name))
            return False
        return img.size[0] > 0

    if usable(base_img):
        w, h = target_size(base_img)
        rgba = pixels(base_img, w, h)
        if usable(alpha_img):
            if alpha_img == base_img and alpha_ch == "A":
                pass
            else:
                a = pixels(alpha_img, w, h)
                rgba[..., 3] = a[..., 3] if alpha_ch == "A" else a[..., alpha_ch if isinstance(alpha_ch, int) else 0]
            entry["cutout"] = bool((rgba[..., 3] < 0.5).mean() > 0.01)
        else:
            rgba[..., 3] = 1.0
        save(rgba, os.path.join(texdir, safe + "_albedo.png"), "sRGB")
        entry["albedo"] = "Textures/" + safe + "_albedo.png"
    if usable(norm_img):
        w, h = target_size(norm_img)
        save(pixels(norm_img, w, h), os.path.join(texdir, safe + "_normal.png"), "Non-Color")
        entry["normal"] = "Textures/" + safe + "_normal.png"
    if usable(rough_img) or usable(metal_img):
        ref = rough_img if usable(rough_img) else metal_img
        w, h = target_size(ref)
        ms = np.zeros((h, w, 4), dtype=np.float32)
        if usable(metal_img):
            m = pixels(metal_img, w, h)
            ms[..., 0] = m[..., metal_ch if isinstance(metal_ch, int) else 0]
        elif p:
            ms[..., 0] = p.inputs["Metallic"].default_value
        if usable(rough_img):
            r = pixels(rough_img, w, h)
            ms[..., 3] = 1.0 - r[..., rough_ch if isinstance(rough_ch, int) else 0]
        elif p:
            ms[..., 3] = 1.0 - p.inputs["Roughness"].default_value
        save(ms, os.path.join(texdir, safe + "_ms.png"), "Non-Color")
        entry["ms"] = "Textures/" + safe + "_ms.png"
    elif p:
        entry["metallic"] = p.inputs["Metallic"].default_value
        entry["smoothness"] = 1.0 - p.inputs["Roughness"].default_value


# ---------------------------------------------------------------- rig helpers
def unhide(ob):
    ob.hide_viewport = False
    ob.hide_render = False
    ob.hide_select = False  # unselectable objects are silently skipped by use_selection
    try:
        ob.hide_set(False)
    except Exception:
        pass


def deform_bone_for(arm, bone_name, world_pos):
    bones = arm.data.bones
    if bone_name:
        cands = [bone_name, bone_name.replace("c_", "", 1)]
        for suffix in ("_fk", "_ik"):
            cands += [c.replace(suffix, "") for c in list(cands)]
        for c in cands:
            if c in bones and bones[c].use_deform:
                return c
    best, bd = None, 1e9
    for b in bones:
        if not b.use_deform:
            continue
        if bone_name and "hand" in bone_name and "hand" not in b.name:
            continue
        d = ((arm.matrix_world @ arm.pose.bones[b.name].head) - world_pos).length
        if d < bd:
            best, bd = b.name, d
    return best


def rigidify(ob, arm, bone):
    """Turns an object attached to a bone/empty into a mesh skinned 100% to a deform bone (bind pose preserved)."""
    bpy.context.view_layer.update()
    world = ob.matrix_world.copy()
    pb = arm.pose.bones[bone]
    m_pose = arm.matrix_world @ pb.matrix
    m_rest = arm.matrix_world @ arm.data.bones[bone].matrix_local
    rest_world = m_rest @ m_pose.inverted() @ world
    for c in list(ob.constraints):
        ob.constraints.remove(c)
    # Bake the whole transform (often a rotated, non-uniform scale under a bone = shear, which FBX cannot store)
    # into a private copy of the mesh, and leave the object with an identity transform under the armature.
    ob.data = ob.data.copy()
    ob.parent = arm
    ob.parent_type = "OBJECT"
    ob.matrix_parent_inverse = Matrix.Identity(4)
    ob.data.transform(arm.matrix_world.inverted() @ rest_world)
    ob.matrix_basis = Matrix.Identity(4)
    ob.vertex_groups.clear()
    vg = ob.vertex_groups.new(name=bone)
    vg.add(list(range(len(ob.data.vertices))), 1.0, "REPLACE")
    md = ob.modifiers.new("GDA_Armature", "ARMATURE")
    md.object = arm
    print("rigidified", ob.name, "->", bone)


def apply_decimation(ob):
    for key, ratio in DEC.items():
        if key.lower() in ob.name.lower() and ratio < 1.0:
            if ob.data.shape_keys:
                ob.shape_key_clear()
            md = ob.modifiers.new("GDA_Decimate", "DECIMATE")
            md.ratio = ratio
            md.use_collapse_triangulate = True
            ob.modifiers.move(len(ob.modifiers) - 1, 0)
            return ratio
    return 1.0


def include_all_collections(lc=None):
    """Objects in excluded/hidden view-layer collections cannot be selected for export (session-only change)."""
    lc = lc or bpy.context.view_layer.layer_collection
    for child in lc.children:  # the scene master collection itself cannot be excluded
        child.exclude = False
        child.hide_viewport = False
        child.collection.hide_select = False
        child.collection.hide_viewport = False
        include_all_collections(child)


def export_fbx(path, objects, anim, all_actions):
    include_all_collections()
    bpy.ops.object.select_all(action="DESELECT")
    for ob in objects:
        unhide(ob)
        ob.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    report["selected_for_export"] = [o.name for o in objects if o.select_get()]
    missing = [o.name for o in objects if not o.select_get()]
    if missing:
        warn("not selectable for export: %s" % missing)
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True, object_types={"ARMATURE", "MESH"}, use_mesh_modifiers=True,
        mesh_smooth_type="FACE", add_leaf_bones=False, use_armature_deform_only=True,
        primary_bone_axis="Y", secondary_bone_axis="X", bake_anim=anim, bake_anim_use_all_bones=True,
        bake_anim_use_nla_strips=False, bake_anim_use_all_actions=all_actions,
        bake_anim_force_startend_keying=True, bake_anim_step=1.0, bake_anim_simplify_factor=1.0,
        path_mode="STRIP", embed_textures=False, axis_forward="-Z", axis_up="Y",
        apply_scale_options="FBX_SCALE_ALL", apply_unit_scale=True, global_scale=1.0)


def scene_visible(ob):
    return ob.name in bpy.context.scene.objects and not ob.hide_render


# ---------------------------------------------------------------- modes
def do_character():
    arms = [o for o in bpy.context.scene.objects if o.type == "ARMATURE"]
    arm = max(arms, key=lambda a: sum(1 for b in a.data.bones if b.use_deform))
    unhide(arm)
    meshes = []
    for ob in bpy.context.scene.objects:
        if ob.type != "MESH" or not scene_visible(ob) or "simulacion" in ob.name.lower():
            continue
        skinned = any(m.type == "ARMATURE" and m.object == arm for m in ob.modifiers)
        if skinned:
            for m in list(ob.modifiers):  # cloth-proxy deformers would bake a static snapshot: keep the skin only
                mtype = m.type
                if mtype in ("SURFACE_DEFORM", "SHRINKWRAP", "MESH_DEFORM", "CLOTH", "COLLISION", "CORRECTIVE_SMOOTH"):
                    ob.modifiers.remove(m)
                    report["warnings"].append("%s: removed %s modifier (cloth proxy); keeps its bone weights" % (ob.name, mtype))
            meshes.append(ob)
        elif ob.parent == arm and ob.parent_type == "BONE":
            world_pos = ob.matrix_world.translation
            rigidify(ob, arm, deform_bone_for(arm, ob.parent_bone, world_pos))
            meshes.append(ob)
    if args.get("fisga") == "1":
        fisga = next((o for o in bpy.data.objects if o.type == "MESH" and o.name.lower().startswith("fisga")), None)
        if fisga:
            anchor = fisga.parent
            sub = None
            for holder in (anchor, fisga):
                if holder is None:
                    continue
                for c in holder.constraints:
                    if getattr(c, "target", None) == arm and getattr(c, "subtarget", ""):
                        sub = c.subtarget
            unhide(fisga)
            if anchor:
                unhide(anchor)
            bpy.context.view_layer.update()
            bone = deform_bone_for(arm, sub or "hand.r", fisga.matrix_world.translation)
            report["fisga"] = {"constraint_bone": sub, "deform_bone": bone}
            rigidify(fisga, arm, bone)
            meshes.append(fisga)
        else:
            warn("fisga requested but not found")
    texdir = os.path.join(OUT, "Textures")
    os.makedirs(texdir, exist_ok=True)
    for ob in meshes:
        before = tris_of(ob)
        r = apply_decimation(ob)
        after = tris_of(ob)
        report["meshes"].append({"name": ob.name, "tris_source": before, "tris_export": after, "ratio": r,
                                 "world_size_center": world_size(ob),
                                 "materials": [s.material.name if s.material else None for s in ob.material_slots]})
        for s in ob.material_slots:
            export_material(s.material, texdir)
    for a in bpy.data.actions:
        report["actions"].append({"name": a.name, "frames": [a.frame_range[0], a.frame_range[1]]})
    report["armature"] = {"name": arm.name, "bones": len(arm.data.bones),
                          "deform": sum(1 for b in arm.data.bones if b.use_deform)}
    report["fps"] = bpy.context.scene.render.fps
    idle = next((a for a in bpy.data.actions if "idle" in a.name.lower() or "espera" in a.name.lower()), None)
    if idle and arm.animation_data:
        arm.animation_data.action = idle
        bpy.context.scene.frame_set(int(idle.frame_range[0]))
    export_fbx(os.path.join(OUT, NAME + ".fbx"), [arm] + meshes, True, args.get("actions", "all") == "all")


def do_enemies():
    global OUT
    texroot = OUT
    only = args.get("rig")
    for arm in [o for o in bpy.context.scene.objects if o.type == "ARMATURE"]:
        if only and arm.name != only:
            continue
        meshes = [o for o in bpy.context.scene.objects if o.type == "MESH" and scene_visible(o)
                  and any(m.type == "ARMATURE" and m.object == arm for m in o.modifiers)]
        if not meshes:
            continue
        nm = "Ghost_" + arm.name.replace("Rig_", "")
        sub = os.path.join(texroot, nm)
        os.makedirs(os.path.join(sub, "Textures"), exist_ok=True)
        OUT = sub
        for ob in meshes:
            report["meshes"].append({"name": ob.name, "rig": arm.name, "tris_export": tris_of(ob),
                                     "materials": [s.material.name for s in ob.material_slots if s.material]})
            for s in ob.material_slots:
                export_material(s.material, os.path.join(sub, "Textures"))
        act = arm.animation_data.action if arm.animation_data else None
        report["actions"].append({"rig": arm.name, "name": act.name if act else None,
                                  "frames": list(act.frame_range) if act else None})
        # One rig per process: keep only its own action and export it as a take (session-only removal).
        for other in list(bpy.data.actions):
            if other != act:
                bpy.data.actions.remove(other)
        export_fbx(os.path.join(sub, nm + ".fbx"), [arm] + meshes, act is not None, True)
        OUT = texroot


def do_static():
    """Unrigged character: exports only the listed meshes (objs=a,b,c), decimated per dec=..., without animation."""
    names = [n for n in args.get("objs", "").split(",") if n]
    texdir = os.path.join(OUT, "Textures")
    os.makedirs(texdir, exist_ok=True)
    objs = []
    for n in names:
        ob = bpy.data.objects.get(n)
        if ob is None or ob.type != "MESH":
            warn("object %s not found" % n)
            continue
        before = tris_of(ob)
        r = apply_decimation(ob)
        report["meshes"].append({"name": ob.name, "tris_source": before, "tris_export": tris_of(ob), "ratio": r,
                                 "world_size_center": world_size(ob),
                                 "materials": [s.material.name if s.material else None for s in ob.material_slots]})
        for s in ob.material_slots:
            export_material(s.material, texdir)
        objs.append(ob)
    export_fbx(os.path.join(OUT, NAME + ".fbx"), objs, False, False)


def clean_name(mat_name):
    n = mat_name.replace("Mat_", "").replace("Material_", "").split(".")[0]
    for s in ("_Baked", "_retopo", "_Retopo"):
        n = n.replace(s, "")
    return n or "Prop"


def do_props():
    texdir = os.path.join(OUT, "Textures")
    os.makedirs(texdir, exist_ok=True)
    objs, used = [], set()
    for ob in bpy.context.scene.objects:
        if ob.type != "MESH" or not scene_visible(ob):
            continue
        mat = ob.material_slots[0].material if ob.material_slots else None
        base = clean_name(mat.name) if mat else "Prop"
        if base == "retopo":
            base = "TerrenoIsla"
        nm, i = base, 2
        while nm in used:
            nm, i = "%s_%d" % (base, i), i + 1
        used.add(nm)
        report["meshes"].append({"name": nm, "source_object": ob.name, "tris_export": tris_of(ob),
                                 "dims": [round(v, 3) for v in ob.dimensions],
                                 "materials": [s.material.name for s in ob.material_slots if s.material]})
        ob.name = "GDA_" + nm
        for s in ob.material_slots:
            export_material(s.material, texdir)
        objs.append(ob)
    export_fbx(os.path.join(OUT, NAME + ".fbx"), objs, False, False)


os.makedirs(OUT, exist_ok=True)
try:
    {"character": do_character, "enemies": do_enemies, "props": do_props, "static": do_static}[MODE]()
    report["ok"] = True
except Exception as e:
    import traceback
    traceback.print_exc()
    report["ok"] = False
    report["error"] = repr(e)
manifest = os.path.join(os.path.abspath(args["out"]),
                        "manifest_%s.json" % args["rig"] if args.get("rig") else "manifest.json")
with open(manifest, "w", encoding="utf-8") as f:
    json.dump(report, f, ensure_ascii=False, indent=1)
print("EXPORT_DONE", MODE, NAME, "ok=%s" % report["ok"], manifest)
# The session is discarded on exit; nothing is saved back to the source .blend.
