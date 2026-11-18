"""Builds the Caravans mod models as Blender collections ready for Timbermesh export.

Interactive: open Blender (4.x/5.x) with the Timbermesh plugin enabled, open the Scripting tab and run this file.
Then export each collection (right-click -> Export (*.timbermesh), Merge meshes ON, Use vertex animations OFF):

  CaravanPost.<Faction>                      -> Data/Buildings/Storage/CaravanPost/CaravanPost.<Faction>.Model.timbermesh
  CaravanPost.<Faction>.ConstructionStage0   -> Data/Buildings/Storage/CaravanPost/CaravanPost.<Faction>.ConstructionStage0.Model.timbermesh
  Caravan.<Faction>                          -> Data/Caravans/Caravan.<Faction>.Model.timbermesh

Headless (builds, exports the caravans into the mod and renders previews):

  blender -b --python Art/build_caravan_models.py -- --export --export-posts --render <out dir> --textures <texture dir>

The Caravan Post is the vanilla District Crossing (front half, with a shadowed doorway) plus a copy of it turned around
onto a second row with the back arch closed by a plaster wall, and an open storage yard with shelves, cargo and a parked
caravan behind it (3x3 footprint, one entrance). It needs the vanilla models,
extracted from your own game install first (git-ignored, never committed):

  python Art/extract_vanilla_models.py          # -> Art/vanilla/DistrictCrossing.<Faction>[.ConstructionStage0].Model.timbermesh

Without Art/vanilla (or --vanilla-dir) only the caravans are built. --export-posts writes the post models.

--textures is optional and only affects the Blender viewport/previews: a folder with the game's diffuse textures
(BaseWood_1K_D_Brown.png, Details_D.Folktails.png, ...). The Folktails ones are packed inside the game's
Timberborn_Data/StreamingAssets/Modding/TimberbornExampleModels.blend. They are never shipped with the mod.

Texturing: the game materials are texture ATLASES (BaseWood = plank strips + log ends, Details = cloth/crates/sacks,
BaseMetal = plates + a round hatch, ...). Every face is therefore mapped onto a named atlas REGION below instead of
the whole 0..1 square, at roughly the texel density the game's own models use. Vertex colours carry baked ambient
occlusion like the vanilla models. Material names must match the game's materials exactly (case sensitive).

Units: 1 Blender unit = 1 tile, z up. Caravans are modelled facing +Y and rotated by CARAVAN_YAW so they face -Y,
which the exporter turns into Unity's +Z (forward).
"""
import argparse
import math
import os
import re
import struct
import sys
import zlib

import bmesh
import bpy
from mathutils import Matrix, Quaternion, Vector

CARAVAN_YAW = 180.0
TEX = 1024.0
WOOD_DENSITY = 0.62  # UV units per metre, close to the vanilla models
METAL_DENSITY = 0.6
AO_MIN = 0.72
AO_HEIGHT = 0.35


def _px(x0, y0, x1, y1):
    """Atlas rectangle in pixels (origin top-left, as seen in an image viewer) -> (u0, v0, u1, v1)."""
    return x0 / TEX, 1.0 - y1 / TEX, x1 / TEX, 1.0 - y0 / TEX


_TOP_MID_X = [(518, 572), (580, 636), (643, 700), (708, 764)]
_RIGHT_X = [(775, 828), (838, 892), (902, 956), (965, 1018)]
_LEFT_X = [(6, 60), (68, 122), (130, 186), (195, 250), (258, 314), (323, 380), (388, 444), (452, 506)]

REGIONS = {
    # BaseWood_* (every faction/colour shares this layout). Planks run vertically in the texture.
    "plank_long": [_px(a, 518, b, 1016) for a, b in _LEFT_X] + [_px(a, 6, b, 502) for a, b in _TOP_MID_X],
    "plank_mid": [_px(a, 132, b, 502) for a, b in _RIGHT_X],
    "plank_short": [_px(a, 518, b, 760) for a, b in _TOP_MID_X] + [_px(a, 518, b, 752) for a, b in _RIGHT_X],
    "plank_stub": [_px(a, 6, b, 122) for a, b in _RIGHT_X],
    "bark": [_px(8, 8, 504, 492)],
    "log_end": [_px(645, 772, 765, 892)],
    "log_end_hole": [_px(517, 772, 637, 892)],
    "log_end_small": [_px(644, 900, 700, 956), _px(644, 964, 700, 1020)],
    "wood_plain": [_px(780, 780, 1016, 1016)],
    # Details.<Faction>
    "cloth_emblem": [_px(6, 6, 506, 506)],
    "cloth_plain": [_px(12, 10, 500, 70)],
    "sack": [_px(522, 40, 758, 496)],
    "sack_top": [_px(560, 60, 720, 130)],
    "rope": [_px(772, 4, 826, 508)],
    "banner_red": [_px(836, 4, 1020, 168)],
    "banner_green": [_px(836, 174, 1020, 338)],
    "banner_blue": [_px(836, 344, 1020, 508)],
    "crate_diagonal": [_px(4, 542, 254, 736)],
    "crate_cross": [_px(258, 542, 508, 736)],
    "crate_framed": [_px(4, 770, 254, 1020)],
    "crate_planks": [_px(258, 770, 508, 1020)],
    "banded_planks": [_px(514, 620, 1022, 722)],
    # BaseMetal.<Faction>
    "metal_plate": [_px(10, 10, 502, 502)],
    "metal_plate_b": [_px(520, 8, 1016, 248)],
    "metal_circle": [_px(774, 262, 1018, 506)],
    "metal_ribs": [_px(4, 516, 504, 1020)],
    "metal_ribs_small": [_px(516, 772, 764, 1020)],
    "metal_tile": [_px(518, 518, 762, 762)],
    "metal_hex": [_px(772, 606, 1020, 764)],
    # Seamless whole textures (PaintedMetal, ThatchedRoof, RoofPlanks)
    "full": [(0.0, 0.0, 1.0, 1.0)],
}

MATERIALS = {
    "Folktails": {
        "wood": "BaseWood_Brown.Folktails",
        "light": "BaseWood_LightBrown.Folktails",
        "metal": "BaseMetal.Folktails",
        "details": "Details.Folktails",
        "paint": "PaintedMetal.Folktails",
        "roof": "ThatchedRoof.Folktails",
    },
    "IronTeeth": {
        "wood": "BaseWood_DarkBrown.IronTeeth",
        "light": "BaseWood_Grey.IronTeeth",
        "metal": "BaseMetal.IronTeeth",
        "details": "Details.IronTeeth",
        "paint": "PaintedMetal.IronTeeth",
        "roof": "RoofPlanks.IronTeeth",
    },
}

# Game material -> (diffuse texture, _Color tint, viewport colour). Used for the Blender preview only.
MATERIAL_PREVIEW = {
    "BaseWood_Brown.Folktails": (["BaseWood_1K_D_Brown"], (1, 1, 1), (0.35, 0.22, 0.12)),
    "BaseWood_LightBrown.Folktails": (["BaseWood_1K_D_White"], (0.66, 0.555, 0.408), (0.55, 0.42, 0.28)),
    "BaseMetal.Folktails": (["BaseMetal_D1", "BaseMetal_D"], (1, 1, 1), (0.45, 0.42, 0.36)),
    "Details.Folktails": (["Details_D.Folktails"], (1, 1, 1), (0.3, 0.45, 0.45)),
    "PaintedMetal.Folktails": (["PaintedMetal_D.Folktails"], (1, 1, 1), (0.75, 0.6, 0.1)),
    "ThatchedRoof.Folktails": (["ThatchedRoof_D"], (0.925, 0.925, 0.925), (0.7, 0.6, 0.3)),
    "BaseWood_DarkBrown.IronTeeth": (["BaseWood_1K_D_Grey"], (0.311, 0.267, 0.207), (0.2, 0.16, 0.12)),
    "BaseWood_Grey.IronTeeth": (["BaseWood_1K_D_Grey"], (0.623, 0.58, 0.526), (0.45, 0.43, 0.4)),
    "BaseMetal.IronTeeth": (["BaseMetal_D2"], (1, 1, 1), (0.4, 0.4, 0.4)),
    "Details.IronTeeth": (["Details_D.IronTeeth"], (1, 1, 1), (0.25, 0.3, 0.45)),
    "PaintedMetal.IronTeeth": (["PaintedMetal_D.IronTeeth"], (1, 1, 1), (0.25, 0.45, 0.55)),
    "RoofPlanks.IronTeeth": (["RoofPlanks_D"], (1, 1, 1), (0.2, 0.3, 0.25)),
    "BaseWood_White.Folktails": (["BaseWood_1K_D_White"], (0.887, 0.864, 0.832), (0.8, 0.78, 0.74)),
    "BaseWood_Indigo.IronTeeth": (["BaseWood_1K_D_Indigo"], (1, 1, 1), (0.25, 0.25, 0.45)),
    "Plaster_White.Folktails": (["Plaster_D_White.Folktails"], (1, 1, 1), (0.85, 0.83, 0.78)),
    "Plaster_Orange.IronTeeth": (["Plaster_D_White.IronTeeth"], (0.835, 0.482, 0.259), (0.8, 0.45, 0.25)),
    "WindowsAtlas.Folktails": (["WindowsAtlas_D.Folktails"], (1, 1, 1), (0.4, 0.5, 0.6)),
    "WindowsAtlas.IronTeeth": (["WindowsAtlas_D.IronTeeth"], (1, 1, 1), (0.4, 0.5, 0.6)),
    "Paper.Folktails": (["Paper_D.Folktails"], (1, 1, 1), (0.85, 0.8, 0.6)),
    "Paper.IronTeeth": (["Paper_D.IronTeeth"], (1, 1, 1), (0.85, 0.8, 0.6)),
}


class Map:
    """How a face (or a group of faces) is mapped onto an atlas region.

    grain: the region's length (V) covers the face's length, width keeps the same scale (wood planks).
    fill:  stretch to the whole region (crate faces, emblems, log ends).
    fit:   uniform `density` UV/m, scaled down if needed to stay inside the region (metal plates).
    tile:  uniform `density` UV/m anchored at the region corner, may run past it (seamless textures only).
    """

    def __init__(self, region, mode="grain", density=WOOD_DENSITY, index=None):
        self.region = region
        self.mode = mode
        self.density = density
        self.index = index


WOOD = Map("wood")


def _wood_region(length):
    pixels = length * WOOD_DENSITY * TEX
    if pixels > 400:
        return "plank_long"
    if pixels > 280:
        return "plank_mid"
    if pixels > 160:
        return "plank_short"
    return "plank_stub"


class Part:
    """Faces sharing one game material; becomes one Blender object."""

    def __init__(self, collection, name, material_name):
        self.collection = collection
        self.name = name
        self.material_name = material_name
        self.faces = []
        self._element = 0
        self.ao_z = 0.0  # height of the object's origin, so the ground-up shading also works for offset objects

    def element(self):
        self._element += 1
        return self._element

    def face(self, element, verts, coords, mapping, out_hint, group=None, smooth=False):
        normal = _newell(verts)
        if normal.dot(out_hint) < 0:
            verts, coords = list(reversed(verts)), list(reversed(coords))
        self.faces.append({
            "element": element, "verts": verts, "coords": coords, "map": mapping,
            "group": (element, group if group is not None else len(self.faces)), "smooth": smooth,
        })

    def build(self):
        if not self.faces:
            return None
        uvs = self._compute_uvs()
        bm = bmesh.new()
        uv_layer = bm.loops.layers.uv.new("UVMap")
        color_layer = bm.loops.layers.color.new("Col")
        vert_cache = {}
        for face_index, face in enumerate(self.faces):
            bm_verts = []
            for co in face["verts"]:
                key = (face["element"], round(co.x, 5), round(co.y, 5), round(co.z, 5))
                if key not in vert_cache:
                    vert_cache[key] = bm.verts.new(co)
                bm_verts.append(vert_cache[key])
            try:
                bm_face = bm.faces.new(bm_verts)
            except ValueError:
                continue
            bm_face.smooth = face["smooth"]
            for loop, uv in zip(bm_face.loops, uvs[face_index]):
                loop[uv_layer].uv = uv
        bm.normal_update()
        for bm_face in bm.faces:
            down = 0.82 if bm_face.normal.z < -0.6 else 1.0
            for loop in bm_face.loops:
                t = min(max((loop.vert.co.z + self.ao_z) / AO_HEIGHT, 0.0), 1.0)
                ao = (AO_MIN + (1.0 - AO_MIN) * t * t * (3 - 2 * t)) * down
                loop[color_layer] = (ao, ao, ao, 1.0)
        mesh = bpy.data.meshes.new(self.name)
        bm.to_mesh(mesh)
        bm.free()
        mesh.materials.append(material(self.material_name))
        obj = bpy.data.objects.new(self.name, mesh)
        self.collection.objects.link(obj)
        return obj

    def _compute_uvs(self):
        bounds = {}
        for face in self.faces:
            b = bounds.setdefault(face["group"], [math.inf, math.inf, -math.inf, -math.inf])
            for a, v in face["coords"]:
                b[0], b[1], b[2], b[3] = min(b[0], a), min(b[1], v), max(b[2], a), max(b[3], v)
        regions = {}
        result = []
        for face in self.faces:
            amin, bmin, amax, bmax = bounds[face["group"]]
            width, length = max(amax - amin, 1e-6), max(bmax - bmin, 1e-6)
            mapping = face["map"]
            if face["group"] not in regions:
                name = _wood_region(length) if mapping.region == "wood" else mapping.region
                options = REGIONS[name]
                index = mapping.index
                if index is None:
                    index = zlib.crc32(f"{self.collection.name}/{self.name}/{face['group']}".encode())
                regions[face["group"]] = options[index % len(options)]
            u0, v0, u1, v1 = regions[face["group"]]
            rw, rh = u1 - u0, v1 - v0
            uvs = []
            for a, b in face["coords"]:
                fa, fb = (a - amin) / width, (b - bmin) / length
                if mapping.mode == "fill":
                    uvs.append((u0 + fa * rw, v0 + fb * rh))
                elif mapping.mode == "grain":
                    scale = rh / length
                    used = min(width * scale, rw)
                    uvs.append(((u0 + u1) / 2 + (fa - 0.5) * used, v0 + fb * rh))
                elif mapping.mode == "fit":
                    scale = min(mapping.density, rw / width, rh / length)
                    uvs.append(((u0 + u1) / 2 + (fa - 0.5) * width * scale,
                                (v0 + v1) / 2 + (fb - 0.5) * length * scale))
                else:
                    uvs.append((u0 + (a - amin) * mapping.density, v0 + (b - bmin) * mapping.density))
            result.append(uvs)
        return result


class Parts:
    """Lazily creates one Part per material key of a faction."""

    def __init__(self, collection, faction, prefix=""):
        self.collection = collection
        self.faction = faction
        self.prefix = prefix
        self.parts = {}

    def __getitem__(self, key):
        if key not in self.parts:
            name = f"{self.collection.name}.{self.prefix}{key.capitalize()}"
            self.parts[key] = Part(self.collection, name, MATERIALS[self.faction][key])
        return self.parts[key]

    def build(self):
        return [obj for obj in (part.build() for part in self.parts.values()) if obj]


def _newell(verts):
    n = Vector((0.0, 0.0, 0.0))
    for i, a in enumerate(verts):
        b = verts[(i + 1) % len(verts)]
        n.x += (a.y - b.y) * (a.z + b.z)
        n.y += (a.z - b.z) * (a.x + b.x)
        n.z += (a.x - b.x) * (a.y + b.y)
    return n


def _transform(center, rot):
    m = Matrix.Translation(Vector(center))
    if rot is not None:
        m = m @ rot.to_4x4()
    return m


def rot_x(deg):
    return Matrix.Rotation(math.radians(deg), 3, "X")


def rot_y(deg):
    return Matrix.Rotation(math.radians(deg), 3, "Y")


def rot_z(deg):
    return Matrix.Rotation(math.radians(deg), 3, "Z")


_AXIS_ROT = {"x": rot_y(90), "y": rot_x(90), "z": Matrix.Identity(3)}


def _resolve(maps, key, axis, grain):
    if isinstance(maps, Map):
        return maps
    for candidate in (key, {"+z": "top", "-z": "bottom"}.get(key), "end" if axis == grain else None, "side", "all"):
        if candidate in maps:
            return maps[candidate]
    return WOOD


def box(part, center, size, maps=WOOD, rot=None, grain=None):
    """Axis-aligned box (optionally rotated). The texture's V runs along `grain` (default: longest side)."""
    m = _transform(center, rot)
    r = m.to_3x3()
    h = Vector(size) / 2
    g = grain if grain is not None else max(range(3), key=lambda i: size[i])
    element = part.element()
    for axis in range(3):
        i, j = [k for k in range(3) if k != axis]
        if g != axis:
            v = g
            u = i if j == g else j
        else:
            v = i if size[i] >= size[j] else j
            u = j if v == i else i
        for sign in (-1, 1):
            normal = Vector((0, 0, 0))
            normal[axis] = sign
            u_vec, v_vec = Vector((0, 0, 0)), Vector((0, 0, 0))
            u_vec[u], v_vec[v] = 1, 1
            flip = -1 if u_vec.cross(v_vec).dot(normal) < 0 else 1
            verts, coords = [], []
            for su, sv in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
                p = Vector((0, 0, 0))
                p[axis], p[u], p[v] = sign * h[axis], su * h[u], sv * h[v]
                verts.append(m @ p)
                coords.append((flip * p[u], p[v]))
            key = ("+" if sign > 0 else "-") + "xyz"[axis]
            part.face(element, verts, coords, _resolve(maps, key, axis, g), r @ normal)


def beam(part, start, end, width, height=None, maps=WOOD, up=(0, 0, 1)):
    """Box running from start to end (grain along it); `height` is measured towards `up`."""
    start, end = Vector(start), Vector(end)
    z = (end - start).normalized()
    up = Vector(up)
    if abs(z.dot(up)) > 0.99:
        up = Vector((1, 0, 0)) if abs(z.x) < 0.9 else Vector((0, 1, 0))
    x = up.cross(z).normalized()
    rot = Matrix((x, z.cross(x), z)).transposed()
    box(part, (start + end) / 2, (width, height or width, (end - start).length), maps, rot=rot, grain=2)


def cylinder(part, center, radius, length, segments, side=WOOD, cap=Map("log_end", "fill"), axis="z", rot=None,
             smooth=True, caps=(True, True), radius_top=None):
    m = _transform(center, rot) @ _AXIS_ROT[axis].to_4x4()
    r = m.to_3x3()
    rt = radius if radius_top is None else radius_top
    hz = length / 2
    angles = [2 * math.pi * i / segments for i in range(segments + 1)]
    element = part.element()
    circumference = 2 * math.pi * (radius + rt) / 2
    for i in range(segments):
        t0, t1 = angles[i], angles[i + 1]
        tm = (t0 + t1) / 2
        verts = [Vector((radius * math.cos(t0), radius * math.sin(t0), -hz)),
                 Vector((radius * math.cos(t1), radius * math.sin(t1), -hz)),
                 Vector((rt * math.cos(t1), rt * math.sin(t1), hz)),
                 Vector((rt * math.cos(t0), rt * math.sin(t0), hz))]
        a0, a1 = circumference * i / segments, circumference * (i + 1) / segments
        coords = [(a0, -hz), (a1, -hz), (a1, hz), (a0, hz)]
        part.face(element, [m @ p for p in verts], coords, side, r @ Vector((math.cos(tm), math.sin(tm), 0)),
                  group="side", smooth=smooth)
    for sign, enabled, rad in ((-1, caps[0], radius), (1, caps[1], rt)):
        if not enabled:
            continue
        pts = [Vector((rad * math.cos(t), rad * math.sin(t), sign * hz)) for t in angles[:-1]]
        coords = [(sign * p.x, p.y) for p in pts]
        part.face(element, [m @ p for p in pts], coords, cap, r @ Vector((0, 0, sign)))


def ring(part, center, r_out, r_in, width, segments, outer=WOOD, inner=None, sides=None, ends=None, axis="x",
         arc=None, rot=None, smooth=True):
    """Annulus prism (wheel rims, hoops, bands). `arc` = (start, end) in degrees for partial rings."""
    m = _transform(center, rot) @ _AXIS_ROT[axis].to_4x4()
    r = m.to_3x3()
    inner, sides, ends = inner or outer, sides or outer, ends or sides or outer
    a_start, a_end = (0.0, 2 * math.pi) if arc is None else (math.radians(arc[0]), math.radians(arc[1]))
    angles = [a_start + (a_end - a_start) * i / segments for i in range(segments + 1)]
    hz = width / 2
    rm = (r_out + r_in) / 2
    element = part.element()

    def p(rad, t, z):
        return Vector((rad * math.cos(t), rad * math.sin(t), z))

    for i in range(segments):
        t0, t1 = angles[i], angles[i + 1]
        tm = (t0 + t1) / 2
        radial = Vector((math.cos(tm), math.sin(tm), 0))
        for rad, mapping, hint, group in ((r_out, outer, radial, "outer"), (r_in, inner, -radial, "inner")):
            verts = [p(rad, t0, -hz), p(rad, t1, -hz), p(rad, t1, hz), p(rad, t0, hz)]
            coords = [(-hz, t0 * rm), (-hz, t1 * rm), (hz, t1 * rm), (hz, t0 * rm)]
            part.face(element, [m @ q for q in verts], coords, mapping, r @ hint, group=group, smooth=smooth)
        for sign in (-1, 1):
            verts = [p(r_out, t0, sign * hz), p(r_out, t1, sign * hz), p(r_in, t1, sign * hz), p(r_in, t0, sign * hz)]
            coords = [(r_out, t0 * rm), (r_out, t1 * rm), (r_in, t1 * rm), (r_in, t0 * rm)]
            part.face(element, [m @ q for q in verts], coords, sides, r @ Vector((0, 0, sign)), group=f"side{sign}")
    if arc is not None:
        for t, sign in ((a_start, -1), (a_end, 1)):
            tangent = Vector((-math.sin(t), math.cos(t), 0)) * sign
            verts = [p(r_in, t, -hz), p(r_out, t, -hz), p(r_out, t, hz), p(r_in, t, hz)]
            coords = [(r_in, -hz), (r_out, -hz), (r_out, hz), (r_in, hz)]
            part.face(element, [m @ q for q in verts], coords, ends, r @ tangent)


def polygon(part, verts, coords, mapping, out_hint):
    part.face(part.element(), [Vector(v) for v in verts], coords, mapping, Vector(out_hint))


def material(name):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.diffuse_color = (*MATERIAL_PREVIEW.get(name, (None, None, (0.5, 0.5, 0.5)))[2], 1.0)
    return mat


def new_collection(name):
    old = bpy.data.collections.get(name)
    if old:
        for obj in list(old.objects):
            bpy.data.objects.remove(obj, do_unlink=True)
        return old
    collection = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(collection)
    return collection


def apply_yaw(collection, degrees):
    if degrees:
        rotation = Matrix.Rotation(math.radians(degrees), 4, "Z")
        for obj in collection.objects:
            if obj.type == "MESH":
                obj.data.transform(rotation)
            # Parents keep their orientation (only mesh data turns), so child nodes turn within their parent's frame too
            obj.location = rotation @ obj.location


# ---------------------------------------------------------------------------------------------------------------------
# Caravans
# ---------------------------------------------------------------------------------------------------------------------

def wooden_wheel(p, center, radius, width, spokes=6):
    x = center[0]
    ring(p["wood"], center, radius, radius - 0.028, width, 14, axis="x")
    ring(p["metal"], center, radius + 0.008, radius - 0.002, width * 0.75, 14,
         outer=Map("metal_ribs_small", "fit", METAL_DENSITY), axis="x")
    hub_length = width + 0.03
    cylinder(p["light"], center, 0.038, hub_length, 8, cap=Map("log_end_small", "fill"), axis="x")
    for k in range(spokes):
        angle = 360.0 * k / spokes + 15
        direction = rot_x(angle) @ Vector((0, 0, 1))
        start = Vector(center) + direction * 0.03
        end = Vector(center) + direction * (radius - 0.02)
        start.x = end.x = x
        beam(p["light"], start, end, 0.022, 0.018, up=(1, 0, 0))


def caravan_wheel(collection, faction, name, center, radius, width):
    """A wheel as its own model node, centred on its axle, so the game can spin it (see CaravanVisualizer)."""
    pivot = bpy.data.objects.new(name, None)
    pivot.location = center
    collection.objects.link(pivot)
    parts = Parts(collection, faction, prefix=name.lstrip("#"))
    wooden_wheel(parts, (0, 0, 0), radius, width)
    for part in parts.parts.values():
        part.ao_z = center[2]
    for obj in parts.build():
        obj.parent = pivot
    return pivot


ARM_HINGE = (0.42, 0.33)  # (y, z) of the drawbar's hinge on the front board; the rear arm's is mirrored
ARM_REACH = (0.36, -0.14)  # the arm's tip relative to its hinge while lowered for the road
PULLEY_RADIUS = 0.03  # must match CaravanVisualizer.PulleyRadius
# Nodes that only show while the caravan hangs from a zipline cable (see CaravanVisualizer)
CABLE_ONLY_NODES = ("#RearArm", "#Pulley")


def caravan_arm(collection, faction, name, sign, pulley_name, yoke):
    """A hinged arm as its own model node, pivoting on its hinge. On the road the front one is the drawbar; on a
    zipline the game swings both arms straight up so the wagon hangs from the pulleys on their tips like a gondola."""
    pivot = bpy.data.objects.new(name, None)
    pivot.location = (0, sign * ARM_HINGE[0], ARM_HINGE[1])
    collection.objects.link(pivot)
    reach = Vector((0, sign * ARM_REACH[0], ARM_REACH[1]))
    direction = reach.normalized()
    parts = Parts(collection, faction, prefix=name.lstrip("#"))
    plate = Map("metal_plate", "fit", METAL_DENSITY)
    beam(parts["light"], -direction * 0.02, reach, 0.04, 0.035, up=(1, 0, 0))
    cylinder(parts["metal"], (0, 0, 0), 0.022, 0.06, 8, side=plate, cap=plate, axis="x")
    if yoke:
        beam(parts["light"], reach - direction * 0.03 + Vector((-0.16, 0, 0)),
             reach - direction * 0.03 + Vector((0.16, 0, 0)), 0.035)
    # A fork that holds the pulley's axle
    pulley_at = reach + direction * (PULLEY_RADIUS + 0.008)
    for x in (-0.014, 0.014):
        beam(parts["metal"], reach - direction * 0.03 + Vector((x, 0, 0)), pulley_at + Vector((x, 0, 0)), 0.008, 0.03,
             maps=plate, up=(1, 0, 0))
    for part in parts.parts.values():
        part.ao_z = ARM_HINGE[1]
    for obj in parts.build():
        obj.parent = pivot

    pulley = bpy.data.objects.new(pulley_name, None)
    pulley.location = pulley_at
    pulley.parent = pivot
    collection.objects.link(pulley)
    wheel = Parts(collection, faction, prefix=pulley_name.lstrip("#"))
    ring(wheel["metal"], (0, 0, 0), PULLEY_RADIUS, PULLEY_RADIUS - 0.009, 0.016, 12,
         outer=Map("metal_ribs_small", "fit", METAL_DENSITY), axis="x")
    cylinder(wheel["light"], (0, 0, 0), PULLEY_RADIUS - 0.009, 0.01, 10, cap=Map("log_end_small", "fill"), axis="x")
    cylinder(wheel["metal"], (0, 0, 0), 0.007, 0.036, 6, side=plate, cap=plate, axis="x")
    for part in wheel.parts.values():
        part.ao_z = ARM_HINGE[1] + pulley_at.z
    for obj in wheel.build():
        obj.parent = pulley
    return pivot


def _is_cable_only(obj):
    while obj is not None:
        if obj.name.startswith(CABLE_ONLY_NODES):
            return True
        obj = obj.parent
    return False


def cargo_crate(p, center, size, rot=None, faces=("crate_diagonal", "crate_cross")):
    box(p["details"], center, (size, size, size), {
        "+x": Map(faces[0], "fill"), "-x": Map(faces[0], "fill"),
        "+y": Map(faces[1], "fill"), "-y": Map(faces[1], "fill"),
        "top": Map("crate_planks", "fill"), "bottom": Map("crate_planks", "fill"),
    }, rot=rot, grain=2)


def sack(p, center, radius, height, axis="z", rot=None):
    cylinder(p["details"], center, radius, height, 8, side=Map("sack", "fill"), cap=Map("sack_top", "fill"),
             axis=axis, rot=rot, radius_top=radius * 0.7)


PENNANTS = {"Folktails": "banner_green", "IronTeeth": "banner_blue"}


def build_caravan(faction):
    """One covered-wagon design for every faction; the faction only changes the materials and the pennant."""
    c = new_collection(f"Caravan.{faction}")
    p = Parts(c, faction)
    wheel_r, wheel_x, axle_z = 0.15, 0.29, 0.15
    for y in (-0.21, 0.21):
        for x in (-wheel_x, wheel_x):
            caravan_wheel(c, faction, f"#Wheel{len([o for o in c.objects if o.type == 'EMPTY'])}", (x, y, axle_z),
                          wheel_r, 0.04)
        cylinder(p["light"], (0, y, axle_z), 0.02, 2 * wheel_x - 0.02, 6, axis="x", cap=Map("log_end_small", "fill"))
    # Chassis and plank bed
    for x in (-0.16, 0.16):
        box(p["light"], (x, 0, 0.21), (0.05, 0.84, 0.05))
    for k, x in enumerate((-0.176, -0.088, 0.0, 0.088, 0.176)):
        box(p["wood"], (x, 0.004 * (k % 2), 0.2575), (0.084, 0.78, 0.035))
    # Side, front and tail boards made of individual planks, with iron corner brackets
    for x in (-0.235, 0.235):
        for z in (0.305, 0.365):
            box(p["wood"], (x, 0, z), (0.03, 0.78, 0.058))
    for z in (0.305, 0.365):
        box(p["wood"], (0, 0.375, z), (0.44, 0.03, 0.058))
    box(p["wood"], (0, -0.375, 0.305), (0.44, 0.03, 0.058))
    for x in (-0.235, 0.235):
        for y in (-0.375, 0.375):
            box(p["metal"], (x, y, 0.33), (0.042, 0.042, 0.13), Map("metal_plate", "fit", METAL_DENSITY))
        box(p["metal"], (x * 1.07, 0.0, 0.335), (0.006, 0.7, 0.022), Map("metal_ribs_small", "fit", METAL_DENSITY))
    # Canvas cover over the front half with the faction emblem on top, held by wooden hoops
    cover_y0, cover_y1, cover_z, cover_r = -0.07, 0.335, 0.39, 0.255
    cover_mid, cover_len = (cover_y0 + cover_y1) / 2, cover_y1 - cover_y0
    ring(p["details"], (0, cover_mid, cover_z), cover_r, cover_r - 0.012, cover_len, 12,
         outer=Map("cloth_emblem", "fill"), inner=Map("cloth_plain", "fill"), sides=Map("cloth_plain", "fill"),
         axis="y", arc=(0, 180))
    for y in (cover_y0, cover_y1):
        sign = -1 if y == cover_y0 else 1
        pts = [(cover_r * 0.985 * math.cos(math.radians(a)), y - sign * 0.004,
                cover_z + cover_r * 0.985 * math.sin(math.radians(a))) for a in range(0, 181, 15)]
        polygon(p["details"], pts, [(x, z) for x, _, z in pts], Map("cloth_plain", "fill"), (0, sign, 0))
    for y in (cover_y0 + 0.012, cover_mid, cover_y1 - 0.012):
        ring(p["light"], (0, y, cover_z), cover_r + 0.014, cover_r, 0.026, 12, axis="y", arc=(0, 180))
    # Cargo in the open rear half
    cargo_crate(p, (-0.1, -0.215, 0.355), 0.16)
    cargo_crate(p, (-0.095, -0.22, 0.5), 0.13, rot=rot_z(14), faces=("crate_framed", "crate_planks"))
    sack(p, (0.11, -0.13, 0.37), 0.068, 0.19)
    sack(p, (0.105, -0.29, 0.33), 0.06, 0.17, axis="x", rot=rot_z(10))
    # Hinged drawbar with a yoke, a rear arm for ziplines (hinge brackets on the front and tail boards), and a pennant
    caravan_arm(c, faction, "#Drawbar", 1, "#Pulley0", yoke=True)
    caravan_arm(c, faction, "#RearArm", -1, "#Pulley1", yoke=False)
    for sign in (-1, 1):
        box(p["metal"], (0, sign * 0.4, ARM_HINGE[1]), (0.075, 0.02, 0.05), Map("metal_plate", "fit", METAL_DENSITY))
    cylinder(p["light"], (0.235, 0.375, 0.53), 0.011, 0.36, 6, axis="z", cap=Map("log_end_small", "fill"))
    box(p["details"], (0.235, 0.31, 0.65), (0.006, 0.12, 0.075),
        {"+x": Map(PENNANTS[faction], "fill"), "-x": Map(PENNANTS[faction], "fill"), "all": Map("cloth_plain", "fill")})
    p.build()
    apply_yaw(c, CARAVAN_YAW)
    return c


# ---------------------------------------------------------------------------------------------------------------------
# Caravan post (3x3). The vanilla District Crossing is only the front half of a building (3x1, cut open at the back):
#   row 1: the District Crossing front, unchanged apart from a shadowed doorway one tile into the arch
#   row 2: the same front turned around, closing the house into a full gable-roofed building (its arch walled up)
#   row 3: an open storage yard - plank deck, rope rail, the side porches' shelves and cargo, and a parked wagon
# ---------------------------------------------------------------------------------------------------------------------

POST_SEAM_Y = -1.0  # the cut plane of the District Crossing (Blender coordinates; the front of the building is y=0)
POST_CENTER_X = -1.5


def _turn_around(center_y):
    """180 degree turn around the middle of the footprint at center_y. Unlike a mirror this keeps textures the right
    way round and swaps the two side porches, so each side shows different cargo."""
    return (Matrix.Translation((POST_CENTER_X, center_y, 0)) @ Matrix.Rotation(math.pi, 4, "Z")
            @ Matrix.Translation((-POST_CENTER_X, -center_y, 0)))


POST_BACK = _turn_around(POST_SEAM_Y)  # row 1 -> row 2
POST_YARD = _turn_around(-1.5)  # row 1 -> row 3, keeping the rail and porches on the outside
POST_WAGON = (Matrix.Translation((POST_CENTER_X - 0.12, -2.5, 0.02)) @ Matrix.Rotation(math.radians(84), 4, "Z"))
VANILLA_POST = "DistrictCrossing.{faction}{stage}.Model.timbermesh"
POST_VIEWS = ((90, 22, 2.4), (-90, 22, 2.4), (125, 35, 2.7), (-55, 35, 2.7), (0, 15, 2.4))  # front, back, ...


class Corner:
    __slots__ = ("pos", "normal", "uv", "color")

    def __init__(self, pos, normal, uv, color):
        self.pos, self.normal, self.uv, self.color = pos, normal, uv, color


class Tri:
    __slots__ = ("material", "corners")

    def __init__(self, material_name, corners):
        self.material, self.corners = material_name, corners

    def centroid(self):
        return sum((c.pos for c in self.corners), Vector()) / 3


def _unity_to_blender(v):
    """Inverse of the exporter's Blender (x, y, z) -> Unity (-x, z, -y)."""
    return Vector((-v[0], -v[2], v[1]))


def load_timbermesh(path):
    """Reads a .timbermesh into triangles (Blender coordinates) and its empty slot nodes."""
    import addon_utils
    addon_utils.enable("timbermesh_blender_plugin", default_set=False)
    from timbermesh_blender_plugin import model_pb2
    model = model_pb2.Model()
    with open(path, "rb") as f:
        model.ParseFromString(zlib.decompress(f.read()))
    tris, slots = [], []
    for node in model.nodes:
        if not node.meshes:
            r = node.rotation
            slots.append((node.name, _unity_to_blender((node.position.x, node.position.y, node.position.z)),
                          Quaternion((r.w, r.x, r.z, -r.y))))
            continue
        props = {}
        for prop in node.vertexProperties:
            n = prop.scalarTypeDimension
            values = struct.unpack("<%df" % (len(prop.data) // 4), prop.data)
            props[prop.name] = [values[i:i + n] for i in range(0, len(values), n)]
        offset = Vector((node.position.x, node.position.y, node.position.z))
        positions = [_unity_to_blender(Vector(p) + offset) for p in props["position"]]
        normals = [_unity_to_blender(n).normalized() for n in props["normal"]]
        colors = props.get("color", [(1.0, 1.0, 1.0, 1.0)] * len(positions))
        for mesh in node.meshes:
            idx = list(mesh.indices)
            for t in range(0, len(idx), 3):
                # The handedness change flips the winding
                corners = [Corner(positions[i].copy(), normals[i].copy(), tuple(props["uv0"][i][:2]), tuple(colors[i]))
                           for i in (idx[t], idx[t + 2], idx[t + 1])]
                tris.append(Tri(mesh.material, corners))
    return tris, slots


def transform_tris(tris, matrix):
    normal_matrix = matrix.to_3x3().inverted().transposed()
    flip = matrix.to_3x3().determinant() < 0
    result = []
    for tri in tris:
        corners = [Corner(matrix @ c.pos, (normal_matrix @ c.normal).normalized(), c.uv, c.color) for c in tri.corners]
        if flip:
            corners.reverse()
        result.append(Tri(tri.material, corners))
    return result


def connected_pieces(tris):
    """Groups triangles into the separate pieces of the model (touching by vertex position)."""
    parent = list(range(len(tris)))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    owner = {}
    for i, tri in enumerate(tris):
        for c in tri.corners:
            j = owner.setdefault((round(c.pos.x, 4), round(c.pos.y, 4), round(c.pos.z, 4)), i)
            a, b = find(i), find(j)
            if a != b:
                parent[a] = b
    pieces = {}
    for i, tri in enumerate(tris):
        pieces.setdefault(find(i), []).append(tri)
    return list(pieces.values())


def bounds(tris):
    points = [c.pos for tri in tris for c in tri.corners]
    return (Vector([min(p[i] for p in points) for i in range(3)]),
            Vector([max(p[i] for p in points) for i in range(3)]))


def tris_to_object(collection, name, tris):
    """Builds one mesh object, keeping the source normals, UVs and vertex colours exactly."""
    materials = sorted({tri.material for tri in tris})
    bm = bmesh.new()
    uv_layer = bm.loops.layers.uv.new("UVMap")
    color_layer = bm.loops.layers.color.new("Col")
    cache, normals = {}, []
    for tri in tris:
        verts = []
        for c in tri.corners:
            key = (round(c.pos.x, 5), round(c.pos.y, 5), round(c.pos.z, 5))
            if key not in cache:
                cache[key] = bm.verts.new(c.pos)
            verts.append(cache[key])
        try:
            face = bm.faces.new(verts)
        except ValueError:  # the back of a double-sided face reuses the same corners
            face = bm.faces.new([bm.verts.new(c.pos) for c in tri.corners])
        face.material_index = materials.index(tri.material)
        face.smooth = True
        for loop, c in zip(face.loops, tri.corners):
            loop[uv_layer].uv = c.uv
            loop[color_layer] = c.color
            normals.append(c.normal)
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    for material_name in materials:
        mesh.materials.append(material(material_name))
    mesh.normals_split_custom_set(normals)
    obj = bpy.data.objects.new(name, mesh)
    collection.objects.link(obj)
    return obj


def _planar_uv(tri):
    """Affine (x, z) -> uv mapping of a wall triangle facing +y, used to continue its texture onto new faces."""
    rows = Matrix([(c.pos.x, c.pos.z, 1.0) for c in tri.corners]).inverted()
    u = rows @ Vector([c.uv[0] for c in tri.corners])
    v = rows @ Vector([c.uv[1] for c in tri.corners])
    return lambda x, z: (u[0] * x + u[1] * z + u[2], v[0] * x + v[1] * z + v[2])


def _back_arch_panel(tris, arch):
    """Plaster panel just inside the back facade that closes the back copy of the arch (two-sided)."""
    walls = [t for t in tris if t.material.startswith("Plaster_") and t.corners[0].normal.y > 0.95
             and abs(t.corners[0].normal.y - t.corners[1].normal.y) < 1e-3]
    facade = max(walls, key=lambda t: t.centroid().y)
    front_uv = _planar_uv(facade)
    to_front = POST_BACK.inverted()

    def uv(p):
        q = to_front @ p
        return front_uv(q.x, q.z)

    facade_y = (POST_BACK @ facade.centroid()).y
    lo, hi = bounds(transform_tris(arch, POST_BACK))
    result = []
    for y, normal in ((facade_y + 0.012, Vector((0, -1, 0))), (facade_y + 0.014, Vector((0, 1, 0)))):
        quad = [Vector((lo.x, y, lo.z)), Vector((hi.x, y, lo.z)), Vector((hi.x, y, hi.z)), Vector((lo.x, y, hi.z))]
        if normal.y > 0:
            quad.reverse()
        corners = [Corner(p, normal.copy(), uv(p), (1.0, 1.0, 1.0, 1.0)) for p in quad]
        result += [Tri(facade.material, [corners[0], corners[1], corners[2]]),
                   Tri(facade.material, [corners[0], corners[2], corners[3]])]
    return result


def _front_rail_gap_rope(pieces):
    """A copy of the front rope rail stretched across the entrance gap (used on the back side, which has no door)."""
    ropes = []
    for piece in pieces:
        lo, hi = bounds(piece)
        if piece[0].material.startswith("Details") and lo.y > -0.12 and hi.z - lo.z < 0.06 and hi.x - lo.x > 0.6:
            ropes.append((lo.x, hi.x, piece))
    if len(ropes) != 2:
        return []
    (left_lo, left_hi, left), (right_lo, _, _) = sorted(ropes, key=lambda r: r[0])
    stretch = (right_lo - left_hi) / (left_hi - left_lo)
    matrix = (Matrix.Translation((left_hi, 0, 0)) @ Matrix.Scale(stretch, 4, (1, 0, 0))
              @ Matrix.Translation((-left_lo, 0, 0)))
    return transform_tris(left, matrix)


def _is_doormat(tri, doormat_material):
    c = tri.centroid()
    return (tri.material == doormat_material and max(p.pos.z for p in tri.corners) < 0.03 and -2.0 < c.x < -1.0)


def _floor_material(tris, front):
    for tri in tris:
        c = tri.centroid()
        if max(p.pos.z for p in tri.corners) < 0.03 and -2.0 < c.x < -1.0 and (c.y > -0.35) == front:
            return tri.material
    raise RuntimeError("Caravans: could not find the District Crossing floor")


def _piece_kind(piece):
    """Sorts the loose pieces of the District Crossing front by what they are, from their position and size."""
    lo, hi = bounds(piece)
    size = hi - lo
    details = piece[0].material.startswith("Details")
    if lo.y < POST_SEAM_Y + 0.01 and size.y < 0.15:
        return "ridge" if size.x > 1.0 and lo.z > 1.4 else "seam"
    if (not piece[0].material.startswith("Plaster_") and 1.0 < size.x < 1.3 and hi.z < 1.2
            and lo.y < POST_SEAM_Y + 0.01 and hi.y > -0.25):
        return "arch"
    if size.x > 2.9 and size.y > 0.9:
        return "floor"
    if hi.z < 0.35 and (lo.y > -0.12 or lo.x < -2.88 or hi.x > -0.12):
        if hi.y < -0.75:
            return "rail_inner"  # the rail corner next to the cut
        if lo.y > -0.12 and -2.12 < lo.x and hi.x < -0.88:
            return "rail_gap"  # the posts either side of the entrance
        if details and lo.y > -0.12 and size.z < 0.06 and size.x > 0.6:
            return "rail_front"
        return "rail_side" if size.y > 0.6 else "rail"
    if details and hi.z < 0.45 and lo.y > -0.35:
        return "crate"
    if (hi.x <= -2.2 or lo.x >= -0.8) and hi.y <= -0.3 and hi.z > 0.45:
        return "porch"
    return "house"


def _darken(tris, factor):
    for tri in tris:
        for c in tri.corners:
            f = factor(c.pos)
            c.color = (c.color[0] * f, c.color[1] * f, c.color[2] * f, c.color[3])


def _doorway_shadow(arch):
    """Shades the arch tunnel darker towards its back and closes it with a dark wall one tile in, so the entrance
    reads as a doorway into the building (like vanilla workshops) instead of a passage."""
    lo, hi = bounds(arch)

    def factor(p):
        t = min(max((-0.35 - p.y) / 0.6, 0.0), 1.0)
        return 1.0 - 0.75 * t * t * (3 - 2 * t)

    _darken(arch, factor)
    y, normal, uv, dark = POST_SEAM_Y + 0.015, Vector((0, 1, 0)), arch[0].corners[0].uv, (0.07, 0.06, 0.05, 1.0)
    quad = [Vector((lo.x, y, lo.z)), Vector((hi.x, y, lo.z)), Vector((hi.x, y, hi.z)), Vector((lo.x, y, hi.z))]
    quad.reverse()
    corners = [Corner(p, normal.copy(), uv, dark) for p in quad]
    return [Tri(arch[0].material, [corners[0], corners[1], corners[2]]),
            Tri(arch[0].material, [corners[0], corners[2], corners[3]])]


def _stretch_y(tris, anchor_y, scale):
    return transform_tris(tris, Matrix.Translation((0, anchor_y, 0)) @ Matrix.Scale(scale, 4, (0, 1, 0))
                          @ Matrix.Translation((0, -anchor_y, 0)))


def _yard(by_kind, floor_material, doormat_material):
    """Row 3: the porches, cargo, rail and deck of the front row, turned to face outwards behind the house."""
    # The deck, plus the right-hand porch shelf, which is part of the floor mesh (the left one is a separate piece)
    deck = transform_tris([tri for tri in by_kind["floor"][0] if max(c.pos.z for c in tri.corners) < 0.031
                           or all(c.pos.x > -0.8 and c.pos.y < -0.3 and c.pos.z < 0.56 for c in tri.corners)],
                          POST_YARD)
    for tri in deck:
        if tri.material == doormat_material:
            tri.material = floor_material
    rail_end = -0.05
    side_ropes = [_stretch_y(piece, rail_end, (rail_end - POST_SEAM_Y + 0.05) / (rail_end - bounds(piece)[0].y))
                  for piece in by_kind["rail_side"]]
    # Open stalls: only the porch shelves and their cargo, without the awnings or the frames that held them
    porch = [tri for piece in by_kind["porch"] if piece[0].material.startswith("Details") or bounds(piece)[1].z < 0.6
             for tri in piece]
    rest = (porch + [tri for piece in side_ropes for tri in piece]
            + [tri for kind in ("rail", "rail_gap", "rail_front", "crate") for piece in by_kind[kind] for tri in piece]
            + _front_rail_gap_rope(by_kind["rail_front"]))
    return deck + transform_tris(rest, POST_YARD)


def object_tris(obj, matrix=Matrix.Identity(4)):
    """Triangles of a built Blender mesh object (used to park a caravan in the post's yard)."""
    mesh = obj.data
    mesh.calc_loop_triangles()
    matrix = matrix @ obj.matrix_world
    normal_matrix = matrix.to_3x3().inverted().transposed()
    uvs, colors = mesh.uv_layers[0].data, mesh.attributes["Col"]
    # Byte colours: read the stored values unconverted, as tris_to_object writes them back the same way
    color_of = ((lambda i: tuple(colors.data[i].color_srgb)) if colors.data_type == "BYTE_COLOR"
                else (lambda i: tuple(colors.data[i].color)))
    result = []
    for tri in mesh.loop_triangles:
        corners = [Corner(matrix @ mesh.vertices[mesh.loops[i].vertex_index].co,
                          (normal_matrix @ mesh.corner_normals[i].vector).normalized(), tuple(uvs[i].uv),
                          color_of(i)) for i in tri.loops]
        result.append(Tri(mesh.materials[tri.material_index].name, corners))
    return result


def extend_post(tris, finished, wagon=()):
    """Builds the 3x3 post from the District Crossing front (see the section comment above)."""
    by_kind = {}
    for piece in connected_pieces(tris):
        by_kind.setdefault(_piece_kind(piece), []).append(piece)
    for kind in ("ridge", "seam", "arch", "floor", "rail", "rail_side", "rail_inner", "rail_gap", "rail_front",
                 "crate", "porch", "house"):
        by_kind.setdefault(kind, [])
    front, back = [], []
    for kind, pieces in by_kind.items():
        for piece in pieces:
            lo, hi = bounds(piece)
            if kind == "seam":
                # Posts and braces along the cut: move them onto the middle of the building instead of doubling them
                front += transform_tris(piece, Matrix.Translation((0, POST_SEAM_Y - (lo.y + hi.y) / 2, 0)))
            else:
                front += piece
                # The yard continues straight on from the back of the house, so row 2 drops its back rail
                if kind not in ("rail_front", "rail_gap"):
                    back += transform_tris(piece, POST_BACK)
    if not finished:
        dirt = [tri for tri in tris if tri.material.startswith("Dirt")]
        return front + back + transform_tris(dirt, POST_YARD)
    if len(by_kind["arch"]) != 1 or len(by_kind["floor"]) != 1:
        raise RuntimeError("Caravans: could not find the District Crossing arch and floor")
    arch = by_kind["arch"][0]
    floor_material, doormat_material = _floor_material(tris, False), _floor_material(tris, True)
    for tri in back:
        # The lighter doormat boards in front of the arch become ordinary floor on the closed back side
        if _is_doormat(tri, doormat_material):
            tri.material = floor_material
    return (front + _doorway_shadow(arch) + back + _back_arch_panel(tris, arch)
            + _yard(by_kind, floor_material, doormat_material) + list(wagon))


def build_post(faction, vanilla_dir, stage="", wagon=None):
    path = os.path.join(vanilla_dir, VANILLA_POST.format(faction=faction, stage=stage))
    if not os.path.exists(path):
        raise FileNotFoundError(f"{path} is missing; run Art/extract_vanilla_models.py first")
    tris, slots = load_timbermesh(path)
    if wagon:
        bpy.context.view_layer.update()  # the wheels' world matrices come from their parents
    parked = ([tri for obj in wagon.objects if obj.type == "MESH" and not _is_cable_only(obj)
               for tri in object_tris(obj, POST_WAGON)]
              if wagon and not stage else [])
    c = new_collection(f"CaravanPost.{faction}{stage}")
    tris_to_object(c, c.name, extend_post(tris, finished=not stage, wagon=parked))
    for name, location, rotation in slots:
        slot = bpy.data.objects.new(name.rsplit(".", 1)[0] if name[-4:-3] == "." else name, None)
        slot.location = location
        slot.rotation_mode = "QUATERNION"
        slot.rotation_quaternion = rotation
        c.objects.link(slot)
    return c


# ---------------------------------------------------------------------------------------------------------------------
# Previews and export
# ---------------------------------------------------------------------------------------------------------------------

def _srgb_to_linear(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def setup_preview_materials(texture_dir):
    for name, (stems, tint, _) in MATERIAL_PREVIEW.items():
        mat = bpy.data.materials.get(name)
        path = next((os.path.join(texture_dir, s + e) for s in stems for e in (".png", ".jpg")
                     if os.path.exists(os.path.join(texture_dir, s + e))), None)
        if mat is None or path is None:
            continue
        mat.use_nodes = True
        nodes, links = mat.node_tree.nodes, mat.node_tree.links
        nodes.clear()
        out = nodes.new("ShaderNodeOutputMaterial")
        bsdf = nodes.new("ShaderNodeBsdfPrincipled")
        bsdf.inputs["Roughness"].default_value = 0.85
        tex = nodes.new("ShaderNodeTexImage")
        tex.image = bpy.data.images.load(path, check_existing=True)
        colors = nodes.new("ShaderNodeVertexColor")
        colors.layer_name = "Col"
        tinted = nodes.new("ShaderNodeVectorMath")
        tinted.operation = "MULTIPLY"
        tinted.inputs[1].default_value = [_srgb_to_linear(c) for c in tint]
        shaded = nodes.new("ShaderNodeVectorMath")
        shaded.operation = "MULTIPLY"
        links.new(tex.outputs["Color"], tinted.inputs[0])
        links.new(tinted.outputs["Vector"], shaded.inputs[0])
        links.new(colors.outputs["Color"], shaded.inputs[1])
        links.new(shaded.outputs["Vector"], bsdf.inputs["Base Color"])
        links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])


def _preview_stage():
    stage = bpy.data.collections.get("_Preview") or bpy.data.collections.new("_Preview")
    if stage.name not in bpy.context.scene.collection.children:
        bpy.context.scene.collection.children.link(stage)
    for obj in list(stage.objects):
        bpy.data.objects.remove(obj, do_unlink=True)
    ground_mat = bpy.data.materials.get("_PreviewGround") or bpy.data.materials.new("_PreviewGround")
    ground_mat.use_nodes = True
    nodes, links = ground_mat.node_tree.nodes, ground_mat.node_tree.links
    nodes.clear()
    coords = nodes.new("ShaderNodeTexCoord")
    checker = nodes.new("ShaderNodeTexChecker")
    checker.inputs["Scale"].default_value = 1.0
    checker.inputs["Color1"].default_value = (0.16, 0.2, 0.1, 1)
    checker.inputs["Color2"].default_value = (0.13, 0.17, 0.08, 1)
    bsdf = nodes.new("ShaderNodeBsdfPrincipled")
    out = nodes.new("ShaderNodeOutputMaterial")
    links.new(coords.outputs["Object"], checker.inputs["Vector"])
    links.new(checker.outputs["Color"], bsdf.inputs["Base Color"])
    links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
    mesh = bpy.data.meshes.new("_PreviewGround")
    bm = bmesh.new()
    bmesh.ops.create_grid(bm, x_segments=1, y_segments=1, size=4.0)
    bm.to_mesh(mesh)
    bm.free()
    mesh.materials.append(ground_mat)
    ground = bpy.data.objects.new("_PreviewGround", mesh)
    stage.objects.link(ground)
    sun = bpy.data.objects.new("_PreviewSun", bpy.data.lights.new("_PreviewSun", "SUN"))
    sun.data.energy = 3.5
    sun.rotation_euler = (math.radians(45), 0, math.radians(35))
    stage.objects.link(sun)
    camera = bpy.data.objects.new("_PreviewCamera", bpy.data.cameras.new("_PreviewCamera"))
    camera.data.lens = 50
    stage.objects.link(camera)
    world = bpy.context.scene.world or bpy.data.worlds.new("World")
    bpy.context.scene.world = world
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.55, 0.62, 0.7, 1)
    world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.9
    return ground, camera


def render_previews(collections, out_dir, views=((-55, 32, 2.6), (125, 32, 2.6), (0, 12, 2.4), (-90, 55, 2.8))):
    os.makedirs(out_dir, exist_ok=True)
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_EEVEE"
    scene.render.resolution_x, scene.render.resolution_y = 900, 700
    scene.view_settings.view_transform = "Standard"
    ground, camera = _preview_stage()
    scene.camera = camera
    layers = bpy.context.view_layer.layer_collection.children
    bpy.context.view_layer.update()
    for collection in collections:
        for layer in layers:
            layer.exclude = layer.name not in (collection.name, "_Preview")
        bounds = [obj.matrix_world @ Vector(corner) for obj in collection.objects if obj.type == "MESH"
                  for corner in obj.bound_box]
        lo = Vector([min(b[i] for b in bounds) for i in range(3)])
        hi = Vector([max(b[i] for b in bounds) for i in range(3)])
        target = (lo + hi) / 2
        ground.location = (round(target.x - 0.5) + 0.5, round(target.y - 0.5) + 0.5, 0)
        size = max((hi - lo).length, 1.0)
        for index, (azimuth, elevation, distance) in enumerate(views):
            az, el = math.radians(azimuth), math.radians(elevation)
            offset = Vector((math.cos(el) * math.cos(az), math.cos(el) * math.sin(az), math.sin(el)))
            camera.location = target + offset * distance * size / 1.2
            camera.rotation_euler = (target - camera.location).to_track_quat("-Z", "Y").to_euler()
            scene.render.filepath = os.path.join(out_dir, f"{collection.name}_{index}.png")
            bpy.ops.render.render(write_still=True)
    for layer in layers:
        layer.exclude = layer.name == "_Preview"


def export_collection(collection, path):
    import addon_utils
    addon_utils.enable("timbermesh_blender_plugin", default_set=False)
    from timbermesh_blender_plugin import timbermesh_exporter
    os.makedirs(os.path.dirname(path), exist_ok=True)
    # Object names are unique per file, so later models' nodes get a ".001" suffix; give them back their exact names
    for obj in collection.objects:
        match = re.fullmatch(r"(#.*?)\.(\d{3}|tmp)", obj.name)
        if obj.type == "EMPTY" and match:
            holder = bpy.data.objects.get(match.group(1))
            if holder:
                holder.name = match.group(1) + ".tmp"
            obj.name = match.group(1)
    settings = timbermesh_exporter.ExportSettings(bpy.context, True, True, False)
    timbermesh_exporter.Exporter.export_collection(collection, path, settings)
    print(f"Caravans: exported {collection.name} -> {path}")


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(prog="build_caravan_models.py")
    parser.add_argument("--export", action="store_true", help="export the caravan models into the mod")
    parser.add_argument("--export-posts", action="store_true", help="also export the caravan post models")
    parser.add_argument("--mod-dir", default=None, help="mod folder (default: the folder above Art/)")
    parser.add_argument("--render", default=None, help="folder to render preview images into")
    parser.add_argument("--textures", default=None, help="folder with the game's diffuse textures (previews only)")
    parser.add_argument("--vanilla-dir", default=None,
                        help="folder with the extracted District Crossing models (default: Art/vanilla)")
    args = parser.parse_args(argv)

    script = bpy.path.abspath(__file__) if "__file__" in globals() else ""
    art_dir = os.path.dirname(os.path.abspath(script))
    mod_dir = args.mod_dir or os.path.dirname(art_dir)
    vanilla_dir = args.vanilla_dir or os.path.join(art_dir, "vanilla")

    caravans = [build_caravan(faction) for faction in ("Folktails", "IronTeeth")]
    posts = []
    if os.path.isdir(vanilla_dir):
        for faction, caravan in zip(("Folktails", "IronTeeth"), caravans):
            posts += [build_post(faction, vanilla_dir, wagon=caravan),
                      build_post(faction, vanilla_dir, ".ConstructionStage0")]
    elif args.export_posts:
        raise SystemExit(f"{vanilla_dir} not found; run Art/extract_vanilla_models.py first")
    else:
        print(f"Caravans: {vanilla_dir} not found, skipping the caravan post models.")
    if args.textures:
        setup_preview_materials(args.textures)

    if args.export:
        for collection in caravans:
            export_collection(collection, os.path.join(mod_dir, "Data", "Caravans", f"{collection.name}.Model.timbermesh"))
    if args.export_posts:
        for collection in posts:
            path = os.path.join(mod_dir, "Data", "Buildings", "Storage", "CaravanPost",
                                f"{collection.name}.Model.timbermesh")
            export_collection(collection, path)
    if args.render:
        render_previews(caravans, args.render)
        render_previews(posts, args.render, views=POST_VIEWS)
    print("Caravans: collections built.")


if __name__ == "__main__":
    main()
