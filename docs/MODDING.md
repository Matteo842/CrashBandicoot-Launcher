# Modding Crash Bandicoot: Recompiled

Mods are C# packages under a `mods/` folder next to the exe. At game start the runtime discovers them, optionally filters by launcher enable state, compiles with Roslyn, and installs MonoMod hooks before the recompiled EXE runs.

Sample mods live in [`examples/mods/`](../examples/mods/) (currently `auto-spin`). Release builds copy them into `mods/`; `AppPaths.EnsureCreated` also seeds from `examples/mods/` when present, without overwriting existing folders.

## Layout

```
mods/
  my-mod/
    mod.json
    Something.cs
    Nested/More.cs
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

Other useful events: `DrawEnvEvent`, `DispEnvEvent`, `OverlayLoadedEvent`, `RuntimeReadyEvent`.

## Sample: Auto Spin

[`examples/mods/auto-spin`](../examples/mods/auto-spin) listens to `PadReadEvent` and, while Cross is held **in a gameplay level**, also presses Square (spin). It stays off on the warp map / title / menus / cinema so Cross can still confirm level select.

## Tips

- Prefer named SDK functions (`VSync`, `PutDrawEnv`, …) over raw addresses when renames exist.
- One **Replace** owner wins; later mods are ignored with a log line.
- Cache invalidates when host or entry assembly MVID / sources change. Delete `mods/.cache` if a fix does not seem to apply.
- Keep mods small and restart after edits — there is no hot-reload yet.
- Frame timing background: [CRASH_BANDICOOT_RECOMP.md §3](CRASH_BANDICOOT_RECOMP.md).
