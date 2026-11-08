# Rooms of our own for the kinds Poly Haven has no panoramas of -- offices and shops -- modelled
# here from boxes and rendered in Blender (Cycles): our geometry, our render, CC0 by construction.
# Each room is rendered STRAIGHT as the box room the shader expects (render-rooms.py's box_view: the
# room x -A..A across, -1..1 up, 0..D deep in half-heights; a camera in front of the window, far enough
# that the back wall fills FAR of the picture; the picture squeezed square), so no panorama, no
# reprojection, and no daylight to grade away: by night only the room's own lights (neutral white:
# the shader colours each room by its lamp), by day the sky through the window and the lights on.
#
#   blender -b --factory-startup -P render-offices.py -- render <out dir> [name ...]
#       every room of ROOMS (or the ones named) -> <out>\<name>_night.exr / _day.exr (linear, 2x the
#       tile) and <out>\tiles\<name>_night.png / _day.png (graded like the atlas's)
#   blender -b --factory-startup -P render-offices.py -- list
#
# rooms.txt names one as "@<name>" (render-rooms.py's bake reads its EXRs).

import bpy
import bmesh
import importlib.util
import math
import mathutils
import os
import random
import sys

import numpy as np

_spec = importlib.util.spec_from_file_location("rr", os.path.join(os.path.dirname(os.path.abspath(__file__)), "render-rooms.py"))
rr = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(rr)

H = 3.0                        # floor to ceiling, metres (the box's 2 half-heights)
HALF = H / 2.0
RENDER = rr.NIGHT_TILE * 2     # rendered at twice the tile, box-filtered down
SAMPLES = 64

# Ready-made models (Poly Haven, CC0): models.txt, downloaded by fetch-models.ps1.
MODELS = os.path.join(os.environ.get("LOCALAPPDATA", ""), "RealGlass-build", "models")

# Plants: (slug, the parts kept -- a pack holds several plants side by side -- natural height in metres).
FLOOR_PLANTS = [
    ("potted_plant_01", None, 1.35),
    ("potted_plant_02", None, 0.84),
    ("pachira_aquatica_01", ["_bark_c", "_leaves_c"], 1.14),
    ("pachira_aquatica_01", ["_bark_d", "_leaves_d"], 1.89),
    ("pachira_aquatica_01", ["_bark_a", "_leaves_a"], 1.30),
]
DESK_PLANTS = [
    ("potted_plant_04", None),
    ("calathea_orbifolia_01", ["_b"]),
    ("anthurium_botany_01", ["_e"]),
    ("anthurium_botany_01", ["_f"]),
]


# ------------------------------------------------------------------ geometry

class Scene:
    """Boxes gathered per material into one mesh each (thousands of products stay fast)."""

    def __init__(self):
        self.meshes = {}
        self.mats = {}
        self.lights = []          # (object, night watts, day watts)
        self.templates = {}       # (slug, parts): (objects, size, [used])

    def mat(self, name, rgb, rough=0.6, metal=0.0, emit=None, glass=False):
        if name not in self.mats:
            self.mats[name] = dict(rgb=rgb, rough=rough, metal=metal, emit=emit, glass=glass)
        return name

    def _bm(self, m):
        if m not in self.meshes:
            self.meshes[m] = bmesh.new()
        return self.meshes[m]

    def box(self, x0, y0, z0, x1, y1, z1, m):
        bm = self._bm(m)
        xs, ys, zs = sorted((x0, x1)), sorted((y0, y1)), sorted((z0, z1))
        v = [bm.verts.new((xs[i], ys[j], zs[k])) for i in (0, 1) for j in (0, 1) for k in (0, 1)]
        for f in ((0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)):
            bm.faces.new([v[i] for i in f])

    def blob(self, x, y, z, rx, ry, rz, m, sub=2):
        bm = self._bm(m)
        r = bmesh.ops.create_icosphere(bm, subdivisions=sub, radius=1.0)
        for vert in r["verts"]:
            vert.co.x = x + vert.co.x * rx
            vert.co.y = y + vert.co.y * ry
            vert.co.z = z + vert.co.z * rz

    def cyl(self, x, y, z0, z1, r, m, seg=12):
        bm = self._bm(m)
        res = bmesh.ops.create_cone(bm, cap_ends=True, segments=seg, radius1=r, radius2=r, depth=z1 - z0)
        for vert in res["verts"]:
            vert.co.x += x
            vert.co.y += y
            vert.co.z += (z0 + z1) / 2.0

    def area(self, x, y, z, sx, sy, night, day, colour=(1.0, 1.0, 1.0), spot=None):
        """An area light facing down (and its visible panel is the caller's)."""
        d = bpy.data.lights.new("l", 'AREA')
        d.shape = 'RECTANGLE'
        d.size, d.size_y = sx, sy
        d.color = colour
        if spot is not None:
            d.spread = math.radians(spot)
        o = bpy.data.objects.new("l", d)
        o.location = (x, y, z)
        bpy.context.scene.collection.objects.link(o)
        self.lights.append((o, night, day))

    def point(self, x, y, z, night, day, radius=0.05, colour=(1.0, 1.0, 1.0)):
        d = bpy.data.lights.new("p", 'POINT')
        d.shadow_soft_size = radius
        d.color = colour
        o = bpy.data.objects.new("p", d)
        o.location = (x, y, z)
        bpy.context.scene.collection.objects.link(o)
        self.lights.append((o, night, day))

    def model(self, slug, x, y, rot, z, parts, height, scale, width):
        """
        A ready-made model under an empty at (x, y, z) turned rot degrees: imported once per scene
        (its parts kept by name suffix, the rest deleted, the footprint centred and the base at 0),
        then each further copy shares its meshes. height / width: scaled to that size; else scale.
        """
        key = (slug, tuple(parts) if parts else None)
        if key not in self.templates:
            path = os.path.join(MODELS, slug, slug + ".gltf")
            if not os.path.exists(path):
                raise IOError("model %s not downloaded (run fetch-models.ps1)" % slug)
            before = set(bpy.context.scene.objects)
            bpy.ops.import_scene.gltf(filepath=path)
            new = [o for o in bpy.context.scene.objects if o not in before]
            keep = []
            for o in new:
                base = o.name.split(".")[0]      # a second import of the same file names its parts "x.001"
                wanted = o.type == 'MESH' and (not parts or any(base.endswith(p) for p in parts))
                if wanted:
                    keep.append(o)
                else:
                    bpy.data.objects.remove(o, do_unlink=True)
            if not keep:
                raise ValueError("model %s: no part %s" % (slug, parts))
            for o in keep:
                o.parent = None
            bpy.context.view_layer.update()
            lo = [1e9] * 3
            hi = [-1e9] * 3
            for o in keep:
                for c in o.bound_box:
                    w = o.matrix_world @ mathutils.Vector(c)
                    lo = [min(lo[i], w[i]) for i in range(3)]
                    hi = [max(hi[i], w[i]) for i in range(3)]
            shift = mathutils.Vector(((lo[0] + hi[0]) / 2.0, (lo[1] + hi[1]) / 2.0, lo[2]))
            for o in keep:
                o.matrix_world = mathutils.Matrix.Translation(-shift) @ o.matrix_world
            self.templates[key] = (keep, [hi[i] - lo[i] for i in range(3)], [False])
        objs, size, used = self.templates[key]
        if height is not None:
            scale = height / max(size[2], 1e-3)
        elif width is not None:
            scale = width / max(size[0], 1e-3)
        empty = bpy.data.objects.new("m_" + slug, None)
        bpy.context.scene.collection.objects.link(empty)
        empty.location = (x, y, z)
        empty.rotation_euler = (0.0, 0.0, math.radians(rot))
        empty.scale = (scale, scale, scale)
        for o in objs:
            c = o if not used[0] else o.copy()
            if used[0]:
                bpy.context.scene.collection.objects.link(c)
            c.parent = empty
        used[0] = True
        return [size[i] * scale for i in range(3)]

    def build(self):
        for name, bm in self.meshes.items():
            me = bpy.data.meshes.new(name)
            bm.to_mesh(me)
            bm.free()
            o = bpy.data.objects.new(name, me)
            bpy.context.scene.collection.objects.link(o)
            spec = self.mats[name]
            m = bpy.data.materials.new(name)
            m.use_nodes = True
            p = m.node_tree.nodes["Principled BSDF"]
            p.inputs["Base Color"].default_value = (*spec["rgb"], 1.0)
            p.inputs["Roughness"].default_value = spec["rough"]
            p.inputs["Metallic"].default_value = spec["metal"]
            if spec["glass"]:
                p.inputs["Transmission Weight"].default_value = 1.0
                p.inputs["Roughness"].default_value = 0.02
            if spec["emit"] is not None:
                rgb, strength = spec["emit"]
                p.inputs["Emission Color"].default_value = (*rgb, 1.0)
                p.inputs["Emission Strength"].default_value = strength
            me.materials.append(m)


# ------------------------------------------------------------------ the room

class Room:
    def __init__(self, s, kind, r):
        self.s, self.r, self.kind = s, r, kind
        a, d = rr.SHAPES[kind]
        self.A, self.D = a, d
        self.W = 2.0 * a * HALF               # across, metres
        self.L = d * HALF                     # deep, metres
        self.x0, self.x1 = -self.W / 2.0, self.W / 2.0

    def shell(self, floor, wall, ceil, back=None, sides=None):
        s, W, L = self.s, self.W, self.L
        s.box(self.x0, 0, -0.05, self.x1, L, 0, floor)
        s.box(self.x0, 0, H, self.x1, L, H + 0.05, ceil)
        s.box(self.x0 - 0.1, 0, 0, self.x0, L, H, sides or wall)
        s.box(self.x1, 0, 0, self.x1 + 0.1, L, H, sides or wall)
        s.box(self.x0, L, 0, self.x1, L + 0.1, H, back or wall)
        # skirting
        sk = s.mat("skirting", (0.2, 0.2, 0.2), 0.5)
        s.box(self.x0, L - 0.02, 0, self.x1, L, 0.08, sk)
        s.box(self.x0, 0, 0, self.x0 + 0.02, L, 0.08, sk)
        s.box(self.x1 - 0.02, 0, 0, self.x1, L, 0.08, sk)

    def ceiling_grid(self, colour=(0.62, 0.62, 0.62)):
        s = self.s
        g = s.mat("tgrid", colour, 0.5)
        x = self.x0
        while x <= self.x1:
            s.box(x - 0.012, 0, H - 0.008, x + 0.012, self.L, H, g)
            x += 0.6
        y = 0.0
        while y <= self.L:
            s.box(self.x0, y - 0.012, H - 0.008, self.x1, y + 0.012, H, g)
            y += 0.6

    def troffers(self, style, day=1.0):
        """Ceiling lights: 'square' 600 panels, 'linear' recessed strips, 'pendant' suspended linear, 'down' downlights."""
        s, r = self.s, self.r
        panel = s.mat("panel", (1, 1, 1), 0.5, emit=((1, 1, 1), 6.0))
        if style == "square":
            nx = max(int(round(self.W / 2.4)), 2)
            ny = max(int(round(self.L / 2.4)), 2)
            for i in range(nx):
                for j in range(ny):
                    x = self.x0 + (i + 0.5) * self.W / nx
                    y = (j + 0.5) * self.L / ny
                    s.box(x - 0.29, y - 0.29, H - 0.015, x + 0.29, y + 0.29, H - 0.005, panel)
                    s.area(x, y, H - 0.02, 0.58, 0.58, 60, 60 * day)
        elif style == "linear":
            nx = max(int(round(self.W / 2.0)), 2)
            for i in range(nx):
                x = self.x0 + (i + 0.5) * self.W / nx
                for y0 in np.arange(0.6, self.L - 0.6, 1.8):
                    s.box(x - 0.06, y0, H - 0.015, x + 0.06, y0 + 1.4, H - 0.005, panel)
                    s.area(x, y0 + 0.7, H - 0.02, 0.12, 1.4, 45, 45 * day)
        elif style == "pendant":
            body = s.mat("pendbody", (0.08, 0.08, 0.08), 0.4, metal=0.5)
            nx = max(int(round(self.W / 2.2)), 2)
            for i in range(nx):
                x = self.x0 + (i + 0.5) * self.W / nx
                for yc in np.arange(1.4, self.L - 0.4, 2.2):
                    s.box(x - 0.07, yc - 0.8, 2.45, x + 0.07, yc + 0.8, 2.52, body)
                    s.box(x - 0.06, yc - 0.78, 2.44, x + 0.06, yc + 0.78, 2.45, panel)
                    for yy in (yc - 0.7, yc + 0.7):
                        s.box(x - 0.003, yy - 0.003, 2.52, x + 0.003, yy + 0.003, H, body)
                    s.area(x, yc, 2.43, 0.12, 1.56, 55, 55 * day)
        else:   # downlights
            disc = s.mat("disc", (1, 1, 1), 0.5, emit=((1, 1, 1), 12.0))
            nx = max(int(round(self.W / 1.5)), 3)
            ny = max(int(round(self.L / 1.5)), 3)
            for i in range(nx):
                for j in range(ny):
                    x = self.x0 + (i + 0.5) * self.W / nx
                    y = (j + 0.5) * self.L / ny
                    s.box(x - 0.07, y - 0.07, H - 0.012, x + 0.07, y + 0.07, H - 0.004, disc)
                    s.area(x, y, H - 0.02, 0.14, 0.14, 22, 22 * day, spot=70)

    # ---- furniture

    def chair(self, x, y, facing, colour=None):
        s, r = self.s, self.r
        base = s.mat("chairbase", (0.04, 0.04, 0.045), 0.6)
        c = colour or r.choice([(0.03, 0.03, 0.035), (0.03, 0.03, 0.035), (0.05, 0.09, 0.18), (0.15, 0.15, 0.16), (0.25, 0.06, 0.05)])
        seat = s.mat("seat_%.2f_%.2f_%.2f" % c, c, 0.85)
        x += r.uniform(-0.08, 0.08)
        s.box(x - 0.25, y - 0.24, 0.43, x + 0.25, y + 0.24, 0.5, seat)
        by = y - 0.24 * facing
        s.box(x - 0.23, by - 0.035, 0.56, x + 0.23, by + 0.035, 1.05, seat)
        s.box(x - 0.025, y - 0.025, 0.09, x + 0.025, y + 0.025, 0.43, base)
        s.box(x - 0.3, y - 0.03, 0.05, x + 0.3, y + 0.03, 0.09, base)
        s.box(x - 0.03, y - 0.3, 0.05, x + 0.03, y + 0.3, 0.09, base)
        for sx in (-0.25, 0.25):
            s.box(x + sx - 0.02, y - 0.12, 0.6, x + sx + 0.02, y + 0.12, 0.64, base)

    def monitor(self, x, y, facing, width=0.55, on=True):
        s, r = self.s, self.r
        bez = s.mat("bezel", (0.02, 0.02, 0.022), 0.3)
        glow = r.choice([(0.55, 0.65, 0.9), (0.8, 0.84, 0.92), (0.4, 0.5, 0.75), (0.85, 0.85, 0.85)])
        scr = s.mat("screen_%.2f" % glow[0], (0.02, 0.02, 0.03), 0.2, emit=(glow, 1.6 if on else 0.0))
        s.box(x - width / 2, y - 0.015, 0.96, x + width / 2, y + 0.015, 0.96 + width * 0.6, bez)
        f = -facing * 0.017
        s.box(x - width / 2 + 0.015, y + f - 0.003, 0.975, x + width / 2 - 0.015, y + f + 0.003, 0.945 + width * 0.6, scr)
        s.box(x - 0.025, y - 0.015, 0.75, x + 0.025, y + 0.015, 0.96, bez)
        s.box(x - 0.11, y - 0.09, 0.75, x + 0.11, y + 0.09, 0.762, bez)

    def desk(self, x, y, facing, top, w=1.6, d=0.8, monitors=None, chair=True, clutter=True):
        """A desk, its user facing +y (facing 1, towards the back) or -y (-1, towards the window)."""
        s, r = self.s, self.r
        leg = s.mat("deskleg", (0.55, 0.55, 0.57), 0.35, metal=0.8)
        s.box(x - w / 2, y - d / 2, 0.72, x + w / 2, y + d / 2, 0.75, top)
        for sx in (-w / 2 + 0.03, w / 2 - 0.03):
            s.box(x + sx - 0.025, y - d / 2 + 0.04, 0, x + sx + 0.025, y + d / 2 - 0.04, 0.72, leg)
        n = monitors if monitors is not None else r.choice([1, 2, 2, 2])
        my = y + (d / 2 - 0.15) * facing
        for k in range(n):
            self.monitor(x + (k - (n - 1) / 2.0) * 0.58, my, facing, on=r.random() < 0.8)
        if clutter:
            kb = s.mat("keyboard", (0.08, 0.08, 0.08), 0.5)
            s.box(x - 0.22, y - 0.08 * facing - 0.07, 0.75, x + 0.22, y - 0.08 * facing + 0.07, 0.765, kb)
            if r.random() < 0.6:
                paper = s.mat("paper", (0.86, 0.86, 0.83), 0.9)
                px = x + r.choice([-1, 1]) * (w / 2 - 0.3)
                s.box(px - 0.15, y - 0.11, 0.75, px + 0.15, y + 0.11, 0.75 + r.uniform(0.005, 0.06), paper)
            if r.random() < 0.4:
                mug = s.mat("mug_%d" % r.randint(0, 3), r.choice([(0.8, 0.8, 0.8), (0.6, 0.1, 0.08), (0.1, 0.2, 0.5), (0.9, 0.7, 0.1)]), 0.3)
                mx = x + r.uniform(-w / 2 + 0.2, w / 2 - 0.2)
                s.cyl(mx, y + r.uniform(-0.2, 0.2), 0.75, 0.85, 0.04, mug, seg=8)
            if r.random() < 0.35:
                self.small_plant(x + w / 2 - 0.15, y + facing * 0.25, 0.75)
        if chair and r.random() < 0.9:
            self.chair(x, y - (d / 2 + 0.25) * facing, facing)

    def model(self, slug, x, y, rot=0.0, z=0.0, parts=None, height=None, scale=1.0, width=None):
        """A ready-made model (models.txt), its footprint centred on x, y, standing at z, turned rot degrees."""
        return self.s.model(slug, x, y, rot, z, parts, height, scale, width)

    def small_plant(self, x, y, z):
        slug, parts = self.r.choice(DESK_PLANTS)
        self.model(slug, x, y, rot=self.r.uniform(0, 360), z=z, parts=parts)

    def plant(self, x, y, tall=1.3):
        # The floor plant nearest the height asked for, a little scaled (0.85..1.25) and turned.
        r = self.r
        options = sorted(FLOOR_PLANTS, key=lambda p: abs(p[2] - tall) + r.uniform(0.0, 0.35))
        slug, parts, natural = options[0]
        self.model(slug, x, y, rot=r.uniform(0, 360), parts=parts, scale=min(max(tall / natural, 0.85), 1.25))

    def cabinet(self, x0, y0, x1, y1, h, colour=None):
        s, r = self.s, self.r
        c = colour or r.choice([(0.72, 0.72, 0.72), (0.25, 0.25, 0.27), (0.62, 0.5, 0.36), (0.85, 0.85, 0.85)])
        body = s.mat("cab_%.2f_%.2f" % (c[0], c[2]), c, 0.45)
        line = s.mat("cabline", (0.1, 0.1, 0.1), 0.5)
        s.box(x0, y0, 0, x1, y1, h, body)
        front = y0 if y0 < y1 else y1
        # drawer / door lines on the face towards the window (-y)
        n = max(int(abs(x1 - x0) / 0.8), 1)
        for i in range(1, n):
            xx = x0 + (x1 - x0) * i / n
            s.box(xx - 0.005, front - 0.005, 0.02, xx + 0.005, front, h - 0.02, line)
        for zz in np.arange(0.35, h, 0.36):
            s.box(x0 + 0.02, front - 0.005, zz - 0.004, x1 - 0.02, front, zz + 0.004, line)

    def whiteboard(self, x, w=1.8, z0=0.9, z1=2.0):
        s, r = self.s, self.r
        L = self.L
        frame = s.mat("wbframe", (0.6, 0.6, 0.62), 0.3, metal=0.7)
        board = s.mat("wb", (0.92, 0.92, 0.92), 0.15)
        s.box(x - w / 2 - 0.02, L - 0.03, z0 - 0.02, x + w / 2 + 0.02, L, z1 + 0.02, frame)
        s.box(x - w / 2, L - 0.035, z0, x + w / 2, L - 0.03, z1, board)
        ink = s.mat("ink", r.choice([(0.05, 0.1, 0.4), (0.4, 0.05, 0.05), (0.05, 0.05, 0.05)]), 0.5)
        for k in range(r.randint(4, 9)):
            yy = z1 - 0.15 - k * 0.11
            xx = x - w / 2 + 0.1 + r.uniform(0, 0.3)
            s.box(xx, L - 0.037, yy, xx + r.uniform(0.2, w * 0.7), L - 0.035, yy + 0.012, ink)

    def pinboard(self, x, w=1.2, z0=1.0, z1=1.8):
        s, r = self.s, self.r
        L = self.L
        cork = s.mat("cork", (0.5, 0.36, 0.22), 0.9)
        s.box(x - w / 2, L - 0.02, z0, x + w / 2, L, z1, cork)
        for k in range(r.randint(5, 10)):
            c = r.choice([(0.9, 0.9, 0.88), (0.95, 0.85, 0.3), (0.6, 0.8, 0.95), (0.95, 0.6, 0.7)])
            note = s.mat("note_%.2f_%.2f" % (c[1], c[2]), c, 0.9)
            px, pz = r.uniform(x - w / 2 + 0.1, x + w / 2 - 0.25), r.uniform(z0 + 0.05, z1 - 0.3)
            s.box(px, L - 0.025, pz, px + r.uniform(0.1, 0.22), L - 0.02, pz + r.uniform(0.1, 0.28), note)

    def door(self, x, w=0.95):
        """A door in the back wall, ajar onto a dim corridor (a little depth past the box)."""
        s = self.s
        L = self.L
        frame = s.mat("doorframe", (0.85, 0.85, 0.85), 0.4)
        leaf = s.mat("doorleaf", self.r.choice([(0.55, 0.38, 0.22), (0.85, 0.85, 0.83), (0.3, 0.3, 0.32)]), 0.45)
        s.box(x - w / 2 - 0.06, L - 0.04, 0, x - w / 2, L + 0.02, 2.16, frame)
        s.box(x + w / 2, L - 0.04, 0, x + w / 2 + 0.06, L + 0.02, 2.16, frame)
        s.box(x - w / 2 - 0.06, L - 0.04, 2.1, x + w / 2 + 0.06, L + 0.02, 2.16, frame)
        s.box(x - w / 2, L - 0.035, 0, x + w / 2, L - 0.005, 2.1, leaf)

    def glass_wall(self, y, x0, x1):
        """A glazed partition across the room at depth y: mullions and frosted band."""
        s = self.s
        gl = s.mat("partition", (0.9, 0.95, 0.95), glass=True)
        mul = s.mat("mullion", (0.15, 0.15, 0.16), 0.4, metal=0.6)
        band = s.mat("frost", (0.85, 0.87, 0.88), 0.6)
        s.box(x0, y - 0.01, 0, x1, y + 0.01, H, gl)
        s.box(x0, y - 0.012, 1.0, x1, y + 0.012, 1.25, band)
        x = x0
        while x <= x1 + 1e-3:
            s.box(x - 0.025, y - 0.03, 0, x + 0.025, y + 0.03, H, mul)
            x += 1.2

    def blinds_back(self):
        pass


# ------------------------------------------------------------------ palettes

CARPETS = [(0.07, 0.075, 0.085), (0.1, 0.11, 0.13), (0.16, 0.15, 0.14), (0.09, 0.1, 0.09), (0.2, 0.19, 0.18)]
WALLS = [(0.8, 0.8, 0.79), (0.78, 0.76, 0.72), (0.86, 0.86, 0.86), (0.7, 0.72, 0.74)]
ACCENTS = [(0.15, 0.3, 0.5), (0.2, 0.4, 0.3), (0.6, 0.35, 0.15), (0.35, 0.35, 0.37), (0.55, 0.15, 0.15), (0.75, 0.6, 0.2)]
TOPS = [(0.88, 0.88, 0.86), (0.62, 0.45, 0.3), (0.4, 0.4, 0.42), (0.78, 0.66, 0.5)]


def office_shell(room, accent_chance=0.5, ceiling=True):
    s, r = room.s, room.r
    wall = s.mat("wall", r.choice(WALLS), 0.85)
    back = s.mat("accent", r.choice(ACCENTS), 0.85) if r.random() < accent_chance else wall
    room.shell(s.mat("carpet", r.choice(CARPETS), 0.95), wall, s.mat("ceiling", (0.84, 0.84, 0.84), 0.9), back=back)
    if ceiling:
        room.ceiling_grid()


# ------------------------------------------------------------------ offices (A 2.0, D 4.0: 6 x 6 m)

def openplan(room):
    s, r = room.s, room.r
    office_shell(room)
    room.troffers(r.choice(["square", "linear", "pendant"]))
    top = s.mat("top", r.choice(TOPS), 0.4)
    screen = s.mat("screen", r.choice([(0.25, 0.33, 0.42), (0.45, 0.46, 0.48), (0.3, 0.38, 0.28), (0.5, 0.42, 0.3)]), 0.95)
    for yc in (2.2, 4.4):
        for x in (-1.7, 0.0, 1.7):
            room.desk(x, yc - 0.42, 1, top)      # back to the window
            room.desk(x, yc + 0.42, -1, top)
        s.box(-2.5, yc - 0.015, 0.75, 2.5, yc + 0.015, 1.15, screen)
    back = r.choice(["cabinets", "whiteboard", "door", "pinboard"])
    if back == "cabinets":
        room.cabinet(-2.9, room.L - 0.45, 2.9, room.L, 1.1)
    elif back == "whiteboard":
        room.whiteboard(r.uniform(-1, 1))
    elif back == "door":
        room.door(r.choice([-1.8, 1.8]))
        room.pinboard(-r.choice([-1.8, 1.8]) * 0.6)
    else:
        room.pinboard(0)
    if r.random() < 0.7:
        room.plant(r.choice([-2.6, 2.6]), room.L - 0.4)


def benching(room):
    s, r = room.s, room.r
    office_shell(room)
    room.troffers(r.choice(["pendant", "linear"]))
    top = s.mat("top", r.choice(TOPS), 0.4)
    for yc in (2.0, 4.3):
        s.box(-2.6, yc - 0.8, 0.72, 2.6, yc + 0.8, 0.75, top)
        leg = s.mat("deskleg", (0.55, 0.55, 0.57), 0.35, metal=0.8)
        for x in (-2.55, 0, 2.55):
            s.box(x - 0.03, yc - 0.75, 0, x + 0.03, yc + 0.75, 0.72, leg)
        for x in (-1.95, -0.65, 0.65, 1.95):
            for f in (1, -1):
                if r.random() < 0.8:
                    room.monitor(x, yc - 0.15 * f, f, on=r.random() < 0.8)
                if r.random() < 0.75:
                    room.chair(x, yc - 1.05 * f, f)
    room.cabinet(-2.9, room.L - 0.4, -0.3, room.L, 1.2)
    room.whiteboard(1.6, w=2.0)
    room.plant(2.6, 0.8, tall=1.0)


def cubicles(room):
    s, r = room.s, room.r
    office_shell(room, accent_chance=0.2)
    room.troffers("square")
    panel = s.mat("cubepanel", r.choice([(0.42, 0.42, 0.4), (0.3, 0.34, 0.4), (0.5, 0.47, 0.42)]), 0.95)
    trim = s.mat("cubetrim", (0.6, 0.6, 0.6), 0.4, metal=0.5)
    top = s.mat("top", r.choice([(0.7, 0.68, 0.62), (0.5, 0.5, 0.5)]), 0.5)
    for yc in (1.9, 4.2):
        for xc in (-1.8, 0.0, 1.8):
            s.box(xc - 0.9, yc + 0.85, 0, xc + 0.9, yc + 0.9, 1.45, panel)          # back panel
            s.box(xc - 0.92, yc + 0.84, 1.45, xc + 0.92, yc + 0.91, 1.48, trim)
            s.box(xc - 0.9, yc - 0.6, 0, xc - 0.85, yc + 0.9, 1.45, panel)          # side panel
            s.box(xc - 0.91, yc - 0.6, 1.45, xc - 0.84, yc + 0.9, 1.48, trim)
            room.desk(xc, yc + 0.45, 1, top, w=1.5, d=0.7)
            if r.random() < 0.5:
                bin_ = s.mat("overhead", (0.55, 0.55, 0.55), 0.5)
                s.box(xc - 0.75, yc + 0.55, 1.1, xc + 0.75, yc + 0.85, 1.4, bin_)
    room.cabinet(-2.9, room.L - 0.4, 2.9, room.L, 1.3)


def meeting(room):
    s, r = room.s, room.r
    office_shell(room, accent_chance=0.8)
    room.troffers(r.choice(["down", "pendant", "square"]))
    wood = s.mat("mtable", r.choice([(0.35, 0.22, 0.12), (0.85, 0.85, 0.83), (0.15, 0.15, 0.16), (0.6, 0.45, 0.3)]), 0.3)
    leg = s.mat("deskleg", (0.55, 0.55, 0.57), 0.35, metal=0.8)
    s.box(-1.0, 1.4, 0.72, 1.0, 4.6, 0.76, wood)
    s.box(-0.25, 1.8, 0, 0.25, 4.2, 0.72, leg)
    chair = r.choice([(0.03, 0.03, 0.035), (0.15, 0.15, 0.16), (0.05, 0.09, 0.18)])
    for y in (1.8, 2.7, 3.6, 4.4):
        for sx, f in ((-1.35, 0), (1.35, 0)):
            s_x = sx
            room.chair(s_x, y, 1 if sx < 0 else -1, colour=chair)
    tv = s.mat("tv", (0.02, 0.02, 0.02), 0.2, emit=((0.35, 0.45, 0.75), 0.8 if r.random() < 0.5 else 0.0))
    bez = s.mat("bezel", (0.02, 0.02, 0.022), 0.3)
    s.box(-1.05, room.L - 0.08, 1.0, 1.05, room.L - 0.03, 2.2, bez)
    s.box(-1.0, room.L - 0.085, 1.05, 1.0, room.L - 0.08, 2.15, tv)
    room.cabinet(-2.9, room.L - 0.5, -1.4, room.L, 0.75)
    room.cabinet(1.4, room.L - 0.5, 2.9, room.L, 0.75)
    room.plant(-2.6, 0.7, tall=1.1)


def private(room):
    s, r = room.s, room.r
    office_shell(room, accent_chance=0.4)
    room.troffers(r.choice(["down", "square"]))
    wood = s.mat("execwood", r.choice([(0.3, 0.18, 0.1), (0.5, 0.36, 0.22), (0.15, 0.15, 0.16)]), 0.35)
    # the desk faces the door, its back to the bookshelf
    s.box(-1.0, 3.6, 0.72, 1.0, 4.5, 0.76, wood)
    s.box(-1.0, 3.6, 0, -0.95, 4.5, 0.72, wood)
    s.box(0.95, 3.6, 0, 1.0, 4.5, 0.72, wood)
    s.box(-0.95, 3.62, 0.1, 0.95, 3.65, 0.72, wood)
    room.monitor(0.2, 3.85, -1, width=0.6)
    room.chair(0, 4.85, -1, colour=(0.03, 0.03, 0.035))
    for x in (-0.6, 0.6):
        room.chair(x, 2.9, 1)
    shelf = s.mat("shelf", (0.3, 0.2, 0.12) if r.random() < 0.5 else (0.85, 0.85, 0.83), 0.5)
    books = [s.mat("book%d" % i, c, 0.8) for i, c in enumerate([(0.4, 0.1, 0.08), (0.1, 0.15, 0.3), (0.7, 0.65, 0.5),
                                                                (0.15, 0.25, 0.12), (0.8, 0.8, 0.75), (0.1, 0.1, 0.1)])]
    L = room.L
    s.box(-2.4, L - 0.35, 0, 2.4, L, 0.05, shelf)
    for k in range(5):
        z = 0.05 + k * 0.42
        s.box(-2.4, L - 0.35, z, 2.4, L, z + 0.025, shelf)
        x = -2.35
        while x < 2.3:
            if r.random() < 0.12:
                x += r.uniform(0.15, 0.4)
                continue
            w = r.uniform(0.025, 0.06)
            s.box(x, L - 0.32, z + 0.025, x + w, L - 0.08, z + 0.025 + r.uniform(0.2, 0.33), r.choice(books))
            x += w + 0.004
    for x in (-2.4, -1.2, 0, 1.2, 2.4):
        s.box(x - 0.02, L - 0.35, 0, x + 0.02, L, 2.15, shelf)
    room.plant(-2.6, 1.2)
    sofa = s.mat("sofa", r.choice([(0.12, 0.12, 0.13), (0.35, 0.2, 0.12), (0.2, 0.25, 0.32)]), 0.85)
    s.box(2.2, 0.8, 0, 2.95, 2.8, 0.45, sofa)
    s.box(2.75, 0.8, 0, 2.95, 2.8, 0.85, sofa)
    s.box(2.2, 0.8, 0, 2.95, 0.98, 0.62, sofa)
    s.box(2.2, 2.62, 0, 2.95, 2.8, 0.62, sofa)
    lamp = s.mat("floorlamp", (0.9, 0.85, 0.7), 0.6, emit=((1, 0.95, 0.85), 4.0))
    s.cyl(2.6, 3.3, 1.35, 1.65, 0.18, lamp)
    s.point(2.6, 3.3, 1.5, 15, 10)


def reception(room):
    s, r = room.s, room.r
    office_shell(room, accent_chance=1.0)
    room.troffers("down")
    desk = s.mat("counter", r.choice([(0.85, 0.85, 0.83), (0.35, 0.22, 0.12), (0.1, 0.1, 0.11)]), 0.3)
    top = s.mat("countertop", (0.92, 0.92, 0.9), 0.2)
    s.box(-1.6, 3.8, 0, 1.6, 4.4, 1.1, desk)
    s.box(-1.65, 3.75, 1.1, 1.65, 4.45, 1.14, top)
    room.monitor(-0.6, 4.2, -1, width=0.45)
    room.chair(-0.6, 4.9, -1)
    # a wall panel with a band of light (no lettering)
    L = room.L
    panel = s.mat("featurepanel", r.choice([(0.35, 0.22, 0.12), (0.2, 0.2, 0.22), (0.7, 0.68, 0.65)]), 0.5)
    glow = s.mat("featureglow", (1, 1, 1), 0.5, emit=((1, 1, 1), 5.0))
    s.box(-2.0, L - 0.06, 0.0, 2.0, L, H, panel)
    s.box(-1.2, L - 0.065, 1.7, 1.2, L - 0.06, 1.78, glow)
    sofa = s.mat("sofa", r.choice([(0.12, 0.12, 0.13), (0.45, 0.3, 0.18), (0.2, 0.3, 0.4)]), 0.85)
    for x0 in (-2.9, 1.9):
        s.box(x0, 1.0, 0, x0 + 1.0, 2.8, 0.42, sofa)
        sx = x0 if x0 < 0 else x0 + 0.8
        s.box(sx, 1.0, 0, sx + 0.2, 2.8, 0.8, sofa)
    tbl = s.mat("lowtable", (0.15, 0.15, 0.15), 0.3)
    s.box(-0.5, 1.5, 0.35, 0.5, 2.3, 0.38, tbl)
    s.box(-0.45, 1.55, 0, 0.45, 2.25, 0.35, tbl)
    room.plant(-2.5, L - 0.5, tall=1.6)
    room.plant(2.5, L - 0.5, tall=1.6)


def breakout(room):
    s, r = room.s, room.r
    floor = s.mat("woodfloor", r.choice([(0.45, 0.33, 0.22), (0.3, 0.3, 0.3), (0.55, 0.45, 0.35)]), 0.5)
    wall = s.mat("wall", r.choice(WALLS), 0.85)
    room.shell(floor, wall, s.mat("ceiling", (0.84, 0.84, 0.84), 0.9))
    shade = s.mat("shade", r.choice([(0.05, 0.05, 0.05), (0.85, 0.85, 0.85), (0.6, 0.4, 0.15)]), 0.4)
    bulb = s.mat("bulb", (1, 1, 1), 0.5, emit=((1, 0.95, 0.85), 10.0))
    wood = s.mat("btable", (0.72, 0.58, 0.4), 0.4)
    leg = s.mat("deskleg", (0.55, 0.55, 0.57), 0.35, metal=0.8)
    for x in (-1.6, 1.6):
        for y in (1.6, 3.4):
            s.box(x - 0.6, y - 0.4, 0.72, x + 0.6, y + 0.4, 0.75, wood)
            s.box(x - 0.03, y - 0.03, 0, x + 0.03, y + 0.03, 0.72, leg)
            for f in (1, -1):
                room.chair(x + r.uniform(-0.2, 0.2), y - 0.65 * f, f)
            s.cyl(x, y, 2.1, 2.3, 0.22, shade)
            s.cyl(x, y, 2.08, 2.1, 0.15, bulb)
            s.box(x - 0.005, y - 0.005, 2.3, x + 0.005, y + 0.005, H, shade)
            s.point(x, y, 2.0, 30, 10, radius=0.1)
    L = room.L
    cab = s.mat("kitchen", r.choice([(0.85, 0.85, 0.83), (0.15, 0.2, 0.25), (0.3, 0.4, 0.3)]), 0.4)
    worktop = s.mat("worktop", (0.25, 0.25, 0.26), 0.3)
    s.box(-2.9, L - 0.65, 0, 1.8, L, 0.88, cab)
    s.box(-2.9, L - 0.66, 0.88, 1.8, L, 0.92, worktop)
    s.box(-2.9, L - 0.35, 1.5, 1.8, L, 2.25, cab)
    fridge = s.mat("fridge", (0.7, 0.7, 0.72), 0.3, metal=0.7)
    s.box(1.9, L - 0.7, 0, 2.9, L, 2.0, fridge)
    under = s.mat("undercab", (1, 1, 1), 0.5, emit=((1, 0.95, 0.85), 4.0))
    s.box(-2.85, L - 0.33, 1.49, 1.75, L - 0.3, 1.5, under)
    s.area(-0.5, L - 0.3, 1.48, 4.5, 0.06, 12, 4)
    coffee = s.mat("coffee", (0.1, 0.1, 0.1), 0.3, metal=0.5)
    s.box(-1.0, L - 0.55, 0.92, -0.6, L - 0.15, 1.35, coffee)
    room.plant(-2.6, 0.7, tall=1.0)


def printroom(room):
    s, r = room.s, room.r
    office_shell(room, accent_chance=0.0)
    room.troffers("square")
    L = room.L
    shelfm = s.mat("metalshelf", (0.55, 0.57, 0.6), 0.4, metal=0.7)
    boxes = [s.mat("archbox%d" % i, c, 0.85) for i, c in enumerate([(0.75, 0.62, 0.42), (0.85, 0.85, 0.82), (0.2, 0.3, 0.5), (0.6, 0.5, 0.35)])]
    for x0 in (-2.9, -0.9, 1.1):
        for k in range(5):
            z = 0.1 + k * 0.45
            s.box(x0, L - 0.5, z, x0 + 1.8, L, z + 0.025, shelfm)
            x = x0 + 0.05
            while x < x0 + 1.7:
                w = r.uniform(0.25, 0.4)
                if r.random() < 0.85:
                    s.box(x, L - 0.45, z + 0.025, x + w - 0.02, L - 0.05, z + 0.025 + r.uniform(0.25, 0.35), r.choice(boxes))
                x += w
        for xx in (x0, x0 + 1.8):
            s.box(xx - 0.02, L - 0.5, 0, xx + 0.02, L, 2.4, shelfm)
    printer = s.mat("printer", (0.85, 0.85, 0.85), 0.4)
    dark = s.mat("printerdark", (0.15, 0.15, 0.16), 0.4)
    s.box(-2.0, 2.5, 0, -0.9, 3.2, 1.0, printer)
    s.box(-1.95, 2.48, 0.6, -0.95, 2.5, 0.75, dark)
    s.box(-2.0, 2.5, 1.0, -0.9, 3.2, 1.25, dark)
    s.box(0.6, 2.4, 0, 2.6, 3.0, 0.9, printer)
    paper = s.mat("paper", (0.86, 0.86, 0.83), 0.9)
    s.box(0.8, 2.5, 0.9, 1.2, 2.8, 1.05, paper)
    room.cabinet(-2.9, 0.8, -2.4, 2.0, 1.3)


OFFICE_LAYOUTS = [("openplan", openplan, 5), ("benching", benching, 3), ("cubicles", cubicles, 3), ("meeting", meeting, 4),
                  ("private", private, 4), ("reception", reception, 2), ("breakout", breakout, 2), ("print", printroom, 1)]


# ------------------------------------------------------------------ shops (A 1.8, D 3.5: 5.4 x 5.25 m)

def shop_shell(room, floor=None, wall=None):
    s, r = room.s, room.r
    f = s.mat("shopfloor", floor or r.choice([(0.8, 0.8, 0.78), (0.35, 0.35, 0.36), (0.55, 0.42, 0.3), (0.9, 0.88, 0.84)]), 0.35)
    w = s.mat("shopwall", wall or r.choice([(0.9, 0.9, 0.88), (0.85, 0.83, 0.8), (0.3, 0.3, 0.32)]), 0.8)
    room.shell(f, w, s.mat("shopceil", (0.88, 0.88, 0.88), 0.9))


def track(room, n=3, spots=4):
    """Track lights: rails across the ceiling with spots."""
    s, r = room.s, room.r
    rail = s.mat("rail", (0.08, 0.08, 0.08), 0.4, metal=0.5)
    head = s.mat("spothead", (0.1, 0.1, 0.1), 0.4, metal=0.5)
    lens = s.mat("lens", (1, 1, 1), 0.5, emit=((1, 0.97, 0.92), 15.0))
    for i in range(n):
        y = (i + 0.7) * room.L / (n + 0.4)
        s.box(room.x0 + 0.3, y - 0.02, H - 0.04, room.x1 - 0.3, y + 0.02, H, rail)
        for k in range(spots):
            x = room.x0 + (k + 0.5) * room.W / spots
            s.box(x - 0.05, y - 0.05, H - 0.2, x + 0.05, y + 0.05, H - 0.04, head)
            s.box(x - 0.04, y - 0.04, H - 0.205, x + 0.04, y + 0.04, H - 0.2, lens)
            s.area(x, y, H - 0.21, 0.08, 0.08, 30, 30, spot=50)


def products(room, x0, x1, y0, y1, z0, z1, rows, palette, fill=0.9, depth=None):
    """Shelf rows of product boxes facing the window."""
    s, r = room.s, room.r
    mats = [s.mat("prod_%.2f_%.2f_%.2f" % c, c, 0.5) for c in palette]
    if abs(x1 - x0) < abs(y1 - y0):
        # A shelf along a side wall: the products in a row along y, facing the aisle.
        for k in range(rows):
            z = z0 + k * (z1 - z0) / rows
            h = (z1 - z0) / rows * r.uniform(0.6, 0.85)
            y = y0
            while y < y1 - 0.05:
                w = r.uniform(0.06, 0.2)
                if r.random() < fill:
                    s.box(x0, y, z, x1, y + w - 0.008, z + h * r.uniform(0.75, 1.0), r.choice(mats))
                y += w + 0.01
        return
    for k in range(rows):
        z = z0 + k * (z1 - z0) / rows
        h = (z1 - z0) / rows * r.uniform(0.6, 0.85)
        x = x0
        while x < x1 - 0.05:
            w = r.uniform(0.06, 0.2)
            if r.random() < fill:
                m = r.choice(mats)
                n = r.randint(1, 4)
                for j in range(n):
                    xx = x + j * w / n
                    s.box(xx, y0, z, xx + w / n - 0.005, y1, z + h * r.uniform(0.75, 1.0), m)
            x += w + 0.01


GROCERY = [(0.8, 0.1, 0.08), (0.95, 0.75, 0.1), (0.1, 0.3, 0.7), (0.9, 0.9, 0.85), (0.15, 0.55, 0.2), (0.9, 0.45, 0.1),
           (0.5, 0.1, 0.4), (0.35, 0.22, 0.12)]
CLOTHES = [(0.05, 0.05, 0.06), (0.85, 0.85, 0.82), (0.15, 0.2, 0.4), (0.5, 0.45, 0.4), (0.55, 0.1, 0.12), (0.3, 0.4, 0.3),
           (0.75, 0.6, 0.45), (0.6, 0.65, 0.75)]


def grocery(room):
    s, r = room.s, room.r
    shop_shell(room, floor=(0.85, 0.85, 0.82), wall=(0.9, 0.9, 0.9))
    room.troffers("linear")
    shelf = s.mat("gshelf", (0.85, 0.85, 0.85), 0.4, metal=0.3)
    # aisles running back from the window
    for xc in (-1.6, 0.0, 1.6):
        s.box(xc - 0.35, 1.2, 0, xc + 0.35, 4.0, 0.12, shelf)
        s.box(xc - 0.03, 1.2, 0, xc + 0.03, 4.0, 1.7, shelf)
        for side in (-1, 1):
            for k in range(5):
                z = 0.12 + k * 0.33
                s.box(xc + side * 0.03, 1.2, z, xc + side * 0.35, 4.0, z + 0.015, shelf)
                yy = 1.25
                while yy < 3.95:
                    d = r.uniform(0.08, 0.2)
                    m = s.mat("prod_%.2f_%.2f_%.2f" % r.choice(GROCERY), r.choice(GROCERY), 0.5)
                    if r.random() < 0.92:
                        s.box(xc + side * 0.05, yy, z + 0.015, xc + side * 0.33, yy + d - 0.01, z + 0.015 + r.uniform(0.15, 0.28), m)
                    yy += d
    # a fridge wall at the back, glowing
    L = room.L
    fr = s.mat("fridgebody", (0.2, 0.2, 0.22), 0.4, metal=0.5)
    inner = s.mat("fridgeglow", (0.9, 0.95, 1.0), 0.4, emit=((0.9, 0.95, 1.0), 3.0))
    s.box(room.x0, L - 0.7, 0, room.x1, L, 2.2, fr)
    s.box(room.x0 + 0.05, L - 0.71, 0.3, room.x1 - 0.05, L - 0.69, 2.05, inner)
    products(room, room.x0 + 0.1, room.x1 - 0.1, L - 0.69, L - 0.6, 0.35, 2.0, 5, GROCERY, fill=0.85)
    door = s.mat("fridgedoor", (0.9, 0.95, 0.95), glass=True)
    s.box(room.x0 + 0.05, L - 0.74, 0.3, room.x1 - 0.05, L - 0.72, 2.05, door)
    for x in np.arange(room.x0, room.x1 + 0.01, 0.75):
        s.box(x - 0.02, L - 0.75, 0.3, x + 0.02, L - 0.7, 2.05, fr)
    s.area(0, L - 1.0, 2.1, room.W - 0.4, 0.1, 15, 15)


def clothing(room):
    s, r = room.s, room.r
    shop_shell(room)
    track(room)
    chrome = s.mat("chrome", (0.8, 0.8, 0.82), 0.15, metal=1.0)
    mats = [s.mat("cloth_%.2f_%.2f_%.2f" % c, c, 0.9) for c in r.sample(CLOTHES, 5)]
    # wall rails both sides and at the back
    L = room.L
    for x in (room.x0 + 0.35, room.x1 - 0.35):
        s.box(x - 0.01, 1.0, 1.65, x + 0.01, L - 0.6, 1.68, chrome)
        y = 1.05
        while y < L - 0.65:
            m = r.choice(mats)
            s.box(x - 0.25, y, 0.75, x + 0.25, y + 0.035, 1.62, m)
            y += r.uniform(0.05, 0.09)
    for x in np.arange(room.x0 + 0.6, room.x1 - 0.5, 0.06):
        if r.random() < 0.85:
            s.box(x, L - 0.4, 0.95, x + 0.035, L - 0.1, 1.8, r.choice(mats))
    s.box(room.x0 + 0.5, L - 0.25, 1.85, room.x1 - 0.5, L - 0.22, 1.88, chrome)
    # a round rail and a table of folded clothes in the middle
    for k in range(18):
        a = k / 18.0 * 2 * math.pi
        xx, yy = -0.9 + 0.45 * math.cos(a), 2.3 + 0.45 * math.sin(a)
        s.box(xx - 0.02, yy - 0.02, 0.6, xx + 0.02, yy + 0.02, 1.3, r.choice(mats))
    s.cyl(-0.9, 2.3, 0, 1.35, 0.02, chrome)
    tbl = s.mat("ctable", (0.62, 0.48, 0.32), 0.4)
    s.box(0.5, 2.6, 0, 1.6, 3.4, 0.8, tbl)
    for i in range(3):
        for j in range(2):
            m = r.choice(mats)
            s.box(0.6 + i * 0.33, 2.7 + j * 0.35, 0.8, 0.88 + i * 0.33, 2.98 + j * 0.35, 0.8 + r.uniform(0.08, 0.2), m)
    mirror = s.mat("mirror", (0.9, 0.9, 0.9), 0.02, metal=1.0)
    s.box(1.8, L - 0.04, 0.2, 2.4, L - 0.02, 2.0, mirror)
    # mannequin (abstract: a pale column)
    body = s.mat("mannequin", (0.85, 0.85, 0.83), 0.4)
    s.cyl(-1.6, 1.9, 0.0, 0.08, 0.2, body)
    s.cyl(-1.6, 1.9, 0.08, 0.9, 0.02, body)
    s.blob(-1.6, 1.9, 1.2, 0.17, 0.11, 0.32, body)
    s.blob(-1.6, 1.9, 1.62, 0.09, 0.09, 0.11, body)


def electronics(room):
    s, r = room.s, room.r
    shop_shell(room, wall=(0.9, 0.9, 0.9))
    room.troffers("square")
    L = room.L
    bez = s.mat("bezel", (0.02, 0.02, 0.022), 0.3)
    for i, x in enumerate(np.arange(room.x0 + 0.6, room.x1 - 0.4, 1.15)):
        for zc in (1.0, 1.85):
            glow = r.choice([(0.3, 0.5, 0.9), (0.9, 0.5, 0.2), (0.2, 0.7, 0.4), (0.8, 0.8, 0.85), (0.6, 0.2, 0.6)])
            scr = s.mat("tv_%.2f_%.2f" % glow[:2], (0.02, 0.02, 0.02), 0.2, emit=(glow, 1.5))
            s.box(x - 0.5, L - 0.06, zc - 0.3, x + 0.5, L - 0.02, zc + 0.3, bez)
            s.box(x - 0.47, L - 0.065, zc - 0.27, x + 0.47, L - 0.06, zc + 0.27, scr)
    tbl = s.mat("etable", (0.9, 0.9, 0.9), 0.3)
    dev = s.mat("device", (0.05, 0.05, 0.06), 0.2, emit=((0.6, 0.7, 0.9), 0.8))
    for xc in (-1.3, 1.3):
        for yc in (1.6, 3.2):
            s.box(xc - 0.75, yc - 0.45, 0, xc + 0.75, yc + 0.45, 0.85, tbl)
            for k in range(4):
                xx = xc - 0.6 + k * 0.38
                s.box(xx - 0.09, yc - 0.12, 0.85, xx + 0.09, yc + 0.12, 0.87, dev)
    shelf = s.mat("eshelf", (0.85, 0.85, 0.85), 0.4)
    side_shelves(room, shelf, 0.4, 1.0, L - 0.5, 2.0, 4,
                 [(0.05, 0.05, 0.06), (0.85, 0.85, 0.85), (0.2, 0.3, 0.6), (0.6, 0.6, 0.62)], 0.7)


def side_shelves(room, shelf, depth, y0, y1, h, rows, palette, fill):
    """Open shelving along both side walls: a back panel, boards, the products on them."""
    s = room.s
    for left in (True, False):
        wall = room.x0 if left else room.x1
        inner = wall + depth if left else wall - depth
        back = wall + 0.03 if left else wall - 0.03
        s.box(wall, y0, 0, back, y1, h, shelf)
        s.box(wall, y0 - 0.02, 0, inner, y0, h, shelf)
        s.box(wall, y1, 0, inner, y1 + 0.02, h, shelf)
        for k in range(rows + 1):
            z = 0.1 + k * (h - 0.15) / rows
            s.box(wall, y0, z - 0.012, inner, y1, z + 0.012, shelf)
        a, b = sorted((back + (0.01 if left else -0.01), inner - (0.03 if left else -0.03)))
        products(room, a, b, y0 + 0.02, y1 - 0.02, 0.112, h - 0.05, rows, palette, fill=fill)


def pharmacy(room):
    s, r = room.s, room.r
    shop_shell(room, floor=(0.88, 0.88, 0.86), wall=(0.93, 0.93, 0.93))
    room.troffers("square")
    L = room.L
    shelf = s.mat("pshelf", (0.95, 0.95, 0.95), 0.4)
    pal = [(0.9, 0.9, 0.9), (0.2, 0.5, 0.8), (0.1, 0.6, 0.4), (0.9, 0.3, 0.3), (0.95, 0.85, 0.3), (0.6, 0.4, 0.7)]
    side_shelves(room, shelf, 0.35, 0.8, L - 1.2, 2.0, 6, pal, 0.9)
    counter = s.mat("pcounter", (0.92, 0.92, 0.92), 0.3)
    accent = s.mat("paccent", r.choice([(0.1, 0.5, 0.35), (0.15, 0.35, 0.6)]), 0.5)
    s.box(-1.8, L - 1.6, 0, 1.8, L - 1.0, 1.0, counter)
    s.box(-1.8, L - 1.61, 0.85, 1.8, L - 1.6, 0.95, accent)
    s.box(room.x0, L - 0.4, 0, room.x1, L, 2.2, shelf)
    products(room, room.x0 + 0.1, room.x1 - 0.1, L - 0.38, L - 0.1, 0.2, 2.1, 7, pal, fill=0.95)
    for xc in (-0.7, 0.7):
        s.box(xc - 0.4, 1.6, 0, xc + 0.4, 2.4, 1.2, shelf)
        products(room, xc - 0.38, xc + 0.38, 1.58, 1.62, 0.15, 1.15, 4, pal)


def cafe(room):
    s, r = room.s, room.r
    shop_shell(room, floor=r.choice([(0.4, 0.28, 0.18), (0.3, 0.3, 0.3)]), wall=r.choice([(0.85, 0.82, 0.76), (0.25, 0.25, 0.27)]))
    L = room.L
    shade = s.mat("cshade", r.choice([(0.05, 0.05, 0.05), (0.75, 0.55, 0.2)]), 0.4, metal=0.4)
    bulb = s.mat("bulb", (1, 1, 1), 0.5, emit=((1, 0.92, 0.8), 10.0))
    wood = s.mat("ctop", (0.55, 0.4, 0.26), 0.4)
    leg = s.mat("deskleg", (0.1, 0.1, 0.1), 0.35, metal=0.5)
    for x in (-1.6, 0.0, 1.6):
        for y in (1.3, 2.7):
            s.cyl(x, y, 0.72, 0.75, 0.35, wood, seg=16)
            s.cyl(x, y, 0, 0.72, 0.03, leg)
            for a in (0.0, math.pi):
                cx, cy = x + 0.55 * math.cos(a + 0.4), y + 0.55 * math.sin(a + 0.4)
                s.box(cx - 0.2, cy - 0.2, 0.43, cx + 0.2, cy + 0.2, 0.47, wood)
                s.box(cx - 0.02, cy - 0.02, 0, cx + 0.02, cy + 0.02, 0.43, leg)
    for x in (-1.6, 0.0, 1.6):
        s.cyl(x, 2.0, 2.05, 2.3, 0.18, shade)
        s.cyl(x, 2.0, 2.03, 2.05, 0.12, bulb)
        s.box(x - 0.005, 1.995, 2.3, x + 0.005, 2.005, H, shade)
        s.point(x, 2.0, 1.95, 30, 10, radius=0.1)
    counter = s.mat("ccounter", r.choice([(0.3, 0.2, 0.12), (0.9, 0.9, 0.88), (0.15, 0.25, 0.22)]), 0.4)
    top = s.mat("ccountertop", (0.85, 0.85, 0.83), 0.2)
    s.box(-2.0, L - 1.5, 0, 1.6, L - 0.9, 1.05, counter)
    s.box(-2.05, L - 1.55, 1.05, 1.65, L - 0.85, 1.09, top)
    machine = s.mat("espresso", (0.7, 0.7, 0.72), 0.2, metal=0.9)
    s.box(-1.0, L - 1.3, 1.09, -0.3, L - 0.95, 1.5, machine)
    # menu boards: dark panels with a lit edge (no lettering)
    board = s.mat("menuboard", (0.05, 0.05, 0.05), 0.6)
    for x in (-1.3, 0.0, 1.3):
        s.box(x - 0.55, L - 0.03, 1.7, x + 0.55, L, 2.4, board)
    shelf = s.mat("cshelf", (0.4, 0.28, 0.18), 0.5)
    s.box(-2.4, L - 0.3, 1.2, 2.4, L, 1.23, shelf)
    products(room, -2.3, 2.3, L - 0.28, L - 0.05, 1.23, 1.6, 1, [(0.9, 0.9, 0.88), (0.3, 0.2, 0.12), (0.6, 0.15, 0.1)], fill=0.7)
    under = s.mat("undercab", (1, 1, 1), 0.5, emit=((1, 0.95, 0.85), 4.0))
    s.box(-2.4, L - 0.3, 1.19, 2.4, L - 0.25, 1.2, under)


def bank(room):
    s, r = room.s, room.r
    shop_shell(room, floor=(0.75, 0.73, 0.7), wall=r.choice([(0.88, 0.88, 0.86), (0.75, 0.78, 0.8)]))
    room.troffers("down")
    L = room.L
    top = s.mat("top", r.choice(TOPS), 0.4)
    for x in (-1.5, 1.5):
        room.desk(x, 3.0, -1, top, w=1.4, monitors=1, chair=False)
        room.chair(x, 3.75, -1, colour=(0.03, 0.03, 0.035))
        for dx in (-0.35, 0.35):
            room.chair(x + dx, 2.2, 1, colour=r.choice([(0.15, 0.25, 0.45), (0.3, 0.3, 0.32)]))
    panel = s.mat("bankpanel", r.choice([(0.1, 0.25, 0.45), (0.15, 0.4, 0.3), (0.5, 0.1, 0.12)]), 0.5)
    s.box(-2.7, L - 0.05, 0, 2.7, L, 2.4, panel)
    screen = s.mat("adscreen", (0.02, 0.02, 0.02), 0.2, emit=((0.7, 0.75, 0.85), 1.2))
    s.box(-0.8, L - 0.06, 1.2, 0.8, L - 0.05, 2.1, screen)
    room.plant(-2.5, 0.9)
    room.plant(2.5, 0.9)
    rack = s.mat("brochure", (0.85, 0.85, 0.85), 0.4)
    s.box(2.2, 1.6, 0, 2.6, 2.6, 1.5, rack)


def books(room):
    s, r = room.s, room.r
    shop_shell(room, floor=(0.4, 0.28, 0.18))
    room.troffers(r.choice(["down", "square"]))
    L = room.L
    shelf = s.mat("bshelf", r.choice([(0.3, 0.2, 0.12), (0.88, 0.88, 0.86)]), 0.5)
    spines = [(0.4, 0.1, 0.08), (0.1, 0.15, 0.3), (0.7, 0.65, 0.5), (0.15, 0.25, 0.12), (0.85, 0.82, 0.75), (0.1, 0.1, 0.1),
              (0.8, 0.5, 0.1), (0.3, 0.45, 0.6)]
    def bookcase(x0, x1, y0, y1, h, rows, face):
        s.box(x0, y0, 0, x1, y1, 0.05, shelf)
        for k in range(rows):
            z = 0.05 + k * h / rows
            s.box(x0, y0, z, x1, y1, z + 0.02, shelf)
        if face == "y":
            products(room, x0 + 0.02, x1 - 0.02, y0 + 0.02, y1 - 0.02, 0.07, h, rows, spines, fill=0.92)
        else:
            for k in range(rows):
                z = 0.07 + k * h / rows
                yy = y0
                while yy < y1 - 0.03:
                    w = r.uniform(0.025, 0.05)
                    m = s.mat("prod_%.2f_%.2f_%.2f" % r.choice(spines), r.choice(spines), 0.8)
                    if r.random() < 0.92:
                        s.box(x0 + 0.02, yy, z, x1 - 0.02, yy + w, z + h / rows * r.uniform(0.6, 0.85), m)
                    yy += w + 0.004
    bookcase(room.x0, room.x1, L - 0.35, L - 0.02, 2.2, 6, "y")
    bookcase(room.x0, room.x0 + 0.35, 0.9, L - 0.4, 2.0, 5, "x")
    bookcase(room.x1 - 0.35, room.x1, 0.9, L - 0.4, 2.0, 5, "x")
    tbl = s.mat("btable2", (0.55, 0.4, 0.26), 0.4)
    for xc, yc in ((-0.9, 1.8), (0.9, 2.8)):
        s.box(xc - 0.7, yc - 0.45, 0, xc + 0.7, yc + 0.45, 0.8, tbl)
        for i in range(4):
            m = s.mat("prod_%.2f_%.2f_%.2f" % r.choice(spines), r.choice(spines), 0.7)
            s.box(xc - 0.6 + i * 0.32, yc - 0.3, 0.8, xc - 0.35 + i * 0.32, yc + 0.0, 0.8 + r.uniform(0.04, 0.15), m)


SHOP_LAYOUTS = [("grocery", grocery, 2), ("clothing", clothing, 3), ("electronics", electronics, 2), ("pharmacy", pharmacy, 2),
                ("cafe", cafe, 2), ("bank", bank, 2), ("books", books, 2)]


# ------------------------------------------------------------------ homes (A 1.4, D 2.8: 4.2 x 4.2 m)

HOME_WALLS = [(0.82, 0.78, 0.7), (0.86, 0.85, 0.82), (0.75, 0.72, 0.66), (0.7, 0.74, 0.72), (0.8, 0.74, 0.7)]
HOME_ACCENTS = [(0.25, 0.32, 0.4), (0.45, 0.5, 0.4), (0.6, 0.42, 0.32), (0.3, 0.3, 0.32), (0.55, 0.5, 0.42)]
FLOORS = [(0.45, 0.3, 0.18), (0.6, 0.45, 0.3), (0.32, 0.22, 0.15), (0.7, 0.6, 0.48)]


def home_shell(room, accent=0.4, floor=None):
    """Walls, a plank floor (each plank its own shade), the ceiling, a skirting."""
    s, r = room.s, room.r
    wall = s.mat("hwall", r.choice(HOME_WALLS), 0.85)
    back = s.mat("haccent", r.choice(HOME_ACCENTS), 0.85) if r.random() < accent else wall
    base = floor or r.choice(FLOORS)
    room.shell(s.mat("hfloor", base, 0.5), wall, s.mat("hceil", (0.88, 0.88, 0.86), 0.9), back=back)
    tones = [s.mat("plank%d" % k, tuple(min(c * f, 1.0) for c in base), 0.45) for k, f in enumerate((0.85, 0.93, 1.0, 1.08, 1.15))]
    x = room.x0
    while x < room.x1:
        y = -r.uniform(0.0, 1.2)
        while y < room.L:
            ln = r.uniform(0.9, 1.8)
            s.box(x + 0.003, max(y, 0.0) + 0.003, 0.0, min(x + 0.18, room.x1) - 0.003, min(y + ln, room.L) - 0.003, 0.004, r.choice(tones))
            y += ln
        x += 0.18


def table_lamp(room, x, y, z, night=18.0):
    s, r = room.s, room.r
    base = s.mat("lampbase", r.choice([(0.85, 0.85, 0.83), (0.1, 0.1, 0.1), (0.6, 0.45, 0.25)]), 0.3, metal=0.3)
    shade = s.mat("lampshade", (1.0, 0.97, 0.9), 0.6, emit=((1.0, 0.95, 0.85), 3.0))
    s.cyl(x, y, z, z + 0.03, 0.08, base)
    s.cyl(x, y, z + 0.03, z + 0.32, 0.015, base)
    s.cyl(x, y, z + 0.3, z + 0.52, 0.15, shade, seg=16)
    s.point(x, y, z + 0.4, night, 0.0, radius=0.08)


def floor_lamp(room, x, y, night=30.0):
    s = room.s
    pole = s.mat("lamppole", (0.08, 0.08, 0.08), 0.3, metal=0.6)
    shade = s.mat("lampshade", (1.0, 0.97, 0.9), 0.6, emit=((1.0, 0.95, 0.85), 3.0))
    s.cyl(x, y, 0.0, 0.03, 0.16, pole)
    s.cyl(x, y, 0.03, 1.4, 0.012, pole)
    s.cyl(x, y, 1.35, 1.65, 0.2, shade, seg=16)
    s.point(x, y, 1.5, night, 0.0, radius=0.1)


def ceiling_lamp(room, x, y, drop=0.95, night=45.0):
    """The ceiling's lamp (modern_ceiling_lamp_01), hanging drop metres, its bulb a light."""
    room.model("modern_ceiling_lamp_01", x, y, z=H - drop)
    s = room.s
    bulb = s.mat("hbulb", (1, 1, 1), 0.5, emit=((1.0, 0.95, 0.85), 8.0))
    s.blob(x, y, H - drop + 0.12, 0.05, 0.05, 0.06, bulb, sub=1)
    s.point(x, y, H - drop + 0.05, night, 0.0, radius=0.06)


def rug(room, x, y, w, d):
    s, r = room.s, room.r
    c = r.choice([(0.55, 0.5, 0.42), (0.3, 0.32, 0.38), (0.6, 0.35, 0.28), (0.75, 0.73, 0.68), (0.25, 0.3, 0.28)])
    body = s.mat("rug_%.2f" % c[0], c, 0.95)
    edge = s.mat("rugedge_%.2f" % c[0], tuple(v * 0.6 for v in c), 0.95)
    s.box(x - w / 2, y - d / 2, 0.004, x + w / 2, y + d / 2, 0.016, edge)
    s.box(x - w / 2 + 0.08, y - d / 2 + 0.08, 0.004, x + w / 2 - 0.08, y + d / 2 - 0.08, 0.018, body)


def bookcase_back(room, x0, x1, h, rows):
    """Shelves against the back wall, full of books (private offices' and studies')."""
    s, r = room.s, room.r
    L = room.L
    shelf = s.mat("hshelf", r.choice([(0.3, 0.2, 0.12), (0.88, 0.88, 0.86), (0.55, 0.4, 0.26)]), 0.5)
    books = [s.mat("book%d" % i, c, 0.8) for i, c in enumerate([(0.4, 0.1, 0.08), (0.1, 0.15, 0.3), (0.7, 0.65, 0.5),
                                                                (0.15, 0.25, 0.12), (0.8, 0.8, 0.75), (0.1, 0.1, 0.1)])]
    s.box(x0, L - 0.32, 0, x1, L, 0.05, shelf)
    for k in range(rows + 1):
        z = 0.05 + k * (h - 0.05) / rows
        s.box(x0, L - 0.32, z, x1, L, z + 0.022, shelf)
        if k == rows:
            break
        x = x0 + 0.04
        while x < x1 - 0.06:
            if r.random() < 0.15:
                x += r.uniform(0.12, 0.35)
                continue
            w = r.uniform(0.025, 0.06)
            s.box(x, L - 0.3, z + 0.022, x + w, L - 0.06, z + 0.022 + r.uniform(0.18, 0.3), r.choice(books))
            x += w + 0.004
    n = max(int((x1 - x0) / 0.9), 1)
    for i in range(n + 1):
        xx = x0 + (x1 - x0) * i / n
        s.box(xx - 0.02, L - 0.32, 0, xx + 0.02, L, h + 0.02, shelf)


def own_bed(room, x, y_head, w):
    """A modern double bed, its head at the back wall: frame, mattress, duvet, pillows, headboard."""
    s, r = room.s, room.r
    frame = s.mat("bedframe", r.choice([(0.3, 0.2, 0.12), (0.85, 0.85, 0.83), (0.25, 0.25, 0.27)]), 0.5)
    duvet = s.mat("duvet", r.choice([(0.9, 0.9, 0.88), (0.55, 0.6, 0.68), (0.75, 0.68, 0.58), (0.4, 0.45, 0.38)]), 0.9)
    sheet = s.mat("sheet", (0.92, 0.92, 0.9), 0.9)
    length = 2.1
    s.box(x - w / 2, y_head - length, 0.0, x + w / 2, y_head, 0.32, frame)
    s.box(x - w / 2 + 0.03, y_head - length + 0.03, 0.32, x + w / 2 - 0.03, y_head - 0.03, 0.52, sheet)
    s.box(x - w / 2 + 0.01, y_head - length + 0.01, 0.4, x + w / 2 - 0.01, y_head - 0.55, 0.58, duvet)
    for px in (x - w / 4, x + w / 4):
        s.blob(px, y_head - 0.3, 0.6, w / 4 - 0.05, 0.16, 0.07, sheet, sub=2)
    s.box(x - w / 2 - 0.05, y_head - 0.06, 0.0, x + w / 2 + 0.05, y_head, 1.1, frame)


def living(room):
    s, r = room.s, room.r
    home_shell(room)
    L = room.L
    sofa = r.choice(["Sofa_01", "sofa_02", "sofa_03"])
    size = room.model(sofa, 0.0, L - 0.5)
    rug(room, 0.0, L - 1.7, 2.6, 1.8)
    room.model(r.choice(["coffee_table_round_01", "CoffeeTable_01", "modern_coffee_table_02", "modern_coffee_table_01"]),
               0.0, L - size[1] - 1.0, rot=r.choice([0, 90]))
    side = r.choice([-1, 1])
    # The armchair at one side, turned to face the room's middle (a model faces -y, the window, at 0).
    room.model(r.choice(["ArmChair_01", "GreenChair_01", "mid_century_lounge_chair"]), side * 1.55, L - 1.8, rot=-90 * side)
    st = room.model("side_table_01", -side * (min(size[0], 2.4) / 2 + 0.35), L - 0.4)
    table_lamp(room, -side * (min(size[0], 2.4) / 2 + 0.35), L - 0.4, st[2])
    room.plant(-side * 1.75, 0.9, tall=r.choice([1.1, 1.4, 1.8]))
    ceiling_lamp(room, 0.0, L / 2)


def bedroom(room):
    s, r = room.s, room.r
    home_shell(room, accent=0.6)
    L = room.L
    if r.random() < 0.3:
        room.model("GothicBed_01", 0.0, L - 1.05)
        bw = 1.5
    else:
        bw = r.choice([1.4, 1.6])
        own_bed(room, 0.0, L, bw)
    for sx in (-1, 1):
        ns = room.model("ClassicNightstand_01", sx * (bw / 2 + 0.35), L - 0.25)
        table_lamp(room, sx * (bw / 2 + 0.35), L - 0.25, ns[2], night=12.0)
    side = r.choice([-1, 1])
    room.model("drawer_cabinet", side * (room.W / 2 - 0.27), 1.5, rot=-90 * side)
    rug(room, 0.0, L - 2.4, 2.0, 1.2)
    room.plant(-side * 1.75, 0.8, tall=r.choice([0.9, 1.3]))
    ceiling_lamp(room, 0.0, L / 2 - 0.3, night=30.0)


def dining(room):
    s, r = room.s, room.r
    home_shell(room, accent=0.2)
    L = room.L
    cab = s.mat("hkitchen", r.choice([(0.88, 0.88, 0.85), (0.2, 0.28, 0.32), (0.4, 0.45, 0.36), (0.55, 0.4, 0.26)]), 0.4)
    worktop = s.mat("hworktop", r.choice([(0.25, 0.25, 0.26), (0.85, 0.85, 0.83), (0.45, 0.32, 0.2)]), 0.3)
    s.box(-2.1, L - 0.62, 0, 1.2, L, 0.88, cab)
    s.box(-2.1, L - 0.64, 0.88, 1.2, L, 0.92, worktop)
    s.box(-2.1, L - 0.35, 1.5, 1.2, L, 2.25, cab)
    tile = s.mat("splash", (0.9, 0.9, 0.88), 0.2)
    s.box(-2.1, L - 0.02, 0.92, 1.2, L, 1.5, tile)
    line = s.mat("cabline", (0.1, 0.1, 0.1), 0.5)
    for xx in np.arange(-2.1, 1.21, 0.6):
        s.box(xx - 0.004, L - 0.625, 0.05, xx + 0.004, L - 0.62, 0.85, line)
        s.box(xx - 0.004, L - 0.355, 1.52, xx + 0.004, L - 0.35, 2.23, line)
    fridge = s.mat("hfridge", (0.75, 0.75, 0.77), 0.3, metal=0.7)
    s.box(1.25, L - 0.7, 0, 2.05, L, 2.0, fridge)
    under = s.mat("hundercab", (1, 1, 1), 0.5, emit=((1, 0.95, 0.85), 1.2))
    s.box(-2.05, L - 0.33, 1.49, 1.15, L - 0.3, 1.5, under)
    s.area(-0.45, L - 0.3, 1.48, 3.2, 0.06, 3, 0)
    big = r.random() < 0.5
    tx, ty = 0.0, 1.9
    room.model("round_wooden_table_01" if big else "round_wooden_table_02", tx, ty)
    reach = 0.85 if big else 0.6
    for (dx, dy, rot) in ((0, -reach, 180), (0, reach, 0), (-reach, 0, 90), (reach, 0, -90)):
        room.model("dining_chair_02", tx + dx, ty + dy, rot=rot)
    ceiling_lamp(room, tx, ty, drop=1.25, night=45.0)
    room.plant(1.75, 0.7, tall=1.0)


def study(room):
    s, r = room.s, room.r
    home_shell(room)
    L = room.L
    bookcase_back(room, -2.1, 0.2, 2.2, 5)
    wood = s.mat("sdesk", r.choice([(0.5, 0.36, 0.22), (0.85, 0.85, 0.83), (0.3, 0.2, 0.12)]), 0.4)
    leg = s.mat("deskleg", (0.55, 0.55, 0.57), 0.35, metal=0.8)
    s.box(0.5, L - 0.75, 0.72, 2.0, L - 0.05, 0.75, wood)
    for xx in (0.53, 1.97):
        s.box(xx - 0.025, L - 0.72, 0, xx + 0.025, L - 0.08, 0.72, leg)
    room.monitor(1.25, L - 0.25, 1, width=0.6)
    table_lamp(room, 1.85, L - 0.3, 0.75, night=10.0)
    room.model("modern_arm_chair_01", 1.25, L - 1.25, rot=180)
    room.model(r.choice(["ArmChair_01", "GreenChair_01", "mid_century_lounge_chair"]), -1.3, 1.6, rot=r.choice([150, 180, 210]))
    floor_lamp(room, -1.85, 2.3)
    room.plant(1.8, 0.8, tall=r.choice([0.9, 1.4]))
    rug(room, -0.6, 1.8, 2.0, 1.5)
    ceiling_lamp(room, 0.0, L / 2, night=25.0)


def lounge(room):
    s, r = room.s, room.r
    home_shell(room, accent=0.5)
    L = room.L
    room.model("modern_wooden_cabinet", 0.0, L - 0.27, width=min(2.44, room.W - 0.6))
    tvb = s.mat("tvbody", (0.02, 0.02, 0.02), 0.2)
    s.box(-0.65, L - 0.2, 0.7, 0.65, L - 0.15, 1.45, tvb)
    room.model("ceramic_vase_01", 1.15, L - 0.27, z=0.68)
    room.model("ceramic_vase_03", -1.2, L - 0.27, z=0.68)
    rug(room, 0.0, 2.2, 2.6, 1.8)
    room.model(r.choice(["modern_coffee_table_02", "coffee_table_round_01"]), 0.0, 2.3)
    if r.random() < 0.5:
        for sx in (-1, 1):
            room.model(r.choice(["ArmChair_01", "GreenChair_01", "mid_century_lounge_chair"]), sx * 0.9, 1.1, rot=180)
    else:
        room.model(r.choice(["sofa_02", "Sofa_01"]), 0.0, 1.0, rot=180)
    room.model("Ottoman_01", 1.5, 2.6, rot=r.uniform(-20, 20))
    room.plant(-1.75, L - 0.5, tall=r.choice([1.3, 1.8]))
    floor_lamp(room, 1.8, L - 0.4)
    ceiling_lamp(room, 0.0, 2.2, night=30.0)


HOME_LAYOUTS = [("living", living, 3), ("bedroom", bedroom, 2), ("dining", dining, 2), ("study", study, 2), ("lounge", lounge, 2)]


def catalog():
    rooms = []
    # Homes last: each room's seed counts the rooms before it, so the offices and shops keep theirs.
    for kind, layouts in (("office", OFFICE_LAYOUTS), ("shop", SHOP_LAYOUTS), ("home", HOME_LAYOUTS)):
        for name, fn, n in layouts:
            for k in range(n):
                rooms.append(("%s_%s_%d" % (kind, name, k + 1), kind, fn, 1000 * len(rooms) + 7 * k + 1))
    return rooms


# ------------------------------------------------------------------ render

def setup(kind):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    sc = bpy.context.scene
    sc.render.engine = 'CYCLES'
    sc.cycles.samples = SAMPLES
    sc.cycles.use_denoising = True
    sc.cycles.max_bounces = 6
    sc.render.resolution_x = RENDER
    sc.render.resolution_y = RENDER
    sc.render.resolution_percentage = 100
    a, d = rr.SHAPES[kind]
    sc.render.pixel_aspect_x = a          # the A:1 window squeezed into a square, as box_view does
    sc.render.pixel_aspect_y = 1.0
    sc.render.image_settings.file_format = 'OPEN_EXR'
    sc.render.image_settings.color_depth = '32'
    sc.view_settings.view_transform = 'Standard'
    w = bpy.data.worlds.new("w")
    w.use_nodes = True
    sc.world = w
    cam_d = rr.FAR * d / (1.0 - rr.FAR)   # half-heights in front of the window
    cam = bpy.data.cameras.new("c")
    cam.sensor_fit = 'VERTICAL'
    cam.angle_y = 2.0 * math.atan(1.0 / cam_d)
    cam.clip_start = 0.1
    co = bpy.data.objects.new("c", cam)
    co.location = (0.0, -cam_d * HALF, HALF)
    co.rotation_euler = (math.radians(90.0), 0.0, 0.0)
    sc.collection.objects.link(co)
    sc.camera = co
    return sc


def render_one(name, kind, fn, seed, out):
    sc = setup(kind)
    s = Scene()
    room = Room(s, kind, random.Random(seed))
    fn(room)
    s.build()
    bg = sc.world.node_tree.nodes["Background"]
    pics = {}
    for phase in ("night", "day"):
        if phase == "night":
            bg.inputs[0].default_value = (0.004, 0.005, 0.008, 1.0)
            bg.inputs[1].default_value = 1.0
        else:
            bg.inputs[0].default_value = (0.75, 0.85, 1.0, 1.0)
            bg.inputs[1].default_value = 6.0
        for (o, night, day) in s.lights:
            o.data.energy = night if phase == "night" else day
        path = os.path.join(out, "%s_%s.exr" % (name, phase))
        sc.render.filepath = path
        bpy.ops.render.render(write_still=True)
        c = rr.load_hdri(path)
        pics[phase] = c
    n = RENDER // rr.NIGHT_TILE
    night = pics["night"].reshape(rr.NIGHT_TILE, n, rr.NIGHT_TILE, n, 3).mean(axis=(1, 3))
    m = RENDER // rr.DAY_TILE
    day = pics["day"].reshape(rr.DAY_TILE, m, rr.DAY_TILE, m, 3).mean(axis=(1, 3))
    return grade_night(night), rr.day_picture(day)


def grade_night(c):
    """Only the room's own light: the atlas's mean, a soft shoulder, no daylight to put out."""
    c = rr.shoulder(rr.to_mean(c, rr.TILE_MEAN))
    return rr.to_mean(c, rr.TILE_MEAN)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    rooms = catalog()
    if argv[0] == "list":
        for r in rooms:
            print("ROOM", r[0], r[1])
        return
    out = argv[1]
    names = set(argv[2:])
    tiles = os.path.join(out, "tiles")
    os.makedirs(tiles, exist_ok=True)
    for name, kind, fn, seed in rooms:
        if names and name not in names:
            continue
        n_pic, d_pic = render_one(name, kind, fn, seed, out)
        rr.save_png(n_pic, os.path.join(tiles, "%s_night.png" % name))
        rr.save_png(d_pic, os.path.join(tiles, "%s_day.png" % name))
        print("RENDERED", name, flush=True)


try:
    main()
except Exception as e:
    print("OFFICES FAILED", repr(e), flush=True)
    import traceback
    traceback.print_exc()
    sys.exit(1)
