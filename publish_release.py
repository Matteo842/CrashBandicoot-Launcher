#!/usr/bin/env python3
"""Build Crash Bandicoot release binaries.

Usage (from repo root):
    python publish_release.py                  # interactive: Windows / Linux / Android
    python publish_release.py --platform windows
    python publish_release.py --platform linux
    python publish_release.py --platform android
    python publish_release.py --platform all
    python publish_release.py --rid linux-x64  # legacy RID flag (Windows/Linux only)

Requires: .NET 10 SDK. Android also needs the Android SDK + a release keystore
(created automatically on first Android publish into signing/, gitignored).
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
    "linux": ROOT / "publish-linux",
    "android": ROOT / "publish-android",
}

RID_FRAMEWORK = {
    "win-x64": "net10.0-windows",
    "linux-x64": "net10.0",
}

PLATFORM_RID = {
    "windows": "win-x64",
    "linux": "linux-x64",
}

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


def app_version() -> str:
    text = PROJECT.read_text(encoding="utf-8")
    match = re.search(r"<Version>([^<]+)</Version>", text)
    if not match:
        die(f"could not read <Version> from {PROJECT.name}")
    return match.group(1).strip()


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

    for junk in out_dir.glob("*.pdb"):
        junk.unlink(missing_ok=True)
    for junk in out_dir.glob("*.xml"):
        junk.unlink(missing_ok=True)

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
    else:
        print("Test:")
        print(f"  1. Copy/run:  {binary} --run /path/to/game.cue")
        print("  2. Needs OpenGL 4.3+ (OpenAL Soft is bundled)")
        print("  3. Expect next to the binary: settings.json, save/, game/, mods/")
        print("  Note: graphical launcher UI is Windows-only; Linux is CLI + game window.")
    return binary


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
        "-p:RuntimeIdentifiers=android-arm64",
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
    size_mb = dest.stat().st_size / (1024 * 1024)
    print()
    print("[publish] OK")
    print("  platform: android (arm64)")
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
            return ["windows", "linux", "android"]
        return [args.platform]
    if args.rid:
        if args.rid.startswith("win-"):
            return ["windows"]
        return ["linux"]
    if not sys.stdin.isatty():
        die("pass --platform windows|linux|android|all")

    print()
    print("Crash Bandicoot — publish release")
    print()
    print("  1) Windows   (win-x64, WinForms launcher)")
    print("  2) Linux     (linux-x64, CLI + game window)")
    print("  3) Android   (signed APK, arm64)")
    print("  4) All three")
    print()
    choice = input("Choice [1-4]: ").strip()
    mapping = {
        "1": ["windows"],
        "2": ["linux"],
        "3": ["android"],
        "4": ["windows", "linux", "android"],
        "windows": ["windows"],
        "linux": ["linux"],
        "android": ["android"],
        "all": ["windows", "linux", "android"],
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
        choices=("windows", "linux", "android", "all"),
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
    args = ap.parse_args()

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
