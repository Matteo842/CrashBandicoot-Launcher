# Asset Pack Stub

Declarative (no C#) texture pack via `mod.json` → `assets`. Synthetic magenta/cyan checkers only — **no game art**.

```
asset-pack-stub/
  mod.json
  textures/demo_tile_a.png
  textures/demo_tile_b.png
```

When `assets` is present, only listed files register (no folder scan). First mod in load order wins per catalog id — this stub loads before `texture-replace-stub` alphabetically, so it usually owns `demo_tile_*`.

Optional folders for later packs: `audio/<id>.wav` (schema in docs; WAV→SPU not wired), `disc/<ISO-path>` (use `assets.disc` or legacy scan without `assets`).
