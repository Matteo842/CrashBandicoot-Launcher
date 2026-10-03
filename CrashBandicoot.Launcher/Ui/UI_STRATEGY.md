# UI strategy: native WinForms

## Priority (locked)

**Windows launcher UI** is native WinForms (`NativeLauncherUi`) — GDI+ painted menu, same JSON protocol with `LauncherHost`.

**Linux launcher UI** is Avalonia (`Linux/`, X11 / XWayland, software rendering): the same painted menu and sheets ported from `NativeLauncherUi`, talking to the config directly (no JSON protocol). The game runs as a child process (`--run <disc> --from-launcher`) in its own Silk window while the launcher is hidden. Without an X11 display the binary starts the game directly. CLI (`--prepare` / `--run`) still works. Keep the two launchers visually in sync when one changes.

Assets under `Ui/`: fonts (Bungee, Nunito) and `world_map.png`.

```text
NativeLauncherUi  ←→  ILauncherUi (JSON)  ←→  LauncherHost / game logic
```
