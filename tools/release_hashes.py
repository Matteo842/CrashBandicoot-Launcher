"""Release archives and the program hashes in the release notes (issue #245).

The desktop releases ship as archives (RecompLauncher wants recomplauncher.json
at the top of a zip), so GitHub's own asset digests are the archives' and no
longer the programs'. The official-build check in the game reads every
"sha256:<64 hex>" in the release JSON, notes included, so the hashes of the
programs inside the archives are written into the notes between two markers.

    python tools/release_hashes.py pack <platform> <version> <program> <out dir>
        Packs one program and recomplauncher.json into its release archive.
    python tools/release_hashes.py notes <dist dir> <notes file>
        Rewrites the hash section of a notes file from the archives in dist.
        Run on the draft and again on Publish, so notes edited by hand before
        publishing get the section back.
"""

import hashlib
import pathlib
import sys
import tarfile
import zipfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
START = "<!-- program-sha256-start -->"
END = "<!-- program-sha256-end -->"

# platform -> (archive name after "CrashBandicoot-<version>-", name inside, label)
PACKS = {
    "windows": ("Windows-x64.zip", "CrashBandicoot.exe", "Windows x64"),
    "windows-arm64": ("Windows-arm64.zip", "CrashBandicoot-arm64.exe", "Windows arm64"),
    "linux": ("Linux-x64.tar.gz", "CrashBandicoot-Linux", "Linux x64"),
    "linux-arm64": ("Linux-arm64.tar.gz", "CrashBandicoot-Linux-arm64", "Linux arm64"),
    # macOS (#14): the program is a whole .app folder, signed ad hoc on the Mac runner.
    "macos": ("macOS-arm64.zip", "Crash Bandicoot Recompiled.app", "macOS Apple Silicon"),
    "macos-x64": ("macOS-x64.zip", "Crash Bandicoot Recompiled.app", "macOS Intel"),
}
PROGRAMS = {name for _, name, _ in PACKS.values() if not name.endswith(".app")}
# The file the official-build check hashes inside a .app.
MAC_PROGRAM = ".app/Contents/MacOS/CrashBandicoot"


def _is_program(name: str) -> bool:
    return name.rsplit("/", 1)[-1] in PROGRAMS or name.endswith(MAC_PROGRAM)


def _zip_tree(z: zipfile.ZipFile, folder: pathlib.Path, inner: str) -> None:
    """Zip a folder with its Unix modes, so the .app keeps its executable bit."""
    for path in sorted(folder.rglob("*")):
        if path.is_dir():
            continue
        name = f"{inner}/{path.relative_to(folder).as_posix()}"
        info = zipfile.ZipInfo.from_file(str(path), name)
        info.create_system = 3  # Unix: Archive Utility reads the mode from external_attr
        # Contents/MacOS holds the program: always executable, even when packed on Windows.
        executable = path.stat().st_mode & 0o111 or "/Contents/MacOS/" in f"/{name}"
        info.external_attr = (0o100755 if executable else 0o100644) << 16
        info.compress_type = zipfile.ZIP_DEFLATED
        with open(path, "rb") as fh:
            z.writestr(info, fh.read())


def check_ready_marker(manifest: pathlib.Path) -> None:
    """prepare.done must name the marker GameStore writes for the current PipelineVersion."""
    import json, re

    src = (ROOT / "CrashBandicoot.Launcher" / "Recomp" / "GameLoader.cs").read_text(encoding="utf-8")
    m = re.search(r'PipelineVersion\s*=\s*"([^"]+)"', src)
    if not m:
        sys.exit("PipelineVersion not found in GameLoader.cs")
    want = f"{{install}}/game/ready-p{m.group(1)}"
    done = json.loads(manifest.read_text(encoding="utf-8")).get("prepare", {}).get("done", [])
    if done != [want]:
        sys.exit(f"recomplauncher.json prepare.done is {done}, expected [{want!r}] (PipelineVersion changed?)")


def pack(platform: str, version: str, program: str, out_dir: str) -> None:
    suffix, inner, _ = PACKS[platform]
    check_ready_marker(ROOT / "recomplauncher.json")
    out = pathlib.Path(out_dir) / f"CrashBandicoot-{version}-{suffix}"
    out.parent.mkdir(parents=True, exist_ok=True)
    manifest = ROOT / "recomplauncher.json"
    if out.suffix == ".zip":
        with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
            if pathlib.Path(program).is_dir():
                _zip_tree(z, pathlib.Path(program), inner)
            else:
                z.write(program, inner)
            z.write(manifest, manifest.name)
    else:
        # tar keeps the executable bit, which browsers and zips drop on Linux.
        with tarfile.open(out, "w:gz") as t:
            for path, name, mode in ((program, inner, 0o755), (manifest, manifest.name, 0o644)):
                info = t.gettarinfo(str(path), name)
                info.mode, info.uid, info.gid, info.uname, info.gname = mode, 0, 0, "", ""
                with open(path, "rb") as fh:
                    t.addfile(info, fh)
    print(f"{out.name}: {out.stat().st_size} bytes")


def _sha256(fh) -> str:
    h = hashlib.sha256()
    for block in iter(lambda: fh.read(1 << 20), b""):
        h.update(block)
    return h.hexdigest()


def program_hashes(dist: pathlib.Path) -> list[tuple[str, str, str]]:
    found = []
    for archive in sorted(dist.iterdir()):
        if archive.name.endswith(".zip"):
            with zipfile.ZipFile(archive) as z:
                for name in z.namelist():
                    if _is_program(name):
                        with z.open(name) as fh:
                            found.append((archive.name, name, _sha256(fh)))
        elif archive.name.endswith(".tar.gz"):
            with tarfile.open(archive, "r:gz") as t:
                for m in t.getmembers():
                    if m.isfile() and _is_program(m.name):
                        found.append((archive.name, m.name, _sha256(t.extractfile(m))))
    return found


def notes(dist_dir: str, notes_file: str) -> None:
    dist = pathlib.Path(dist_dir)
    hashes = program_hashes(dist)
    if not hashes:
        sys.exit(f"no programs found in the archives in {dist}")
    lines = [START, "", "**Official programs** (SHA-256, checked by the game on start; keep this section):", ""]
    lines += [f"- `{inner}` in `{archive}`: `sha256:{digest}`" for archive, inner, digest in hashes]
    lines += ["", END]
    section = "\n".join(lines)

    path = pathlib.Path(notes_file)
    body = path.read_text(encoding="utf-8") if path.exists() else ""
    if START in body and END in body:
        before, rest = body.split(START, 1)
        body = before + section + rest.split(END, 1)[1]
    else:
        body = (body.rstrip() + "\n\n" if body.strip() else "") + section + "\n"
    path.write_text(body, encoding="utf-8")
    for archive, inner, digest in hashes:
        print(f"{archive} {inner} {digest}")


if __name__ == "__main__":
    if len(sys.argv) == 6 and sys.argv[1] == "pack":
        pack(*sys.argv[2:])
    elif len(sys.argv) == 4 and sys.argv[1] == "notes":
        notes(*sys.argv[2:])
    else:
        sys.exit(__doc__)
