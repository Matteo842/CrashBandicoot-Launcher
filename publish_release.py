#!/usr/bin/env python3
"""Build Crash Bandicoot release binaries.

Usage (from repo root):
    python publish_release.py                  # interactive: Windows / Linux / Android
    python publish_release.py --platform windows
    python publish_release.py --platform windows-arm64
    python publish_release.py --platform linux
    python publish_release.py --platform linux-arm64
    python publish_release.py --platform android
    python publish_release.py --platform macos      # osx-arm64 .app (run on a Mac)
    python publish_release.py --platform macos-x64  # osx-x64 .app (run on a Mac)
    python publish_release.py --platform all        # everything but macOS
    python publish_release.py --rid linux-x64  # legacy RID flag (Windows/Linux only)
    python publish_release.py --set-version 2.3  # write 2.3 into every version field

Requires: .NET 10 SDK. Android also needs the Android SDK + a release keystore
(created automatically on first Android publish into signing/, gitignored).
linux-arm64 also needs native/linux-arm64/libcimgui.so and libnfd.so, and
windows-arm64 needs native/win-arm64/nfd.dll and rabbitizer.dll, macos needs
native/osx-arm64/libnfd.dylib and macos-x64 needs native/osx-x64/librabbitizer.dylib,
all built by the "Release" GitHub workflow (see the README.md in each folder).
macOS builds must run on a Mac: the .app is signed (ad hoc) with codesign (#14).

Normal releases are built on GitHub: Actions > Release > Run workflow
(.github/workflows/release.yml), which uses --set-version and --platform.
"""

from __future__ import annotations

import argparse
import os
import re
import secrets
import shutil
import struct
import subprocess
import sys
import time
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
PROJECT = ROOT / "CrashBandicoot.Launcher" / "CrashBandicoot.Launcher.csproj"
ANDROID_PROJECT = ROOT / "AndroidRuntimeHost" / "AndroidRuntimeHost.csproj"
ICON_PNG = ROOT / "icon.png"
ICON_ICO = ROOT / "CrashBandicoot.Launcher" / "app.ico"
SIGNING_DIR = ROOT / "signing"
KEYSTORE = SIGNING_DIR / "android-release.keystore"
KEYSTORE_PROPS = SIGNING_DIR / "keystore.properties"
KEY_ALIAS = "upload"

DEFAULT_OUT = {
    "windows": ROOT / "publish-single",
    "windows-arm64": ROOT / "publish-win-arm64",
    "linux": ROOT / "publish-linux",
    "linux-arm64": ROOT / "publish-linux-arm64",
    "macos": ROOT / "publish-macos",
    "macos-x64": ROOT / "publish-macos-x64",
    "android": ROOT / "publish-android",
}

RID_FRAMEWORK = {
    "win-x64": "net10.0-windows",
    "win-arm64": "net10.0-windows",
    "linux-x64": "net10.0",
    "linux-arm64": "net10.0",
    "osx-arm64": "net10.0",
    "osx-x64": "net10.0",
}

PLATFORM_RID = {
    "windows": "win-x64",
    "windows-arm64": "win-arm64",
    "linux": "linux-x64",
    "linux-arm64": "linux-arm64",
    "macos": "osx-arm64",
    "macos-x64": "osx-x64",
}

# macOS is not in "all": it builds on a Mac only (codesign), see the Release workflow.
ALL_PLATFORMS = ["windows", "windows-arm64", "linux", "linux-arm64", "android"]
MAC_APP_NAME = "Crash Bandicoot Recompiled.app"
MAC_BUNDLE_ID = "io.github.matteo842.crashbandicoot-recompiled"
# .NET 10 does not support Big Sur (11) or older (#14); the build log prints the real floor (minos).
MAC_MIN_VERSION = "12.0"

# Every place that holds the app version (the "2.2" commit touched exactly
# these). Each pattern must match once: a moved or renamed field stops the
# release instead of shipping a build with an old version.
VERSION_FIELDS = (
    (PROJECT, r"(<Version>)[^<]*(</Version>)", "{v}"),
    (PROJECT, r"(<AssemblyVersion>)[^<]*(</AssemblyVersion>)", "{v4}"),
    (PROJECT, r"(<FileVersion>)[^<]*(</FileVersion>)", "{v4}"),
    (PROJECT, r"(<InformationalVersion>)[^<]*(</InformationalVersion>)", "{v}"),
    (ANDROID_PROJECT, r"(<ApplicationDisplayVersion>)[^<]*(</ApplicationDisplayVersion>)", "{v}"),
    (ROOT / "CrashBandicoot.Launcher" / "LauncherHost.cs", r'(public const string AppVersion = ")[^"]*(")', "{v}"),
    (ROOT / "CrashBandicoot.Launcher" / "Ui" / "NativeLauncherUi.cs", r'(string _version = "v)[^"]*(")', "{v}"),
    (ROOT / "CrashBandicoot.Launcher" / "Linux" / "LinuxGui.cs", r'(<summary>"v)[^"]*(" from the assembly version)', "{v}"),
    (ROOT / "RecompOne.Runtime" / "Host" / "Window" / "AppVersion.cs", r'(public const string Version = ")[^"]*(")', "{v}"),
)
ANDROID_VERSION_CODE = r"(<ApplicationVersion>)(\d+)(</ApplicationVersion>)"

# Natives that a package ships for x64 but not for arm64 (or, on macOS, for one
# of the two). The "Release" workflow (.github/workflows/release.yml) builds them
# into native/<rid>/; the launcher csproj packs them into that RID's single-file.
#   linux-arm64: ImGui.NET and NativeFileDialogSharp
#   win-arm64:   NativeFileDialogSharp and RabbitizerSharp
#   osx-arm64:   NativeFileDialogSharp (it ships osx-x64 only)
#   osx-x64:     RabbitizerSharp (it ships osx-arm64 only)
ARM64_NATIVES = {
    "linux-arm64": ("libcimgui.so", "libnfd.so"),
    "win-arm64": ("nfd.dll", "rabbitizer.dll"),
    "osx-arm64": ("libnfd.dylib",),
    "osx-x64": ("librabbitizer.dylib",),
}
EM_AARCH64 = 183
IMAGE_FILE_MACHINE_ARM64 = 0xAA64
MACHO_CPU = {"osx-arm64": 0x0100000C, "osx-x64": 0x01000007}

FORBIDDEN_GLOBS = (
    "*.bin",
    "*.cue",
    "*.chd",
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


def app_version() -> str:
    text = PROJECT.read_text(encoding="utf-8")
    match = re.search(r"<Version>([^<]+)</Version>", text)
    if not match:
        die(f"could not read <Version> from {PROJECT.name}")
    return match.group(1).strip()


def set_version(version: str) -> None:
    """Write version into every field in VERSION_FIELDS (the old manual "2.2" commit).

    The Android versionCode goes up by one whenever the display version changes,
    so a new APK always installs over the previous one.
    """
    if not re.fullmatch(r"\d+(\.\d+){1,3}", version):
        die(f"version {version!r} must look like 2.3 or 2.3.1")
    v4 = ".".join((version.split(".") + ["0", "0", "0"])[:4])

    # Bytes in, bytes out: keeps each file's BOM and line endings.
    texts: dict[Path, str] = {}
    for path, _, _ in VERSION_FIELDS:
        if path not in texts:
            if not path.is_file():
                die(f"version file missing: {path}")
            texts[path] = path.read_bytes().decode("utf-8")
    before = dict(texts)

    for path, pattern, value in VERSION_FIELDS:
        new = value.format(v=version, v4=v4)
        text, count = re.subn(pattern, lambda m: m.group(1) + new + m.group(2), texts[path])
        if count != 1:
            die(f"{path.relative_to(ROOT)}: expected 1 match for {pattern!r}, found {count}")
        texts[path] = text

    if texts[ANDROID_PROJECT] != before[ANDROID_PROJECT]:
        text, count = re.subn(
            ANDROID_VERSION_CODE,
            lambda m: m.group(1) + str(int(m.group(2)) + 1) + m.group(3),
            texts[ANDROID_PROJECT],
        )
        if count != 1:
            die(f"{ANDROID_PROJECT.name}: expected 1 <ApplicationVersion>, found {count}")
        texts[ANDROID_PROJECT] = text

    changed = [path for path in texts if texts[path] != before[path]]
    for path in changed:
        path.write_bytes(texts[path].encode("utf-8"))
        print(f"[publish] version {version} -> {path.relative_to(ROOT)}")
    if not changed:
        print(f"[publish] every version field already says {version}")


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


def expected_binary(out_dir: Path, rid: str) -> Path:
    if rid.startswith("win-"):
        return out_dir / "CrashBandicoot.exe"
    return out_dir / "CrashBandicoot"


def clean_out_dir(out_dir: Path) -> None:
    """Remove out_dir, or sidestep WinError 32 if Explorer/AV/VM has it locked."""
    if not out_dir.exists():
        return

    print(f"[publish] cleaning {out_dir}")
    last_err: BaseException | None = None
    for attempt in range(1, 6):
        try:
            shutil.rmtree(out_dir)
            return
        except OSError as e:
            last_err = e
            print(f"[publish] clean attempt {attempt}/5 failed: {e}")
            time.sleep(0.4 * attempt)

    stamp = time.strftime("%Y%m%d-%H%M%S")
    quarantine = out_dir.with_name(f"{out_dir.name}.old-{stamp}")
    try:
        out_dir.rename(quarantine)
        print(
            f"[publish] WARNING: could not delete {out_dir.name} "
            f"(file in use). Moved it to {quarantine.name} instead."
        )
        print(
            "[publish] Close Explorer tabs / terminals in that folder, "
            "stop copying to the VM, then delete the .old-* folder later."
        )
        return
    except OSError as e:
        die(
            f"cannot clean {out_dir}: still locked ({last_err}); "
            f"rename also failed ({e}). Close anything using that folder and retry."
        )


def copy_example_mods(out_dir: Path) -> None:
    """Ship sample mods under out/mods (and examples/mods for EnsureCreated seeding)."""
    src = ROOT / "examples" / "mods"
    if not src.is_dir():
        print("[publish] WARNING: examples/mods missing — skipping sample mods")
        return

    for dest_root in (out_dir / "mods", out_dir / "examples" / "mods"):
        dest_root.mkdir(parents=True, exist_ok=True)
        for child in src.iterdir():
            if child.name.startswith("."):
                continue
            dest = dest_root / child.name
            if dest.exists():
                continue
            if child.is_dir():
                shutil.copytree(child, dest)
            elif child.is_file():
                shutil.copy2(child, dest)
        print(f"[publish] sample mods -> {dest_root.relative_to(out_dir)}")


def warn_forbidden(out_dir: Path) -> None:
    bad: list[Path] = []
    for pattern in FORBIDDEN_GLOBS:
        bad.extend(out_dir.rglob(pattern))
    game_dir = out_dir / "game"
    if game_dir.is_dir():
        bad.append(game_dir)
    if not bad:
        return
    print("[publish] WARNING: unexpected files in output (do not ship dumps):")
    for p in bad[:20]:
        print(f"  - {p.relative_to(out_dir)}")
    if len(bad) > 20:
        print(f"  … and {len(bad) - 20} more")


def arm64_native_problem(path: Path, rid: str) -> str | None:
    """Why path is not an arm64 library for rid, or None when it is."""
    data = path.read_bytes()
    if rid.startswith("osx-"):
        if len(data) < 8 or data[:4] != b"\xcf\xfa\xed\xfe":
            return f"{path.name} is not a 64-bit macOS library"
        if struct.unpack_from("<I", data, 4)[0] != MACHO_CPU[rid]:
            return f"{path.name} is not built for {rid.split('-')[1]}"
        return None
    if rid.startswith("win-"):
        pe = struct.unpack_from("<I", data, 0x3C)[0] if len(data) >= 0x40 and data[:2] == b"MZ" else 0
        if not pe or data[pe:pe + 4] != b"PE\0\0" or len(data) < pe + 6:
            return f"{path.name} is not a Windows DLL"
        if struct.unpack_from("<H", data, pe + 4)[0] != IMAGE_FILE_MACHINE_ARM64:
            return f"{path.name} is not built for arm64"
        return None
    head = data[:20]
    if len(head) < 20 or head[:4] != b"\x7fELF" or head[4] != 2:
        return f"{path.name} is not a 64-bit Linux library"
    if struct.unpack("<H", head[18:20])[0] != EM_AARCH64:
        return f"{path.name} is not built for arm64 (aarch64)"
    return None


def check_arm64_natives(rid: str) -> None:
    """Stop early when native/<rid> lacks an arm64 build of a library."""
    names = ARM64_NATIVES[rid]
    native_dir = ROOT / "native" / rid
    problems: list[str] = []
    for name in names:
        path = native_dir / name
        if not path.is_file():
            problems.append(f"{name} is missing")
            continue
        problem = arm64_native_problem(path, rid)
        if problem:
            problems.append(problem)
    if not problems:
        print(f"[publish] {rid} natives OK ({', '.join(names)})")
        return
    die(
        f"{native_dir.relative_to(ROOT)}: " + "; ".join(problems) + ".\n"
        "  The GitHub 'Release' workflow builds them on its own. For a local build,\n"
        f"  copy {' and '.join(names)} from the "
        f"'{'macos' if rid.startswith('osx-') else rid}-natives' artifact\n"
        f"  of a Release run into {native_dir}\n"
        "  (or publish the other platforms one at a time)."
    )


def check_arm64_bundle(binary: Path, rid: str) -> None:
    """The bundle manifest stores file names uncompressed; make sure ours are in it."""
    names = ARM64_NATIVES[rid]
    data = binary.read_bytes()
    missing = [name for name in names if name.encode() not in data]
    if missing:
        die(
            f"{binary.name} does not contain {', '.join(missing)}. "
            f"Check the {rid} ItemGroup in CrashBandicoot.Launcher.csproj."
        )
    print(f"[publish] bundle contains {', '.join(names)}")


def publish_desktop(out_dir: Path, rid: str) -> Path:
    if not PROJECT.is_file():
        die(f"project not found: {PROJECT}")
    if rid not in RID_FRAMEWORK:
        die(f"unsupported RID {rid!r}; choose one of: {', '.join(RID_FRAMEWORK)}")

    framework = RID_FRAMEWORK[rid]
    is_windows = rid.startswith("win-")

    out_dir = out_dir.resolve()
    clean_out_dir(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    cmd = [
        "dotnet",
        "publish",
        str(PROJECT),
        "-c",
        "Release",
        "-f",
        framework,
        "-r",
        rid,
        "--self-contained",
        "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:IncludeAllContentForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-o",
        str(out_dir),
    ]

    ico: Path | None = None
    if is_windows:
        ico = ensure_app_icon()
        cmd.insert(-2, f"-p:ApplicationIcon={ico}")
    else:
        print("[publish] skipping Windows .ico (non-Windows RID)")

    print("[publish] running:")
    print("  " + " ".join(cmd))
    print()

    r = subprocess.run(cmd, cwd=str(ROOT))
    if r.returncode != 0:
        die(f"dotnet publish failed (exit {r.returncode})")

    binary = expected_binary(out_dir, rid)
    if not binary.is_file():
        die(f"expected binary missing: {binary}")
    if rid in ARM64_NATIVES:
        check_arm64_bundle(binary, rid)

    for junk in out_dir.glob("*.pdb"):
        junk.unlink(missing_ok=True)
    for junk in out_dir.glob("*.xml"):
        junk.unlink(missing_ok=True)

    if rid.startswith("osx-"):
        return make_mac_app(out_dir, binary, rid)

    copy_example_mods(out_dir)
    warn_forbidden(out_dir)

    size_mb = binary.stat().st_size / (1024 * 1024)
    print()
    print("[publish] OK")
    print(f"  rid : {rid}")
    print(f"  tfm : {framework}")
    print(f"  bin : {binary}")
    if ico is not None:
        print(f"  icon: {ico}")
    print(f"  size: {size_mb:.1f} MB")
    print()
    if is_windows:
        print("Test:")
        print(f'  1. Copy/run:  "{binary}"')
        print("  2. Select a valid .cue (+ .bin beside it)")
        print("  3. Expect next to the exe: settings.json, save\\, game\\, mods\\")
        if rid == "win-arm64":
            print("  4. arm64: Windows 11 on ARM (Snapdragon X); needs the Visual C++")
            print("     Redistributable for ARM64, and OpenGL from Microsoft's")
            print("     OpenCL, OpenGL, and Vulkan Compatibility Pack")
    else:
        print("Test:")
        print(f"  1. Copy/run:  {binary}   (opens the launcher; --run /path/to/game.cue skips it)")
        print("  2. Needs OpenGL 4.3+ (OpenAL Soft is bundled); the launcher window needs X11/XWayland")
        print("  3. Expect next to the binary: settings.json, save/, game/, mods/")
        if rid == "linux-arm64":
            print("  4. arm64: needs a 64-bit OS, e.g. Raspberry Pi OS 64-bit Bookworm or newer")
    return binary


def mac_icon(resources: Path) -> bool:
    """AppIcon.icns from icon.png with sips + iconutil (macOS tools)."""
    if not ICON_PNG.is_file() or not shutil.which("sips") or not shutil.which("iconutil"):
        print("[publish] WARNING: no sips/iconutil, the .app has no icon")
        return False
    iconset = resources.parent / "AppIcon.iconset"
    iconset.mkdir(exist_ok=True)
    for size in (16, 32, 128, 256):
        for scale in (1, 2):
            px = size * scale
            if px > 256:  # icon.png is 256x256: do not upscale
                continue
            name = f"icon_{size}x{size}{'@2x' if scale == 2 else ''}.png"
            subprocess.run(["sips", "-z", str(px), str(px), str(ICON_PNG), "--out", str(iconset / name)],
                           check=True, capture_output=True)
    r = subprocess.run(["iconutil", "-c", "icns", str(iconset), "-o", str(resources / "AppIcon.icns")])
    shutil.rmtree(iconset, ignore_errors=True)
    return r.returncode == 0


def make_mac_app(out_dir: Path, binary: Path, rid: str) -> Path:
    """Wrap the single-file binary in a .app and sign it ad hoc (#14).

    Apple Silicon refuses to run unsigned code, and an ad hoc signature
    (codesign -s -) needs no Apple account. Users still see the Gatekeeper
    "unidentified developer" prompt the first time (right-click > Open).
    """
    version = app_version()
    app = out_dir / MAC_APP_NAME
    macos_dir = app / "Contents" / "MacOS"
    resources = app / "Contents" / "Resources"
    macos_dir.mkdir(parents=True, exist_ok=True)
    resources.mkdir(parents=True, exist_ok=True)
    exe = macos_dir / "CrashBandicoot"
    shutil.move(str(binary), str(exe))
    exe.chmod(0o755)
    has_icon = mac_icon(resources)
    icon_key = "    <key>CFBundleIconFile</key><string>AppIcon</string>\n" if has_icon else ""
    (app / "Contents" / "Info.plist").write_text(
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">\n'
        '<plist version="1.0">\n<dict>\n'
        "    <key>CFBundleName</key><string>Crash Bandicoot Recompiled</string>\n"
        "    <key>CFBundleDisplayName</key><string>Crash Bandicoot: Recompiled</string>\n"
        f"    <key>CFBundleIdentifier</key><string>{MAC_BUNDLE_ID}</string>\n"
        "    <key>CFBundleExecutable</key><string>CrashBandicoot</string>\n"
        "    <key>CFBundlePackageType</key><string>APPL</string>\n"
        f"    <key>CFBundleShortVersionString</key><string>{version}</string>\n"
        f"    <key>CFBundleVersion</key><string>{version}</string>\n"
        f"    <key>LSMinimumSystemVersion</key><string>{MAC_MIN_VERSION}</string>\n"
        "    <key>LSApplicationCategoryType</key><string>public.app-category.games</string>\n"
        "    <key>NSHighResolutionCapable</key><true/>\n"
        f"{icon_key}"
        "</dict>\n</plist>\n",
        encoding="utf-8",
    )
    # Leftovers of the plain publish (nothing else should be there).
    for extra in out_dir.iterdir():
        if extra != app:
            if extra.is_dir():
                shutil.rmtree(extra)
            else:
                extra.unlink()

    if shutil.which("codesign"):
        if subprocess.run(["codesign", "--force", "--deep", "--sign", "-", str(app)]).returncode != 0:
            die("codesign failed")
        subprocess.run(["codesign", "--verify", "--verbose=2", str(app)], check=True)
    elif rid == "osx-arm64":
        die("codesign not found: build macOS releases on a Mac (Apple Silicon runs signed code only)")
    else:
        print("[publish] WARNING: codesign not found, the .app is unsigned")
    if shutil.which("otool"):
        load = subprocess.run(["otool", "-l", str(exe)], capture_output=True, text=True).stdout
        m = re.search(r"minos (\S+)", load)
        print(f"[publish] needs macOS {m.group(1) if m else '?'} or newer (binary minos)")

    size_mb = exe.stat().st_size / (1024 * 1024)
    print()
    print("[publish] OK")
    print(f"  rid : {rid}")
    print(f"  app : {app}")
    print(f"  size: {size_mb:.1f} MB")
    print()
    print("Test:")
    print(f'  1. open "{app}"   (first time from a download: right-click > Open)')
    print("  2. Needs OpenGL 4.1 (every Mac since 2012); the launcher opens first")
    print("  3. User data: ~/Library/Application Support/CrashBandicoot-Recompiled")
    return app


def find_java_home() -> Path:
    env = os.environ.get("JAVA_HOME")
    if env:
        home = Path(env)
        if (home / "bin" / "java.exe").is_file() or (home / "bin" / "java").is_file():
            return home
    studio_jbr = Path(r"D:\Android\Android Studio\jbr")
    if (studio_jbr / "bin" / "java.exe").is_file():
        return studio_jbr
    die(
        "JAVA_HOME is not set and Android Studio JBR was not found at "
        r"D:\Android\Android Studio\jbr"
    )


def find_android_sdk() -> Path:
    for key in ("ANDROID_HOME", "ANDROID_SDK_ROOT"):
        env = os.environ.get(key)
        if env and Path(env).is_dir():
            return Path(env)
    local = Path(os.environ.get("LOCALAPPDATA", "")) / "Android" / "Sdk"
    if local.is_dir():
        return local
    die("Android SDK not found. Set ANDROID_HOME or install the SDK via Android Studio.")


def find_keytool(java_home: Path) -> Path:
    for name in ("keytool.exe", "keytool"):
        candidate = java_home / "bin" / name
        if candidate.is_file():
            return candidate
    die(f"keytool not found under {java_home}")


def load_keystore_props() -> dict[str, str]:
    if not KEYSTORE_PROPS.is_file():
        die(f"missing {KEYSTORE_PROPS} — run an Android publish once to create it")
    props: dict[str, str] = {}
    for raw in KEYSTORE_PROPS.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        props[key.strip()] = value.strip()
    for required in ("storeFile", "storePassword", "keyAlias", "keyPassword"):
        if required not in props:
            die(f"{KEYSTORE_PROPS.name} is missing {required}=")
    return props


def write_keystore_props(store_file: Path, password: str, alias: str) -> None:
    SIGNING_DIR.mkdir(parents=True, exist_ok=True)
    KEYSTORE_PROPS.write_text(
        "\n".join(
            [
                "# Gitignored. Back this up with the .keystore — losing them means",
                "# you can never update the same Android app identity.",
                f"storeFile={store_file}",
                f"storePassword={password}",
                f"keyAlias={alias}",
                f"keyPassword={password}",
                "",
            ]
        ),
        encoding="utf-8",
    )


def ensure_android_keystore() -> dict[str, str]:
    if KEYSTORE.is_file() and KEYSTORE_PROPS.is_file():
        props = load_keystore_props()
        print(f"[publish] using existing keystore {KEYSTORE.relative_to(ROOT)}")
        return props

    if KEYSTORE.is_file() and not KEYSTORE_PROPS.is_file():
        die(
            f"{KEYSTORE.name} exists but {KEYSTORE_PROPS.name} is missing. "
            "Restore the properties file from backup, or delete the keystore to create a new one."
        )

    java_home = find_java_home()
    keytool = find_keytool(java_home)
    password = secrets.token_hex(16)
    SIGNING_DIR.mkdir(parents=True, exist_ok=True)
    cmd = [
        str(keytool),
        "-genkeypair",
        "-noprompt",
        "-storetype",
        "PKCS12",
        "-alias",
        KEY_ALIAS,
        "-keyalg",
        "RSA",
        "-keysize",
        "2048",
        "-validity",
        "10000",
        "-keystore",
        str(KEYSTORE),
        "-storepass",
        password,
        "-keypass",
        password,
        "-dname",
        "CN=Crash Bandicoot Recompiled, OU=Unofficial fan project, O=CrashBandicoot-Launcher, C=IT",
    ]
    print(f"[publish] creating Android upload keystore ({KEYSTORE.relative_to(ROOT)})")
    r = subprocess.run(cmd, cwd=str(ROOT), capture_output=True, text=True)
    if r.returncode != 0:
        die(f"keytool failed:\n{r.stderr or r.stdout}")
    write_keystore_props(KEYSTORE.resolve(), password, KEY_ALIAS)
    print(f"[publish] wrote {KEYSTORE_PROPS.relative_to(ROOT)} (gitignored)")
    print("[publish] BACK UP signing/android-release.keystore and signing/keystore.properties.")
    print("[publish] If you lose them, sideload/Play updates with this app id will fail.")
    return load_keystore_props()


def find_signed_apk(search_roots: list[Path]) -> Path | None:
    matches: list[Path] = []
    for root in search_roots:
        if not root.exists():
            continue
        matches.extend(root.rglob("*-Signed.apk"))
        matches.extend(root.rglob("*.apk"))
    signed = [p for p in matches if p.is_file() and p.name.endswith("-Signed.apk")]
    if signed:
        return max(signed, key=lambda p: p.stat().st_mtime)
    apks = [p for p in matches if p.is_file() and p.suffix.lower() == ".apk"]
    if apks:
        return max(apks, key=lambda p: p.stat().st_mtime)
    return None


# Release libmonodroid.so dlopens libxamarin-app.so and requires these symbols.
# A Debug APK can boot without them (different libmonodroid). Shipping without
# them is the instant-open crash from GitHub issue #17.
_RELEASE_XAMARIN_APP_SYMBOLS = (
    b"compressed_assembly_count",
    b"compressed_assembly_descriptors",
    b"uncompressed_assemblies_data_size",
)


def verify_release_apk_native_symbols(apk: Path) -> None:
    with zipfile.ZipFile(apk) as z:
        xa_names = [n for n in z.namelist() if n.endswith("libxamarin-app.so")]
        if not xa_names:
            die(f"{apk.name} is missing libxamarin-app.so")
        data = z.read(xa_names[0])
    missing = [s.decode() for s in _RELEASE_XAMARIN_APP_SYMBOLS if s not in data]
    if missing:
        die(
            f"{apk.name} would crash on start: libxamarin-app.so is missing "
            + ", ".join(missing)
            + ". Enable AndroidEnableAssemblyCompression and rebuild."
        )
    # LLVM marshal methods export TypeManager.activate and skip RegisterNatives.
    # If MainActivity's JNI stub is missing too, open crashes with n_onCreate.
    has_llvm_typemanager = b"Java_mono_android_TypeManager_n_1activate" in data
    has_oncreate_stub = b"MainActivity_n_1onCreate" in data
    if has_llvm_typemanager and not has_oncreate_stub:
        die(
            f"{apk.name} would crash on start: LLVM marshal methods are on but "
            "MainActivity JNI stubs are missing. Set AndroidEnableMarshalMethods=false "
            "and rebuild."
        )
    print("[publish] native symbol check OK (libxamarin-app.so)")


def publish_android(out_dir: Path) -> Path:
    if not ANDROID_PROJECT.is_file():
        die(f"project not found: {ANDROID_PROJECT}")

    props = ensure_android_keystore()
    sdk = find_android_sdk()
    java_home = find_java_home()
    version = app_version()

    out_dir = out_dir.resolve()
    clean_out_dir(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    store = Path(props["storeFile"])
    cmd = [
        "dotnet",
        "publish",
        str(ANDROID_PROJECT),
        "-c",
        "Release",
        "-f",
        "net10.0-android",
        f"-p:AndroidSdkDirectory={sdk}",
        f"-p:JavaSdkDirectory={java_home}",
        "-p:AndroidPackageFormat=apk",
        "-p:AndroidEnableAssemblyCompression=true",
        "-p:AndroidEnableMarshalMethods=false",
        "-p:RunAOTCompilation=false",
        "-p:AndroidEnableProfiledAot=false",
        "-p:AndroidKeyStore=true",
        f"-p:AndroidSigningKeyStore={store}",
        f"-p:AndroidSigningKeyAlias={props['keyAlias']}",
        f"-p:AndroidSigningStorePass={props['storePassword']}",
        f"-p:AndroidSigningKeyPass={props['keyPassword']}",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-o",
        str(out_dir),
    ]

    print("[publish] Android SDK:", sdk)
    print("[publish] Java home :", java_home)
    print("[publish] running: dotnet publish AndroidRuntimeHost -c Release (signed APK, passwords omitted)")
    print()

    r = subprocess.run(cmd, cwd=str(ROOT))
    if r.returncode != 0:
        die(f"dotnet publish (Android) failed (exit {r.returncode})")

    apk = find_signed_apk(
        [
            out_dir,
            ROOT / "AndroidRuntimeHost" / "bin" / "Release" / "net10.0-android",
        ]
    )
    if apk is None:
        die("signed APK not found after publish")

    dest = out_dir / f"CrashBandicoot-{version}.apk"
    if apk.resolve() != dest.resolve():
        shutil.copy2(apk, dest)

    verify_release_apk_native_symbols(dest)

    for leftover in out_dir.iterdir():
        if leftover.resolve() == dest.resolve():
            continue
        if leftover.is_file():
            leftover.unlink()
        elif leftover.is_dir():
            shutil.rmtree(leftover)

    size_mb = dest.stat().st_size / (1024 * 1024)
    print()
    print("[publish] OK")
    print("  platform: android")
    print(f"  apk     : {dest}")
    print(f"  size    : {size_mb:.1f} MB")
    print()
    print("Test:")
    print(f'  adb install -r "{dest}"')
    print("  First install over a debug APK will fail (different signature) — uninstall first.")
    return dest


def pick_platforms(args: argparse.Namespace) -> list[str]:
    if args.platform:
        if args.platform == "all":
            return list(ALL_PLATFORMS)
        return [args.platform]
    if args.rid:
        if args.rid == "win-arm64":
            return ["windows-arm64"]
        if args.rid.startswith("win-"):
            return ["windows"]
        if args.rid == "linux-arm64":
            return ["linux-arm64"]
        if args.rid.startswith("osx-"):
            return ["macos" if args.rid == "osx-arm64" else "macos-x64"]
        return ["linux"]
    if not sys.stdin.isatty():
        die("pass --platform windows|windows-arm64|linux|linux-arm64|macos|macos-x64|android|all")

    print()
    print("Crash Bandicoot — publish release")
    print()
    print("  1) Windows      (win-x64, WinForms launcher)")
    print("  2) Linux        (linux-x64, CLI + game window)")
    print("  3) Android      (signed APK, arm64)")
    print("  4) All five     (Windows, Windows arm64, Linux, Linux arm64, Android)")
    print("  5) Linux arm64  (linux-arm64, Raspberry Pi 4/5; needs native/linux-arm64)")
    print("  6) Windows arm64 (win-arm64, Snapdragon X; needs native/win-arm64)")
    print("  7) macOS        (osx-arm64 .app, Apple Silicon; on a Mac, needs native/osx-arm64)")
    print("  8) macOS x64    (osx-x64 .app, Intel Macs; on a Mac, needs native/osx-x64)")
    print()
    choice = input("Choice [1-8]: ").strip()
    mapping = {
        "1": ["windows"],
        "2": ["linux"],
        "3": ["android"],
        "4": list(ALL_PLATFORMS),
        "5": ["linux-arm64"],
        "6": ["windows-arm64"],
        "7": ["macos"],
        "8": ["macos-x64"],
        "macos": ["macos"],
        "macos-x64": ["macos-x64"],
        "windows": ["windows"],
        "windows-arm64": ["windows-arm64"],
        "linux": ["linux"],
        "linux-arm64": ["linux-arm64"],
        "android": ["android"],
        "all": list(ALL_PLATFORMS),
    }
    picked = mapping.get(choice.lower())
    if not picked:
        die(f"invalid choice {choice!r}")
    return picked


def out_dir_for(platform: str, override: Path | None) -> Path:
    if override is not None:
        return override if override.is_absolute() else (ROOT / override)
    return DEFAULT_OUT[platform]


def main() -> None:
    ap = argparse.ArgumentParser(
        description="Publish CrashBandicoot for Windows, Linux, and/or Android"
    )
    ap.add_argument(
        "--platform",
        choices=("windows", "windows-arm64", "linux", "linux-arm64", "macos", "macos-x64", "android", "all"),
        help="target platform (default: interactive menu)",
    )
    ap.add_argument(
        "--out",
        type=Path,
        default=None,
        help="output folder (default depends on platform)",
    )
    ap.add_argument(
        "--rid",
        choices=sorted(RID_FRAMEWORK),
        help="legacy RID flag for Windows/Linux (default: menu)",
    )
    ap.add_argument(
        "--clean",
        action="store_true",
        help="only delete the output folder(s) and exit",
    )
    ap.add_argument(
        "--init-keystore",
        action="store_true",
        help="create the Android upload keystore if missing, then exit",
    )
    ap.add_argument(
        "--set-version",
        metavar="VERSION",
        help="write VERSION (e.g. 2.3) into every version field, then exit",
    )
    args = ap.parse_args()

    if args.set_version:
        set_version(args.set_version)
        return

    if args.init_keystore:
        require_dotnet()
        ensure_android_keystore()
        return

    if args.clean:
        targets = []
        if args.out is not None:
            targets = [args.out if args.out.is_absolute() else (ROOT / args.out)]
        else:
            targets = list(DEFAULT_OUT.values())
        for out_dir in targets:
            if out_dir.exists():
                shutil.rmtree(out_dir)
                print(f"[publish] removed {out_dir}")
            else:
                print(f"[publish] nothing to clean ({out_dir})")
        return

    platforms = pick_platforms(args)
    if args.out is not None and len(platforms) > 1:
        die("--out cannot be used with multiple platforms; publish one at a time")
    for platform in platforms:
        if PLATFORM_RID.get(platform) in ARM64_NATIVES:
            check_arm64_natives(PLATFORM_RID[platform])

    require_dotnet()
    print(f"[publish] version {app_version()}")
    produced: list[Path] = []
    for platform in platforms:
        print()
        print(f"[publish] === {platform} ===")
        out_dir = out_dir_for(platform, args.out)
        if platform == "android":
            produced.append(publish_android(out_dir))
        else:
            produced.append(publish_desktop(out_dir, PLATFORM_RID[platform]))

    print()
    print("[publish] done:")
    for path in produced:
        print(f"  - {path}")


if __name__ == "__main__":
    main()
