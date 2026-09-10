"""Procedural Eiffel Tower generator for Blender.

Dimensions follow the published figures for the tower, which are public domain:
a 125 m square base, platforms at 57.63 m, 115.73 m and 276.1 m, 300 m to the
top of the cupola and 330 m to the tip of the broadcast mast.

The tower is built from individual bars so the lattice reads as real ironwork
rather than as a decimated silhouette. Every bar is an extruded angle iron or a
small box girder, and the four corner chords are optionally built up from four
sub-angles laced together, which is how the real structure is put together.

Run standalone for a quick look:

    blender --background --python eiffel_tower.py -- --detail 0.4 --out t.blend
"""

from __future__ import annotations

import bisect
import math
import os
import sys

import bpy

from iron import iron_material


# --------------------------------------------------------------------------
# tower profile
# --------------------------------------------------------------------------

# (height, half of the square cross-section side) in metres.
#
# The widths above the first platform were measured off a reference model's
# silhouette rather than guessed. The first pass tapered far too fast - at the
# second platform it was 32% narrower than it should be - which turned the
# shaft into a needle instead of the broad, slowly narrowing body the tower
# actually has. The slight widening at 288 m is real: the structure flares a
# little under the top platform before the cupola.
PROFILE = [
    (0.0, 62.5),
    (12.0, 55.5),
    (25.0, 47.0),
    (40.0, 38.5),
    (57.63, 32.5),    # first platform, 65 m square
    (80.0, 26.0),
    (100.0, 21.0),
    (115.73, 17.5),   # second platform
    (150.0, 14.0),
    (200.0, 10.2),
    (250.0, 8.2),
    (276.1, 7.4),     # third platform
    (288.0, 6.4),
    (296.0, 4.6),
    (300.0, 3.4),     # cupola
]

PLATFORM1 = 57.63
PLATFORM2 = 115.73
PLATFORM3 = 276.1
CUPOLA_TOP = 300.0
MAST_TOP = 330.0


def _monotone_cubic(xs, ys):
    """Fritsch-Carlson monotone cubic interpolation, so the legs curve smoothly
    without the overshoot a natural spline would give near the base."""
    n = len(xs)
    h = [xs[i + 1] - xs[i] for i in range(n - 1)]
    d = [(ys[i + 1] - ys[i]) / h[i] for i in range(n - 1)]
    m = [d[0]] + [(d[i - 1] + d[i]) * 0.5 for i in range(1, n - 1)] + [d[-1]]
    for i in range(n - 1):
        if d[i] == 0.0:
            m[i] = m[i + 1] = 0.0
            continue
        a, b = m[i] / d[i], m[i + 1] / d[i]
        s = a * a + b * b
        if s > 9.0:
            t = 3.0 / math.sqrt(s)
            m[i] = t * a * d[i]
            m[i + 1] = t * b * d[i]

    def f(x):
        if x <= xs[0]:
            return ys[0]
        if x >= xs[-1]:
            return ys[-1]
        i = min(bisect.bisect_right(xs, x) - 1, n - 2)
        t = (x - xs[i]) / h[i]
        t2, t3 = t * t, t * t * t
        return ((2 * t3 - 3 * t2 + 1) * ys[i]
                + (t3 - 2 * t2 + t) * h[i] * m[i]
                + (-2 * t3 + 3 * t2) * ys[i + 1]
                + (t3 - t2) * h[i] * m[i + 1])

    return f


half_side = _monotone_cubic([p[0] for p in PROFILE], [p[1] for p in PROFILE])

QUADRANTS = [(1, 1), (1, -1), (-1, -1), (-1, 1)]


def leg_width(z):
    """Side of the square box truss of one leg at height z."""
    t = min(max(z / PLATFORM1, 0.0), 1.0)
    return 16.0 * (1.0 - t) + 7.0 * t


# --------------------------------------------------------------------------
# bar mesh accumulation
# --------------------------------------------------------------------------

def section_angle(w, t):
    """Angle iron cross-section, centred on the bar axis."""
    a, b = w * 0.5, w * 0.5 - t
    return [(-a, -a), (a, -a), (a, -b), (-b, -b), (-b, a), (-a, a)]


def section_box(w, h):
    a, b = w * 0.5, h * 0.5
    return [(-a, -b), (a, -b), (a, b), (-a, b)]


def _frame(p0, p1):
    """Unit direction plus two perpendicular axes for a bar from p0 to p1."""
    dx, dy, dz = p1[0] - p0[0], p1[1] - p0[1], p1[2] - p0[2]
    length = math.sqrt(dx * dx + dy * dy + dz * dz)
    if length < 1e-6:
        return None
    d = (dx / length, dy / length, dz / length)
    ref = (0.0, 0.0, 1.0) if abs(d[2]) < 0.95 else (1.0, 0.0, 0.0)
    ux = d[1] * ref[2] - d[2] * ref[1]
    uy = d[2] * ref[0] - d[0] * ref[2]
    uz = d[0] * ref[1] - d[1] * ref[0]
    un = math.sqrt(ux * ux + uy * uy + uz * uz)
    u = (ux / un, uy / un, uz / un)
    v = (d[1] * u[2] - d[2] * u[1],
         d[2] * u[0] - d[0] * u[2],
         d[0] * u[1] - d[1] * u[0])
    return d, u, v, length


class BarMesh:
    """Accumulates bars into flat vertex/face lists for one mesh."""

    def __init__(self):
        self.verts = []
        self.faces = []

    def add(self, p0, p1, section, roll=0.0):
        frame = _frame(p0, p1)
        if frame is None:
            return
        _, u, v, _ = frame
        if roll:
            c, s = math.cos(roll), math.sin(roll)
            u, v = (tuple(c * u[i] + s * v[i] for i in range(3)),
                    tuple(-s * u[i] + c * v[i] for i in range(3)))

        base = len(self.verts)
        n = len(section)
        for p in (p0, p1):
            for (a, b) in section:
                self.verts.append((p[0] + a * u[0] + b * v[0],
                                   p[1] + a * u[1] + b * v[1],
                                   p[2] + a * u[2] + b * v[2]))
        for i in range(n):
            j = (i + 1) % n
            self.faces.append((base + i, base + j, base + n + j, base + n + i))
        self.faces.append(tuple(range(base + n - 1, base - 1, -1)))
        self.faces.append(tuple(range(base + n, base + 2 * n)))

    def to_object(self, name, collection=None):
        mesh = bpy.data.meshes.new(name)
        mesh.from_pydata(self.verts, [], self.faces)
        mesh.validate(verbose=False)
        mesh.update()
        obj = bpy.data.objects.new(name, mesh)
        (collection or bpy.context.scene.collection).objects.link(obj)
        return obj


# --------------------------------------------------------------------------
# structural pieces
# --------------------------------------------------------------------------

def add_chord(bars, p0, p1, w, built_up, lacing_step=1.8):
    """A corner chord: either a single angle, or four sub-angles laced
    together, which is what the real members are."""
    if not built_up:
        bars.add(p0, p1, section_angle(w, w * 0.28))
        return

    frame = _frame(p0, p1)
    if frame is None:
        return
    d, u, v, length = frame
    sub = w * 0.34
    o = w * 0.33
    offs = [(-o, -o), (o, -o), (o, o), (-o, o)]

    def at(t, ou, ov):
        return (p0[0] + (p1[0] - p0[0]) * t + ou * u[0] + ov * v[0],
                p0[1] + (p1[1] - p0[1]) * t + ou * u[1] + ov * v[1],
                p0[2] + (p1[2] - p0[2]) * t + ou * u[2] + ov * v[2])

    for ou, ov in offs:
        bars.add(at(0.0, ou, ov), at(1.0, ou, ov), section_angle(sub, sub * 0.3))

    # zig-zag lacing between neighbouring sub-angles
    steps = max(2, int(length / lacing_step))
    lace = section_box(sub * 0.5, sub * 0.2)
    for k in range(steps):
        t0, t1 = k / steps, (k + 1) / steps
        for i in range(4):
            a, b = offs[i], offs[(i + 1) % 4]
            if k % 2 == 0:
                bars.add(at(t0, *a), at(t1, *b), lace)
            else:
                bars.add(at(t0, *b), at(t1, *a), lace)


def lattice_column(bars, n_levels, corner_fn, chord_w, brace_w, built_up,
                   sub_braces=True):
    """Generic four-chord lattice. `corner_fn(level, corner) -> point`."""
    for k in range(n_levels - 1):
        for c in range(4):
            add_chord(bars, corner_fn(k, c), corner_fn(k + 1, c), chord_w, built_up)

    horiz = section_angle(brace_w * 1.15, brace_w * 0.3)
    diag = section_angle(brace_w, brace_w * 0.3)
    post = section_angle(brace_w * 0.7, brace_w * 0.22)
    small = section_angle(brace_w * 0.55, brace_w * 0.2)

    for k in range(n_levels):
        for c in range(4):
            bars.add(corner_fn(k, c), corner_fn(k, (c + 1) % 4), horiz)

    for k in range(n_levels - 1):
        for c in range(4):
            c2 = (c + 1) % 4
            a0, a1 = corner_fn(k, c), corner_fn(k + 1, c)
            b0, b1 = corner_fn(k, c2), corner_fn(k + 1, c2)
            bars.add(a0, b1, diag)
            bars.add(b0, a1, diag)
            if sub_braces:
                # Second level of bracing. The real ironwork subdivides every
                # panel again, and without this the tower reads as a coarse
                # wireframe - which also makes it far harder to reconstruct,
                # since a splat has almost nothing to attach to between the
                # primary diagonals.
                def at(u, v):
                    lo = tuple(a0[i] + (b0[i] - a0[i]) * u for i in range(3))
                    hi = tuple(a1[i] + (b1[i] - a1[i]) * u for i in range(3))
                    return tuple(lo[i] + (hi[i] - lo[i]) * v for i in range(3))

                bars.add(at(0.5, 0.0), at(0.5, 1.0), post)
                bars.add(at(0.0, 0.5), at(1.0, 0.5), post)
                for iu in (0.0, 0.5):
                    for iv in (0.0, 0.5):
                        bars.add(at(iu, iv), at(iu + 0.5, iv + 0.5), small)
                        bars.add(at(iu + 0.5, iv), at(iu, iv + 0.5), small)


# --------------------------------------------------------------------------
# tower sections
# --------------------------------------------------------------------------

def build_legs(bars, detail, built_up):
    """The four splayed box trusses from the ground to the first platform."""
    n = max(5, int(14 * detail))
    zs = [PLATFORM1 * (k / n) ** 1.05 for k in range(n + 1)]

    for sx, sy in QUADRANTS:
        def corner_fn(k, c, sx=sx, sy=sy):
            z = zs[k]
            s = half_side(z)
            w = leg_width(z)
            # corner 0 is the outer corner; the box extends inward.
            du = (0.0, w, w, 0.0)[c]
            dv = (0.0, 0.0, w, w)[c]
            return (sx * (s - du), sy * (s - dv), z)

        lattice_column(bars, n + 1, corner_fn, chord_w=1.8, brace_w=0.7,
                       built_up=built_up)


def _leg_inner_x(z):
    """How far along a face the ironwork can reach before it runs into a leg."""
    return half_side(z) - leg_width(z) * 0.45


def build_arches(bars, detail):
    """The decorative arches spanning between the piers under the first floor.

    Purely ornamental on the real tower, but they carry the silhouette. The
    ribbon follows the slope of the face (its distance from the axis tracks the
    tower profile) and is clipped where it meets the legs, so it lands on the
    piers instead of floating outside them.
    """
    # One sample per ~4.5 m of span: any denser and the webbing posts merge
    # into a comb instead of reading as panels.
    n = max(10, int(20 * detail))
    z_top = PLATFORM1 - 2.0
    z_crown, x_half = 41.0, 44.0
    depth = 2.4          # half thickness of the ribbon, across the face
    chord = section_angle(0.85, 0.26)
    web = section_angle(0.4, 0.14)

    def opening_z(x):
        return z_crown - (z_crown - 18.0) * (abs(x) / x_half) ** 2.0

    for side in range(4):
        ca, sa = math.cos(side * math.pi * 0.5), math.sin(side * math.pi * 0.5)

        def place(x, z, off, ca=ca, sa=sa):
            y = half_side(z) - 3.0 + off
            return (x * ca - y * sa, x * sa + y * ca, z)

        xs = [-x_half + 2.0 * x_half * k / n for k in range(n + 1)]
        # keep only the span that clears the legs
        xs = [x for x in xs if abs(x) < _leg_inner_x(opening_z(x))]
        if len(xs) < 3:
            continue

        for off in (-depth, depth):
            prev = None
            for x in xs:
                p = place(x, opening_z(x), off)
                if prev is not None:
                    bars.add(prev, p, chord)
                prev = p
            prev = None
            for x in (xs[0], xs[-1]):
                bars.add(place(x, opening_z(x), off), place(x, z_top, off), chord)
        for x in (xs[0], xs[-1]):
            bars.add(place(x, z_top, -depth), place(x, z_top, depth), chord)

        # Infill as a grid that follows the arc rather than a row of vertical
        # posts. The posts version read as a comb: every bar parallel, evenly
        # spaced, obviously generated. Here each cell is braced diagonally and
        # the verticals are thinned out, which is what the real spandrel does.
        rows = max(2, int(3 * detail) + 1)

        def grid(x, j, off):
            zb = opening_z(x)
            return place(x, zb + (z_top - zb) * j / rows, off)

        for i, x in enumerate(xs):
            for off in (-depth, depth):
                if i % 2 == 0:
                    bars.add(grid(x, 0, off), grid(x, rows, off), web)
            bars.add(grid(x, 0, -depth), grid(x, 0, depth), web)
            if i % 2 == 0:
                bars.add(grid(x, rows, -depth), grid(x, rows, depth), web)
            if i + 1 >= len(xs):
                continue
            x2 = xs[i + 1]
            for j in range(rows):
                for off in (-depth, depth):
                    bars.add(grid(x, j, off), grid(x2, j + 1, off), web)
                    bars.add(grid(x2, j, off), grid(x, j + 1, off), web)
            for j in range(1, rows):
                for off in (-depth, depth):
                    bars.add(grid(x, j, off), grid(x2, j, off), web)

        # Heavier double bottom chord plus a scalloped rim under it: the arch's
        # underside is the one edge a viewer standing beneath actually reads.
        rim = section_box(0.55, 0.9)
        for off in (-depth, depth):
            prev = None
            for x in xs:
                p = place(x, opening_z(x) - 1.4, off)
                if prev is not None:
                    bars.add(prev, p, rim)
                prev = p
        for i, x in enumerate(xs):
            lo = place(x, opening_z(x), 0.0)
            under = place(x, opening_z(x) - 1.4, 0.0)
            bars.add(lo, under, web)
            if i + 1 < len(xs):
                x2 = xs[i + 1]
                mid = 0.5 * (x + x2)
                bars.add(place(mid, opening_z(mid) - 1.4, -depth),
                         place(mid, opening_z(mid) - 2.6, 0.0), web)
                bars.add(place(mid, opening_z(mid) - 1.4, depth),
                         place(mid, opening_z(mid) - 2.6, 0.0), web)


def build_platform(bars, z, outer, inner, thickness, detail, railing=True):
    """A square platform ring: edge girders, floor beams and a railing.

    The platforms overhang the lattice and are deep enough to read as slabs.
    Drawing them as a single thin line, as the first pass did, loses one of the
    tower's strongest silhouette features - from the ground the first floor is a
    thick band sticking well out past the legs, not a hairline.
    """
    n = max(6, int(outer * 0.8 * detail))
    girder = section_box(1.1, thickness)
    beam = section_box(0.45, thickness * 0.6)
    rail = section_angle(0.11, 0.04)
    fascia = section_box(0.8, thickness * 0.5)

    for r, sec in ((outer, girder), (inner, beam)):
        pts = [(r, r, z), (r, -r, z), (-r, -r, z), (-r, r, z)]
        for i in range(4):
            bars.add(pts[i], pts[(i + 1) % 4], sec)

    # Lower edge of the slab plus its hangers, so the band has visible depth.
    zl = z - thickness * 0.95
    lower = [(outer, outer, zl), (outer, -outer, zl),
             (-outer, -outer, zl), (-outer, outer, zl)]
    for i in range(4):
        bars.add(lower[i], lower[(i + 1) % 4], fascia)
    hangers = max(6, int(outer * 0.5 * detail))
    for i in range(4):
        a, b = lower[i], lower[(i + 1) % 4]
        for k in range(hangers + 1):
            t = k / hangers
            x = a[0] + (b[0] - a[0]) * t
            y = a[1] + (b[1] - a[1]) * t
            bars.add((x, y, zl), (x, y, z), fascia)

    for i in range(n + 1):
        t = -outer + 2.0 * outer * i / n
        if abs(t) <= inner:
            for s in (1, -1):
                bars.add((t, s * inner, z), (t, s * outer, z), beam)
                bars.add((s * inner, t, z), (s * outer, t, z), beam)
        else:
            bars.add((t, -outer, z), (t, outer, z), beam)
            bars.add((-outer, t, z), (outer, t, z), beam)

    if railing:
        h = 1.15
        pts = [(outer, outer), (outer, -outer), (-outer, -outer), (-outer, outer)]
        for i in range(4):
            a, b = pts[i], pts[(i + 1) % 4]
            bars.add((a[0], a[1], z + h), (b[0], b[1], z + h), rail)
            steps = max(4, int(outer * 1.2 * detail))
            for k in range(steps + 1):
                t = k / steps
                x = a[0] + (b[0] - a[0]) * t
                y = a[1] + (b[1] - a[1]) * t
                bars.add((x, y, z), (x, y, z + h), rail)


def build_upper(bars, z0, z1, detail, built_up, chord_w, brace_w, power=1.0,
                rate=0.26):
    n = max(5, int((z1 - z0) * rate * detail))
    zs = [z0 + (z1 - z0) * (k / n) ** power for k in range(n + 1)]

    def corner_fn(k, c):
        z = zs[k]
        s = half_side(z)
        sx, sy = QUADRANTS[c]
        return (sx * s, sy * s, z)

    lattice_column(bars, n + 1, corner_fn, chord_w, brace_w, built_up)


def build_top(bars, detail):
    """Enclosed top floor, cupola and the broadcast mast."""
    s = half_side(PLATFORM3)
    wall = section_box(0.35, 0.35)
    h = 6.5
    pts = [(s, s), (s, -s), (-s, -s), (-s, s)]
    for zz in (PLATFORM3, PLATFORM3 + h):
        for i in range(4):
            a, b = pts[i], pts[(i + 1) % 4]
            bars.add((a[0], a[1], zz), (b[0], b[1], zz), wall)
    steps = max(3, int(6 * detail))
    for i in range(4):
        a, b = pts[i], pts[(i + 1) % 4]
        for k in range(steps + 1):
            t = k / steps
            x, y = a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t
            bars.add((x, y, PLATFORM3), (x, y, PLATFORM3 + h), wall)

    # cupola: a small tapering lattice up to 300 m
    z0, z1 = PLATFORM3 + h, CUPOLA_TOP
    n = max(4, int(8 * detail))
    for k in range(n):
        t0, t1 = k / n, (k + 1) / n
        za, zb = z0 + (z1 - z0) * t0, z0 + (z1 - z0) * t1
        ra, rb = half_side(za) * 0.8, half_side(zb) * 0.8
        for c in range(4):
            sx, sy = QUADRANTS[c]
            sx2, sy2 = QUADRANTS[(c + 1) % 4]
            bars.add((sx * ra, sy * ra, za), (sx * rb, sy * rb, zb),
                     section_angle(0.5, 0.16))
            bars.add((sx * ra, sy * ra, za), (sx2 * rb, sy2 * rb, zb),
                     section_angle(0.3, 0.11))

    # mast
    bars.add((0, 0, CUPOLA_TOP), (0, 0, MAST_TOP - 6.0), section_box(1.5, 1.5))
    bars.add((0, 0, MAST_TOP - 6.0), (0, 0, MAST_TOP), section_box(0.4, 0.4))


# --------------------------------------------------------------------------
# material
# --------------------------------------------------------------------------

def eiffel_paint():
    """Three graduated shades of the tower's brown, darkest at the bottom, plus
    a little grime so the ironwork is not flat."""
    mat = bpy.data.materials.new("EiffelIron")
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()

    out = nt.nodes.new("ShaderNodeOutputMaterial")
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    geo = nt.nodes.new("ShaderNodeNewGeometry")
    sep = nt.nodes.new("ShaderNodeSeparateXYZ")
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    mapr = nt.nodes.new("ShaderNodeMapRange")
    noise = nt.nodes.new("ShaderNodeTexNoise")
    mix = nt.nodes.new("ShaderNodeMixRGB")
    bump = nt.nodes.new("ShaderNodeBump")

    # Map Range carries a second, vector-typed set of sockets with the same
    # names, so address the float ones by index.
    mapr.inputs[1].default_value = 0.0
    mapr.inputs[2].default_value = 300.0
    noise.inputs["Scale"].default_value = 22.0
    noise.inputs["Detail"].default_value = 8.0
    mix.blend_type = "MULTIPLY"
    mix.inputs["Fac"].default_value = 0.28
    bump.inputs["Strength"].default_value = 0.25

    ramp.color_ramp.elements[0].position = 0.0
    ramp.color_ramp.elements[0].color = (0.048, 0.030, 0.016, 1.0)
    ramp.color_ramp.elements[1].position = 1.0
    ramp.color_ramp.elements[1].color = (0.145, 0.098, 0.055, 1.0)
    mid = ramp.color_ramp.elements.new(0.45)
    mid.color = (0.090, 0.058, 0.032, 1.0)

    nt.links.new(geo.outputs["Position"], sep.inputs["Vector"])
    nt.links.new(sep.outputs["Z"], mapr.inputs["Value"])
    nt.links.new(mapr.outputs["Result"], ramp.inputs["Fac"])
    nt.links.new(ramp.outputs["Color"], mix.inputs["Color1"])
    nt.links.new(noise.outputs["Color"], mix.inputs["Color2"])
    nt.links.new(mix.outputs["Color"], bsdf.inputs["Base Color"])
    nt.links.new(noise.outputs["Fac"], bump.inputs["Height"])
    nt.links.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
    # Painted iron, not bare metal. Any metallic weight here mirrors the sky
    # over the whole lattice and washes the tower out to a blue-grey haze; the
    # specular level is pulled below the 0.5 default for the same reason.
    bsdf.inputs["Metallic"].default_value = 0.0
    bsdf.inputs["Specular IOR Level"].default_value = 0.18
    bsdf.inputs["Roughness"].default_value = 0.62
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
    return mat


# --------------------------------------------------------------------------
# entry point
# --------------------------------------------------------------------------

def build_tower(detail=1.0, built_up=True, name="EiffelTower", collection=None):
    bars = BarMesh()
    build_legs(bars, detail, built_up)
    build_arches(bars, detail)
    build_platform(bars, PLATFORM1, half_side(PLATFORM1) * 1.17, 22.0, 5.4,
                   detail)
    build_upper(bars, PLATFORM1 + 5.4, PLATFORM2, detail, built_up, 1.1, 0.4)
    build_platform(bars, PLATFORM2, half_side(PLATFORM2) * 1.42, 10.0, 4.4,
                   detail)
    build_upper(bars, PLATFORM2 + 4.4, PLATFORM3, detail, built_up, 0.75, 0.26,
                power=0.92, rate=0.22)
    build_platform(bars, PLATFORM3, half_side(PLATFORM3) * 1.45, 3.6, 3.0,
                   detail, railing=False)
    build_top(bars, detail)

    obj = bars.to_object(name, collection)
    obj.data.materials.append(iron_material())
    print("[eiffel] %d verts, %d faces" % (len(bars.verts), len(bars.faces)))
    return obj


def _cli_args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    detail, out = 0.5, ""
    for i, a in enumerate(argv):
        if a == "--detail":
            detail = float(argv[i + 1])
        elif a == "--out":
            out = argv[i + 1]
    return detail, out


if __name__ == "__main__":
    detail, out = _cli_args()
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o, do_unlink=True)
    build_tower(detail=detail, built_up=detail >= 0.6)
    if out:
        bpy.ops.wm.save_as_mainfile(filepath=os.path.abspath(out))
