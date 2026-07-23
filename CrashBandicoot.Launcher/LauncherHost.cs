using System.Text.Json;
using CrashBandicoot.Launcher.Recomp;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using NativeFileDialogSharp;
using RecompOne.Runtime.Config;

namespace CrashBandicoot.Launcher;

public sealed record LaunchRequest(string DllPath, string CuePath);

public sealed class LauncherHost : Form
{
    public const string AppVersion = "0.1.0";

    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public LaunchRequest? Launch { get; private set; }

    public LauncherHost()
    {
        Text = "Crash Bandicoot: Recompiled";
        // Fixed size: ~20% narrower than the previous 1280×720 default.
        ClientSize = new Size(1024, 720);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(6, 16, 24);

        Controls.Add(_web);
        Load += async (_, _) => await InitAsync();
        FormClosing += (_, e) =>
        {
            // If we already decided to launch, allow close.
            if (Launch != null) return;
        };
    }

    async Task InitAsync()
    {
        ConfigManager.Load();
        var env = await CoreWebView2Environment.CreateAsync();
        await _web.EnsureCoreWebView2Async(env);

        _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
        _web.CoreWebView2.Settings.AreDevToolsEnabled = true;

        _web.CoreWebView2.WebMessageReceived += OnWebMessage;

        var uiDir = Path.Combine(AppContext.BaseDirectory, "Ui");
        var index = Path.Combine(uiDir, "index.html");
        if (!File.Exists(index))
            throw new FileNotFoundException("Launcher UI missing: " + index);

        _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "crash.launcher",
            uiDir,
            CoreWebView2HostResourceAccessKind.Allow);
        _web.CoreWebView2.Navigate("https://crash.launcher/index.html");
    }

    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) return;
            var type = typeEl.GetString() ?? "";

            switch (type)
            {
                case "ready":
                case "getState":
                    PushState();
                    break;
                case "pickDisc":
                    PickDisc();
                    break;
                case "start":
                    _ = StartGameAsync();
                    break;
                case "exit":
                    Launch = null;
                    BeginInvoke(Close);
                    break;
                case "openMods":
                    OpenModsFolder();
                    break;
                case "saveControls":
                    SaveControls(root);
                    break;
                case "saveSettings":
                    SaveSettings(root);
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
        string status;
        string kind;

        if (string.IsNullOrWhiteSpace(cue) || !File.Exists(cue))
        {
            status = "No disc yet — select your Crash Bandicoot .cue (keep the .bin beside it).";
            kind = "";
        }
        else
        {
            var v = DiscValidator.Validate(cue);
            if (!v.Ok)
            {
                status = v.Message;
                kind = "error";
            }
            else if (GameCache.TryGetValid(v.Fingerprint, out _))
            {
                status = "Ready — Start Game launches instantly.";
                kind = "ok";
            }
            else
            {
                status = "Disc OK — first Start prepares the game on this PC (one-time).";
                kind = "ok";
            }
        }

        var k = ConfigManager.Game.Keys;
        var state = new
        {
            version = AppVersion,
            status,
            statusKind = kind,
            discName,
            masterVolume = ConfigManager.Game.MasterVolume,
            muted = ConfigManager.Game.Muted,
            fullscreen = ConfigManager.View.Fullscreen,
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
            },
        };

        Post(new { type = "state", state });
    }

    void PickDisc()
    {
        var pick = Dialog.FileOpen("cue");
        if (!pick.IsOk || string.IsNullOrWhiteSpace(pick.Path)) return;

        var path = Path.GetFullPath(pick.Path);
        var v = DiscValidator.Validate(path);
        if (!v.Ok)
        {
            PostError(v.Message);
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
                    PostError("Select your Crash Bandicoot .cue first.");
                    return;
                }
            }

            var v = DiscValidator.Validate(cue);
            if (!v.Ok)
            {
                PostError(v.Message);
                return;
            }

            ConfigManager.Game.CdPath = v.CuePath;
            ConfigManager.SaveGame();

            string dllPath;
            if (GameCache.TryGetValid(v.Fingerprint, out var cached) && File.Exists(cached))
            {
                dllPath = cached;
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
                PostError("Prepared game DLL missing. Try Start again.");
                return;
            }

            Launch = new LaunchRequest(dllPath, v.CuePath);
            BeginInvoke(Close);
        }
        catch (Exception ex)
        {
            Post(new { type = "prepareDone" });
            PostError(Unwrap(ex));
            PushState();
        }
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
        ConfigManager.SaveGame();
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

        ConfigManager.SaveGame();
        try { ConfigManager.SaveView(Array.Empty<RecompOne.Runtime.Host.Window.IPanel>()); }
        catch { /* optional */ }
        PushState();
    }

    static void OpenModsFolder()
    {
        var modsDir = Path.Combine(AppContext.BaseDirectory, "mods");
        Directory.CreateDirectory(modsDir);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = modsDir,
                UseShellExecute = true,
            });
        }
        catch
        {
            // ignore
        }
    }

    void Post(object payload)
    {
        if (_web.CoreWebView2 == null) return;
        var json = JsonSerializer.Serialize(payload, _json);
        _web.CoreWebView2.PostWebMessageAsJson(json);
    }

    void PostError(string message) => Post(new { type = "error", message });
}
