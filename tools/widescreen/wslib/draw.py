"""False-colour pictures of a Frame.

Faces are tinted by plane (X planes red, Y green, Z blue, slanted grey), each mesh
with its own hue shift, darker with depth. Magenta = void; repair triangles are
shaded by their normal with a cyan tint; white = 4:3 edge.
"""
import numpy as np
from PIL import Image, ImageDraw

from .raster import CORE, REPAIR_LAYER

_HUES = [(1.0, 1.0, 1.0), (1.0, 0.8, 0.6), (0.6, 1.0, 0.8), (0.8, 0.6, 1.0), (1.0, 1.0, 0.5), (0.5, 1.0, 1.0)]


def plane_of(m, pi):
    v = m.corners(pi)
    for a, n in ((0, "X"), (1, "Y"), (2, "Z")):
        if v[0, a] == v[1, a] == v[2, a]:
            return n, int(v[0, a])
    return "S", 0


def shaded(fr, repairs_mark=True):
    img = np.zeros((fr.h, fr.w, 3), np.uint8)
    img[:] = (255, 0, 255)
    keys = np.unique(np.stack([fr.layer, fr.poly], -1).reshape(-1, 2), axis=0)
    base = {"X": np.array([200, 70, 60]), "Y": np.array([70, 190, 70]), "Z": np.array([70, 90, 220]),
            "S": np.array([150, 150, 150])}
    for li, pi in keys:
        if li < 0:
            continue
        repair = li >= REPAIR_LAYER
        m = fr.layers[li - REPAIR_LAYER if repair else li]
        mask = (fr.layer == li) & (fr.poly == pi)
        if repair:
            t = fr.repair_tris[li][pi]
            n = np.cross(t[1] - t[0], t[2] - t[0])
            n = np.abs(n) / (np.linalg.norm(n) or 1)
            col = (np.array([200, 70, 60]) * n[0] + np.array([70, 190, 70]) * n[1] + np.array([70, 90, 220]) * n[2])
            # Cyan tint marks repairs.
            col = col * 0.55 + np.array([0, 110, 110]) if repairs_mark else col
        else:
            col = base[plane_of(m, pi)[0]] * np.array(_HUES[li % len(_HUES)])
            col = col * (0.8 + 0.2 * ((pi * 7919) % 5) / 4)
            if m.backdrop:
                col = np.array([120, 120, 40])
        d = fr.depth[mask]
        d = np.where(d > 2.5e9, d - 3e9, np.where(d > 1.5e9, d - 2e9, np.where(d > 5e8, d - 1e9, np.where(d > 5e5, d - 1e6, d))))
        k = np.clip(1.25 - d / 9000, 0.35, 1)[:, None]
        img[mask] = np.clip(col * k, 0, 255).astype(np.uint8)
    for x in (fr.cx - CORE, fr.cx + CORE - 1):
        img[:, x] = (255, 255, 255)
    return img


def save(img, path, scale=2, notes=()):
    im = Image.fromarray(img).resize((img.shape[1] * scale, img.shape[0] * scale), Image.NEAREST)
    if notes:
        d = ImageDraw.Draw(im)
        for i, t in enumerate(notes):
            d.text((4, 4 + 12 * i), t, fill=(255, 255, 0))
    im.save(path)
    return im
