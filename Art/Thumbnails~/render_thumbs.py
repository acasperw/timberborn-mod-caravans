"""Renders 16:9 thumbnail backdrops of the caravans. Run with:
blender -b --python render_thumbs.py -- --textures <game diffuse textures> [--out <dir>] [--preview] [--shots a,b]
Each shot writes <name>.png and <name>_mask.png (alpha of the hero wagon, for layering the title) into renders/.
Needs Art/vanilla (run Art/extract_vanilla_models.py first). Then run compose.py to add the title."""
import argparse
import math
import os
import random
import sys

import bmesh
import bpy
from mathutils import Euler, Matrix, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
ART = os.path.dirname(HERE)
sys.path.insert(0, ART)
import build_caravan_models as b  # noqa: E402

ROAD_X = -1.5


def lin(c):
    return tuple(b._srgb_to_linear(v) for v in c)


def material(name, build):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    nodes.clear()
    out = nodes.new("ShaderNodeOutputMaterial")
    bsdf = nodes.new("ShaderNodeBsdfPrincipled")
    bsdf.inputs["Roughness"].default_value = 0.9
    links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
    build(nodes, links, bsdf)
    return mat


def noisy(colors, scale, detail=6.0, bump=0.0, coords="Object"):
    def build(nodes, links, bsdf):
        tc = nodes.new("ShaderNodeTexCoord")
        noise = nodes.new("ShaderNodeTexNoise")
        noise.inputs["Scale"].default_value = scale
        noise.inputs["Detail"].default_value = detail
        links.new(tc.outputs[coords], noise.inputs["Vector"])
        ramp = nodes.new("ShaderNodeValToRGB")
        ramp.color_ramp.elements[0].color = (*lin(colors[0]), 1)
        ramp.color_ramp.elements[1].color = (*lin(colors[1]), 1)
        ramp.color_ramp.elements[0].position = 0.35
        ramp.color_ramp.elements[1].position = 0.65
        links.new(noise.outputs["Fac"], ramp.inputs["Fac"])
        links.new(ramp.outputs["Color"], bsdf.inputs["Base Color"])
        if bump:
            bn = nodes.new("ShaderNodeBump")
            bn.inputs["Strength"].default_value = bump
            links.new(noise.outputs["Fac"], bn.inputs["Height"])
            links.new(bn.outputs["Normal"], bsdf.inputs["Normal"])
    return build


def add_object(name, bm, mat, collection, smooth=False):
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    for poly in mesh.polygons:
        poly.use_smooth = smooth
    mesh.materials.append(mat)
    obj = bpy.data.objects.new(name, mesh)
    collection.objects.link(obj)
    return obj


def road_points():
    # Straight out of the post door, then a lazy bend to the right towards the map edge
    pts = [Vector((ROAD_X, y * 0.5, 0)) for y in range(0, 17)]
    centre = Vector((ROAD_X + 6, 8, 0))
    for i in range(1, 25):
        a = math.pi - i * (math.pi / 2) / 24
        pts.append(centre + Vector((math.cos(a), math.sin(a), 0)) * 6)
    pts += [Vector((ROAD_X + 6 + x, 14, 0)) for x in range(1, 40)]
    return pts


def build_scene(collection):
    grass = material("_Grass", noisy(((0.3, 0.48, 0.14), (0.5, 0.64, 0.22)), 0.35, detail=10, bump=0.2))
    dirt = material("_Dirt", noisy(((0.47, 0.36, 0.24), (0.6, 0.49, 0.34)), 4.0, bump=0.4))
    bark = material("_Bark", noisy(((0.3, 0.2, 0.12), (0.4, 0.28, 0.17)), 8.0))
    leaves = [material(f"_Leaves{i}", noisy((a, c), 3.0, bump=0.3)) for i, (a, c) in enumerate((
        ((0.17, 0.38, 0.13), (0.27, 0.5, 0.17)), ((0.22, 0.44, 0.12), (0.36, 0.56, 0.18)),
        ((0.13, 0.3, 0.14), (0.2, 0.4, 0.18))))]
    stone = material("_Stone", noisy(((0.5, 0.5, 0.48), (0.64, 0.63, 0.6)), 6.0, bump=0.5))

    bm = bmesh.new()
    bmesh.ops.create_grid(bm, x_segments=1, y_segments=1, size=150)
    add_object("_Ground", bm, grass, collection)

    pts = road_points()
    bm = bmesh.new()
    verts = []
    for i, p in enumerate(pts):
        d = (pts[min(i + 1, len(pts) - 1)] - pts[max(i - 1, 0)]).normalized()
        side = Vector((-d.y, d.x, 0)) * 0.46
        verts.append((bm.verts.new(p + side + Vector((0, 0, 0.004))), bm.verts.new(p - side + Vector((0, 0, 0.004)))))
    for (a, c), (e, f) in zip(verts, verts[1:]):
        bm.faces.new((a, c, f, e))
    add_object("_Road", bm, dirt, collection)

    rng = random.Random(7)

    def clear(p, margin):
        if -3.4 - margin < p.x < 0.4 + margin and -3.4 - margin < p.y < 0.4 + margin:
            return False
        return min((p - q).length for q in pts[::2]) > 0.75 + margin

    def tree(p, scale):
        kind = rng.random()
        trunk_h = (0.5 if kind < 0.55 else 0.35) * scale
        bm = bmesh.new()
        bmesh.ops.create_cone(bm, cap_ends=True, segments=7, radius1=0.07 * scale, radius2=0.05 * scale,
                              depth=trunk_h, matrix=Matrix.Translation(p + Vector((0, 0, trunk_h / 2))))
        add_object("_Trunk", bm, bark, collection)
        bm = bmesh.new()
        mat = rng.choice(leaves)
        if kind < 0.55:  # pine
            for k in range(3):
                r = (0.5 - k * 0.12) * scale
                bmesh.ops.create_cone(bm, cap_ends=True, segments=8, radius1=r, radius2=0, depth=0.7 * scale,
                                      matrix=Matrix.Translation(p + Vector((0, 0, trunk_h + (0.25 + k * 0.33) * scale)))
                                      @ Matrix.Rotation(rng.random(), 4, "Z"))
        else:  # broadleaf
            for _ in range(3):
                off = Vector((rng.uniform(-.2, .2), rng.uniform(-.2, .2), rng.uniform(0.3, 0.55))) * scale
                bmesh.ops.create_icosphere(bm, subdivisions=2, radius=rng.uniform(0.3, 0.42) * scale,
                                           matrix=Matrix.Translation(p + Vector((0, 0, trunk_h)) + off))
        add_object("_Canopy", bm, mat, collection, smooth=kind >= 0.55)

    placed = 0
    while placed < 420:
        p = Vector((rng.uniform(-30, 40), rng.uniform(-25, 45), 0))
        if clear(p, 0.6) and (p - Vector((ROAD_X, 3, 0))).length > 2.2:
            tree(p, rng.uniform(1.1, 1.8))
            placed += 1
    for _ in range(140):  # rocks and bushes near the road
        p = Vector((rng.uniform(-8, 10), rng.uniform(-6, 18), 0))
        if not clear(p, 0.05):
            continue
        bm = bmesh.new()
        if rng.random() < 0.5:
            r = rng.uniform(0.06, 0.15)
            bmesh.ops.create_icosphere(bm, subdivisions=1, radius=r,
                                       matrix=Matrix.Translation(p) @ Matrix.Diagonal((1.3, 1, 0.6, 1)))
            add_object("_Rock", bm, stone, collection)
        else:
            for _ in range(3):
                off = Vector((rng.uniform(-.12, .12), rng.uniform(-.12, .12), 0.08))
                bmesh.ops.create_icosphere(bm, subdivisions=2, radius=rng.uniform(0.1, 0.17),
                                           matrix=Matrix.Translation(p + off))
            add_object("_Bush", bm, rng.choice(leaves), collection, smooth=True)


def setup_world_and_light(collection):
    world = bpy.data.worlds.new("_ThumbWorld")
    bpy.context.scene.world = world
    world.use_nodes = True
    nodes, links = world.node_tree.nodes, world.node_tree.links
    nodes.clear()
    out = nodes.new("ShaderNodeOutputWorld")
    tc = nodes.new("ShaderNodeTexCoord")
    sep = nodes.new("ShaderNodeSeparateXYZ")
    ramp = nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].color = (*lin((0.86, 0.9, 0.9)), 1)
    ramp.color_ramp.elements[1].color = (*lin((0.36, 0.6, 0.92)), 1)
    ramp.color_ramp.elements[1].position = 0.22
    sky = nodes.new("ShaderNodeBackground")
    sky.inputs["Strength"].default_value = 1.0
    ambient = nodes.new("ShaderNodeBackground")
    ambient.inputs["Color"].default_value = (*lin((0.68, 0.76, 0.88)), 1)
    ambient.inputs["Strength"].default_value = 0.75
    path = nodes.new("ShaderNodeLightPath")
    mix = nodes.new("ShaderNodeMixShader")
    links.new(tc.outputs["Generated"], sep.inputs["Vector"])
    links.new(sep.outputs["Z"], ramp.inputs["Fac"])
    links.new(ramp.outputs["Color"], sky.inputs["Color"])
    links.new(path.outputs["Is Camera Ray"], mix.inputs["Fac"])
    links.new(ambient.outputs["Background"], mix.inputs[1])
    links.new(sky.outputs["Background"], mix.inputs[2])
    links.new(mix.outputs["Shader"], out.inputs["Surface"])

    sun = bpy.data.objects.new("_Sun", bpy.data.lights.new("_Sun", "SUN"))
    sun.data.energy = 4.2
    sun.data.color = (1.0, 0.93, 0.8)
    sun.data.angle = math.radians(4)
    sun.rotation_euler = (math.radians(52), 0, math.radians(125))
    collection.objects.link(sun)


def place(collection, location, yaw_deg):
    m = Matrix.Translation(location) @ Matrix.Rotation(math.radians(yaw_deg), 4, "Z")
    for obj in collection.objects:
        if obj.parent is None:
            obj.matrix_world = m @ obj.matrix_world


def tilt_wheels(collection, angle):
    for obj in collection.objects:
        if obj.type == "EMPTY" and obj.name.startswith("#Wheel"):
            obj.rotation_euler.x += angle


# name: (post faction, {caravan: (road y or (x, y), yaw)}, hero, camera location, look-at, lens, focus distance object)
SHOTS = {
    "hero_folktails": dict(post="Folktails", caravans={"Folktails": ((ROAD_X, 3.1, 0), 180)}, hero="Folktails",
                           cam=(-0.05, 5.45, 0.62), look=(ROAD_X - 0.5, 2.85, 0.36), lens=38, fstop=2.0),
    "hero_ironteeth": dict(post="IronTeeth", caravans={"IronTeeth": ((ROAD_X, 3.1, 0), 180)}, hero="IronTeeth",
                           cam=(-0.05, 5.45, 0.62), look=(ROAD_X - 0.5, 2.85, 0.36), lens=38, fstop=2.0),
    "convoy": dict(post="Folktails", caravans={"Folktails": ((ROAD_X, 5.4, 0), 180),
                                               "IronTeeth": ((ROAD_X, 3.7, 0), 180)}, hero="Folktails",
                   cam=(0.75, 7.1, 0.7), look=(ROAD_X - 0.45, 4.1, 0.38), lens=40, fstop=2.4),
    "departure": dict(post="Folktails", caravans={"IronTeeth": ((ROAD_X, 2.6, 0), 180)}, hero="IronTeeth",
                      cam=(3.6, 6.4, 3.0), look=(ROAD_X + 0.2, 0.2, 0.3), lens=32, fstop=5.6),
}


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser()
    parser.add_argument("--textures", required=True, help="folder with the game's diffuse textures")
    parser.add_argument("--out", default=os.path.join(HERE, "renders"))
    parser.add_argument("--preview", action="store_true")
    parser.add_argument("--shots", default=",".join(SHOTS))
    args = parser.parse_args(argv)
    os.makedirs(args.out, exist_ok=True)

    vanilla = os.path.join(ART, "vanilla")
    caravans = {f: b.build_caravan(f) for f in ("Folktails", "IronTeeth")}
    posts = {f: b.build_post(f, vanilla, wagon=caravans[f]) for f in ("Folktails", "IronTeeth")}
    b.setup_preview_materials(args.textures)
    stage = bpy.data.collections.new("_Stage")
    bpy.context.scene.collection.children.link(stage)
    build_scene(stage)
    setup_world_and_light(stage)

    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "GPU"
    prefs = bpy.context.preferences.addons["cycles"].preferences
    for backend in ("OPTIX", "CUDA", "HIP", "ONEAPI"):
        try:
            prefs.compute_device_type = backend
            prefs.get_devices()
            if any(d.type == backend for d in prefs.devices):
                for d in prefs.devices:
                    d.use = True
                print("Thumbs: using", backend)
                break
        except TypeError:
            continue
    else:
        scene.cycles.device = "CPU"
    scene.cycles.samples = 24 if args.preview else 160
    scene.cycles.use_denoising = True
    scene.render.resolution_x, scene.render.resolution_y = 1920, 1080
    scene.render.resolution_percentage = 40 if args.preview else 100
    scene.view_settings.view_transform = "AgX"
    scene.view_settings.look = "AgX - Medium High Contrast"
    scene.view_settings.exposure = 0.35

    camera = bpy.data.objects.new("_ThumbCam", bpy.data.cameras.new("_ThumbCam"))
    stage.objects.link(camera)
    scene.camera = camera
    bpy.context.view_layer.update()
    base = {c.name: [(o, o.matrix_world.copy()) for o in c.objects if o.parent is None]
            for c in list(caravans.values())}
    layers = {l.name: l for l in bpy.context.view_layer.layer_collection.children}
    for name in args.shots.split(","):
        shot = SHOTS[name]
        for f, coll in caravans.items():
            for obj, m in base[coll.name]:
                obj.matrix_world = m
            if f in shot["caravans"]:
                loc, yaw = shot["caravans"][f]
                place(coll, Vector(loc), yaw)
        for lname, layer in layers.items():
            keep = lname == "_Stage" or lname == f"CaravanPost.{shot['post']}" or \
                any(lname == f"Caravan.{f}" for f in shot["caravans"])
            layer.exclude = not keep
        bpy.context.view_layer.update()
        camera.location = shot["cam"]
        look = Vector(shot["look"])
        camera.rotation_euler = (look - camera.location).to_track_quat("-Z", "Y").to_euler()
        camera.data.lens = shot["lens"]
        hero = caravans[shot["hero"]]
        hero_pts = [o.matrix_world @ Vector(k) for o in hero.objects if o.type == "MESH" for k in o.bound_box]
        hero_centre = sum(hero_pts, Vector()) / len(hero_pts)
        camera.data.dof.use_dof = True
        camera.data.dof.focus_distance = (hero_centre - camera.location).length
        camera.data.dof.aperture_fstop = shot["fstop"]

        scene.render.film_transparent = False
        scene.render.filepath = os.path.join(args.out, f"{name}.png")
        bpy.ops.render.render(write_still=True)

        # Mask: everything except the hero wagon becomes holdout, so alpha = where the hero wagon is visible
        holdouts = [o for o in scene.objects if o.visible_get() and o.type == "MESH" and o.name not in hero.all_objects]
        for o in holdouts:
            o.is_holdout = True
        scene.render.film_transparent = True
        samples = scene.cycles.samples
        scene.cycles.samples = 16
        scene.render.filepath = os.path.join(args.out, f"{name}_mask.png")
        bpy.ops.render.render(write_still=True)
        scene.cycles.samples = samples
        for o in holdouts:
            o.is_holdout = False
    print("Thumbs: done")


main()
