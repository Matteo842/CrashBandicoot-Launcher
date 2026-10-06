#!/usr/bin/env python
"""Native 16:9 scenery-hole toolkit for Crash 1 (any level). Reads the disc at run time.

  wide.py levels
  wide.py info  <level> [zone]
  wide.py scan  <level> [--step 4] [--zones a0,a1] [--repairs R.json [--check]] [--out DIR]
  wide.py view  <level> <where> [--repairs R.json] [--out P.png] [--meshes a,b --box ... --nocull]
  wide.py grid  <level> <mesh[,mesh]> <X|Y|Z>=<value> [--box x0,y0,z0,x1,y1,z1] [--at <where>]
  wide.py faces <level> <mesh[,mesh]> x0,y0,z0,x1,y1,z1 [--slant]
  wide.py poly  <level> <mesh> <poly>...
  wide.py route <level> <zone:path[:k0-k1|:k]>... [--ahead 1450] [--below 1255] [--every 6] [--speed 1200] [--hold 2]
  wide.py axes  <level> [--meshes a,b]   (towers/columns: vertical axis + mirror line)
  wide.py export <level> <dir>            (mesh blobs for the C# repair dump)
  wide.py pairs <before run> <after run> <out.png> x,z...   (probe before/after sheet)

<level>: id (41) or slug (the-lab). <where>: zone:path:k (a0:0:12), or
[@]x,y,z,pitch,yaw (path units; yaw 0 looks -Z, -1024 looks +X), or log:<probe run dir>:<render> (exact logged camera).
"""
import argparse
import collections
import json
import os
import struct
import sys
import time

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from wslib import analyze, draw, grid, raster  # noqa: E402
from wslib.level import Camera, Level, catalog  # noqa: E402


def level_arg(s):
    if s.isdigit():
        return Level(int(s))
    for lid, e in catalog().items():
        if e["slug"] == s:
            return Level(lid)
    raise SystemExit(f"unknown level {s}")


def where(L, spec):
    """-> (camera, zone name). A leading '@' is ignored (lets x start with a minus sign)."""
    spec = spec.lstrip("@")
    if spec.startswith("log:"):
        _, run, render = spec.split(":", 2) if spec.count(":") == 2 else (None, *spec[4:].rsplit(":", 1))
        e = next(e for e in json.load(open(os.path.join(run, "log.json"))) if e["render"] == int(render))
        return Camera.from_log(e), e["zone"]
    parts = spec.split(":")
    if len(parts) == 3:
        p = L.path(":".join(parts[:2]))
        k = min(int(parts[2]), len(p) - 1)
        return p.camera(k), L.zone(parts[0]).name
    x, y, z, a, b = map(float, spec.split(","))
    best = min(L.zones.values(), key=lambda zn: _zone_distance(zn, (x, y, z)))
    return Camera((x, y, z), a, b), best.name


def _zone_distance(z, p):
    x0, y0, z0, w, h, d = z.rect
    q = np.clip(p, (x0, y0, z0), (x0 + w, y0 + h, z0 + d))
    return float(np.linalg.norm(np.subtract(p, q)))


def load_repairs(path):
    """{mesh: [(polygon, 3x3 absolute, behind)]} from the C# dump (RepairDump)."""
    if not path:
        return None
    data = json.load(open(path))
    # Behind-scenery fills: 1 = behind all scenery, 2 = also under the backdrop.
    behind = 2 if data.get("under_backdrop") else 1
    out = {}
    for name, e in data["meshes"].items():
        out[name] = [(r[0], np.array(r[1:10], float).reshape(3, 3), behind if r[10] else 0) for r in e["repairs"]]
    return out


def own_repairs(L, zone, repairs):
    """The runtime draws repairs only for the camera zone's own meshes, not for neighbours."""
    if not repairs:
        return None
    own = set(L.zone(zone).worlds)
    return {k: v for k, v in repairs.items() if k in own}


def repair_effect(base, fixed):
    """(filled, fight, cover, fight mask) band pixels: voids the repairs fill; retail pixels a
    repair wins at nearly the same depth (coplanar overlap: z-fighting in game, must be 0) or
    sky pixels a scenery repair hides (backdrops write no depth; must be 0 too);
    retail pixels a repair covers from clearly in front (fine for a rebuilt tower in front
    of a wall, wrong for a copy sticking out)."""
    left, right = base.bands()
    cols = (left | right)[None, :]
    filled = base.void & ~fixed.void & cols
    over = (base.layer >= 0) & (fixed.layer >= raster.REPAIR_LAYER) & cols
    with np.errstate(invalid="ignore"):
        gap = base.depth - fixed.depth
    fight = over & (gap < 8 + 0.02 * base.depth)
    # Close depths along a crease (shared edge of two slanted faces) are no fight:
    # only nearly parallel faces (under 20 degrees) count.
    ys, xs = np.nonzero(fight)
    verdict = {}
    for y, x in zip(ys, xs):
        key = (fixed.layer[y, x], fixed.poly[y, x], base.layer[y, x], base.poly[y, x])
        if key not in verdict:
            r = fixed.repair_tris[key[0]][key[1]]
            o = base.layers[key[2]].corners(key[3]).astype(float)
            n1 = np.cross(r[1] - r[0], r[2] - r[0]); n2 = np.cross(o[1] - o[0], o[2] - o[0])
            cos = abs(n1 @ n2) / ((np.linalg.norm(n1) * np.linalg.norm(n2)) or 1)
            verdict[key] = cos > np.cos(np.radians(20))
        fight[y, x] = verdict[key]
    # Backdrops write no depth in game: any repair hides the sky where they overlap.
    sky = over & np.isin(base.layer, [li for li, m in enumerate(base.layers) if m.backdrop])         & ~np.isin(fixed.layer, [raster.REPAIR_LAYER + li for li, m in enumerate(base.layers) if m.backdrop])
    fight |= sky
    return int(filled.sum()), int(fight.sum()), int((over & ~fight).sum()), fight


def cmd_levels(a):
    for lid, e in catalog().items():
        if e["kind"] not in ("empty",):
            print(f"{lid:3d}  {e['code']}  {e['slug']:28s} {e['name']}")


def cmd_info(a):
    L = level_arg(a.level)
    print(L)
    zones = [L.zone(a.zone)] if a.zone else [L.zones[z] for z in sorted(L.zones)]
    for z in zones:
        print(f"{z.name} worlds={z.worlds} neigh={[n for n in z.neighbours if n != z.name]} rect={z.rect} "
              f"flags={z.flags:x} paths={[len(p) for p in z.paths]}")
        if a.zone:
            for p in z.paths:
                for k in range(0, len(p), max(1, len(p) // 8)):
                    print(f"   {p.tag}:{k} {p.points[k]}")
    print()
    for n in L.mesh_names:
        m = L.mesh(n)
        lo, hi = m.pos.min(0), m.pos.max(0)
        print(f"{n} polys={m.polys} verts={len(m.pos)} backdrop={m.backdrop} trans={m.trans} "
              f"box=({lo.tolist()})..({hi.tolist()}) tpages={m.tpages}")


def _save_view(L, cam, zone, repairs, out, centre=True, notes=()):
    ms = L.zone_meshes(zone)
    repairs = own_repairs(L, zone, repairs)
    fr = raster.render(cam, ms, repairs) if centre else raster.render_bands(cam, ms, repairs)
    rep = analyze.describe(L, fr, ms)
    img = draw.shaded(fr)
    notes = [f"{L.name} {zone} {cam}", *notes]
    if repairs:
        base = raster.render(cam, ms) if centre else raster.render_bands(cam, ms)
        filled, fight, cover, mask = repair_effect(base, fr)
        img[mask] = (255, 40, 0)
        notes.append(f"repairs fill {filled} px, z-fight {fight} px (red), cover {cover} px")
        rep.insert(0, dict(effect=(filled, fight, cover)))
    draw.save(img, out, notes=notes)
    return fr, rep


def cmd_view(a):
    L = level_arg(a.level)
    cam, zone = where(L, a.where)
    if a.meshes or a.box:
        # Inspection render: chosen meshes / box only, any camera, no analysis.
        ms = [L.mesh(n) for n in a.meshes.split(",")] if a.meshes else L.zone_meshes(zone)
        if a.box:
            lo, hi = _box(a.box)
            ms = [m.subset(m.in_box(lo, hi)) for m in ms]
        reps = None
        if a.repairs:
            reps = load_repairs(a.repairs)
            if a.box:
                lo, hi = (np.array(v) for v in _box(a.box))
                reps = {k: [r for r in v if ((r[1] >= lo) & (r[1] <= hi)).all()] for k, v in reps.items()}
            reps = {k: [(p, t, 0) for p, t, _ in v] for k, v in reps.items()}
        fr = raster.render(cam, ms, reps, cull=not a.nocull)
        out = a.out or "inspect.png"
        draw.save(draw.shaded(fr), out, notes=[f"{cam} {[m.name for m in ms]}"])
        print(out)
        return
    out = a.out or f"view_{L.id}_{a.where.replace(':', '_').replace(',', '_')}.png"
    fr, rep = _save_view(L, cam, zone, load_repairs(a.repairs), out)
    vl, vr, vc = fr.void_counts()
    print(f"{cam} zone {zone} meshes {[m.name for m in L.zone_meshes(zone)]}")
    print(f"void L={vl} R={vr} centre={vc} -> {out}")
    if rep and "effect" in rep[0]:
        print("repairs fill %d px, z-fight %d px (must be 0), cover %d px from in front" % rep[0]["effect"])
        rep = rep[1:]
    print(analyze.format_report(rep))


def cmd_scan(a):
    L = level_arg(a.level)
    repairs = load_repairs(a.repairs)
    out = a.out or f"scan_{L.id}"
    os.makedirs(out, exist_ok=True)
    zones = [L.zone(z).name for z in a.zones.split(",")] if a.zones else sorted(L.zones)
    points = []
    t0 = time.time()
    for zn in zones:
        z = L.zones[zn]
        ms = L.zone_meshes(z)
        for p in z.paths:
            ks = list(range(0, len(p), a.step))
            if ks[-1] != len(p) - 1:
                ks.append(len(p) - 1)
            own = own_repairs(L, zn, repairs)
            for k in ks:
                cam = p.camera(k)
                fr = raster.render_bands(cam, ms, own)
                vl, vr, _ = fr.void_counts()
                sus = fr.suspicious()
                e = dict(zone=zn, path=p.index, k=k, cam=[int(v) for v in cam.pos],
                         pitch=cam.pitch, yaw=cam.yaw, L=vl, R=vr,
                         SL=int(sus[:, :fr.cx].sum()), SR=int(sus[:, fr.cx:].sum()))
                if own and a.check:
                    e["filled"], e["fight"], e["cover"], _ = repair_effect(raster.render_bands(cam, ms), fr)
                points.append(e)
                if a.verbose:
                    print(f"{zn}:{p.index}:{k} L={vl} R={vr} {e.get('filled', '')} {e.get('fight', '')} {e.get('cover', '')}", flush=True)
        print(f"{zn}: {sum(len(p) for p in z.paths)} points, {time.time() - t0:.0f}s", flush=True)
    # Hotspots: consecutive scanned points of one path with voids.
    spots, cur = [], None
    for e in points:
        hot = e["SL"] + e["SR"] >= a.min
        same = cur and cur[-1]["zone"] == e["zone"] and cur[-1]["path"] == e["path"]
        if hot and same:
            cur.append(e)
        elif hot:
            cur = [e]
            spots.append(cur)
        else:
            cur = None
    spots.sort(key=lambda s: -max(e["SL"] + e["SR"] for e in s))
    fights = [e for e in points if e.get("fight")]
    covers = [e for e in points if e.get("cover")]
    lines = [f"# {L.name} (level {L.id}) native 16:9 void scan", "",
             f"{len(points)} camera points, step {a.step}, {len(spots)} hotspots (>= {a.min} band void px at 1x 684x216"
             " in rows where the 4:3 edge shows scenery; voids continuing a centre pit don't count).",
             f"Repairs: {a.repairs or 'none (retail meshes only)'}", ""]
    if a.check and a.repairs:
        lines += [f"Repairs win over retail pixels at nearly equal depth (z-fight) at {len(fights)} points (must be 0):", ""]
        lines += [f"- {e['zone']}:{e['path']}:{e['k']} z-fight {e['fight']} cover {e['cover']} filled {e['filled']}"
                  for e in sorted(fights, key=lambda e: -e["fight"])[:30]] + [""]
        lines += [f"Repairs cover retail pixels from in front at {len(covers)} points (check these look right):", ""]
        lines += [f"- {e['zone']}:{e['path']}:{e['k']} cover {e['cover']} filled {e['filled']}"
                  for e in sorted(covers, key=lambda e: -e["cover"])[:30]] + [""]
    for i, s in enumerate(spots):
        worst = max(s, key=lambda e: e["SL"] + e["SR"])
        where_ = f"{worst['zone']}:{worst['path']}:{worst['k']}"
        cam = L.path(f"{worst['zone']}:{worst['path']}").camera(worst["k"])
        img = os.path.join(out, f"spot{i:02d}_{where_.replace(':', '_')}.png")
        fr, rep = _save_view(L, cam, worst["zone"], repairs, img,
                             notes=[f"spot {i}: k {s[0]['k']}..{s[-1]['k']}"])
        lines += [f"## spot {i}: {worst['zone']} path {worst['path']} k {s[0]['k']}..{s[-1]['k']} "
                  f"(worst k {worst['k']}: void L={worst['L']} R={worst['R']}, beside scenery L={worst['SL']} R={worst['SR']})",
                  f"camera {cam.pos.astype(int).tolist()} pitch {cam.pitch} yaw {cam.yaw}; "
                  f"`wide.py view {L.id} {where_}`; image {os.path.basename(img)}", "```",
                  analyze.format_report(rep), "```", ""]
    open(os.path.join(out, "report.md"), "w").write("\n".join(lines))
    json.dump(dict(level=L.id, step=a.step, points=points), open(os.path.join(out, "scan.json"), "w"))
    print(f"{len(spots)} hotspots -> {out}/report.md ({time.time() - t0:.0f}s)")


def _box(s):
    v = list(map(float, s.split(",")))
    return (v[:3], v[3:])


def cmd_grid(a):
    L = level_arg(a.level)
    axis, value = a.plane.split("=")
    meshes = [L.mesh(n) for n in a.meshes.split(",")]
    hits = None
    if a.at:
        cam, zone = where(L, a.at)
        ms = L.zone_meshes(zone)
        fr = raster.render(cam, ms)
        hs = [analyze.plane_hits(fr, c, "XYZ".index(axis), float(value)) for _, c, _ in analyze.components(fr, 20)]
        hs = [h for h in hs if h is not None]
        if hs:
            hits = np.concatenate(hs)
            if a.box:
                lo, hi = _box(a.box)
                ua = [k for k in range(3) if k != "XYZ".index(axis)]
                pad = 4000
                keep = np.ones(len(hits), bool)
                for k in ua:
                    keep &= (hits[:, k] >= lo[k] - pad) & (hits[:, k] <= hi[k] + pad)
                hits = hits[keep]
    print(grid.grid_map(meshes, axis, float(value), _box(a.box) if a.box else None, hits, a.extend))


def cmd_faces(a):
    L = level_arg(a.level)
    lo, hi = _box(a.box)
    groups = {}
    for n in a.meshes.split(","):
        m = L.mesh(n)
        for pi in range(m.polys):
            v = m.corners(pi)
            if not ((v >= lo) & (v <= hi)).all():
                continue
            pl = draw.plane_of(m, pi)
            key = f"{pl[0]}={pl[1]}" if pl[0] != "S" else "slant"
            groups.setdefault(key, []).append((m, pi, v))
    for key in sorted(groups, key=lambda k: (k[0], float(k[2:]) if "=" in k else 0)):
        g = groups[key]
        if key == "slant" and not a.slant:
            print(f"slant {len(g)}")
            continue
        print(f"== {key} ({len(g)})")
        for m, pi, v in sorted(g, key=lambda e: (e[2][:, 1].min(), e[2][:, 0].min(), e[2][:, 2].min())):
            lo_, hi_ = v.min(0), v.max(0)
            print(f"  {m.name} p{pi} t{m.tinf[pi]} m{m.material[pi]:02x} x{lo_[0]}..{hi_[0]} y{lo_[1]}..{hi_[1]} "
                  f"z{lo_[2]}..{hi_[2]} rgb{m.rgb[m.tri[pi]].tolist()}")


def cmd_poly(a):
    L = level_arg(a.level)
    m = L.mesh(a.mesh)
    edges = analyze.edge_counts([m])
    for pi in map(int, a.polys):
        info = analyze.poly_info(m, pi, edges)
        print(f"p{pi} tinf={m.tinf[pi]} mat={m.material[pi]:02x} semi={m.semi[pi]} plane={info['plane']} "
              f"open_edges={info['open']} right={info['right']} half={info['half']}")
        for k, vi in enumerate(m.tri[pi]):
            uv = m.uvs(pi)
            print(f"   v{vi} {m.pos[vi].tolist()} rgb={m.rgb[vi].tolist()} uv={uv[k] if uv else None}")


def cmd_route(a):
    """Crash waypoints for the probe "path:" script: ahead of each camera point along its
    horizontal view direction and below it. Crash 1 cameras sit about 1400 behind and 1100-1250
    above Crash (The Lab: 1416 / 1125; check a probe log). zone:path:k0-k1 flies along a
    path section; zone:path:k stops there for --hold seconds."""
    L = level_arg(a.level)
    pts = []

    def crash_at(cam):
        f = cam.R[2].copy()
        f[1] = 0
        f /= np.linalg.norm(f) or 1
        return cam.pos + f * a.ahead + np.array([0, -a.below, 0])
    for spec in a.paths:
        parts = spec.split(":")
        p = L.path(":".join(parts[:2]))
        if len(parts) > 2 and "-" not in parts[2]:
            c = crash_at(p.camera(min(int(parts[2]), len(p) - 1)))
            pts.append(f"{c[0]:.0f},{c[1]:.0f},{c[2]:.0f},{a.fast}")
            # A one-unit step at hold speed keeps Crash there.
            pts.append(f"{c[0]:.0f},{c[1]:.0f},{c[2] - 1:.0f},{1 / a.hold:.4f}")
            continue
        k0, k1 = (map(int, parts[2].split("-")) if len(parts) > 2 else (0, len(p) - 1))
        for k in list(range(k0, k1 + 1, a.every)) + [k1]:
            c = crash_at(p.camera(k))
            pts.append(f"{c[0]:.0f},{c[1]:.0f},{c[2]:.0f}")
    print(f"path:{a.start}:{a.speed}:" + ";".join(pts))


def cmd_axes(a):
    """Round towers/columns: each is found by its rings (one vertical axis), gathered from every
    connected part inside a cylinder about the axis (shaft, ledges, crown, spikes). Towers come
    in mirrored twins (mirror in X plus a Z shift); a twin the 4:3 cull removed down to a few
    spikes is found by matching vertices. Prints the C# NativeWideTower lines: each tower is
    completed from its twin (mirrored) and from itself turned half a turn about its axis."""
    L = level_arg(a.level)
    names = a.meshes.split(",") if a.meshes else [n for n in L.mesh_names if not L.mesh(n).backdrop]
    for n in names:
        m = L.mesh(n)
        comps = analyze.components_of(m, min_polys=1)
        found = []
        for comp in comps:
            if len(comp) < a.min_part:
                continue
            v = np.concatenate([m.corners(p) for p in comp])
            lo, hi = v.min(0), v.max(0)
            if max(hi[0] - lo[0], hi[2] - lo[2]) > a.width:
                continue
            ax = analyze.vertical_axis(m, comp)
            # Machines and lamps fit rings too; towers are wide.
            if ax is not None and ax[4] >= 600 and not any(
                    abs(f[0] - ax[0]) <= 24 and abs(f[1] - ax[1]) <= 24 for f in found):
                found.append((round(ax[0]), round(ax[1])))

        def gather(cx, cz):
            seeds, polys = [], []
            for comp in comps:
                v = np.concatenate([m.corners(p) for p in comp])
                if v[:, 1].min() >= 1700 and (np.hypot(v[:, 0] - cx, v[:, 2] - cz) <= a.radius).all():
                    seeds.append(min(comp))
                    polys += comp
            return seeds, polys
        towers = []
        for cx, cz in found:
            seeds, polys = gather(cx, cz)
            v = np.concatenate([m.corners(p) for p in polys])
            if v[:, 1].max() - v[:, 1].min() >= a.height:
                towers.append(dict(x=cx, z=cz, seeds=seeds, polys=polys, twin=None))
        # Mirror twins: vertices at the same height matching x + x' = 2 * axis, z' = z + shift.
        byy = collections.defaultdict(list)
        for x, y, z in np.unique(m.pos, axis=0).tolist():
            byy[y].append((x, z))
        best = {}
        for i, t in enumerate(list(towers)):
            votes = collections.Counter()
            for x, y, z in np.unique(np.concatenate([m.corners(p) for p in t["polys"]]), axis=0).tolist():
                for x2, z2 in byy[y]:
                    if abs(x2 - x) > 2000:
                        votes[(x + x2, z2 - z)] += 1
            if votes and votes.most_common(1)[0][1] >= a.twin_votes:
                best[i] = votes.most_common(1)[0][0]
        for i, (total, shift) in list(best.items()):
            t = towers[i]
            tx, tz = total - t["x"], t["z"] + shift
            j = next((k for k, o in enumerate(towers) if abs(o["x"] - tx) <= 24 and abs(o["z"] - tz) <= 24), None)
            if j is None:
                seeds, polys = gather(tx, tz)
                towers.append(dict(x=tx, z=tz, seeds=seeds, polys=polys, twin=None, remnant=True))
                j = len(towers) - 1
            elif j in best and best[j] != (total, -shift):
                continue  # not each other's best match
            if t["twin"] is None and towers[j]["twin"] is None:
                t["twin"] = (j, total / 2, shift)
                towers[j]["twin"] = (i, total / 2, -shift)
        if not towers:
            continue
        print(f"{n}:")
        for i, t in enumerate(towers):
            tw = t["twin"]
            twin = f", Twin: {tw[0]}, TwinAxis: {tw[1]:g}, TwinShift: {tw[2]:g}" if tw else ""
            note = "  // remnant only" if t.get("remnant") else ""
            print(f"    new({t['x']}, {t['z']}, [{', '.join(map(str, t['seeds']))}]{twin}),{note}  // {i}: {len(t['polys'])} polys")


def cmd_pairs(a):
    """Before/after sheet of two probe runs (e.g. NOFIX=1 vs fixed): for each Crash position
    x,z the nearest captured frame of each run, side by side, with band void counts
    (magenta when the runs used VOID=1)."""
    from PIL import Image, ImageDraw
    runs = [(r, json.load(open(os.path.join(r, "log.json")))) for r in (a.before, a.after)]
    if a.at == ["all"]:
        # Every frame of the after run where Crash moved at least --every units.
        targets, last = [], None
        for e in runs[1][1]:
            if e["crash"] and (last is None or abs(e["crash"][2] - last[1]) + abs(e["crash"][0] - last[0]) >= a.every):
                last = (e["crash"][0], e["crash"][2])
                targets.append(last)
        if len(targets) > a.rows:
            stem, ext = os.path.splitext(a.out)
            for i in range(0, len(targets), a.rows):
                a2 = argparse.Namespace(**{**vars(a), "at": [f"{x},{z}" for x, z in targets[i:i + a.rows]],
                                           "out": f"{stem}_{i // a.rows:02d}{ext}"})
                cmd_pairs(a2)
            return
    else:
        targets = [tuple(map(float, t.split(","))) for t in a.at]
    W, H = 912, 288
    sheet = Image.new("RGB", (W * 2, H * len(targets)))
    d = ImageDraw.Draw(sheet)
    for i, (x, z) in enumerate(targets):
        for k, (run, log) in enumerate(runs):
            e = min((e for e in log if e["crash"]), key=lambda e: (e["crash"][0] - x) ** 2 + (e["crash"][2] - z) ** 2)
            im = Image.open(os.path.join(run, f"r{e['render']:05d}.png")).convert("RGB")
            px = np.array(im).astype(int)
            mag = (px[..., 0] > 200) & (px[..., 1] < 60) & (px[..., 2] > 200)
            band = px.shape[1] * 86 // 684
            void = int(mag[:, :band].sum() + mag[:, -band:].sum())
            sheet.paste(im.resize((W, H)), (k * W, i * H))
            d.text((k * W + 4, i * H + 4), f"{os.path.basename(run)} r{e['render']} {e['zone']} crash {e['crash']} "
                   f"cam {e['cam']} band void {void}", fill=(255, 255, 0))
    sheet.save(a.out)
    print(a.out)


def cmd_export(a):
    """Mesh blobs for tools/widescreen/RepairDump: header + polygons + vertices, plus the exe
    (for the UV map). Game data: keep the directory out of the repository."""
    L = level_arg(a.level)
    os.makedirs(a.dir, exist_ok=True)
    from wslib.level import exe
    open(os.path.join(a.dir, "exe.bin"), "wb").write(exe().data)
    index = dict(level=L.id, exe_taddr=exe().taddr, meshes=[])
    for n in L.mesh_names:
        m = L.mesh(n)
        h, p, v = m.blob()
        fn = f"{n}.bin"
        open(os.path.join(a.dir, fn), "wb").write(h + p + v)
        index["meshes"].append(dict(name=n, file=fn, header=len(h), polys=len(p), verts=len(v)))
    json.dump(index, open(os.path.join(a.dir, "index.json"), "w"), indent=1)
    print(f"{len(index['meshes'])} meshes -> {a.dir}")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("levels")
    s = sub.add_parser("info"); s.add_argument("level"); s.add_argument("zone", nargs="?")
    s = sub.add_parser("view"); s.add_argument("level"); s.add_argument("where")
    s.add_argument("--repairs"); s.add_argument("--out")
    s.add_argument("--meshes"); s.add_argument("--box"); s.add_argument("--nocull", action="store_true")
    s = sub.add_parser("scan"); s.add_argument("level"); s.add_argument("--step", type=int, default=4)
    s.add_argument("--zones"); s.add_argument("--repairs"); s.add_argument("--out")
    s.add_argument("--min", type=int, default=120); s.add_argument("-v", "--verbose", action="store_true")
    s.add_argument("--check", action="store_true", help="with --repairs: also count retail pixels repairs cover")
    s = sub.add_parser("grid"); s.add_argument("level"); s.add_argument("meshes"); s.add_argument("plane")
    s.add_argument("--box"); s.add_argument("--at"); s.add_argument("--extend", type=int, default=0)
    s = sub.add_parser("faces"); s.add_argument("level"); s.add_argument("meshes"); s.add_argument("box")
    s.add_argument("--slant", action="store_true")
    s = sub.add_parser("poly"); s.add_argument("level"); s.add_argument("mesh"); s.add_argument("polys", nargs="+")
    s = sub.add_parser("route"); s.add_argument("level"); s.add_argument("paths", nargs="+")
    s.add_argument("--ahead", type=float, default=1450); s.add_argument("--below", type=float, default=1255)
    s.add_argument("--every", type=int, default=6); s.add_argument("--speed", type=int, default=1200)
    s.add_argument("--start", type=int, default=300); s.add_argument("--hold", type=float, default=2)
    s.add_argument("--fast", type=int, default=6000)
    s = sub.add_parser("export"); s.add_argument("level"); s.add_argument("dir")
    s = sub.add_parser("pairs"); s.add_argument("before"); s.add_argument("after"); s.add_argument("out")
    s.add_argument("at", nargs="+", help="Crash x,z positions, or 'all' (every frame of the after run)")
    s.add_argument("--every", type=float, default=1500); s.add_argument("--rows", type=int, default=8)
    s = sub.add_parser("axes"); s.add_argument("level"); s.add_argument("--meshes")
    s.add_argument("--min-part", type=int, default=30); s.add_argument("--height", type=float, default=1500)
    s.add_argument("--radius", type=float, default=1750); s.add_argument("--twin-votes", type=int, default=20)
    s.add_argument("--width", type=float, default=3000, help="largest footprint (skips symmetric rooms/arches)")
    a = ap.parse_args()
    globals()["cmd_" + a.cmd](a)


if __name__ == "__main__":
    main()
