#!/usr/bin/env python3
"""Build a single-file CrashBandicoot.exe (self-contained win-x64).

Usage (from repo root):
    python publish_release.py
    python publish_release.py --out publish-single
    python publish_release.py --clean

Requires: .NET 10 SDK (`dotnet` on PATH).
Uses repo-root icon.png (256x256) as the exe ApplicationIcon.
"""

from __future__ import annotations

import argparse
import shutil
import struct
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
PROJECT = ROOT / "CrashBandicoot.Launcher" / "CrashBandicoot.Launcher.csproj"
ICON_PNG = ROOT / "icon.png"
ICON_ICO = ROOT / "CrashBandicoot.Launcher" / "app.ico"
DEFAULT_OUT = ROOT / "publish-single"

FORBIDDEN_GLOBS = (
    "*.bin",
    "*.cue",
    "*.iso",
    "game.recomp.dll",
    "main.cs",
)


def die(msg: str, code: int = 1) -> None:
    print(f"[publish] ERROR: {msg}", file=sys.stderr)
    raise SystemExit(code)


def require_dotnet() -> None:
    try:
        r = subprocess.run(
            ["dotnet", "--version"],
            capture_output=True,
            text=True,
            check=False,
        )
    except FileNotFoundError:
        die("dotnet not found on PATH. Install .NET 10 SDK.")
    if r.returncode != 0:
        die("dotnet --version failed.")
    print(f"[publish] SDK {r.stdout.strip()}")


def _png_size(png: bytes) -> tuple[int, int]:
    if len(png) < 24 or png[:8] != b"\x89PNG\r\n\x1a\n":
        die(f"not a PNG: {ICON_PNG}")
    if png[12:16] != b"IHDR":
        die(f"PNG missing IHDR: {ICON_PNG}")
    w, h = struct.unpack(">II", png[16:24])
    return int(w), int(h)


def png_to_ico(png_path: Path, ico_path: Path) -> None:
    """Write a Windows .ico that embeds the PNG (Vista+; fine for 256x256)."""
    png = png_path.read_bytes()
    w, h = _png_size(png)
    # ICO width/height bytes use 0 to mean 256
    wb = 0 if w >= 256 else w
    hb = 0 if h >= 256 else h
    offset = 6 + 16
    header = struct.pack("<HHH", 0, 1, 1)
    entry = struct.pack(
        "<BBBBHHII",
        wb,
        hb,
        0,
        0,
        1,
        32,
        len(png),
        offset,
    )
    ico_path.parent.mkdir(parents=True, exist_ok=True)
    ico_path.write_bytes(header + entry + png)
    print(f"[publish] icon {png_path.name} ({w}x{h}) -> {ico_path.relative_to(ROOT)}")


def ensure_app_icon() -> Path:
    if not ICON_PNG.is_file():
        die(f"missing {ICON_PNG} (expected 256x256 PNG in repo root)")
    if (
        not ICON_ICO.is_file()
        or ICON_PNG.stat().st_mtime > ICON_ICO.stat().st_mtime
    ):
        png_to_ico(ICON_PNG, ICON_ICO)
    else:
        print(f"[publish] using existing {ICON_ICO.relative_to(ROOT)}")
    return ICON_ICO


def publish(out_dir: Path) -> None:
    if not PROJECT.is_file():
        die(f"project not found: {PROJECT}")

    ico = ensure_app_icon()

    out_dir = out_dir.resolve()
    if out_dir.exists():
        print(f"[publish] cleaning {out_dir}")
        shutil.rmtree(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    cmd = [
        "dotnet",
        "publish",
        str(PROJECT),
        "-c",
        "Release",
        "-r",
        "win-x64",
        "--self-contained",
        "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:IncludeAllContentForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        f"-p:ApplicationIcon={ico}",
        "-o",
        str(out_dir),
    ]

    print("[publish] running:")
    print("  " + " ".join(cmd))
    print()

    r = subprocess.run(cmd, cwd=str(ROOT))
    if r.returncode != 0:
        die(f"dotnet publish failed (exit {r.returncode})")

    exe = out_dir / "CrashBandicoot.exe"
    if not exe.is_file():
        die(f"expected exe missing: {exe}")

    for junk in out_dir.glob("*.pdb"):
        junk.unlink(missing_ok=True)
    for junk in out_dir.glob("*.xml"):
        junk.unlink(missing_ok=True)

    bad: list[Path] = []
    for pattern in FORBIDDEN_GLOBS:
        bad.extend(out_dir.rglob(pattern))
    game_dir = out_dir / "game"
    if game_dir.is_dir():
        bad.append(game_dir)
    if bad:
        print("[publish] WARNING: unexpected files in output (do not ship dumps):")
        for p in bad[:20]:
            print(f"  - {p.relative_to(out_dir)}")
        if len(bad) > 20:
            print(f"  … and {len(bad) - 20} more")

    size_mb = exe.stat().st_size / (1024 * 1024)
    print()
    print("[publish] OK")
    print(f"  exe : {exe}")
    print(f"  icon: {ico}")
    print(f"  size: {size_mb:.1f} MB")
    print()
    print("Test:")
    print(f'  1. Copy/run:  "{exe}"')
    print("  2. Select a valid .cue (+ .bin beside it)")
    print("  3. Expect next to the exe: settings.json, save\\, game\\")


def main() -> None:
    ap = argparse.ArgumentParser(description="Publish CrashBandicoot as a single .exe")
    ap.add_argument(
        "--out",
        type=Path,
        default=DEFAULT_OUT,
        help=f"output folder (default: {DEFAULT_OUT.name})",
    )
    ap.add_argument(
        "--clean",
        action="store_true",
        help="only delete the output folder and exit",
    )
    args = ap.parse_args()

    out_dir = args.out if args.out.is_absolute() else (ROOT / args.out)

    if args.clean:
        if out_dir.exists():
            shutil.rmtree(out_dir)
            print(f"[publish] removed {out_dir}")
        else:
            print(f"[publish] nothing to clean ({out_dir})")
        return

    require_dotnet()
    publish(out_dir)


if __name__ == "__main__":
    main()
