"""What borders the 16:9 voids, and what kind of cut each border is."""
from collections import Counter, defaultdict

import numpy as np
from scipy import ndimage

from .draw import plane_of
from .raster import REPAIR_LAYER


def edge_counts(meshes):
    """Absolute edge -> number of triangles using it, across the drawn meshes."""
    counts = Counter()
    for m in meshes:
        p = m.pos[m.tri]
        for a, b in ((0, 1), (1, 2), (2, 0)):
            for u, v in zip(map(tuple, p[:, a]), map(tuple, p[:, b])):
                counts[(u, v) if u <= v else (v, u)] += 1
    return counts


def poly_info(m, pi, edges):
    v = [tuple(int(c) for c in x) for x in m.corners(pi)]
    open_edges = []
    for k in range(3):
        u, w = v[k], v[(k + 1) % 3]
        if edges[(u, w) if u <= w else (w, u)] == 1:
            open_edges.append(k)
    right = None
    for r in range(3):
        p, q, c = np.array(v[(r + 1) % 3]), np.array(v[(r + 2) % 3]), np.array(v[r])
        if np.dot(p - c, q - c) == 0:
            right = r
    # Half cell: right triangle whose hypotenuse (opposite the right angle) has no partner.
    half = right is not None and ((right + 1) % 3) in open_edges
    return dict(plane=plane_of(m, pi), open=open_edges, right=right, half=half, corners=v)


def components(fr, min_pixels=12):
    """Connected void regions inside the side bands: list of (side, mask, pixels)."""
    left, right = fr.bands()
    out = []
    for side, cols in (("L", left), ("R", right)):
        mask = fr.void & cols[None, :]
        lab, n = ndimage.label(mask)
        for i in range(1, n + 1):
            cm = lab == i
            px = int(cm.sum())
            if px >= min_pixels:
                out.append((side, cm, px))
    return out


def borders(fr, comp):
    """(layer, poly) -> number of void pixels of comp they touch (4-neighbourhood)."""
    grown = ndimage.binary_dilation(comp) & ~comp & ~fr.void
    keys = Counter(zip(fr.layer[grown].tolist(), fr.poly[grown].tolist()))
    return keys


def plane_hits(fr, comp, axis, value, step=2):
    """Where rays through comp's pixels meet the absolute plane axis=value (in front of the camera)."""
    ys, xs = np.nonzero(comp)
    if len(xs) == 0:
        return None
    sel = slice(None, None, step)
    d = np.stack([(xs[sel] + 0.5 - fr.cx) / fr.cam.proj, (ys[sel] + 0.5 - fr.cy) / fr.cam.proj,
                  np.ones(len(xs[sel]))], 1)
    dw = np.linalg.solve(fr.cam.R, d.T).T
    with np.errstate(divide="ignore", invalid="ignore"):
        t = (value - fr.cam.pos[axis]) / dw[:, axis]
    ok = np.isfinite(t) & (t > 0) & (t < 60000)
    if not ok.any():
        return None
    return fr.cam.pos + dw[ok] * t[ok, None]


def describe(level, fr, meshes, min_pixels=40):
    sus = fr.suspicious()
    """Per void component: border surfaces grouped by mesh and plane, with half cells and the
    region the void covers in each axis plane (absolute ranges)."""
    edges = edge_counts(meshes)
    report = []
    for side, comp, px in components(fr, min_pixels):
        ys, xs = np.nonzero(comp)
        b = borders(fr, comp)
        groups = defaultdict(lambda: dict(touch=0, polys=[], half=[], open=0, tex=Counter()))
        for (li, pi), n in b.most_common():
            repair = li >= REPAIR_LAYER
            m = meshes[li - REPAIR_LAYER if repair else li]
            if repair:
                g = groups[(m.name, "repair")]
                g["touch"] += n
                continue
            info = poly_info(m, pi, edges)
            key = (m.name, "sky" if m.backdrop else "%s=%d" % info["plane"] if info["plane"][0] != "S" else "slant")
            g = groups[key]
            g["touch"] += n
            g["polys"].append(pi)
            g["tex"][int(m.tinf[pi])] += 1
            if info["half"]:
                g["half"].append(pi)
            if info["open"]:
                g["open"] += 1
        entry = dict(side=side, pixels=px, beside=int((sus & comp).sum()), screen=(int(xs.min()), int(xs.max()), int(ys.min()), int(ys.max())),
                     frame_edge=bool(ys.min() == 0 or ys.max() == fr.h - 1 or xs.min() == 0 or xs.max() == fr.w - 1),
                     surfaces=[])
        for (name, plane), g in sorted(groups.items(), key=lambda e: -e[1]["touch"]):
            s = dict(mesh=name, plane=plane, touch=g["touch"], polys=sorted(set(g["polys"]))[:40],
                     half=sorted(set(g["half"])), open=g["open"], texinfo=dict(g["tex"].most_common(6)))
            if plane[:1] in "XYZ" and "=" in plane:
                axis, value = "XYZ".index(plane[0]), int(plane[2:])
                hits = plane_hits(fr, comp, axis, value)
                m = level.mesh(name)
                faces = [m.corners(p) for p in g["polys"]]
                if hits is not None and faces:
                    allv = np.concatenate(faces)
                    lo, hi = allv.min(0), allv.max(0)
                    # Keep hits near the surface that borders the void.
                    near = (np.abs(hits - np.clip(hits, lo - 3200, hi + 3200)).max(1) == 0)
                    h = hits[near]
                    if len(h):
                        s["void_in_plane"] = [[int(h[:, k].min()), int(h[:, k].max())] for k in range(3)]
                        s["surface_box"] = [[int(lo[k]), int(hi[k])] for k in range(3)]
            entry["surfaces"].append(s)
        if not groups:
            entry["kind"] = "nothing beyond"
        elif any(k[1] == "sky" for k in groups):
            entry["kind"] = "sky/backdrop end"
        elif any(groups[k]["half"] for k in groups):
            entry["kind"] = "half cells (per-triangle 4:3 cull)"
        else:
            entry["kind"] = "surface ends"
        report.append(entry)
    return report


def format_report(report):
    lines = []
    for e in report:
        if "side" not in e:
            lines.append("  repairs fill %d px, z-fight %d px (must be 0), cover %d px from in front" % e["effect"])
            continue
        lines.append(f"  void {e['side']} {e['pixels']} px ({e['beside']} beside scenery) screen x{e['screen'][0]}-{e['screen'][1]} "
                     f"y{e['screen'][2]}-{e['screen'][3]}  [{e['kind']}]")
        for s in e["surfaces"][:8]:
            extra = ""
            if "void_in_plane" in s:
                v, b = s["void_in_plane"], s["surface_box"]
                extra = " void " + " ".join(f"{a}{r[0]}..{r[1]}" for a, r in zip("XYZ", v)) \
                    + " | surface " + " ".join(f"{a}{r[0]}..{r[1]}" for a, r in zip("XYZ", b))
            half = f" half={s['half'][:8]}" if s.get("half") else ""
            lines.append(f"    {s['mesh']} {s['plane']} touch={s['touch']} open={s.get('open', 0)}"
                         f" polys={s.get('polys', [])[:10]}{half} tinf={list(s.get('texinfo', {}))}{extra}")
    return "\n".join(lines)


def components_of(m, polys=None, min_polys=40):
    """Connected parts (shared vertex positions) of a mesh's polygons."""
    from scipy.sparse import coo_matrix
    from scipy.sparse.csgraph import connected_components
    polys = np.arange(m.polys) if polys is None else np.asarray(polys)
    key = {}
    vid = np.array([key.setdefault(tuple(p), len(key)) for p in m.pos.tolist()])
    t = vid[m.tri[polys]]
    rows = np.concatenate([t[:, 0], t[:, 1], t[:, 2]])
    cols = np.concatenate([t[:, 1], t[:, 2], t[:, 0]])
    n = len(key)
    _, lab = connected_components(coo_matrix((np.ones(len(rows)), (rows, cols)), shape=(n, n)), directed=False)
    groups = defaultdict(list)
    for p, a in zip(polys, t[:, 0]):
        groups[lab[a]].append(int(p))
    return [g for g in sorted(groups.values(), key=len, reverse=True) if len(g) >= min_polys]


def vertical_axis(m, polys, tol=12):
    """Rotational objects (columns, towers, trunks): fit a circle to the vertices of each
    height; returns (cx, cz, rings, coverage, largest ring radius) when the rings share one
    centre, else None.
    coverage: 8 booleans, which 45-degree sectors (0 = +X, counter-clockwise towards -Z)
    have vertices."""
    v = np.unique(np.concatenate([m.corners(p) for p in polys]), axis=0)
    centres = []
    for y in np.unique(v[:, 1]):
        ring = v[v[:, 1] == y][:, [0, 2]].astype(float)
        if len(ring) < 4:
            continue
        A = np.stack([ring[:, 0], ring[:, 1], np.ones(len(ring))], 1)
        c = np.linalg.lstsq(A, (ring ** 2).sum(1), rcond=None)[0]
        cx, cz = c[0] / 2, c[1] / 2
        r = np.sqrt(max(c[2] + cx * cx + cz * cz, 0))
        err = np.abs(np.hypot(ring[:, 0] - cx, ring[:, 1] - cz) - r).max()
        if err <= tol and r > 50:
            centres.append((cx, cz, r))
    if len(centres) < 3:
        return None
    c = np.array(centres)
    # The centre most rings agree on (a decoration ring can fit its own circle).
    votes = [int(((np.abs(c[:, 0] - x) <= tol) & (np.abs(c[:, 1] - z) <= tol)).sum()) for x, z, _ in c]
    best = int(np.argmax(votes))
    agree = (np.abs(c[:, 0] - c[best, 0]) <= tol) & (np.abs(c[:, 1] - c[best, 1]) <= tol)
    if agree.sum() < 3:
        return None
    c = c[agree]
    cx, cz = np.median(c[:, 0]), np.median(c[:, 1])
    ang = np.degrees(np.arctan2(-(v[:, 2] - cz), v[:, 0] - cx)) % 360
    near = np.hypot(v[:, 0] - cx, v[:, 2] - cz) > 100
    coverage = [bool(((ang[near] >= s * 45) & (ang[near] < s * 45 + 45)).any()) for s in range(8)]
    return float(cx), float(cz), len(c), coverage, float(c[:, 2].max())
