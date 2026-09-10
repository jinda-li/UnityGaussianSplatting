"""Builds the Champ de Mars scene around the procedural tower.

Everything here is either generated or pulled from Poly Haven (CC0), so the
rendered images - and the splat trained from them - carry no third party
licence. The tower itself is public domain: Gustave Eiffel died in 1923, so the
design's copyright expired in 1993. Only the night-time illumination is still
protected, which is why this scene is daylight only.

    blender --background --python scene.py -- --detail 1.0 --out scene.blend
"""

from __future__ import annotations

import math
import os
import random
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import assets  # noqa: E402
import cityscape  # noqa: E402
import eiffel_tower  # noqa: E402
import tower_asset  # noqa: E402
from sky import HDRI, setup_hdri, setup_view  # noqa: E402

# The brief is a 120 m radius around the base, so the ground runs a little
# wider than that and the tree lines sit just inside the edge.
GROUND_RADIUS = 700.0
# The real esplanade under the tower is a good deal tighter than this used to
# be; pushing the lawn and the tree line in gives the scene something other
# than bare ground to look at from the camera ring.
ESPLANADE_RADIUS = 72.0
PLINTH_HEIGHT = 1.6
# The lawn sits a little below the gravel so the two never z-fight.
LAWN_Z = -0.04
# Training cameras stay inside this radius, so nothing gets scattered into it.
CAMERA_RADIUS = 95.0


def clear():
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)


def _plane(name, size, location=(0.0, 0.0, 0.0)):
    mesh = bpy.data.meshes.new(name)
    h = size * 0.5
    mesh.from_pydata([(-h, -h, 0.0), (h, -h, 0.0), (h, h, 0.0), (-h, h, 0.0)],
                     [], [(0, 1, 2, 3)])
    mesh.uv_layers.new(name="UVMap")
    uv = mesh.uv_layers[0].data
    for i, co in enumerate([(0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0)]):
        uv[i].uv = co
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    obj.location = location
    bpy.context.scene.collection.objects.link(obj)
    return obj


def _disc(name, radius, z=0.0, segments=96):
    mesh = bpy.data.meshes.new(name)
    verts = [(0.0, 0.0, 0.0)]
    uvs = [(0.5, 0.5)]
    for i in range(segments):
        a = 2.0 * math.pi * i / segments
        verts.append((math.cos(a) * radius, math.sin(a) * radius, 0.0))
    faces = [(0, i + 1, (i + 1) % segments + 1) for i in range(segments)]
    mesh.from_pydata(verts, [], faces)
    mesh.uv_layers.new(name="UVMap")
    uv = mesh.uv_layers[0].data
    for poly in mesh.polygons:
        for li in poly.loop_indices:
            v = mesh.vertices[mesh.loops[li].vertex_index].co
            uv[li].uv = (v.x / (2.0 * radius) + 0.5, v.y / (2.0 * radius) + 0.5)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    obj.location = (0.0, 0.0, z)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def _box(name, sx, sy, sz, location):
    hx, hy = sx * 0.5, sy * 0.5
    verts = [(-hx, -hy, 0.0), (hx, -hy, 0.0), (hx, hy, 0.0), (-hx, hy, 0.0),
             (-hx, -hy, sz), (hx, -hy, sz), (hx, hy, sz), (-hx, hy, sz)]
    faces = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4),
             (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    mesh.uv_layers.new(name="UVMap")
    uv = mesh.uv_layers[0].data
    for poly in mesh.polygons:
        for li in poly.loop_indices:
            v = mesh.vertices[mesh.loops[li].vertex_index].co
            # planar UVs in metres so the stone tiles at a believable size
            if abs(poly.normal.z) > 0.5:
                uv[li].uv = (v.x, v.y)
            elif abs(poly.normal.x) > 0.5:
                uv[li].uv = (v.y, v.z)
            else:
                uv[li].uv = (v.x, v.z)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    obj.location = location
    bpy.context.scene.collection.objects.link(obj)
    return obj


def ground_material(name, asset_id, texres, uv_scale, variation=0.25,
                    noise_scale=0.035, specular=0.08, bump=0.0, tint=None,
                    detail_scale=2.7):
    """A tiled ground texture with large scale break-up.

    A bare tile repeated 150 times reads as an obvious grid, and worse, gives
    the splat trainer a periodic signal it will happily reconstruct as banding.
    Two defences: a low frequency world-space noise multiplied over the whole
    surface, and a second copy of the texture at a different scale blended in,
    which breaks the grid itself rather than just modulating it.
    """
    mat = assets.pbr_material(name, asset_id, texres, uv_scale=uv_scale,
                              specular=specular)
    if bump:
        _add_height_bump(mat, asset_id, texres, uv_scale, bump)
    nt = mat.node_tree
    bsdf = next(n for n in nt.nodes
                if n.bl_idname == "ShaderNodeBsdfPrincipled")
    base = bsdf.inputs["Base Color"]
    if not base.is_linked:
        return mat
    source = base.links[0].from_socket
    nt.links.remove(base.links[0])

    if detail_scale:
        source = _break_tiling(nt, source, asset_id, texres, uv_scale,
                               detail_scale)

    coord = nt.nodes.new("ShaderNodeTexCoord")
    mapping = nt.nodes.new("ShaderNodeMapping")
    mapping.inputs["Scale"].default_value = (noise_scale,) * 3
    noise = nt.nodes.new("ShaderNodeTexNoise")
    noise.inputs["Detail"].default_value = 4.0
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].color = (1.0 - variation,) * 3 + (1.0,)
    ramp.color_ramp.elements[1].color = (1.0 + variation * 0.55,) * 3 + (1.0,)
    mix = nt.nodes.new("ShaderNodeMixRGB")
    mix.blend_type = "MULTIPLY"
    mix.inputs["Fac"].default_value = 1.0

    nt.links.new(coord.outputs["Object"], mapping.inputs["Vector"])
    nt.links.new(mapping.outputs["Vector"], noise.inputs["Vector"])
    nt.links.new(noise.outputs["Fac"], ramp.inputs["Fac"])
    nt.links.new(source, mix.inputs["Color1"])
    nt.links.new(ramp.outputs["Color"], mix.inputs["Color2"])
    tail = mix.outputs["Color"]
    if tint:
        tinted = nt.nodes.new("ShaderNodeMixRGB")
        tinted.blend_type = "MULTIPLY"
        tinted.inputs["Fac"].default_value = 1.0
        tinted.inputs["Color2"].default_value = tint
        nt.links.new(tail, tinted.inputs["Color1"])
        tail = tinted.outputs["Color"]
    nt.links.new(tail, base)
    return mat


def _break_tiling(nt, source, asset_id, texres, uv_scale, detail_scale):
    """Blend a second, differently scaled copy of the texture over the first.

    Modulating a tiled texture with noise hides the seams but not the grid: the
    same arrangement of stones still repeats every tile, and at a grazing angle
    that shows up as diagonal banding across the middle distance. Overlaying
    the texture at an incommensurate scale breaks the pattern itself.
    """
    maps = assets.fetch_texture(asset_id, texres)
    if "diffuse" not in maps:
        return source
    coord = nt.nodes.new("ShaderNodeTexCoord")
    mapping = nt.nodes.new("ShaderNodeMapping")
    scale = uv_scale * detail_scale
    mapping.inputs["Scale"].default_value = (scale, scale, scale)
    mapping.inputs["Location"].default_value = (0.37, 0.61, 0.0)
    nt.links.new(coord.outputs["UV"], mapping.inputs["Vector"])
    tex = nt.nodes.new("ShaderNodeTexImage")
    tex.image = bpy.data.images.load(maps["diffuse"], check_existing=True)
    tex.extension = "REPEAT"
    nt.links.new(mapping.outputs["Vector"], tex.inputs["Vector"])

    mask_map = nt.nodes.new("ShaderNodeMapping")
    mask_map.inputs["Scale"].default_value = (0.22,) * 3
    nt.links.new(coord.outputs["Object"], mask_map.inputs["Vector"])
    mask = nt.nodes.new("ShaderNodeTexNoise")
    mask.inputs["Detail"].default_value = 3.0
    nt.links.new(mask_map.outputs["Vector"], mask.inputs["Vector"])

    blend = nt.nodes.new("ShaderNodeMixRGB")
    blend.blend_type = "OVERLAY"
    nt.links.new(mask.outputs["Fac"], blend.inputs["Fac"])
    nt.links.new(source, blend.inputs["Color1"])
    nt.links.new(tex.outputs["Color"], blend.inputs["Color2"])
    return blend.outputs["Color"]


def _add_height_bump(mat, asset_id, texres, uv_scale, strength):
    """Bump the displacement map into the normal.

    A gravel texture with only a normal map still reads as a printed carpet
    from eye height; the height map is what gives individual stones an edge.
    Real displacement would be better but subdividing a 144 m disc finely
    enough is not worth the memory here.
    """
    maps = assets.fetch_texture(asset_id, texres)
    if "displacement" not in maps:
        return
    nt = mat.node_tree
    bsdf = next(n for n in nt.nodes
                if n.bl_idname == "ShaderNodeBsdfPrincipled")
    mapping = next(n for n in nt.nodes if n.bl_idname == "ShaderNodeMapping")
    tex = nt.nodes.new("ShaderNodeTexImage")
    tex.image = bpy.data.images.load(maps["displacement"], check_existing=True)
    tex.image.colorspace_settings.name = "Non-Color"
    tex.extension = "REPEAT"
    nt.links.new(mapping.outputs["Vector"], tex.inputs["Vector"])
    bump_node = nt.nodes.new("ShaderNodeBump")
    bump_node.inputs["Strength"].default_value = strength
    bump_node.inputs["Distance"].default_value = 0.03
    nt.links.new(tex.outputs["Color"], bump_node.inputs["Height"])
    existing = bsdf.inputs["Normal"]
    if existing.is_linked:
        nt.links.new(existing.links[0].from_socket, bump_node.inputs["Normal"])
    nt.links.new(bump_node.outputs["Normal"], bsdf.inputs["Normal"])


def build_grass(texres, rng, count=260000, inner_pad=0.5, width=70.0):
    """Dense grass across the band the ground-level cameras look over.

    Scattered as a particle system rather than as objects. At the density a
    lawn actually needs - several clumps per square metre over some 40,000 m2 -
    one Blender object per clump runs to a quarter of a million datablocks,
    which bloats the .blend and makes the initial-point ray casting crawl. A
    hair system with collection instancing carries the same geometry for a
    fraction of the cost.
    """
    protos = assets.append_model("grass_medium_01", "1k", mode="variants",
                                 min_faces=300)
    if not protos:
        return None
    coll = bpy.data.collections.new("GrassClumps")
    bpy.context.scene.collection.children.link(coll)
    for obj in protos:
        obj.location = (0.0, 0.0, -500.0)
        obj.hide_render = True
        for user in list(obj.users_collection):
            user.objects.unlink(obj)
        coll.objects.link(obj)

    inner = ESPLANADE_RADIUS + inner_pad
    emitter = _ring("LawnEmitter", inner, inner + width, z=LAWN_Z - 0.02)
    emitter.show_instancer_for_render = False

    mod = emitter.modifiers.new("Grass", "PARTICLE_SYSTEM")
    ps = emitter.particle_systems[mod.name]
    st = ps.settings
    st.type = "HAIR"
    st.count = count
    st.hair_length = 1.0
    st.use_advanced_hair = True
    st.render_type = "COLLECTION"
    st.instance_collection = coll
    st.use_collection_pick_random = True
    st.particle_size = 0.95
    st.size_random = 0.45
    st.use_rotations = True
    st.rotation_mode = "GLOB_Z"
    st.phase_factor_random = 2.0
    st.child_type = "NONE"
    print("[grass] %d clumps from %d variants" % (count, len(protos)))
    return emitter


def _ring(name, inner, outer, z=0.0, segments=192):
    """Flat annulus, used as a scatter surface."""
    verts = []
    faces = []
    for i in range(segments):
        a = 2.0 * math.pi * i / segments
        verts.append((math.cos(a) * inner, math.sin(a) * inner, 0.0))
        verts.append((math.cos(a) * outer, math.sin(a) * outer, 0.0))
    for i in range(segments):
        j = (i + 1) % segments
        faces.append((i * 2, i * 2 + 1, j * 2 + 1, j * 2))
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    obj.location = (0.0, 0.0, z)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def build_ground(texres, ground_res="4k"):
    """Sand esplanade under the tower, lawn beyond it, gravel allees.

    Tiling is expressed as a tile size in metres rather than a repeat count, so
    changing a surface's extent does not silently change how coarse it looks.
    """
    lawn = _disc("Lawn", GROUND_RADIUS, z=LAWN_Z)
    lawn.data.materials.append(
        ground_material("Lawn", "leafy_grass", ground_res,
                        uv_scale=GROUND_RADIUS * 2.0 / 1.6, variation=0.22,
                        tint=(0.62, 0.86, 0.48, 1.0)))

    # The esplanade is the surface the player stands on, so it gets the highest
    # texture resolution and the tightest tiling in the scene.
    esplanade = _disc("Esplanade", ESPLANADE_RADIUS, z=0.0)
    esplanade.data.materials.append(
        # gravel_floor_04 is compacted sand with essentially no stones in it,
        # so from standing height it renders as a flat cream carpet. This one
        # has visible grain, which is what the eye looks for underfoot.
        ground_material("Sand", "flower_scattered_gravel", ground_res,
                        uv_scale=ESPLANADE_RADIUS * 2.0 / 0.85, variation=0.24,
                        bump=0.8))

    # The allees that run away from the tower down the Champ de Mars.
    gravel = ground_material("Gravel", "gravel_floor_02", ground_res,
                             uv_scale=150.0 / 1.0, variation=0.25)
    for sign in (1.0, -1.0):
        for offset in (-58.0, 58.0):
            path = _plane("Allee", 1.0, (offset, sign * 175.0, 0.01))
            path.scale = (13.0, 150.0, 1.0)
            path.data.materials.append(gravel)
    return esplanade


def build_haze(radius=700.0, density=3.0e-5, colour=(0.70, 0.79, 0.95, 1.0),
               anisotropy=0.4):
    """Aerial perspective from a bounded fog volume.

    Distance haze is one of the strongest depth cues a photograph has, and its
    absence is why an otherwise correct render still reads as CG: the tree line
    at 200 m comes out exactly as saturated as the gravel at 5 m.

    It has to be a bounded volume. Putting the same scatter on the world makes
    every path from the environment infinitely long, so all the light is
    extinguished and the whole scene renders as a black silhouette. Inside a
    sphere the path lengths are finite - and it renders *faster* than no fog at
    all, because scattering terminates rays early.

    Density and radius are both kept low. Thicker fog is physically fine but
    the tower then casts a hard-edged shadow shaft through it and across the
    sky, which looks like a rendering seam rather than weather.
    """
    bpy.ops.mesh.primitive_uv_sphere_add(radius=radius, segments=32,
                                         ring_count=16)
    haze = bpy.context.object
    haze.name = "Haze"
    if hasattr(haze, "visible_shadow"):
        haze.visible_shadow = False
    mat = bpy.data.materials.new("Haze")
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    vol = nt.nodes.new("ShaderNodeVolumeScatter")
    vol.inputs["Color"].default_value = colour
    vol.inputs["Anisotropy"].default_value = anisotropy
    vol.inputs["Density"].default_value = density
    # Volume only: a surface shader here would put a wall around the scene.
    nt.links.new(vol.outputs["Volume"], out.inputs["Volume"])
    haze.data.materials.append(mat)
    return haze


def build_sky_dome(radius=3000.0, segments=96, rings=48, env=None):
    """A camera-only shell painted with the same sky the world lights with.

    Without it the sky is the hardest part of the scene to reconstruct: it has
    no surface, so the trainer has to guess a depth for it, and with cameras
    spread over hundreds of metres every guess is contradicted by some other
    view. The result is a dirty, mottled sky full of floaters - which is most of
    the image area in a shot that looks up at the tower.

    The dome is invisible to every ray except the camera's, so it changes the
    lighting not at all; it only gives those Gaussians somewhere consistent to
    sit. At 3 km a few hundred metres of camera travel is under a degree of
    parallax, which a smooth gradient cannot show.
    """
    bpy.ops.mesh.primitive_uv_sphere_add(radius=radius, segments=segments,
                                         ring_count=rings)
    dome = bpy.context.object
    dome.name = "SkyDome"
    for attr in ("visible_diffuse", "visible_glossy", "visible_transmission",
                 "visible_volume_scatter", "visible_shadow"):
        if hasattr(dome, attr):
            setattr(dome, attr, False)

    mat = bpy.data.materials.new("SkyDome")
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    emission = nt.nodes.new("ShaderNodeEmission")
    sky = nt.nodes.new("ShaderNodeTexSky")
    geo = nt.nodes.new("ShaderNodeNewGeometry")
    flip = nt.nodes.new("ShaderNodeVectorMath")
    flip.operation = "SCALE"
    flip.inputs["Scale"].default_value = -1.0

    # Incoming points from the surface back towards the camera; the sky wants
    # the direction the ray was travelling.
    nt.links.new(geo.outputs["Incoming"], flip.inputs[0])

    if env is not None and env.image:
        # Sample the same HDRI the world is lit with, through the same
        # rotation, or the dome and the background will not agree.
        nt.nodes.remove(sky)
        sky = nt.nodes.new("ShaderNodeTexEnvironment")
        sky.image = env.image
        world_map = env.inputs["Vector"].links[0].from_node
        mapping = nt.nodes.new("ShaderNodeMapping")
        mapping.inputs["Rotation"].default_value = (
            world_map.inputs["Rotation"].default_value)
        nt.links.new(flip.outputs["Vector"], mapping.inputs["Vector"])
        nt.links.new(mapping.outputs["Vector"], sky.inputs["Vector"])
    else:
        world_sky = next((n for n in bpy.context.scene.world.node_tree.nodes
                          if n.bl_idname == "ShaderNodeTexSky"), None)
        if world_sky:
            sky.sky_type = world_sky.sky_type
            sky.sun_elevation = world_sky.sun_elevation
            sky.sun_rotation = world_sky.sun_rotation
            sky.turbidity = world_sky.turbidity
            sky.sun_disc = False
        nt.links.new(flip.outputs["Vector"], sky.inputs["Vector"])

    nt.links.new(sky.outputs["Color"], emission.inputs["Color"])
    nt.links.new(emission.outputs["Emission"], out.inputs["Surface"])
    dome.data.materials.append(mat)
    return dome


def build_plinths(texres):
    """Stone bases the four piers stand on."""
    stone = assets.pbr_material("Plinth", "large_sandstone_blocks", "4k",
                                uv_scale=0.28, specular=0.18)
    s = eiffel_tower.half_side(0.0)
    w = eiffel_tower.leg_width(0.0)
    for sx, sy in eiffel_tower.QUADRANTS:
        cx = sx * (s - w * 0.5)
        cy = sy * (s - w * 0.5)
        base = _box("Plinth", w + 9.0, w + 9.0, PLINTH_HEIGHT, (cx, cy, 0.0))
        base.data.materials.append(stone)


def _scatter(prototypes, placements, rng, name, z=0.0):
    """Linked duplicates of the prototype meshes - the geometry is shared, so a
    few hundred trees cost almost nothing beyond the originals."""
    made = []
    for proto in prototypes:
        # The originals are parked far below the ground; keep them out of the
        # render entirely rather than relying on the floor to hide them.
        proto.hide_render = True
    for (x, y, scale, rot) in placements:
        proto = rng.choice(prototypes)
        obj = bpy.data.objects.new("%s_inst" % name, proto.data)
        obj.location = (x, y, z)
        obj.rotation_euler = (0.0, 0.0, rot)
        obj.scale = (scale, scale, scale)
        bpy.context.scene.collection.objects.link(obj)
        made.append(obj)
    return made


def build_trees(texres, rng, rows=True):
    # jacaranda is the only one in the Poly Haven set with a full canopy, so it
    # carries most of the tree line. Weighting is done by repeating the loaded
    # prototype, not by appending the asset twice: a second append makes
    # Blender rename the objects, the exact-name match for the assembled tree
    # then misses, and the fallback hands back all fifteen loose parts.
    weights = [("jacaranda_tree", 5), ("tree_small_02", 2),
               ("island_tree_03", 1)]
    protos = []
    for asset_id, weight in weights:
        objs = assets.append_model(asset_id, texres)
        for obj in objs:
            obj.location = (0.0, 0.0, -500.0)   # park the originals off scene
        protos.extend(objs * weight)
    if not protos:
        return []

    placements = []
    if rows:
        # Double rows of plane trees flanking the allees, as on the Champ de
        # Mars. They start beyond CAMERA_RADIUS so no tree ever ends up in a
        # training camera's lap.
        for sign_x in (1.0, -1.0):
            for offset in (118.0, 140.0, 162.0):
                for sign_y in (1.0, -1.0):
                    for i in range(16):
                        y = sign_y * (92.0 + i * 14.0) + rng.uniform(-2.5, 2.5)
                        placements.append(
                            (sign_x * offset + rng.uniform(-2.5, 2.5), y,
                             rng.uniform(0.85, 1.25),
                             rng.uniform(0.0, math.tau)))
    # Belts further out. The far one matters more than it looks: at eye level
    # the ground disc's edge would otherwise meet the sky as a hard line.
    for _ in range(220):
        a = rng.uniform(0.0, math.tau)
        r = rng.uniform(122.0, 260.0)
        placements.append((math.cos(a) * r, math.sin(a) * r,
                           rng.uniform(0.8, 1.3), rng.uniform(0.0, math.tau)))
    for _ in range(260):
        a = rng.uniform(0.0, math.tau)
        r = rng.uniform(270.0, 620.0)
        placements.append((math.cos(a) * r, math.sin(a) * r,
                           rng.uniform(0.9, 1.5), rng.uniform(0.0, math.tau)))
    placements = [p for p in placements
                  if math.hypot(p[0], p[1]) > CAMERA_RADIUS + 12.0]
    # Scattered onto the lawn, which sits at LAWN_Z; bedding everything a few
    # centimetres below it hides the seam where a trunk or a tuft meets the
    # ground instead of leaving it visibly perched on top.
    return _scatter(protos, placements, rng, "Tree", z=LAWN_Z - 0.05)


def build_shrubs(texres, rng):
    """Low planting just outside the camera ring, for foreground depth."""
    protos = []
    for asset_id in ("shrub_01", "shrub_03", "grass_medium_01"):
        protos.extend(assets.append_model(asset_id, texres))
    for obj in protos:
        obj.location = (0.0, 0.0, -500.0)
    if not protos:
        return

    placements = []
    for _ in range(520):
        a = rng.uniform(0.0, math.tau)
        r = rng.uniform(ESPLANADE_RADIUS + 6.0, 200.0)
        placements.append((math.cos(a) * r, math.sin(a) * r,
                           rng.uniform(0.8, 1.6), rng.uniform(0.0, math.tau)))
    _scatter(protos, placements, rng, "Shrub")


def build_furniture(texres, rng):
    protos = []
    for asset_id in ("street_lamp_01", "painted_wooden_bench"):
        protos.append((asset_id, assets.append_model(asset_id, texres)))

    lamps = [o for i, objs in protos if i == "street_lamp_01" for o in objs]
    benches = [o for i, objs in protos if i != "street_lamp_01" for o in objs]
    for obj in lamps + benches:
        obj.location = (0.0, 0.0, -500.0)

    placements = []
    for i in range(22):
        a = math.tau * i / 22.0
        r = ESPLANADE_RADIUS - 8.0
        placements.append((math.cos(a) * r, math.sin(a) * r, 1.0, a))
    if lamps:
        _scatter(lamps, placements, rng, "Lamp", z=-0.03)

    seats = []
    for i in range(18):
        a = math.tau * i / 18.0 + 0.14
        r = ESPLANADE_RADIUS - 20.0
        seats.append((math.cos(a) * r, math.sin(a) * r, 1.0,
                      a + math.pi * 0.5))
    if benches:
        _scatter(benches, seats, rng, "Bench", z=-0.03)


DEFAULT_TOWER = os.path.join(
    os.path.expanduser("~"), "Documents", "3DGS", "Eiffel", "sketchfab",
    "eiffel_tower_model_3d_with_best_quality.glb")


def build_scene(detail=1.0, texres="2k", seed=7, with_assets=True,
                tower_path=DEFAULT_TOWER):
    rng = random.Random(seed)
    clear()
    scene = bpy.context.scene
    env = setup_hdri(scene)
    setup_view(scene)

    # An external mesh wins when one is present: the procedural generator gets
    # the silhouette right but not the tower's own bracing pattern, and it has
    # none of the pavilions, lift tracks or stairs.
    if tower_path and os.path.exists(tower_path):
        tower = tower_asset.import_tower(tower_path, texres=texres)
        # The imported mesh brings its own masonry piers, already sitting on
        # z = 0. Lifting it by PLINTH_HEIGHT as well - which the procedural
        # tower needs, because build_plinths puts its piers under it - left the
        # whole tower hovering 1.6 m off the ground.
        tower.location = (0.0, 0.0, 0.0)
    else:
        tower = eiffel_tower.build_tower(detail=detail,
                                         built_up=detail >= 0.6)
        tower.location = (0.0, 0.0, PLINTH_HEIGHT)

    if with_assets:
        build_sky_dome(env=env)
        build_haze()
        build_ground(texres)
        # The external tower brings its own piers; the procedural one does not.
        if not (tower_path and os.path.exists(tower_path)):
            build_plinths(texres)
        cityscape.build_hinterland()
        cityscape.build_seine()
        cityscape.build_city(rng)
        build_trees(texres, rng)
        build_grass(texres, rng)
        build_furniture(texres, rng)
    return tower


def _cli():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    cfg = {"detail": 1.0, "texres": "2k", "out": "", "seed": 7,
           "assets": 1, "tower": ""}
    for i, a in enumerate(argv):
        key = a.lstrip("-")
        if key in cfg and i + 1 < len(argv):
            cfg[key] = type(cfg[key])(argv[i + 1])
    return cfg


if __name__ == "__main__":
    cfg = _cli()
    build_scene(cfg["detail"], cfg["texres"], cfg["seed"], bool(cfg["assets"]),
                cfg["tower"] or DEFAULT_TOWER)
    if cfg["out"]:
        bpy.ops.wm.save_as_mainfile(filepath=os.path.abspath(cfg["out"]))
        print("[scene] saved", os.path.abspath(cfg["out"]))
