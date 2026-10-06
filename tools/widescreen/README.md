# Native 16:9 scenery-hole toolkit

Finds and fixes the black holes the native widescreen view shows at the sides of
the screen, where Crash 1's scenery was cut to the 4:3 view. Works for any level.
Nothing from the game is stored here: every tool reads the player's own disc at run
time (`WS_DISC`, default `D:/GitHub/RecompOne/Crash Bandicoot.bin`) and writes its
output under `artifacts/` (git-ignored).

Requirements: Python 3 with numpy, scipy, Pillow; .NET 10 SDK for the two C# tools.

## Workflow

1. **Scan the level offline** (seconds, no game window):

       python tools/widescreen/wide.py scan 41 --step 2 --out artifacts/issue104/scan

   Renders the 16:9 view at every camera-path point of every zone (zone's meshes
   plus neighbours, like the runtime), counts side-band voids, groups them into
   hotspots and writes `report.md` + one false-colour picture per hotspot. For each
   void it lists what borders it: mesh, plane (`X=6600`, `slant`, `sky`), half cells
   (lone triangles of a 4:3 per-triangle cull), and where the void meets that plane
   in world coordinates (the area a repair must cover). Voids that only continue a
   pit or darkness already visible at the 4:3 edge are not counted.

2. **Understand the geometry**:

       wide.py view  41 a0:0:0                   # picture + void report at a path point
       wide.py view  41 log:<probe run>:<render> # exact camera of a probe frame
       wide.py view  41 @-3000,4000,19700,0,-1024 --meshes e__FW --box ... --nocull
       wide.py grid  41 a__FW X=6600 --box ... --at a0:0:0   # cell map, '?' = void
       wide.py faces 41 a__FW x0,y0,z0,x1,y1,z1
       wide.py poly  41 a__FW 892 901            # corners, colours, UVs, open edges
       wide.py axes  41                          # towers/columns: axis + C# line

3. **Write the repair in C#** (`RecompOne.Runtime/Host/FramePacing.NativeWide*.cs`).
   Building blocks: `NativeWideCopy` (box copies, mirrors, `Complete`), `NativeWideGrid`,
   `NativeWideTerrain`, `NativeWideSymmetry`, `NativeWideRows` (rows of wall cells
   continued past every run end), periodic walls (`NativeWidePeriodicRepairs`, for
   irregular faces such as arched windows), `NativeWideTower` (round towers rebuilt
   from their mirrored twin and turned half a turn about their axis; `wide.py axes`
   prints the specs, twins included), `NativeWideSkyArc`, plus
   `NativeWideRepairsBehind` for fills that must only show where nothing else is.

4. **Check the real C# offline** (about 10 s):

       tools/widescreen/redump.sh 41
       wide.py scan 41 --repairs artifacts/widescreen/repairs_41.json --check

   `RepairDump` runs the runtime's own `NativeWideSceneRepairs` on the disc meshes
   (no game, no window) and prints the generation time per mesh. `--check` reports
   per camera point the void pixels filled, **z-fight** pixels (a repair beating a
   nearly parallel retail face at almost the same depth, or hiding the sky: must be 0)
   and pixels a repair covers from clearly in front (fine for a rebuilt tower in front
   of a wall, wrong for a copy sticking out). Pictures tint repairs cyan, z-fights red.

5. **Confirm in game** with the probe (short runs: the PC may be in use). The probe
   carries its own copy of the runtime: rebuild it after every runtime change.

       dotnet build tools/widescreen/Probe -c Debug -o artifacts/widescreen/probe
       wide.py route 41 a0:0:0 c1:0:44 --ahead 1416 --below 1125 --hold 1.5 > route.txt
       VOID=1 artifacts/widescreen/probe/WideProbe.exe fix 41 50 --script "$(cat route.txt)" --snap 15 --out artifacts/issue104/runs
       VOID=1 NOFIX=1 artifacts/widescreen/probe/WideProbe.exe base 41 50 --script "$(cat route.txt)" --snap 15 --out artifacts/issue104/runs
       wide.py pairs artifacts/issue104/runs/base artifacts/issue104/runs/fix pairs.png all

   `VOID=1` paints the side bands magenta where nothing is drawn, `NOFIX=1` drops all
   scene repairs. The probe logs the exact camera of every frame (`log.json`), so
   `view log:<run>:<render>` reproduces any frame offline. Crash/camera offsets differ
   per level: read them from a short run's `log.json` (`crash` vs `cam`).

## Notes

- Camera matrix from a path point: pitch = rot[0], yaw = rot[1] (4096 = full turn),
  screen Y scaled by 0.625; matches the GTE matrix the probe logs.
- Backdrops (sky) are camera-centred and write no depth in game: anything drawn in
  the side pass covers them, which the offline renderer models.
- Meshes overlap at zone boundaries and can hold the same parts: a repair copied in
  one mesh can fight the neighbour's original (offline `--check` shows it as z-fight).
- `RepairDump` prints the time each mesh's repairs take (computed once, the first
  frame a mesh is drawn); `PROFILE=1` splits The Lab's passes.
- Crash 2 shares the NSF page/entry container (`wslib/nsf.py`); its zone and scenery
  formats differ, so `wslib/level.py` would need a Crash 2 counterpart.
