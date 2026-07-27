# Crash Bandicoot Launcher (unofficial)

> **Unofficial fan project.** Not affiliated with, endorsed by, or connected to Sony Interactive Entertainment, Activision, Naughty Dog, or any rights holder of *Crash Bandicoot*.  
> *Crash Bandicoot* and related names/marks belong to their respective owners.

This repository contains **tools and a launcher** that work with a copy of *Crash Bandicoot* you already own (PS1, NTSC-U, **SCUS-94900**). It does **not** include the game, disc images, or a ready-made game binary.

Built on [RecompOne](https://github.com/BlackLabelHQ/RecompOne) (static PS1 recompilation + runtime). Current release: **1.5.0** (native WinForms launcher on Windows; CLI on Linux). Still experimental — expect bugs.

---

## What this is

A small host application that:

1. Asks you for your own dumped disc (`.cue` + `.bin`).
2. On first run, **recompiles and compiles on your PC** into a `game/` folder next to the exe (it is not uploaded anywhere by this app).
3. Afterwards, starts from that prepared game folder.

Think of it as a **convenience shell around tools**, not a redistribution of *Crash Bandicoot*.

## What this is not

| Not this | Meaning |
|----------|---------|
| Not the game | We do not ship retail assets, ISOs, or `.bin`/`.cue` files |
| Not a piracy kit | You need a dump of a disc **you own** |
| Not an official port | No Sony / Activision involvement |
| Not “download and play without a disc” | The disc image is still required at runtime |
| Not finished | UI and compatibility are work in progress |

If someone offers you this project **bundled with a ROM/ISO**, that is not from this repository — don’t use it, don’t upload it here.

---

## Requirements

### Windows

- Windows 10/11 x64  
- A **legal** dump of *Crash Bandicoot* NTSC-U (`SCUS_949.00` / SCUS-94900) as `.cue` + matching `.bin` in the same folder  

### Linux

- x64 Linux with **OpenGL 4.3+** (Mesa / NVIDIA / AMD — a real GPU or working VM 3D accel)  
- **OpenAL Soft** system library (e.g. Debian/Ubuntu: `libopenal1`)  
- Same legal `.cue` + `.bin` dump as above  

On Linux there is **no graphical launcher menu** yet — use the CLI (`--prepare` / `--run`). The game opens in a standalone Silk/GLFW window. The painted WinForms UI remains Windows-only.

**VMware / weak GL:** if you get an instant `Segmentation fault` right after `launching … game.recomp.dll`, the VM likely cannot create an OpenGL 4.3 context. Check with `glxinfo -B`, enable 3D acceleration, or try software GL for a smoke test:

```bash
sudo apt install mesa-utils libopenal1
LIBGL_ALWAYS_SOFTWARE=1 ./CrashBandicoot --run /path/to/game.cue
```

We only aim to support that specific NTSC-U version for now.

---

## How to play

### Windows

1. Download the **release** `.exe` from this GitHub repo (not a random reupload).  
2. Run `CrashBandicoot.exe`.  
3. **Select disc** → choose your `.cue`.  
4. **Start Game**.  
   - First time: local prepare into `game\` next to the exe (can take a bit).  
   - Next times: reuses that prepared game folder.  

Optional (advanced):

```powershell
CrashBandicoot.exe --prepare "D:\path\to\your\game.cue"
CrashBandicoot.exe --run "D:\path\to\your\game.cue"
```

### Linux

```bash
./CrashBandicoot --prepare /path/to/your/game.cue
./CrashBandicoot --run /path/to/your/game.cue
```

If `settings.json` already has a valid `CdPath`, `--run` / `--smoke` can omit the cue path.

---

## Building from source (developers)

Needs [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build CrashBandicoot.Launcher -c Release
dotnet run --project CrashBandicoot.Launcher -c Release -f net10.0-windows
```

Linux / CLI framework:

```bash
dotnet build CrashBandicoot.Launcher -c Release -f net10.0
dotnet run --project CrashBandicoot.Launcher -c Release -f net10.0 -- --run /path/to/game.cue
```

Single-file release:

```powershell
python publish_release.py
python publish_release.py --rid linux-x64 --out publish-linux
```

Output (Windows): `publish-single\CrashBandicoot.exe`. Options: `python publish_release.py --out my-folder`, `python publish_release.py --clean`.

Equivalent raw commands:

```powershell
dotnet publish CrashBandicoot.Launcher -c Release -f net10.0-windows -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -o .\publish-single
```

```bash
dotnet publish CrashBandicoot.Launcher -c Release -f net10.0 -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ./publish-linux
```

The published binary extracts bundled deps at runtime; **user data** (`save/`, `game/`, `settings.json`) is always written next to the real exe (not in the temp extract folder).

Before you upload a release, check that you are shipping tools/UI only — **no** `.bin`/`.cue`, **no** `main.cs`, **no** `game.recomp.dll` / `game/` folder.

### Repo layout

| Path | Role |
|------|------|
| `CrashBandicoot.Launcher/` | Native WinForms launcher (Windows) + CLI + local recomp pipeline |
| `RecompOne.Runtime/` | PS1 HLE runtime (from RecompOne) |
| `RecompOne.Recompiler/` | Recompiler library (from RecompOne) |
| `examples/mods/` | Sample mods (`auto-spin`, `disc-overlay-stub`, `vram-transfer-stub`) — see [`docs/MODDING.md`](docs/MODDING.md) |
| `CrashBandicoot.Recompiled/` | Placeholder only (gitignored) — not used for consumer builds |

---

## Privacy / local data

All next to the binary (portable; gitignored):

- Config: `settings.json` (+ `interface.ini` for UI layout)  
- Saves: `save/carda.sav`, `save/cardb.sav`  
- Prepared game: `game/{fingerprint}/` (DLL + generated sources — persistent until you delete them)  
- Mods: `mods/` (enable/disable from the launcher **MODS** menu; see [`docs/MODDING.md`](docs/MODDING.md))  

This project does not include telemetry in the launcher path described here. Don’t commit those folders or dumps to git.

---

## Contributing / issues

Bug reports and PRs that improve **tools, runtime, UI, or docs** are welcome.  
Please **do not** open issues asking where to download the game, and **do not** attach or link disc images or generated game sources.

---

## Credits

- [RecompOne](https://github.com/BlackLabelHQ/RecompOne) — recompiler & runtime (MIT)  
- Inspired by the wider static-recompilation community (e.g. N64Recomp-style projects)  

## License

This repository’s original launcher code is under the **MIT License** (see [`LICENSE`](LICENSE)).  
RecompOne components retain their upstream MIT license and copyright notices.  

The MIT license applies to **our software**. It does **not** grant any rights to *Crash Bandicoot* itself.

---

## Legal

This project distributes **tools and runtime**, not Crash Bandicoot. Do not upload disc dumps or generated recompiled game code to public remotes. Trademark and copyright belong to their respective owners; this is an unofficial fan project.
