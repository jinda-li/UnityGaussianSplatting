"""Painted ironwork material for the tower.

The first version of this was procedural - a height gradient plus a noise bump -
and it rendered like moulded plastic. Real ironwork carries surface detail at a
scale you can see from a few metres away: brush texture in the paint, rivet
heads, rust bleeding out of seams, grime settled on every upward face. None of
that survives as a flat colour, and a splat trained on flat colour stays flat.

So the paint is built from a real scanned PBR set, box projected because the
generated bars carry no UVs, and then pushed to the tower's own brown, with a
procedural rivet lattice and occlusion grime on top of it. Poly Haven is CC0,
so nothing here needs attribution.
"""

from __future__ import annotations

import bpy

import assets

# The tower's paint has always been applied in graduated shades, darkest at the
# bottom, so the whole thing reads as one colour from the ground.
# Brighter than a paint chip would suggest: a dense lattice self-shadows
# heavily, so a scan-accurate albedo renders as a silhouette from the ground.
SHADE_LOW = (0.104, 0.064, 0.033, 1.0)
SHADE_MID = (0.170, 0.108, 0.056, 1.0)
SHADE_HIGH = (0.252, 0.165, 0.088, 1.0)


def _box_texture(nt, mapping, path, non_color, blend=0.35):
    tex = nt.nodes.new("ShaderNodeTexImage")
    tex.image = bpy.data.images.load(path, check_existing=True)
    if non_color:
        tex.image.colorspace_settings.name = "Non-Color"
    # Box projection needs no UVs, which the bar mesh does not have.
    tex.projection = "BOX"
    tex.projection_blend = blend
    tex.extension = "REPEAT"
    nt.links.new(mapping.outputs["Vector"], tex.inputs["Vector"])
    return tex


def _rivets(nt, coord, pitch=0.085, row_spacing=0.30, radius=0.36,
            height=0.0062, seam_spacing=0.92, seam_height=0.0038):
    """Rows of rivet heads and the plate seams they fasten, as a bump.

    Rivets are the single most recognisable thing about the tower's ironwork -
    two and a half million of them - and no scanned texture supplies them at
    the right spacing. Photographs of a leg close up show what the arrangement
    actually is: lines of shallow domes running the length of a member, tight
    along the line (8-9 cm) and much further apart between lines (30 cm or
    more), following the edges of the built-up plates and the seams between
    them.

    So it is built in two parts. A Voronoi with its randomness at zero is a
    regular grid of cell centres, and its Distance output is a cone rising from
    each one; a ramp turns the cone into a dome. That alone gives a uniform dot
    field - which is what this used to be, and on a member set at an angle to
    the projection axes it read as diagonal dotted lines, a pattern the tower
    has nowhere. Multiplying the dome field by a band mask keeps only every
    third or fourth row, so the dots resolve into lines.

    The seams are a separate, much wider band pattern raised slightly proud:
    the members are not rolled sections but plates riveted together, and the
    long ridge where two plates overlap is as visible as the rivets are.

    None of this can be geometry. At eight rivets per metre over 500,000 faces
    of ironwork it would be tens of millions of separate heads.
    """
    mapping = nt.nodes.new("ShaderNodeMapping")
    mapping.inputs["Scale"].default_value = (1.0 / pitch,) * 3
    nt.links.new(coord.outputs["Object"], mapping.inputs["Vector"])
    grid = nt.nodes.new("ShaderNodeTexVoronoi")
    grid.feature = "F1"
    grid.inputs["Randomness"].default_value = 0.0
    # The node has a Scale input of its own, defaulting to 5, which multiplies
    # whatever the Mapping node already did. Left alone it turned an 8.5 cm
    # rivet pitch into a 1.7 cm one - far below a pixel at any distance the
    # tower is seen from, so the lattice just picked up a faint noise.
    grid.inputs["Scale"].default_value = 1.0
    nt.links.new(mapping.outputs["Vector"], grid.inputs["Vector"])
    dome = nt.nodes.new("ShaderNodeValToRGB")
    dome.color_ramp.interpolation = "EASE"
    dome.color_ramp.elements[0].position = 0.0
    dome.color_ramp.elements[0].color = (1.0, 1.0, 1.0, 1.0)
    dome.color_ramp.elements[1].position = radius
    dome.color_ramp.elements[1].color = (0.0, 0.0, 0.0, 1.0)
    nt.links.new(grid.outputs["Distance"], dome.inputs["Fac"])

    def bands(period, low, high, profile="SIN"):
        band_map = nt.nodes.new("ShaderNodeMapping")
        band_map.inputs["Scale"].default_value = (1.0 / period,) * 3
        nt.links.new(coord.outputs["Object"], band_map.inputs["Vector"])
        wave = nt.nodes.new("ShaderNodeTexWave")
        wave.wave_type = "BANDS"
        wave.bands_direction = "X"
        wave.wave_profile = profile
        wave.inputs["Scale"].default_value = 1.0
        wave.inputs["Distortion"].default_value = 0.0
        nt.links.new(band_map.outputs["Vector"], wave.inputs["Vector"])
        ramp = nt.nodes.new("ShaderNodeValToRGB")
        ramp.color_ramp.elements[0].position = low
        ramp.color_ramp.elements[0].color = (0.0, 0.0, 0.0, 1.0)
        ramp.color_ramp.elements[1].position = high
        ramp.color_ramp.elements[1].color = (1.0, 1.0, 1.0, 1.0)
        nt.links.new(wave.outputs["Fac"], ramp.inputs["Fac"])
        return ramp

    rows = bands(row_spacing, 0.62, 0.80)
    kept = nt.nodes.new("ShaderNodeMixRGB")
    kept.blend_type = "MULTIPLY"
    kept.inputs["Fac"].default_value = 1.0
    nt.links.new(dome.outputs["Color"], kept.inputs["Color1"])
    nt.links.new(rows.outputs["Color"], kept.inputs["Color2"])

    seams = bands(seam_spacing, 0.86, 0.96)
    relief = nt.nodes.new("ShaderNodeMixRGB")
    relief.blend_type = "ADD"
    relief.inputs["Fac"].default_value = seam_height / height
    nt.links.new(kept.outputs["Color"], relief.inputs["Color1"])
    nt.links.new(seams.outputs["Color"], relief.inputs["Color2"])

    bump = nt.nodes.new("ShaderNodeBump")
    bump.inputs["Strength"].default_value = 1.0
    bump.inputs["Distance"].default_value = height
    nt.links.new(relief.outputs["Color"], bump.inputs["Height"])
    return bump, dome


def _grime(nt, distance=0.22):
    """Occlusion-driven dirt, or None if this Blender has no AO node.

    A lattice is nothing but junctions, and in every photograph of the tower
    those junctions are darker and duller than the open faces: a century of
    soot and rain has collected in every internal corner. Without it each
    member is uniformly lit along its whole length, which is the single
    strongest "untextured CG" cue the ironwork has - and one no colour map can
    fix, because the effect depends on the geometry around each point rather
    than on the point itself.

    The distance has to stay short. At 0.55 m almost every point inside a
    lattice this dense has a neighbour within range, so the effect stopped
    being dirt-in-the-corners and became a flat darkening of the entire
    interior - the underside of the first floor rendered as one dead brown
    sheet. 0.22 m is about the width of a member, which is the scale grime
    actually collects at.
    """
    try:
        ao = nt.nodes.new("ShaderNodeAmbientOcclusion")
    except RuntimeError:
        return None
    ao.samples = 8
    ao.only_local = True
    ao.inputs["Distance"].default_value = distance
    return ao


def iron_material(name="EiffelIron", asset_id="rusty_painted_metal",
                  rust_id="rust_coarse_01", texres="2k", tile=1.45,
                  rust_amount=0.20, paint_variation=0.62, top_height=300.0,
                  grime_amount=0.38):
    """Painted iron with real surface detail, graded by height.

    The asset matters more than any of the tuning below. This used to be built
    on `metal_plate`, which is diamond tread plate: a floor covering, stamped
    with a chevron pattern the tower has nowhere on it. Tiled at 26 cm and
    stripped of its hue - which it had to be, or the ironwork went green - what
    reached the render was a flat grey with a faint regular ripple in it, and
    from any distance at all the whole lattice read as smooth plastic.

    `rusty_painted_metal` is a red-brown painted steel plate with rust bleeding
    down it in vertical streaks, which is what the tower actually looks like
    close up, so its hue is now largely kept rather than thrown away. Tiled at
    1.45 m the streaks run the length of a member instead of repeating four
    times across its width.

    On top of that: a rivet lattice in the bump, and occlusion grime in the
    junctions. Between them they are most of what separates ironwork from a
    shape with a brown material on it.
    """
    maps = assets.fetch_texture(asset_id, texres)
    rust = assets.fetch_texture(rust_id, texres)

    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])

    coord = nt.nodes.new("ShaderNodeTexCoord")
    mapping = nt.nodes.new("ShaderNodeMapping")
    mapping.inputs["Scale"].default_value = (1.0 / tile,) * 3
    nt.links.new(coord.outputs["Object"], mapping.inputs["Vector"])

    rust_map = nt.nodes.new("ShaderNodeMapping")
    rust_map.inputs["Scale"].default_value = (1.0 / (tile * 0.55),) * 3
    nt.links.new(coord.outputs["Object"], rust_map.inputs["Vector"])

    # --- the paint's own brown, graded from the bottom of the tower up -------
    geo = nt.nodes.new("ShaderNodeNewGeometry")
    sep = nt.nodes.new("ShaderNodeSeparateXYZ")
    height = nt.nodes.new("ShaderNodeMapRange")
    # Map Range carries a second, vector-typed set of sockets with the same
    # names, so address the float ones by index.
    height.inputs[1].default_value = 0.0
    height.inputs[2].default_value = top_height
    shade = nt.nodes.new("ShaderNodeValToRGB")
    shade.color_ramp.elements[0].position = 0.0
    shade.color_ramp.elements[0].color = SHADE_LOW
    shade.color_ramp.elements[1].position = 1.0
    shade.color_ramp.elements[1].color = SHADE_HIGH
    shade.color_ramp.elements.new(0.45).color = SHADE_MID
    nt.links.new(geo.outputs["Position"], sep.inputs["Vector"])
    nt.links.new(sep.outputs["Z"], height.inputs["Value"])
    nt.links.new(height.outputs["Result"], shade.inputs["Fac"])

    # --- scanned paint, kept close to its own hue and re-graded -------------
    diff = _box_texture(nt, mapping, maps["diffuse"], False)
    tuned = nt.nodes.new("ShaderNodeHueSaturation")
    # The scan is a warmer, redder brown than the tower's own; pulling the
    # saturation back part of the way lands on the paint colour without losing
    # the streaks, which are the part worth having.
    #
    # Value is what makes this work and it is not a brightness control. The
    # scan is multiplied into the paint colour, so unless its mean sits at 1.0
    # the multiply darkens as well as modulates. The scan's own mean is about
    # 0.28 linear; leaving it there took the paint down to an albedo of 0.048 -
    # near-black - and at that level the sky's reflection off the surface is
    # brighter than anything the diffuse contributes, so the lattice rendered
    # neutral grey however brown its base colour was. Measured off the ironwork
    # probe: four per cent saturation. 3.6 puts the mean back at 1.
    tuned.inputs["Saturation"].default_value = 0.55
    tuned.inputs["Value"].default_value = 3.6
    nt.links.new(diff.outputs["Color"], tuned.inputs["Color"])

    painted = nt.nodes.new("ShaderNodeMixRGB")
    painted.blend_type = "MULTIPLY"
    painted.inputs["Fac"].default_value = paint_variation
    nt.links.new(shade.outputs["Color"], painted.inputs["Color1"])
    nt.links.new(tuned.outputs["Color"], painted.inputs["Color2"])

    # --- rust, biased onto upward faces where water and dirt sit ------------
    rust_col = _box_texture(nt, rust_map, rust["diffuse"], False)
    upward = nt.nodes.new("ShaderNodeSeparateXYZ")
    nt.links.new(geo.outputs["Normal"], upward.inputs["Vector"])
    up_range = nt.nodes.new("ShaderNodeMapRange")
    up_range.inputs[1].default_value = -0.2
    up_range.inputs[2].default_value = 1.0
    up_range.inputs[3].default_value = 0.05
    up_range.inputs[4].default_value = 1.0
    nt.links.new(upward.outputs["Z"], up_range.inputs["Value"])

    patch = nt.nodes.new("ShaderNodeTexNoise")
    patch.inputs["Scale"].default_value = 3.0
    patch.inputs["Detail"].default_value = 6.0
    nt.links.new(rust_map.outputs["Vector"], patch.inputs["Vector"])
    patch_ramp = nt.nodes.new("ShaderNodeValToRGB")
    patch_ramp.color_ramp.elements[0].position = 0.45
    patch_ramp.color_ramp.elements[1].position = 0.72
    nt.links.new(patch.outputs["Fac"], patch_ramp.inputs["Fac"])

    mask = nt.nodes.new("ShaderNodeMath")
    mask.operation = "MULTIPLY"
    nt.links.new(patch_ramp.outputs["Color"], mask.inputs[0])
    nt.links.new(up_range.outputs["Result"], mask.inputs[1])
    mask_amt = nt.nodes.new("ShaderNodeMath")
    mask_amt.operation = "MULTIPLY"
    mask_amt.inputs[1].default_value = rust_amount
    nt.links.new(mask.outputs["Value"], mask_amt.inputs[0])

    base = nt.nodes.new("ShaderNodeMixRGB")
    base.blend_type = "MIX"
    nt.links.new(mask_amt.outputs["Value"], base.inputs["Fac"])
    nt.links.new(painted.outputs["Color"], base.inputs["Color1"])
    nt.links.new(rust_col.outputs["Color"], base.inputs["Color2"])
    tail = base.outputs["Color"]

    # --- grime in the junctions --------------------------------------------
    ao = _grime(nt)
    if ao is not None:
        dirt = nt.nodes.new("ShaderNodeValToRGB")
        dirt.color_ramp.elements[0].position = 0.0
        dirt.color_ramp.elements[0].color = (1.0 - grime_amount,) * 3 + (1.0,)
        dirt.color_ramp.elements[1].position = 0.85
        dirt.color_ramp.elements[1].color = (1.0, 1.0, 1.0, 1.0)
        nt.links.new(ao.outputs["AO"], dirt.inputs["Fac"])
        shaded = nt.nodes.new("ShaderNodeMixRGB")
        shaded.blend_type = "MULTIPLY"
        shaded.inputs["Fac"].default_value = 1.0
        nt.links.new(tail, shaded.inputs["Color1"])
        nt.links.new(dirt.outputs["Color"], shaded.inputs["Color2"])
        tail = shaded.outputs["Color"]
    nt.links.new(tail, bsdf.inputs["Base Color"])

    # --- roughness and relief ----------------------------------------------
    #
    # Deliberately NOT from the scan. rusty_painted_metal is photographed off a
    # shipping container: its normal and roughness maps are a wall of vertical
    # corrugations 20 cm apart, and box-projected onto the tower they turned
    # every member into ribbed sheet metal. Its diffuse - flat paint with rust
    # running down it - is the only channel worth taking.
    rough_noise = nt.nodes.new("ShaderNodeTexNoise")
    rough_noise.inputs["Detail"].default_value = 4.0
    nt.links.new(mapping.outputs["Vector"], rough_noise.inputs["Vector"])
    rmap = nt.nodes.new("ShaderNodeMapRange")
    rmap.inputs[3].default_value = 0.47
    rmap.inputs[4].default_value = 0.66
    nt.links.new(rough_noise.outputs["Fac"], rmap.inputs["Value"])
    nt.links.new(rmap.outputs["Result"], bsdf.inputs["Roughness"])

    # A fine bump for the brushed, repainted surface itself, under the rivets.
    grain = nt.nodes.new("ShaderNodeTexNoise")
    grain.inputs["Scale"].default_value = 42.0
    grain.inputs["Detail"].default_value = 6.0
    nt.links.new(mapping.outputs["Vector"], grain.inputs["Vector"])
    grain_bump = nt.nodes.new("ShaderNodeBump")
    grain_bump.inputs["Strength"].default_value = 0.28
    grain_bump.inputs["Distance"].default_value = 0.004
    nt.links.new(grain.outputs["Fac"], grain_bump.inputs["Height"])

    rivet_bump, _ = _rivets(nt, coord)
    nt.links.new(grain_bump.outputs["Normal"], rivet_bump.inputs["Normal"])
    nt.links.new(rivet_bump.outputs["Normal"], bsdf.inputs["Normal"])

    # Painted iron, not bare metal. Any metallic weight mirrors the sky across
    # the whole lattice and washes the tower out to a blue-grey haze; the
    # specular level sits below the 0.5 default for the same reason.
    bsdf.inputs["Metallic"].default_value = 0.0
    # Measured against Commons close-ups, sunlit ironwork sits near saturation
    # 0.20; at specular 0.16 the lit faces came back at 0.14, because what the
    # sheen reflects is a neutral sky and every bit of it dilutes the brown.
    bsdf.inputs["Specular IOR Level"].default_value = 0.09
    return mat


def stone_material(name="EiffelPier", asset_id="large_sandstone_blocks",
                   texres="4k", tile=1.4, tint=(0.560, 0.505, 0.400, 1.0),
                   splash=(0.230, 0.205, 0.165, 1.0), splash_height=2.6,
                   soot=0.18):
    """Box-projected masonry for the piers the tower stands on.

    The imported mesh models them as part of the ironwork, so without this they
    inherit the brown paint and read as giant cardboard boxes.

    Three things beyond the scan, all of which the piers were missing:

    - a warm tint. At (0.62, 0.60, 0.55) the stone was all but neutral and,
      lit by a sky that is itself blue, came back reading cold grey-blue
      against the tower's brown. Real Paris limestone is a warm cream.
    - a dark band at the foot. Every masonry plinth in a public square has one,
      from rain splash and street dirt, and its absence is why the piers looked
      like they had been dropped onto the ground rather than standing in it.
    - occlusion soot, so the recessed courses and the cornice undersides are
      not the same value as the faces that catch the sky. Kept light: the
      imported piers carry a lot of stepped panelling, and at 0.30 every recess
      in it went near black and the stonework read as a chequerboard.

    The tile also came down from 2.2 m to 1.4, which is nearer the size of the
    real ashlar.
    """
    maps = assets.fetch_texture(asset_id, texres)
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    bsdf.inputs["Metallic"].default_value = 0.0
    bsdf.inputs["Specular IOR Level"].default_value = 0.12
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])

    coord = nt.nodes.new("ShaderNodeTexCoord")
    mapping = nt.nodes.new("ShaderNodeMapping")
    mapping.inputs["Scale"].default_value = (1.0 / tile,) * 3
    nt.links.new(coord.outputs["Object"], mapping.inputs["Vector"])

    diff = _box_texture(nt, mapping, maps["diffuse"], False)

    # Break the tile before anything else touches it. A 1.4 m masonry tile
    # box-projected onto a 25 m pier repeats eighteen times across one face,
    # and because the scan has a few conspicuously dark blocks in it the repeat
    # reads as a deliberate staircase pattern climbing the stonework. A second
    # copy at an incommensurate scale, blended through low frequency noise,
    # breaks the arrangement rather than just softening its seams.
    second_map = nt.nodes.new("ShaderNodeMapping")
    second_map.inputs["Scale"].default_value = (1.0 / (tile * 2.37),) * 3
    second_map.inputs["Location"].default_value = (11.3, 4.7, 2.9)
    nt.links.new(coord.outputs["Object"], second_map.inputs["Vector"])
    second = _box_texture(nt, second_map, maps["diffuse"], False)
    blend_map = nt.nodes.new("ShaderNodeMapping")
    blend_map.inputs["Scale"].default_value = (0.06,) * 3
    nt.links.new(coord.outputs["Object"], blend_map.inputs["Vector"])
    blend_noise = nt.nodes.new("ShaderNodeTexNoise")
    blend_noise.inputs["Detail"].default_value = 3.0
    nt.links.new(blend_map.outputs["Vector"], blend_noise.inputs["Vector"])
    broken = nt.nodes.new("ShaderNodeMixRGB")
    broken.blend_type = "OVERLAY"
    nt.links.new(blend_noise.outputs["Fac"], broken.inputs["Fac"])
    nt.links.new(diff.outputs["Color"], broken.inputs["Color1"])
    nt.links.new(second.outputs["Color"], broken.inputs["Color2"])

    tinted = nt.nodes.new("ShaderNodeMixRGB")
    tinted.blend_type = "MULTIPLY"
    tinted.inputs["Fac"].default_value = 1.0
    tinted.inputs["Color2"].default_value = tint
    nt.links.new(broken.outputs["Color"], tinted.inputs["Color1"])
    tail = tinted.outputs["Color"]

    # Splash band at the foot.
    geo = nt.nodes.new("ShaderNodeNewGeometry")
    sep = nt.nodes.new("ShaderNodeSeparateXYZ")
    nt.links.new(geo.outputs["Position"], sep.inputs["Vector"])
    band = nt.nodes.new("ShaderNodeMapRange")
    band.inputs[1].default_value = 0.0
    band.inputs[2].default_value = splash_height
    band.clamp = True
    nt.links.new(sep.outputs["Z"], band.inputs["Value"])
    # Ragged, not a ruled line: the dirt line on a plinth follows the stone.
    edge = nt.nodes.new("ShaderNodeTexNoise")
    edge.inputs["Scale"].default_value = 5.0
    edge.inputs["Detail"].default_value = 5.0
    nt.links.new(mapping.outputs["Vector"], edge.inputs["Vector"])
    jitter = nt.nodes.new("ShaderNodeMath")
    jitter.operation = "MULTIPLY_ADD"
    jitter.inputs[1].default_value = 0.34
    nt.links.new(edge.outputs["Fac"], jitter.inputs[0])
    nt.links.new(band.outputs["Result"], jitter.inputs[2])
    band_ramp = nt.nodes.new("ShaderNodeValToRGB")
    band_ramp.color_ramp.elements[0].position = 0.10
    band_ramp.color_ramp.elements[0].color = splash
    band_ramp.color_ramp.elements[1].position = 0.62
    band_ramp.color_ramp.elements[1].color = (1.0, 1.0, 1.0, 1.0)
    nt.links.new(jitter.outputs["Value"], band_ramp.inputs["Fac"])
    dirty = nt.nodes.new("ShaderNodeMixRGB")
    dirty.blend_type = "MULTIPLY"
    dirty.inputs["Fac"].default_value = 1.0
    nt.links.new(tail, dirty.inputs["Color1"])
    nt.links.new(band_ramp.outputs["Color"], dirty.inputs["Color2"])
    tail = dirty.outputs["Color"]

    ao = _grime(nt, distance=0.45)
    if ao is not None:
        shade = nt.nodes.new("ShaderNodeValToRGB")
        shade.color_ramp.elements[0].position = 0.0
        shade.color_ramp.elements[0].color = (1.0 - soot,) * 3 + (1.0,)
        shade.color_ramp.elements[1].position = 0.8
        shade.color_ramp.elements[1].color = (1.0, 1.0, 1.0, 1.0)
        nt.links.new(ao.outputs["AO"], shade.inputs["Fac"])
        sooted = nt.nodes.new("ShaderNodeMixRGB")
        sooted.blend_type = "MULTIPLY"
        sooted.inputs["Fac"].default_value = 1.0
        nt.links.new(tail, sooted.inputs["Color1"])
        nt.links.new(shade.outputs["Color"], sooted.inputs["Color2"])
        tail = sooted.outputs["Color"]
    nt.links.new(tail, bsdf.inputs["Base Color"])

    if "rough" in maps:
        rough = _box_texture(nt, mapping, maps["rough"], True)
        nt.links.new(rough.outputs["Color"], bsdf.inputs["Roughness"])
    if "normal" in maps:
        nrm = _box_texture(nt, mapping, maps["normal"], True)
        nmap = nt.nodes.new("ShaderNodeNormalMap")
        nt.links.new(nrm.outputs["Color"], nmap.inputs["Color"])
        nt.links.new(nmap.outputs["Normal"], bsdf.inputs["Normal"])
    return mat
