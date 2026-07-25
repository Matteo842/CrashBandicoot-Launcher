# UI strategy: native WinForms

## Priority (locked)

**Windows launcher UI** is native WinForms (`NativeLauncherUi`) — GDI+ painted menu, same JSON protocol with `LauncherHost`.

Assets under `Ui/`: fonts (Bungee, Nunito) and `world_map.png`.

```text
NativeLauncherUi  ←→  ILauncherUi (JSON)  ←→  LauncherHost / game logic
```
