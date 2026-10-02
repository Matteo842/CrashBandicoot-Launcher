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
- **High resolution.** Internal resolution from native up to **8x (4K)**.
- **Image options.** Bilinear, sharp bilinear and soft smooth texture filters, plus **dedither** and **dejitter** to remove the PS1 dithering and wobbly polygons.
- **Cheats.** Infinite lives, level select, god mode and **fly mode**.
- **Mods.** Drop C# mods, texture packs and asset overlays into `mods/` and switch them on or off from the launcher. See the [modding guide](docs/MODDING.md).
- **Controllers and keyboard.** Gamepad support and rebindable keys.
- **Windows, Linux and Android.**
- **Plays from a `.chd` or `.cue` + `.bin` dump.** No extraction or converter needed.

> It's still a work in progress. Widescreen is a big job: some levels still show the edges of the original scenery. Bugs exist, and we'd love to hear about them (see [Found a bug?](#found-a-bug)).

---

## Quick start

You need your own dump of **Crash Bandicoot (USA)**, serial **SCUS-94900**, as a `.chd` or as a `.cue` with its `.bin`. The game is not included here; read [the fine print](#the-fine-print).

### Windows

1. Download `CrashBandicoot.exe` from [**Releases**](https://github.com/Matteo842/CrashBandicoot-Launcher/releases/latest).
2. Run it and click **Select disc** to choose your `.chd` or `.cue`.
3. Click **START GAME**.

The first launch builds the game on your PC, which takes a moment. Later launches start straight away.

### Android

Install the `.apk` from [Releases](https://github.com/Matteo842/CrashBandicoot-Launcher/releases/latest) (arm64), select your disc, and play with touch controls or a gamepad.

### Linux

There's no graphical launcher on Linux yet, so use the command line:

```bash
./CrashBandicoot --prepare /path/to/your/game.cue
./CrashBandicoot --run /path/to/your/game.cue
```

The game opens in its own window. You need a GPU with **OpenGL 4.3+** (Mesa, NVIDIA or AMD). Audio (OpenAL Soft) is bundled, so there's nothing else to install.

<details>
<summary><b>Linux troubleshooting: instant <code>Segmentation fault</code> in a VM</b></summary>

If it crashes right after `launching … game.recomp.dll`, the VM probably can't create an OpenGL 4.3 context. Check with `glxinfo -B` and enable 3D acceleration. For a quick test, try software GL:

```bash
sudo apt install mesa-utils
LIBGL_ALWAYS_SOFTWARE=1 ./CrashBandicoot --run /path/to/game.cue
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

Linux / CLI:

```bash
dotnet build CrashBandicoot.Launcher -c Release -f net10.0
dotnet run --project CrashBandicoot.Launcher -c Release -f net10.0 -- --run /path/to/game.cue
```

### Release builds

From the repo root, `python publish_release.py` asks which platform to build:

1. **Windows**: `publish-single\CrashBandicoot.exe`
2. **Linux**: `publish-linux\CrashBandicoot`
3. **Android**: `publish-android\CrashBandicoot-<version>.apk` (arm64, signed)
4. All three

You can also run it without prompts: `--platform windows|linux|android|all`. Other options are `--out my-folder` and `--clean`.

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
| `CrashBandicoot.Launcher/` | WinForms launcher (Windows), CLI, and the local recompile pipeline |
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
