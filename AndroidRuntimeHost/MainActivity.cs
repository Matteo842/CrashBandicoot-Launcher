using System.Text.RegularExpressions;
using Android.App;
using Android.Content;
using Android.Content.PM;
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
    ScreenOrientation = ScreenOrientation.Landscape,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public sealed class MainActivity : Activity
{
    const int PickDiscFolderRequest = 949;
    const string DiscTreePreference = "disc_tree";

    LauncherScreen _launcher = null!;
    TextView _status = null!;
    ImageView _screen = null!;
    ProgressBar _progress = null!;
    Android.Net.Uri? _treeUri;
    DiscDocuments? _disc;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        if (Window != null)
        {
            Window.SetStatusBarColor(Color.Rgb(6, 16, 24));
            Window.SetNavigationBarColor(Color.Rgb(6, 16, 24));
            Window.DecorView.SystemUiFlags = SystemUiFlags.LayoutStable |
                                             SystemUiFlags.LayoutHideNavigation |
                                             SystemUiFlags.ImmersiveSticky;
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
        _launcher = new LauncherScreen(this);
        _launcher.SelectDiscRequested += PickDiscFolder;
        _launcher.StartGameRequested += () => _ = StartGameAsync();
        SetContentView(_launcher);

        if (_disc != null)
            ShowDiscReady();
    }

    void ShowGameUi()
    {
        var root = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
        };
        root.SetGravity(GravityFlags.CenterHorizontal);
        root.SetBackgroundColor(Color.Rgb(7, 11, 18));
        root.SetPadding(Dp(18), Dp(12), Dp(18), Dp(12));

        _screen = new ImageView(this);
        _screen.SetBackgroundColor(Color.Black);
        _screen.SetScaleType(ImageView.ScaleType.FitCenter);
        root.AddView(_screen, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f));

        _status = new TextView(this)
        {
            Text = "Seleziona la cartella che contiene il tuo CUE/BIN.",
            TextSize = 16,
            Gravity = GravityFlags.Center,
        };
        _status.SetTextColor(Color.White);
        _status.SetPadding(0, Dp(10), 0, Dp(8));
        root.AddView(_status, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        _progress = new ProgressBar(this) { Indeterminate = true, Visibility = ViewStates.Gone };
        root.AddView(_progress, new LinearLayout.LayoutParams(Dp(42), Dp(42)));
        SetContentView(root);
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
                _launcher.ShowDisc(
                    ready: false,
                    "Disco non valido",
                    "Nella cartella servono un file .cue e il relativo .bin.");
                return;
            }

            ShowDiscReady();
        }
        catch (Exception ex)
        {
            _launcher.ShowDisc(
                ready: false,
                "Cartella non leggibile",
                ex.Message);
        }
    }

    void ShowDiscReady()
    {
        if (_disc == null) return;
        var sizeMb = _disc.BinSize / (1024d * 1024d);
        _launcher.ShowDisc(
            ready: true,
            "File del disco pronti",
            $"{_disc.CueName}  •  {_disc.BinName} ({sizeMb:0} MB)\nIl controllo completo SCUS-94900 verrà eseguito all'avvio.");
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
                           ?? throw new IOException("Impossibile aprire il CUE."))
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
        if (_disc == null) return;
        _launcher.SetBusy(true, "Apro il runtime integrato e preparo i file del gioco.");
        ShowGameUi();
        _progress.Visibility = ViewStates.Visible;

        try
        {
            await Task.Run(async () =>
            {
                var dataRoot = Path.Combine(FilesDir!.AbsolutePath, "runtime");
                AppPaths.SetRoot(dataRoot);
                AppPaths.EnsureCreated();

                SetStatus("Copio il disco nello spazio privato dell'app (solo la prima volta)…");
                var cuePath = await EnsureLocalDiscAsync(_disc);

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

                SetStatus("Controllo che sia Crash Bandicoot NTSC-U…");
                var validation = DiscValidator.Validate(cuePath);
                if (!validation.Ok)
                    throw new InvalidOperationException($"{validation.Title}: {validation.Problem} {validation.Fix}");

                string gameDll;
                if (GameStore.TryGetValid(validation.Fingerprint, cuePath, out gameDll))
                {
                    SetStatus("Gioco già preparato. Avvio…");
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

                SetStatus("Avvio del core PS1…");
                RecompOne.Runtime.Runtime.SetPlatformHost(
                    new AndroidPlatformHost(this, _screen, _status, _progress));
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
                _launcher.ShowError(ex.GetBaseException().Message);
            });
        }
    }

    static void RunGameCore(string gameDll, string cuePath)
        => RunOnLargeStack("PS1 Game Core", () => GameLoader.Run(gameDll, cuePath));

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
                              ?? throw new IOException("Impossibile aprire il BIN.");
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
