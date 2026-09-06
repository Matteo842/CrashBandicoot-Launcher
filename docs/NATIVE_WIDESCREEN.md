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

| Scene | Current result and remaining work |
| --- | --- |
| N. Sanity Beach (9) | Initial ground extended; later camera positions and the exposed horizon still need visual refinement. |
| Jungle Rollers (12) | Banks and selected trunk edges extended; the upper right scenery remains incomplete. |
| The Great Gate (18), Native Fortress (26) | Initial ground and outer trunk surfaces extended. Continue testing while climbing through subsequent zones. |
| Upstream (15) | Selected outer banks extended without changing the water width. Some foliage boundaries still need completion. |
| Up the Creek (24) | Water shader matches the retail pass. Exposed bank/foliage boundaries still need completion; extending isolated grass strips was rejected because it left floating strips. |
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
