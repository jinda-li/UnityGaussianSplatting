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
import parkland  # noqa: E402
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
# The band between the esplanade and the lawn: a gravel walk, then the hoop
# fence, then grass. These are constants rather than build_lawn_edge arguments
# because build_grass needs the same numbers - its emitter used to start at
# ESPLANADE_RADIUS + 0.5, which is 6 m inside the far edge of the walk, so the
# lawn grew straight over the gravel and the walk never appeared in a render at
# all.
WALK_WIDTH = 6.0
FENCE_PAD = 1.2

# The lawn is not a field. Seen from the tower, the Champ de Mars lawn is a
# narrow strip on the axis, cut into rectangular panels by transverse gravel
# walks, with a wide pale gravel apron between each panel and the rows of
# planes that wall the park in. This scene had one 700 m disc of grass instead,
# and no amount of dressing fixed the result reading as a field with a tower in
# it: what makes it a formal park is the geometry, not the props.
#
# Panels are 92 m across (46 either side of the axis) and about 105 m long.
LAWN_HALF_WIDTH = 46.0
LAWN_APRON = 66.0          # gravel runs from the panel edge out to here
LAWN_FROM = -80.0          # first panel starts just past the tower's walk
LAWN_TO = -440.0
CROSS_WALK_SPACING = 105.0
CROSS_WALK_WIDTH = 9.0
# The Trocadero half is shorter and is gardens rather than lawn.
LAWN_FROM_NORTH = 84.0
LAWN_TO_NORTH = 250.0


def lawn_panels():
    """The rectangles that are actually grass, as (cx, cy, sx, sy).

    One definition, used three times: to lay the gravel that separates them, to
    emit the grass that covers them, and to keep anything else off them.
    """
    panels = []
    for start, end in ((LAWN_FROM, LAWN_TO), (LAWN_FROM_NORTH, LAWN_TO_NORTH)):
        span = abs(end - start)
        count = max(int(round(span / CROSS_WALK_SPACING)), 1)
        step = span / count
        sign = -1.0 if end < start else 1.0
        for i in range(count):
            near = start + sign * i * step
            far = start + sign * (i + 1) * step
            # Trim the cross walk out of each end except the outermost.
            a = near + sign * (CROSS_WALK_WIDTH * 0.5)
            b = far - sign * (CROSS_WALK_WIDTH * 0.5)
            panels.append((0.0, (a + b) * 0.5,
                           LAWN_HALF_WIDTH * 2.0, abs(b - a)))
    return panels
# The lawn sits a little below the gravel so the two never z-fight.
LAWN_Z = -0.04
# Nothing gets scattered where a training camera stands. That used to be one
# radius, because the ground cameras were rings; now they are a ring round the
# esplanade plus a grid down the lawn, so the keep-out is a disc *and* a
# corridor. A tree inside either ends up in some camera's lap and punches a
# hole in the splat.
CAMERA_RADIUS = 95.0
# Half-width and extent of the lawn grid, padded. Matches parkland.ROW_X minus
# the room a canopy needs.
CORRIDOR_HALF_WIDTH = 58.0
CORRIDOR_FROM = -330.0
CORRIDOR_TO = 190.0


def clear_of_cameras(x, y, pad=10.0):
    """True if nothing scattered at (x, y) can end up in a training camera."""
    if math.hypot(x, y) < CAMERA_RADIUS + pad:
        return False
    return not (abs(x) < CORRIDOR_HALF_WIDTH + pad
                and CORRIDOR_FROM - pad < y < CORRIDOR_TO + pad)

# The one viewpoint this scene is dressed for.
#
# The splat has to look right from somewhere specific, and everything below -
# exposure, sun angle, where the grass goes, how far the lawn detail reaches -
# is tuned against this frame rather than against an average over the whole
# camera array. It is on the Champ de Mars lawn, slightly off the tower's axis
# so both faces of the near pier read, far enough back that the whole tower and
# a foreground of grass fit in a 20 mm lens, and at standing eye height.
#
# Closer than about 100 m and the piers fill the frame: at the landing spawn
# the old plan proposed, (66, -66), the camera is a few metres from a pier and
# sees masonry, not a tower. Further than about 160 m and the tree belt at
# r = 122 m closes across the lower half of the tower.
HERO_POS = (34.0, -116.0, 1.65)
HERO_LOOK = (0.0, 0.0, 66.0)
HERO_LENS = 20.0


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


def _rect(name, sx, sy, location=(0.0, 0.0, 0.0)):
    """Rectangle whose UVs are laid out in metres.

    _plane normalises its UVs to 0..1, which is fine for a square but stretches
    the texture on anything long and thin. Here one UV unit is one metre, so a
    material's uv_scale means "tiles per metre" and a 26 x 300 m path tiles the
    same way in both directions.
    """
    mesh = bpy.data.meshes.new(name)
    hx, hy = sx * 0.5, sy * 0.5
    mesh.from_pydata([(-hx, -hy, 0.0), (hx, -hy, 0.0),
                      (hx, hy, 0.0), (-hx, hy, 0.0)], [], [(0, 1, 2, 3)])
    mesh.uv_layers.new(name="UVMap")
    uv = mesh.uv_layers[0].data
    for i, co in enumerate([(0.0, 0.0), (sx, 0.0), (sx, sy), (0.0, sy)]):
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


def _surface_wear(mat, scale=0.55, strength=0.42, tint=(1.00, 0.98, 0.94, 1.0)):
    """Metre-scale mottling for a paved surface, in world space.

    ground_material already breaks up its tiling, but its noise is keyed to the
    object's own coordinates - and for the walk and the allees those run over
    150 m, so the "large scale" variation has features 30 m across and the
    surface is uniform over its whole width. Fine gravel photographs as a flat
    grey at any distance, which is correct, but a real path is flat grey with
    tracks worn into it, dirt washed across it and leaf litter blown into the
    edges. Without something at the metre scale it reads as poured concrete.

    The tint has to average 1.0. This is a modulation layer, not a colour, and
    it multiplies whatever came before it - so when it was centred on 0.68 it
    quietly took a third of a stop off every paved surface in the scene. That
    hid a later correction completely: raising the gravel's own tint by 65 per
    cent moved the rendered walk from luminance 136 to 139, because this was
    eating it. Same class of mistake as the ironwork albedo in section 4.24;
    look for it whenever a change to a colour has no visible effect.
    """
    nt = mat.node_tree
    bsdf = next(n for n in nt.nodes
                if n.bl_idname == "ShaderNodeBsdfPrincipled")
    base = bsdf.inputs["Base Color"]
    if not base.is_linked:
        return mat
    source = base.links[0].from_socket
    nt.links.remove(base.links[0])

    coord = nt.nodes.new("ShaderNodeTexCoord")
    mapping = nt.nodes.new("ShaderNodeMapping")
    mapping.inputs["Scale"].default_value = (scale,) * 3
    nt.links.new(coord.outputs["Generated"], mapping.inputs["Vector"])
    noise = nt.nodes.new("ShaderNodeTexNoise")
    noise.inputs["Scale"].default_value = 1.0
    noise.inputs["Detail"].default_value = 8.0
    noise.inputs["Roughness"].default_value = 0.62
    nt.links.new(mapping.outputs["Vector"], noise.inputs["Vector"])
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].position = 0.34
    ramp.color_ramp.elements[0].color = tuple(
        c * (1.0 - strength) for c in tint[:3]) + (1.0,)
    ramp.color_ramp.elements[1].position = 0.70
    ramp.color_ramp.elements[1].color = tuple(
        min(c * (1.0 + strength), 1.0) for c in tint[:3]) + (1.0,)
    nt.links.new(noise.outputs["Fac"], ramp.inputs["Fac"])
    mix = nt.nodes.new("ShaderNodeMixRGB")
    mix.blend_type = "MULTIPLY"
    mix.inputs["Fac"].default_value = 1.0
    nt.links.new(source, mix.inputs["Color1"])
    nt.links.new(ramp.outputs["Color"], mix.inputs["Color2"])
    nt.links.new(mix.outputs["Color"], base)
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


MOW_PERIOD = 7.4
MOW_STRENGTH = 0.13


def _mow_factor(nt, period=MOW_PERIOD, strength=MOW_STRENGTH):
    """A mowing-stripe multiplier, keyed to world position.

    A ride-on mower lays the blades over in the direction it travelled, so
    alternating passes reflect differently and the lawn reads as banded. In
    every photograph of the Champ de Mars those bands run up the park, parallel
    to its axis. The effect is small - a tenth of a stop - but it is the only
    cue in the frame that says "tended lawn" rather than "green field".

    Driven from Geometry.Position, not from object or generated coordinates,
    because the same stripe has to land on two different things: the lawn disc,
    and the grass clumps scattered over it. The clumps are particle instances
    and carry their own local coordinates, so anything object-space would give
    each clump its own private stripe. Position is world space for instances
    too, which is the only way the two line up.
    """
    geo = nt.nodes.new("ShaderNodeNewGeometry")
    mapping = nt.nodes.new("ShaderNodeMapping")
    mapping.inputs["Scale"].default_value = (1.0 / period,) * 3
    nt.links.new(geo.outputs["Position"], mapping.inputs["Vector"])
    wave = nt.nodes.new("ShaderNodeTexWave")
    wave.wave_type = "BANDS"
    wave.bands_direction = "X"
    wave.wave_profile = "SIN"
    wave.inputs["Scale"].default_value = 1.0
    wave.inputs["Distortion"].default_value = 0.0
    nt.links.new(mapping.outputs["Vector"], wave.inputs["Vector"])
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].color = (1.0 - strength,) * 3 + (1.0,)
    ramp.color_ramp.elements[1].color = (1.0 + strength,) * 3 + (1.0,)
    nt.links.new(wave.outputs["Fac"], ramp.inputs["Fac"])
    return ramp.outputs["Color"]


def mow_vegetation(objects, inputs=("Diffuse", "Diffuse Dead")):
    """Put the lawn's mowing stripe onto the grass clumps as well.

    Striping only the ground texture achieves nothing once the clumps cover it,
    which at this density is most of the frame - the stripes were invisible
    except on the bare patches. Poly Haven's vegetation materials are a node
    group fed by named image sockets, so the multiply goes on the same Diffuse
    input assets.retint uses.
    """
    seen = set()
    touched = 0
    for obj in objects:
        if obj.type != "MESH":
            continue
        for mat in obj.data.materials:
            if mat is None or mat.name in seen or not mat.node_tree:
                continue
            seen.add(mat.name)
            nt = mat.node_tree
            for node in list(nt.nodes):
                if node.bl_idname != "ShaderNodeGroup":
                    continue
                for socket in node.inputs:
                    if socket.name not in inputs or not socket.is_linked:
                        continue
                    source = socket.links[0].from_socket
                    nt.links.remove(socket.links[0])
                    mix = nt.nodes.new("ShaderNodeMixRGB")
                    mix.blend_type = "MULTIPLY"
                    mix.inputs["Fac"].default_value = 1.0
                    nt.links.new(source, mix.inputs["Color1"])
                    nt.links.new(_mow_factor(nt), mix.inputs["Color2"])
                    nt.links.new(mix.outputs["Color"], socket)
                    touched += 1
    return touched


def _lawn_macro(mat, fresh=(0.300, 0.520, 0.150, 1.0),
                dry=(0.520, 0.530, 0.230, 1.0),
                worn=(0.470, 0.400, 0.280, 1.0),
                thatch=(0.400, 0.360, 0.200, 1.0),
                patch_scale=0.009, wear_scale=0.030, thatch_scale=0.55):
    """Tint the lawn in patches instead of with one flat colour.

    A single multiply over a tiled grass texture reads as painted felt from
    standing height, and it is the loudest CG cue in the hero frame because the
    lawn owns the bottom third of it. Real turf on the Champ de Mars is a
    patchwork tens of metres across: fresh growth where the sprinklers reach,
    sun-dried yellow where they do not, and bare earth along the tracks people
    walk. Those patches are far larger than any texture tile, so they have to
    come from world-space noise; the texture keeps supplying the blade-level
    detail underneath.

    The values are set against measurements off reference photographs rather
    than by eye, because eye-matching a lawn inside a render that is itself
    mis-exposed just moves the error around. On two Commons photographs of this
    exact view, sunlit Champ de Mars lawn measures about (135, 137, 78) and
    (109, 134, 54) - green, saturation 0.43 to 0.60, and in both of them the
    lawn is *brighter* than the sky above it.

    This scene had it the other way round: lawn (81, 87, 56) at saturation
    0.36, under a sky at (225, 228, 234). Both halves of that were wrong. The
    tints multiply the texture, whose own luminance is about 0.35, so these
    have to sit well above the green one would pick off a colour wheel to reach
    a real lawn's albedo of roughly 0.25 - and the sky comes down separately,
    with exposure. Target on the hero probe: lawn near (125, 138, 70).
    """
    nt = mat.node_tree
    bsdf = next(n for n in nt.nodes
                if n.bl_idname == "ShaderNodeBsdfPrincipled")
    base = bsdf.inputs["Base Color"]
    if not base.is_linked:
        return mat
    source = base.links[0].from_socket
    nt.links.remove(base.links[0])

    coord = nt.nodes.new("ShaderNodeTexCoord")

    def noise(scale, detail):
        mapping = nt.nodes.new("ShaderNodeMapping")
        mapping.inputs["Scale"].default_value = (scale,) * 3
        nt.links.new(coord.outputs["Object"], mapping.inputs["Vector"])
        tex = nt.nodes.new("ShaderNodeTexNoise")
        tex.inputs["Detail"].default_value = detail
        nt.links.new(mapping.outputs["Vector"], tex.inputs["Vector"])
        return tex

    # Fresh/dry patchwork.
    patches = nt.nodes.new("ShaderNodeMixRGB")
    patches.inputs["Color1"].default_value = fresh
    patches.inputs["Color2"].default_value = dry
    patch_ramp = nt.nodes.new("ShaderNodeValToRGB")
    # Skewed towards fresh: an even split between the two tints averaged out
    # to the dry one and the whole lawn came back the colour of dust.
    patch_ramp.color_ramp.elements[0].position = 0.44
    patch_ramp.color_ramp.elements[1].position = 0.78
    nt.links.new(noise(patch_scale, 5.0).outputs["Fac"], patch_ramp.inputs["Fac"])
    nt.links.new(patch_ramp.outputs["Color"], patches.inputs["Fac"])

    # Worn tracks on top, kept to a small fraction of the area by a ramp that
    # only opens in the top of the noise range.
    tracks = nt.nodes.new("ShaderNodeMixRGB")
    tracks.inputs["Color2"].default_value = worn
    wear_ramp = nt.nodes.new("ShaderNodeValToRGB")
    wear_ramp.color_ramp.elements[0].position = 0.70
    wear_ramp.color_ramp.elements[1].position = 0.86
    nt.links.new(noise(wear_scale, 3.0).outputs["Fac"], wear_ramp.inputs["Fac"])
    nt.links.new(wear_ramp.outputs["Color"], tracks.inputs["Fac"])
    nt.links.new(patches.outputs["Color"], tracks.inputs["Color1"])

    # Thatch: the dead layer between the living blades. The two noises above
    # work at 100 m and 33 m, which is right for where a lawn is watered and
    # where it is walked on, but it left the ground between the grass clumps a
    # single flat olive - and that ground is most of what a camera at eye
    # height actually sees, because the clumps only cover it edge-on. This one
    # runs at about 1.8 m, the scale at which real turf goes patchy.
    thatched = nt.nodes.new("ShaderNodeMixRGB")
    thatched.inputs["Color2"].default_value = thatch
    thatch_ramp = nt.nodes.new("ShaderNodeValToRGB")
    # Kept to a minority of the area: at 0.42/0.70 the thatch covered about
    # half the lawn and the whole thing read brown.
    thatch_ramp.color_ramp.elements[0].position = 0.56
    thatch_ramp.color_ramp.elements[1].position = 0.82
    nt.links.new(noise(thatch_scale, 6.0).outputs["Fac"],
                 thatch_ramp.inputs["Fac"])
    nt.links.new(thatch_ramp.outputs["Color"], thatched.inputs["Fac"])
    nt.links.new(tracks.outputs["Color"], thatched.inputs["Color1"])

    mown = nt.nodes.new("ShaderNodeMixRGB")
    mown.blend_type = "MULTIPLY"
    mown.inputs["Fac"].default_value = 1.0
    nt.links.new(thatched.outputs["Color"], mown.inputs["Color1"])
    nt.links.new(_mow_factor(nt), mown.inputs["Color2"])

    mix = nt.nodes.new("ShaderNodeMixRGB")
    mix.blend_type = "MULTIPLY"
    mix.inputs["Fac"].default_value = 1.0
    nt.links.new(source, mix.inputs["Color1"])
    nt.links.new(mown.outputs["Color"], mix.inputs["Color2"])

    # Daisies and dandelions. A Paris lawn in summer is not one colour: it is
    # speckled with small bright flowers, and from standing height that speckle
    # is most of what tells the eye it is looking at a living lawn rather than a
    # green surface. Geometry would be better and is not affordable at the
    # densities involved, but the speckle survives being averaged down - which a
    # blade of grass does not - so it is one of the few lawn details that still
    # reads at fifty metres.
    speck = nt.nodes.new("ShaderNodeTexVoronoi")
    speck.feature = "F1"
    speck.inputs["Scale"].default_value = 1.0
    speck.inputs["Randomness"].default_value = 1.0
    speck_map = nt.nodes.new("ShaderNodeMapping")
    speck_map.inputs["Scale"].default_value = (5.5,) * 3
    speck_geo = nt.nodes.new("ShaderNodeNewGeometry")
    nt.links.new(speck_geo.outputs["Position"], speck_map.inputs["Vector"])
    nt.links.new(speck_map.outputs["Vector"], speck.inputs["Vector"])
    speck_ramp = nt.nodes.new("ShaderNodeValToRGB")
    speck_ramp.color_ramp.interpolation = "CONSTANT"
    speck_ramp.color_ramp.elements[0].position = 0.0
    speck_ramp.color_ramp.elements[0].color = (1.0, 1.0, 1.0, 1.0)
    # 0.17 of a cell at 18 cm cells is a flower head about 3 cm across, which
    # is life size. At 0.055 they were a centimetre and vanished under a pixel
    # from three metres away.
    speck_ramp.color_ramp.elements[1].position = 0.17
    speck_ramp.color_ramp.elements[1].color = (0.0, 0.0, 0.0, 1.0)
    nt.links.new(speck.outputs["Distance"], speck_ramp.inputs["Fac"])
    # Half white daisies, half yellow dandelions, picked by a second noise.
    hue = nt.nodes.new("ShaderNodeTexNoise")
    hue.inputs["Scale"].default_value = 90.0
    nt.links.new(speck_map.outputs["Vector"], hue.inputs["Vector"])
    hue_ramp = nt.nodes.new("ShaderNodeValToRGB")
    hue_ramp.color_ramp.elements[0].position = 0.45
    hue_ramp.color_ramp.elements[0].color = (0.78, 0.78, 0.70, 1.0)
    hue_ramp.color_ramp.elements[1].position = 0.55
    hue_ramp.color_ramp.elements[1].color = (0.82, 0.66, 0.10, 1.0)
    nt.links.new(hue.outputs["Fac"], hue_ramp.inputs["Fac"])
    flowered = nt.nodes.new("ShaderNodeMixRGB")
    nt.links.new(speck_ramp.outputs["Color"], flowered.inputs["Fac"])
    nt.links.new(mix.outputs["Color"], flowered.inputs["Color1"])
    nt.links.new(hue_ramp.outputs["Color"], flowered.inputs["Color2"])
    nt.links.new(flowered.outputs["Color"], base)
    return mat


def build_grass(texres, rng, count=620000, inner_pad=0.4, width=70.0):
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
    # See assets.retint: this asset's leaf albedo is authored dark, and a lawn
    # built out of it measures well below a photograph of the real one.
    assets.retint(protos, value=1.45, saturation=1.22)
    mow_vegetation(protos)
    coll = bpy.data.collections.new("GrassClumps")
    bpy.context.scene.collection.children.link(coll)
    for obj in protos:
        # Parked below the ground, but NOT hide_render: a particle system
        # instancing a collection skips any member that is hidden from the
        # render, so setting the flag here silently emptied the whole lawn -
        # 260,000 particles rendering nothing, and a bare texture underfoot
        # that no amount of shading work could fix.
        obj.location = (0.0, 0.0, -500.0)
        for user in list(obj.users_collection):
            user.objects.unlink(obj)
        coll.objects.link(obj)

    # One emitter per lawn panel. Emitters used to be a ring round the tower
    # plus a rectangle down the corridor, which had to be hand-fitted to avoid
    # putting grass on the gravel - and twice did not, which is how the walk
    # round the esplanade managed to be invisible in every render for a week
    # after it was built. Driving them off lawn_panels() means the grass is on
    # the grass by construction.
    emitters = []
    for cx, cy, sx, sy in lawn_panels():
        emitters.append(_rect("LawnEmitter", sx, sy, (cx, cy, LAWN_Z - 0.02)))
    # The band round the tower itself, inside the first panel.
    inner = ESPLANADE_RADIUS + WALK_WIDTH + FENCE_PAD + inner_pad
    emitters.append(_ring("LawnEmitterRing", inner, LAWN_APRON,
                          z=LAWN_Z - 0.02))

    # Split the budget by area so density is the same on every panel.
    areas = []
    for emitter in emitters:
        mesh = emitter.data
        mesh.calc_loop_triangles()
        areas.append(sum(t.area for t in mesh.loop_triangles) or 1.0)
    total = sum(areas)
    for index, emitter in enumerate(emitters):
        emitter.show_instancer_for_render = False
        mod = emitter.modifiers.new("Grass", "PARTICLE_SYSTEM")
        ps = emitter.particle_systems[mod.name]
        st = ps.settings
        st.type = "HAIR"
        st.count = max(int(count * 2.0 * areas[index] / total), 500)
        st.hair_length = 1.0
        st.use_advanced_hair = True
        st.render_type = "COLLECTION"
        st.instance_collection = coll
        st.use_collection_pick_random = True
        # Scale, then density - in that order, because they trade off. The
        # clumps are 25 cm tall as authored. Blown up to 1.5x they closed the
        # canopy, but the lawn then read as knee-high meadow and swallowed the
        # 42 cm hoop fence along its edge; a mown park lawn is 5-10 cm. So they
        # go the other way - but only part of the way. At 0.60 (15 cm) the
        # lawn came back reading as dry dirt with tufts on it, because a clump
        # that short no longer occludes the ground behind it at a grazing
        # angle. 0.95 is about 24 cm: still short enough that the hoop fence
        # stands clear of it, tall enough to close from eye height.
        st.particle_size = 0.95
        st.size_random = 0.40
        st.use_rotations = True
        st.rotation_mode = "GLOB_Z"
        st.phase_factor_random = 2.0
        # Children rather than a bigger count. The clumps cover about 40 per
        # cent of the ground at 13 per square metre, which from eye height
        # reads as weeds on soil rather than as turf, and closing that needs
        # something like 40 per square metre - 3.6 million particles, which the
        # .blend cannot carry. Children are generated at render time around
        # each parent, so they cost geometry but not particle storage.
        st.child_type = "SIMPLE"
        st.child_percent = 5
        st.rendered_child_count = 5
        st.child_radius = 0.30
        st.child_roundness = 1.0
    print("[grass] %d clumps from %d variants over %d emitters"
          % (int(count * 2.0), len(protos), len(emitters)))
    return emitters[0]


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
        # 0.9 m per tile, not 1.6. The grass clumps only ever cover the ground
        # edge-on, so most of what a camera at eye height sees of the lawn is
        # the texture between them - and at 1.6 m the blades in it were three
        # times too big to read as blades, which left the gaps between clumps
        # as smooth painted colour. The height map goes into the normal for the
        # same reason: turf seen at a grazing angle is all micro-relief.
        _lawn_macro(ground_material("Lawn", "leafy_grass", ground_res,
                                    uv_scale=GROUND_RADIUS * 2.0 / 0.9,
                                    variation=0.22, bump=0.7)))

    # The esplanade is the surface the player stands on, so it gets the highest
    # texture resolution and the tightest tiling in the scene.
    esplanade = _disc("Esplanade", ESPLANADE_RADIUS, z=0.0)
    _project_uv_metres(esplanade)
    esplanade.data.materials.append(
        # gravel_floor_04 is compacted sand with essentially no stones in it,
        # so from standing height it renders as a flat cream carpet. This one
        # has visible grain, which is what the eye looks for underfoot.
        _surface_wear(ground_material(
                        # Same gravel as the walk and the allees, at a coarser
                        # tile. flower_scattered_gravel was used here for its
                        # grain, but it is a warm pink-grey and no amount of
                        # tinting stopped the esplanade reading as a mauve band
                        # across the base of the tower - a different material
                        # from the paths it runs into, in a place where the real
                        # ground is all one surface.
                        # Tile 2.2 m, not 1.35. gravel_floor_02 is fine pea
                        # gravel: at a metre-ish tile its stones are 5 mm, which
                        # is under a pixel from anywhere a camera stands, so the
                        # surface filtered to a flat sheet. Stretched to 2.2 m
                        # the stones read at 2-3 cm, which is the size of the
                        # gravel the esplanade is actually laid with.
                        "Sand", "gravel_floor_02", ground_res,
                        uv_scale=1.0 / 2.2, variation=0.24,
                        bump=1.4,
                        # The texture is a warm pink-grey that reads mauve over
                        # Pale, and warm. The Champ de Mars is not laid with
                        # grey gravel but with sable stabilise - a compacted
                        # pale limestone sand - and in photographs of the
                        # allees it is the brightest thing in the frame after
                        # the sky, running about 1.7x the sunlit lawn. This
                        # scene had the ratio at 1.25, which is why the paved
                        # band round the tower kept reading as asphalt.
                        # gravel_floor_02 is a grey-green pea gravel, so the
                        # tint carries the warmth as well as the level.
                        # Measured 2026-09-11 against the Alley photograph:
                        # the walk rendered at luminance 159 against a sunlit
                        # lawn of about 120, a ratio of 1.33 where the
                        # photograph gives 1.7 - and at saturation 0.13 where
                        # the real sable stabilise measures under 0.05. So it
                        # was both too dark and too warm, which together are
                        # exactly what makes a pale sand read as grey asphalt.
                        tint=(1.00, 0.97, 0.90, 1.0)), scale=0.9))

    # The allees that run away from the tower down the Champ de Mars.
    #
    # These used to be a unit plane scaled to 26 x 300 m, which left the UVs
    # running 0..1 over both axes: at 150 repeats that is an 8 cm tile across
    # the path and a 2 m tile along it. Stretched that hard the gravel resolved
    # into a flat streak and the allees rendered as blank white slabs, clearly
    # visible from the hero camera. _rect lays the UVs out in metres so the
    # tile stays square whatever the path's proportions.
    gravel = _surface_wear(
        ground_material("Gravel", "gravel_floor_02", ground_res,
                        uv_scale=1.0 / 1.8, variation=0.25, bump=1.4,
                        # Same surface as the esplanade - the allees and the
                        # ground round the piers are one material in the real
                        # park - so the same correction applies.
                        tint=(1.00, 0.97, 0.90, 1.0)))
    # One walk under each row of pruned planes and one between the pair, which
    # is how the real allees are laid out - the trees stand in the gravel, not
    # beside it. They also have to line up with parkland.ROW_X or the tree
    # walls end up growing out of the lawn.
    inner, outer = parkland.ROW_X
    for offset in (inner - 5.0, (inner + outer) * 0.5, outer + 5.0):
        for sx in (1.0, -1.0):
            path = _rect("Allee", 8.5, 340.0, (sx * offset, -265.0, 0.01))
            path.data.materials.append(gravel)
            path = _rect("Allee", 8.5, 160.0, (sx * offset, 180.0, 0.01))
            path.data.materials.append(gravel)
    build_lawn_gravel(gravel)
    return esplanade


def build_lawn_gravel(gravel, z=0.012):
    """The pale apron beside the lawn and the walks that cut across it.

    This is the single change that stopped the park reading as a field. From
    any distance the eye reads a formal garden by its edges - a green rectangle
    with a hard pale border and a walk cutting across it every hundred metres -
    and a continuous carpet of grass has none of them, however well the grass
    itself is shaded.
    """
    made = 0
    for start, end in ((LAWN_FROM, LAWN_TO),
                       (LAWN_FROM_NORTH, LAWN_TO_NORTH)):
        length = abs(end - start)
        centre = (start + end) * 0.5
        width = LAWN_APRON - LAWN_HALF_WIDTH
        for sx in (1.0, -1.0):
            strip = _rect("LawnApron", width, length,
                          (sx * (LAWN_HALF_WIDTH + width * 0.5), centre, z))
            strip.data.materials.append(gravel)
            made += 1
        span = abs(end - start)
        count = max(int(round(span / CROSS_WALK_SPACING)), 1)
        sign = -1.0 if end < start else 1.0
        for i in range(count + 1):
            y = start + sign * span * i / count
            walk = _rect("LawnCross", LAWN_APRON * 2.0, CROSS_WALK_WIDTH,
                         (0.0, y, z))
            walk.data.materials.append(gravel)
            made += 1
    print("[lawn] %d gravel panels framing the grass" % made)
    return made


def build_haze(radius=2600.0, density=2.0e-5, colour=(0.70, 0.79, 0.95, 1.0),
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

    Density is kept low. Thicker fog is physically fine but the tower then
    casts a hard-edged shadow shaft through it and across the sky, which looks
    like a rendering seam rather than weather.

    The radius is another matter. It was 700 m, which is the edge of the lawn
    disc - so the Palais de Chaillot at 700 m, Tour Montparnasse at 2.3 km and
    the hinterland ground at 4.2 km all sat outside the fog entirely and came
    back as crisp as the gravel underfoot. That was hidden while the camera
    clip range was cutting everything past 1 km off anyway; with the range
    fixed the far skyline needs the same treatment as the near one. 2.6 km
    covers everything the eye reads as distance, with the density dropped to
    keep the total depth about where it was.
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


def build_sky_dome(radius=3000.0, segments=96, rings=48, env=None,
                   sky_value=0.97, sky_saturation=1.15):
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

    # Deepen the blue - on the dome only, which is to say on what the camera
    # sees and not on what lights the scene.
    #
    # Measured against Commons photographs of this view, the HDRI's zenith
    # renders about 1.5x brighter than the sunlit lawn; in the photographs the
    # two are about equal. Every photograph of the Champ de Mars that looks
    # like the postcard was shot through a polariser, and this is the same
    # move: it darkens and saturates the sky and touches nothing else.
    #
    # Saturation is the number that matters - measured high in the sky, the
    # milder of the two reference photographs is at 0.49 and the clearly
    # polarised one at 0.62, against 0.34 here untouched - but it cannot be
    # pushed far, because this is a multiplier and the sky it multiplies is not
    # uniform. A real sky loses saturation towards the horizon; scaling by 1.45
    # or more amplified the little that was left down there into a band of cyan
    # more saturated than the zenith, which is the gradient upside down.
    #
    # Measure away from the sun. The first attempts at this were read off the
    # left edge of the vista frame, which is exactly where the sun's glow is:
    # every setting came back looking clipped and flat, and two settings a
    # factor of two apart measured identical.
    #
    # It is safe to bake into the splat because it is a fixed function of
    # direction. The dome sits at 3 km, so the same sky direction is the same
    # colour from every training camera - unlike the lens effects in section 8,
    # which change with where the camera is pointing and would train as
    # view-dependent colour.
    tone = nt.nodes.new("ShaderNodeHueSaturation")
    tone.inputs["Saturation"].default_value = sky_saturation
    tone.inputs["Value"].default_value = sky_value
    nt.links.new(sky.outputs["Color"], tone.inputs["Color"])
    nt.links.new(tone.outputs["Color"], emission.inputs["Color"])
    nt.links.new(emission.outputs["Emission"], out.inputs["Surface"])
    dome.data.materials.append(mat)
    return dome


def _hoop(name, span=0.90, height=0.42, thickness=0.018, arc=10, sides=6):
    """One low wire hoop, the kind that edges every lawn on the Champ de Mars.

    Built as a swept tube rather than a curve so it needs no modifier and can
    be linked-duplicated a few hundred times for the cost of one mesh.
    """
    verts = []
    faces = []
    for i in range(arc + 1):
        t = i / arc
        angle = math.pi * t
        cx = -span * 0.5 * math.cos(angle)
        cz = height * math.sin(angle)
        # Ring of vertices around the wire's centre line, in the plane across
        # the sweep: the hoop lies in x-z, so the ring spans y and the local
        # normal direction.
        nx = math.sin(angle)
        nz = -math.cos(angle)
        for j in range(sides):
            a = 2.0 * math.pi * j / sides
            r = thickness
            verts.append((cx + nx * math.cos(a) * r,
                          math.sin(a) * r,
                          cz + nz * math.cos(a) * r))
    for i in range(arc):
        for j in range(sides):
            a = i * sides + j
            b = i * sides + (j + 1) % sides
            faces.append((a, b, b + sides, a + sides))
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def build_lawn_edge(texres, rng, walk_width=WALK_WIDTH, fence_pad=FENCE_PAD):
    """Gravel walkway and hoop fence where the esplanade meets the lawn.

    From the hero camera that boundary is the strongest horizontal in the lower
    half of the frame, and it was a single hard line between two flat colours -
    the one place the eye can check whether the ground is a real surface or a
    painted disc. The real park puts a gravel walk and a knee-high wire fence
    there, which breaks the line into three and, more usefully, drops a row of
    small, sharply lit objects at a known scale into the middle distance.
    """
    walk = _ring("LawnWalk", ESPLANADE_RADIUS, ESPLANADE_RADIUS + walk_width,
                 z=0.006)
    walk.data.materials.append(
        _surface_wear(
            ground_material("Walk", "gravel_floor_02", texres,
                            uv_scale=1.0 / 1.8, variation=0.22, bump=1.4,
                            # Slightly paler than the esplanade it borders:
                            # the walk is swept, the open ground is not.
                            tint=(0.86, 0.80, 0.66, 1.0))))
    # _ring lays its UVs out implicitly, so give the walk the same metre-based
    # treatment the allees get rather than letting the texture stretch round.
    _project_uv_metres(walk)

    radius = ESPLANADE_RADIUS + walk_width + fence_pad
    proto = _hoop("HoopProto")
    proto.hide_render = True
    proto.location = (0.0, 0.0, -500.0)
    paint = bpy.data.materials.new("FenceGreen")
    paint.use_nodes = True
    bsdf = next(n for n in paint.node_tree.nodes
                if n.bl_idname == "ShaderNodeBsdfPrincipled")
    bsdf.inputs["Base Color"].default_value = (0.022, 0.038, 0.026, 1.0)
    bsdf.inputs["Roughness"].default_value = 0.45
    bsdf.inputs["Metallic"].default_value = 0.0
    bsdf.inputs["Specular IOR Level"].default_value = 0.35
    proto.data.materials.append(paint)

    step = 0.86
    count = int(2.0 * math.pi * radius / step)
    placements = []
    for i in range(count):
        a = 2.0 * math.pi * i / count
        placements.append((math.cos(a) * radius, math.sin(a) * radius,
                           rng.uniform(0.94, 1.03), a + math.pi * 0.5))
    _scatter([proto], placements, rng, "Hoop", z=LAWN_Z + 0.01)
    print("[edge] %d hoops at r=%.1f m" % (count, radius))
    return walk


def _project_uv_metres(obj):
    """Rewrite an object's UVs as its own x/y in metres.

    Meshes built by _ring have no UV layer at all, so a tiled material lands on
    the default (0, 0) and paints the whole surface one pixel.
    """
    mesh = obj.data
    if not mesh.uv_layers:
        mesh.uv_layers.new(name="UVMap")
    uv = mesh.uv_layers[0].data
    for poly in mesh.polygons:
        for li in poly.loop_indices:
            co = mesh.vertices[mesh.loops[li].vertex_index].co
            uv[li].uv = (co.x, co.y)
    mesh.update()


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
        # A scale may be a single number or a per-axis triple. The formal rows
        # use the triple: squashing the canopy across the row and stretching it
        # up is what turns a line of round trees into the clipped green wall the
        # Champ de Mars actually has.
        obj.scale = scale if hasattr(scale, "__len__") else (scale,) * 3
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
               ("island_tree_03", 3)]
    protos = []
    tall = []
    for asset_id, weight in weights:
        objs = assets.append_model(asset_id, texres)
        # Measured against File:Paris_75007_Champ-de-Mars_Alley_20170526.jpg,
        # sampling every foliage pixel in both frames (g > r+8 and g > b+8)
        # rather than a hand-placed patch:
        #
        #     reference canopy   hue 89   sat 0.60   value 0.38
        #     this scene         hue 73   sat 0.42   value 0.47
        #
        # Wrong in all three at once, and in the direction that reads as
        # "ornamental nursery stock" rather than "Parisian plane": yellow, pale
        # and thin. The old value of 1.28 was brightening foliage that was
        # already too bright - a pruned plane canopy is dense enough to shade
        # itself, so it photographs DARKER than an isolated tree, not lighter.
        # 0.5 is no hue change and the full range is a 360 degree rotation, so
        # +16 degrees of green is 0.5 + 16/360.
        # Measured against File:Paris_75007_Champ-de-Mars_Alley_20170526.jpg,
        # sampling every foliage pixel in both frames (g > r+8 and g > b+8)
        # rather than a hand-placed patch. Target: hue 89, saturation 0.60,
        # value 0.38.
        #
        # Value and hue respond and are now on target. Saturation does not:
        # 1.45 and 2.10 measured 0.475 and 0.477, a 45 per cent change in the
        # knob for half a per cent in the render. By this project's own rule
        # that is a chain that has run out of room, not a knob that needs
        # turning further - the leaf group does its own shading below this
        # socket, and what is left of the gap is blue skylight filling the
        # canopy rather than albedo. So this sits at 1.45, where the knob still
        # does something, and the remaining 0.12 of saturation is a note in the
        # plan rather than a bigger number here.
        #
        # The hue knob is geared about 0.64: +42 degrees moved the rendered
        # canopy 27, which overshot to 100. +24 lands on 89.
        assets.retint(objs, value=0.94, saturation=1.45,
                      hue=0.5 + 24.0 / 360.0)
        # Plane bark is the giveaway that a row of trees is Parisian: pale
        # grey-cream, flaking to olive, and light enough that a row of trunks
        # reads as a colonnade. These prototypes are jacaranda and friends,
        # whose bark is dark brown - down the park that came out as a row of
        # black verticals under the canopy.
        for part in ("trunk", "branches"):
            assets.retint(objs, value=2.3, saturation=0.30, match=part)
        for obj in objs:
            obj.location = (0.0, 0.0, -500.0)   # park the originals off scene
        protos.extend(objs * weight)
        if asset_id == "jacaranda_tree":
            tall.extend(objs)
    if not protos:
        return []
    if not tall:
        tall = protos

    placements = []
    row_placements = []
    if rows:
        # Double rows of plane trees flanking the allees, as on the Champ de
        # Mars. They start beyond CAMERA_RADIUS so no tree ever ends up in a
        # training camera's lap.
        for sign_x in (1.0, -1.0):
            # parkland.build_tree_walls now owns the rows that frame the lawn;
            # these are the looser planting behind them.
            for offset in (104.0, 126.0):
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
        r = rng.uniform(115.0, 268.0)
        placements.append((math.cos(a) * r, math.sin(a) * r,
                           rng.uniform(0.8, 1.3), rng.uniform(0.0, math.tau)))
    for _ in range(260):
        a = rng.uniform(0.0, math.tau)
        r = rng.uniform(275.0, 620.0)
        placements.append((math.cos(a) * r, math.sin(a) * r,
                           rng.uniform(0.9, 1.5), rng.uniform(0.0, math.tau)))
    placements = [p for p in placements if clear_of_cameras(p[0], p[1], 12.0)]

    # The formal rows that wall in the lawn. Appended after the keep-out filter
    # because they are placed deliberately, not scattered: the camera grid was
    # laid out to fit between them (render_rig.camera_poses), so they are clear
    # by construction and the radial test would only throw them away.
    for x in parkland.ROW_X:
        for sx in (1.0, -1.0):
            for y0, y1 in ((-parkland.ROW_START, -parkland.ROW_END),
                           (parkland.ROW_START, parkland.ROW_START + 190.0)):
                steps = max(int(abs(y1 - y0) / parkland.ROW_SPACING), 1)
                for i in range(steps + 1):
                    y = y0 + (y1 - y0) * i / steps
                    # Deliberately little variation in height. These are pruned
                    # every year to one line, and in the photographs the top of
                    # the row is nearly level - scattering the scale the way the
                    # loose planting does gave a ragged skyline that read as
                    # woodland. Squashed across the row and left full along it,
                    # so neighbours 7.4 m apart still close into a wall.
                    # Height matters more than it looks. Cut to 0.62 the rows
                    # were level but only 12 m tall, and from down the park they
                    # read as a hedge rather than as the wall of planes that
                    # frames the lawn in every photograph - those run about 18 m.
                    grow = rng.uniform(0.86, 0.94)
                    row_placements.append(
                        (sx * x + rng.uniform(-0.7, 0.7),
                         y + rng.uniform(-0.7, 0.7),
                         (grow * 1.16, grow * 1.16, grow * 1.55),
                         rng.uniform(0.0, math.tau)))
    # Scattered onto the lawn, which sits at LAWN_Z; bedding everything a few
    # centimetres below it hides the seam where a trunk or a tuft meets the
    # ground instead of leaving it visibly perched on top.
    # Two scatters, not one, and this is the whole point of the split.
    #
    # _scatter picks a prototype at random per placement, and the prototype
    # list is three species: a 13 m jacaranda and two that are under 4 m. For
    # the loose planting that is what is wanted. For the formal rows it meant
    # every other "plane tree" in the wall was a shrub - measured across the
    # built scene, the median tree top stood at 6.5 m against the 20-25 m of
    # the real rows, which is why the hero frame had a hedge where it should
    # have had a wall of trees reaching a third of the way up the legs. No
    # amount of colour work fixes a tree line that is a third of its height.
    made = _scatter(protos, placements, rng, "Tree", z=LAWN_Z - 0.05)
    made += _scatter(tall, row_placements, rng, "Tree", z=LAWN_Z - 0.05)
    return made


def build_shrubs(texres, rng):
    """Low planting just outside the camera ring, under the tree line."""
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
        # These used to start at the esplanade edge, which scattered bushes
        # across the open lawn the ground cameras stand on - both wrong for the
        # Champ de Mars, whose central lawn is bare grass, and a hole punched in
        # the splat wherever one landed in front of a training camera.
        r = rng.uniform(CAMERA_RADIUS + 8.0, 205.0)
        placements.append((math.cos(a) * r, math.sin(a) * r,
                           rng.uniform(0.8, 1.6), rng.uniform(0.0, math.tau)))
    placements = [p for p in placements if clear_of_cameras(p[0], p[1], 8.0)]
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

    # Benches belong along the allees facing the lawn, not scattered on the
    # esplanade - that ring is where parkland.build_pavilions puts the ticket
    # kiosks, and the two were landing on top of each other.
    # Along the gravel apron beside the lawn, facing the grass, at the spacing
    # the real allees use - close enough that a bench is always in frame from
    # anywhere on the lawn, which is a large part of why the reference
    # photographs never look empty even with nobody in them.
    seats = []
    for sx in (1.0, -1.0):
        for i in range(34):
            y = -92.0 - i * 10.0 + rng.uniform(-1.5, 1.5)
            seats.append((sx * (LAWN_HALF_WIDTH + 5.0), y, 1.0,
                          math.pi if sx > 0.0 else 0.0))
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
        build_lawn_edge(texres, rng)
        build_furniture(texres, rng)
        parkland.build_perimeter(rng=rng)
        parkland.build_pavilions(rng)
        parkland.build_floodmasts()
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
