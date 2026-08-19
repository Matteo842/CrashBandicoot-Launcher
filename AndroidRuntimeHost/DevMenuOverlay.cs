using Android.App;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using RecompOne.Runtime;
using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Host.Cheats;
using RecompOne.Runtime.Host.Window;
using Color = Android.Graphics.Color;
using Process = System.Diagnostics.Process;

namespace CrashBandicoot.AndroidRuntime;

/// <summary>
/// Touch-first Developer Menu. Same sections as the desktop F3 book, with
/// finger-sized rows and no ImGui debug panels.
/// </summary>
sealed partial class DevMenuOverlay : FrameLayout
{
    static readonly Color Panel = Color.Argb(179, 28, 28, 28);
    static readonly Color Sand = Color.Rgb(244, 228, 188);
    static readonly Color Muted = Color.Rgb(170, 170, 170);
    static readonly Color Wumpa = Color.Rgb(255, 176, 32);
    static readonly Color Night = Color.Rgb(6, 16, 24);

    readonly Activity _activity;
    readonly Typeface _displayFont;
    readonly Typeface _bodyFont;
    readonly Typeface _bodyBold;
    readonly LinearLayout _card;
    readonly LinearLayout _body;
    readonly TextView _title;
    readonly Button _back;
    readonly Action<float> _setVolume;
    readonly Action<bool> _hudChanged;
    readonly Process _process = Process.GetCurrentProcess();

    string _section = "";

    public bool IsOpen => Visibility == ViewStates.Visible;

    public DevMenuOverlay(Activity activity, Action<float> setVolume, Action<bool> hudChanged)
        : base(activity)
    {
        _activity = activity;
        _setVolume = setVolume;
        _hudChanged = hudChanged;
        _displayFont = LoadTypeface("Fonts/Bungee-Regular.ttf", Typeface.DefaultBold!);
        _bodyFont = LoadTypeface("Fonts/Nunito-SemiBold.ttf", Typeface.Default!);
        _bodyBold = LoadTypeface("Fonts/Nunito-ExtraBold.ttf", Typeface.DefaultBold!);

        Visibility = ViewStates.Gone;
        Clickable = true;
        SetBackgroundColor(Color.Argb(50, 0, 0, 0));
        Click += (_, _) => Close();

        _card = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        _card.Clickable = true;
        _card.SetPadding(Dp(18), Dp(14), Dp(18), Dp(14));
        _card.Background = RoundedBackground(Panel, Color.Argb(90, 255, 255, 255), Dp(1), Dp(4));
        _card.Click += (_, _) => { /* swallow so the dim backdrop does not close */ };

        var header = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        _title = Label("DEVELOPER MENU", 16, Wumpa, _displayFont);
        _title.SetIncludeFontPadding(false);
        header.AddView(_title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        var close = ActionButton("X", compact: true);
        close.Click += (_, _) => Close();
        header.AddView(close, new LinearLayout.LayoutParams(Dp(48), Dp(44)));
        _card.AddView(header);

        _back = ActionButton("<  BACK", compact: false);
        _back.Visibility = ViewStates.Gone;
        _back.Click += (_, _) =>
        {
            if (_section.StartsWith("debug-", StringComparison.Ordinal))
                ShowSection("debug");
            else
                ShowRoot();
        };
        _card.AddView(_back, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(48))
        {
            TopMargin = Dp(8),
            BottomMargin = Dp(4),
        });

        var scroll = new ScrollView(activity) { FillViewport = true };
        _body = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        scroll.AddView(_body);
        _card.AddView(scroll, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f)
        {
            TopMargin = Dp(6),
        });

        AddView(_card, CardLayout());
        ShowRoot();
    }

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public void Open()
    {
        RecompOne.Runtime.Hardware.Controller.SetVirtualPadState(0);
        ShowRoot();
        Visibility = ViewStates.Visible;
        BringToFront();
    }

    public void Close()
    {
        StopLiveRefresh();
        Visibility = ViewStates.Gone;
        RecompOne.Runtime.Hardware.Controller.SetVirtualPadState(0);
        RecompOne.Runtime.Memory.RamLogger.TrackReads = false;
    }

    void ShowRoot()
    {
        StopLiveRefresh();
        _section = "";
        _title.Text = "DEVELOPER MENU";
        _back.Visibility = ViewStates.Gone;
        _body.RemoveAllViews();
        Hint("DEVELOPER");
        Divider();
        Toggle("Show FPS / Mem HUD", ConfigManager.View.ShowDevHud, value =>
        {
            ConfigManager.View.ShowDevHud = value;
            ConfigManager.SaveView(Array.Empty<IPanel>());
            _hudChanged(value);
        }, "Top-right overlay. Independent of this menu.");
        LiveMono();
        UpdateRootFps();
        Divider();
        Category("Cheats", "cheats");
        Category("Levels", "levels");
        Category("Display", "display");
        Category("Rendering", "rendering");
        Category("Audio", "audio");
        Category("Mods", "mods");
        Category("Debug", "debug");
        Category("Engine", "engine");
        Divider();
        Hint("Hold 3 fingers for ½ second, or tap DEV.");
        StartLiveRefresh(UpdateRootFps);
    }

    void ShowSection(string id)
    {
        StopLiveRefresh();
        _section = id;
        _back.Visibility = ViewStates.Visible;
        _body.RemoveAllViews();
        switch (id)
        {
            case "cheats":
                _title.Text = "CHEATS";
                BuildCheats();
                break;
            case "levels":
                _title.Text = "LEVELS";
                BuildLevels();
                break;
            case "display":
                _title.Text = "DISPLAY";
                BuildDisplay();
                break;
            case "rendering":
                _title.Text = "RENDERING";
                BuildRendering();
                break;
            case "audio":
                _title.Text = "AUDIO";
                BuildAudio();
                break;
            case "debug":
                _title.Text = "DEBUG";
                BuildDebug();
                break;
            case "debug-cpu":
                _title.Text = "CPU STATE";
                BuildCpu();
                break;
            case "debug-mem":
                _title.Text = "MEMORY EDITOR";
                BuildMemory();
                break;
            case "debug-ram":
                _title.Text = "RAM MAP";
                BuildRamMap();
                break;
            case "debug-vram":
                _title.Text = "VRAM VIEWER";
                BuildVram();
                break;
            case "debug-spu":
                _title.Text = "SPU VIEWER";
                BuildSpu();
                break;
            case "debug-cd":
                _title.Text = "CD DEBUG";
                BuildCd();
                break;
            case "debug-overlay":
                _title.Text = "OVERLAY EVENTS";
                BuildOverlays();
                break;
            case "debug-console":
                _title.Text = "CONSOLE";
                BuildConsole();
                break;
            case "mods":
                _title.Text = "MODS";
                BuildMods();
                break;
            default:
                _title.Text = "ENGINE";
                BuildEngine();
                break;
        }
    }

    void BuildCheats()
    {
        Hint("NTSC-U (SCUS-94900) — applied every frame while enabled.");
        Toggle("Infinite Lives", CheatConfig.InfiniteLives, value =>
        {
            CheatConfig.InfiniteLives = value;
            CheatConfig.Save();
        }, "Keeps map lives at 99 and freezes the active level lives counter.");
        Toggle("Infinite Wumpa", CheatConfig.InfiniteWumpa, value =>
        {
            CheatConfig.InfiniteWumpa = value;
            CheatConfig.Save();
        }, "Freezes Wumpa at 99 when a single active lives slot is found.");
        Toggle("Level Select", CheatConfig.LevelSelect, value =>
        {
            CheatConfig.LevelSelect = value;
            CheatConfig.Save();
        }, "Unlocks the level select flag in RAM — use on the warp map.");
        Divider();
        FullButton("99 Lives (map)", () => CheatManager.Give99LivesOnMap());
        FullButton("2nd Mask (map)", () => CheatManager.Give2ndMaskOnMap());
        Hint("Aku Aku ×2 on the warp map.");
        FullButton("99 Wumpa (level)", () =>
        {
            if (!CheatManager.Give99Wumpa())
                Toast("No unique active level slot — try mid-level.");
        });
        FullButton("Instant Save Menu", () => CheatManager.OpenInstantSaveMenu());
    }

    void BuildLevels()
    {
        Hint("Current level");
        if (CheatManager.TryGetLevelId(out var id))
        {
            Body($"ID: {id}  (0x{id:X})");
            if (Catalog.Levels.TryGet(id, out var info))
            {
                Body(info.Name);
                Hint($"slug: {info.Slug}  kind: {info.Kind}");
            }
            else if (CheatManager.IsOnTitleMenuMap())
                Hint("Title / menus / warp map / game over");
        }
        else
            Hint("ID: — (no guest RAM)");

        Hint($"VA 0x{Catalog.Levels.LevelIdAddr:X8}");
        FullButton("Refresh", () => ShowSection("levels"));
        Hint("Level Select and other cheats are under Cheats.");
    }

    void BuildDisplay()
    {
        var view = ConfigManager.View;
        Toggle("Widescreen (16:9)", view.Widescreen, value =>
        {
            view.Widescreen = value;
            AndroidGraphics.ApplyLive();
        }, "Hack: stretches 4:3 (gameplay only).");
        Choice("Frame rate", ViewConfig.FrameRateLabels,
            ViewConfig.FrameRateToIndex(view.FrameRate),
            index =>
            {
                view.FrameRate = ViewConfig.FrameRateOptionValues[index];
                AndroidGraphics.ApplyLive();
                FramePacing.Reset();
                ShowSection("display");
            });
        Hint("Gameplay only. Refresh cap — 60 and 120 play at the same speed. Menus stay 30 game / 60 present.");
        Toggle("Dedither", view.Dedither, value =>
        {
            view.Dedither = value;
            AndroidGraphics.ApplyLive();
        }, "Removes dither noise.");
        Toggle("Dejitter", view.Dejitter, value =>
        {
            view.Dejitter = value;
            AndroidGraphics.ApplyLive();
        }, "Less polygon wobble.");
        Toggle("Fullscreen", view.Fullscreen, value =>
        {
            view.Fullscreen = value;
            ConfigManager.SaveView(Array.Empty<IPanel>());
            if (_activity is MainActivity main)
                main.ApplyGameDisplayMode();
        }, "Hides the status and navigation bars.");
        Hint("Menu bar (F1) is desktop-only.");
        Hint("More options: Rendering.");
    }

    void BuildRendering()
    {
        var view = ConfigManager.View;
        Hint("Presets — live apply, no restart.");
        for (var i = 0; i < AndroidGraphics.PresetLabels.Length; i++)
        {
            var index = i;
            FullButton(AndroidGraphics.PresetLabels[i], () =>
            {
                AndroidGraphics.ApplyPreset(index);
                ShowSection("rendering");
            }, primary: true);
        }
        Divider();
        Toggle("Widescreen (16:9)", view.Widescreen, value =>
        {
            view.Widescreen = value;
            AndroidGraphics.ApplyLive();
        }, "Hack: stretches 4:3 (gameplay only).");
        Toggle("Dedither", view.Dedither, value =>
        {
            view.Dedither = value;
            AndroidGraphics.ApplyLive();
        });
        Toggle("Dejitter", view.Dejitter, value =>
        {
            view.Dejitter = value;
            AndroidGraphics.ApplyLive();
        });
        Toggle("Integer scaling", view.IntegerScale, value =>
        {
            view.IntegerScale = value;
            AndroidGraphics.ApplyLive();
        }, "Sharp upscale, black bars.");
        Toggle("Crisp pixels (nearest)", view.PresentNearest, value =>
        {
            view.PresentNearest = value;
            AndroidGraphics.ApplyLive();
        }, "No blurry upscale.");
        Toggle("VSync", view.VSync, value =>
        {
            view.VSync = value;
            AndroidGraphics.ApplyLive();
        }, "Desktop swap-interval. Android always keeps the software present clock so SPU audio stays in sync.");
        Choice("Frame rate", ViewConfig.FrameRateLabels,
            ViewConfig.FrameRateToIndex(view.FrameRate),
            index =>
            {
                view.FrameRate = ViewConfig.FrameRateOptionValues[index];
                AndroidGraphics.ApplyLive();
                FramePacing.Reset();
                ShowSection("rendering");
            });
        Hint("Gameplay only. Refresh cap — delta time keeps 60 and 120 at the same play speed. Menus stay 60 present.");
        Divider();
        Choice("Internal resolution", ViewConfig.InternalResolutionOptions
                .Select(scale => scale == 1 ? "Native (1x)" : scale == 8 ? "8x (4K)" : $"{scale}x")
                .ToArray(),
            IndexOf(ViewConfig.InternalResolutionOptions, view.InternalResolution),
            index =>
            {
                view.InternalResolution = ViewConfig.InternalResolutionOptions[index];
                ConfigManager.SaveView(Array.Empty<IPanel>());
                Toast("Restart the game session to apply internal resolution.");
            });
        Hint("Higher = more VRAM. Restart required.");
        Choice("Texture filter", ViewConfig.TextureFilterLabels,
            Math.Clamp(view.TextureFilter, 0, ViewConfig.TextureFilterLabels.Length - 1),
            index =>
            {
                view.TextureFilter = index;
                AndroidGraphics.ApplyLive();
                ShowSection("rendering");
            });
        if (view.TextureFilter != ViewConfig.TextureFilterOff)
        {
            Slider("Filter strength", 0, 100, (int)MathF.Round(view.TextureFilterStrength * 100f),
                value =>
                {
                    view.TextureFilterStrength = value / 100f;
                    AndroidGraphics.ApplyLive();
                });
        }
        Hint("Filters auto-off on menus and cutscenes.");
    }

    void BuildMods()
    {
        Hint("Enable or disable packs in the launcher Mods menu. C# hooks apply on the next game start.");
        var mods = RecompOne.Runtime.Modding.ModLoader.LoadedMods;
        Body($"{mods.Count} mod(s) loaded this session");
        if (mods.Count == 0)
            Hint("None loaded. Import a zip from the launcher, Save, then start the game.");
        else
        {
            foreach (var mod in mods)
            {
                var line = string.IsNullOrWhiteSpace(mod.Version) ? mod.Name : $"{mod.Name}  v{mod.Version}";
                if (!string.IsNullOrWhiteSpace(mod.Author))
                    line += $"  —  {mod.Author}";
                Body(line);
                Hint(mod.Id);
            }
        }

        Divider();
        FullButton("Reload assets", () =>
        {
            RecompOne.Runtime.Modding.ModLoader.ReloadAssets();
            Toast("Texture / disc packs reloaded.");
            ShowSection("mods");
        });
        Hint("Refreshes PNG and disc overlays. Does not recompile C#.");
    }

    void BuildAudio()
    {
        var game = ConfigManager.Game;
        Slider("Master volume", 0, 100, (int)MathF.Round(game.MasterVolume * 100f), value =>
        {
            game.MasterVolume = value / 100f;
            _setVolume(game.Muted ? 0f : game.MasterVolume);
            ConfigManager.SaveGame();
        });
        Toggle("Mute", game.Muted, value =>
        {
            game.Muted = value;
            _setVolume(value ? 0f : game.MasterVolume);
            ConfigManager.SaveGame();
        });
    }

    void BuildDebug()
    {
        Hint("Same tools as the desktop Debug menu.");
        Divider();
        Hint("GPU");
        Category("VRAM Viewer", "debug-vram");
        Divider();
        Hint("CPU / Memory");
        Category("CPU State", "debug-cpu");
        Category("RAM Map", "debug-ram");
        Category("Memory Editor", "debug-mem");
        Divider();
        Hint("Hardware");
        Category("SPU Viewer", "debug-spu");
        Category("CD Debug", "debug-cd");
        Divider();
        Hint("System");
        Category("Overlay Events", "debug-overlay");
        Category("Console", "debug-console");
        Divider();
        FullButton("Reset view settings", () =>
        {
            ConfigManager.ResetView(Array.Empty<IPanel>());
            AndroidGraphics.ApplyLive();
            Toast("View settings reset.");
            ShowSection("debug");
        });
    }

    void BuildEngine()
    {
        Hint("Host process");
        Body($"FPS: {AndroidPlatformHost.LastFps:0.00}");
        Body($"Pacing: {AndroidDisplayPacing.Describe()}");
        Body($"Setting: {ConfigManager.View.FrameRate}   TargetHz: {RecompOne.Runtime.Host.FrameClock.TargetHz:0}");
        Body($"ForceOriginal: {RecompOne.Runtime.Host.FramePacing.ForceOriginal}   WantsUnlock: {RecompOne.Runtime.Host.FramePacing.WantsUnlock}");
        Body($"dt active: {RecompOne.Runtime.Host.FramePacing.IsActive(Runtime.Mem)}   ticks {RecompOne.Runtime.Host.FramePacing.LastFrameTicks}/34");
        _process.Refresh();
        Body($"Working set: {FormatBytes(_process.WorkingSet64)}");
        Body($"Private bytes: {FormatBytes(_process.PrivateMemorySize64)}");
        Body($"GC heap: {FormatBytes(GC.GetTotalMemory(false))}");
        Body($"GC collections: gen0={GC.CollectionCount(0)}  gen1={GC.CollectionCount(1)}  gen2={GC.CollectionCount(2)}");
        Divider();
        Hint("Known pools (explains ~100–300 MB)");
        Hint("Managed / estimated GPU — not a full process accounting.");
        var pools = KnownPools(out var accounted);
        foreach (var (name, bytes, note) in pools)
            Body($"{name}: {(bytes > 0 ? FormatBytes(bytes) : "—")}  ·  {note}");
        Body($"Accounted (non-zero rows): {FormatBytes(accounted)}");
        Hint("+ JIT of game.recomp.dll, .NET, display RTs, driver overhead");
        Divider();
        Hint("Guest watches (SCUS-94900)");
        Body(GuestLine("ticks_elapsed", 0x80034520));
        Body(GuestLine("frames_elapsed", 0x80060E04));
        Body(GuestLine("vblank counter", 0x800549F0));
        Body(GuestLine("title_state", 0x800618D4));
        Body(GuestLine("pads[0]", 0x8005E71C));
        if (CheatManager.TryGetLevelId(out var levelId))
            Body($"level_id: {levelId}  (0x{CheatManager.LevelIdAddr:X8})");
        else
            Hint("level_id: —");
        Hint($"GL scale active: {GlVram.Scale}x   RamLogger: {(Runtime.RamLog.IsAllocated ? "on" : "off")}");
        FullButton("Refresh", () => ShowSection("engine"));
    }

    void Category(string title, string id)
    {
        var row = ActionButton($"{title}  …", compact: false);
        row.Gravity = GravityFlags.Left | GravityFlags.CenterVertical;
        row.SetPadding(Dp(16), 0, Dp(16), 0);
        row.Click += (_, _) => ShowSection(id);
        _body.AddView(row, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(56))
        {
            TopMargin = Dp(4),
            BottomMargin = Dp(4),
        });
    }

    void Toggle(string label, bool value, Action<bool> changed, string? hint = null)
    {
        var toggle = new Switch(_activity)
        {
            Text = label,
            TextSize = 15,
            Checked = value,
        };
        toggle.SetTypeface(_bodyBold, TypefaceStyle.Normal);
        toggle.SetTextColor(Sand);
        toggle.CheckedChange += (_, e) => changed(e.IsChecked);
        _body.AddView(toggle, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(52))
        {
            TopMargin = Dp(4),
        });
        if (!string.IsNullOrWhiteSpace(hint))
            Hint(hint);
    }

    void Slider(string name, int minimum, int maximum, int current, Action<int> changed)
    {
        var header = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        header.AddView(Label(name, 14, Sand, _bodyBold), new LinearLayout.LayoutParams(
            0, ViewGroup.LayoutParams.WrapContent, 1f));
        var value = Label($"{current}%", 13, Muted, _bodyFont);
        header.AddView(value);
        _body.AddView(header, Margin(top: 8));

        var slider = new SeekBar(_activity)
        {
            Max = maximum - minimum,
            Progress = Math.Clamp(current - minimum, 0, maximum - minimum),
        };
        slider.ProgressChanged += (_, e) =>
        {
            var actual = minimum + e.Progress;
            value.Text = $"{actual}%";
            if (e.FromUser) changed(actual);
        };
        _body.AddView(slider, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(40)));
    }

    void Choice(string name, string[] choices, int selected, Action<int> changed)
    {
        _body.AddView(Label(name, 14, Sand, _bodyBold), Margin(top: 10, bottom: 4));
        var spinner = new Spinner(_activity, SpinnerMode.Dropdown);
        var adapter = new ArrayAdapter<string>(_activity,
            Android.Resource.Layout.SimpleSpinnerItem, choices);
        adapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
        spinner.Adapter = adapter;
        spinner.SetSelection(Math.Clamp(selected, 0, choices.Length - 1));
        var armed = false;
        spinner.ItemSelected += (_, e) =>
        {
            if (!armed)
            {
                armed = true;
                return;
            }
            changed(e.Position);
        };
        _body.AddView(spinner, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(48)));
    }

    void FullButton(string label, Action click, bool primary = false)
    {
        var button = ActionButton(label, compact: false, primary);
        button.Click += (_, _) => click();
        _body.AddView(button, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(50))
        {
            TopMargin = Dp(6),
        });
    }

    void Hint(string text) => _body.AddView(Label(text, 12, Muted, _bodyFont), Margin(top: 4, bottom: 4));
    void Body(string text) => _body.AddView(Label(text, 14, Sand, _bodyFont), Margin(top: 2, bottom: 2));

    void Divider()
    {
        var line = new View(_activity);
        line.SetBackgroundColor(Color.Argb(70, 255, 255, 255));
        _body.AddView(line, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(1))
        {
            TopMargin = Dp(10),
            BottomMargin = Dp(10),
        });
    }

    Button ActionButton(string text, bool compact, bool primary = false)
    {
        var button = new Button(_activity)
        {
            Text = text,
            TextSize = compact ? 14 : 15,
            Gravity = GravityFlags.Center,
            StateListAnimator = null,
        };
        button.SetTypeface(_bodyBold, TypefaceStyle.Normal);
        button.SetTextColor(primary ? Night : Sand);
        button.SetMinHeight(0);
        button.SetMinWidth(0);
        button.Background = RoundedBackground(
            primary ? Color.Argb(179, 255, 176, 32) : Color.Argb(179, 42, 42, 42),
            Color.Argb(80, 255, 255, 255), Dp(1), Dp(3));
        return button;
    }

    TextView Label(string value, float size, Color color, Typeface font)
    {
        var text = new TextView(_activity)
        {
            Text = value,
            TextSize = size,
        };
        text.SetTypeface(font, TypefaceStyle.Normal);
        text.SetTextColor(color);
        text.SetIncludeFontPadding(false);
        return text;
    }

    LinearLayout.LayoutParams Margin(int top = 0, int bottom = 0) =>
        new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = Dp(top),
            BottomMargin = Dp(bottom),
        };

    FrameLayout.LayoutParams CardLayout()
    {
        var metrics = Resources?.DisplayMetrics;
        var width = metrics?.WidthPixels ?? 1200;
        var height = metrics?.HeightPixels ?? 800;
        var landscape = width >= height;
        var layout = new FrameLayout.LayoutParams(
            landscape ? Math.Min(Dp(400), (int)(width * 0.46f)) : (int)(width * 0.94f),
            landscape ? (int)(height * 0.92f) : (int)(height * 0.88f),
            landscape ? GravityFlags.Left | GravityFlags.CenterVertical : GravityFlags.Center)
        {
            LeftMargin = landscape ? Dp(12) : 0,
            TopMargin = Dp(8),
            BottomMargin = Dp(8),
        };
        return layout;
    }

    public void RelayoutCard() => _card.LayoutParameters = CardLayout();

    static string GuestLine(string label, uint address)
    {
        var mem = Runtime.Mem;
        if (mem == null) return $"{label}: —";
        try { return $"{label}: {mem.ReadU32(address)}  (0x{address:X8})"; }
        catch { return $"{label}: —"; }
    }

    static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.0} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.00} GB";
    }

    static int IndexOf(int[] values, int value)
    {
        var index = Array.IndexOf(values, value);
        return index < 0 ? 0 : index;
    }

    void Toast(string message) =>
        Android.Widget.Toast.MakeText(_activity, message, ToastLength.Short)?.Show();

    static GradientDrawable RoundedBackground(Color fill, Color stroke, int strokeWidth, float radius)
    {
        var drawable = new GradientDrawable();
        drawable.SetColor(fill);
        drawable.SetCornerRadius(radius);
        drawable.SetStroke(strokeWidth, stroke);
        return drawable;
    }

    int Dp(float value) =>
        (int)(value * (Resources?.DisplayMetrics?.Density ?? 1f) + 0.5f);

    Typeface LoadTypeface(string assetPath, Typeface fallback)
    {
        try { return Typeface.CreateFromAsset(_activity.Assets, assetPath) ?? fallback; }
        catch { return fallback; }
    }
}
