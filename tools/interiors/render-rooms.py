# The pictures of the rooms behind the glass (UnityProject/Assets/RealGlass/Interiors.cginc), made
# from Poly Haven's indoor panoramas (CC0, https://polyhaven.com/license). Runs in Blender's own
# Python (numpy, and Blender's HDR reader and PNG writer); no rendering: each view is the panorama
# reprojected, here, in numpy -- through a pinhole camera for the survey, onto a box room for the
# atlas (box_view).
#
#   blender -b --factory-startup -P render-rooms.py -- survey <hdri dir> <out dir> [yaws] [size]
#       every panorama x every yaw at "size" px, exposed for a look: to choose rooms by eye.
#   blender -b --factory-startup -P render-rooms.py -- bake <hdri dir> <rooms.txt> <out dir>
#       the rooms of rooms.txt as box rooms: by day and by night, packed into rooms-night.png (8 x 8
#       tiles of 256 px) and rooms-day.png (8 x 8 tiles of 128 px), each tile's mean luminance
#       TILE_MEAN, and rooms-pools.txt (the kinds' first tile, count and box shape) for the editor's
#       baker.
#
# rooms.txt: one room a line -- slug yaw pitch fov kind [lamp...] -- (pitch and fov: the survey's
# view; the bake looks level, turned to the nearest wall) -- kind one of home office shop
# hotel leisure public industry; a lamp "x,y" (0..1 across the picture, from its bottom left) puts
# a lamp's pool of light there in the night picture (none given: one high in the middle). '#' starts
# a comment.
#
# The night picture: the panorama's daylight is not a room at night. Its own windows and sun patches
# (far brighter than the room's median in the HDR) are put out, the colour balanced to neutral
# (grey world: the shader gives each room its lamp's colour), and light falls off from the lamps --
# then the same mean as every other tile.

import bpy
import math
import os
import sys

import numpy as np

TILE_MEAN = 0.18                  # Interiors.cginc RG_ROOM_TILE_MEAN
KINDS = ["home", "office", "shop", "hotel", "leisure", "public", "industry", "spare"]
NIGHT_TILE, DAY_TILE = 256, 128
PER_ROW, ROWS = 16, 8             # 128 rooms (8 x 8 until 2026-11-08: the rooms of our own needed room)
SUPER = 2                         # supersampling per axis


def load_hdri(path):
    img = bpy.data.images.load(path, check_existing=False)
    w, h = img.size
    px = np.empty(w * h * 4, dtype=np.float32)
    img.pixels.foreach_get(px)
    bpy.data.images.remove(img)
    return px.reshape(h, w, 4)[:, :, :3]          # rows bottom-up, linear


def view(pano, yaw_deg, pitch_deg, fov_deg, size):
    """The panorama through a pinhole camera: size x size, rows bottom-up, supersampled."""
    n = size * SUPER
    yaw, pitch = math.radians(yaw_deg), math.radians(pitch_deg)
    f = np.array([math.sin(yaw) * math.cos(pitch), math.sin(pitch), math.cos(yaw) * math.cos(pitch)])
    r = np.array([math.cos(yaw), 0.0, -math.sin(yaw)])
    u = np.cross(f, r)
    u /= np.linalg.norm(u)
    t = math.tan(math.radians(fov_deg) * 0.5)
    s = (np.arange(n) + 0.5) / n * 2.0 - 1.0
    x, y = np.meshgrid(s, s)                      # y: row 0 at the bottom
    d = f[None, None, :] + (x[..., None] * t) * r[None, None, :] + (y[..., None] * t) * u[None, None, :]
    return sample(pano, d).reshape(size, SUPER, size, SUPER, 3).mean(axis=(1, 3))


# The box rooms (Interiors.cginc RG_ROOM_FAR, and the kinds' shapes in rooms-pools.txt): the room
# is a box behind its window, x across (-A..A), y up (-1..1, floor to ceiling), z in (0..D); the
# picture is the box seen from a camera in front of the window, far enough that the back wall fills
# FAR of the picture across; the panorama is painted on the box's walls from EYE, the middle of the
# room at eye height -- interior mapping's cubemap from the room's centre (van Dongen 2008), stored
# as one perspective picture per room (SimCity 2013's atlas, as Ben Golus wrote it up).
FAR = 0.55
EYE_Y = 0.05
SHAPES = {                         # kind: (aspect A = width / height, depth D in half-heights)
    "home": (1.4, 2.8), "hotel": (1.4, 2.8), "office": (2.0, 4.0), "shop": (1.8, 3.5),
    "leisure": (1.8, 3.5), "public": (1.8, 3.5), "industry": (1.8, 3.5), "spare": (1.4, 2.8),
}


def box_view(pano, yaw_deg, aspect, depth, size):
    """The room as a box, seen through its window: size x size (the width squeezed by the aspect)."""
    n = size * SUPER
    yaw = math.radians(yaw_deg)
    f = np.array([math.sin(yaw), 0.0, math.cos(yaw)])
    r = np.array([math.cos(yaw), 0.0, -math.sin(yaw)])
    up = np.array([0.0, 1.0, 0.0])
    cam = FAR * depth / (1.0 - FAR)
    s = (np.arange(n) + 0.5) / n * 2.0 - 1.0
    u, v = np.meshgrid(s, s)
    fx, fy = u * aspect, v
    dx, dy, dz = fx, fy, np.full_like(fx, cam)
    with np.errstate(divide="ignore", invalid="ignore"):
        tx = np.where(np.abs(dx) > 1e-9, (np.sign(dx) * aspect - fx) / dx, np.inf)
        ty = np.where(np.abs(dy) > 1e-9, (np.sign(dy) - fy) / dy, np.inf)
    tz = depth / cam
    t = np.minimum(np.minimum(tx, ty), tz)
    hx, hy, hz = fx + t * dx, fy + t * dy, t * dz
    qx, qy, qz = hx, hy - EYE_Y, hz - depth * 0.5
    d = qx[..., None] * r + qy[..., None] * up + qz[..., None] * f
    return sample(pano, d).reshape(size, SUPER, size, SUPER, 3).mean(axis=(1, 3))


def align_yaw(pano, yaw_deg, reach=40, step=5):
    """
    The yaw near the one chosen that looks square at a wall: there the edges above and below the
    middle of the view (the back wall's ceiling line, skirting, shelves) run level, where a view
    into a corner has them slanting. The share of the gradient's energy within 4 degrees of level,
    over the middle half of the columns and the top and bottom thirds of the rows; a little less
    for each degree turned away from the choice.
    """
    best, best_score = yaw_deg, -1.0
    for dyaw in range(-reach, reach + 1, step):
        c = view(pano, yaw_deg + dyaw, 0.0, 90.0, 96)
        lum = np.log(np.maximum(luma(c), 1e-4))
        gx = np.zeros_like(lum)
        gy = np.zeros_like(lum)
        gx[:, 1:-1] = lum[:, 2:] - lum[:, :-2]
        gy[1:-1, :] = lum[2:, :] - lum[:-2, :]
        e = gx * gx + gy * gy
        level = (np.abs(gx) < np.tan(math.radians(4.0)) * np.abs(gy)).astype(np.float64)
        n = lum.shape[0]
        rows = np.r_[0:n // 3, n - n // 3:n]
        cols = slice(n // 4, n - n // 4)
        ee = e[rows, cols]
        score = (ee * level[rows, cols]).sum() / max(ee.sum(), 1e-9) - 0.002 * abs(dyaw)
        if score > best_score:
            best, best_score = yaw_deg + dyaw, score
    return best


def sample(pano, d):
    """The panorama in the directions d (any length; x right, y up, z forward), bilinear."""
    h, w, _ = pano.shape
    d = d / np.linalg.norm(d, axis=-1, keepdims=True)
    lon = np.arctan2(d[..., 0], d[..., 2])
    lat = np.arcsin(np.clip(d[..., 1], -1.0, 1.0))
    fu = (lon / (2.0 * math.pi) + 0.5) * w - 0.5
    fv = (lat / math.pi + 0.5) * h - 0.5
    u0 = np.floor(fu).astype(np.int64)
    v0 = np.clip(np.floor(fv).astype(np.int64), 0, h - 2)
    au = (fu - u0)[..., None]
    av = np.clip(fv - v0, 0.0, 1.0)[..., None]
    u1 = (u0 + 1) % w
    u0 = u0 % w
    c = (pano[v0, u0] * (1 - au) * (1 - av) + pano[v0, u1] * au * (1 - av) +
         pano[v0 + 1, u0] * (1 - au) * av + pano[v0 + 1, u1] * au * av)
    return c


def luma(c):
    return c[..., 0] * 0.2126 + c[..., 1] * 0.7152 + c[..., 2] * 0.0722


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def shoulder(c, knee=0.8):
    """Linear up to knee, then a soft roll-off to 1: no hard clip in an 8-bit tile."""
    over = np.maximum(c - knee, 0.0)
    return np.minimum(c, knee) + (1.0 - knee) * (1.0 - np.exp(-over / (1.0 - knee)))


def to_mean(c, mean, mask=None):
    lum = luma(c)
    m = lum.mean() if mask is None else (lum * mask).sum() / max(mask.sum(), 1e-6)
    return c * (mean / max(m, 1e-6))


def day_picture(c):
    # The geometric mean to middle grey first (a sunny window does not darken the room), then the
    # plain mean to the tile's.
    gm = math.exp(np.log(np.maximum(luma(c), 1e-4)).mean())
    c = c * (0.18 / gm)
    c = shoulder(to_mean(shoulder(c, 0.9), TILE_MEAN))
    return to_mean(c, TILE_MEAN)


def box_blur(a, r):
    """A separable box blur of radius r (pixels) over a 2-D array, edges clamped."""
    def along(v, axis):
        pad = [(0, 0)] * v.ndim
        pad[axis] = (r + 1, r)
        p = np.pad(v, pad, mode="edge")
        cs = np.cumsum(p, axis=axis)
        hi = np.take(cs, np.arange(2 * r + 1, cs.shape[axis]), axis=axis)
        lo = np.take(cs, np.arange(0, cs.shape[axis] - 2 * r - 1), axis=axis)
        return (hi - lo) / (2 * r + 1)
    return along(along(a, 0), 1)


def night_picture(c, lamps):
    lum = luma(c)
    med = float(np.median(lum))
    # The panorama's own windows and sun patches -- far brighter than the room AND the colour of
    # daylight (blue to the red's: a lamp's warm light keeps its highlights) -- put out: a night
    # outside, dark and a little blue. The mask softened, so no ragged rims.
    # Only the glare of daylight itself (a white sheet or a sunlit floor is a few times the median, a
    # window's sky ten and more), softened; what it covers turns a dim night-blue, not black -- a
    # pane seen from inside at night, and no holes where the mask is ragged. Views that are mostly
    # window are not chosen (rooms.txt).
    bright = smoothstep(5.0, 12.0, lum / max(med, 1e-6))
    daylit = smoothstep(0.45, 0.75, c[..., 2] / np.maximum(c[..., 0], 1e-6))
    out = np.clip(box_blur(bright * daylit, 2) * 1.5, 0.0, 1.0)
    keep = 1.0 - out
    # Neutral, but gently: the grey world of what stays, each channel moved at most 0.7..1.4 (a room
    # photographed in yellow light stays a little yellow rather than turning blue).
    mean_rgb = (c * keep[..., None]).reshape(-1, 3).sum(axis=0) / max(keep.sum(), 1e-6)
    gain = np.clip(mean_rgb.mean() / np.maximum(mean_rgb, 1e-6), 0.7, 1.4)
    c = c * gain[None, None, :]
    c = np.minimum(c, med * 6.0)                  # what was left of the daylight's glare
    # Lamplight: brighter round the lamps, the rest of the room in their spill.
    n = c.shape[0]
    s = (np.arange(n) + 0.5) / n
    x, y = np.meshgrid(s, s)
    light = np.full_like(x, 0.5)
    for (lx, ly) in lamps:
        dx, dy = (x - lx) * 1.4, (y - ly)
        light += 0.7 / (1.0 + 5.0 * (dx * dx + dy * dy))
    c = c * light[..., None]
    # What the glare covered is filled from round it (normalised convolution: the blurred room over
    # the blurred mask, a wide reach where a near one finds nothing) and dimmed, with a little night
    # blue: a pane seen from inside at night, its curtain catching the room's light -- no holes.
    def fill(radius):
        den = box_blur(keep, radius)
        num = np.stack([box_blur(c[..., i] * keep, radius) for i in range(3)], axis=2)
        return num / np.maximum(den, 1e-4)[..., None], den
    near, near_w = fill(12)
    far, _ = fill(48)
    filled = near * smoothstep(0.05, 0.3, near_w)[..., None] + far * (1.0 - smoothstep(0.05, 0.3, near_w))[..., None]
    pane = filled * np.array([0.45, 0.48, 0.55])[None, None, :]
    c = c * keep[..., None] + pane * out[..., None]
    c = np.power(np.maximum(c, 0.0) / max(med, 1e-6), 1.1) * med   # a little more contrast
    c = shoulder(to_mean(c, TILE_MEAN))
    return to_mean(c, TILE_MEAN)


def save_png(rgb, path):
    """Linear rgb (rows bottom-up) to an sRGB PNG through Blender's Standard view."""
    h, w, _ = rgb.shape
    img = bpy.data.images.new("out", w, h, alpha=False, float_buffer=True)
    rgba = np.concatenate([np.clip(rgb, 0.0, 1.0), np.ones((h, w, 1), dtype=np.float32)], axis=2)
    img.pixels.foreach_set(rgba.astype(np.float32).ravel())
    scene = bpy.context.scene
    scene.view_settings.view_transform = 'Standard'
    scene.view_settings.look = 'None'
    scene.view_settings.exposure = 0.0
    scene.view_settings.gamma = 1.0
    scene.render.image_settings.file_format = 'PNG'
    scene.render.image_settings.color_mode = 'RGB'
    scene.render.image_settings.color_depth = '8'
    img.save_render(path, scene=scene)
    bpy.data.images.remove(img)


def survey(src, out, yaws, size):
    os.makedirs(out, exist_ok=True)
    for f in sorted(os.listdir(src)):
        if not f.endswith(".hdr"):
            continue
        pano = load_hdri(os.path.join(src, f))
        for yaw in yaws:
            c = day_picture(view(pano, yaw, -3.0, 80.0, size))
            save_png(c, os.path.join(out, "%s_%03d.png" % (f[:-4], yaw)))
        print("SURVEY", f, flush=True)


def read_rooms(path):
    rooms = []
    for raw in open(path, encoding="utf-8"):
        line = raw.split("#")[0].strip()
        if not line:
            continue
        p = line.split()
        if p[0].startswith("@"):
            # "@name kind": a room of our own (render-offices.py), no panorama.
            if len(p) < 2 or p[1] not in KINDS:
                raise ValueError("a room of our own needs a kind: %s" % raw.strip())
            rooms.append(dict(slug=p[0], yaw=0.0, pitch=0.0, fov=0.0, kind=p[1], lamps=[]))
            continue
        lamps = [tuple(float(v) for v in a.split(",")) for a in p[5:]] or [(0.5, 0.85)]
        if p[4] not in KINDS:
            raise ValueError("unknown kind '%s' in: %s" % (p[4], raw.strip()))
        rooms.append(dict(slug=p[0], yaw=float(p[1]), pitch=float(p[2]), fov=float(p[3]), kind=p[4], lamps=lamps))
    return rooms


def bake(src, rooms_file, out):
    os.makedirs(out, exist_ok=True)
    rooms = read_rooms(rooms_file)
    # Grouped by kind, in KINDS' order: each kind's tiles together.
    rooms.sort(key=lambda r: KINDS.index(r["kind"]))
    if len(rooms) > PER_ROW * ROWS:
        raise ValueError("%d rooms, the atlas holds %d" % (len(rooms), PER_ROW * ROWS))
    night = np.zeros((NIGHT_TILE * ROWS, NIGHT_TILE * PER_ROW, 3), dtype=np.float32)
    day = np.zeros((DAY_TILE * ROWS, DAY_TILE * PER_ROW, 3), dtype=np.float32)
    rendered = os.path.join(os.path.dirname(os.path.abspath(src)), "rendered")
    cache = {}
    tiles_dir = os.path.join(out, "tiles")
    os.makedirs(tiles_dir, exist_ok=True)
    taken = {}                                    # slug: the yaws already used
    for k, room in enumerate(rooms):
        if room["slug"].startswith("@"):
            # A room of our own (render-offices.py): rendered as the box room already, by night with
            # only its own lights -- graded to the atlas's mean, nothing put out.
            name = room["slug"][1:]
            n_full = load_hdri(os.path.join(rendered, name + "_night.exr"))
            d_full = load_hdri(os.path.join(rendered, name + "_day.exr"))
            f = n_full.shape[0] // NIGHT_TILE
            n_pic = n_full.reshape(NIGHT_TILE, f, NIGHT_TILE, f, 3).mean(axis=(1, 3))
            n_pic = to_mean(shoulder(to_mean(n_pic, TILE_MEAN)), TILE_MEAN)
            g = d_full.shape[0] // DAY_TILE
            d_pic = day_picture(d_full.reshape(DAY_TILE, g, DAY_TILE, g, 3).mean(axis=(1, 3)))
            col, row = k % PER_ROW, k // PER_ROW
            night[row * NIGHT_TILE:(row + 1) * NIGHT_TILE, col * NIGHT_TILE:(col + 1) * NIGHT_TILE] = n_pic
            day[row * DAY_TILE:(row + 1) * DAY_TILE, col * DAY_TILE:(col + 1) * DAY_TILE] = d_pic
            save_png(n_pic, os.path.join(tiles_dir, "%03d_%s_%s_night.png" % (k, room["kind"], name)))
            save_png(d_pic, os.path.join(tiles_dir, "%03d_%s_%s_day.png" % (k, room["kind"], name)))
            print("ROOM", k, room["kind"], room["slug"], flush=True)
            continue
        if room["slug"] not in cache:
            cache.clear()
            cache[room["slug"]] = load_hdri(os.path.join(src, room["slug"] + ".hdr"))
        pano = cache[room["slug"]]
        # The room's walls square to the view (rooms.txt chose what to look at; this turns it until
        # a wall faces the window), then the box of its kind's shape. Two views of one panorama that
        # turn to the same wall: the second takes the next wall round, the side nearer its choice.
        yaw = align_yaw(pano, room["yaw"])
        used = taken.setdefault(room["slug"], [])
        def clash(y):
            return any(abs((y - u + 180.0) % 360.0 - 180.0) < 45.0 for u in used)
        if clash(yaw):
            options = [y for y in (yaw + 90.0, yaw - 90.0, yaw + 180.0) if not clash(y)]
            options.sort(key=lambda y: abs((y - room["yaw"] + 180.0) % 360.0 - 180.0))
            yaw = options[0] if options else yaw
        used.append(yaw)
        aspect, depth = SHAPES[room["kind"]]
        v = box_view(pano, yaw, aspect, depth, NIGHT_TILE)
        n_pic = night_picture(v, room["lamps"])
        d_pic = day_picture(v.reshape(DAY_TILE, 2, DAY_TILE, 2, 3).mean(axis=(1, 3)))
        col, row = k % PER_ROW, k // PER_ROW      # row counted from the bottom, as the arrays are
        night[row * NIGHT_TILE:(row + 1) * NIGHT_TILE, col * NIGHT_TILE:(col + 1) * NIGHT_TILE] = n_pic
        day[row * DAY_TILE:(row + 1) * DAY_TILE, col * DAY_TILE:(col + 1) * DAY_TILE] = d_pic
        save_png(n_pic, os.path.join(tiles_dir, "%02d_%s_%s_%d_night.png" % (k, room["kind"], room["slug"], yaw)))
        save_png(d_pic, os.path.join(tiles_dir, "%02d_%s_%s_%d_day.png" % (k, room["kind"], room["slug"], yaw)))
        print("ROOM", k, room["kind"], room["slug"], room["yaw"], "->", yaw, flush=True)
    save_png(night, os.path.join(out, "rooms-night.png"))
    save_png(day, os.path.join(out, "rooms-day.png"))
    with open(os.path.join(out, "rooms-pools.txt"), "w", encoding="utf-8") as f:
        # Tiles a row, a tile's pixels by night and by day, rooms, the back wall's share of the
        # picture; then per kind: first tile, how many, its box's aspect and depth.
        f.write("%d %d %d %d %.4f\n" % (PER_ROW, NIGHT_TILE, DAY_TILE, len(rooms), FAR))
        for kind in KINDS:
            idx = [i for i, r in enumerate(rooms) if r["kind"] == kind]
            aspect, depth = SHAPES[kind]
            f.write("%s %d %d %.4f %.4f\n" % (kind, idx[0] if idx else 0, len(idx), aspect, depth))
    print("BAKE OK", len(rooms), "rooms", flush=True)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    try:
        if argv[0] == "survey":
            yaws = [int(v) for v in argv[3].split(",")] if len(argv) > 3 else [0, 60, 120, 180, 240, 300]
            survey(argv[1], argv[2], yaws, int(argv[4]) if len(argv) > 4 else 192)
        elif argv[0] == "bake":
            bake(argv[1], argv[2], argv[3])
        else:
            raise ValueError("survey or bake")
    except Exception as e:
        print("ROOMS FAILED", repr(e), flush=True)
        import traceback
        traceback.print_exc()
        sys.exit(1)


if __name__ == "__main__":   # render-offices.py imports the grading
    main()
