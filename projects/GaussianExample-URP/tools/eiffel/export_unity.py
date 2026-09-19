"""Export a light version of the built scene for Unity.

    blender --background scene_hdri.blend --python export_unity.py -- --out DIR

The Blender scene is built for offline path tracing: 1.24 million instanced grass
clumps, 872 trees at 2.2 million faces each, and materials that are node trees
with no Unity equivalent. None of that survives an FBX export, and most of it
would not run in real time if it did. So this writes a real-time stand-in:

    Tower.fbx        the tower mesh, unchanged
    TreeProto.fbx    one tree, cut from 2.2 M faces to a few thousand
    Lamp.fbx, Bench.fbx, Hoop.fbx, Perimeter.fbx
                     the other repeated props, one copy each at the origin
    Environment.fbx  every unique static mesh joined into one object, with
                     metre-scale UVs, plus one EMPTY per prop instance and three
                     markers: Hero, HeroLook and Sun

Why empties and not a placement file: Blender is Z-up right-handed and Unity is
Y-up left-handed, and hand-converting positions and rotations is where these
pipelines go wrong. An empty goes through the same FBX axis conversion as the
meshes do, so the Unity builder can put a prop on each empty with an identity
local transform and it lands where it was in Blender - whatever the importer
decided about axes. The Hero and Sun markers use the same trick for the spawn
point and the light direction.

Grass is left out. It is particles, and a lawn texture reads well enough from
standing height for this.
"""

import math
import os
import random
import sys

import bpy
import numpy as np
from mathutils import Matrix, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import sky  # noqa: E402
from scene import HERO_LOOK, HERO_POS  # noqa: E402

PROJECT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
DEFAULT_OUT = os.path.join(PROJECT, "Assets", "EiffelMR", "Generated", "Model")

# name prefix of the scattered copies -> the prototype they share
INSTANCES = {
    "Tree_inst": "TreeProto",
    "Lamp_inst": "Lamp",
    "Bench_inst": "Bench",
    "Hoop_inst": "Hoop",
    "Perimeter_inst": "Perimeter",
}
PROTO_SOURCE = {
    "Lamp": "street_lamp_01",
    "Bench": "painted_wooden_bench",
    "Hoop": "HoopProto",
    "Perimeter": "PerimeterBay",
}
SKIP = ("SkyDome", "Haze", "EiffelTower", "HeroCam", "ProbeCam")

# Trees further out than this are not exported. The far belt exists in the
# render to stop the ground disc meeting the sky as a hard line; in Unity the
# skybox horizon does that job, and it is several hundred trees.
TREE_RADIUS = 460.0


def args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    cfg = {"out": DEFAULT_OUT, "leaf_keep": 0.006, "leaf_grow": 5.0,
           "branch_keep": 0.012}
    for i, a in enumerate(argv):
        key = a.lstrip("-")
        if key in cfg and i + 1 < len(argv):
            cfg[key] = type(cfg[key])(argv[i + 1])
    return cfg


def world_pos(obj):
    return obj.matrix_world.to_translation()


def parked(obj):
    # Prototypes and appended loose parts are parked half a kilometre down.
    return world_pos(obj).z < -100.0


def export_fbx(objects, path):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objects:
        o.hide_set(False)
        o.hide_viewport = False
        o.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True, object_types={"MESH", "EMPTY"},
        apply_unit_scale=True, apply_scale_options="FBX_SCALE_ALL",
        axis_forward="-Z", axis_up="Y", bake_space_transform=False,
        use_mesh_modifiers=True, mesh_smooth_type="FACE",
        add_leaf_bones=False, bake_anim=False, path_mode="STRIP",
        embed_textures=False)
    print("[export] %-18s %d object(s)" % (os.path.basename(path),
                                            len(objects)), flush=True)


def copy_at_origin(src, name):
    """A detached copy of a prototype with its transform reset."""
    dup = src.copy()
    dup.data = src.data.copy()
    dup.name = name
    dup.matrix_world = Matrix.Identity(4)
    dup.hide_render = False
    bpy.context.scene.collection.objects.link(dup)
    return dup


def reduced_tree(src, leaf_keep, leaf_grow, branch_keep, trunk_ratio=0.05,
                 seed=11):
    """Cut a Poly Haven tree from millions of faces to a few thousand.

    The leaves are hundreds of thousands of small cards. A random few per cent
    of them, each grown about its own centre, keep the canopy's outline and
    much of its coverage at a fraction of the faces - thinning the cards is
    better than decimating them, which melts every card into a smear that no
    longer matches its alpha texture. Branches keep only their largest faces,
    which are the limbs; twigs are invisible from the ground anyway.
    """
    rng = np.random.default_rng(seed)
    me = src.data
    npoly = len(me.polygons)
    loop_start = np.empty(npoly, np.int32)
    loop_total = np.empty(npoly, np.int32)
    mat_idx = np.empty(npoly, np.int32)
    area = np.empty(npoly, np.float32)
    me.polygons.foreach_get("loop_start", loop_start)
    me.polygons.foreach_get("loop_total", loop_total)
    me.polygons.foreach_get("material_index", mat_idx)
    me.polygons.foreach_get("area", area)
    nloop = len(me.loops)
    loop_vert = np.empty(nloop, np.int32)
    me.loops.foreach_get("vertex_index", loop_vert)
    co = np.empty(len(me.vertices) * 3, np.float32)
    me.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3)
    uv = None
    if me.uv_layers.active:
        uv = np.empty(nloop * 2, np.float32)
        me.uv_layers.active.data.foreach_get("uv", uv)
        uv = uv.reshape(-1, 2)

    names = [m.name.lower() if m else "" for m in me.materials]
    keep = np.zeros(npoly, bool)
    grow = np.ones(npoly, np.float32)
    for i, n in enumerate(names):
        sel = mat_idx == i
        if "leaves" in n:
            keep |= sel & (rng.random(npoly) < leaf_keep)
            grow[sel] = leaf_grow
        elif "branch" in n:
            if sel.any():
                cut = np.quantile(area[sel], 1.0 - branch_keep)
                keep |= sel & (area >= cut)
        else:                       # trunk
            keep |= sel
        print("[tree] %-28s %8d faces -> %7d" % (n, int(sel.sum()),
                                                 int((sel & keep).sum())),
              flush=True)
    faces = np.flatnonzero(keep)

    verts, polys, uvs, mats = [], [], [], []
    for f in faces:
        s, t = loop_start[f], loop_total[f]
        lv = loop_vert[s:s + t]
        p = co[lv]
        c = p.mean(axis=0)
        p = c + (p - c) * grow[f]
        base = len(verts)
        verts.extend(p.tolist())
        polys.append(list(range(base, base + t)))
        if uv is not None:
            uvs.extend(uv[s:s + t].tolist())
        mats.append(mat_idx[f])

    out = bpy.data.meshes.new("TreeProto")
    out.from_pydata(verts, [], polys)
    for m in me.materials:
        out.materials.append(m)
    out.polygons.foreach_set("material_index", np.array(mats, np.int32))
    if uv is not None:
        layer = out.uv_layers.new(name="UVMap")
        layer.data.foreach_set("uv", np.array(uvs, np.float32).ravel())
    out.update()
    obj = bpy.data.objects.new("TreeProto", out)
    bpy.context.scene.collection.objects.link(obj)

    # The trunk is a closed tube, so thinning its faces would leave holes; it
    # is collapsed instead, and only it - a vertex group keeps the decimate off
    # the leaf cards, which collapse would reduce to nothing.
    trunk_mats = [i for i, n in enumerate(names)
                  if "leaves" not in n and "branch" not in n]
    trunk_verts = sorted({v for p, m in zip(polys, mats) if m in trunk_mats
                          for v in p})
    if trunk_verts:
        group = obj.vertex_groups.new(name="trunk")
        group.add(trunk_verts, 1.0, "REPLACE")
        dec = obj.modifiers.new("TrunkDecimate", "DECIMATE")
        dec.decimate_type = "COLLAPSE"
        dec.ratio = trunk_ratio
        dec.vertex_group = "trunk"
    deps = bpy.context.evaluated_depsgraph_get()
    final = len(obj.evaluated_get(deps).data.polygons)
    print("[tree] %d -> %d faces" % (npoly, final), flush=True)
    return obj


def metre_uvs(obj):
    """UVs = world X/Y in metres, so a Unity material's tiling is its tile size.

    The ground in the Blender scene is box-projected in the shader and several
    meshes have no usable UVs; this gives every static surface the same
    predictable mapping.
    """
    me = obj.data
    layer = me.uv_layers.new(name="UVMetres")
    me.uv_layers.active = layer
    nloop = len(me.loops)
    lv = np.empty(nloop, np.int32)
    me.loops.foreach_get("vertex_index", lv)
    co = np.empty(len(me.vertices) * 3, np.float32)
    me.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3)[lv]
    layer.data.foreach_set("uv", co[:, :2].ravel())
    # Only this layer, so Unity's UV0 is unambiguous.
    for other in [l for l in me.uv_layers if l.name != "UVMetres"]:
        me.uv_layers.remove(other)


def join_statics(objs):
    """Every unique static mesh, transforms applied, joined into one."""
    copies = []
    deps = bpy.context.evaluated_depsgraph_get()
    for o in objs:
        ev = o.evaluated_get(deps)
        me = bpy.data.meshes.new_from_object(ev)
        me.transform(o.matrix_world)
        c = bpy.data.objects.new(o.name + "_s", me)
        bpy.context.scene.collection.objects.link(c)
        copies.append(c)
    bpy.ops.object.select_all(action="DESELECT")
    for c in copies:
        c.select_set(True)
    bpy.context.view_layer.objects.active = copies[0]
    bpy.ops.object.join()
    joined = bpy.context.view_layer.objects.active
    joined.name = "Static"
    metre_uvs(joined)
    return joined


def marker(name, location, look=None):
    e = bpy.data.objects.new(name, None)
    e.location = location
    if look is not None:
        d = Vector(look) - Vector(location)
        # Empties point down -Z with Y up, the same convention as a camera.
        e.rotation_euler = d.to_track_quat("-Z", "Z").to_euler()
    bpy.context.scene.collection.objects.link(e)
    return e


def sun_vector(scene, out_dir=None):
    """Direction to the sun in world space; also writes sky.json.

    sky.json carries the sun's azimuth in the HDRI's OWN frame. Unity shows the
    same image through Skybox/Panoramic, and to put its sun where the light is
    it needs the angle between the two - which only this side knows, because
    Blender solved the world rotation from the image.
    """
    env = next((n for n in scene.world.node_tree.nodes
                if n.bl_idname == "ShaderNodeTexEnvironment"), None)
    elev, image_az = 33.4, None
    if env and env.image:
        image_az, elev = sky.sun_direction(env.image)
    if out_dir:
        import json
        with open(os.path.join(out_dir, "sky.json"), "w") as fh:
            json.dump({"image_azimuth_deg": image_az, "elevation_deg": elev,
                       "world_azimuth_deg": sky.SUN_AZIMUTH,
                       "hdri": os.path.basename(env.image.filepath) if env else None},
                      fh, indent=2)
    az = math.radians(sky.SUN_AZIMUTH)
    el = math.radians(elev)
    print("[export] sun azimuth %.0f elevation %.1f" % (sky.SUN_AZIMUTH, elev),
          flush=True)
    return Vector((math.cos(el) * math.cos(az), math.cos(el) * math.sin(az),
                   math.sin(el)))


def main():
    cfg = args()
    out = os.path.abspath(cfg["out"])
    os.makedirs(out, exist_ok=True)
    scene = bpy.context.scene
    objs = list(scene.objects)

    tower = bpy.data.objects["EiffelTower"]
    export_fbx([tower], os.path.join(out, "Tower.fbx"))

    tree_src = bpy.data.objects.get("jacaranda_tree_LOD0")
    tree = reduced_tree(tree_src, cfg["leaf_keep"], cfg["leaf_grow"],
                        cfg["branch_keep"])
    export_fbx([tree], os.path.join(out, "TreeProto.fbx"))
    for proto, src_name in PROTO_SOURCE.items():
        src = bpy.data.objects.get(src_name)
        if not src:
            print("[export] missing prototype", src_name, flush=True)
            continue
        export_fbx([copy_at_origin(src, proto)],
                   os.path.join(out, proto + ".fbx"))

    statics, empties = [], []
    counts = {}
    for o in objs:
        if o.type != "MESH" or o.hide_render or parked(o):
            continue
        base = o.name.split(".")[0]
        if base in SKIP or o.particle_systems:
            continue
        proto = INSTANCES.get(base)
        if proto:
            if proto == "TreeProto" and world_pos(o).length > TREE_RADIUS:
                continue
            n = counts.get(proto, 0)
            counts[proto] = n + 1
            e = bpy.data.objects.new("I__%s__%04d" % (proto, n), None)
            e.matrix_world = o.matrix_world.copy()
            scene.collection.objects.link(e)
            empties.append(e)
        elif base.startswith(("grass_", "jacaranda_", "tree_small",
                              "island_tree")):
            continue
        else:
            statics.append(o)
    print("[export] %d static meshes, instances %s" % (len(statics), counts),
          flush=True)

    static = join_statics(statics)
    hero = marker("Hero", (HERO_POS[0], HERO_POS[1], 0.0))
    look = marker("HeroLook", HERO_POS, HERO_LOOK)
    sun = marker("Sun", sun_vector(scene, out) * 500.0)
    export_fbx([static, hero, look, sun] + empties,
               os.path.join(out, "Environment.fbx"))
    print("[export] done ->", out, flush=True)


if __name__ == "__main__":
    main()
