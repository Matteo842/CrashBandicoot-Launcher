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
Crash Bandicoot title screen. The touch overlay supports the D-pad, all four face
buttons, L1/L2/R1/R2, Start, Select, diagonals, sliding between directions, and
simultaneous direction/action presses. Audio and settings are the next runtime
milestones. No retail disc or generated game output is bundled.

Build with JDK 21 (the Android Studio JDK 25 can still be used by the Gradle app):

```powershell
dotnet build -f net10.0-android `
  -p:AndroidSdkDirectory="$env:LOCALAPPDATA\Android\Sdk" `
  -p:JavaSdkDirectory="$env:JAVA_HOME"
```
