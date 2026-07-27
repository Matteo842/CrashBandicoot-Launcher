# Disc overlay (put YOUR files here)

This folder mirrors ISO paths on the user's own Crash Bandicoot disc image.

## Layout

```
mods/disc-overlay-stub/
  mod.json
  disc/
    README.md          ← this file (ignored by the runtime)
    S0/YOURFILE.NSD    ← example: same relative path as on the ISO
```

## Rules

- Use the **same relative path** as on the disc (case-insensitive; `;1` is optional).
- Files here are **your** replacements — do not redistribute copyrighted game assets.
- The runtime never writes your `.bin` / `.cue`. Missing ISO paths are skipped with a `[Disc]` log line.
- Precedence: mod load order (dependency topo-sort); **first** remap for a path wins.

## Check it worked

1. Enable this mod in the launcher **MODS** menu (Save + restart).
2. Drop a replacement under `disc/` matching an ISO path.
3. Look for: `[Disc] remap '…' ← … (disc-overlay-stub)`
