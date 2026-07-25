using System.Text.Json;
using CrashBandicoot.Launcher.Recomp;
using CrashBandicoot.Launcher.Ui;
using NativeFileDialogSharp;
using RecompOne.Runtime;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Host.Cheats;

namespace CrashBandicoot.Launcher;

public sealed class LauncherHost : Form
{
    public const string AppVersion = "1.3.0";

    readonly ILauncherUi _ui = LauncherUiFactory.Create();
    readonly Panel _gameHost = new()
    {
        Dock = DockStyle.Fill,
        Visible = false,
        BackColor = Color.Black,
    };
    readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    bool _inGame;
    Size _menuClientSize;
    bool _shellFullscreen;
    Rectangle _preFsBounds;
    FormBorderStyle _preFsBorder = FormBorderStyle.Sizable;

    public LauncherHost()
    {
        Text = "Crash Bandicoot: Recompiled";
        // Fixed size: ~20% narrower than the previous 1280×720 default.
        ClientSize = new Size(1024, 720);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(6, 16, 24);
        TryApplyAppIcon();

        Controls.Add(_gameHost);
        if (_ui.AsControl != null)
            Controls.Add(_ui.AsControl);
        else
            throw new InvalidOperationException(
                "This WinForms host requires ILauncherUi.AsControl.");

        Runtime.SetHostFullscreenHandler(ApplyShellFullscreen);
        _gameHost.Resize += (_, _) =>
        {
            if (_inGame) Runtime.FitEmbeddedWindow();
        };
        Load += async (_, _) => await InitAsync();
        FormClosed += (_, _) =>
        {
            Runtime.SetHostFullscreenHandler(null);
            _ui.Dispose();
        };
        FormClosing += (_, e) =>
        {
            // While the game loop is pumping via DoEvents, allow close to tear down the child HWND.
            if (_inGame) return;
        };
    }

    void TryApplyAppIcon()
    {
        try
        {
            // Prefer the exe ApplicationIcon (set via app.ico at build time).
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                using var extracted = Icon.ExtractAssociatedIcon(exe);
                if (extracted != null)
                {
                    Icon = (Icon)extracted.Clone();
                    return;
                }
            }
        }
        catch
        {
            // fall through
        }

        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(ico))
                Icon = new Icon(ico);
        }
        catch
        {
            // keep WinForms default
        }
    }

    async Task InitAsync()
    {
        ConfigManager.Load();
        _ui.MessageReceived += OnUiMessage;
        var uiDir = Path.Combine(AppContext.BaseDirectory, "Ui");
        await _ui.InitializeAsync(uiDir);
    }

    void OnUiMessage(object? sender, LauncherUiMessageEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.Json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) return;
            var type = typeEl.GetString() ?? "";

            switch (type)
            {
                case "ready":
                case "getState":
                    PushState();
                    if (type == "ready" && RecompOne.Runtime.AppPaths.IsOnDesktop)
                        PostDesktopWarning();
                    break;
                case "pickDisc":
                    PickDisc();
                    break;
                case "start":
                    _ = StartGameAsync();
                    break;
                case "exit":
                    BeginInvoke(Close);
                    break;
                case "openMods":
                    // Mods menu disabled for 1.x — ignored on purpose.
                    break;
                case "saveControls":
                    SaveControls(root);
                    break;
                case "saveSettings":
                    SaveSettings(root);
                    break;
                case "saveCheats":
                    SaveCheats(root);
                    break;
            }
        }
        catch (Exception ex)
        {
            PostError(ex.Message);
        }
    }

    void PushState()
    {
        var cue = ConfigManager.Game.CdPath;
        var discName = string.IsNullOrWhiteSpace(cue) ? "" : Path.GetFileName(cue);
        var discPath = string.IsNullOrWhiteSpace(cue) ? "" : cue;
        string status = "";
        string kind = "";
        var canStart = false;

        if (!string.IsNullOrWhiteSpace(cue) && File.Exists(cue))
        {
            var v = DiscValidator.Validate(cue);
            if (!v.Ok)
            {
                status = v.Message;
                kind = "error";
                // Stale / broken path in settings.json — do not keep Start enabled.
                ClearConfiguredDisc();
                discName = "";
                discPath = "";
            }
            else
            {
                canStart = true;
                // Keep footer clean: ready state is the enabled Start button, not a green status line.
            }
        }
        else if (!string.IsNullOrWhiteSpace(cue) && !File.Exists(cue))
        {
            status = "Configured disc path is missing. Select your .cue again.";
            kind = "error";
            ClearConfiguredDisc();
            discName = "";
            discPath = "";
        }

        var k = ConfigManager.Game.Keys;
        var state = new
        {
            version = AppVersion,
            status,
            statusKind = kind,
            canStart,
            discName,
            discPath,
            masterVolume = ConfigManager.Game.MasterVolume,
            muted = ConfigManager.Game.Muted,
            fullscreen = ConfigManager.View.Fullscreen,
            widescreen = ConfigManager.View.Widescreen,
            internalResolution = ConfigManager.View.InternalResolution,
            textureFilter = ConfigManager.View.TextureFilter,
            textureFilterStrength = ConfigManager.View.TextureFilterStrength,
            dedither = ConfigManager.View.Dedither,
            dejitter = ConfigManager.View.Dejitter,
            infiniteLives = CheatConfig.InfiniteLives,
            levelSelect = CheatConfig.LevelSelect,
            mods = ConfigManager.Game.ActiveMods,
            keys = new Dictionary<string, string>
            {
                ["cross"] = k.Cross,
                ["circle"] = k.Circle,
                ["square"] = k.Square,
                ["triangle"] = k.Triangle,
                ["start"] = k.Start,
                ["select"] = k.Select,
                ["up"] = k.Up,
                ["down"] = k.Down,
                ["left"] = k.Left,
                ["right"] = k.Right,
                ["cheatMenu"] = ConfigManager.View.CheatMenuKey,
            },
        };

        Post(new { type = "state", state });
    }

    static void ClearConfiguredDisc()
    {
        if (string.IsNullOrWhiteSpace(ConfigManager.Game.CdPath)) return;
        ConfigManager.Game.CdPath = "";
        ConfigManager.SaveGame();
    }

    void PickDisc()
    {
        var pick = Dialog.FileOpen("cue");
        if (!pick.IsOk || string.IsNullOrWhiteSpace(pick.Path)) return;

        var path = Path.GetFullPath(pick.Path);
        var previous = ConfigManager.Game.CdPath;
        var v = DiscValidator.Validate(path);
        if (!v.Ok)
        {
            // Critical: do NOT keep the previous valid path. Otherwise Start silently
            // relaunches the old dump from settings.json and it looks like the fake .cue worked.
            ClearConfiguredDisc();
            PostDiscError(v, string.IsNullOrWhiteSpace(previous) ? null : previous);
            PushState();
            return;
        }

        ConfigManager.Game.CdPath = v.CuePath;
        ConfigManager.SaveGame();
        PushState();
    }

    async Task StartGameAsync()
    {
        try
        {
            var cue = ConfigManager.Game.CdPath;
            if (string.IsNullOrWhiteSpace(cue) || !File.Exists(cue))
            {
                PickDisc();
                cue = ConfigManager.Game.CdPath;
                if (string.IsNullOrWhiteSpace(cue) || !File.Exists(cue))
                {
                    PostError(
                        "No disc selected",
                        "Start needs a valid Crash Bandicoot dump.",
                        "Click Select disc (.cue) and choose the .cue that sits next to its .bin.",
                        "pair");
                    PushState();
                    return;
                }
            }

            var v = DiscValidator.Validate(cue);
            if (!v.Ok)
            {
                ClearConfiguredDisc();
                PostDiscError(v);
                PushState();
                return;
            }

            ConfigManager.Game.CdPath = v.CuePath;
            ConfigManager.SaveGame();

            string dllPath;
            if (GameStore.TryGetValid(v.Fingerprint, v.CuePath, out var prepared) && File.Exists(prepared))
            {
                dllPath = prepared;
            }
            else
            {
                Post(new { type = "prepare", fraction = 0.02, detail = "Starting…" });
                var progress = new Progress<PipelineProgress>(p =>
                {
                    try
                    {
                        BeginInvoke(() => Post(new
                        {
                            type = "prepare",
                            fraction = p.Fraction,
                            detail = string.IsNullOrEmpty(p.Detail) ? p.Stage : p.Detail,
                        }));
                    }
                    catch
                    {
                        // form disposing
                    }
                });

                dllPath = await Task.Run(() => RecompPipeline.EnsureReady(v.CuePath, progress));
                Post(new { type = "prepareDone" });
            }

            if (!File.Exists(dllPath))
            {
                PostError(
                    "Prepared game missing",
                    "The compiled game DLL was not found after prepare.",
                    "Press Start again to rebuild from your disc.",
                    "other");
                return;
            }

            // Prepared game only skips recompile — launch still requires a live, matching dump.
            var gate = DiscValidator.EnsureDiscPresentForLaunch(v.CuePath, v.Fingerprint);
            if (!gate.Ok)
            {
                ClearConfiguredDisc();
                PostDiscError(gate);
                PushState();
                return;
            }

            // Run the recompiled game inside this same WinForms window (OpenGL child HWND).
            BeginInvoke(() => RunEmbeddedGame(dllPath, v.CuePath));
        }
        catch (Exception ex)
        {
            Post(new { type = "prepareDone" });
            PostError("Something went wrong", Unwrap(ex), "Try selecting your .cue again, then Start.", "other");
            PushState();
        }
    }

    void RunEmbeddedGame(string dllPath, string cuePath)
    {
        EnterGameMode();
        try
        {
            ConfigManager.Load();
            ConfigManager.Game.CdPath = cuePath;
            ConfigManager.SaveGame();

            // Ensure the panel has a real HWND before parenting the Silk window.
            _gameHost.Visible = true;
            _ = _gameHost.Handle;
            Runtime.SetEmbedParent(_gameHost.Handle);

            Console.WriteLine($"[CrashBandicoot] launching (embedded) {dllPath}");
            GameLoader.Run(dllPath, cuePath);
        }
        catch (Exception ex)
        {
            var msg = Unwrap(ex);
            Console.Error.WriteLine("[CrashBandicoot] launch failed: " + msg);
            MessageBox.Show(
                msg,
                "Crash Bandicoot: Recompiled",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            Runtime.SetEmbedParent(0);
            if (!IsDisposed && IsHandleCreated)
                LeaveGameMode();
        }
    }

    void EnterGameMode()
    {
        _inGame = true;
        _menuClientSize = ClientSize;
        _ui.Visible = false;
        _gameHost.Visible = true;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        if (ClientSize.Width < 1280 || ClientSize.Height < 720)
            ClientSize = new Size(1280, 720);
        // Remember windowed placement so F11 can leave borderless fullscreen cleanly.
        _preFsBounds = Bounds;
        _preFsBorder = FormBorderStyle.Sizable;
        _shellFullscreen = false;
        if (ConfigManager.View.Fullscreen)
            ApplyShellFullscreen(true);
    }

    void LeaveGameMode()
    {
        _inGame = false;
        _shellFullscreen = false;
        WindowState = FormWindowState.Normal;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = _menuClientSize.Width > 0 ? _menuClientSize : new Size(1024, 720);
        _gameHost.Visible = false;
        _ui.Visible = true;
        PushState();
    }

    /// <summary>
    /// Borderless cover of the current monitor. Maximize alone is not fullscreen
    /// (title bar + taskbar remain) — that is what users reported as "broken".
    /// </summary>
    void ApplyShellFullscreen(bool on)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => ApplyShellFullscreen(on));
            return;
        }
        if (!_inGame) return;

        if (on)
        {
            if (!_shellFullscreen)
            {
                if (WindowState == FormWindowState.Normal)
                    _preFsBounds = Bounds;
                _preFsBorder = FormBorderStyle == FormBorderStyle.None
                    ? FormBorderStyle.Sizable
                    : FormBorderStyle;
            }
            _shellFullscreen = true;
            // Normal + None + screen Bounds is the reliable WinForms borderless pattern.
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = Screen.FromControl(this).Bounds;
        }
        else
        {
            // Always leave borderless even if our flag desynced (F11 must exit).
            if (!_shellFullscreen && FormBorderStyle != FormBorderStyle.None)
                return;
            _shellFullscreen = false;
            FormBorderStyle = _preFsBorder == FormBorderStyle.None
                ? FormBorderStyle.Sizable
                : _preFsBorder;
            WindowState = FormWindowState.Normal;
            if (_preFsBounds.Width > 0 && _preFsBounds.Height > 0)
                Bounds = _preFsBounds;
            else
                ClientSize = new Size(1280, 720);
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_inGame)
        {
            if (keyData == Keys.F11 || keyData == (Keys.Alt | Keys.Enter))
            {
                Runtime.RequestFullscreenToggle();
                return true;
            }

            // Embedded OpenGL child often keeps focus off Silk; route cheat hotkey like F11.
            var cheatName = ConfigManager.View.CheatMenuKey;
            if (string.IsNullOrWhiteSpace(cheatName)) cheatName = "F3";
            if (Enum.TryParse<Keys>(cheatName.Trim(), ignoreCase: true, out var cheatKey)
                && keyData == cheatKey)
            {
                Runtime.RequestCheatMenuToggle();
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    static string Unwrap(Exception ex)
    {
        if (ex is AggregateException agg && agg.InnerException != null)
            return Unwrap(agg.InnerException);
        if (ex.InnerException != null)
            return Unwrap(ex.InnerException);
        return ex.Message;
    }

    void SaveControls(JsonElement root)
    {
        if (!root.TryGetProperty("keys", out var keys)) return;
        var k = ConfigManager.Game.Keys;
        string Get(string name, string fallback) =>
            keys.TryGetProperty(name, out var el) ? el.GetString() ?? fallback : fallback;

        k.Cross = Get("cross", k.Cross);
        k.Circle = Get("circle", k.Circle);
        k.Square = Get("square", k.Square);
        k.Triangle = Get("triangle", k.Triangle);
        k.Start = Get("start", k.Start);
        k.Select = Get("select", k.Select);
        k.Up = Get("up", k.Up);
        k.Down = Get("down", k.Down);
        k.Left = Get("left", k.Left);
        k.Right = Get("right", k.Right);
        if (keys.TryGetProperty("cheatMenu", out var cheatKeyEl) ||
            keys.TryGetProperty("miracomando", out cheatKeyEl))
        {
            var cheatKey = cheatKeyEl.GetString();
            if (!string.IsNullOrWhiteSpace(cheatKey))
                ConfigManager.View.CheatMenuKey = cheatKey;
        }
        ConfigManager.SaveGame();
        ConfigManager.SaveView(Array.Empty<RecompOne.Runtime.Host.Window.IPanel>());
        PushState();
    }

    void SaveCheats(JsonElement root)
    {
        if (root.TryGetProperty("infiniteLives", out var lives))
            CheatConfig.InfiniteLives = lives.GetBoolean();
        if (root.TryGetProperty("levelSelect", out var ls))
            CheatConfig.LevelSelect = ls.GetBoolean();
        ConfigManager.SaveView(Array.Empty<RecompOne.Runtime.Host.Window.IPanel>());
        PushState();
    }

    void SaveSettings(JsonElement root)
    {
        if (root.TryGetProperty("masterVolume", out var vol) && vol.TryGetSingle(out var v))
            ConfigManager.Game.MasterVolume = Math.Clamp(v, 0f, 1f);
        if (root.TryGetProperty("muted", out var muted))
            ConfigManager.Game.Muted = muted.GetBoolean();
        if (root.TryGetProperty("fullscreen", out var fs))
            ConfigManager.View.Fullscreen = fs.GetBoolean();
        if (root.TryGetProperty("widescreen", out var ws))
            ConfigManager.View.Widescreen = ws.GetBoolean();
        if (root.TryGetProperty("internalResolution", out var ir) && ir.TryGetInt32(out var scale))
            ConfigManager.View.InternalResolution = scale;
        if (root.TryGetProperty("textureFilter", out var tf) && tf.TryGetInt32(out var filter))
            ConfigManager.View.TextureFilter = filter;
        else if (root.TryGetProperty("bilinearFilter", out var bf))
            ConfigManager.View.TextureFilter = bf.GetBoolean() ? ViewConfig.TextureFilterBilinear : ViewConfig.TextureFilterOff;
        if (root.TryGetProperty("textureFilterStrength", out var tfs) && tfs.TryGetSingle(out var strength))
            ConfigManager.View.TextureFilterStrength = strength;
        if (root.TryGetProperty("dedither", out var dd))
            ConfigManager.View.Dedither = dd.GetBoolean();
        if (root.TryGetProperty("dejitter", out var dj))
            ConfigManager.View.Dejitter = dj.GetBoolean();

        ConfigManager.SaveGame();
        // Persist Fullscreen etc. without touching ImGui layout (empty panel list
        // + no live host). Previously this could rewrite interface.ini with a
        // stale ImGui snapshot after an embedded game session.
        ConfigManager.SaveView(Array.Empty<RecompOne.Runtime.Host.Window.IPanel>());
        PushState();
    }

    void PostDesktopWarning()
    {
        Post(new
        {
            type = "desktopWarning",
            title = "Running from the Desktop",
            message =
                "This launcher creates folders next to the exe (save, game, settings). " +
                "On the Desktop that clutters your workspace and can be slower or messier when Windows syncs Desktop folders.",
            fix =
                "Move CrashBandicoot.exe into its own folder (for example Documents\\CrashBandicoot) and run it from there.",
            path = RecompOne.Runtime.AppPaths.Root,
        });
    }

    void Post(object payload)
    {
        var json = JsonSerializer.Serialize(payload, _json);
        _ui.PostJson(json);
    }

    void PostDiscError(DiscValidation v, string? clearedPrevious = null)
    {
        var fix = v.Fix;
        if (!string.IsNullOrWhiteSpace(clearedPrevious))
            fix += " Your previous disc selection was cleared so Start cannot silently use the old dump.";

        Post(new
        {
            type = "error",
            title = string.IsNullOrWhiteSpace(v.Title) ? "Disc problem" : v.Title,
            message = string.IsNullOrWhiteSpace(v.Problem) ? v.Message : v.Problem,
            fix = fix,
            focus = string.IsNullOrWhiteSpace(v.Focus) ? "other" : v.Focus,
            cuePath = string.IsNullOrWhiteSpace(v.CuePath) ? null : v.CuePath,
            binPath = string.IsNullOrWhiteSpace(v.BinPath) ? null : v.BinPath,
        });
    }

    void PostError(string title, string message, string fix, string focus = "other") =>
        Post(new
        {
            type = "error",
            title,
            message,
            fix,
            focus,
            cuePath = (string?)null,
            binPath = (string?)null,
        });

    void PostError(string message) =>
        PostError("Error", message, "Try again, or select your .cue + .bin dump.", "other");
}
