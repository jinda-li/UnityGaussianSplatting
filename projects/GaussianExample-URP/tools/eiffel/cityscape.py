"""Paris around the tower: the background a real photograph would contain.

From the Champ de Mars the tower is never seen against empty ground. Looking
one way there is the Seine, the Pont d'Iena and the curved wings of the Palais
de Chaillot on the Trocadero hill; the other way the lawn runs a kilometre to
the Ecole Militaire, with Tour Montparnasse behind it. Both sides of the park
are walled in by Haussmann apartment blocks - seven storeys of cream limestone
under grey zinc mansards, in an unbroken line.

None of that needs to be modelled in detail: at 250 m and beyond it is massing,
a window rhythm and a roof colour. But without it the scene reads as a tower
standing in a field, which is the one thing the real place never looks like.

Everything here is generated, so there is nothing to license.
"""

from __future__ import annotations

import math
import random

import bpy

# The lawn runs along -Y towards the Ecole Militaire; the Seine and the
# Trocadero are on +Y.
SEINE_Y = 190.0
CHAILLOT_Y = 700.0
MILITAIRE_Y = -950.0


def _box(name, sx, sy, sz, location, rotation=0.0):
    hx, hy = sx * 0.5, sy * 0.5
    verts = [(-hx, -hy, 0.0), (hx, -hy, 0.0), (hx, hy, 0.0), (-hx, hy, 0.0),
             (-hx, -hy, sz), (hx, -hy, sz), (hx, hy, sz), (-hx, hy, sz)]
    faces = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4),
             (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    obj.location = location
    obj.rotation_euler = (0.0, 0.0, rotation)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def _simple_material(name, colour, roughness=0.7, specular=0.2):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    bsdf.inputs["Base Color"].default_value = colour
    bsdf.inputs["Roughness"].default_value = roughness
    bsdf.inputs["Specular IOR Level"].default_value = specular
    bsdf.inputs["Metallic"].default_value = 0.0
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
    return mat


def haussmann_facade(name="Haussmann", floor=3.15, bay=2.75,
                     win_w=1.20, win_h=2.15,
                     stone=(0.150, 0.128, 0.092, 1.0),
                     window=(0.014, 0.017, 0.023, 1.0)):
    """Cream limestone with tall French windows.

    The obvious approach - a Brick node, bricks as windows and mortar as wall -
    gets the rhythm but not the proportion: the mortar band is the same width
    all round, so the openings come out square and the result reads as a modern
    office block. A Haussmann window is roughly 1.2 m wide and 2.15 m tall in a
    3.15 m storey, so the horizontal and vertical masks are built separately
    and multiplied.

    The masks run in (x, z) and (y, z); which pair a face uses is chosen from
    its normal, because a facade is vertical and any single mapping leaves the
    walls on one axis with no variation across them at all.
    """
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])

    coord = nt.nodes.new("ShaderNodeTexCoord")
    sep = nt.nodes.new("ShaderNodeSeparateXYZ")
    nt.links.new(coord.outputs["Object"], sep.inputs["Vector"])

    def math(op, a_socket=None, a_val=None, b_val=None):
        node = nt.nodes.new("ShaderNodeMath")
        node.operation = op
        if a_socket is not None:
            nt.links.new(a_socket, node.inputs[0])
        elif a_val is not None:
            node.inputs[0].default_value = a_val
        if b_val is not None:
            node.inputs[1].default_value = b_val
        return node.outputs["Value"]

    def band(socket, period, open_size, phase=0.0):
        """1 inside an opening of `open_size`, 0 on the wall between them."""
        shifted = math("ADD", a_socket=socket, b_val=phase)
        wrapped = nt.nodes.new("ShaderNodeMath")
        wrapped.operation = "WRAP"
        wrapped.inputs[1].default_value = period
        wrapped.inputs[2].default_value = 0.0
        nt.links.new(shifted, wrapped.inputs[0])
        centred = math("SUBTRACT", a_socket=wrapped.outputs["Value"],
                       b_val=period * 0.5)
        dist = math("ABSOLUTE", a_socket=centred)
        return math("LESS_THAN", a_socket=dist, b_val=open_size * 0.5)

    v_band = band(sep.outputs["Z"], floor, win_h, phase=floor * 0.5)

    def wall_mask(u_socket):
        h_band = band(u_socket, bay, win_w)
        return math("MULTIPLY", a_socket=h_band, b_val=1.0), h_band

    mask_x, hx = wall_mask(sep.outputs["X"])
    mask_y, hy = wall_mask(sep.outputs["Y"])
    win_x = math("MULTIPLY", a_socket=hx, b_val=1.0)
    win_x = nt.nodes.new("ShaderNodeMath")
    win_x.operation = "MULTIPLY"
    nt.links.new(hx, win_x.inputs[0])
    nt.links.new(v_band, win_x.inputs[1])
    win_y = nt.nodes.new("ShaderNodeMath")
    win_y.operation = "MULTIPLY"
    nt.links.new(hy, win_y.inputs[0])
    nt.links.new(v_band, win_y.inputs[1])

    geo = nt.nodes.new("ShaderNodeNewGeometry")
    nsep = nt.nodes.new("ShaderNodeSeparateXYZ")
    nt.links.new(geo.outputs["Normal"], nsep.inputs["Vector"])
    ax = math("ABSOLUTE", a_socket=nsep.outputs["X"])
    face = math("GREATER_THAN", a_socket=ax, b_val=0.5)

    pick = nt.nodes.new("ShaderNodeMixRGB")
    nt.links.new(face, pick.inputs["Fac"])
    nt.links.new(win_x.outputs["Value"], pick.inputs["Color1"])
    nt.links.new(win_y.outputs["Value"], pick.inputs["Color2"])

    # Each block gets its own tone, or a kilometre of them is one flat wall.
    info = nt.nodes.new("ShaderNodeObjectInfo")
    vary = nt.nodes.new("ShaderNodeMapRange")
    vary.inputs[3].default_value = 0.74
    vary.inputs[4].default_value = 1.30
    nt.links.new(info.outputs["Random"], vary.inputs["Value"])
    tone = nt.nodes.new("ShaderNodeCombineColor")
    for i in range(3):
        nt.links.new(vary.outputs["Result"], tone.inputs[i])
    wall = nt.nodes.new("ShaderNodeMixRGB")
    wall.blend_type = "MULTIPLY"
    wall.inputs["Fac"].default_value = 1.0
    wall.inputs["Color1"].default_value = stone
    nt.links.new(tone.outputs["Color"], wall.inputs["Color2"])

    base = nt.nodes.new("ShaderNodeMixRGB")
    nt.links.new(pick.outputs["Color"], base.inputs["Fac"])
    nt.links.new(wall.outputs["Color"], base.inputs["Color1"])
    base.inputs["Color2"].default_value = window
    nt.links.new(base.outputs["Color"], bsdf.inputs["Base Color"])

    rough = nt.nodes.new("ShaderNodeMixRGB")
    nt.links.new(pick.outputs["Color"], rough.inputs["Fac"])
    rough.inputs["Color1"].default_value = (0.72, 0.72, 0.72, 1.0)
    rough.inputs["Color2"].default_value = (0.15, 0.15, 0.15, 1.0)
    nt.links.new(rough.outputs["Color"], bsdf.inputs["Roughness"])
    bsdf.inputs["Specular IOR Level"].default_value = 0.24
    return mat


def _frustum(name, sx, sy, sz, taper, location, rotation=0.0):
    """A box whose top face is inset - the mansard roof profile."""
    hx, hy = sx * 0.5, sy * 0.5
    tx, ty = hx * taper, hy * taper
    verts = [(-hx, -hy, 0.0), (hx, -hy, 0.0), (hx, hy, 0.0), (-hx, hy, 0.0),
             (-tx, -ty, sz), (tx, -ty, sz), (tx, ty, sz), (-tx, ty, sz)]
    faces = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4),
             (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    obj.location = location
    obj.rotation_euler = (0.0, 0.0, rotation)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def _block(rng, x, y, width, depth, storeys, facade, roof_mat, rotation=0.0):
    """One apartment block: body plus a set-back mansard roof."""
    height = storeys * 3.15 + 1.4
    # Sunk a little so no block ever shows daylight under its own footprint.
    body = _box("Block", width, depth, height, (x, y, -0.6), rotation)
    body.data.materials.append(facade)
    # Cornice: the projecting stone band that caps every Haussmann facade and
    # separates it from the roof. Without it the wall runs straight into the
    # mansard and the whole thing reads as a modern slab.
    cornice = _box("Cornice", width + 1.3, depth + 1.3, 1.1,
                   (x, y, height - 1.7), rotation)
    cornice.data.materials.append(facade)
    # Mansard: steeply sloped, not a straight parapet.
    attic = _frustum("Attic", width * 0.99, depth * 0.99, 4.2, 0.72,
                     (x, y, height - 0.6), rotation)
    attic.data.materials.append(roof_mat)
    if rng.random() < 0.7:
        cx = x + rng.uniform(-width * 0.25, width * 0.25)
        stack = _box("Chimney", 1.1, 2.6, 2.2, (cx, y, height + 2.8), rotation)
        stack.data.materials.append(roof_mat)
    return body


def build_city(rng=None, near=235.0, far=620.0):
    """Apartment blocks lining both sides of the park, plus the landmarks."""
    rng = rng or random.Random(11)
    facade = haussmann_facade()
    roof = _simple_material("Zinc", (0.055, 0.058, 0.062, 1.0), roughness=0.45,
                            specular=0.35)
    # The landmarks used to be flat untextured stone. At 600 m, lit and hazed,
    # a featureless box does not read as distant - it reads as a blank card
    # standing behind the trees, and the Chaillot wings are visible straight
    # through the tower's arch from the hero camera. The same facade generator
    # the apartment blocks use gives them a window rhythm; the palace's storeys
    # are half again as tall as an apartment's, and its stone is paler.
    stone = haussmann_facade("PalePierre", floor=5.4, bay=4.4,
                             win_w=1.9, win_h=3.6,
                             stone=(0.185, 0.170, 0.140, 1.0))

    made = 0
    for sign in (1.0, -1.0):
        for row, offset in enumerate((near, near + 95.0, near + 210.0,
                                      near + 340.0, far)):
            y = -880.0
            while y < 470.0:
                depth = rng.uniform(22.0, 34.0)
                width = rng.uniform(15.0, 27.0)
                # Leave the river open where the Seine crosses.
                if abs(y - SEINE_Y) < 55.0 and offset < near + 150.0:
                    y += width + 14.0
                    continue
                _block(rng, sign * (offset + rng.uniform(-14.0, 14.0)), y,
                       depth, width, rng.randint(5, 9), facade, roof)
                made += 1
                y += width + rng.uniform(0.5, 3.0)

    # Ecole Militaire closing the far end of the lawn.
    body = _box("EcoleMilitaire", 190.0, 70.0, 26.0, (0.0, MILITAIRE_Y, 0.0))
    body.data.materials.append(stone)
    wing = _box("EcoleWing", 80.0, 34.0, 21.0, (0.0, MILITAIRE_Y + 48.0, 0.0))
    wing.data.materials.append(stone)
    dome = _box("EcoleDome", 26.0, 26.0, 22.0, (0.0, MILITAIRE_Y, 26.0))
    dome.data.materials.append(roof)

    # Palais de Chaillot: two wings curving around the Trocadero esplanade.
    for sign in (1.0, -1.0):
        for i in range(7):
            t = i / 6.0
            angle = math.radians(28.0 + t * 54.0) * sign
            r = 240.0
            x = math.sin(angle) * r
            y = CHAILLOT_Y - math.cos(angle) * r * 0.42
            # The hill below tops out at z = 0, so the wings stand on 0 too.
            # Placing them at 26 left the whole palace hanging in the air.
            seg = _box("Chaillot", 46.0, 26.0, 24.0, (x, y, 0.0), -angle * 0.8)
            seg.data.materials.append(stone)
    hill = _box("TrocaderoHill", 620.0, 300.0, 26.4,
                (0.0, CHAILLOT_Y - 40.0, -26.4))
    hill.data.materials.append(
        _simple_material("Hill", (0.13, 0.16, 0.09, 1.0)))

    # Tour Montparnasse, the one high-rise on this skyline.
    mp = _box("Montparnasse", 60.0, 40.0, 200.0, (330.0, -2300.0, 0.0))
    mp.data.materials.append(
        _simple_material("MPGlass", (0.030, 0.032, 0.038, 1.0),
                         roughness=0.25, specular=0.45))

    print("[city] %d apartment blocks" % made)
    return made


def build_hinterland(radius=4200.0, z=-0.5):
    """Ground for everything past the park.

    The lawn disc only reaches 700 m, but the apartment blocks run to 880 m and
    the Ecole Militaire and Tour Montparnasse are further still, so all of them
    were standing on nothing and read as cut-outs hanging in the haze.
    """
    plane = _box("Hinterland", radius * 2.0, radius * 2.0, 0.4,
                 (0.0, -400.0, z - 0.4))
    plane.data.materials.append(
        _simple_material("Hinterland", (0.055, 0.055, 0.050, 1.0),
                         roughness=0.85, specular=0.1))
    return plane


def build_seine(width=190.0, length=2400.0, y=SEINE_Y):
    """The river, and the bridge on the tower's axis."""
    water = _box("Seine", length, width, 0.6, (0.0, y, -0.9), math.pi / 2.0)
    mat = bpy.data.materials.new("Seine")
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    bsdf.inputs["Base Color"].default_value = (0.045, 0.060, 0.055, 1.0)
    bsdf.inputs["Roughness"].default_value = 0.12
    bsdf.inputs["Specular IOR Level"].default_value = 0.5
    # Ripple, or the river renders as a mirror and reads as glass.
    bump = nt.nodes.new("ShaderNodeBump")
    bump.inputs["Strength"].default_value = 0.35
    noise = nt.nodes.new("ShaderNodeTexNoise")
    noise.inputs["Scale"].default_value = 240.0
    noise.inputs["Detail"].default_value = 6.0
    nt.links.new(noise.outputs["Fac"], bump.inputs["Height"])
    nt.links.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
    water.data.materials.append(mat)

    deck = _box("PontIena", 35.0, width + 24.0, 2.0, (0.0, y, 0.4))
    deck.data.materials.append(
        _simple_material("BridgeStone", (0.36, 0.34, 0.30, 1.0)))
    return water
