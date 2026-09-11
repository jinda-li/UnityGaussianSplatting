"""Poly Haven asset fetching for the Eiffel scene.

Everything Poly Haven publishes is CC0, and their file API is open, so the
scene can be rebuilt from scratch on any machine without an account. Downloads
are cached under ASSET_DIR so a rebuild is cheap.
"""

from __future__ import annotations

import json
import os
import urllib.request

import bpy

API = "https://api.polyhaven.com"
UA = {"User-Agent": "EiffelSceneBuild/1.0"}

ASSET_DIR = os.environ.get(
    "EIFFEL_ASSETS",
    r"C:\Users\standalone\Documents\3DGS\Eiffel\assets",
)


def _get_json(url):
    req = urllib.request.Request(url, headers=UA)
    with urllib.request.urlopen(req) as fh:
        return json.load(fh)


def _download(url, dest):
    if os.path.exists(dest) and os.path.getsize(dest) > 0:
        return dest
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    tmp = dest + ".part"
    req = urllib.request.Request(url, headers=UA)
    with urllib.request.urlopen(req) as src, open(tmp, "wb") as out:
        while True:
            chunk = src.read(1 << 20)
            if not chunk:
                break
            out.write(chunk)
    os.replace(tmp, dest)
    print("[assets] fetched %s (%.1f MB)"
          % (os.path.basename(dest), os.path.getsize(dest) / 1e6))
    return dest


def fetch_model(asset_id, res="2k"):
    """Download a model's .blend plus its textures. Returns the .blend path."""
    files = _get_json("%s/files/%s" % (API, asset_id))
    entry = files["blend"][res]["blend"]
    root = os.path.join(ASSET_DIR, "models", asset_id, res)
    blend = _download(entry["url"], os.path.join(root, "%s.blend" % asset_id))
    for rel, info in entry.get("include", {}).items():
        _download(info["url"], os.path.join(root, rel.replace("/", os.sep)))
    return blend


def _pick_prototypes(asset_id, objects, mode, min_faces):
    """Choose which objects in a Poly Haven model file are worth instancing.

    These files hold more than one mesh per asset, and the extras are traps.
    A vegetation asset ships a `Cube` that is the geometry-nodes scatter
    domain, a carrier object holding the node tree, every piece at three levels
    of detail, and - for a tree - the trunk and each leaf card as separate
    source objects alongside the assembled tree.

    So `jacaranda_tree` yields seven meshes, of which exactly one
    (`jacaranda_tree_LOD0`, 2.2 M faces) is the tree; the rest are a bare trunk
    and five leaf cards. Scattering by picking one at random plants a bare
    trunk six times out of seven, which is what turned the tree line into dead
    scrub. Grass is the opposite case: its `large_a`, `mid_b`, `tall_c` and so
    on are all genuine clumps and should stay separate for variety.
    """
    meshes = [o for o in objects if o.type == "MESH"]
    lod0 = [o for o in meshes
            if o.name.lower().endswith("_lod0")
            and "geonodes" not in o.name.lower()]
    assembled = [o for o in lod0
                 if o.name.lower() == asset_id.lower() + "_lod0"]

    if mode == "assembled":
        chosen = assembled or lod0 or meshes
    else:
        chosen = [o for o in lod0 if o not in assembled] or lod0 or meshes
    chosen = [o for o in chosen if len(o.data.polygons) >= min_faces] or chosen
    return [o for o in chosen
            if not o.name.lower().startswith("cube")
            and "geometry_nodes" not in o.name.lower()]


def append_model(asset_id, res="2k", mode="assembled", min_faces=1):
    """Append a Poly Haven model and return the prototypes worth instancing.

    mode="assembled" returns the single fully built object (a whole tree);
    mode="variants" returns the separate clumps of a scatter asset.
    """
    blend = fetch_model(asset_id, res)
    before = set(bpy.data.objects)
    with bpy.data.libraries.load(blend, link=False) as (src, dst):
        dst.objects = list(src.objects)
    fresh = list(set(bpy.data.objects) - before)
    added = []
    for obj in _pick_prototypes(asset_id, fresh, mode, min_faces):
        bpy.context.scene.collection.objects.link(obj)
        added.append(obj)
    print("[assets] %s -> %d prototypes (%s) from %d objects"
          % (asset_id, len(added), mode, len(fresh)))
    return added


# Poly Haven's map names, in the order the material builder wants them.
_MAP_KEYS = {
    "diffuse": ("Diffuse", "diff", "col"),
    "normal": ("nor_gl",),
    "rough": ("Rough", "rough"),
    "displacement": ("Displacement", "disp"),
}


def fetch_texture(asset_id, res="2k", fmt="jpg"):
    """Download the maps for a Poly Haven texture. Returns {slot: path}."""
    files = _get_json("%s/files/%s" % (API, asset_id))
    root = os.path.join(ASSET_DIR, "textures", asset_id, res)
    out = {}
    for slot, candidates in _MAP_KEYS.items():
        for key in candidates:
            entry = files.get(key, {}).get(res, {})
            info = entry.get(fmt) or entry.get("jpg") or entry.get("png")
            if not info:
                continue
            name = os.path.basename(info["url"])
            out[slot] = _download(info["url"], os.path.join(root, name))
            break
    return out


def fetch_hdri(asset_id, res="4k", fmt="hdr"):
    """Download an HDRI and return the local path."""
    files = _get_json("%s/files/%s" % (API, asset_id))
    entry = files["hdri"][res]
    info = entry.get(fmt) or entry.get("hdr") or entry.get("exr")
    root = os.path.join(ASSET_DIR, "hdris", asset_id)
    return _download(info["url"], os.path.join(root,
                                               os.path.basename(info["url"])))


def pbr_material(name, asset_id, res="2k", uv_scale=1.0, displacement=False,
                 specular=0.2):
    """Principled material driven by a Poly Haven texture set.

    uv_scale is in tile repeats per unit of UV, so a ground plane whose UVs run
    0..1 across 300 m wants a large value.

    specular is pulled well below the Principled default of 0.5. Ground seen at
    a grazing angle under a bright sky picks up so much Fresnel white from that
    default that the sand's brown washes out to a flat grey.
    """
    maps = fetch_texture(asset_id, res)
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()

    out = nt.nodes.new("ShaderNodeOutputMaterial")
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    bsdf.inputs["Specular IOR Level"].default_value = specular
    coord = nt.nodes.new("ShaderNodeTexCoord")
    mapping = nt.nodes.new("ShaderNodeMapping")
    mapping.inputs["Scale"].default_value = (uv_scale, uv_scale, uv_scale)
    nt.links.new(coord.outputs["UV"], mapping.inputs["Vector"])
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])

    def image(path, non_color):
        tex = nt.nodes.new("ShaderNodeTexImage")
        tex.image = bpy.data.images.load(path, check_existing=True)
        if non_color:
            tex.image.colorspace_settings.name = "Non-Color"
        tex.extension = "REPEAT"
        nt.links.new(mapping.outputs["Vector"], tex.inputs["Vector"])
        return tex

    if "diffuse" in maps:
        nt.links.new(image(maps["diffuse"], False).outputs["Color"],
                     bsdf.inputs["Base Color"])
    if "rough" in maps:
        nt.links.new(image(maps["rough"], True).outputs["Color"],
                     bsdf.inputs["Roughness"])
    if "normal" in maps:
        nrm = nt.nodes.new("ShaderNodeNormalMap")
        nt.links.new(image(maps["normal"], True).outputs["Color"],
                     nrm.inputs["Color"])
        nt.links.new(nrm.outputs["Normal"], bsdf.inputs["Normal"])
    if displacement and "displacement" in maps:
        disp = nt.nodes.new("ShaderNodeDisplacement")
        disp.inputs["Scale"].default_value = 0.05
        nt.links.new(image(maps["displacement"], True).outputs["Color"],
                     disp.inputs["Height"])
        nt.links.new(disp.outputs["Displacement"], out.inputs["Displacement"])
    return mat

def retint(objects, value=1.0, saturation=1.0, hue=0.5,
           inputs=("Diffuse", "Diffuse Dead", "Base Color"), match=None):
    """Push a Poly Haven vegetation material's albedo without rebuilding it.

    Their models ship a node group per material - the image maps go into named
    sockets like Diffuse, Rough, Normal, Alpha, and the group does the leaf
    shading - so there is no Principled node to reach into and no single base
    colour to set. Inserting a Hue/Saturation node on the group's Diffuse
    input is the one edit that works on all of them and survives whatever the
    group does downstream.

    Needed because these albedos are authored for close-up product renders and
    come out too dark for turf seen across a park: measured against Commons
    photographs of the Champ de Mars, the lawn was rendering a quarter dark and
    noticeably less green than the real thing.

    `match` restricts the edit to materials whose name contains that substring,
    which is how a tree's trunk gets treated differently from its leaves - the
    parts arrive as one model with materials named for what they are.
    """
    seen = set()
    touched = 0
    for obj in objects:
        if obj.type != "MESH":
            continue
        for mat in obj.data.materials:
            if mat is None or mat.name in seen or not mat.node_tree:
                continue
            if match is not None and match not in mat.name:
                continue
            seen.add(mat.name)
            nt = mat.node_tree
            for node in list(nt.nodes):
                # Group nodes are Poly Haven's own vegetation shaders; the
                # Principled case covers the plainer materials - bark, branch
                # cards - that come through as an image straight into a BSDF.
                if node.bl_idname not in ("ShaderNodeGroup",
                                          "ShaderNodeBsdfPrincipled"):
                    continue
                for socket in node.inputs:
                    if socket.name not in inputs or not socket.is_linked:
                        continue
                    source = socket.links[0].from_socket
                    nt.links.remove(socket.links[0])
                    hsv = nt.nodes.new("ShaderNodeHueSaturation")
                    hsv.inputs["Hue"].default_value = hue
                    hsv.inputs["Saturation"].default_value = saturation
                    hsv.inputs["Value"].default_value = value
                    nt.links.new(source, hsv.inputs["Color"])
                    nt.links.new(hsv.outputs["Color"], socket)
                    touched += 1
    return touched
