[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Latest Release](https://img.shields.io/github/v/release/Matteo842/CrashBandicoot-Launcher)](https://github.com/Matteo842/CrashBandicoot-Launcher/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/Matteo842/CrashBandicoot-Launcher/total.svg)](https://github.com/Matteo842/CrashBandicoot-Launcher/releases)
[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/V7V61GBYAX)

# Crash Bandicoot Launcher

*Unofficial fan project.*

**The 1996 classic, running natively on PC and Android. In widescreen, at 240 FPS, in 4K.**

This isn't an emulator. The original PS1 code is **statically recompiled** into a native program on your own machine, then given the upgrades the PS1 never could: a real 16:9 view of the world, high frame rates, high resolutions, mods and cheats.

---
<img width="1920" height="800" alt="Crash-Launcher1" src="https://github.com/user-attachments/assets/3682fe9c-0deb-466c-adbe-285a1fd7bbcc" />

---

## What you get

- **Native widescreen (16:9).** Nothing is stretched: the game draws more of the world on both sides of the screen, with the same camera. The HUD moves out to the new screen edges.
- **High frame rate.** 60, 120, 240 FPS or uncapped. Each object keeps its original speed, so the game plays as it did on a PS1, only smoother. You can switch frame rate in game with hotkeys.
- **High resolution.** Internal resolution from native up to **8x (4K)**, with integer scaling if you want perfectly sharp pixels.
- **Image options.** Bilinear, sharp bilinear and soft smooth texture filters, plus **dedither** and **dejitter** to remove the PS1 dithering and wobbly polygons. If you keep the dithering, it stays aligned to the PS1 pixels at any resolution. There's also an optional **CRT filter** (MattiasCRT) for the old TV look.
- **Quick save and quick load.** Save anywhere and load it back in a second: `F5` saves, `F8` loads, `F6`/`F7` switch slot. The same actions are in the pause menu.
- **Keep your lives.** Optional: the lives count is stored in your memory card save and comes back when you load it, as in Crash 2 and 3.
- **Box counter.** Optional on-screen count of the boxes you broke and the boxes the level's gem needs. It turns gray when a death loses the gem.
- **Gem hints.** Optional: on the map, a card shows what the selected level's gem needs (all boxes without dying, and the colored gem that opens its gem path). In levels it reminds you at the start, tells which gem an inactive gem platform needs, and warns when a death loses the gem.
- **Japanese box drop.** Optional: at the level end, only one box drops on Crash for all the boxes you missed, like the Japanese version. The counter still shows how many you missed.
- **Story intro on New Game.** Optional: New Game plays the story cutscene before the map, like Crash 2 and 3. Start skips it.
- **Level warp.** Jump to any level or bonus round from the Dev Menu, including **Stormy Ascent**, the level cut from the original game.
- **Cheats.** Infinite lives, level select (secret levels too), god mode, **fly mode** and all 26 gems on or off.
- **Mods.** Drop C# mods, texture packs and asset overlays into `mods/` and switch them on or off from the launcher or the in-game Dev Menu. See the [modding guide](docs/MODDING.md).
- **Controllers and keyboard.** Gamepad support (PlayStation Classic pad included), rebindable keys and optional **analog movement**: the left stick steers Crash in any direction, not only the 8 of the D-pad.
- **A launcher on every platform.** The same Crash-themed launcher on Windows, Linux and Android, usable with mouse, keyboard, touch or gamepad. In game, the pause menu, Dev Menu and Settings can match its style (or keep the classic gray look).
- **Plays from a `.chd` or `.cue` + `.bin` dump.** No extraction or converter needed.

Every extra is optional: each one has its own switch in Settings, so you can play it exactly like the original.

> It's still a work in progress. Widescreen is a big job: some levels still show the edges of the original scenery. Bugs exist, and we'd love to hear about them (see [Found a bug?](#found-a-bug)).

---

## Quick start

You need your own dump of **Crash Bandicoot (USA)**, serial **SCUS-94900**, as a `.chd` or as a `.cue` with its `.bin`. The game is not included here; read [the fine print](#the-fine-print).

### Windows

1. Download `CrashBandicoot.exe` from [**Releases**](https://github.com/Matteo842/CrashBandicoot-Launcher/releases/latest). On a Windows on ARM PC (for example a Snapdragon X laptop), download `CrashBandicoot-arm64.exe` instead.
2. Run it and click **Select disc** to choose your `.chd` or `.cue`.
3. Click **START GAME**.

The first launch builds the game on your PC, which takes a moment. Later launches start straight away. If the game window doesn't open, check the [System requirements](#system-requirements).

### Android

Install the `.apk` from [Releases](https://github.com/Matteo842/CrashBandicoot-Launcher/releases/latest) (arm64), select your disc, and play with touch controls or a gamepad.

### Linux

1. Download `CrashBandicoot-Linux` from [**Releases**](https://github.com/Matteo842/CrashBandicoot-Launcher/releases/latest). On a Raspberry Pi 4/5 or another 64-bit ARM computer, download `CrashBandicoot-Linux-arm64` instead.
2. Mark it as executable once, because browsers drop that permission: right-click it, **Properties → Permissions → Allow executing file as program** (or `chmod +x CrashBandicoot-Linux`).
3. Double-click it. The launcher opens.
4. Click **Select disc** to choose your `.chd` or `.cue`, then click **START GAME**. A disc in the same folder as the program is picked up on its own.

It's the same launcher as on Windows, with Settings, Controls, Mods and Cheats, usable with mouse, keyboard or gamepad. The first launch builds the game from your disc, which takes a minute or two. While you play, the launcher steps aside, and it comes back when you close the game. In **About** (the **i** button) you can add it to your applications menu, which also makes it show up in Steam's *Add a Non-Steam Game* list.

You need a GPU with **OpenGL 3.3+** or **OpenGL ES 3.0+** (Mesa, NVIDIA or AMD; OpenGL 4.3 is used when available) and a distribution with **glibc 2.34+** (see [System requirements](#system-requirements)). Audio (OpenAL Soft) is bundled, so there's nothing else to install. The launcher window uses X11, which Wayland desktops provide through XWayland. If it doesn't start, attach `logs/last-run.txt` (next to the program) to your [bug report](https://github.com/Matteo842/CrashBandicoot-Launcher/issues).

<details>
<summary><b>Linux: start the game without the launcher (Steam shortcuts, scripts)</b></summary>

```bash
./CrashBandicoot-Linux --run /path/to/your/game.chd
```

Without the path, `--run` uses the last disc you played.

</details>

<details>
<summary><b>Linux troubleshooting: instant <code>Segmentation fault</code> in a VM</b></summary>

If it crashes right after `launching … game.recomp.dll`, the VM probably can't create an OpenGL context (the game tries OpenGL 4.3, then 3.3, then OpenGL ES 3). Check with `glxinfo -B` and enable 3D acceleration. For a quick test, try software GL:

```bash
sudo apt install mesa-utils
LIBGL_ALWAYS_SOFTWARE=1 ./CrashBandicoot-Linux --run /path/to/game.cue
```

</details>

<details>
<summary><b>More about disc images (CHD, CLI options)</b></summary>

- Supported dump: a standalone CD image with a single `MODE2/2352` track, matching the NTSC-U disc. Other regions, multi-track layouts, DVD/GD-ROM images and CHDs that need a parent are not supported.
- To turn a `.cue` + `.bin` into a CHD, use [MAME's chdman](https://docs.mamedev.org/tools/chdman.html) with the CUE next to its BIN:

  ```text
  chdman createcd -i "Crash Bandicoot.cue" -o "Crash Bandicoot.chd"
  ```

- The disc image must still be there each time you play, because the game streams levels and audio from it.
- Command line options also work on Windows (`CrashBandicoot.exe --prepare …` / `--run …`). If `settings.json` already points to a valid disc, `--run` doesn't need the path.

</details>

---

## System requirements

| | Windows | Linux | Android |
|---|---|---|---|
| **System** | Windows 10 or 11 | glibc 2.34 or later: Ubuntu 22.04+, Debian 12+, Fedora 35+, RHEL / AlmaLinux / Rocky Linux 9+ | Android 8.0 (API 26) or later |
| **CPU** | 64-bit x86 (`x64`) or 64-bit ARM (`arm64`) | 64-bit x86 (`x86-64`) or 64-bit ARM (`arm64`) | 64-bit ARM (`arm64-v8a`) |
| **Graphics** | Vulkan 1.0, or OpenGL 4.3 (or 3.3) | OpenGL 4.3 (or 3.3, or OpenGL ES 3.0), or Vulkan 1.0 | OpenGL ES 3.2, or Vulkan 1.0 |
| **Also needed** | Visual C++ Redistributable 2015–2022 ([x64](https://aka.ms/vs/17/release/vc_redist.x64.exe), or [ARM64](https://aka.ms/vs/17/release/vc_redist.arm64.exe) for the arm64 build) | X11 or XWayland | Nothing |

- **No 32-bit builds** on any platform.
- **Vulkan** is the default renderer on Windows (Settings › Graphics › Renderer › Auto). It needs a Vulkan 1.0 driver with dual-source blending: NVIDIA GeForce 600 series, AMD Radeon HD 7000 series, Intel HD Graphics 500 (Skylake) or newer. Without it, or if Vulkan fails to start, the game opens with OpenGL on its own. On Linux, Auto still uses OpenGL; pick Vulkan in the Renderer list to use it. On Android, Vulkan is a switch in Settings (off by default).
- **OpenGL 4.3** works on most PC GPUs from 2012 on, for example NVIDIA GeForce 400 series, AMD Radeon HD 7000 series, Intel HD Graphics 4600 or newer. Renderer › **OpenGL** skips Vulkan. If the game window doesn't open, update your GPU driver first.
- **Older GPUs:** without OpenGL 4.3 the game falls back on its own to OpenGL 3.3 (for example Intel HD Graphics on Atom/Celeron chips), then to OpenGL ES 3.0 or newer. Settings › Graphics › **Renderer** picks one of them first. OpenGL ES also needs framebuffer fetch or `GL_EXT_blend_func_extended`; Mesa drivers have them.
- **Visual C++ Redistributable (Windows):** most PCs already have it, because many games install it. Without it, the game can't open its window or play sound.
- **Windows on ARM (Snapdragon X):** use `CrashBandicoot-arm64.exe`, which runs natively; `CrashBandicoot.exe` also starts there, but only through Windows' x64 emulation. OpenGL on these PCs comes from Microsoft's *OpenCL, OpenGL, and Vulkan Compatibility Pack* (Microsoft Store): install or update it if the game window doesn't open or draws wrongly. This build gets less testing than the x64 one.
- **Linux:** the launcher also needs fontconfig and ICU (`libicu`), which every desktop distribution includes. Older releases, such as Ubuntu 20.04 or Debian 11, have an older glibc and can't run the game.
- **Linux on ARM (Raspberry Pi 4/5):** the `arm64` build needs a 64-bit system, such as Raspberry Pi OS 64-bit (Bookworm or newer); the 32-bit Raspberry Pi OS can't run it. The Pi 4 and 5 GPU offers OpenGL ES 3.1, which the game uses through the fallback above. A Pi 3 or older has no OpenGL ES 3 and can't run the game. The first launch builds the game on the Pi itself, which takes much longer than on a PC. This build gets less testing than the x86-64 one.
- **Android GPU:** besides OpenGL ES 3.2, the driver must support framebuffer fetch or `GL_EXT_blend_func_extended`. Adreno (Snapdragon) and Mali drivers do. The APK also contains `x86_64` libraries (emulators, some Chromebooks), but that gets less testing.

---

## How it works

1. You point the launcher at **your** disc.
2. The [RecompOne](https://github.com/BlackLabelHQ/RecompOne) recompiler translates the game's MIPS code into C# and compiles it **on your PC**, in a `game/` folder next to the exe. Nothing is uploaded anywhere.
3. A runtime pretends to be enough of a PS1 (BIOS, GPU, sound, CD) for that code to run, and replaces the renderer with a modern one.

The extra features (widescreen, high frame rate, cheats) hook into the game's own logic. To do that we had to understand how Crash actually works inside, and for that we owe a lot to the people [credited below](#thanks).

Everything stays next to the exe: `settings.json`, saves in `save/`, the prepared game in `game/`, and your `mods/`. There's no telemetry.

---

## Found a bug?

[Open an issue](https://github.com/Matteo842/CrashBandicoot-Launcher/issues). A level name and a short description of what happened help a lot. Pull requests for the launcher, runtime, UI or docs are welcome.

Please don't ask where to download the game, and never attach disc images or generated game code.

---

## Thanks

This project stands on the shoulders of some amazing people.

<table>
<tr>
<td align="center" width="140"><a href="https://github.com/arciks1192-svg"><img src="https://github.com/arciks1192-svg.png" width="80" alt="arciks1192-svg"><br><b>@arciks1192-svg</b></a></td>
<td>Our full-time bug hunter. Most of the issues ever opened on this repo came from them: every broken platform, odd camera and missing wall was found, reported and retested. A huge part of the game playing as well as it does today is down to them. <b>Thank you!</b></td>
</tr>
<tr>
<td align="center"><a href="https://github.com/wurlyfox"><img src="https://github.com/wurlyfox.png" width="80" alt="wurlyfox"><br><b>@wurlyfox</b></a></td>
<td><b><a href="https://github.com/wurlyfox/c1">c1</a></b>, the decompilation of Crash Bandicoot's engine. <b>This project would never have been possible without it.</b> Most of the cool features exist because c1 told us what the game's memory and code actually mean. Widescreen, high frame rate, level select and direct level warps all rely on it.</td>
</tr>
<tr>
<td align="center"><a href="https://github.com/ManDude"><img src="https://github.com/ManDude.png" width="80" alt="ManDude"><br><b>@ManDude</b></a></td>
<td><b><a href="https://github.com/ManDude/goocdump">goocdump</a></b>, the decompiled <b>GOOL</b> scripts. GOOL is Naughty Dog's own language that drives every crate, enemy and platform in the game. Reading those scripts is how we make each object behave correctly at 60, 120 and 240 FPS.</td>
</tr>
<tr>
<td align="center"><a href="https://github.com/BlackLabelHQ/RecompOne"><img src="https://github.com/BlackLabelHQ.png" width="80" alt="BlackLabelHQ"><br><b>RecompOne</b></a></td>
<td>The static PS1 recompiler and runtime this whole thing is built on (MIT).</td>
</tr>
</table>

Also thanks to:

- [CHDSharp](https://github.com/purelogiccode/CHDSharp) by Peterson Fernandes ([@purelogiccode](https://github.com/purelogiccode)) and Gordon Jefferyes ([@gjefferyes](https://github.com/gjefferyes)), for CHD support. See the [third-party notices](THIRD-PARTY-NOTICES.txt).
- [CRT View](https://github.com/mattiasgustavsson/crtview) by Mattias Gustavsson, whose crtemu shader is the optional MattiasCRT filter (MIT).
- The static recompilation community (N64Recomp and friends), for showing it could be done.
- **Naughty Dog**, for making the game we're all still playing almost 30 years later.

If you enjoy the project, you can buy me a coffee on [Ko-fi](https://ko-fi.com/V7V61GBYAX)

---

<details>
<summary><b>Building from source (developers)</b></summary>

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build CrashBandicoot.Launcher -c Release
dotnet run --project CrashBandicoot.Launcher -c Release -f net10.0-windows
```

Linux (no arguments opens the launcher, `--run` starts the game directly):

```bash
dotnet build CrashBandicoot.Launcher -c Release -f net10.0
dotnet run --project CrashBandicoot.Launcher -c Release -f net10.0
dotnet run --project CrashBandicoot.Launcher -c Release -f net10.0 -- --run /path/to/game.cue
```

### Release builds

From the repo root, `python publish_release.py` asks which platform to build:

1. **Windows**: `publish-single\CrashBandicoot.exe`
2. **Linux**: `publish-linux\CrashBandicoot`
3. **Android**: `publish-android\CrashBandicoot-<version>.apk` (arm64, signed)
4. All five
5. **Linux arm64**: `publish-linux-arm64\CrashBandicoot` (needs the two libraries in `native/linux-arm64/`, see the [README](native/linux-arm64/README.md) there)
6. **Windows arm64**: `publish-win-arm64\CrashBandicoot.exe` (needs the two libraries in `native/win-arm64/`, see the [README](native/win-arm64/README.md) there)

You can also run it without prompts: `--platform windows|windows-arm64|linux|linux-arm64|android|all`. Other options are `--out my-folder` and `--clean`.

The first Android publish creates `signing/android-release.keystore`, which is gitignored. Back it up: if you lose the keystore, you can't update the same app.

Equivalent raw commands:

```powershell
dotnet publish CrashBandicoot.Launcher -c Release -f net10.0-windows -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -o .\publish-single
```

```bash
dotnet publish CrashBandicoot.Launcher -c Release -f net10.0 -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ./publish-linux
```

The single-file binary extracts its bundled dependencies at runtime. User data (`save/`, `game/`, `settings.json`) is always written next to the real exe.

Before uploading a release, make sure it contains only tools and UI: **no** `.bin`/`.cue`/`.chd`, **no** `main.cs`, **no** `game.recomp.dll` or `game/` folder.

### Repo layout

| Path | Role |
|------|------|
| `CrashBandicoot.Launcher/` | WinForms launcher (Windows), Avalonia launcher (Linux, `Linux/`), CLI, and the local recompile pipeline |
| `AndroidLauncher/`, `AndroidRuntimeHost/` | Android app |
| `RecompOne.Runtime/` | PS1 HLE runtime (from RecompOne), plus frame pacing, widescreen and cheats |
| `RecompOne.Recompiler/` | Recompiler library (from RecompOne) |
| `examples/mods/` | Sample mods, see [`docs/MODDING.md`](docs/MODDING.md) |
| `docs/` | Technical notes ([how we got it playable](docs/CRASH_BANDICOOT_RECOMP.md), [native widescreen](docs/NATIVE_WIDESCREEN.md), …) |

</details>

---

## The fine print

- **Unofficial fan project.** It is not affiliated with or endorsed by Sony Interactive Entertainment, Activision, Naughty Dog, or any rights holder. *Crash Bandicoot* and related names and marks belong to their respective owners.
- **The game is not included.** This repo has no ISOs, no disc images, no game assets and no recompiled game code. You need a dump of a disc **you own**. If you find this project bundled with a ROM or ISO, it didn't come from here.
- **License:** the original code in this repo is [MIT](LICENSE). RecompOne components keep their upstream MIT license and notices. The license covers *our software* only and grants no rights to *Crash Bandicoot* itself.
