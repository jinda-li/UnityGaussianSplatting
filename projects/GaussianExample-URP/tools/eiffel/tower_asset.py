"""Load an external Eiffel Tower mesh and place it in the scene's metric frame.

The procedural generator in `eiffel_tower.py` gets the silhouette right but not
the structure: its bracing is a generic space frame rather than the tower's own
pattern, and it has none of the first and second floor pavilions, lift tracks or
stairs. Those are not parameters to tune, they are separate buildings, so past a
point an accurate scanned or modelled mesh is simply the shorter road.

Whatever mesh is used, the material comes from `iron.py` - the source models
carry no textures at all - and everything is normalised to the same metric frame
as the procedural tower: base at z = 0, axis at the origin, tip at TIP_HEIGHT.
That keeps the rest of the pipeline, and the Unity scale numbers, unchanged.
"""

from __future__ import annotations

import os

import bmesh
import bpy
import mathutils

from iron import iron_material, stone_material

# Height to the tip of the mast, in metres.
TIP_HEIGHT = 324.0


def _all_vertices(objects):
    pts = []
    for obj in objects:
        m = obj.matrix_world
        for v in obj.data.vertices:
            pts.append(m @ v.co)
    return pts


def _join(objects, name):
    view = bpy.context.view_layer
    for obj in view.objects:
        obj.select_set(False)
    for obj in objects:
        obj.select_set(True)
    view.objects.active = objects[0]
    if len(objects) > 1:
        bpy.ops.object.join()
    merged = view.objects.active
    merged.name = name
    for obj in view.objects:
        obj.select_set(False)
    return merged


def _trim(mesh, radius):
    """Drop faces outside `radius` of the axis.

    These models usually ship sitting on a slab of their own - this one carries
    one out to 108 m - which would fight the scene's own ground plane.
    """
    bm = bmesh.new()
    bm.from_mesh(mesh)
    doomed = [f for f in bm.faces
              if max(abs(f.calc_center_median().x),
                     abs(f.calc_center_median().y)) > radius]
    if doomed:
        bmesh.ops.delete(bm, geom=doomed, context="FACES")
        bm.to_mesh(mesh)
        mesh.update()
    bm.free()
    return len(doomed)


def import_tower(path, name="EiffelTower", texres="2k",
                 tip_height=TIP_HEIGHT, trim_radius=78.0,
                 pier_height=16.0, pier_radius=30.0):
    """Import, merge, re-scale and re-material an external tower mesh."""
    if not os.path.exists(path):
        raise FileNotFoundError(path)

    before = set(bpy.data.objects)
    ext = os.path.splitext(path)[1].lower()
    if ext in (".glb", ".gltf"):
        bpy.ops.import_scene.gltf(filepath=path)
    elif ext == ".fbx":
        bpy.ops.import_scene.fbx(filepath=path)
    elif ext == ".obj":
        bpy.ops.wm.obj_import(filepath=path)
    else:
        raise ValueError("unsupported model format: %s" % ext)

    fresh = [o for o in set(bpy.data.objects) - before if o.type == "MESH"]
    if not fresh:
        raise RuntimeError("no meshes imported from %s" % path)
    # glTF nests everything under a root empty that carries the Y-up to Z-up
    # rotation. Clearing the parent without putting the world matrix back drops
    # that rotation and lays the whole tower on its side.
    for obj in fresh:
        world = obj.matrix_world.copy()
        obj.parent = None
        obj.matrix_world = world
    tower = _join(fresh, name)

    # Bake the import transform into the mesh so object space is metres. The
    # ironwork material box-projects from object coordinates, and a leftover
    # object scale would tile it at the wrong size.
    bpy.context.view_layer.objects.active = tower
    tower.select_set(True)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

    # The glTF carries custom split normals, and with them the ironwork renders
    # as a black silhouette from every direction while the ground beside it sits
    # in full sun - the shading normals simply do not agree with the surfaces
    # they belong to. Clearing them and recalculating from the geometry fixes
    # it; on lattice this fine there is nothing for authored normals to buy.
    if getattr(tower.data, "has_custom_normals", False):
        bpy.ops.mesh.customdata_custom_splitnormals_clear()
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.normals_make_consistent(inside=False)
    bpy.ops.object.mode_set(mode="OBJECT")
    tower.select_set(False)

    pts = _all_vertices([tower])
    zs = [p.z for p in pts]
    z_min, z_max = min(zs), max(zs)
    scale = tip_height / (z_max - z_min)

    # Centre on the tower's own axis rather than the bounding box: the base
    # slab some of these models carry is not concentric with the shaft.
    spire = [p for p in pts if p.z > z_min + 0.95 * (z_max - z_min)]
    spire.sort(key=lambda p: p.x)
    cx = spire[len(spire) // 2].x
    spire.sort(key=lambda p: p.y)
    cy = spire[len(spire) // 2].y

    mesh = tower.data
    offset = mathutils.Vector((cx, cy, z_min))
    for v in mesh.vertices:
        v.co = (v.co - offset) * scale
    mesh.update()

    dropped = _trim(mesh, trim_radius) if trim_radius else 0

    mesh.materials.clear()
    mesh.materials.append(iron_material(texres=texres, top_height=tip_height))
    mesh.materials.append(stone_material())
    # The piers are modelled as part of the ironwork mesh, so they arrive with
    # the paint on them. Anything low and wide is masonry, not iron.
    piers = 0
    for poly in mesh.polygons:
        c = poly.center
        if c.z < pier_height and max(abs(c.x), abs(c.y)) > pier_radius:
            poly.material_index = 1
            piers += 1
    mesh.update()

    pts = _all_vertices([tower])
    zs = [p.z for p in pts]
    xs = [abs(p.x) for p in pts]
    xs.sort()
    print("[tower] %s: %d faces (%d trimmed, %d pier), height %.1f m,"
          " half-width %.1f m"
          % (os.path.basename(path), len(mesh.polygons), dropped, piers,
             max(zs) - min(zs), xs[int(len(xs) * 0.995)]))
    return tower
