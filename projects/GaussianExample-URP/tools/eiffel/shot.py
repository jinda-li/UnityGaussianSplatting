"""Render one look at a built scene, for eyeballing it before a full render run.

    blender --background scene.blend --python shot.py -- \
        --pos 90,-60,1.7 --look 0,0,120 --lens 24 --out shot.png
"""

import os
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from blenderutil import aim  # noqa: E402


def args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    cfg = {"pos": "90,-90,1.7", "look": "0,0,110", "lens": 24.0,
           "out": "shot.png", "res": 1200, "samples": 64, "engine": "CYCLES",
           "exposure": 99.0, "view": ""}
    for i, a in enumerate(argv):
        key = a.lstrip("-")
        if key in cfg and i + 1 < len(argv):
            cfg[key] = type(cfg[key])(argv[i + 1])
    return cfg


def vec(s):
    return tuple(float(v) for v in s.split(","))


def main():
    cfg = args()
    scene = bpy.context.scene

    cam_data = bpy.data.cameras.new("ShotCam")
    cam_data.lens = cfg["lens"]
    cam = bpy.data.objects.new("ShotCam", cam_data)
    cam.location = vec(cfg["pos"])
    scene.collection.objects.link(cam)
    aim(cam, vec(cfg["look"]))
    scene.camera = cam

    if cfg["exposure"] < 90.0:
        scene.view_settings.exposure = cfg["exposure"]
    if cfg["view"]:
        scene.view_settings.view_transform = cfg["view"]

    scene.render.engine = cfg["engine"]
    if cfg["engine"] == "CYCLES":
        scene.cycles.samples = cfg["samples"]
        scene.cycles.use_denoising = True
        scene.cycles.device = "GPU"
        prefs = bpy.context.preferences.addons.get("cycles")
        if prefs:
            prefs.preferences.compute_device_type = "OPTIX"
            prefs.preferences.get_devices()
            for dev in prefs.preferences.devices:
                dev.use = dev.type != "CPU"
    else:
        scene.eevee.taa_render_samples = cfg["samples"]
    scene.render.resolution_x = cfg["res"]
    scene.render.resolution_y = int(cfg["res"] * 1.25)
    scene.render.filepath = os.path.abspath(cfg["out"])
    bpy.ops.render.render(write_still=True)
    print("[shot] wrote", scene.render.filepath)


main()
