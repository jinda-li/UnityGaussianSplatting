"""Daylight rig shared by the preview and the training renders.

Lighting comes entirely from the Sky texture, including its own sun disc. That
is the point of the Nishita model: the disc and the scattered sky are generated
together, so their ratio is physically consistent.

An earlier version switched the disc off and lit the scene with a separate Sun
lamp instead. It looked plausible but was badly out of balance - measured on
open ground, the sky alone rendered the sand at 217/255 while the sun alone
managed 122/255, so roughly three quarters of the light was flat blue skylight.
Every surface came out neutral grey: the sand lost its brown, the ironwork lost
its rust, and that washed-out colour would have been baked into the splat.
"""

from __future__ import annotations

import math

import bpy

import assets


HDRI = "kloofendal_48d_partly_cloudy_puresky"


def setup_view(scene, transform="Filmic", exposure=2.2):
    """Tone mapping for both the preview and the training renders.

    The default exposure suits the HDRI lighting; the analytic Sky texture is
    several stops brighter and wants roughly -3.8 instead. Filmic rolls the
    bright sky off without AgX's heavy desaturation, which matters because
    whatever hue survives here is what the splat ends up storing.
    """
    scene.view_settings.exposure = exposure
    for candidate in (transform, "Filmic", "Standard"):
        try:
            scene.view_settings.view_transform = candidate
            return candidate
        except TypeError:
            continue
    return None


def setup_daylight(scene, elevation_deg=58.0, azimuth_deg=135.0,
                   sky_strength=1.0, turbidity=3.0, sun_size_deg=0.9):
    """Midday sun and sky from one physically consistent source."""
    elevation = math.radians(elevation_deg)
    azimuth = math.radians(azimuth_deg)

    world = bpy.data.worlds.new("Daylight")
    scene.world = world
    world.use_nodes = True
    nt = world.node_tree
    nt.nodes.clear()
    bg = nt.nodes.new("ShaderNodeBackground")
    sky = nt.nodes.new("ShaderNodeTexSky")
    out = nt.nodes.new("ShaderNodeOutputWorld")
    sky.sky_type = "MULTIPLE_SCATTERING"
    sky.sun_elevation = elevation
    sky.sun_rotation = azimuth
    sky.sun_disc = True
    # A slightly enlarged disc spares Cycles from chasing a half-degree light
    # source; the extra shadow softening is invisible at this scale.
    sky.sun_size = math.radians(sun_size_deg)
    sky.turbidity = turbidity
    bg.inputs["Strength"].default_value = sky_strength
    nt.links.new(sky.outputs["Color"], bg.inputs["Color"])
    nt.links.new(bg.outputs["Background"], out.inputs["Surface"])
    return sky


def setup_hdri(scene, asset_id=HDRI, res="4k", strength=1.0,
               rotation_deg=118.0):
    """Light the scene from a captured sky instead of the analytic one.

    The Nishita sky is physically correct and completely cloudless, which is
    the single loudest "this is CG" cue in an outdoor shot - a real photograph
    of the Champ de Mars has cloud structure overhead and colour variation
    across the dome. A pure-sky HDRI brings both, plus a sun that is already in
    the right relationship to the skylight.

    Returns the environment texture node so the sky dome can sample the same
    image and stay consistent with the background.
    """
    path = assets.fetch_hdri(asset_id, res)
    world = bpy.data.worlds.new("DaylightHDRI")
    scene.world = world
    world.use_nodes = True
    nt = world.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputWorld")
    bg = nt.nodes.new("ShaderNodeBackground")
    env = nt.nodes.new("ShaderNodeTexEnvironment")
    env.image = bpy.data.images.load(path, check_existing=True)
    coord = nt.nodes.new("ShaderNodeTexCoord")
    mapping = nt.nodes.new("ShaderNodeMapping")
    mapping.inputs["Rotation"].default_value = (0.0, 0.0,
                                                math.radians(rotation_deg))
    nt.links.new(coord.outputs["Generated"], mapping.inputs["Vector"])
    nt.links.new(mapping.outputs["Vector"], env.inputs["Vector"])
    nt.links.new(env.outputs["Color"], bg.inputs["Color"])
    bg.inputs["Strength"].default_value = strength
    nt.links.new(bg.outputs["Background"], out.inputs["Surface"])
    return env
