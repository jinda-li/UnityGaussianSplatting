"""Render the whole look-dev set in one Blender load.

Loading scene_hdri.blend costs about half a minute and the file is a third of a
gigabyte, so checking four things after a change used to mean four loads. These
are the frames worth looking at after any change to the dressing:

    blender --background scene.blend --python probes.py -- --out DIR
    blender --background scene.blend --python probes.py -- --out DIR --only hero,ground

`hero` is the frame the scene is tuned for (scene.HERO_POS); the rest exist to
catch the things it cannot show - what the ground looks like underfoot, whether
the ironwork holds up close, whether the middle distance has anything in it.
"""

import os
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from blenderutil import aim, camera  # noqa: E402
from scene import HERO_LENS, HERO_LOOK, HERO_POS  # noqa: E402

# name -> (position, target, lens, aspect)
PROBES = {
    # The frame everything is tuned against.
    "hero": (HERO_POS, HERO_LOOK, HERO_LENS, 1.25),
    # Down the lawn: the formal rows, the perspective they make, and the
    # tower at a distance where its silhouette is what carries it.
    "vista": ((0.0, -280.0, 1.65), (0.0, 0.0, 110.0), 24.0, 1.25),
    # Underfoot. Grass geometry, ground texture scale, the lawn edge.
    "ground": ((26.0, -104.0, 1.55), (14.0, -88.0, 0.0), 40.0, 0.75),
    # The ironwork at the distance a visitor first reads it as metal rather
    # than as a pattern.
    "iron": ((44.0, -70.0, 1.65), (26.0, -34.0, 46.0), 55.0, 1.0),
    # The middle distance: esplanade, fence, kiosks, tree line, the city
    # behind. Everything that is not the tower and not underfoot.
    "middle": ((34.0, -116.0, 1.65), (6.0, -40.0, 4.0), 85.0, 1.25),
    # Close enough to a pier to check rivets and paint texture - roughly the
    # distance a visitor stands at the foot of a leg.
    "rivet": ((74.0, -74.0, 1.70), (57.0, -57.0, 14.0), 135.0, 1.0),
    # The esplanade band: gravel walk, hoop fence, security line. Shot along
    # the ground so the surfaces are seen at the grazing angle that shows up
    # tiling and washed-out colour.
    "esplanade": ((0.0, -84.0, 1.60), (34.0, -66.0, 0.6), 50.0, 0.75),
    # Straight up inside the arch, which is where a player who has just landed
    # actually looks first.
    "arch": ((8.0, -18.0, 1.65), (0.0, 6.0, 120.0), 16.0, 1.25),
}

DEFAULT = ("hero", "vista", "ground", "iron", "middle")


def args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    cfg = {"out": ".", "res": 900, "samples": 96, "only": "", "tag": ""}
    for i, a in enumerate(argv):
        key = a.lstrip("-")
        if key in cfg and i + 1 < len(argv):
            cfg[key] = type(cfg[key])(argv[i + 1])
    return cfg


def patch_colours(path, patches):
    """Average colour of a few named boxes, as 0-255 triples.

    A render written to PNG is display-referred and Blender hands the buffer
    back the same way, so these scale straight to 0-255 with no transfer
    function - applying one as well reported the lawn at 201 when the file held
    147.
    """
    img = bpy.data.images.load(path)
    w, h = img.size
    px = img.pixels[:]
    out = []
    for name, fx, fy in patches:
        x0, y0 = int(fx * w), int((1.0 - fy) * h) - 6
        acc, n = [0.0, 0.0, 0.0], 0
        for y in range(max(y0, 0), min(y0 + 6, h)):
            for x in range(max(x0, 0), min(x0 + 6, w)):
                i = (y * w + x) * 4
                for c in range(3):
                    acc[c] += px[i + c]
                n += 1
        if n:
            out.append((name, tuple(int(round(min(max(c / n, 0.0), 1.0) * 255))
                                    for c in acc)))
    bpy.data.images.remove(img)
    return out


# The sky patch sits on the side away from the sun. Sampled on the sun's side
# it reads the glow rather than the sky, and every change to the sky measures
# the same.
HERO_PATCHES = [("sky", 0.88, 0.05), ("iron", 0.48, 0.27),
                ("stone", 0.78, 0.78), ("lawn_far", 0.50, 0.83),
                ("lawn_near", 0.50, 0.96)]


def main():
    cfg = args()
    scene = bpy.context.scene
    names = [n.strip() for n in cfg["only"].split(",") if n.strip()] or DEFAULT

    scene.render.engine = "CYCLES"
    scene.cycles.samples = cfg["samples"]
    scene.cycles.use_denoising = True
    scene.cycles.device = "GPU"
    prefs = bpy.context.preferences.addons.get("cycles")
    if prefs:
        prefs.preferences.compute_device_type = "OPTIX"
        prefs.preferences.get_devices()
        for dev in prefs.preferences.devices:
            dev.use = dev.type != "CPU"
    # The scene does not change between probes, only the camera, so let Cycles
    # keep its BVH instead of rebuilding it five times.
    scene.render.use_persistent_data = True

    cam_data = camera("ProbeCam", 50.0)
    cam = bpy.data.objects.new("ProbeCam", cam_data)
    scene.collection.objects.link(cam)
    scene.camera = cam

    print("[probe] view %s exposure %+.2f"
          % (scene.view_settings.view_transform, scene.view_settings.exposure))
    for name in names:
        if name not in PROBES:
            print("[probe] unknown probe %r" % name)
            continue
        pos, look, lens, aspect = PROBES[name]
        cam_data.lens = lens
        cam.location = pos
        aim(cam, look)
        scene.render.resolution_x = cfg["res"]
        scene.render.resolution_y = int(cfg["res"] * aspect)
        out = os.path.join(os.path.abspath(cfg["out"]),
                           "%s%s.png" % (name, cfg["tag"]))
        scene.render.filepath = out
        bpy.ops.render.render(write_still=True)
        print("[probe] %s -> %s" % (name, out))
        if name == "hero":
            for patch, rgb in patch_colours(out, HERO_PATCHES):
                print("[probe]   %-10s %3d %3d %3d" % ((patch,) + rgb))


# Guarded: importing this module must not render anything. probes.py in
# particular is imported by other scripts for PROBES and patch_colours, and
# an unguarded call rendered the whole default set into the working
# directory every time - minutes of GPU time and a pile of stray PNGs in
# the source tree.
if __name__ == "__main__":
    main()
