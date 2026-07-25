# UI strategy: native WinForms

## Priority (locked)

**Windows launcher UI** is native WinForms (`NativeLauncherUi`) — GDI+ painted menu, same JSON protocol with `LauncherHost`.

**Linux** has no graphical launcher yet: use CLI (`--prepare` / `--run`) and a standalone Silk game window. Do not rewrite `NativeLauncherUi` for Linux; a future Linux UI would be a separate `ILauncherUi` implementation.

Assets under `Ui/`: fonts (Bungee, Nunito) and `world_map.png`.

```text
NativeLauncherUi  ←→  ILauncherUi (JSON)  ←→  LauncherHost / game logic
```
