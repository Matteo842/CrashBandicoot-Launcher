# Crash Bandicoot Launcher — Android foundation

This directory is a standalone Android Studio project inside the main repository.
It does not alter or replace the Windows/Linux launcher.

## Open it

Open `AndroidLauncher` (this directory) in Android Studio. Do not open the repository
root as an Android project.

The project is currently tested with:

- Android Studio 2026.1.3 (Quail 3)
- Android Gradle Plugin 9.0.1
- Gradle 9.1.0
- `compileSdk` / `targetSdk` 36
- `minSdk` 26 (Android 8.0)
- Android Studio bundled JDK 25 (JDK 17 or newer is supported)

Android Studio may offer a newer AGP through the Upgrade Assistant. It is not
required for this foundation build; upgrade the wrapper and plugin together only
when a new Android feature actually requires it.

`local.properties` is intentionally not committed. Android Studio creates it with the
local SDK path on first sync.

## What works now

- Native landscape launcher shell with the visual direction of the desktop launcher.
- Android Storage Access Framework folder picker; no broad storage permission.
- Persisted access to the user-selected disc folder across restarts.
- Initial `.cue` syntax, matching `.bin`, and minimum-size checks.
- Shared brand fonts, catalog data, recompiler configuration, and patches are copied
  from their authoritative repository locations into build output by Gradle.
- `Start Game` launches the separately installable `AndroidRuntimeHost` preview and
  grants it temporary access to the already selected disc folder.
- The complete launcher-to-runtime path has been validated on a physical arm64
  device through on-device recompilation and the first rendered game frame.

## Runtime preview

`../AndroidRuntimeHost` now carries the existing managed CPU/GPU/CD core through
.NET for Android. It validates SCUS-94900, recompiles and compiles the generated game
assembly on the device, loads it, and displays software VRAM through an Android view.
Install both debug APKs while this boundary is being tested; they use different
application ids and can coexist.

The remaining milestones are audio, controller/touch input, an OpenGL ES renderer,
and finally merging the runtime into one distributable APK. The preview
intentionally contains no retail disc or generated game output.

## Legal boundary

No retail disc, extracted game files, generated game source, or `game.recomp.dll` may
be committed or bundled. The user must supply their own legal BIN/CUE dump. The root
`.gitignore` applies the same safety rules to Android artifacts.
