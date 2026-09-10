"""Painted ironwork material for the tower.

The first version of this was procedural - a height gradient plus a noise bump -
and it rendered like moulded plastic. Real ironwork carries surface detail at a
scale you can see from a few metres away: brush texture in the paint, rivet
heads, rust bleeding out of seams, grime settled on every upward face. None of
that survives as a flat colour, and a splat trained on flat colour stays flat.

So the paint is built from a real scanned PBR set, box projected because the
generated bars carry no UVs, and then pushed to the tower's own brown. Poly
Haven is CC0, so nothing here needs attribution.
"""

from __future__ import annotations

import bpy

import assets

# The tower's paint has always been applied in graduated shades, darkest at the
# bottom, so the whole thing reads as one colour from the ground.
# Brighter than a paint chip would suggest: a dense lattice self-shadows
# heavily, so a scan-accurate albedo renders as a silhouette from the ground.
SHADE_LOW = (0.082, 0.054, 0.030, 1.0)
SHADE_MID = (0.145, 0.098, 0.056, 1.0)
SHADE_HIGH = (0.225, 0.156, 0.092, 1.0)


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


def iron_material(name="EiffelIron", asset_id="metal_plate",
                  rust_id="rust_coarse_01", texres="2k", tile=0.26,
                  rust_amount=0.22, paint_variation=0.30, top_height=300.0):
    """Painted iron with real surface detail, graded by height.

    Realism here comes from the normal and roughness maps, not from the
    diffuse. Painted steel is close to a flat colour; what the eye reads is the
    micro-relief of plate seams and rivets and the way sheen varies across
    them. An early version multiplied the full scanned diffuse into the paint
    and the streaks in it box-projected into something that looked like planed
    timber, so the colour variation is now deliberately slight.
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
    rust_map.inputs["Scale"].default_value = (1.0 / (tile * 3.1),) * 3
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

    # --- scanned paint, stripped of its own hue and re-tinted ---------------
    diff = _box_texture(nt, mapping, maps["diffuse"], False)
    desat = nt.nodes.new("ShaderNodeHueSaturation")
    # Keep only a hint of the scan's light and dark, and none of its hue.
    desat.inputs["Saturation"].default_value = 0.0
    desat.inputs["Value"].default_value = 1.7
    nt.links.new(diff.outputs["Color"], desat.inputs["Color"])

    painted = nt.nodes.new("ShaderNodeMixRGB")
    painted.blend_type = "MULTIPLY"
    painted.inputs["Fac"].default_value = paint_variation
    nt.links.new(shade.outputs["Color"], painted.inputs["Color1"])
    nt.links.new(desat.outputs["Color"], painted.inputs["Color2"])

    # --- rust, biased onto upward faces where water and dirt sit ------------
    rust_col = _box_texture(nt, rust_map, rust[
        "diffuse"], False)
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
    nt.links.new(base.outputs["Color"], bsdf.inputs["Base Color"])

    # --- roughness and normal ----------------------------------------------
    if "rough" in maps:
        rough = _box_texture(nt, mapping, maps["rough"], True)
        rmap = nt.nodes.new("ShaderNodeMapRange")
        rmap.inputs[3].default_value = 0.42
        rmap.inputs[4].default_value = 0.78
        nt.links.new(rough.outputs["Color"], rmap.inputs["Value"])
        nt.links.new(rmap.outputs["Result"], bsdf.inputs["Roughness"])
    else:
        bsdf.inputs["Roughness"].default_value = 0.62

    if "normal" in maps:
        nrm = _box_texture(nt, mapping, maps["normal"], True)
        nmap = nt.nodes.new("ShaderNodeNormalMap")
        nmap.inputs["Strength"].default_value = 1.1
        nt.links.new(nrm.outputs["Color"], nmap.inputs["Color"])
        nt.links.new(nmap.outputs["Normal"], bsdf.inputs["Normal"])

    # Painted iron, not bare metal. Any metallic weight mirrors the sky across
    # the whole lattice and washes the tower out to a blue-grey haze; the
    # specular level sits below the 0.5 default for the same reason.
    bsdf.inputs["Metallic"].default_value = 0.0
    bsdf.inputs["Specular IOR Level"].default_value = 0.22
    return mat


def stone_material(name="EiffelPier", asset_id="large_sandstone_blocks",
                   texres="4k", tile=2.2, tint=(0.62, 0.60, 0.55, 1.0)):
    """Box-projected masonry for the piers the tower stands on.

    The imported mesh models them as part of the ironwork, so without this they
    inherit the brown paint and read as giant cardboard boxes.
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
    tinted = nt.nodes.new("ShaderNodeMixRGB")
    tinted.blend_type = "MULTIPLY"
    tinted.inputs["Fac"].default_value = 1.0
    tinted.inputs["Color2"].default_value = tint
    nt.links.new(diff.outputs["Color"], tinted.inputs["Color1"])
    nt.links.new(tinted.outputs["Color"], bsdf.inputs["Base Color"])

    if "rough" in maps:
        rough = _box_texture(nt, mapping, maps["rough"], True)
        nt.links.new(rough.outputs["Color"], bsdf.inputs["Roughness"])
    if "normal" in maps:
        nrm = _box_texture(nt, mapping, maps["normal"], True)
        nmap = nt.nodes.new("ShaderNodeNormalMap")
        nt.links.new(nrm.outputs["Color"], nmap.inputs["Color"])
        nt.links.new(nmap.outputs["Normal"], bsdf.inputs["Normal"])
    return mat
