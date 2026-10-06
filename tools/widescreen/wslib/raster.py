"""Software ID buffer of the native-wide view at 1x: 684x216, 4:3 core = 512 px.

Each pixel keeps (layer, polygon): layer = index into the drawn mesh list, or
REPAIR_LAYER + mesh index for runtime repair triangles. Back faces are culled
like the side pass (majority winding; noCull materials and repairs exempt).
"""
import numpy as np

W, H, CX, CY = 684, 216, 342, 108
CORE = 256          # half width of the 4:3 view
NEAR = 145          # projection / 2 + 1, as the side pass clips
REPAIR_LAYER = 1000


class Frame:
    def __init__(self, cam, layers, w=W, h=H):
        self.cam, self.layers = cam, layers
        self.w, self.h, self.cx, self.cy = w, h, w // 2, h // 2
        self.depth = np.full((h, w), np.inf)
        self.layer = np.full((h, w), -1, np.int32)
        self.poly = np.full((h, w), -1, np.int32)

    @property
    def void(self):
        return self.layer < 0

    def bands(self):
        """(left, right) boolean masks of the 16:9-only columns."""
        cols = np.arange(self.w)
        left = cols < self.cx - CORE
        right = cols >= self.cx + CORE
        return left, right

    def void_counts(self):
        left, right = self.bands()
        v = self.void
        return int(v[:, left].sum()), int(v[:, right].sum()), int(v[:, ~(left | right)].sum())

    def suspicious(self):
        """Band voids in rows where the 4:3 edge column shows scenery: the scenery seen at
        the edge does not continue. Rows whose edge is void too (pits, darkness) don't count."""
        left, right = self.bands()
        v = self.void
        l_edge = ~v[:, self.cx - CORE]
        r_edge = ~v[:, self.cx + CORE - 1]
        mask = np.zeros_like(v)
        mask[:, left] = v[:, left] & l_edge[:, None]
        mask[:, right] = v[:, right] & r_edge[:, None]
        return mask


def _clip_near(tri):
    inside = tri[:, 2] >= NEAR
    if inside.all():
        return [tri]
    poly = []
    for k in range(3):
        u, v = tri[k - 1], tri[k]
        if (u[2] >= NEAR) != (v[2] >= NEAR):
            t = (NEAR - u[2]) / (v[2] - u[2])
            poly.append(u + (v - u) * t)
        if v[2] >= NEAR:
            poly.append(v)
    return [np.array([poly[0], poly[j], poly[j + 1]]) for j in range(1, len(poly) - 1)]


def _raster(fr, tri, proj, layer, pi, xlo, xhi, depth_bias=0.0):
    p = np.empty((3, 3))
    p[:, 0] = fr.cx + proj * tri[:, 0] / tri[:, 2]
    p[:, 1] = fr.cy + proj * tri[:, 1] / tri[:, 2]
    p[:, 2] = tri[:, 2]
    x0 = max(int(p[:, 0].min()), xlo)
    x1 = min(int(p[:, 0].max()) + 1, xhi - 1)
    y0 = max(int(p[:, 1].min()), 0)
    y1 = min(int(p[:, 1].max()) + 1, fr.h - 1)
    if x0 > x1 or y0 > y1:
        return
    area = (p[1, 0] - p[0, 0]) * (p[2, 1] - p[0, 1]) - (p[1, 1] - p[0, 1]) * (p[2, 0] - p[0, 0])
    if abs(area) < 1e-6:
        return
    ys, xs = np.mgrid[y0:y1 + 1, x0:x1 + 1] + 0.5
    w0 = ((p[1, 0] - xs) * (p[2, 1] - ys) - (p[1, 1] - ys) * (p[2, 0] - xs)) / area
    w1 = ((p[2, 0] - xs) * (p[0, 1] - ys) - (p[2, 1] - ys) * (p[0, 0] - xs)) / area
    w2 = 1 - w0 - w1
    inside = (w0 >= -1e-9) & (w1 >= -1e-9) & (w2 >= -1e-9)
    if not inside.any():
        return
    z = 1 / (w0 / p[0, 2] + w1 / p[1, 2] + w2 / p[2, 2]) + depth_bias
    sub = fr.depth[y0:y1 + 1, x0:x1 + 1]
    closer = inside & (z < sub)
    sub[closer] = z[closer]
    fr.layer[y0:y1 + 1, x0:x1 + 1][closer] = layer
    fr.poly[y0:y1 + 1, x0:x1 + 1][closer] = pi


def _project_all(cs, proj, cx):
    z = np.maximum(cs[..., 2], 1.0)
    return cx + proj * cs[..., 0] / z


def render(cam, meshes, repairs=None, xrange=None, w=W, h=H, cull=True):
    """meshes: list of Mesh; repairs: {mesh name: list of (polygon, 3x3 absolute positions, behind)}.
    xrange: (lo, hi) pixel columns to rasterize (default: whole frame)."""
    fr = Frame(cam, meshes, w, h)
    fr.repair_tris = {}
    xlo, xhi = xrange or (0, w)
    proj = cam.proj
    batches = []
    for li, m in enumerate(meshes):
        cs = m.local @ cam.R.T if m.backdrop else cam.to_camera(m.pos)
        tris = cs[m.tri]
        nocull = (m.material & 0x10) != 0
        # Backdrops are drawn first and write no depth: everything else covers them.
        batches.append((li, tris, nocull, 1e9 if m.backdrop else 0.0))
    if repairs:
        for li, m in enumerate(meshes):
            rs = repairs.get(m.name)
            if not rs:
                continue
            pos = np.array([r[1] for r in rs], float).reshape(-1, 3)
            fr.repair_tris[REPAIR_LAYER + li] = pos.reshape(-1, 3, 3)
            if m.backdrop:
                # Backdrop repairs are drawn with the backdrop (sky arcs).
                pos = pos - np.array(m.trans, float)
            cs = (pos @ cam.R.T) if m.backdrop else cam.to_camera(pos)
            tris = cs.reshape(-1, 3, 3)
            # Behind-scenery repairs only fill what nothing else covers.
            # Backdrop repairs: with the backdrop, or (behind fills under the backdrop) farther still.
            bias = np.array([(3e9 if int(r[2]) == 2 else 1e9) if m.backdrop else {0: 0.0, 1: 1e6, 2: 2e9}[int(r[2])]
                             for r in rs])
            batches.append((REPAIR_LAYER + li, tris, np.ones(len(rs), bool), bias))
    # Majority winding of fully visible scenery decides which side is front.
    sign = 1
    if cull:
        votes = 0
        for li, tris, nocull, _ in batches:
            if li >= REPAIR_LAYER:
                continue
            ok = (tris[..., 2] > NEAR).all(1)
            if not ok.any():
                continue
            t = tris[ok]
            sx = fr.cx + proj * t[..., 0] / t[..., 2]
            sy = fr.cy + proj * t[..., 1] / t[..., 2]
            onscreen = (sx.max(1) > 0) & (sx.min(1) < w) & (sy.max(1) > 0) & (sy.min(1) < h)
            a = (sx[:, 1] - sx[:, 0]) * (sy[:, 2] - sy[:, 0]) - (sy[:, 1] - sy[:, 0]) * (sx[:, 2] - sx[:, 0])
            votes += int(np.sign(a[onscreen & ~nocull[ok]]).sum())
        sign = 1 if votes >= 0 else -1
    for li, tris, nocull, bias in batches:
        zmax = tris[..., 2].max(1)
        keep = zmax >= NEAR
        if not keep.any():
            continue
        sx = _project_all(tris, proj, fr.cx)
        front = tris[..., 2].min(1) >= NEAR
        # Off-range triangles (only when fully in front of the near plane).
        out = front & ((sx.max(1) < xlo) | (sx.min(1) >= xhi))
        keep &= ~out
        for pi in np.nonzero(keep)[0]:
            for piece in _clip_near(tris[pi]):
                if cull and not nocull[pi]:
                    px = fr.cx + proj * piece[:, 0] / piece[:, 2]
                    py = fr.cy + proj * piece[:, 1] / piece[:, 2]
                    a = (px[1] - px[0]) * (py[2] - py[0]) - (py[1] - py[0]) * (px[2] - px[0])
                    if a * sign < 0:
                        continue
                b = bias[pi] if isinstance(bias, np.ndarray) else bias
                _raster(fr, piece, proj, li, int(pi), xlo, xhi, b)
    return fr


def band_ranges(w=W):
    """Side columns plus the 4:3 edge column next to each."""
    cx = w // 2
    return [(0, cx - CORE + 1), (cx + CORE - 1, w)]


def render_bands(cam, meshes, repairs=None, cull=True):
    """Only the 16:9 side columns (the centre is left empty: much faster for scans)."""
    fr = None
    for lo, hi in band_ranges():
        part = render(cam, meshes, repairs, (lo, hi), cull=cull)
        if fr is None:
            fr = part
        else:
            fr.repair_tris.update(part.repair_tris)
            sel = slice(lo, hi)
            fr.depth[:, sel] = part.depth[:, sel]
            fr.layer[:, sel] = part.layer[:, sel]
            fr.poly[:, sel] = part.poly[:, sel]
    return fr
