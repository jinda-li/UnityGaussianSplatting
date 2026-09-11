"""The formal landscape of the Champ de Mars, and the clutter under the tower.

Two things separate a photograph taken on the lawn from the render that used to
come out of scene.py, and neither of them is the tower.

The first is that this is a French formal park, not a meadow with trees on it.
The lawn is a long rectangle walled on both sides by rows of plane trees, so
from anywhere on the grass the two walls run away from you in hard perspective
and meet at the tower. Trees scattered at random read as parkland anywhere in
the world; rows read as Paris, and they give the frame the converging lines it
was missing. The row geometry lives here (ROW_X and friends); the planting
itself is done by scene.build_trees, which already holds the tree prototypes.

The rows were first built here out of procedural canopies - superellipsoids
lumped with noise, to imitate the curtain pruning the real planes get. The
shape was right and the result was useless: at the 100-250 m the hero frame
sees them at, a smooth shaded blob reads as a green sausage, because what the
eye identifies a tree by at that distance is the broken edge of the foliage,
not its outline. Real leaf geometry in a straight line beats a correct
silhouette with no texture.

The second is that the ground under the tower is not empty. It has a security
perimeter, ticket pavilions at the piers, floodlight masts standing inside the
arch, litter bins and signs. In the render that whole band was bare gravel, and
an empty plaza at the foot of a monument is a stronger CG cue than anything
about the monument itself.

Everything here is generated, so it carries no third party licence.
"""

from __future__ import annotations

import math

import bpy


# The lawn corridor.
#
# These rows have to run *past* the hero camera on both sides, from close to the
# tower's feet all the way down the park, or they are not walls and the frame
# gets no converging lines out of them. The first attempt started them at
# y = -105 m to clear a circular camera ring, which put every tree behind the
# hero camera: perfectly placed, completely invisible.
#
# The circle was the thing that had to give. Ground cameras now cover the lawn
# on a rectangular grid inside |x| < 50 (see render_rig.camera_poses), so the
# rows can sit where the real ones do - about 60 m off the axis - without ever
# standing in front of one.
#
# ROW_START is where the rows begin, measured along the park from the tower's
# centre. It has to be small enough that the walls are already running when
# they pass the hero camera at y = -116 - at 86 m they only existed for the
# 30 m between the camera and the esplanade and the frame got nothing out of
# them - and large enough that a tree does not end up standing on the gravel
# or inside the lawn's hoop fence at r = 79 m.
ROW_X = (64.0, 78.0)
ROW_START = 52.0
ROW_END = 440.0
ROW_SPACING = 7.4


def _material(name, colour, roughness=0.6, specular=0.15, variation=0.0,
              scale=0.6):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    bsdf = next(n for n in nt.nodes if n.bl_idname == "ShaderNodeBsdfPrincipled")
    bsdf.inputs["Roughness"].default_value = roughness
    bsdf.inputs["Metallic"].default_value = 0.0
    bsdf.inputs["Specular IOR Level"].default_value = specular
    if not variation:
        bsdf.inputs["Base Color"].default_value = colour
        return mat
    # Foliage read as a solid green brick without this: a pruned canopy is one
    # shape, but it is thousands of leaves at a dozen different angles to the
    # sun, and the eye checks for that mottling before it checks the silhouette.
    coord = nt.nodes.new("ShaderNodeTexCoord")
    mapping = nt.nodes.new("ShaderNodeMapping")
    mapping.inputs["Scale"].default_value = (scale,) * 3
    nt.links.new(coord.outputs["Object"], mapping.inputs["Vector"])
    tex = nt.nodes.new("ShaderNodeTexNoise")
    tex.inputs["Detail"].default_value = 8.0
    tex.inputs["Roughness"].default_value = 0.7
    nt.links.new(mapping.outputs["Vector"], tex.inputs["Vector"])
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].position = 0.34
    ramp.color_ramp.elements[0].color = tuple(c * (1.0 - variation)
                                              for c in colour[:3]) + (1.0,)
    ramp.color_ramp.elements[1].position = 0.68
    ramp.color_ramp.elements[1].color = tuple(
        min(c * (1.0 + variation * 1.6), 1.0) for c in colour[:3]) + (1.0,)
    nt.links.new(tex.outputs["Fac"], ramp.inputs["Fac"])
    nt.links.new(ramp.outputs["Color"], bsdf.inputs["Base Color"])
    return mat


def _link(obj):
    bpy.context.scene.collection.objects.link(obj)
    return obj


def _mesh_object(name, verts, faces, shade_smooth=False):
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    mesh.update()
    if shade_smooth:
        for poly in mesh.polygons:
            poly.use_smooth = True
    return _link(bpy.data.objects.new(name, mesh))


def _box(name, sx, sy, sz, location, rotation=0.0):
    hx, hy = sx * 0.5, sy * 0.5
    verts = [(-hx, -hy, 0.0), (hx, -hy, 0.0), (hx, hy, 0.0), (-hx, hy, 0.0),
             (-hx, -hy, sz), (hx, -hy, sz), (hx, hy, sz), (-hx, hy, sz)]
    faces = [(0, 1, 2, 3), (7, 6, 5, 4), (0, 4, 5, 1),
             (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)]
    obj = _mesh_object(name, verts, faces)
    obj.location = location
    obj.rotation_euler = (0.0, 0.0, rotation)
    return obj


def _tube(name, radius_bottom, radius_top, height, sides=8, base=0.0):
    verts = []
    for i in range(sides):
        a = 2.0 * math.pi * i / sides
        verts.append((math.cos(a) * radius_bottom,
                      math.sin(a) * radius_bottom, base))
    for i in range(sides):
        a = 2.0 * math.pi * i / sides
        verts.append((math.cos(a) * radius_top,
                      math.sin(a) * radius_top, base + height))
    faces = []
    for i in range(sides):
        j = (i + 1) % sides
        faces.append((i, j, j + sides, i + sides))
    faces.append(tuple(range(sides - 1, -1, -1)))
    faces.append(tuple(range(sides, sides * 2)))
    return _mesh_object(name, verts, faces)


def build_perimeter(radius=68.0, height=2.45, post_step=3.0, rng=None):
    """The security line round the tower's feet.

    Since 2021 the base is fenced: glazed panels on the river and park sides,
    dark steel palings elsewhere. It matters here for a duller reason than
    accuracy - it is a continuous, sharply lit, human-scale object standing on
    the one part of the frame that was an empty sheet of gravel, so it gives
    the middle distance something to measure the tower against.
    """
    steel = _material("PerimeterSteel", (0.018, 0.020, 0.022, 1.0),
                      roughness=0.4, specular=0.4)

    # One bay - two posts, two rails and the palings between them - built once
    # and instanced round the circle.
    #
    # The first version filled each bay with a glazed panel, which is what the
    # river side of the real fence has. Rendered at 120 m it came back as a ring
    # of flat white boards brighter than the sky: a thin opaque slab with a pale
    # base colour is not glass, and there was nothing to see through it. Dark
    # palings read correctly at any distance and are what the park side of the
    # real fence is anyway.
    verts = []
    faces = []

    def add(sx, sy, sz, cx, cy, cz):
        base = len(verts)
        hx, hy, hz = sx * 0.5, sy * 0.5, sz * 0.5
        for dx in (-hx, hx):
            for dy in (-hy, hy):
                for dz in (-hz, hz):
                    verts.append((cx + dx, cy + dy, cz + dz))
        quads = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1),
                 (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)]
        faces.extend(tuple(base + i for i in quad) for quad in quads)

    bay = post_step
    add(0.09, 0.09, height, -bay * 0.5, 0.0, height * 0.5)
    add(0.09, 0.09, height, bay * 0.5, 0.0, height * 0.5)
    for z in (0.30, height - 0.22):
        add(bay, 0.05, 0.07, 0.0, 0.0, z)
    palings = int(bay / 0.14)
    for i in range(palings):
        x = -bay * 0.5 + bay * (i + 0.5) / palings
        add(0.022, 0.022, height - 0.30, x, 0.0, 0.30 + (height - 0.30) * 0.5)
    proto = _mesh_object("PerimeterBay", verts, faces)
    proto.data.materials.append(steel)
    proto.hide_render = True
    proto.location = (0.0, 0.0, -600.0)

    count = int(2.0 * math.pi * radius / post_step)
    for i in range(count):
        a = 2.0 * math.pi * (i + 0.5) / count
        obj = bpy.data.objects.new("Perimeter_inst", proto.data)
        obj.location = (math.cos(a) * radius, math.sin(a) * radius, 0.0)
        obj.rotation_euler = (0.0, 0.0, a + math.pi * 0.5)
        _link(obj)
    print("[park] perimeter: %d bays at r=%.0f m" % (count, radius))
    return count


def build_pavilions(rng, radius=52.0):
    """Ticket kiosks, litter bins and signboards on the esplanade."""
    roof = _material("KioskRoof", (0.030, 0.033, 0.036, 1.0), roughness=0.35,
                     specular=0.35)
    wall = _material("KioskWall", (0.055, 0.075, 0.062, 1.0), roughness=0.5,
                     specular=0.25)
    glazing = _material("KioskGlass", (0.30, 0.34, 0.35, 1.0), roughness=0.10,
                        specular=0.5)
    plastic = _material("BinGreen", (0.030, 0.055, 0.038, 1.0), roughness=0.55,
                        specular=0.2)

    made = 0
    for i in range(4):
        a = math.pi * 0.25 + math.pi * 0.5 * i
        cx, cy = math.cos(a) * radius, math.sin(a) * radius
        body = _box("Kiosk", 13.0, 5.4, 3.1, (cx, cy, 0.0), a + math.pi * 0.5)
        body.data.materials.append(wall)
        band = _box("KioskWindow", 13.2, 5.6, 1.15, (cx, cy, 1.35),
                    a + math.pi * 0.5)
        band.data.materials.append(glazing)
        cap = _box("KioskRoof", 14.4, 6.6, 0.35, (cx, cy, 3.1),
                   a + math.pi * 0.5)
        cap.data.materials.append(roof)
        made += 3

    for i in range(26):
        a = 2.0 * math.pi * i / 26.0 + 0.11
        r = rng.uniform(40.0, 63.0)
        x, y = math.cos(a) * r, math.sin(a) * r
        # Paris litter bins are a transparent sack in a hooped frame on a post,
        # not a solid drum: a plain cylinder at this size reads as a bollard.
        post = _tube("BinPost", 0.05, 0.05, 1.15, sides=6)
        post.location = (x, y, 0.0)
        post.data.materials.append(roof)
        hoop = _tube("BinHoop", 0.30, 0.30, 0.06, sides=10)
        hoop.location = (x, y, 1.10)
        hoop.data.materials.append(plastic)
        sack = _tube("BinSack", 0.28, 0.24, 0.62, sides=10)
        sack.location = (x, y, 0.46)
        sack.data.materials.append(plastic)
        made += 3

    for i in range(12):
        a = 2.0 * math.pi * i / 12.0 + 0.4
        r = 66.0
        post = _tube("SignPost", 0.05, 0.05, 2.3, sides=6)
        post.location = (math.cos(a) * r, math.sin(a) * r, 0.0)
        post.data.materials.append(roof)
        board = _box("SignBoard", 0.9, 0.06, 0.62,
                     (math.cos(a) * r, math.sin(a) * r, 1.6), a)
        board.data.materials.append(wall)
        made += 2
    print("[park] %d pavilion / bin / sign parts" % made)
    return made


def build_floodmasts(height=27.0, radius=27.0, count=4):
    """The lattice floodlight masts that stand inside the arch.

    They are the one vertical the eye has to judge the arch's height against -
    in the reference photographs they read as bright slivers against the dark
    underside of the first floor - and the render had nothing at all in there.
    """
    # Galvanised, not white, and darker than seems right on paper. At 0.40 the
    # masts came out brighter than the sky behind them; at 0.145 they were still
    # the loudest object in the middle distance, because a thin vertical made of
    # flat-shaded tubes catches sky on every facet and the specular adds to it.
    # 0.055 with the sheen pulled right down puts them where a photograph has
    # them: present, and not the first thing you see.
    steel = _material("MastSteel", (0.055, 0.057, 0.060, 1.0), roughness=0.52,
                      specular=0.12)
    made = 0
    spread = 1.15
    for i in range(count):
        a = math.pi * 0.25 + 2.0 * math.pi * i / count
        cx, cy = math.cos(a) * radius, math.sin(a) * radius
        for leg in range(3):
            b = 2.0 * math.pi * leg / 3.0 + a
            # Three bare legs read as slack cables hanging out of the arch
            # rather than as a mast standing on the ground; the ties below are
            # what make the group resolve into one structure.
            post = _tube("Mast", 0.16, 0.09, height, sides=5)
            post.location = (cx + math.cos(b) * spread,
                             cy + math.sin(b) * spread, 0.0)
            post.data.materials.append(steel)
            made += 1
        for level in range(1, 4):
            z = height * level / 4.0
            tie = _tube("MastTie", spread * 1.15, spread * 1.15, 0.10, sides=3)
            tie.location = (cx, cy, z)
            tie.rotation_euler = (0.0, 0.0, a)
            tie.data.materials.append(steel)
            made += 1
        head = _box("MastHead", 2.6, 1.4, 1.7, (cx - 1.3, cy - 0.7, height))
        head.data.materials.append(steel)
        made += 1
    print("[park] %d floodlight mast parts" % made)
    return made
