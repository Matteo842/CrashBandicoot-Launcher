"""ASCII map of the faces in one axis plane: which cells exist, which are half cells,
which texinfo each uses (letters), and where the 16:9 void meets the plane ('?').

  A..Z  whole cell (letter = texinfo, legend below)    a..z  partly covered (half cell)
  .     empty                                           ?     empty and seen through a void
Columns/rows are a uniform lattice at the plane's most common cell size.
"""
from collections import Counter

import numpy as np

AXES = {"X": (2, 1), "Y": (0, 2), "Z": (0, 1)}  # plane axis -> (u axis, v axis)


def _inside(p, a, b, c):
    def s(p, q, r):
        return (p[0] - r[0]) * (q[1] - r[1]) - (q[0] - r[0]) * (p[1] - r[1])
    d1, d2, d3 = s(p, a, b), s(p, b, c), s(p, c, a)
    neg = d1 < 0 or d2 < 0 or d3 < 0
    pos = d1 > 0 or d2 > 0 or d3 > 0
    return not (neg and pos)


def faces_in_plane(meshes, axis, value, box=None):
    a = "XYZ".index(axis)
    out = []
    for m in meshes:
        p = m.pos[m.tri]
        sel = (p[:, :, a] == value).all(1)
        if box is not None:
            lo, hi = np.array(box[0]), np.array(box[1])
            sel &= ((p >= lo) & (p <= hi)).all((1, 2))
        for pi in np.nonzero(sel)[0]:
            out.append((m, int(pi), p[pi]))
    return out


def grid_map(meshes, axis, value, box=None, hits=None, extend=0):
    faces = faces_in_plane(meshes, axis, value, box)
    if not faces:
        return f"no faces in {axis}={value}"
    ua, va = AXES[axis]
    cu = sorted({int(v[ua]) for _, _, t in faces for v in t})
    cv = sorted({int(v[va]) for _, _, t in faces for v in t})

    def spacing(c):
        d = Counter(b - a for a, b in zip(c, c[1:]) if b - a > 16)
        return d.most_common(1)[0][0] if d else 400

    def lattice(c, step, extra):
        # Uniform lines at the most common phase, covering the faces and the void hits.
        phase = Counter(x % step for x in c).most_common(1)[0][0]
        lo, hi = min(c + extra), max(c + extra)
        start = lo - ((lo - phase) % step)
        return list(range(int(start), int(hi) + step, step))
    su, sv = spacing(cu), spacing(cv)
    hu = list(hits[:, ua]) if hits is not None and len(hits) else []
    hv = list(hits[:, va]) if hits is not None and len(hits) else []
    us = lattice(cu, su, hu)
    vs = lattice(cv, sv, hv)
    for _ in range(extend):
        us = [us[0] - su] + us + [us[-1] + su]
    tinfs = Counter(int(m.tinf[pi]) for m, pi, _ in faces)
    letters = {t: chr(65 + i) if i < 26 else "*" for i, (t, _) in enumerate(tinfs.most_common())}
    tris = [((t[0][ua], t[0][va]), (t[1][ua], t[1][va]), (t[2][ua], t[2][va]), letters[int(m.tinf[pi])], m, pi)
            for m, pi, t in faces]
    seen = set()
    if hits is not None and len(hits):
        for h in hits:
            i = np.searchsorted(us, h[ua]) - 1
            j = np.searchsorted(vs, h[va]) - 1
            seen.add((int(i), int(j)))
    rows = []
    for j in range(len(vs) - 2, -1, -1) if axis != "Y" else range(len(vs) - 1):
        line = []
        for i in range(len(us) - 1):
            u0, u1, v0, v1 = us[i], us[i + 1], vs[j], vs[j + 1]
            du, dv = (u1 - u0) * 0.15, (v1 - v0) * 0.15
            probes = [(u0 + du, v0 + dv), (u1 - du, v1 - dv), (u0 + du, v1 - dv), (u1 - du, v0 + dv)]
            hit = [None] * 4
            for a, b, c, ch, m, pi in tris:
                for k, p in enumerate(probes):
                    if hit[k] is None and _inside(p, a, b, c):
                        hit[k] = ch
            n = sum(h is not None for h in hit)
            ch = next((h for h in hit if h), None)
            if n == 4:
                line.append(ch)
            elif n >= 1:
                line.append(ch.lower())
            else:
                line.append("?" if (i, j) in seen else ".")
        rows.append(f"{vs[j]:>7} {''.join(line)}")
    head = f"{axis}={value}: u={'XYZ'[ua]} {us[0]}..{us[-1]} (cell ~{su}), v={'XYZ'[va]} {vs[0]}..{vs[-1]} (cell ~{sv})"
    legend = "  ".join(f"{ch}=tinf{t}x{tinfs[t]}" for t, ch in letters.items())
    ticks = "".join("|" if i % 5 == 0 else " " for i in range(len(us) - 1))
    cols = "  ".join(f"{i}:{us[i]}" for i in range(0, len(us) - 1, 5))
    return "\n".join([head, f"{'':>7} {ticks}  columns {cols}"] + rows + [legend])
