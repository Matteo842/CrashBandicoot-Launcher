# Modding Crash Bandicoot: Recompiled

Mods are C# packages under a `mods/` folder next to the exe. At game start the runtime discovers them, optionally filters by launcher enable state, compiles with Roslyn, and installs MonoMod hooks before the recompiled EXE runs.

Sample mods live in [`examples/mods/`](../examples/mods/) (`auto-spin`, `disc-overlay-stub`, `vram-transfer-stub`, `spu-dma-stub`, `catalog-stub`). Release builds copy them into `mods/`; `AppPaths.EnsureCreated` also seeds from `examples/mods/` when present, without overwriting existing folders.

## Layout

```
mods/
  my-mod/
    mod.json
    Something.cs
    Nested/More.cs
    disc/                 # optional ISO path overlays (see Disc remap)
      S0/MYFILE.NSD
  other-mod.zip          # zip root must contain mod.json
  .cache/                # compiled DLLs (auto-managed)
```

### `mod.json`

```json
{
  "id": "my-mod",
  "name": "My Mod",
  "version": "1.0.0",
  "author": "you",
  "dependencies": ["other-mod-id"]
}
```

- **id** — unique, used by `ActiveMods` and the cache filename.
- **dependencies** — other mod ids that must load first (topo-sorted; missing deps skip the mod).

Asset-only mods are allowed: if there are no `.cs` sources but a `disc/` folder (or zip entries under `disc/`) is present, the mod still loads and contributes disc remaps.

## Enable / disable (launcher)

1. Open **MODS** in the launcher.
2. Toggle checkboxes, **Save**.
3. Restart the game (hooks install only at start).

Until you save once, every discovered mod loads (`ModsConfigured = false`). After save, only ids in `settings.json` → `ActiveMods` load (empty list = none). Dependencies of an enabled mod are pulled in automatically.

**Open mods folder** creates `mods/` if needed and opens it in the file explorer.

In-game: Developer menu bar → **Mods → Mods…** lists what actually loaded this session.

## Compiling

The in-process Roslyn compiler injects common `global using` directives (`System`, `System.Collections.Generic`, `System.Linq`, …). You can still add explicit usings. Compile errors are printed to the console as `[Mods] <id>: …`.

## Hook attributes

Target the **recompiled** method on an overlay. Crash Bandicoot today is a single overlay named `main`. SDK renames (from `CrashBandicoot.json`) become method names — e.g. address `0x8003E638` → `VSync`.

```csharp
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

// Replace (one owner per function). Prefer the orig-callback form.
[Replace("main", "VSync")]
static void MyVSync(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
{
    orig(c, m);
}

[PreHook("main", "PutDrawEnv")]
static bool BeforeDraw(CpuContext c, IMemory m)
{
    // return false to skip original + Replace
    return true;
}

[PostHook("main", "DrawOTag")]
static void AfterDraw(CpuContext c, IMemory m) { }
```

Unresolved names can use an address instead:

```csharp
[Replace("main", Address = 0x80011FC4u)]
static void GameLoopHook(/* … */) { }
```

**Order:** all Pre hooks → Replace or original → all Post hooks.

## `IMod` lifecycle

```csharp
public sealed class MyMod : IMod
{
    public void OnLoad() { /* after assembly load */ }
    public void OnUnload() { /* reserved; unload orchestration is limited today */ }
}
```

## Events (no function hook)

```csharp
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hardware;

Event.AddListener<PadReadEvent>(e =>
{
    if (e.Port != 0) return;
    // Controller layout, active-low (bit cleared = pressed)
    if ((e.Buttons & Controller.Cross) == 0)
        e.Buttons = (ushort)(e.Buttons & ~Controller.Square);
});

Event.AddListener<VSyncEvent>(e => { /* after each waited vblank in LibEtc.VSync */ });
```

`PadReadEvent` runs on both the BIOS pad buffer path and the LibPad refresh path, so filters apply whichever API the game uses.

Other useful events: `DrawEnvEvent`, `DispEnvEvent`, `OverlayLoadedEvent`, `RuntimeReadyEvent`, `VramTransferEvent` (see below), `SpuDmaEvent` (see below).

## Asset catalogs

Stable **ids** for levels / textures / sounds so mods can target assets by name instead of raw LBA / VRAM rect / SPU address. Reverse engineering fills the JSON once; modders consume ids. **No game textures or audio are shipped** — only metadata fingerprints.

Embedded defaults live in `RecompOne.Runtime/Catalog/data/` (`levels.scus94900.json`, `textures.scus94900.json`, `sounds.scus94900.json`). Namespace: `RecompOne.Runtime.Catalogs`. Optional loose overrides: place the same filenames under `catalog/` next to the exe (full replace per file).

### Levels

Crash 1 (SCUS-94900) level table is seeded from [cbhacks Level ID](https://wiki.cbhacks.com/w/Level_ID). Each entry has `id`, `slug`, `name`, `kind`, and NSD `code`.

`kind`: `gameplay` | `titleMap` | `complete` | `cinema` | `bonus` | `unused` | `empty`.

Widescreen / filters / auto-spin treat `titleMap`, `complete`, and `cinema` as non-gameplay (same gate as before).

```csharp
using RecompOne.Runtime.Catalogs;

if (Catalog.Levels.TryGetBySlug("n-sanity-beach", out var level))
    Console.WriteLine($"{level.Id} {level.Name}");

if (Catalog.Levels.TryGetCurrent(out var cur))
    Console.WriteLine($"now: {cur.Slug}");

bool ui = Catalog.Levels.IsUiOrCinema(id); // titleMap | complete | cinema
```

### Textures / sounds (fingerprints)

Texture entries match VRAM **Load** uploads by optional `pixelHash` (SHA256 prefix of BGR555 pixels) and/or destination rect `(x,y,w,h)`, optionally scoped with `levelIds`. Sound entries match SPU DMA by optional `payloadHash` and/or `(spuAddr, length)`.

Shipped DBs start **empty** — use discovery (below) or a future export tool to fill them.

```json
{
  "game": "SCUS-94900",
  "textures": [
    {
      "id": "crate_wood",
      "match": { "x": 512, "y": 384, "w": 64, "h": 64 },
      "pixelHash": "0123456789abcdef",
      "levelIds": [9, 12]
    }
  ]
}
```

```json
{
  "game": "SCUS-94900",
  "sounds": [
    {
      "id": "spin",
      "match": { "spuAddr": "0x1A000", "length": 2048 },
      "payloadHash": null
    }
  ]
}
```

```csharp
if (Catalog.Textures.Resolve(e, out var tex) && tex != null)
    Console.WriteLine($"matched {tex.Id}");

if (Catalog.Sounds.Resolve(e, out var snd) && snd != null)
    Console.WriteLine($"matched {snd.Id}");
```

### Discovery logging

Set `"CatalogDiscovery": true` in `settings.json` (or `GameConfig.CatalogDiscovery`). Unknown VRAM Loads / SPU DMA uploads log once per fingerprint:

```
[Catalog] unknown texture rect=(512,0) 64x64 hash=… level=0x9 (n-sanity-beach)
[Catalog] unknown sound spu=0x1A000 len=2048 hash=…
```

Paste those fingerprints into the JSON catalogs. Caps at 256 unique lines per session.

### Roadmap (not in this release)

1. Catalogs with stable ids — **done**
2. PNG / WAV loaders applied via VRAM / SPU hooks
3. Folder convention + `mod.json` assets (pack without C#)
4. GUI/CLI export/replace from the user’s disc
5. High-level API (`Textures.Replace("crate_wood", …)`) + hot-reload

## VRAM transfer hook

Fires on GP0 **LoadImage** (CPU→VRAM), **StoreImage** (VRAM→CPU), and **MoveImage** (VRAM→VRAM). This is the path for texture-style patches — **not** `RenderPrimEvent`.

Works on both soft VRAM and the HLE/GL backend (including widescreen / filters): mods patch pixels **before** `WriteVram`.

```csharp
using RecompOne.Runtime.Events;

Event.AddListener<VramTransferEvent>(e =>
{
    if (e.Direction != VramTransfer.Load) return;
    if (e.Pixels == null) return;
    // e.X/Y/W/H = destination rect in VRAM
    // e.Pixels = BGR555, row-major, length e.PixelCount (== W*H)
    // Mutate in place to replace the upload:
    // for (int i = 0; i < e.PixelCount; i++) e.Pixels[i] = …;
});
```

| Direction | When | `Pixels` |
|-----------|------|----------|
| `Load` | After DMA/GP0 filled the upload buffer, before soft/GL commit | Mutable upload |
| `Store` | After readback into a buffer, before the game reads it | Mutable readback |
| `Move` | After VRAM→VRAM copy | `null` (coords only; `SrcX`/`SrcY` + `X`/`Y`) |

No game textures are shipped. See [`examples/mods/vram-transfer-stub`](../examples/mods/vram-transfer-stub) for a logging sample.

## SPU DMA hook

When the game uploads **sound samples** (and related SPU data) from main RAM into the sound chip’s 512 KiB RAM, DMA channel 4 runs. `SpuDmaEvent` fires **after** the bytes are copied from main RAM and **before** they are written into SPU RAM — mutate `e.Data` to replace/patch audio.

```csharp
using RecompOne.Runtime.Events;

Event.AddListener<SpuDmaEvent>(e =>
{
    // e.SpuAddr      — destination in SPU RAM (bytes)
    // e.MainRamAddr  — source in main RAM
    // e.Data / Length — mutable payload (often PS1 ADPCM / VAB chunks)
    // Array.Clear(e.Data); // would silence this upload
});
```

This is **not** an audio-pack format and does not ship game audio. See [`examples/mods/spu-dma-stub`](../examples/mods/spu-dma-stub).

## Disc remap

Replace disc file reads without patching the user's `.bin` / `.cue`. Host files live under:

```
mods/<mod-id>/disc/<ISO-relative-path>
```

Example: `mods/my-patch/disc/S0/S000001E.NSD` redirects ISO path `S0/S000001E.NSD` (`;1` optional, case-insensitive).

**How it works**

1. After mods load, the runtime indexes each loaded mod's `disc/` tree (folders, or `disc/…` inside a `.zip`).
2. Each host file is resolved with `CueFs.Locate` on the user's disc. If the path is missing, it is skipped (`[Disc] … not on disc, skipped`).
3. Later `CdSearchFile` / sector reads / `ReadFile` (including BIOS open and boot) serve the host file when the LBA or path matches.
4. **First match wins** using the same order as mod load (dependency topo-sort, then id). Later mods cannot override an already-remapped path (logged as `skip … already remapped`).
5. Same-or-smaller replacements keep the original disc LBA; larger files get a virtual LBA so neighbors are not shadowed.
6. Overlay bytes are treated as concatenated Mode1 user data (2048 bytes/sector). XA/FMV framed streams are not a supported authoring target yet (raw LBA intercept still applies if you supply matching sector payloads).

Look for log lines:

```
[Mods] disc-overlay-stub v1.0.0: asset-only (disc overlay)
[Disc] remap 'S0/FOO.NSD' ← disc-overlay-stub/disc/S0/FOO.NSD (disc-overlay-stub) lba=… size=…
[Disc] 1 disc remap(s) active
```

Do **not** ship copyrighted game dumps, NSF/NSD extracted from the retail disc, textures, or audio in the repo or releases. The stub sample only documents the folder layout.

## Sample: Auto Spin

[`examples/mods/auto-spin`](../examples/mods/auto-spin) listens to `PadReadEvent` and, while Cross is held **in a gameplay level**, also presses Square (spin). It stays off on the warp map / title / menus / cinema so Cross can still confirm level select (uses `Catalog.Levels.IsUiOrCinema`).

## Sample: Catalog Stub

[`examples/mods/catalog-stub`](../examples/mods/catalog-stub) logs level slug/name changes and whether VRAM Loads / SPU DMA resolve to a catalog id. With empty texture/sound DBs you will see `unresolved` lines — useful while filling catalogs. Does not mutate pixels or audio.

## Sample: Disc Overlay Stub

[`examples/mods/disc-overlay-stub`](../examples/mods/disc-overlay-stub) is asset-only (no C#). Put your own replacements under `disc/` mirroring ISO paths; see that folder's README.

## Sample: VRAM Transfer Stub

[`examples/mods/vram-transfer-stub`](../examples/mods/vram-transfer-stub) listens to `VramTransferEvent` and **draws a green check** into each Load upload (and a small HUD badge at the top-left of the display) so you can see the hook working. Disable the mod when you no longer want the overlay.

## Sample: SPU DMA Stub

[`examples/mods/spu-dma-stub`](../examples/mods/spu-dma-stub) logs the first SPU DMA uploads (`[SPU] DMA → …`). Enable it and play — console lines mean sample data is going through the hook.

## Tips

- Prefer named SDK functions (`VSync`, `PutDrawEnv`, …) over raw addresses when renames exist.
- One **Replace** owner wins; later mods are ignored with a log line.
- Cache invalidates when host or entry assembly MVID / sources change. Delete `mods/.cache` if a fix does not seem to apply.
- Keep mods small and restart after edits — there is no hot-reload yet.
- Frame timing background: [CRASH_BANDICOOT_RECOMP.md §3](CRASH_BANDICOOT_RECOMP.md).
