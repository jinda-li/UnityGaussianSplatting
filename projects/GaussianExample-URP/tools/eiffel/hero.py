"""Render the scene's hero frame - the one viewpoint the dressing is tuned for.

The whole point of picking a hero position is that look-dev decisions get made
against the same frame every time, so this takes no camera arguments by
default: run it, compare against the last one, change the scene, run it again.

    blender --background scene.blend --python hero.py -- --out hero.png

It also prints the average colour of a few patches - lawn, ironwork, sky,
masonry - because judging exposure and saturation by eye across two renders a
day apart does not work. Those numbers are what caught the scene sitting a
stop and a half over: sunlit lawn at 201/255 and "brown" paint at six per cent
saturation.
"""

import os
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from blenderutil import aim, camera  # noqa: E402
from scene import HERO_LENS, HERO_LOOK, HERO_POS  # noqa: E402

# Fractions of the frame, so the same patches follow any resolution. Each is
# (name, x, y) of the top-left corner of a small square.
PATCHES = [
    ("sky", 0.07, 0.05),
    ("iron", 0.48, 0.27),
    ("stone", 0.78, 0.78),
    ("lawn_far", 0.50, 0.83),
    ("lawn_near", 0.50, 0.96),
]


def args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    cfg = {"out": "hero.png", "res": 900, "samples": 96, "lens": HERO_LENS,
           "exposure": 99.0}
    for i, a in enumerate(argv):
        key = a.lstrip("-")
        if key in cfg and i + 1 < len(argv):
            cfg[key] = type(cfg[key])(argv[i + 1])
    return cfg


def sample(path, patches=PATCHES, size=6):
    """Average colour of each patch, as 0-255 triples."""
    img = bpy.data.images.load(path)
    w, h = img.size
    px = img.pixels[:]
    out = []
    for name, fx, fy in patches:
        # Blender's pixel buffer starts at the bottom row.
        x0 = int(fx * w)
        y0 = int((1.0 - fy) * h) - size
        acc = [0.0, 0.0, 0.0]
        n = 0
        for y in range(max(y0, 0), min(y0 + size, h)):
            for x in range(max(x0, 0), min(x0 + size, w)):
                i = (y * w + x) * 4
                for c in range(3):
                    acc[c] += px[i + c]
                n += 1
        if not n:
            continue
        # A render written to PNG is already display-referred, and Blender
        # hands the buffer back the same way, so these scale straight to 0-255.
        # Applying an sRGB transfer here as well reported the lawn at 201 when
        # the file actually holds 147.
        out.append((name, tuple(int(round(min(max(c / n, 0.0), 1.0) * 255))
                                for c in acc)))
    bpy.data.images.remove(img)
    return out


def main():
    cfg = args()
    scene = bpy.context.scene

    cam_data = camera("HeroCam", cfg["lens"])
    cam = bpy.data.objects.new("HeroCam", cam_data)
    cam.location = HERO_POS
    scene.collection.objects.link(cam)
    aim(cam, HERO_LOOK)
    scene.camera = cam

    if cfg["exposure"] < 90.0:
        scene.view_settings.exposure = cfg["exposure"]

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
    scene.render.resolution_x = cfg["res"]
    scene.render.resolution_y = int(cfg["res"] * 1.25)
    scene.render.filepath = os.path.abspath(cfg["out"])
    bpy.ops.render.render(write_still=True)

    print("[hero] wrote", scene.render.filepath)
    print("[hero] view %s exposure %+.2f"
          % (scene.view_settings.view_transform, scene.view_settings.exposure))
    for name, rgb in sample(scene.render.filepath):
        print("[hero] %-10s %3d %3d %3d" % ((name,) + rgb))


# Guarded: importing this module must not render anything. probes.py in
# particular is imported by other scripts for PROBES and patch_colours, and
# an unguarded call rendered the whole default set into the working
# directory every time - minutes of GPU time and a pile of stray PNGs in
# the source tree.
if __name__ == "__main__":
    main()
