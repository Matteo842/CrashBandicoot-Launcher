# Crash Bandicoot Launcher (unofficial)

> **Unofficial fan project.** Not affiliated with, endorsed by, or connected to Sony Interactive Entertainment, Activision, Naughty Dog, or any rights holder of *Crash Bandicoot*.  
> *Crash Bandicoot* and related names/marks belong to their respective owners.

This repository contains **tools and a Windows launcher** that work with a copy of *Crash Bandicoot* you already own (PS1, NTSC-U, **SCUS-94900**). It does **not** include the game, disc images, or a ready-made game binary.

Built on [RecompOne](https://github.com/BlackLabelHQ/RecompOne) (static PS1 recompilation + runtime). Current release: **1.1.0** (memory card save/load). Still experimental — expect bugs.

---

## What this is

A small host application that:

1. Asks you for your own dumped disc (`.cue` + `.bin`).
2. On first run, **recompiles and compiles on your PC** into a `game\` folder next to the exe (it is not uploaded anywhere by this app).
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

- Windows 10/11 x64  
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (usually already installed with Edge)  
- A **legal** dump of *Crash Bandicoot* NTSC-U (`SCUS_949.00` / SCUS-94900) as `.cue` + matching `.bin` in the same folder  

We only aim to support that specific version for now.

---

## How to play

1. Download the **release** `.exe` from this GitHub repo (not a random reupload).  
2. Run `CrashBandicoot.exe`.  
3. **Select disc** → choose your `.cue`.  
4. **Start Game**.  
   - First time: local prepare into `game\` next to the exe (can take a bit).  
   - Next times: reuses that prepared game folder.  

Optional (advanced):

```powershell
CrashBandicoot.exe --prepare "D:\path\to\your\game.cue"
```

---

## Building from source (developers)

Needs [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build CrashBandicoot.Launcher -c Release
dotnet run --project CrashBandicoot.Launcher -c Release
```

Single-file release (one `CrashBandicoot.exe`, like a Python one-file build):

```powershell
python publish_release.py
```

Output: `publish-single\CrashBandicoot.exe`. Options: `python publish_release.py --out my-folder`, `python publish_release.py --clean`.

Equivalent raw command:

```powershell
dotnet publish CrashBandicoot.Launcher -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -o .\publish-single
```

The published `CrashBandicoot.exe` extracts bundled deps at runtime; **user data** (`save\`, `game\`, `settings.json`) is always written next to the real exe (not in the temp extract folder).

Before you upload a release, check that you are shipping tools/UI only — **no** `.bin`/`.cue`, **no** `main.cs`, **no** `game.recomp.dll` / `game\` folder.

### Repo layout

| Path | Role |
|------|------|
| `CrashBandicoot.Launcher/` | WebView2 launcher + local recomp pipeline + UI |
| `RecompOne.Runtime/` | PS1 HLE runtime (from RecompOne) |
| `RecompOne.Recompiler/` | Recompiler library (from RecompOne) |
| `CrashBandicoot.Recompiled/` | Placeholder only (gitignored) — not used for consumer builds |

---

## Privacy / local data

All next to `CrashBandicoot.exe` (portable; gitignored):

- Config: `settings.json` (+ `interface.ini` for UI layout)  
- Saves: `save\carda.sav`, `save\cardb.sav`  
- Prepared game: `game\{fingerprint}\` (DLL + generated sources — persistent until you delete them)  
- Mods: `mods\`  

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
