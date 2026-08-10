# Android runtime preview

This is an isolated .NET-for-Android proof of concept for the real game runtime.
It intentionally uses a different application id from `AndroidLauncher`, so both
APKs can be installed together while the runtime is tested on a physical device.
The launcher passes its selected document-tree URI to this app when `Start Game` is
pressed; this app can also be opened directly and has its own folder picker.

Current path:

1. select a folder containing a legal single-file SCUS-94900 CUE/BIN dump;
2. copy it to app-private storage through Android's document provider;
3. validate, recompile, and compile the generated game assembly on device;
4. load that assembly through the existing runtime;
5. show the software PS1 VRAM in an Android `ImageView`.

The complete path above has been validated on a physical arm64 device through the
Crash Bandicoot title screen. The preview deliberately starts without audio and
controls; those are the next runtime milestones. No retail disc or generated game
output is bundled.

Build with JDK 21 (the Android Studio JDK 25 can still be used by the Gradle app):

```powershell
dotnet build -f net10.0-android `
  -p:AndroidSdkDirectory="$env:LOCALAPPDATA\Android\Sdk" `
  -p:JavaSdkDirectory="$env:JAVA_HOME"
```
