using System.Diagnostics;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CrashBandicoot.Launcher.Recomp;
using NativeFileDialogSharp;
using RecompOne.Runtime;
using RecompOne.Runtime.Config;

namespace CrashBandicoot.Launcher.Linux;

/// <summary>Launcher logic: disc selection, prepare, starting the game. Mirrors <c>LauncherHost</c> on Windows.</summary>
sealed partial class LauncherWindow
{
    bool _busy;
    bool _gameRunning;
    bool _hiddenForGame;
    DispatcherTimer? _hideFallback;

    void InitState()
    {
        ConfigManager.Load();
        AutoSelectNearbyDisc();
        RefreshState();
        if (AppPaths.IsOnDesktop && !DesktopIsHome())
            ShowDesktopWarning();
    }

    /// <summary>Some setups point the XDG desktop folder at $HOME; then "on the Desktop" means nothing.</summary>
    static bool DesktopIsHome()
    {
        var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory).TrimEnd('/');
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('/');
        return desk.Length == 0 || string.Equals(desk, home, StringComparison.Ordinal);
    }

    void OnMenuActivated(int index)
    {
        if (_busy || _gameRunning) return;
        switch (index)
        {
            case 0: _ = StartGameAsync(); break;
            case 1: ShowControls(); break;
            case 2: ShowSettings(); break;
            case 3: ShowMods(); break;
            case 4: ShowCheat(); break;
            case 5: Close(); break;
            case LauncherStage.FocusInfo: ShowAbout(); break;
            case LauncherStage.FocusChip: _ = PickDiscAsync(); break;
        }
    }

    /// <summary>Validate the saved disc and refresh the footer / START GAME state (LauncherHost.PushState).</summary>
    void RefreshState()
    {
        var cue = ConfigManager.Game.CdPath;
        var discPath = string.IsNullOrWhiteSpace(cue) ? "" : cue;
        var status = "";
        var kind = "";
        var canStart = false;

        if (!string.IsNullOrWhiteSpace(cue) && File.Exists(cue))
        {
            var v = DiscValidator.Validate(cue);
            if (!v.Ok)
            {
                status = v.Message;
                kind = "error";
                // Stale / broken path in settings.json: do not keep Start enabled.
                ClearConfiguredDisc();
                discPath = "";
            }
            else
            {
                canStart = true;
            }
        }
        else if (!string.IsNullOrWhiteSpace(cue))
        {
            status = "Configured disc path is missing. Select your .cue or .chd again.";
            kind = "error";
            ClearConfiguredDisc();
            discPath = "";
        }

        _stage.SetState(canStart, status, kind, discPath, LinuxGui.VersionLabel);
    }

    static void ClearConfiguredDisc()
    {
        if (string.IsNullOrWhiteSpace(ConfigManager.Game.CdPath)) return;
        ConfigManager.Game.CdPath = "";
        ConfigManager.SaveGame();
    }

    /// <summary>First start convenience: the only valid .chd/.cue next to the program becomes the disc.</summary>
    static void AutoSelectNearbyDisc()
    {
        var saved = ConfigManager.Game.CdPath;
        if (!string.IsNullOrWhiteSpace(saved) && File.Exists(saved)) return;
        try
        {
            var nearby = Directory.EnumerateFiles(AppPaths.Root)
                .Where(f => f.EndsWith(".chd", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (nearby.Length != 1) return;
            var v = DiscValidator.Validate(nearby[0]);
            if (!v.Ok) return;
            Console.WriteLine($"[Launcher] using disc next to the program: {v.CuePath}");
            ConfigManager.Game.CdPath = v.CuePath;
            ConfigManager.SaveGame();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Launcher] disc lookup failed: {ex.Message}");
        }
    }

    async Task PickDiscAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            await PickAndValidateDiscAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[Launcher] disc selection failed: " + ex);
            ShowErrorSheet("Something went wrong", Unwrap(ex), "Try selecting your .cue or .chd again.", "other");
            RefreshState();
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Ask for a disc and keep it only if it validates. False when cancelled or invalid.</summary>
    async Task<bool> PickAndValidateDiscAsync()
    {
        var picked = await PickDiscPathAsync();
        if (picked == null) return false;

        var path = Path.GetFullPath(picked);
        var previous = ConfigManager.Game.CdPath;
        var v = DiscValidator.Validate(path);
        if (!v.Ok)
        {
            // Do NOT keep the previous valid path, or Start would silently relaunch the old dump.
            ClearConfiguredDisc();
            ShowDiscError(v, string.IsNullOrWhiteSpace(previous) ? null : previous);
            RefreshState();
            return false;
        }

        ConfigManager.Game.CdPath = v.CuePath;
        ConfigManager.SaveGame();
        RefreshState();
        return true;
    }

    /// <summary>Desktop file chooser (portal or GTK through Avalonia, then GTK through nfd).</summary>
    async Task<string?> PickDiscPathAsync()
    {
        try
        {
            if (StorageProvider.CanOpen)
            {
                var start = await StorageProvider.TryGetFolderFromPathAsync(AppPaths.Root);
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select your Crash Bandicoot disc (.chd or .cue)",
                    AllowMultiple = false,
                    SuggestedStartLocation = start,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("Crash Bandicoot disc (.chd, .cue)")
                        {
                            Patterns = ["*.chd", "*.cue", "*.CHD", "*.CUE"],
                        },
                        FilePickerFileTypes.All,
                    ],
                });
                return files.Count > 0 ? files[0].TryGetLocalPath() : null;
            }
            Console.Error.WriteLine("[Launcher] no system file picker (portal / GTK), trying nfd");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Launcher] file picker failed: {ex.GetBaseException().Message}");
        }

        try
        {
            var pick = Dialog.FileOpen("cue,chd", AppPaths.Root);
            if (pick.IsOk && !string.IsNullOrWhiteSpace(pick.Path))
                return pick.Path;
            if (!pick.IsError)
                return null;
            Console.Error.WriteLine($"[Launcher] nfd error: {pick.ErrorMessage}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Launcher] nfd unavailable: {ex.GetBaseException().Message}");
        }

        ShowErrorSheet(
            "No file picker",
            "This desktop has no file chooser the launcher can use (xdg-desktop-portal or GTK 3).",
            "Put your .chd (or .cue + .bin) in the same folder as the program and start it again, " +
            "or run it from a terminal with --run /path/to/game.chd.",
            "other");
        return null;
    }

    async Task StartGameAsync()
    {
        if (_busy || _gameRunning) return;
        _busy = true;
        try
        {
            var cue = ConfigManager.Game.CdPath;
            if (string.IsNullOrWhiteSpace(cue) || !File.Exists(cue))
            {
                if (!await PickAndValidateDiscAsync())
                {
                    if (!_sheetHost.IsVisible)
                        ShowErrorSheet(
                            "No disc selected",
                            "Start needs a valid Crash Bandicoot dump.",
                            "Click Select disc and choose your .chd or the .cue next to its .bin.",
                            "pair");
                    RefreshState();
                    return;
                }
                cue = ConfigManager.Game.CdPath;
            }

            var v = DiscValidator.Validate(cue);
            if (!v.Ok)
            {
                ClearConfiguredDisc();
                ShowDiscError(v);
                RefreshState();
                return;
            }

            ConfigManager.Game.CdPath = v.CuePath;
            ConfigManager.SaveGame();

            if (!GameStore.TryGetValid(v.Fingerprint, v.CuePath, out var dllPath) || !File.Exists(dllPath))
            {
                ShowPrep("Preparing game", "Starting…", 0.02);
                // Created on the UI thread, so reports are posted back to it.
                var progress = new Progress<PipelineProgress>(p =>
                    ShowPrep("Preparing game", string.IsNullOrEmpty(p.Detail) ? p.Stage : p.Detail, p.Fraction));
                dllPath = await Task.Run(() => RecompPipeline.EnsureReady(v.CuePath, progress));
                HidePrep();
                // Roslyn leaves a lot of garbage behind; give it back before the game starts.
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }

            if (!File.Exists(dllPath))
            {
                ShowErrorSheet(
                    "Prepared game missing",
                    "The compiled game DLL was not found after prepare.",
                    "Press Start again to rebuild from your disc.",
                    "other");
                return;
            }

            // A prepared game only skips the recompile: launch still needs the matching dump.
            var gate = DiscValidator.EnsureDiscPresentForLaunch(v.CuePath, v.Fingerprint);
            if (!gate.Ok)
            {
                ClearConfiguredDisc();
                ShowDiscError(gate);
                RefreshState();
                return;
            }

            LaunchGame(v.CuePath);
        }
        catch (Exception ex)
        {
            HidePrep();
            Console.Error.WriteLine("[Launcher] start failed: " + ex);
            ShowErrorSheet("Something went wrong", Unwrap(ex),
                "Try selecting your .cue or .chd again, then Start.", "other");
            RefreshState();
        }
        finally
        {
            _busy = false;
        }
    }

    void LaunchGame(string cuePath)
    {
        KeyCaptureBox.CancelActive();
        CloseSheet();
        _gameRunning = true;
        _hiddenForGame = false;
        // Free the pad so the game is the only one reading it.
        _pad.Release();
        LinuxLaunch.RestartLog($"game started from the launcher: {cuePath}");
        ShowPrep("Starting game", "Opening the game window…", 0, indeterminate: true,
            note: "The launcher comes back when you close the game.");

        try
        {
            GameSession.Start(cuePath,
                onWindowReady: () => Dispatcher.UIThread.Post(HideForGame),
                onExited: s => Dispatcher.UIThread.Post(() => OnGameExited(s)));
        }
        catch (Exception ex)
        {
            _gameRunning = false;
            HidePrep();
            Console.Error.WriteLine("[Launcher] could not start the game: " + ex);
            ShowErrorSheet("Game could not start", ex.Message,
                "Details are in logs/last-run.txt next to the program.", "other");
            return;
        }

        // In case the window-ready line never shows up, step aside anyway.
        _hideFallback = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Normal, (_, _) => HideForGame());
        _hideFallback.Start();
    }

    void HideForGame()
    {
        _hideFallback?.Stop();
        _hideFallback = null;
        if (!_gameRunning || _hiddenForGame) return;
        _hiddenForGame = true;
        HidePrep();
        _anim.Stop();
        Hide();
    }

    void OnGameExited(GameSession session)
    {
        _hideFallback?.Stop();
        _hideFallback = null;
        _gameRunning = false;
        HidePrep();
        if (_hiddenForGame)
        {
            _hiddenForGame = false;
            Show();
            Activate();
            _anim.Start();
        }
        _stage.Focus();

        // The game saves its own settings (in-game menus, FPS hotkeys): pick them up.
        ConfigManager.Load();
        RefreshState();

        if (session.ExitCode != 0)
            ShowGameError(session);
    }

    void ShowGameError(GameSession s)
    {
        var code = s.ExitCode;
        var reason = s.Failure;
        if (string.IsNullOrWhiteSpace(reason))
        {
            reason = code > 128 && code < 160
                ? $"The game process stopped with signal {code - 128} (exit code {code})."
                : $"The game process exited with code {code}.";
        }

        var title = s.WindowOpened ? "The game closed unexpectedly" : "Game could not start";
        var fix = s.WindowOpened
            ? "Details are in logs/last-run.txt next to the program. Please attach it to your bug report."
            : "The game needs an OpenGL 4.3 GPU driver (Mesa, NVIDIA or AMD). Details are in logs/last-run.txt next to the program.";
        ShowErrorSheet(title, reason, fix, "other", logsButton: true);
    }

    static string Unwrap(Exception ex)
    {
        while (true)
        {
            if (ex is AggregateException { InnerException: { } agg })
            {
                ex = agg;
                continue;
            }
            if (ex.InnerException != null)
            {
                ex = ex.InnerException;
                continue;
            }
            return ex.Message;
        }
    }

    /// <summary>Open a folder in the desktop file manager.</summary>
    void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        try
        {
            var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            psi.ArgumentList.Add(dir);
            using var _ = Process.Start(psi);
            return;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Launcher] xdg-open failed: {ex.Message}");
        }

        try
        {
            _ = Launcher.LaunchUriAsync(new Uri(dir.EndsWith('/') ? dir : dir + "/"));
        }
        catch
        {
            // nothing else to try
        }
    }
}
