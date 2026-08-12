# Unified Android app

This is the single distributable Android application. The launcher UI, persisted
Storage Access Framework folder selection, on-device recompiler, and game runtime
all live in this .NET-for-Android project and are packaged into one APK.

Current path:

1. select a folder containing a legal single-file SCUS-94900 CUE/BIN dump;
2. copy it to app-private storage through Android's document provider;
3. validate, recompile, and compile the generated game assembly on device;
4. load that assembly through the existing runtime;
5. show the software PS1 VRAM in an Android `ImageView`;
6. drive player one through a multitouch PlayStation controller overlay.

The complete path above has been validated on a physical arm64 device through the
Crash Bandicoot title screen. The multitouch overlay uses a floating eight-way joystick
whose origin is fixed for the duration of each touch, alongside all four face buttons,
Start, and Select. Its extended travel provides stable directional precision and supports
continuous movement and simultaneous direction/action presses. The overlay can be disabled completely for physical-controller
play; its opacity, size, color/neutral appearance, and control-group positions are saved
per device and can be edited from the launcher's Controls screen. L1/L2/R1/R2 are
hidden by default for Crash Bandicoot but can be enabled from the same screen.
The Settings screen mirrors the desktop launcher controls: master volume/mute,
fullscreen, widescreen, internal resolution, texture filter and strength, dedither,
and dejitter. Android fullscreen, software-present filtering, widescreen output,
dedither, and software dejitter are applied by the current host; GPU-only enhancement
values are persisted for the future Android GPU renderer.
Gameplay uses optional Android immersive fullscreen and restores hidden system bars only
transiently when the user explicitly swipes for them.
The launcher GPU Lab runs without a disc and records the GLES vendor/renderer,
extensions, active EXT/ARM/fallback framebuffer-fetch path, texture-barrier and
QCOM shading-rate support, thermal status, and a fixed synthetic 1x/2x/4x/8x
workload. Results and real-game frame-time percentiles are saved as a shareable
JSON report. Mali devices use the distinct `GL_ARM_shader_framebuffer_fetch`
shader syntax rather than being treated as EXT-compatible.
For renderer validation over ADB, launch the activity with the string extra
`gpu_framebuffer_fetch` set to `ext`, `arm`, or `fallback`; unsupported forced
paths safely fall back to automatic detection.

Firebase Test Lab can launch scenario 1 through
`com.google.intent.action.TEST_LOOP`. This ROM-free game loop runs the same
1x/2x/4x/8x GPU workload without interaction, writes driver details, the active
shader path, thermal state, throughput, and frame-time percentiles to Test
Lab's result URI, then closes the activity. It is labelled `gpu_compatibility`
and `performance`; no game disc is uploaded.

Audio is the next runtime milestone. No retail disc or generated game output is bundled.

Build with JDK 21 (the Android Studio JDK 25 can still be used by the Gradle app):

```powershell
dotnet build -f net10.0-android `
  -p:AndroidSdkDirectory="$env:LOCALAPPDATA\Android\Sdk" `
  -p:JavaSdkDirectory="$env:JAVA_HOME"
```
