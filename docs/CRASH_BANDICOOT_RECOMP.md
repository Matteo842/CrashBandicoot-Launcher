# Crash Bandicoot on RecompOne — How We Got It Playable

**Game:** Crash Bandicoot (SCUS-94900, NTSC-U)  
**Stack:** RecompOne static MIPS→C# recompiler + `RecompOne.Runtime`  
**Status at write-up:** Title → map → level 1 complete → level 2 entered; gameplay solid; frame timing corrected to ~30 Hz.

This note is a field guide for the next title (Spyro, etc.): what broke, why, and what to check first.

---

## 1. Architecture (what actually runs)

```
Disc (.cue/.bin)  →  recompiled C# (main.cs + Entry)
                         │
                         ▼
              CpuContext + IMemory (PSMemory)
                         │
         ┌───────────────┼───────────────┐
         ▼               ▼               ▼
   BIOS HLE (A/B)   PsyQ SDK HLE    GPU/SPU/CD
   Pad, events      VSync, GPU…     Present / audio
```

- **Recompiler** (`RecompOne.Recompiler`): offline; turns EXE/overlays into C# methods + a dispatcher map.
- **Runtime** (`RecompOne.Runtime`): online; fakes enough PS1 so that code can run natively.
- **Game host** (`CrashBandicoot.Recomp`): tiny `Program.cs` that points `CdPath` at a `.cue` and calls `Entry.Run`.

The disc is still required at runtime (NSF/NSD paging, audio, etc.). The recompiled binary is *code*, not a full ROM replacement.

---

## 2. Boot path we had to clear (in order)

### 2.1 Pad input wrong halfword

**Symptom:** Cross / confirm did nothing on SCEA / menus.  
**Cause:** Retail `PadUpdate` reads **port 0 in the low 16 bits**. Early notes / c1-style assumptions and a delay-slot footgun pointed at the high half. `BiosB.PadRead` must write pad1 into the low halfword.  
**Lesson:** Verify pad layout against *this* game’s `PadUpdate`, not a generic SDK guess. Keyboard Cross was **Z** (`0x0040` in the low half).

### 2.2 Title stuck on SCEA (not dead input)

**Symptom:** Button worked in memory, screen never advanced.  
**Cause:** `ticks_elapsed` (`0x80034520`) never moved — BIOS root-counter IRQs are not emulated. Crash drives GOOL waits off  
`ticks_elapsed` → `draw_stamp` → `frames_elapsed`.  
**Fix direction:** Advance ticks when vblank time passes (see §3).  
**Lesson:** If “waits” never finish, dump timing globals before assuming input or rendering is broken.

### 2.3 Halfword memcpy / Duff’s device (`0x80033FBC`)

**Symptom:** `unmapped call: 0x80034048` (and similar) after starting a game / loading.  
**Cause:** Hand-tuned copy uses **computed `jr` into the middle of the copy body**. The recompiler emits `Dispatcher.Call(c, m, reg)` for those addresses; mid-function labels are not registered entry points.  
**Fix:** Config patch → HLE `LibMem.RMemcpy` (overlap-safe halfword copy).  
**Lesson:** Any `jr` to `base - index*stride` inside a leaf is a recomp footgun. Prefer HLE or emit a `switch` on known targets.

### 2.4 Decompressor split across two “functions” (`0x800334A0` / `0x80033878`)

**Symptom:** `unmapped call: 0x80033C94` during page init (“Inited and Allocated N pages”).  
**Cause:** Function detection cut one decompressor in two. A forward `beq` became `Dispatcher.Call` + `return` instead of `goto` into the other half. Same function also had **Duff’s-device** `jr s4` into unrolled copy bodies and tail stubs that `jr` to a **prologue-hijacked RA** (loop continue), not the real caller.  
**Fix (pragmatic):** Merge bodies in generated C#, replace Calls with `switch`/`goto`, after stub `Call` continue via `RA` (`0x80033748` / `0x80033C28`).  
**Lesson:** Cross-function branches to mid-labels ⇒ merge. `jr ra` after RA was overwritten for local control flow ≠ C# `return` from the outer function.

### 2.5 Inline jump tables via `bgez $zero` (`func_80037D50`)

**Symptom:** `unmapped call: 0x80037F04` deeper in render/fill paths.  
**Cause:** Tables of unconditional `bgez r0, target` (8-byte slots). Recompiler treated `jr` into the table as `Dispatcher.Call`, leaving table entries as dead code after `return`.  
**Fix:** `switch (addr) { case slot: /* delay-slot side effects */; goto Ltarget; }`.  
**Lesson:** JumpTableAnalyzer today mostly catches **memory-loaded** tables, not **PC-relative Duff / bgez ladders**. Expect hand fixes or analyzer work for Spyro-era ND code.

### 2.6 After that: playable

Map and levels ran; first level completable; second level reachable. Remaining issues are polish / wider testing — not “won’t boot.”

---

## 3. Frame timing (the big “everything is too fast” bug)

Crash is **not** a dumb “1 logic tick per display frame with no dt” engine. ND used estimated frame time (ticks) for velocity/accel, and the **game loop pads to ~30 fps**:

1. `VSync(0)`
2. If `ticks_elapsed - stamp < 25`, call `VSync(0)` again

### What we got wrong

`CrashBandicoot.json` renamed **`0x8003E638`** to `VSync`. That address is **not** the public PsyQ `VSync` entry (`0x8003E4F0`). It is an **internal wait**: busy-wait until vblank counter `0x800549F0 >= A0`.

Early HLE did roughly:

- always `PresentFrame()` + throttle at 60 Hz  
- always add **34** to `ticks_elapsed`

Effects:

- Wait that should be a no-op still presented a frame  
- One real `VSync(0)` often presented **twice** (two internal waits)  
- **34 ≥ 25** ⇒ the game’s second pad `VSync` never ran ⇒ **~60 logic Hz**  
- Physics/animation saw “full frame” deltas at 60 Hz ⇒ **~2× speed**

### What works

HLE the wait helper properly:

- Only present/throttle while `count < target`  
- Increment `0x800549F0` once per waited vblank  
- Add **~17** ticks per vblank (one ~1/60 s unit; two pads ≈ 34, and `delta < 25` can still fire)

`FrameClock` stays **60 Hz per vblank**; Crash’s own loop restores **~30 Hz** gameplay.

**Lesson for Spyro / next ND title:** Identify the *real* `VSync` entry vs wait helper; sync the SDK vblank counter; never invent a tick quantum that skips the game’s own frame pad.

---

## 4. Config / patches that mattered

`CrashBandicoot.json` highlights:

| Address    | Role |
|-----------|------|
| `8003E638` | Wait-vblank helper → `LibEtc.VSync` HLE (name is historical) |
| `8004025C` | `DrawSync` |
| GPU / CD symbols | HLE entry points |
| Patch `80033FBC` | → `LibMem.RMemcpy` |

Hand edits in `CrashBandicoot.Recompiled/main.cs` (decompressor merge, Duff switches, jump tables) are **not** regenerated safely until the recompiler grows:

- function merge on cross-boundary branches  
- Duff / `bgez $zero` table emission  
- smarter `jr` when RA is a known local continuation  

---

## 5. Useful addresses (SCUS-94900)

| VA | Name / use |
|----|------------|
| `0x80034520` | `ticks_elapsed` |
| `0x80060E04` | `frames_elapsed` (approx; confirm if regenerating) |
| `0x800618D4` | `title_state` |
| `0x8005E71C` | `pads[0]` |
| `0x800549F0` | PsyQ vblank counter (wait target) |
| `0x8003E4F0` | Retail / PsyQ-style `VSync(mode)` wrapper |
| `0x8003E638` | Internal wait-until-count |
| `0x80033FBC` | Halfword memcpy (Duff) → HLE |
| `0x800334A0` / `0x80033878` | Decompress (must stay one control-flow graph) |
| `0x80011FC4` | Game loop (see CBHacks wiki) |

---

## 6. Run / rebuild (dev)

From repo root (disc must be present locally; gitignored):

```powershell
dotnet run --project CrashBandicoot.Recomp -c Release -- "Crash Bandicoot.cue"
```

After JSON / recompiler changes:

```powershell
dotnet run --project RecompOne.Recompiler -c Release -- CrashBandicoot.json
dotnet build CrashBandicoot.Recomp -c Release
```

---

## 7. Playbook for the next game (e.g. Spyro)

1. **Identify EXE + overlays**; get a symbol map / ELF if any; fill `Game.json` with SDK renames (`VSync`, `DrawSync`, `Cd*`, GPU).
2. **Boot until first unmapped call** — fix in priority order: BIOS/pad → CD → GPU present → SDK wait/VSync → memcpy/Duff → jump tables.
3. **Dump timing globals** if menus “freeze” with live input.
4. **Assume ND code uses Duff + inline `bgez` tables**; budget HLE or post-pass fixes.
5. **Do not trust function bounds** from linear sweep alone when you see mid-function Calls.
6. **Prove frame rate** with the game’s own pad logic (second VSync / tick threshold), not only the host FPS counter.
7. Keep **hand patches listed** in the game JSON + a short “regen will clobber X” note next to edited regions in `main.cs`.

---

## 8. Legal / distribution reminder

Ship **recompiled code + runtime + launcher**. Users supply **their own** dumped `.cue`/`.bin`. Never commit or redistribute retail disc images.

---

## 9. Outcome

RecompOne + targeted HLE/patches is enough to **play** Crash 1 end-to-end through early levels at correct pacing. The remaining work is hardening the recompiler (so Spyro does not need the same manual surgery) and packaging a consumer host that picks a disc and runs without a CLI.
