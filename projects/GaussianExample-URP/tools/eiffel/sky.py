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


# Reference photographs of this view - Commons has several taken from the lawn
# on the tower's axis - are shot under a clear deep blue sky, and in them the
# sky is no brighter than the sunlit lawn beneath it. The partly-cloudy sky
# this used to sit on rendered its zenith at 225/255 against a lawn at 93: two
# and a half times too bright, and washing the ironwork out along with it.
#
# That reverses an earlier decision. A cloudless analytic sky was rejected as
# the loudest "this is CG" cue in an outdoor shot, and for a generic landscape
# that is right. For this one it is not: the postcard view of the Champ de Mars
# is a clear summer sky, and matching the reference beats the general rule.
HDRI = "drakensberg_solitary_mountain_puresky"


# Where the sun should end up in world terms, in degrees measured the usual
# way (0 along +X, 90 along +Y). See setup_hdri for why this value.
SUN_AZIMUTH = 180.0


def sun_direction(image, step=4):
    """Find an HDRI's sun by looking for its brightest pixel.

    Returns (azimuth, elevation) in degrees, in the image's own frame. Blender
    maps a direction to an equirectangular image as

        u = -atan2(d.y, d.x) / 2pi + 0.5
        v =  atan2(d.z, |d.xy|) / pi + 0.5

    so both angles come straight back out of the pixel's coordinates. Sampling
    every `step`th pixel is plenty: the sun is the only thing in a pure-sky
    HDRI within orders of magnitude of its own brightness, and it is several
    pixels across at 4k.
    """
    width, height = image.size
    pixels = image.pixels[:]
    best = (-1.0, 0, 0)
    for y in range(0, height, step):
        row = y * width * 4
        for x in range(0, width, step):
            i = row + x * 4
            lum = pixels[i] + pixels[i + 1] + pixels[i + 2]
            if lum > best[0]:
                best = (lum, x, y)
    _, x, y = best
    u = (x + 0.5) / width
    v = (y + 0.5) / height
    return math.degrees((0.5 - u) * 2.0 * math.pi), math.degrees(
        (v - 0.5) * math.pi)


def rotation_for(image, world_azimuth=SUN_AZIMUTH):
    """The mapping rotation that puts this HDRI's sun at a given world azimuth.

    The mapping node turns the world direction by -rotation before the lookup,
    so world_azimuth = image_azimuth - rotation.
    """
    azimuth, _ = sun_direction(image)
    return (azimuth - world_azimuth) % 360.0


def setup_view(scene, transform="Filmic", exposure=1.3):
    """Tone mapping for both the preview and the training renders.

    The default exposure suits the HDRI lighting; the analytic Sky texture is
    several stops brighter and wants roughly -4.7 instead. This used to sit at
    +2.2, which clipped every sunlit surface: measured off the hero frame the
    lawn came back at 201/255 and the ironwork at 161/255 with six per cent
    saturation - a grey tower on a yellow mat. Pulling it down to +1.3 puts the
    lawn near 135 and lets the paint read brown again. Filmic rolls the
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
               rotation_deg=None):
    """Light the scene from a captured sky instead of the analytic one.

    The Nishita sky is physically correct and completely cloudless, which is
    the single loudest "this is CG" cue in an outdoor shot - a real photograph
    of the Champ de Mars has cloud structure overhead and colour variation
    across the dome. A pure-sky HDRI brings both, plus a sun that is already in
    the right relationship to the skylight.

    The rotation is chosen for the hero viewpoint in scene.HERO_POS. This sun
    sits 47.9 degrees up and, in the HDRI's own frame, at an azimuth of -34.3
    degrees; the mapping node turns the world by -rotation_deg before the
    lookup, so the sun ends up at world azimuth -34.3 - rotation_deg. At the
    old 118 degrees that put the shadow behind the tower, out of every
    ground-level frame - a 324 m structure casting nothing the camera can see.
    rotation_deg defaults to whatever puts the sun at SUN_AZIMUTH. The hero
    camera looks along azimuth 106, so 180 places the sun about 75 degrees off
    to its left: out of frame, but high on the side of the tower that faces the
    camera.

    That last part is what the value is for. The four faces of the lattice have
    to differ in brightness or the tower renders as one flat brown screen -
    which is exactly what happened at 26 degrees, where the sun sat behind the
    structure and every camera-facing surface was in its own shade. Measured on
    the hero frame the ironwork came back at six per cent saturation and almost
    no contrast between faces. It also cost nothing to fix: at 26 the shadow it
    bought fell outside the frame anyway.

    Do not chase a longer shadow with this. 70 degrees was tried, which swings
    the tower's shadow out across the near lawn - and drags the sun disc into
    the top right of the hero frame, flattening the tower into a silhouette. At
    this sun's elevation the shadow tip is under 300 m from the base, so a
    shadow that reaches the foreground is one pointing at the camera, and that
    is the same thing as shooting into the sun.

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
    if rotation_deg is None:
        # Every sky has its sun somewhere different, so a hard-coded rotation
        # silently moves the light the moment the HDRI is swapped. Solve for it
        # instead and the sun lands on SUN_AZIMUTH whichever sky is used.
        rotation_deg = rotation_for(env.image)
        print("[sky] %s: rotation %.1f deg puts the sun at azimuth %.0f"
              % (asset_id, rotation_deg, SUN_AZIMUTH))
    mapping.inputs["Rotation"].default_value = (0.0, 0.0,
                                                math.radians(rotation_deg))
    nt.links.new(coord.outputs["Generated"], mapping.inputs["Vector"])
    nt.links.new(mapping.outputs["Vector"], env.inputs["Vector"])
    nt.links.new(env.outputs["Color"], bg.inputs["Color"])
    bg.inputs["Strength"].default_value = strength
    nt.links.new(bg.outputs["Background"], out.inputs["Surface"])
    return env
