"""ISO9660 access to the player's own disc image (.bin 2352-byte raw sectors or .iso 2048).

Nothing from the disc is stored in the repository: every tool reads it at run time.
Set WS_DISC to point at another image.
"""
import os
import struct

DEFAULT_DISC = r"D:/GitHub/RecompOne/Crash Bandicoot.bin"


class Disc:
    def __init__(self, path=None):
        self.path = path or os.environ.get("WS_DISC", DEFAULT_DISC)
        self.f = open(self.path, "rb")
        size = os.path.getsize(self.path)
        self.raw = size % 2352 == 0 and size % 2048 != 0 or self._probe_raw()
        pvd = self.sector(16)
        if pvd[1:6] != b"CD001":
            raise ValueError(f"{self.path}: no ISO9660 volume descriptor")
        root = pvd[156:156 + 34]
        self.root = (struct.unpack_from("<I", root, 2)[0], struct.unpack_from("<I", root, 10)[0])
        self._files = None

    def _probe_raw(self):
        self.f.seek(16 * 2352 + 24)
        return self.f.read(6)[1:6] == b"CD001"

    def sector(self, lba):
        if self.raw:
            self.f.seek(lba * 2352)
            return self.f.read(2352)[24:24 + 2048]
        self.f.seek(lba * 2048)
        return self.f.read(2048)

    def extent(self, lba, size):
        out = bytearray()
        for i in range((size + 2047) // 2048):
            out += self.sector(lba + i)
        return bytes(out[:size])

    def _dir(self, lba, size):
        data = self.extent(lba, size)
        i = 0
        while i < len(data):
            n = data[i]
            if n == 0:
                i = (i // 2048 + 1) * 2048
                continue
            rec = data[i:i + n]
            name = rec[33:33 + rec[32]].decode("latin1")
            yield name, struct.unpack_from("<I", rec, 2)[0], struct.unpack_from("<I", rec, 10)[0], rec[25]
            i += n

    def files(self):
        """{'/S0/S0000006.NSF': (lba, size), ...} with ';1' version suffixes removed."""
        if self._files is None:
            self._files = {}

            def walk(lba, size, path):
                for name, elba, esize, flags in self._dir(lba, size):
                    if name in ("\x00", "\x01"):
                        continue
                    full = path + "/" + name.split(";")[0]
                    if flags & 2:
                        walk(elba, esize, full)
                    else:
                        self._files[full.upper()] = (elba, esize)

            walk(*self.root, "")
        return self._files

    def find(self, name):
        name = name.upper()
        for full, ext in self.files().items():
            if full.endswith("/" + name):
                return full, ext
        raise FileNotFoundError(name)

    def read(self, name):
        full, (lba, size) = self.find(name)
        return self.extent(lba, size)


_disc = None


def disc():
    global _disc
    if _disc is None:
        _disc = Disc()
    return _disc
