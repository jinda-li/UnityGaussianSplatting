"""Small helpers shared by the Eiffel scene scripts."""

from __future__ import annotations

import mathutils


def aim(obj, target, up="Y"):
    """Point a camera or light at a world-space target.

    Blender cameras look down their local -Z, so the rotation comes from
    tracking that axis onto the direction vector.
    """
    direction = mathutils.Vector(target) - obj.location
    obj.rotation_euler = direction.to_track_quat("-Z", up).to_euler()
