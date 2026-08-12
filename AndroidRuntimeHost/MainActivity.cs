using System.Text.RegularExpressions;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.Database;
using Android.Net;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using CrashBandicoot.Launcher.Recomp;
using RecompOne.Runtime;
using RecompOne.Runtime.Config;
using Color = Android.Graphics.Color;

namespace CrashBandicoot.AndroidRuntime;

[Activity(
    Label = "Crash Bandicoot Launcher",
    MainLauncher = true,
    Exported = true,
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize |
                           ConfigChanges.KeyboardHidden | ConfigChanges.UiMode)]
[IntentFilter(
    new[] { FirebaseGameLoopRunner.Action },
    Categories = new[] { Intent.CategoryDefault },
    DataMimeType = "application/javascript")]
public sealed class MainActivity : Activity
{
    const int PickDiscFolderRequest = 949;
    const string DiscTreePreference = "disc_tree";

    LauncherScreen? _launcher;
    TextView _status = null!;
    TextureView _screen = null!;
    ProgressBar _progress = null!;
    FrameLayout.LayoutParams? _statusLayout;
    DevMenuOverlay? _devMenu;
    TextView? _devHud;
    Android.Net.Uri? _treeUri;
    DiscDocuments? _disc;
    bool _usingLocalDiscCopy;
    bool _gameUiVisible;
    FirebaseGameLoopRunner? _firebaseGameLoop;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        RequestedOrientation = ScreenOrientation.SensorLandscape;
        InitializeRuntimeConfiguration();
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        if (Window != null)
        {
            Window.SetStatusBarColor(Color.Rgb(6, 16, 24));
            Window.SetNavigationBarColor(Color.Rgb(6, 16, 24));
            Window.DecorView.SystemUiFlags = SystemUiFlags.LayoutStable |
                                             SystemUiFlags.LayoutHideNavigation |
                                             SystemUiFlags.ImmersiveSticky;
        }

        if (FirebaseGameLoopRunner.IsRequested(Intent))
        {
            _firebaseGameLoop = new FirebaseGameLoopRunner(this, Intent!);
            _firebaseGameLoop.Start();
            return;
        }

        ShowLauncherUi();

        var saved = GetPreferences(FileCreationMode.Private).GetString(DiscTreePreference, null);
        if (!string.IsNullOrWhiteSpace(saved))
        {
            _treeUri = Android.Net.Uri.Parse(saved);
            ScanSelectedFolder();
        }
    }

    void ShowLauncherUi()
    {
        _gameUiVisible = false;
        _devMenu = null;
        _devHud = null;
        _launcher = new LauncherScreen(this);
        _launcher.SelectDiscRequested += PickDiscFolder;
        _launcher.StartGameRequested += () => _ = StartGameAsync();
        _launcher.SettingsRequested += () => SettingsDialog.Show(this, ApplyGameDisplayMode);
        _launcher.GpuLabRequested += () => GpuLabDialog.Show(this);
        SetContentView(_launcher);

        if (_disc != null)
            ShowDiscReady();
        else if (_usingLocalDiscCopy)
            TryUseLocalDiscCopy();
    }

    void ShowGameUi()
    {
        _gameUiVisible = true;
        ApplyGameDisplayMode();
        RecompOne.Runtime.Hardware.Controller.SetVirtualPadState(0);
        var root = new GameTouchRoot(this);
        root.SetBackgroundColor(Color.Rgb(7, 11, 18));

        _screen = new TextureView(this);
        _screen.LayoutChange += (_, e) =>
        {
            var width = e.Right - e.Left;
            var height = e.Bottom - e.Top;
            if (width > 0 && height > 0)
            {
                _screen.SurfaceTexture?.SetDefaultBufferSize(width, height);
                _activeHost?.NotifySurfaceSize(width, height);
            }
        };
        root.AddView(_screen, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        var touchSettings = new TouchControlSettings(this);
        if (touchSettings.Enabled)
        {
            var touchControls = new TouchControllerView(this, touchSettings, editing: false);
            root.AddView(touchControls, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        }

        _status = new TextView(this)
        {
            Text = "Select the folder that contains your CUE/BIN.",
            TextSize = 13,
            Gravity = GravityFlags.Center,
        };
        _status.SetTextColor(Color.White);
        _status.SetBackgroundColor(Color.Argb(145, 3, 9, 14));
        _status.SetPadding(Dp(12), Dp(5), Dp(12), Dp(5));
        _statusLayout = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent,
            GravityFlags.Top | GravityFlags.CenterHorizontal);
        ApplyStatusBarLayout();
        root.AddView(_status, _statusLayout);

        _progress = new ProgressBar(this) { Indeterminate = true, Visibility = ViewStates.Gone };
        root.AddView(_progress, new FrameLayout.LayoutParams(Dp(42), Dp(42), GravityFlags.Center));

        _devHud = new TextView(this)
        {
            TextSize = 12,
            Gravity = GravityFlags.Right,
            Visibility = ConfigManager.View.ShowDevHud ? ViewStates.Visible : ViewStates.Gone,
        };
        _devHud.SetTextColor(Color.Rgb(244, 228, 188));
        _devHud.SetBackgroundColor(Color.Argb(120, 8, 8, 8));
        _devHud.SetPadding(Dp(8), Dp(4), Dp(8), Dp(4));
        root.AddView(_devHud, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent,
            GravityFlags.Top | GravityFlags.Right)
        {
            TopMargin = Dp(52),
            RightMargin = Dp(10),
        });

        var devButton = new Button(this)
        {
            Text = "DEV",
            TextSize = 11,
            Gravity = GravityFlags.Center,
            StateListAnimator = null,
        };
        devButton.SetTextColor(Color.Argb(200, 255, 255, 255));
        devButton.SetMinHeight(0);
        devButton.SetMinWidth(0);
        devButton.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));
        devButton.Background = new Android.Graphics.Drawables.ColorDrawable(Color.Transparent);
        _devMenu = new DevMenuOverlay(this,
            volume => _activeHost?.SetMasterVolume(volume),
            visible =>
            {
                if (_devHud == null) return;
                _devHud.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
            });
        devButton.Click += (_, _) => _devMenu?.Toggle();
        root.AddView(devButton, new FrameLayout.LayoutParams(Dp(56), Dp(36),
            GravityFlags.Top | GravityFlags.Right)
        {
            TopMargin = Dp(10),
            RightMargin = Dp(10),
        });
        root.AddView(_devMenu, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        root.ThreeFingerHold = () => _devMenu?.Toggle();
        SetContentView(root);
    }

    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        if (keyCode == Keycode.Back && _gameUiVisible && _devMenu?.IsOpen == true)
        {
            _devMenu.Close();
            return true;
        }

        return base.OnKeyDown(keyCode, e);
    }

    void EnterImmersiveGameMode()
    {
        if (Window == null) return;

        Window.AddFlags(WindowManagerFlags.Fullscreen);
        Window.DecorView.SystemUiFlags = SystemUiFlags.LayoutStable |
                                         SystemUiFlags.LayoutHideNavigation |
                                         SystemUiFlags.LayoutFullscreen |
                                         SystemUiFlags.HideNavigation |
                                         SystemUiFlags.Fullscreen |
                                         SystemUiFlags.ImmersiveSticky;

        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            Window.SetDecorFitsSystemWindows(false);
            var controller = Window.InsetsController;
            if (controller != null)
            {
                controller.Hide(WindowInsets.Type.SystemBars());
                controller.SystemBarsBehavior =
                    (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
            }
        }
    }

    void LeaveImmersiveGameMode()
    {
        if (Window == null) return;

        Window.ClearFlags(WindowManagerFlags.Fullscreen);
        Window.DecorView.SystemUiFlags = SystemUiFlags.LayoutStable;
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            Window.SetDecorFitsSystemWindows(true);
            Window.InsetsController?.Show(WindowInsets.Type.SystemBars());
        }
    }

    internal void ApplyGameDisplayMode()
    {
        if (!_gameUiVisible) return;
        if (ConfigManager.View.Fullscreen) EnterImmersiveGameMode();
        else LeaveImmersiveGameMode();
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus && _gameUiVisible) ApplyGameDisplayMode();
    }

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        if (_gameUiVisible)
        {
            ApplyGameDisplayMode();
            ApplyStatusBarLayout();
            _devMenu?.RelayoutCard();
        }
        else if (_launcher != null)
        {
            _launcher.RequestLayout();
            _launcher.Invalidate();
        }
    }

    void ApplyStatusBarLayout()
    {
        if (_statusLayout == null) return;
        var landscape = IsLandscape();
        _statusLayout.LeftMargin = Dp(landscape ? 150 : 20);
        _statusLayout.RightMargin = Dp(landscape ? 150 : 20);
        _statusLayout.TopMargin = Dp(landscape ? 34 : 48);
        _status.LayoutParameters = _statusLayout;
    }

    bool IsLandscape()
    {
        var metrics = Resources?.DisplayMetrics;
        return metrics != null && metrics.WidthPixels >= metrics.HeightPixels;
    }

    AndroidPlatformHost? _activeHost;

    protected override void OnPause()
    {
        RecompOne.Runtime.Hardware.Controller.SetVirtualPadState(0);
        // Never leak game audio into the home screen / lock screen.
        _activeHost?.PauseAudio();
        base.OnPause();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _activeHost?.ResumeAudio();
    }

    void PickDiscFolder()
    {
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission |
                        ActivityFlags.GrantPersistableUriPermission |
                        ActivityFlags.GrantPrefixUriPermission);
        StartActivityForResult(intent, PickDiscFolderRequest);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != PickDiscFolderRequest || resultCode != Result.Ok || data?.Data == null)
            return;

        _treeUri = data.Data;
        try
        {
            ContentResolver.TakePersistableUriPermission(
                _treeUri,
                data.Flags & (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission));
        }
        catch
        {
            // Some document providers grant access only for this app session.
        }

        GetPreferences(FileCreationMode.Private).Edit()!
            .PutString(DiscTreePreference, _treeUri.ToString())!
            .Apply();
        ScanSelectedFolder();
    }

    void ScanSelectedFolder()
    {
        try
        {
            _disc = _treeUri == null ? null : FindDiscDocuments(_treeUri);
            if (_disc == null)
            {
                if (TryUseLocalDiscCopy()) return;
                _launcher?.ShowDisc(
                    ready: false,
                    "Invalid disc",
                    "The folder needs a .cue file and its matching .bin.");
                return;
            }

            ShowDiscReady();
        }
        catch (Exception ex)
        {
            // Document providers may grant session-only access: after a process
            // restart the tree URI is dead, but the imported copy still works.
            if (TryUseLocalDiscCopy()) return;
            _launcher?.ShowDisc(
                ready: false,
                "Folder is not readable",
                ex.Message);
        }
    }

    bool TryUseLocalDiscCopy()
    {
        var discDir = Path.Combine(FilesDir!.AbsolutePath, "disc");
        var cue = Path.Combine(discDir, "game.cue");
        var bin = Path.Combine(discDir, "game.bin");
        var cachedSize = GetPreferences(FileCreationMode.Private).GetLong("cached_bin_size", -1);
        if (!File.Exists(cue) || !File.Exists(bin) || cachedSize <= 0 ||
            new FileInfo(bin).Length != cachedSize)
            return false;

        _usingLocalDiscCopy = true;
        var sizeMb = cachedSize / (1024d * 1024d);
        _launcher?.ShowDisc(
            ready: true,
            "Local disc copy ready",
            $"game.bin ({sizeMb:0} MB) already imported into the app.\nFull SCUS-94900 validation runs at launch.");
        return true;
    }

    void ShowDiscReady()
    {
        if (_disc == null) return;
        var sizeMb = _disc.BinSize / (1024d * 1024d);
        _launcher?.ShowDisc(
            ready: true,
            "Disc files ready",
            $"{_disc.CueName}  •  {_disc.BinName} ({sizeMb:0} MB)\nFull SCUS-94900 validation runs at launch.");
    }

    DiscDocuments? FindDiscDocuments(Android.Net.Uri treeUri)
    {
        var treeId = DocumentsContract.GetTreeDocumentId(treeUri);
        var children = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, treeId);
        var projection = new[]
        {
            DocumentsContract.Document.ColumnDocumentId,
            DocumentsContract.Document.ColumnDisplayName,
            DocumentsContract.Document.ColumnSize,
        };

        var docs = new List<DocumentInfo>();
        using ICursor? cursor = ContentResolver.Query(children, projection, null, null, null);
        if (cursor == null) return null;
        var idColumn = cursor.GetColumnIndex(DocumentsContract.Document.ColumnDocumentId);
        var nameColumn = cursor.GetColumnIndex(DocumentsContract.Document.ColumnDisplayName);
        var sizeColumn = cursor.GetColumnIndex(DocumentsContract.Document.ColumnSize);
        while (cursor.MoveToNext())
        {
            var id = cursor.GetString(idColumn);
            var name = cursor.GetString(nameColumn);
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
            var size = !cursor.IsNull(sizeColumn) ? cursor.GetLong(sizeColumn) : 0L;
            docs.Add(new DocumentInfo(name, DocumentsContract.BuildDocumentUriUsingTree(treeUri, id), size));
        }

        var cue = docs.FirstOrDefault(d => d.Name.EndsWith(".cue", StringComparison.OrdinalIgnoreCase));
        if (cue == null) return null;
        string cueText;
        using (var input = ContentResolver.OpenInputStream(cue.Uri)
                           ?? throw new IOException("Unable to open the CUE."))
        using (var reader = new StreamReader(input))
            cueText = reader.ReadToEnd();

        var match = Regex.Match(cueText, "(?im)^\\s*FILE\\s+\"([^\"]+)\"\\s+(?:BINARY|MOTOROLA)\\s*$");
        if (!match.Success) return null;
        var binName = Path.GetFileName(match.Groups[1].Value.Replace('\\', '/'));
        var bin = docs.FirstOrDefault(d => d.Name.Equals(binName, StringComparison.OrdinalIgnoreCase));
        if (bin == null) return null;
        return new DiscDocuments(cue.Name, cue.Uri, cueText, bin.Name, bin.Uri, bin.Size);
    }

    async Task StartGameAsync()
    {
        if (_disc == null && !_usingLocalDiscCopy) return;
        _launcher?.SetBusy(true, "Opening the bundled runtime and preparing the game files.");
        ShowGameUi();
        _progress.Visibility = ViewStates.Visible;

        try
        {
            await Task.Run(async () =>
            {
                var dataRoot = Path.Combine(FilesDir!.AbsolutePath, "runtime");
                AppPaths.SetRoot(dataRoot);
                AppPaths.EnsureCreated();

                string cuePath;
                if (_usingLocalDiscCopy)
                {
                    SetStatus("Using the disc copy already imported…");
                    cuePath = Path.Combine(FilesDir!.AbsolutePath, "disc", "game.cue");
                }
                else
                {
                    SetStatus("Copying the disc into the app's private storage (first time only)…");
                    cuePath = await EnsureLocalDiscAsync(_disc!);
                }

                var bootstrap = Path.Combine(dataRoot, "bootstrap");
                Directory.CreateDirectory(bootstrap);
                var configPath = Path.Combine(bootstrap, "CrashBandicoot.json");
                var patchPath = Path.Combine(bootstrap, "main.cs.patch");
                CopyAsset("Recomp/CrashBandicoot.json", configPath);
                CopyAsset("Recomp/Patches/main.cs.patch", patchPath);

                var references = Path.Combine(dataRoot, "compiler-refs");
                CopyAssetDirectory("CompilerRefs", references);

                ConfigManager.Load();
                ConfigManager.Game.CdPath = cuePath;
                ConfigManager.SaveGame();
                ApplyRuntimeGraphicsSettings();

                SetStatus("Checking that this is Crash Bandicoot NTSC-U…");
                var validation = DiscValidator.Validate(cuePath);
                if (!validation.Ok)
                    throw new InvalidOperationException($"{validation.Title}: {validation.Problem} {validation.Fix}");

                string gameDll;
                if (GameStore.TryGetValid(validation.Fingerprint, cuePath, out gameDll))
                {
                    SetStatus("Game already prepared. Starting…");
                }
                else
                {
                    var sourceDir = GameStore.SourcesDir(validation.Fingerprint);
                    gameDll = GameStore.DllPath(validation.Fingerprint);
                    Directory.CreateDirectory(sourceDir);
                    var messages = new Progress<string>(SetStatus);
                    RecompRunner.Run(configPath, cuePath, sourceDir, messages, patchPath);
                    RunOnLargeStack("Game Compiler", () =>
                        GameCompiler.CompileToDll(
                            sourceDir,
                            gameDll,
                            messages,
                            references,
                            concurrentBuild: false));
                    GameStore.WriteManifest(validation.Fingerprint, cuePath, validation.BinPath, gameDll);
                }

                SetStatus("Starting the PS1 core…");
                RunGameCore(gameDll, cuePath);
            });
        }
        catch (Exception ex)
        {
            var root = Path.Combine(FilesDir!.AbsolutePath, "runtime");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "android-last-error.txt"), ex.ToString());
            RunOnUiThread(() =>
            {
                _progress.Visibility = ViewStates.Gone;
                ShowLauncherUi();
                _launcher?.ShowError(ex.GetBaseException().Message);
            });
        }
    }

    void RunGameCore(string gameDll, string cuePath)
        => RunOnLargeStack("PS1 Game Core", () =>
        {
            AndroidEglContext? egl = null;
            Surface? surface = null;
            Silk.NET.OpenGL.GL? gl = null;
            RecompOne.Runtime.Hle.GlBackend? backend = null;
            GameGpuDiagnosticsSession? diagnostics = null;
            AndroidPlatformHost? host = null;
            try
            {
                surface = WaitForGameSurface();
                egl = new AndroidEglContext(surface, AcquireCurrentGameSurface);
                gl = Silk.NET.OpenGL.GL.GetApi(egl);
                RecompOne.Runtime.Hle.GlVram.Scale = ConfigManager.View.InternalResolution;
                backend = new RecompOne.Runtime.Hle.GlBackend(gl);
                var gpuInfo = AndroidGlesInfo.Capture(
                    gl, egl, Intent?.GetStringExtra(AndroidGlesInfo.ForceFramebufferFetchExtra));
                // Framebuffer fetch keeps effects such as Crash's spin in one
                // ordered batch. EXT and ARM expose different shader syntax,
                // so the selected path must be passed explicitly to the backend.
                backend.InitGl(gles: true, framebufferFetch: gpuInfo.FramebufferFetchPath);
                if (!backend.Ready)
                    throw new InvalidOperationException("The Android OpenGL ES renderer failed to initialize.");
                var configured = gpuInfo.ConfigureBackend(
                    backend, ConfigManager.View.InternalResolution);
                Android.Util.Log.Info("CrashGPU",
                    $"GPU {gpuInfo.Vendor} / {gpuInfo.Renderer}; " +
                    $"framebuffer fetch: {gpuInfo.FramebufferFetchLabel}; " +
                    $"texture barrier: {(configured.textureBarrier ? gpuInfo.TextureBarrierFunction : "flush fallback")}; " +
                    $"2x2 shading: {configured.coarseShading}");
                diagnostics = new GameGpuDiagnosticsSession(
                    this, gpuInfo, ConfigManager.View.InternalResolution,
                    configured.textureBarrier, configured.coarseShading);

                RecompOne.Runtime.Hle.GpuHle.Backend = backend;
                RecompOne.Runtime.Hle.GpuHle.Active = true;
                RecompOne.Runtime.Hle.GpuHle.NativeResolution =
                    ConfigManager.View.InternalResolution <= 1;
                host = new AndroidPlatformHost(this, _status, _progress, egl, backend, diagnostics);
                host.AttachHud(_devHud);
                _activeHost = host;
                RecompOne.Runtime.Runtime.SetPlatformHost(host);
                GameLoader.Run(gameDll, cuePath);
            }
            finally
            {
                diagnostics?.Complete();
                RecompOne.Runtime.Runtime.SetPlatformHost(null);
                // If the core died before Runtime.Shutdown, this still releases
                // the AudioTrack; AndroidPlatformHost.Shutdown is idempotent.
                host?.Shutdown();
                _activeHost = null;
                RecompOne.Runtime.Hle.GpuHle.Active = false;
                RecompOne.Runtime.Hle.GpuHle.Backend = null;
                backend?.Dispose();
                gl?.Dispose();
                egl?.Dispose();
                surface?.Dispose();
            }
        });

    Surface WaitForGameSurface()
    {
        var deadline = System.Environment.TickCount64 + 5000;
        while ((!_screen.IsAvailable || _screen.SurfaceTexture == null) &&
               System.Environment.TickCount64 < deadline)
            Thread.Sleep(10);

        if (!_screen.IsAvailable || _screen.SurfaceTexture == null)
            throw new InvalidOperationException("The Android video surface is not ready.");
        return new Surface(_screen.SurfaceTexture);
    }

    Surface AcquireCurrentGameSurface()
    {
        var texture = _screen.SurfaceTexture
                      ?? throw new InvalidOperationException("SurfaceTexture is not available.");
        return new Surface(texture);
    }

    void InitializeRuntimeConfiguration()
    {
        var dataRoot = Path.Combine(FilesDir!.AbsolutePath, "runtime");
        AppPaths.SetRoot(dataRoot);
        AppPaths.EnsureCreated();
        ConfigManager.Load();

        // A phone game should start immersive unless the user explicitly changes it.
        if (!ConfigManager.View.Values.ContainsKey("Fullscreen"))
        {
            ConfigManager.View.Fullscreen = true;
            ConfigManager.SaveView(Array.Empty<RecompOne.Runtime.Host.Window.IPanel>());
        }
    }

    static void ApplyRuntimeGraphicsSettings()
    {
        var view = ConfigManager.View;
        RecompOne.Runtime.Hle.GpuHle.WideAspect = view.Widescreen ? 16f / 9f : 0f;
        RecompOne.Runtime.Hle.GpuHle.TextureFilter = view.TextureFilter;
        RecompOne.Runtime.Hle.GpuHle.TextureFilterStrength = view.TextureFilterStrength;
        RecompOne.Runtime.Hle.GpuHle.Dedither = view.Dedither;
        RecompOne.Runtime.Hle.GpuHle.Dejitter = view.Dejitter;
        RecompOne.Runtime.Hle.GpuHle.PresentNearest = view.PresentNearest;
        RecompOne.Runtime.Hle.GpuHle.IntegerScale = view.IntegerScale;
        RecompOne.Runtime.Host.FrameClock.SkipThrottle = view.VSync;
        RecompOne.Runtime.Hle.GpuHle.RefreshWideFov();
    }

    static void RunOnLargeStack(string threadName, Action action)
    {
        // Android's .NET thread-pool workers have a comparatively small stack.
        // Roslyn processing the large generated source and recompiled MIPS calls
        // both need more headroom than a normal pool worker provides.
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        var gameThread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            }
        }, 32 * 1024 * 1024)
        {
            IsBackground = true,
            Name = threadName,
            Priority = threadName == "PS1 Game Core"
                ? System.Threading.ThreadPriority.AboveNormal
                : System.Threading.ThreadPriority.Normal,
        };

        gameThread.Start();
        gameThread.Join();
        failure?.Throw();
    }

    async Task<string> EnsureLocalDiscAsync(DiscDocuments disc)
    {
        var discDir = Path.Combine(FilesDir!.AbsolutePath, "disc");
        Directory.CreateDirectory(discDir);
        var cuePath = Path.Combine(discDir, "game.cue");
        var binPath = Path.Combine(discDir, "game.bin");
        var prefs = GetPreferences(FileCreationMode.Private);
        var sourceKey = $"{disc.CueUri}|{disc.BinUri}";
        var cachedKey = prefs.GetString("cached_disc", null);
        var cachedSize = prefs.GetLong("cached_bin_size", -1);

        if (!File.Exists(binPath) || new FileInfo(binPath).Length != disc.BinSize ||
            cachedSize != disc.BinSize || !string.Equals(cachedKey, sourceKey, StringComparison.Ordinal))
        {
            using var input = ContentResolver.OpenInputStream(disc.BinUri)
                              ?? throw new IOException("Unable to open the BIN.");
            using var output = File.Create(binPath);
            await input.CopyToAsync(output);
            prefs.Edit()!.PutString("cached_disc", sourceKey)!.PutLong("cached_bin_size", disc.BinSize)!.Apply();
        }

        var fileLine = new Regex(
            "^\\s*FILE\\s+\"[^\"]+\"\\s+(?:BINARY|MOTOROLA)\\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        var localCue = fileLine.Replace(disc.CueText, "FILE \"game.bin\" BINARY", 1);
        await File.WriteAllTextAsync(cuePath, localCue);
        return cuePath;
    }

    void CopyAsset(string assetPath, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = Assets!.Open(assetPath);
        using var output = File.Create(destination);
        input.CopyTo(output);
    }

    void CopyAssetDirectory(string assetDirectory, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var name in Assets!.List(assetDirectory) ?? [])
            CopyAsset($"{assetDirectory}/{name}", Path.Combine(destination, name));
    }

    void SetStatus(string text) => RunOnUiThread(() => _status.Text = text);
    int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density + 0.5f);

    sealed record DocumentInfo(string Name, Android.Net.Uri Uri, long Size);
    sealed record DiscDocuments(
        string CueName,
        Android.Net.Uri CueUri,
        string CueText,
        string BinName,
        Android.Net.Uri BinUri,
        long BinSize);
}
