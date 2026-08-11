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
Crash Bandicoot title screen. The multitouch overlay supports the D-pad, all four
face buttons, Start, Select, diagonals, sliding between directions, and simultaneous
direction/action presses. The overlay can be disabled completely for physical-controller
play; its opacity, size, color/neutral appearance, and control-group positions are saved
per device and can be edited from the launcher's Controls screen. L1/L2/R1/R2 are
hidden by default for Crash Bandicoot but can be enabled from the same screen.
Gameplay uses Android immersive fullscreen and restores hidden system bars only transiently
when the user explicitly swipes for them.
Audio is the next runtime milestone. No retail disc or generated game output is bundled.

Build with JDK 21 (the Android Studio JDK 25 can still be used by the Gradle app):

```powershell
dotnet build -f net10.0-android `
  -p:AndroidSdkDirectory="$env:LOCALAPPDATA\Android\Sdk" `
  -p:JavaSdkDirectory="$env:JAVA_HOME"
```
