"""Textured offline render: the ID buffer of raster.render shaded with the level's real
texture pages (decoded straight from the NSF), so wrongly textured repairs and
transparent texels show without a game window.

Per pixel the camera ray meets the pixel's triangle; its barycentric weights give
the UV (and Gouraud colour) like the GPU's affine mapping would (close enough at
these sizes). Animated textures use frame 0. Transparent texels (value 0) are
drawn dark magenta; void stays bright magenta.
"""
import json
import struct

import numpy as np

from .disc import disc
from .level import UV_MAP, exe
from .nsf import eidstr, pages
from .raster import REPAIR_LAYER

_pages = {}


def tex_pages(lid):
    if lid not in _pages:
        out = {}
        for pg in pages(disc().read(f"S00000{lid:02X}.NSF")):
            _, typ, eid = struct.unpack_from("<HHI", pg, 0)
            if typ == 1:
                out[eidstr(eid)] = np.frombuffer(pg, "<u2").reshape(128, 256)
        _pages[lid] = out
    return _pages[lid]


def material(m, pi):
    """(page eid, seg, colour mode, clut col, clut row) of polygon pi, or None if untextured."""
    t = m.tinf[pi]
    if not m.textured[pi] or t + 1 >= len(m.texinfo):
        return None
    col, rgn = m.texinfo[t], m.texinfo[t + 1]
    return (m.tpages[m.tpag[pi]], (rgn >> 18) & 3, (rgn >> 20) & 3, (col >> 24) & 15, (rgn >> 6) & 0x7F)


def sample(page, seg, cm, cc, cr, u, v):
    """15-bit texel values at integer u, v arrays."""
    u = np.asarray(u, int) & 255
    v = np.asarray(v, int) & 127
    clut = page[cr, cc * 16:cc * 16 + 256].astype(int)
    if cm == 0:
        idx = (page[v, (seg * 64 + u // 4) & 255].astype(int) >> ((u & 3) * 4)) & 15
        return clut[idx]
    if cm == 1:
        idx = (page[v, (seg * 64 + u // 2) & 255].astype(int) >> ((u & 1) * 8)) & 255
        return clut[np.minimum(idx, len(clut) - 1)]
    return page[v, (seg * 64 + u) & 255].astype(int)


def load_repair_uvs(path):
    """{mesh: [(source polygon, 3x5 u,v,r,g,b)]} from the RepairDump JSON."""
    data = json.load(open(path))
    return {n: [(r[0], np.array(r[11:26], float).reshape(3, 5)) for r in e["repairs"]]
            for n, e in data["meshes"].items()}


def textured(fr, lid, repair_uvs=None, rows=None):
    """RGB image of a Frame. repair_uvs: load_repair_uvs() output for repair layers."""
    cam = fr.cam
    tp = tex_pages(lid)
    img = np.zeros((fr.h, fr.w, 3), np.uint8)
    img[:] = (255, 0, 255)
    ys, xs = np.mgrid[0:fr.h, 0:fr.w]
    # Camera-space ray per pixel; world direction = R^-1 d.
    d = np.stack([(xs - fr.cx) / cam.proj, (ys - fr.cy) / cam.proj, np.ones_like(xs, float)], -1)
    Rinv = np.linalg.inv(cam.R)
    keys = np.unique(np.stack([fr.layer, fr.poly], -1).reshape(-1, 2), axis=0)
    for li, pi in keys:
        if li < 0:
            continue
        repair = li >= REPAIR_LAYER
        m = fr.layers[li - REPAIR_LAYER if repair else li]
        mask = (fr.layer == li) & (fr.poly == pi)
        if repair:
            tri = fr.repair_tris[li][pi].astype(float)
            if m.backdrop:
                tri = tri - np.array(m.trans, float) + cam.pos
            src, uvs = repair_uvs[m.name][pi] if repair_uvs else (None, None)
            if src is None:
                img[mask] = (0, 200, 200)
                continue
            mat = material(m, src)
            if mat is None:
                img[mask] = np.clip(uvs[:, 2:5].mean(0), 0, 255).astype(np.uint8)
                continue
            uv = uvs[:, :2]
            rgb = uvs[:, 2:5]
        else:
            tri = m.corners(pi).astype(float)
            if m.backdrop:
                tri = tri - np.array(m.trans, float) + cam.pos
            mat = material(m, pi)
            rgb = m.rgb[m.tri[pi]].astype(float)
            if mat is None:
                img[mask] = np.clip(rgb.mean(0), 0, 255).astype(np.uint8)
                continue
            uv = np.array(m.uvs(pi), float)
        dirs = d[mask] @ Rinv.T
        o = cam.pos
        e1, e2 = tri[1] - tri[0], tri[2] - tri[0]
        n = np.cross(e1, e2)
        den = dirs @ n
        den[np.abs(den) < 1e-9] = 1e-9
        t = ((tri[0] - o) @ n) / den
        p = o + dirs * t[:, None] - tri[0]
        # Barycentric weights of p in (e1, e2).
        d00, d01, d11 = e1 @ e1, e1 @ e2, e2 @ e2
        d20, d21 = p @ e1, p @ e2
        det = d00 * d11 - d01 * d01 or 1e-9
        b1 = (d11 * d20 - d01 * d21) / det
        b2 = (d00 * d21 - d01 * d20) / det
        b0 = 1 - b1 - b2
        # Edge pixels hit just outside their triangle: clamp, then renormalise
        # so the UV stays inside the triangle's texture tile.
        w = np.clip(np.stack([b0, b1, b2], -1), 0, 1)
        w /= np.maximum(w.sum(-1, keepdims=True), 1e-9)
        u = np.floor(w @ uv[:, 0]).astype(int)
        v = np.floor(w @ uv[:, 1]).astype(int)
        shade = w @ rgb
        page, seg, cm, cc, cr = mat
        if page not in tp:
            img[mask] = (0, 200, 200)
            continue
        c = sample(tp[page], seg, cm, cc, cr, u, v)
        tex = np.stack([(c & 31) * 8, ((c >> 5) & 31) * 8, ((c >> 10) & 31) * 8], -1).astype(float)
        out = np.clip(tex * shade / 128, 0, 255).astype(np.uint8)
        out[c == 0] = (90, 0, 90)
        img[mask] = out
    return img
