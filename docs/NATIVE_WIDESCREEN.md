# Native 16:9 — implementation and verification

This is still experimental. Rendering the loaded world is supported across the gameplay shader paths, but completing the scenery exposed outside the retail camera is not finished for every level or camera position.

## Rendering contract

The GTE projection scale and the original 4:3 image are preserved. Additional triangles are projected with the same camera into a wider framebuffer. There is no horizontal image warp or separate scale for the edges.

The original ordering table draws the centre. The side pass enumerates the loaded WGEO meshes independently of the authored SLST visibility list, clips against the near plane, and uses the original materials, UV animation, lighting and fog inputs. It supports normal, fog, water ripple, tint, fog plus tint, and lamp shading. Tint deliberately matches the lighting bank selected by the current recompiled game; changing the native pass alone would create a lighting seam.

The side pass runs after the retail world transform has prepared its shader inputs, and submits after `PutDrawEnv` selects the corresponding double buffer. Each display target records whether it contains a wide world. Menus, the map and cinematics retain 4:3.

World geometry and scenery extensions use depth. Extensions have a small polygon offset so the authored surface wins at coplanar joins. They must not be drawn behind *every* original primitive: the original meshes contain black backing planes which can otherwise cover closer additions. Sky and full-screen effects have separate drawing modes. The bridges' additive fog layers are tiled at the original texture scale and composited at their original ordering-table positions. Full-screen flat overlays, including the subtractive death fade, cover both side bands without scene depth.

## Scenery completion

Some exposed edges are genuinely the end of the retail mesh. Simply widening the visibility list or disabling back-face culling cannot recover triangles which are absent. Those checks were performed for Jungle Rollers, The Great Gate, Native Fortress and Up the Creek; repeating them is not a solution to their remaining asset boundaries.

`FramePacing.NativeWideSceneRepairs.cs` contains narrowly selected additions for known assets. Mesh counts and world origins identify the asset; materials and boundary coordinates select the exposed surface. New surfaces exist in world space and retain texture density. They are not collision geometry. Never apply the same operation indiscriminately to every boundary: cliffs, rivers and doorways must stay open.

Scenery additions are cached by level, world origin and mesh counts. A level can have several repaired WGEO meshes loaded together; reusing one level-wide list would apply the first mesh's triangles and material indices to the other meshes.

| Scene | Current result and remaining work |
| --- | --- |
| N. Sanity Beach (9) | Initial ground extended; later camera positions and the exposed horizon still need visual refinement. |
| Jungle Rollers (12) | Initial upper right trunk completed, including its clipped top and the join to the bank. Opening movement, TNT death and respawn exercised. The suspected colourful texture defect near the first low stone wall was traced to original foreground totem polygons 34/35; the sampled texture is intact. Continue inspecting exposed silhouettes and later cameras. |
| The Great Gate (18), Native Fortress (26) | Initial ground and outer trunk surfaces extended. Continue testing while climbing through subsequent zones. |
| Upstream (15) | Selected outer banks extended without changing the water width. Some foliage boundaries still need completion. |
| Up the Creek (24) | Initial banks, supporting bank faces and cut trunk contours extended together. The next left trunk and the bank behind the right totem now cover the cuts exposed when leaving the log; that bank spans two WGEO meshes. Movement through the first two lilies, the next plant death and respawns exercised. The water keeps its authored width. A further upper-right trunk cut appears while advancing past the lilies; later river zones and foliage still require completion and a full traversal. |
| Road to Nowhere (20), The High Road (22) | Sky, fog layers and background cover the wider view. Death fade also covers the side bands. |
| Cortex (31) | Empty WGEO placeholder is skipped instead of disabling the whole scene's wide pass. Full fight needs playtesting. |
| Slippery Climb (46) | Initial right wall extended with the level's lighting. Rain coverage is a separate task. |
| Castle Machinery (55) | Initial upper left backing wall extended. Later cameras need playtesting. |

A clean initial view does not certify a complete level. Exercise camera movement, spawning, transparent objects, deaths, respawns, boss behaviour and returning to the map. Side-band object depth still depends on the existing GTE coordinate cache; unusual overlaps and transparent surfaces need attention during playtesting. Desktop GL is tested; GLES depth clears use the float API, but Android runtime validation remains outstanding.

## Repeatable centre check

`tools/CrashBandicoot.WideCheck` launches the game directly using an existing recompiled assembly and the player's own disc. It keeps its config and saves beneath the requested output directory.

```powershell
dotnet build tools/CrashBandicoot.WideCheck -c Release

tools/CrashBandicoot.WideCheck/bin/Release/net10.0/CrashBandicoot.WideCheck.exe `
  --disc "D:/path/to/game.cue" `
  --game "D:/path/to/game.recomp.dll" `
  --level 18 --output artifacts/wide-check-gate
```

The default captures the initial camera after loading, at VSync callback 600. `--frame` selects another callback. `--fps 60 --walk --frame 700` also exercises motion and can catch the bridges' death fade. The walking script presses forward, jump and spin; it is not a full-level playthrough.

The tool captures the native frame and replays the **same ordering table**, restoring the original draw environment first. That restoration matters: the death fade changes draw mode, including dithering, before the next replay. Comparing frames from separate runs would mix rendering changes with animation and timing differences.

Outputs are `native.png`, `original.png` and `result.json`. PNGs contain raw framebuffer pixels with the PS1's non-square pixel aspect; view them at 16:9 and 4:3 respectively for visual assessment. The JSON reports whether wide rendering was active, the number of changed centre RGB pixels, and the maximum channel difference. Exit 0 means wide rendering was active and the centre was identical; it does **not** certify complete side geometry. Exit 1 means comparison failure, 2 invalid arguments, and 3 a timeout or no completed comparison.

Local checks have shown identical centre RGB for the repaired initial scenes and the moving bridge death fade. Shader diagnostics also compared emitted vertex colours, UVs, texture pages and projected positions against the retail pass. Keep generated assemblies, disc data, dumps and screenshots in ignored output directories.

## Boundary view and movement captures

Add `--boundaries` to generate `boundaries.svg` beside the final PNG. Open the SVG directly with its PNG beside it. It displays the frame at the correct aspect ratio and marks open edges of the original static WGEO meshes in magenta. Hover a line for the world origin, polygon and material identifiers. These are diagnostic candidates: foliage silhouettes, hidden edges and boundaries already covered by repairs are also marked. Confirm an actual hole in `native.png` before extending anything. The overlay does not draw the added repair mesh or evaluate animated water displacement.

`--snapshots` captures earlier frames during the same game session, without replaying their ordering tables. Only the final `--frame` performs the centre comparison. `snapshots.json` records both requested and actual callback numbers, the active level and image dimensions. Duplicate snapshot requests are combined, and requests after the final frame are rejected.

```powershell
tools/CrashBandicoot.WideCheck/bin/Release/net10.0/CrashBandicoot.WideCheck.exe `
  --disc "D:/path/to/game.cue" --game "D:/path/to/game.recomp.dll" `
  --level 24 --fps 60 --walk --frame 700 `
  --snapshots 360,420,500,600,700 --boundaries `
  --output artifacts/wide-check-creek-motion
```

Each early capture has a `native-000360.png` and, when requested, a corresponding `boundaries-000360.svg`. PNG pixels remain unscaled for comparison; the SVG handles display aspect ratio only.

For more specific movements, `--input <script.json>` replaces `--walk`. A script is an array of `{ "from": 320, "to": 360, "buttons": ["Up", "Cross"] }` ranges. `from` is inclusive and `to` exclusive, in VSync callbacks; overlapping ranges combine buttons. All buttons are released outside the ranges. Accepted names are Up, Down, Left, Right, Cross, Square, Circle, Triangle, Start and Select, ignoring case. The checked-in example moves around the opening TNT area of Jungle Rollers:

```powershell
tools/CrashBandicoot.WideCheck/bin/Release/net10.0/CrashBandicoot.WideCheck.exe `
  --disc "D:/path/to/game.cue" --game "D:/path/to/game.recomp.dll" `
  --level 12 --fps 60 --frame 600 `
  --input tools/CrashBandicoot.WideCheck/scenarios/jungle-opening.json `
  --snapshots 360,420,480,540,600 --boundaries `
  --output artifacts/wide-check-jungle-motion
```

These scripts exercise a short section, not a full level, and movement can differ with runtime timing. The final `result.json` includes `actualLevel` as well as the requested `level`; a game-over screen or a draw without a wide world does not pass the wide check. Successful comparisons unwind the game and shut down audio/GL before returning exit 0, avoiding the native teardown crash previously caused by exiting inside the draw hook.

`scenarios/creek-lilies.json` times the spin and jumps to leave Up the Creek's first log and cross the first two lilies. The exploratory run continued into the next plant and its death animation; this is a camera/respawn exercise, not a complete or guaranteed successful route. Capture the movement and the final fade with:

```powershell
tools/CrashBandicoot.WideCheck/bin/Release/net10.0/CrashBandicoot.WideCheck.exe `
  --disc "D:/path/to/game.cue" --game "D:/path/to/game.recomp.dll" `
  --level 24 --fps 60 --frame 650 `
  --input tools/CrashBandicoot.WideCheck/scenarios/creek-lilies.json `
  --snapshots 400,440,480,500,520,550,600,650 --boundaries `
  --output artifacts/wide-check-creek-lilies
```

## Verification — 2026-09-06 continuation

- Initial same-OT comparisons: levels 9, 12, 15, 18, 20, 22, 24, 26, 31, 46 and 55 all had zero changed centre RGB pixels.
- Motion comparisons: Up the Creek at callback 700, the Road to Nowhere death fade at 700, and Jungle Rollers at 1600 also had zero changed centre RGB pixels. Earlier motion captures include deaths, respawns and changing camera positions.
- The initial Up the Creek completion covers the bank support faces as well as the grass and trunks. Jungle Rollers uses matching offsets at shared cut endpoints, with extra coverage at the top of the distant right trunk while retaining its root height.
- A baseline build of the preceding repair source also exhibited the suspected later Jungle Rollers right-edge texture defect near the low stone wall. The 2026-09-07 investigation below traces that slice to the original foreground totem; the initial corner fix does not certify the whole route.
- The Windows launcher and verification tool are built locally. Full level traversal, the remaining scenes in the table, transparent object overlaps and Android validation remain outstanding.

Local comparison images, scripts used for longer exploratory runs and reports are under the ignored `artifacts/wide-session/` directory. Do not commit these game-derived captures or the temporary baseline build.

## Verification — 2026-09-07 continuation

- Up the Creek: completed the next left trunk's cut (materials 90/92/102) and the right outer bank (54/56), plus turf in the adjacent mesh at origin `(8197, 6468, 114910)` (8/10). These selections exclude the water and the totem itself.
- The two loaded creek meshes now hold separate cached additions: 1,364 and 105 triangles. Removing only the new selections in an isolated diagnostic restores the first mesh's preceding 1,229-triangle list and leaves the second empty.
- A controlled side-pass replay at the same camera compared these additions enabled/disabled. Repeating the enabled replay changed zero pixels; the additions changed 5,048 side pixels and zero centre pixels. This diagnostic rebuilds the side pass with post-draw shader state, so use it to compare the additions, not as a replacement for the standard native-vs-retail centre check. Evidence: `artifacts/wide-session/creek-same-frame-v3/ab-result.json` and `prima-dopo.png`.
- Standard same-OT centre checks passed during log departure/death at callback 701 and the lily/plant route at 650, both with actual process exit 0. The script reached the first two lilies before dying at the following plant. It still exposes a further upper-right trunk cut around callback 480; do not mark the river traversal complete.
- Jungle Rollers: RAM picking and VRAM texture inspection traced the colourful right-side slice to original totem polygons 34/35, materials 16/18. Texture corruption was not confirmed; no masking geometry or texture substitution was added.
- Final initial-view regressions for levels 24, 12 and 15 passed with zero changed centre pixels and actual process exit 0 (`artifacts/wide-session/continuation-final-results.json`). The Windows launcher build succeeded without warnings; version remains 1.8.1.
