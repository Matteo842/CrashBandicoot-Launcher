"""NSF pages and entries (Crash 1; Crash 2 shares the page/entry container)."""
import struct

ALPHA = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_!"


def eidstr(e):
    return "".join(ALPHA[(e >> s) & 63] for s in (25, 19, 13, 7, 1))


def eidnum(s):
    v = 1
    for k, ch in zip((25, 19, 13, 7, 1), s):
        v |= ALPHA.index(ch) << k
    return v


def _decompress(buf, off):
    _, _, length, skip = struct.unpack_from("<HHII", buf, off)
    src = off + 12
    dst = bytearray()
    while len(dst) < length:
        p = buf[src]
        src += 1
        if p & 0x80:
            data = (p << 8) | buf[src]
            src += 1
            span = data & 7
            seek = (data >> 3) & 0xFFF
            span = 0x40 if span == 7 else span + 3
            for _ in range(span):
                dst.append(dst[-seek])
        else:
            dst += buf[src:src + p]
            src += p
    src += skip
    rem = 0x10000 - length
    dst += buf[src:src + rem]
    src += rem
    return bytes(dst[:0x10000]), src - off


def pages(nsf):
    off = 0
    while off < len(nsf):
        magic = struct.unpack_from("<H", nsf, off)[0]
        if magic == 0x1234:
            yield nsf[off:off + 0x10000]
            off += 0x10000
        elif magic == 0x1235:
            pg, used = _decompress(nsf, off)
            yield pg
            off = (off + used + 0x7FF) & ~0x7FF
        else:
            off += 0x800


def entries(pg):
    _, typ, _, count = struct.unpack_from("<HHII", pg, 0)
    if typ != 0:
        return
    for i in range(count):
        eo = struct.unpack_from("<I", pg, 16 + 4 * i)[0]
        m, eid, etype, icount = struct.unpack_from("<IIii", pg, eo)
        if m != 0x100FFFF:
            continue
        offs = struct.unpack_from("<%dI" % (icount + 1), pg, eo + 16)
        yield eid, etype, [pg[eo + offs[k]:eo + offs[k + 1]] for k in range(icount)]


def load(nsf):
    """{eid: (type, [items])} for every entry in a level file."""
    out = {}
    for pg in pages(nsf):
        for eid, etype, items in entries(pg):
            out[eid] = (etype, items)
    return out
