# UI strategy: CSS, Linux, and RmlUi

## Priority (locked)

**Keep full CSS via system WebView** (same `index.html`), and prepare for **native Linux** later.

| Choice | Decision |
|---|---|
| CSS fidelity | Keep Chrome/WebKit CSS — do **not** port to RmlUi RCSS |
| Cross-platform path | System WebView: WebView2 (Windows) → WebKitGTK / Photino (Linux) |
| Low-RAM alternatives | Deferred: Sciter (licensed) or RmlUi only if RAM becomes a hard requirement |

There is no free engine that gives 100% CSS without a browser. “Less RAM than Electron” still means a WebView (system Edge/WebKit), not RmlUi.

## Why not RmlUi (production)

RmlUi is a lightweight C++ RCSS engine (great for in-game HUDs). It would force:

- A native C++ host + renderer
- Rewriting `index.html` into RML/RCSS (not 1:1 with current CSS)
- Losing JS and real CSS for little gain on a .NET launcher

**Do not migrate production UI to RmlUi** unless product priorities flip to minimum RAM over CSS fidelity.

## Architecture

Host logic talks to [`ILauncherUi`](ILauncherUi.cs) over the existing JSON protocol (`ready`, `start`, `pickDisc`, `state`, `prepare`, …).

- **Today:** [`WebView2LauncherUi`](WebView2LauncherUi.cs) inside WinForms [`LauncherHost`](../LauncherHost.cs)
- **Linux (next):** implement `ILauncherUi` with Photino / WebKitGTK; reuse the same `index.html` + fonts/assets
- WinForms-only pieces (HWND game embed, some dialogs) stay Windows until Runtime embedding is abstracted separately

```text
Ui/index.html  ←→  ILauncherUi (JSON)  ←→  LauncherHost / game logic
                      │
          ┌───────────┴───────────┐
          │ Windows: WebView2     │
          │ Linux: Photino later  │
          └───────────────────────┘
```

## Linux follow-up (not in this change)

1. Add Photino.NET (or PhotinoX) for a Linux RID
2. Implement `PhotinoLauncherUi : ILauncherUi` (or a top-level Photino window that owns the same bridge)
3. Keep posting/receiving the same JSON shapes — no HTML rewrite
4. Split embedded OpenGL HWND parenting from WinForms when Runtime supports a Linux parent window
