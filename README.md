# Crash Bandicoot: Recompiled

Consumer launcher for **Crash Bandicoot** (SCUS-94900, NTSC-U) on [RecompOne](https://github.com/).

You supply a legal disc dump. The launcher prepares the game **on your PC** the first time, then starts instantly — no CLI, no copying recompiled sources by hand.

The menu is a **Crash-themed HTML UI** hosted in WebView2 (Wumpa orange / jungle — not a Banjo clone).

## Players

1. Download a release (or build from source — see below).
2. Run `CrashBandicoot.exe`.
3. **Select Disc** and pick your `Crash Bandicoot.cue` (keep the matching `.bin` beside it).
4. Click **Start Game**.
   - First launch: the app recompiles and compiles locally (progress screen). Output stays in `%LocalAppData%\CrashBandicoot-Launcher\recomp\` — never uploaded.
   - Later launches: start is instant from that cache.
5. Use **Controls**, **Settings**, and **Mods** from the menu as needed.

You must own a legal copy of the game. This project does **not** distribute disc images or prebuilt game code.

### Headless prepare (optional)

```powershell
CrashBandicoot.exe --prepare "D:\path\to\Crash Bandicoot.cue"
```

## What ships in this repo / release

| Included | Not included |
|----------|----------------|
| `RecompOne.Runtime` (PS1 HLE host) | Retail `.bin` / `.cue` |
| `RecompOne.Recompiler` + `CrashBandicoot.json` | Generated `main.cs` / game DLL |
| Launcher UI + Crash post-pass patch | Save files / personal `settings.json` |

The post-pass under `CrashBandicoot.Launcher/Recomp/Patches/` applies known SCUS-94900 control-flow fixes after a clean recompile (decompressor merge, Duff / jump-table handling).

## Developers

```powershell
dotnet build CrashBandicoot.Launcher -c Release
dotnet run --project CrashBandicoot.Launcher -c Release
```

Publish a Windows x64 folder (not single-file — Roslyn compile + mods need real DLL paths):

```powershell
dotnet publish CrashBandicoot.Launcher -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o .\publish
```

Confirm the publish output contains `Recomp\CrashBandicoot.json` and `Recomp\Patches\main.cs.patch`, and does **not** contain generated game sources (`main.cs` / `game.recomp.dll`).

### Regenerating the post-pass patch

If the recompiler’s clean output changes and the post-pass fails context matching:

1. Produce a clean regen into a temp folder with RecompOne.
2. Diff against a known-good hand-fixed `main.cs`.
3. Update `CrashBandicoot.Launcher/Recomp/Patches/main.cs.patch` (see `tools/make_postpass_patch.py`).

### Layout

- `CrashBandicoot.Launcher/` — WebView2 host + `Ui/index.html` + local recomp pipeline
- `RecompOne.Runtime/` — runtime / HLE
- `RecompOne.Recompiler/` — static MIPS→C# tool (library)
- `CrashBandicoot.Recompiled/` — local-only; gitignored except README (not used by the consumer pipeline)

## Legal

This project distributes **tools and runtime**, not Crash Bandicoot. Do not upload disc dumps or generated recompiled game code to public remotes. Trademark and copyright belong to their respective owners; this is an unofficial fan project.
