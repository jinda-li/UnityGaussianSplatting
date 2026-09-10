"""Quick look at the procedural tower on its own.

    blender --background --python preview.py -- --detail 0.5 --out preview.png
"""

import math
import os
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import eiffel_tower  # noqa: E402
from blenderutil import aim  # noqa: E402
from sky import setup_daylight, setup_view  # noqa: E402


def args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    cfg = {"detail": 0.5, "out": "preview.png", "res": 960, "engine": "EEVEE",
           "dist": 420.0, "height": 120.0, "samples": 24, "lens": 35.0,
           "target": 140.0}
    for i, a in enumerate(argv):
        key = a.lstrip("-")
        if key in cfg and i + 1 < len(argv):
            cfg[key] = type(cfg[key])(argv[i + 1])
    return cfg


def main():
    cfg = args()
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o, do_unlink=True)

    eiffel_tower.build_tower(detail=cfg["detail"], built_up=cfg["detail"] >= 0.6)

    scene = bpy.context.scene
    setup_daylight(scene)

    cam_data = bpy.data.cameras.new("Cam")
    cam_data.lens = cfg["lens"]
    cam = bpy.data.objects.new("Cam", cam_data)
    d, h = cfg["dist"], cfg["height"]
    cam.location = (d * 0.72, -d * 0.72, h)
    aim(cam, (0.0, 0.0, cfg["target"]))
    scene.collection.objects.link(cam)
    scene.camera = cam

    scene.render.engine = ("BLENDER_EEVEE" if cfg["engine"] == "EEVEE"
                           else "CYCLES")
    if scene.render.engine == "CYCLES":
        scene.cycles.samples = cfg["samples"]
        scene.cycles.device = "GPU"
    else:
        scene.eevee.taa_render_samples = cfg["samples"]
    scene.render.resolution_x = cfg["res"]
    scene.render.resolution_y = int(cfg["res"] * 1.25)
    scene.render.film_transparent = False
    setup_view(scene)
    scene.render.filepath = os.path.abspath(cfg["out"])
    bpy.ops.render.render(write_still=True)
    print("[preview] wrote", scene.render.filepath)


main()
