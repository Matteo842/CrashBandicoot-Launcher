using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CrashBandicoot.Launcher.Recomp;
using RecompOne.Runtime;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Host.Cheats;
using RecompOne.Runtime.Modding;
using static CrashBandicoot.Launcher.Linux.LinuxTheme;

namespace CrashBandicoot.Launcher.Linux;

/// <summary>Sheets (Controls, Settings, Mods, Cheat, About, errors). Same content as <c>NativeLauncherUi</c>.</summary>
sealed partial class LauncherWindow
{
    static readonly (string Label, string Id)[] KeyFields =
    [
        ("Cross", "cross"), ("Circle", "circle"), ("Square", "square"), ("Triangle", "triangle"),
        ("Start", "start"), ("Select", "select"),
        ("Up", "up"), ("Down", "down"), ("Left", "left"), ("Right", "right"),
        ("Cheat menu", "cheatMenu"),
    ];

    static readonly Dictionary<string, string> FocusLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cue"] = "Problem with the .cue",
        ["chd"] = "Problem with the .chd",
        ["bin"] = "Problem with the .bin",
        ["pair"] = ".cue + .bin pair",
        ["game"] = "Wrong game / region",
        ["other"] = "Launcher error",
    };

    /// <summary>Whether the Samples &amp; stubs group is expanded in the Mods sheet.</summary>
    bool _modsStubExpanded;

    // ── Building blocks ───────────────────────────────────────────────

    static TextBlock Label(string text, double width = 200) => new()
    {
        Text = text,
        FontSize = 17,
        Foreground = Brush(Sand),
        Width = width,
        VerticalAlignment = VerticalAlignment.Center,
    };

    static TextBlock Hint(string text, double bottom = 20) =>
        WithMargin(BodyText(text, 14, 160), new Thickness(0, 0, 0, bottom));

    static T WithMargin<T>(T control, Thickness margin) where T : Control
    {
        control.Margin = margin;
        return control;
    }

    static StackPanel Row(Control label, Control control, double bottom) => new()
    {
        Orientation = Orientation.Horizontal,
        Margin = new Thickness(0, 0, 0, bottom),
        Children = { label, control },
    };

    static StackPanel ButtonRow(params Control[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        foreach (var b in buttons) row.Children.Add(b);
        return row;
    }

    static ComboBox MakeCombo((int Value, string Text)[] items, int current)
    {
        var index = Array.FindIndex(items, i => i.Value == current);
        return new ComboBox
        {
            Width = 310,
            MinHeight = 32,
            FontSize = 16,
            ItemsSource = items.Select(i => i.Text).ToArray(),
            SelectedIndex = index < 0 ? 0 : index,
            Tag = items,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    static int ComboValue(ComboBox cb, int fallback) =>
        cb.Tag is (int Value, string Text)[] items && cb.SelectedIndex >= 0 && cb.SelectedIndex < items.Length
            ? items[cb.SelectedIndex].Value
            : fallback;

    static Border Section(string heading, string body) => new()
    {
        Background = Brush(70, 0, 0, 0),
        Padding = new Thickness(12, 8, 12, 10),
        Margin = new Thickness(0, 0, 0, 12),
        Child = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                BodyText(heading.ToUpperInvariant(), 11, 120, bold: true),
                BodyText(body, 13, 230),
            },
        },
    };

    /// <summary>Title on top, buttons at the bottom, scrollable body in between.</summary>
    static Border SheetLayout(double width, string title, Control body, Control buttons, bool scroll)
    {
        var dock = new DockPanel { LastChildFill = true };
        var titleBlock = SheetTitle(title);
        DockPanel.SetDock(titleBlock, Dock.Top);
        dock.Children.Add(titleBlock);
        var footer = WithMargin(buttons, new Thickness(36, 14, 36, 24));
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        if (scroll)
        {
            body.Margin = new Thickness(36, 0, 28, 0);
            dock.Children.Add(new ScrollViewer
            {
                Content = body,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            });
        }
        else
        {
            body.Margin = new Thickness(36, 0, 36, 0);
            dock.Children.Add(body);
        }
        var card = MakeCard(width, dock);
        card.Margin = new Thickness(8);
        return card;
    }

    // ── Sheets ────────────────────────────────────────────────────────

    void ShowAbout()
    {
        var body = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                BodyText("Unofficial fan project — not affiliated with Sony, Activision, or Naughty Dog.", 16, 210),
                BodyText("Unofficial tools for a disc you own. First prepare writes a game folder next to the program — later Starts reuse that.", 16, 210),
                BodyText("Prepared files never replace your dump: you still need a valid NTSC-U .chd or .cue + .bin (SCUS-94900) every time you play.", 16, 210),
            },
        };
        var status = BodyText("", 14, 160);
        body.Children.Add(status);

        var back = new ThemeButton("Back", primary: false);
        back.Click += (_, _) => CloseSheet();
        var menu = new ThemeButton("Add to app menu", primary: false, width: 200);
        menu.Click += (_, _) => status.Text = DesktopEntry.Install(out var detail)
            ? "Added to your applications menu as \"Crash Bandicoot: Recompiled\". Steam can add it from there (Add a Non-Steam Game)."
            : "Could not add the menu entry: " + detail;

        OpenSheet(SheetLayout(598, "About", body, ButtonRow(back, menu), scroll: false), [back, menu]);
    }

    void ShowControls()
    {
        var keys = ConfigManager.Game.Keys;
        var current = new Dictionary<string, string>
        {
            ["cross"] = keys.Cross,
            ["circle"] = keys.Circle,
            ["square"] = keys.Square,
            ["triangle"] = keys.Triangle,
            ["start"] = keys.Start,
            ["select"] = keys.Select,
            ["up"] = keys.Up,
            ["down"] = keys.Down,
            ["left"] = keys.Left,
            ["right"] = keys.Right,
            ["cheatMenu"] = ConfigManager.View.CheatMenuKey,
        };

        var nav = new List<Control>();
        var boxes = new Dictionary<string, KeyCaptureBox>();
        var body = new StackPanel
        {
            Children =
            {
                WithMargin(BodyText("Click a binding, then press a key. Backspace, Shift, and Enter work. Escape cancels.", 16, 210),
                    new Thickness(0, 0, 0, 16)),
            },
        };
        foreach (var (label, id) in KeyFields)
        {
            var box = new KeyCaptureBox { BoundKey = current[id] };
            boxes[id] = box;
            nav.Add(box);
            body.Children.Add(Row(Label(label, 174), box, 8));
        }

        var fpsKeys = new ThemeCheck("FPS mode keys 1–5") { Checked = ConfigManager.View.FrameRateHotkeys };
        nav.Add(fpsKeys);
        body.Children.Add(WithMargin(fpsKeys, new Thickness(0, 8, 0, 4)));
        body.Children.Add(Hint("1 original, 2 60, 3 120, 4 240, 5 uncapped. Uncheck to free those keys.", 0));

        var save = new ThemeButton("Save", primary: true);
        save.Click += (_, _) =>
        {
            string Get(string id) => KeyBindingNames.Canonical(boxes[id].BoundKey);
            keys.Cross = Get("cross");
            keys.Circle = Get("circle");
            keys.Square = Get("square");
            keys.Triangle = Get("triangle");
            keys.Start = Get("start");
            keys.Select = Get("select");
            keys.Up = Get("up");
            keys.Down = Get("down");
            keys.Left = Get("left");
            keys.Right = Get("right");
            var cheatKey = Get("cheatMenu");
            if (!string.IsNullOrWhiteSpace(cheatKey))
                ConfigManager.View.CheatMenuKey = cheatKey;
            ConfigManager.View.FrameRateHotkeys = fpsKeys.Checked;
            ConfigManager.SaveGame();
            SaveView();
            CloseSheet();
        };
        var back = new ThemeButton("Back", primary: false);
        back.Click += (_, _) => CloseSheet();
        nav.Add(save);
        nav.Add(back);

        OpenSheet(SheetLayout(644, "Controls", body, ButtonRow(save, back), scroll: true), nav);
    }

    void ShowSettings()
    {
        var view = ConfigManager.View;
        var game = ConfigManager.Game;
        var nav = new List<Control>();
        T Nav<T>(T c) where T : Control
        {
            nav.Add(c);
            return c;
        }

        var vol = Nav(new ThemeSlider { Value = (int)Math.Round(game.MasterVolume * 100) });
        var muted = Nav(new ThemeCheck("Muted") { Checked = game.Muted });
        var fullscreen = Nav(new ThemeCheck("Fullscreen") { Checked = view.Fullscreen });
        var widescreen = Nav(new ThemeCheck("Native widescreen (16:9)") { Checked = view.Widescreen });
        var frameRate = Nav(MakeCombo(
            [(0, "Original (30 FPS)"), (60, "60 FPS"), (120, "120 FPS"), (240, "240 FPS"), (-1, "Uncapped")],
            view.FrameRate));
        var internalRes = Nav(MakeCombo([(1, "Native (1x)"), (2, "2x"), (4, "4x"), (8, "8x (4K)")],
            view.InternalResolution));
        var texFilter = Nav(MakeCombo([(0, "Off"), (1, "Bilinear"), (2, "Sharp bilinear"), (3, "Soft smooth")],
            view.TextureFilter));
        var strength = Nav(new ThemeSlider { Value = (int)Math.Round(view.TextureFilterStrength * 100) });
        var dedither = Nav(new ThemeCheck("Dedither") { Checked = view.Dedither });
        var dejitter = Nav(new ThemeCheck("Dejitter") { Checked = view.Dejitter });

        var strengthRow = Row(Label("Filter strength", 202), strength, 30);
        void SyncStrength() => strengthRow.IsVisible = ComboValue(texFilter, 0) != 0;
        texFilter.SelectionChanged += (_, _) => SyncStrength();
        SyncStrength();

        var body = new StackPanel
        {
            Children =
            {
                Row(Label("Master volume", 202), vol, 30),
                WithMargin(muted, new Thickness(0, 0, 0, 22)),
                WithMargin(fullscreen, new Thickness(0, 0, 0, 22)),
                WithMargin(widescreen, new Thickness(0, 0, 0, 14)),
                Hint("Native 16:9 gameplay with a wider view. Crates and other objects stay visible in the extra view. Menus, map, and cutscenes stay 4:3."),
                Row(Label("Frame rate", 202), frameRate, 14),
                Hint("Gameplay and bonus. Same speed as 30 FPS, unique frames at the chosen rate. Menus, crate tally, and bonus save stay 30. In-game keys 1–5 switch modes unless turned off in Controls."),
                Row(Label("Internal resolution", 202), internalRes, 14),
                Hint("Applies on next game start. Use 8x on 4K monitors."),
                Row(Label("Texture filter", 202), texFilter, 30),
                strengthRow,
                WithMargin(dedither, new Thickness(0, 0, 0, 22)),
                WithMargin(dejitter, new Thickness(0, 0, 0, 14)),
                Hint("Texture filters auto-off on menus & cutscenes. Dedither and dejitter apply everywhere.", 8),
            },
        };

        var save = Nav(new ThemeButton("Save", primary: true));
        save.Click += (_, _) =>
        {
            game.MasterVolume = Math.Clamp(vol.Value / 100f, 0f, 1f);
            game.Muted = muted.Checked;
            view.Fullscreen = fullscreen.Checked;
            view.Widescreen = widescreen.Checked;
            view.FrameRate = ComboValue(frameRate, 0);
            view.InternalResolution = ComboValue(internalRes, 4);
            view.TextureFilter = ComboValue(texFilter, 0);
            view.TextureFilterStrength = strength.Value / 100f;
            view.Dedither = dedither.Checked;
            view.Dejitter = dejitter.Checked;
            ConfigManager.SaveGame();
            SaveView();
            CloseSheet();
        };
        var back = Nav(new ThemeButton("Back", primary: false));
        back.Click += (_, _) => CloseSheet();

        var card = SheetLayout(644, "Settings", body, ButtonRow(save, back), scroll: true);
        card.VerticalAlignment = VerticalAlignment.Stretch;
        card.Margin = new Thickness(0, 16);
        OpenSheet(card, nav);
    }

    void ShowCheat()
    {
        var lives = new ThemeCheck("Infinite Lives") { Checked = CheatConfig.InfiniteLives };
        var level = new ThemeCheck("Level Select") { Checked = CheatConfig.LevelSelect };
        var god = new ThemeCheck("God Mode") { Checked = CheatConfig.GodMode };
        var fly = new ThemeCheck("Fly  (D-pad/stick, Cross/R1 up, Triangle/L2 down)") { Checked = CheatConfig.Fly };

        var body = new StackPanel
        {
            Children =
            {
                Hint("Cheat toggles for NTSC-U. Applied while the game is running (also open in-game Developer Menu with the hotkey)."),
                WithMargin(lives, new Thickness(0, 0, 0, 10)),
                WithMargin(level, new Thickness(0, 0, 0, 10)),
                WithMargin(god, new Thickness(0, 0, 0, 10)),
                WithMargin(fly, new Thickness(0, 0, 0, 18)),
                Hint("99 Lives (map) and Instant Save Menu are one-shots available in the in-game Developer Menu.", 0),
            },
        };

        var save = new ThemeButton("Save", primary: true);
        save.Click += (_, _) =>
        {
            CheatConfig.InfiniteLives = lives.Checked;
            CheatConfig.LevelSelect = level.Checked;
            CheatConfig.GodMode = god.Checked;
            CheatConfig.Fly = fly.Checked;
            SaveView();
            CloseSheet();
        };
        var back = new ThemeButton("Back", primary: false);
        back.Click += (_, _) => CloseSheet();

        OpenSheet(SheetLayout(598, "Cheat", body, ButtonRow(save, back), scroll: false),
            [lives, level, god, fly, save, back]);
    }

    sealed record ModEntry(string Id, string Name, string Version, string Author, string Category);

    void ShowMods()
    {
        var game = ConfigManager.Game;
        var active = new HashSet<string>(game.ActiveMods ?? [], StringComparer.OrdinalIgnoreCase);
        List<ModEntry> entries;
        try
        {
            entries = ModLoader.DiscoverInfos()
                .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                .Select(m => new ModEntry(m.Id, string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name, m.Version,
                    m.Author, m.ResolvedCategory))
                .ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Launcher] mod discovery failed: {ex.Message}");
            entries = [];
        }
        // Same rule as the runtime: until Mods is saved once, nothing is enabled.
        var enabled = entries.ToDictionary(e => e.Id, e => game.ModsConfigured && active.Contains(e.Id),
            StringComparer.OrdinalIgnoreCase);

        var groups = entries
            .GroupBy(e => e.Category, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => CategoryRank(g.Key.ToLowerInvariant()))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Key: g.Key.ToLowerInvariant(),
                Title: CategoryTitle(g.Key.ToLowerInvariant()),
                Entries: g.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
        var expanded = groups.ToDictionary(g => g.Key, g => g.Key != "stub" || _modsStubExpanded);

        var list = new StackPanel { Margin = new Thickness(0, 0, 14, 8) };
        var scroll = new ScrollViewer
        {
            Content = list,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Margin = new Thickness(28, 0, 28, 0),
        };

        var openBtn = new ThemeButton("Open mods folder", primary: true, width: 200);
        openBtn.Click += (_, _) => OpenFolder(AppPaths.ModsDir);
        var save = new ThemeButton("Save", primary: true);
        save.Click += (_, _) =>
        {
            game.ModsConfigured = true;
            game.ActiveMods = enabled.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
            ConfigManager.SaveGame();
            CloseSheet();
        };
        var back = new ThemeButton("Back", primary: false);
        back.Click += (_, _) => CloseSheet();

        var listNav = new List<Control>();

        void Rebuild(string? focusGroup)
        {
            list.Children.Clear();
            listNav.Clear();
            Control? focus = null;
            if (groups.Count == 0)
            {
                list.Children.Add(WithMargin(
                    BodyText("(no mods found — open the folder and add a mod.json package)", 16, 160),
                    new Thickness(10, 12, 10, 0)));
            }

            foreach (var g in groups)
            {
                var open = expanded[g.Key];
                var header = new ModGroupHeader(g.Title, g.Entries.Count, open) { Margin = new Thickness(0, 0, 0, 10) };
                var key = g.Key;
                header.Toggled += (_, _) =>
                {
                    expanded[key] = !expanded[key];
                    if (key == "stub") _modsStubExpanded = expanded[key];
                    Rebuild(key);
                };
                list.Children.Add(header);
                listNav.Add(header);
                if (key == focusGroup) focus = header;

                if (!open)
                {
                    header.Margin = new Thickness(0, 0, 0, 24);
                    continue;
                }

                foreach (var mod in g.Entries)
                {
                    var label = string.IsNullOrWhiteSpace(mod.Version) ? mod.Name : $"{mod.Name}  v{mod.Version}";
                    if (!string.IsNullOrWhiteSpace(mod.Author))
                        label += $"  —  {mod.Author}";
                    var check = new ThemeCheck(label) { Checked = enabled[mod.Id] };
                    var id = mod.Id;
                    check.CheckedChanged += (_, _) => enabled[id] = check.Checked;
                    listNav.Add(check);
                    list.Children.Add(new Border
                    {
                        Background = Brush(Color.FromRgb(28, 18, 14)),
                        BorderBrush = Brush(55, 255, 180, 80),
                        BorderThickness = new Thickness(1),
                        Height = 64,
                        Margin = new Thickness(0, 0, 0, 14),
                        Child = new Panel
                        {
                            Children =
                            {
                                WithMargin(check, new Thickness(16, 6, 16, 0)),
                                WithMargin(new TextBlock
                                {
                                    Text = mod.Id,
                                    FontSize = 12,
                                    Foreground = Brush(130, Sand),
                                    TextTrimming = TextTrimming.CharacterEllipsis,
                                }, new Thickness(50, 38, 16, 0)),
                            },
                        },
                    });
                    check.VerticalAlignment = VerticalAlignment.Top;
                }
                list.Children[^1].Margin = new Thickness(0, 0, 0, 32);
            }

            SetNav(listNav.Concat([openBtn, save, back]));
            if (focus != null) FocusNav(focus);
        }

        Rebuild(null);

        var dock = new DockPanel();
        var title = SheetTitle("Mods");
        DockPanel.SetDock(title, Dock.Top);
        dock.Children.Add(title);
        var hint = Hint("Drop mods into the mods folder next to the program. Enable/disable here; restart the game to apply hooks. Asset packs can hot-reload in-game.", 14);
        hint.Margin = new Thickness(36, 0, 36, 14);
        DockPanel.SetDock(hint, Dock.Top);
        dock.Children.Add(hint);
        var footer = new DockPanel
        {
            Margin = new Thickness(36, 14, 36, 22),
            Children = { openBtn, ButtonRow(save, back) },
        };
        DockPanel.SetDock(openBtn, Dock.Left);
        footer.Children[1].HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        dock.Children.Add(scroll);

        var card = MakeCard(double.NaN, dock);
        card.HorizontalAlignment = HorizontalAlignment.Stretch;
        card.VerticalAlignment = VerticalAlignment.Stretch;
        card.Margin = new Thickness(10);
        OpenSheet(card, listNav.Concat([openBtn, save, back]).ToList());
    }

    static int CategoryRank(string key) => key switch
    {
        "gameplay" => 0,
        "assets" => 1,
        "installed" => 2,
        "stub" => 100,
        _ => 50,
    };

    static string CategoryTitle(string key) => key switch
    {
        "gameplay" => "Gameplay",
        "assets" => "Assets",
        "installed" => "Installed",
        "stub" => "Samples & stubs",
        _ => string.IsNullOrWhiteSpace(key) ? "Installed" : char.ToUpperInvariant(key[0]) + key[1..],
    };

    void ShowDiscError(DiscValidation v, string? clearedPrevious = null)
    {
        var fix = v.Fix;
        if (!string.IsNullOrWhiteSpace(clearedPrevious))
            fix += " Your previous disc selection was cleared so Start cannot silently use the old dump.";
        ShowErrorSheet(
            string.IsNullOrWhiteSpace(v.Title) ? "Disc problem" : v.Title,
            string.IsNullOrWhiteSpace(v.Problem) ? v.Message : v.Problem,
            fix,
            string.IsNullOrWhiteSpace(v.Focus) ? "other" : v.Focus,
            v.CuePath,
            v.BinPath);
    }

    /// <summary>Error card: what went wrong / how to fix it, with Select disc (or Open logs) and Close.</summary>
    void ShowErrorSheet(string title, string problem, string fix, string focus,
        string? cuePath = null, string? binPath = null, bool logsButton = false)
    {
        HidePrep();
        FocusLabels.TryGetValue(focus, out var focusLabel);
        focusLabel ??= FocusLabels["other"];

        var body = new StackPanel
        {
            Children =
            {
                new Border
                {
                    Background = Brush(40, 255, 138, 0),
                    Padding = new Thickness(10, 4),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 14),
                    Child = new TextBlock
                    {
                        Text = focusLabel,
                        FontSize = 13,
                        FontWeight = FontWeight.ExtraBold,
                        Foreground = Brush(Wumpa),
                    },
                },
                Section("What went wrong", problem),
                Section("How to fix it", fix),
            },
        };

        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(cuePath))
            paths.Add(Path.GetExtension(cuePath) + "  " + cuePath);
        if (!string.IsNullOrWhiteSpace(binPath))
            paths.Add(".bin  " + binPath);
        if (paths.Count > 0)
            body.Children.Add(BodyText(string.Join("\n", paths), 11, 180));

        ThemeButton primary;
        if (logsButton)
        {
            primary = new ThemeButton("Open logs", primary: true, width: 180);
            primary.Click += (_, _) => OpenFolder(AppPaths.LogsDir);
        }
        else
        {
            primary = new ThemeButton("Select disc", primary: true, width: 180);
            primary.Click += (_, _) =>
            {
                CloseSheet();
                _ = PickDiscAsync();
            };
        }
        var close = new ThemeButton("Close", primary: false);
        close.Click += (_, _) => CloseSheet();

        OpenSheet(SheetLayout(644, title, body, ButtonRow(primary, close), scroll: true), [primary, close]);
    }

    void ShowDesktopWarning()
    {
        var body = new StackPanel
        {
            Children =
            {
                Section("Why this matters",
                    "This launcher creates folders next to the program (save, game, logs, mods) and a settings file. On the Desktop that clutters your workspace."),
                Section("What to do",
                    "Move the program into its own folder (for example ~/Games/CrashBandicoot) and run it from there."),
                BodyText("Current folder  " + AppPaths.Root, 13, 180),
            },
        };
        var got = new ThemeButton("Got it", primary: true);
        got.Click += (_, _) => CloseSheet();
        OpenSheet(SheetLayout(644, "Running from the Desktop", body, ButtonRow(got), scroll: false), [got]);
    }

    /// <summary>Persist ViewConfig (fullscreen, cheats, keys) without an ImGui layout, like LauncherHost.</summary>
    static void SaveView() =>
        ConfigManager.SaveView(Array.Empty<RecompOne.Runtime.Host.Window.IPanel>());
}
