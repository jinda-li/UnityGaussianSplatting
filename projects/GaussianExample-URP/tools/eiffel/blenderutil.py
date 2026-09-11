"""Small helpers shared by the Eiffel scene scripts."""

from __future__ import annotations

import mathutils

# How far a camera in this scene has to be able to see.
#
# Blender's default is 1000 m, and every camera in these scripts was taking it.
# The scene is much bigger than that: the sky dome is a 3 km shell, the
# hinterland ground runs to 4.2 km, Tour Montparnasse stands at 2.3 km. All of
# it was being clipped away, in the look-dev probes and in the training renders
# alike - so the sky dome, whose entire purpose is to give the splat a surface
# to put the sky on, has never appeared in a single frame. It rendered as world
# background instead, which looks similar and trains completely differently.
#
# Nothing costs anything for raising it: Cycles is a ray tracer with no depth
# buffer, so a long clip range has no precision penalty.
CLIP_END = 12000.0


def camera(name, lens, clip_end=CLIP_END):
    """A camera datablock that can actually see this scene."""
    import bpy

    data = bpy.data.cameras.new(name)
    data.lens = lens
    data.clip_end = clip_end
    return data


def aim(obj, target, up="Y"):
    """Point a camera or light at a world-space target.

    Blender cameras look down their local -Z, so the rotation comes from
    tracking that axis onto the direction vector.
    """
    direction = mathutils.Vector(target) - obj.location
    obj.rotation_euler = direction.to_track_quat("-Z", up).to_euler()
