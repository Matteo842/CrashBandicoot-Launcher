"""Crash 1 level data: zones, camera paths, WGEO scenery meshes, texture UVs.

Formats: wurlyfox c1 src/formats (zdat.h, wgeo.h), cross-checked against
FramePacing.NativeWideRenderer.cs (vertex/polygon bitfields, TryNativeWideMaterial).
"""
import json
import math
import os
import struct
from functools import cached_property

import numpy as np

from .disc import disc
from .nsf import eidstr, load

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
CATALOG = os.path.join(ROOT, "RecompOne.Runtime", "Catalog", "data", "levels.scus94900.json")
UV_MAP = 0x80051774
WGEO, ZDAT = 3, 7


def catalog():
    return {e["id"]: e for e in json.load(open(CATALOG))["levels"]}


def _s13(v):
    return v - 0x2000 if v & 0x1000 else v


class Exe:
    """SCUS_949.00 text/data, addressable by RAM address."""

    def __init__(self):
        self.data = disc().read("SCUS_949.00")
        self.taddr = struct.unpack_from("<I", self.data, 0x18)[0]

    def u32(self, addr):
        return struct.unpack_from("<I", self.data, 0x800 + addr - self.taddr)[0]


_exe = None


def exe():
    global _exe
    if _exe is None:
        _exe = Exe()
    return _exe


class Mesh:
    """One WGEO entry. Positions are absolute (header translation added)."""

    def __init__(self, name, items):
        self.name = name
        h = items[0]
        self.header = h
        self.trans = struct.unpack_from("<3i", h, 0)
        pc, vc, tic, tpc, self.backdrop = struct.unpack_from("<5I", h, 12)
        self.tpages = [eidstr(e) for e in struct.unpack_from("<%dI" % tpc, h, 0x20)]
        self.texinfo = struct.unpack_from("<%dI" % tic, h, 0x40)
        self.poly_bytes, self.vert_bytes = items[1], items[2]
        local = np.zeros((vc, 3), np.int32)
        rgb = np.zeros((vc, 3), np.int32)
        self.water = np.zeros(vc, bool)
        for i in range(vc):
            v0, v1 = struct.unpack_from("<II", items[2], i * 8)
            local[i] = (_s13((v1 >> 3) & 0x1FFF) * 8, _s13((v1 >> 19) & 0x1FFF) * 8,
                        _s13((v0 >> 24) + (((v1 >> 1) & 3) << 8) + (((v1 >> 16) & 7) << 10)) * 8)
            rgb[i] = (v0 & 255, (v0 >> 8) & 255, (v0 >> 16) & 255)
            self.water[i] = bool(v1 & 1)
        self.local = local
        self.rgb = rgb
        self.pos = local + np.array(self.trans, np.int32)
        tri = np.zeros((pc, 3), np.int32)
        tinf = np.zeros(pc, np.int32)
        tpag = np.zeros(pc, np.int32)
        anim = np.zeros(pc, np.int32)
        for pi in range(pc):
            p0, p1 = struct.unpack_from("<II", items[1], pi * 8)
            tri[pi] = ((p1 >> 20) & 0xFFF, (p1 >> 8) & 0xFFF, (p0 >> 20) & 0xFFF)
            tinf[pi] = (p0 >> 8) & 0xFFF
            tpag[pi] = (p0 >> 5) & 7
            anim[pi] = (p1 >> 1) & 0xF
        self.tri, self.tinf, self.tpag, self.anim = tri, tinf, tpag, anim
        mat = np.array([self.texinfo[t] >> 24 if t < tic else 0 for t in tinf], np.int32)
        self.material = mat
        self.textured = (mat & 0x80) != 0
        self.semi = (mat & 0x60) != 0x60

    def __repr__(self):
        return f"<Mesh {self.name} polys={len(self.tri)} verts={len(self.pos)} backdrop={self.backdrop}>"

    @property
    def polys(self):
        return len(self.tri)

    def corners(self, pi):
        return self.pos[self.tri[pi]]

    def uvs(self, pi):
        """UVs of polygon pi as TryNativeWideMaterial computes them (anim frame 0, tpage V bit unknown)."""
        t = self.tinf[pi]
        if t + 1 >= len(self.texinfo) or not self.textured[pi]:
            return None
        rgn = self.texinfo[t + 1]
        cm = (rgn >> 20) & 3
        bu = ((rgn >> 10) & 0xF8) >> cm
        bv = (rgn & 0x1F) << 2
        e = exe()
        uv01 = e.u32(UV_MAP + (rgn >> 22) * 8)
        uv2 = e.u32(UV_MAP + (rgn >> 22) * 8 + 4) & 0xFFFF
        return [((bu + (uv01 & 255)) & 255, (bv + ((uv01 >> 8) & 255)) & 255),
                ((bu + ((uv01 >> 16) & 255)) & 255, (bv + (uv01 >> 24)) & 255),
                ((bu + (uv2 & 255)) & 255, (bv + (uv2 >> 8)) & 255)]

    def texkey(self, pi):
        """What a polygon looks like regardless of UV orientation: material word + texture region."""
        t = self.tinf[pi]
        return (self.texinfo[t], self.texinfo[t + 1] if t + 1 < len(self.texinfo) else 0)

    def subset(self, keep):
        """Shallow copy drawing only polygons where keep is True (ids stay in .orig)."""
        import copy
        c = copy.copy(self)
        idx = np.nonzero(keep)[0]
        c.orig = idx
        for f in ("tri", "tinf", "tpag", "anim", "material", "textured", "semi"):
            setattr(c, f, getattr(self, f)[idx])
        return c

    def in_box(self, lo, hi):
        p = self.pos[self.tri]
        return ((p >= np.array(lo)) & (p <= np.array(hi))).all((1, 2))

    def blob(self):
        return self.header, self.poly_bytes, self.vert_bytes


class Path:
    def __init__(self, zone, index, item, origin):
        self.zone, self.index = zone, index
        self.slst = eidstr(struct.unpack_from("<I", item, 0)[0])
        n = struct.unpack_from("<H", item, 30)[0]
        pts = []
        for k in range(n):
            x, y, z, a, b, c = struct.unpack_from("<6h", item, 50 + k * 12)
            pts.append((origin[0] + x, origin[1] + y, origin[2] + z, a, b, c))
        self.points = pts

    def __len__(self):
        return len(self.points)

    def camera(self, k):
        x, y, z, a, b, c = self.points[k]
        return Camera((x, y, z), a, b)

    @property
    def tag(self):
        return f"{self.zone}:{self.index}"


class Zone:
    def __init__(self, level, name, items):
        self.level, self.name = level, name
        h = items[0]
        self.header = h
        wc = struct.unpack_from("<I", h, 0)[0]
        self.worlds = [eidstr(struct.unpack_from("<I", h, 4 + i * 0x40)[0]) for i in range(wc)]
        pidx, pcount, ecount, ncount = struct.unpack_from("<4I", h, 0x204)
        self.neighbours = [eidstr(n) for n in struct.unpack_from("<8I", h, 0x214)[:ncount]]
        self.rect = struct.unpack_from("<6i", items[1], 0)
        self.flags = struct.unpack_from("<I", h, 0x2FC)[0] if len(h) >= 0x300 else 0
        self.paths = [Path(name, p, items[pidx + p], self.rect[:3]) for p in range(pcount)]
        self.entity_items = items[pidx + pcount:pidx + pcount + ecount]

    def __repr__(self):
        return f"<Zone {self.name} worlds={self.worlds} neigh={self.neighbours} paths={[len(p) for p in self.paths]}>"


class Level:
    def __init__(self, lid):
        self.id = lid
        info = catalog()[lid]
        self.name, self.code = info["name"], info["code"]
        self.file = f"S00000{lid:02X}.NSF"
        self.entries = load(disc().read(self.file))
        self._meshes = {}
        self.zones = {}
        for eid, (t, items) in self.entries.items():
            if t == ZDAT:
                n = eidstr(eid)
                self.zones[n] = Zone(self, n, items)

    def __repr__(self):
        return f"<Level {self.id} {self.name} {self.file} zones={len(self.zones)}>"

    @cached_property
    def mesh_names(self):
        return sorted(eidstr(e) for e, (t, _) in self.entries.items() if t == WGEO)

    def mesh(self, name):
        m = self._meshes.get(name)
        if m is None:
            for eid, (t, items) in self.entries.items():
                if t == WGEO and eidstr(eid) == name:
                    m = self._meshes[name] = Mesh(name, items)
                    break
            else:
                raise KeyError(name)
        return m

    def zone(self, name):
        if name in self.zones:
            return self.zones[name]
        full = f"{name}_{self.code}Z"
        if full in self.zones:
            return self.zones[full]
        raise KeyError(name)

    def zone_meshes(self, zone, neighbours=True):
        """Meshes the native-wide renderer draws: the zone's own (backdrops included) and
        its neighbours' scenery (no backdrops; residency is assumed)."""
        z = self.zone(zone) if isinstance(zone, str) else zone
        names = list(z.worlds)
        if neighbours:
            for n in z.neighbours:
                if n not in self.zones:
                    continue
                for w in self.zones[n].worlds:
                    if w not in names and not self.mesh(w).backdrop:
                        names.append(w)
        return [self.mesh(n) for n in names]

    def paths(self):
        for z in sorted(self.zones):
            yield from self.zones[z].paths

    def path(self, spec):
        """'a1:0' or 'a1_FZ:0'."""
        zone, p = spec.split(":")[:2]
        return self.zone(zone).paths[int(p)]


class Camera:
    """Crash 1 camera from a path point: pitch a and yaw b in 4096ths of a turn.
    The rotation matches the logged GTE matrix at 0x800577E4 (rows scaled by 4096,
    screen Y by 0.625)."""

    def __init__(self, pos, pitch, yaw, proj=288, matrix=None):
        self.pos = np.array(pos, float)
        self.pitch, self.yaw, self.proj = pitch, yaw, proj
        if matrix is None:
            a, b = pitch * math.tau / 4096, yaw * math.tau / 4096
            sa, ca, sb, cb = math.sin(a), math.cos(a), math.sin(b), math.cos(b)
            matrix = [cb * 4096, 0, -sb * 4096,
                      -sa * sb * 2560, -ca * 2560, -sa * cb * 2560,
                      -ca * sb * 4096, sa * 4096, -ca * cb * 4096]
            matrix = [int(round(v)) for v in matrix]
        self.matrix = list(matrix)
        self.R = np.array(self.matrix, float).reshape(3, 3) / 4096

    @classmethod
    def from_log(cls, e):
        return cls(e["cam"], 0, 0, e.get("proj", 288), e["matrix"])

    def __repr__(self):
        return f"<Camera {tuple(int(v) for v in self.pos)} pitch={self.pitch} yaw={self.yaw}>"

    def to_camera(self, pts):
        return (np.asarray(pts, float) - self.pos) @ self.R.T

    def ray(self, sx, sy, cx, cy):
        """World direction through 1x screen pixel (sx, sy) for a view centred at (cx, cy)."""
        d = np.array([(sx - cx) / self.proj, (sy - cy) / self.proj, 1.0])
        return np.linalg.solve(self.R, d)
