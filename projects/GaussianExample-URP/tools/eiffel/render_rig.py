"""Renders the training set for the Eiffel splat and writes it as a COLMAP
dataset, ready for `gsplat`'s simple_trainer.

Because the images are synthetic the camera poses are exact, so there is no
structure-from-motion step at all: the poses go straight into
`sparse/0/images.txt`. That removes the usual source of drift and, more
importantly, lets the initial point cloud come from a direct ray cast instead
of sparse feature matches. Feature matching is what normally fails on the tower
- a thin lattice against a blank sky gives SfM almost nothing to hold on to.

--mode object renders the tower on its own, for the miniature the player holds.

    blender --background scene.blend --python render_rig.py -- \
        --out C:/.../Eiffel/dataset --res 1600 --samples 128

Add --points-only to rebuild just the initial cloud.
"""

from __future__ import annotations

import math
import os
import random
import sys

import bpy
import mathutils

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from blenderutil import aim  # noqa: E402

# Blender's camera looks down -Z with +Y up; COLMAP's looks down +Z with +Y
# down. This flip converts between the two.
FLIP = mathutils.Matrix.Diagonal((1.0, -1.0, -1.0)).to_4x4()

TOWER_TOP = 330.0


def camera_poses():
    """Ground level rings plus elevated arcs.

    The rings are what the viewer will actually stand in, so they get the
    densest coverage; the elevated arcs exist to give the upper two thirds of
    the tower the parallax it needs, since from the ground every view of the
    spire is nearly the same ray.
    """
    poses = []

    # (radius, height, count, target height, lens)
    rings = [
        (92.0, 1.65, 84, 150.0, 20.0),
        (92.0, 1.65, 42, 20.0, 35.0),
        (66.0, 1.65, 72, 170.0, 18.0),
        (44.0, 1.65, 60, 190.0, 16.0),
        (26.0, 1.65, 48, 210.0, 14.0),
        (12.0, 1.65, 36, 260.0, 14.0),
        (78.0, 12.0, 48, 160.0, 24.0),
        (58.0, 30.0, 40, 180.0, 28.0),
    ]
    for radius, height, count, target, lens in rings:
        for i in range(count):
            a = 2.0 * math.pi * (i + 0.37 * (radius % 7)) / count
            pos = (math.cos(a) * radius, math.sin(a) * radius, height)
            poses.append((pos, (0.0, 0.0, target), lens))

    # Elevated arcs: a drone-style shell around the shaft.
    for height, radius, count, lens in ((70.0, 150.0, 40, 35.0),
                                        (140.0, 175.0, 36, 35.0),
                                        (210.0, 150.0, 32, 35.0),
                                        (300.0, 120.0, 24, 35.0)):
        for i in range(count):
            a = 2.0 * math.pi * (i + 0.5) / count
            pos = (math.cos(a) * radius, math.sin(a) * radius, height)
            poses.append((pos, (0.0, 0.0, height * 0.55), lens))

    # Tight shells hugging the shaft. The drone arcs above sit 120-175 m out,
    # which leaves the spire - the thinnest structure in the scene - about 150 m
    # from every camera that ever looks at it, and it reconstructs as mush. These
    # rings put a camera within a few tens of metres of each section instead.
    for height, radius, count in ((130.0, 34.0, 28),
                                  (175.0, 28.0, 28),
                                  (220.0, 24.0, 24),
                                  (262.0, 20.0, 24),
                                  (292.0, 18.0, 20),
                                  (316.0, 16.0, 16)):
        for i in range(count):
            a = 2.0 * math.pi * (i + 0.23 * height) / count
            pos = (math.cos(a) * radius, math.sin(a) * radius, height)
            poses.append((pos, (0.0, 0.0, height + 14.0), 28.0))

    # A few looking outward and down so the ground and the tree line are not
    # only ever seen edge-on.
    for i in range(28):
        a = 2.0 * math.pi * i / 28.0
        r = 40.0
        pos = (math.cos(a) * r, math.sin(a) * r, 22.0)
        look = (math.cos(a) * 150.0, math.sin(a) * 150.0, 0.0)
        poses.append((pos, look, 30.0))
    return poses


def object_poses():
    """Hemisphere around the tower alone, for the splat the player holds.

    Held at roughly arm's length a 0.3 m model subtends the same angle as the
    real 330 m tower seen from about 350 m, so that is where this shell sits.
    Scaling the 1:1 scene splat down to fit a hand does not work: it simply has
    no detail at that size, because nothing ever looked at it from here.
    """
    poses = []
    for elevation, radius, count in ((2.0, 430.0, 48),
                                     (18.0, 430.0, 44),
                                     (35.0, 420.0, 40),
                                     (52.0, 400.0, 32),
                                     (68.0, 380.0, 24),
                                     (80.0, 360.0, 12),
                                     (-12.0, 450.0, 24)):
        e = math.radians(elevation)
        for i in range(count):
            a = 2.0 * math.pi * (i + 0.31 * elevation) / count
            horiz = radius * math.cos(e)
            poses.append(((math.cos(a) * horiz, math.sin(a) * horiz,
                           150.0 + radius * math.sin(e)),
                          (0.0, 0.0, 150.0), 50.0))
    return poses


def isolate_tower(scene, tower_name="EiffelTower"):
    """Hide everything but the tower, keeping the sky so the lighting is intact.

    The sky ends up as a handful of very distant Gaussians; a GaussianCutout box
    in Unity clips them, which is machinery the project already has.
    """
    hidden = 0
    for obj in scene.collection.all_objects:
        if obj.type != "MESH":
            continue
        # The dome stays: the isolated tower still needs its sky, and a
        # GaussianCutout box clips those distant Gaussians in Unity.
        if obj.name in (tower_name, "SkyDome"):
            continue
        obj.hide_render = True
        # hide_render alone is not enough: scene.ray_cast walks the depsgraph,
        # so the initial cloud would still be seeded with ground and trees.
        obj.hide_viewport = True
        hidden += 1
    print("[rig] object mode: hid %d meshes" % hidden)


def make_camera(scene, lens):
    data = bpy.data.cameras.new("RigCam")
    data.lens = lens
    data.sensor_fit = "HORIZONTAL"
    cam = bpy.data.objects.new("RigCam", data)
    scene.collection.objects.link(cam)
    scene.camera = cam
    return cam


def intrinsics(scene, cam):
    """COLMAP PINHOLE parameters for the current camera and resolution."""
    scale = scene.render.resolution_percentage / 100.0
    width = int(scene.render.resolution_x * scale)
    height = int(scene.render.resolution_y * scale)
    sensor = cam.data.sensor_width
    fx = cam.data.lens * width / sensor
    return width, height, fx, fx, width * 0.5, height * 0.5


def cam_matrix(cam):
    """Camera-to-world for an unparented camera, without touching the depsgraph.

    Deriving it here rather than reading matrix_world matters while the rig is
    being keyframed: a view_layer.update() at that point re-evaluates the
    animation and would stomp the transform we just assigned.
    """
    return (mathutils.Matrix.Translation(cam.location)
            @ cam.rotation_euler.to_matrix().to_4x4())


def colmap_pose(cam):
    """World-to-camera rotation quaternion and translation, COLMAP convention."""
    world_to_cam = (cam_matrix(cam) @ FLIP).inverted()
    q = world_to_cam.to_quaternion()
    t = world_to_cam.to_translation()
    return (q.w, q.x, q.y, q.z), (t.x, t.y, t.z)


def setup_render(scene, res_x, res_y, samples):
    scene.render.engine = "CYCLES"
    scene.cycles.samples = samples
    scene.cycles.use_denoising = True
    scene.cycles.device = "GPU"
    prefs = bpy.context.preferences.addons.get("cycles")
    if prefs:
        prefs.preferences.compute_device_type = "OPTIX"
        prefs.preferences.get_devices()
        for dev in prefs.preferences.devices:
            dev.use = dev.type != "CPU"
    scene.render.resolution_x = res_x
    scene.render.resolution_y = res_y
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGB"
    scene.render.film_transparent = False
    # The scene never changes between shots, only the camera, so keep the
    # acceleration structure between frames.
    scene.render.use_persistent_data = True


def render_dataset(out_dir, res_x, res_y, samples, limit=0, stride=1,
                   poses=None):
    scene = bpy.context.scene
    setup_render(scene, res_x, res_y, samples)
    images_dir = os.path.join(out_dir, "images")
    sparse_dir = os.path.join(out_dir, "sparse", "0")
    os.makedirs(images_dir, exist_ok=True)
    os.makedirs(sparse_dir, exist_ok=True)

    poses = list(poses if poses is not None else camera_poses())
    if stride > 1:
        poses = poses[::stride]
    if limit:
        poses = poses[:limit]

    # One COLMAP camera per distinct focal length.
    lenses = sorted({lens for _, _, lens in poses})
    cam_ids = {lens: i + 1 for i, lens in enumerate(lenses)}

    cam = make_camera(scene, lenses[0])
    cam_lines = []
    for lens in lenses:
        cam.data.lens = lens
        w, h, fx, fy, cx, cy = intrinsics(scene, cam)
        cam_lines.append("%d PINHOLE %d %d %.10f %.10f %.10f %.10f"
                         % (cam_ids[lens], w, h, fx, fy, cx, cy))

    # Drive the rig as an animation rather than a Python loop of still renders.
    # Calling bpy.ops.render.render() repeatedly leaks: over 80 shots it grew to
    # 9.7 GB of RAM and filled all 24 GB of VRAM, at which point Cycles stalled.
    # Rendering a frame range keeps Blender in charge of the loop and, together
    # with persistent data, skips the per-frame BVH rebuild.
    img_lines = []
    for idx, (pos, look, lens) in enumerate(poses, start=1):
        cam.data.lens = lens
        cam.location = pos
        aim(cam, look)
        cam.keyframe_insert("location", frame=idx)
        cam.keyframe_insert("rotation_euler", frame=idx)
        cam.data.keyframe_insert("lens", frame=idx)

        name = "%05d.png" % idx
        (qw, qx, qy, qz), (tx, ty, tz) = colmap_pose(cam)
        img_lines.append("%d %.10f %.10f %.10f %.10f %.10f %.10f %.10f %d %s"
                         % (idx, qw, qx, qy, qz, tx, ty, tz,
                            cam_ids[lens], name))
        # COLMAP puts the image's 2D observations on a second line and writes it
        # empty when there are none - but pycolmap reads the file with
        # iter(readline, '') and stops dead at the first blank line, which would
        # silently yield a dataset of zero images. One inert observation keeps
        # the alternating parser happy; nothing downstream reads it, since the
        # tracks it would belong to only matter for the optional depth loss.
        img_lines.append("0.0 0.0 1")

    # Keyframes sit on consecutive integer frames and only those frames get
    # rendered, so the interpolation mode never comes into play.
    scene.frame_start = 1
    scene.frame_end = len(poses)
    scene.render.filepath = os.path.join(images_dir, "#####")
    bpy.ops.render.render(animation=True)

    with open(os.path.join(sparse_dir, "cameras.txt"), "w") as fh:
        fh.write("# Camera list\n")
        fh.write("\n".join(cam_lines) + "\n")
    with open(os.path.join(sparse_dir, "images.txt"), "w") as fh:
        fh.write("# Image list\n")
        fh.write("\n".join(img_lines) + "\n")
    print("[rig] wrote %d images" % len(poses))


# --------------------------------------------------------------------------
# initial point cloud
# --------------------------------------------------------------------------

def build_points(out_dir, count=400000, seed=3, views=60, stride=4,
                 pose_stride=1, poses=None):
    """Seed the trainer with a dense, correctly coloured point cloud.

    Blender 5.1 rewrote the compositor and its File Output node now only writes
    multilayer EXR, so instead of routing a Position pass through it, this
    ray casts the scene directly - which is exact anyway - and reads each
    point's colour straight out of the training image that saw it.
    """
    rng = random.Random(seed)
    scene = bpy.context.scene
    depsgraph = bpy.context.evaluated_depsgraph_get()
    images_dir = os.path.join(out_dir, "images")

    poses = list(poses if poses is not None else camera_poses())
    if pose_stride > 1:
        poses = poses[::pose_stride]
    step = max(1, len(poses) // views)
    cam = make_camera(scene, poses[0][2])

    points = []
    for idx in range(0, len(poses), step):
        pos, look, lens = poses[idx]
        name = "%05d.png" % (idx + 1)
        path = os.path.join(images_dir, name)
        if not os.path.exists(path):
            print("[rig] missing %s, render the images first" % name)
            continue
        cam.data.lens = lens
        cam.location = pos
        aim(cam, look)
        bpy.context.view_layer.update()
        points.extend(_cast_view(scene, depsgraph, cam, path, stride))
        print("[rig] %s -> %d points" % (name, len(points)))

    rng.shuffle(points)
    points = points[:count]
    sparse_dir = os.path.join(out_dir, "sparse", "0")
    os.makedirs(sparse_dir, exist_ok=True)
    with open(os.path.join(sparse_dir, "points3D.txt"), "w") as fh:
        fh.write("# 3D point list\n")
        for i, (x, y, z, r, g, b) in enumerate(points, start=1):
            fh.write("%d %.6f %.6f %.6f %d %d %d 0.0\n"
                     % (i, x, y, z, r, g, b))
    print("[rig] wrote %d init points" % len(points))


def _cast_view(scene, depsgraph, cam, image_path, stride, max_radius=4000.0,
               far_radius=800.0, far_keep=20):
    img = bpy.data.images.load(image_path)
    iw, ih = img.size
    pixels = list(img.pixels)
    ch = img.channels

    w, h, fx, fy, cx, cy = intrinsics(scene, cam)
    m = cam_matrix(cam)
    rot = m.to_3x3()
    origin = m.to_translation()
    sx, sy = iw / float(w), ih / float(h)

    out = []
    for y in range(0, h, stride):
        for x in range(0, w, stride):
            local = mathutils.Vector(((x + 0.5 - cx) / fx,
                                      (cy - y - 0.5) / fy,
                                      -1.0))
            hit, loc, _, _, _, _ = scene.ray_cast(depsgraph, origin,
                                                  rot @ local)
            if not hit:
                continue
            r2 = loc.x * loc.x + loc.y * loc.y + loc.z * loc.z
            if r2 > max_radius * max_radius:
                continue
            # The sky dome is a huge, featureless surface; sampling it as densely
            # as the tower would spend most of the budget on a gradient.
            if r2 > far_radius * far_radius and (x + y) % far_keep:
                continue
            # image.pixels starts at the bottom row
            ix = min(iw - 1, int(x * sx))
            iy = min(ih - 1, int((h - 1 - y) * sy))
            o = (iy * iw + ix) * ch
            out.append((loc.x, loc.y, loc.z,
                        _srgb(pixels[o]), _srgb(pixels[o + 1]),
                        _srgb(pixels[o + 2])))
    bpy.data.images.remove(img)
    return out


def _srgb(v):
    v = max(0.0, min(1.0, v))
    s = 12.92 * v if v <= 0.0031308 else 1.055 * (v ** (1 / 2.4)) - 0.055
    return int(round(s * 255.0))


def _cli():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    cfg = {"out": "", "res": 1600, "samples": 128, "limit": 0,
           "points": 400000, "points_only": 0, "images_only": 0,
           "point_views": 60, "pose_stride": 1, "mode": "scene"}
    for i, a in enumerate(argv):
        key = a.lstrip("-").replace("-", "_")
        if key in cfg and i + 1 < len(argv):
            cfg[key] = type(cfg[key])(argv[i + 1])
    return cfg


if __name__ == "__main__":
    cfg = _cli()
    if not cfg["out"]:
        raise SystemExit("--out is required")
    out = os.path.abspath(cfg["out"])
    res_x = cfg["res"]
    res_y = int(res_x * 0.75)
    if cfg["mode"] == "object":
        isolate_tower(bpy.context.scene)
        poses = object_poses()
    else:
        poses = camera_poses()
    if not cfg["points_only"]:
        render_dataset(out, res_x, res_y, cfg["samples"], cfg["limit"],
                       cfg["pose_stride"], poses)
    if not cfg["images_only"]:
        build_points(out, cfg["points"], views=cfg["point_views"],
                     pose_stride=cfg["pose_stride"], poses=poses)
