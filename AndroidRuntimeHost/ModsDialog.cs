using Android.App;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Modding;

namespace CrashBandicoot.AndroidRuntime;

/// <summary>Android counterpart of the desktop launcher Mods sheet.</summary>
static class ModsDialog
{
    static readonly Color Night = Color.Rgb(6, 16, 24);
    static readonly Color Card = Color.Rgb(11, 35, 27);
    static readonly Color Wumpa = Color.Rgb(255, 138, 0);
    static readonly Color Sand = Color.Rgb(244, 228, 188);
    static readonly Color Muted = Color.Rgb(177, 188, 188);
    static readonly Color Row = Color.Rgb(28, 18, 14);

    static Controller? _open;

    public static void Show(Activity activity, Action importRequested) =>
        new Controller(activity, importRequested).Show();

    public static void ReloadIfOpen() => _open?.Reload();

    public static void NotifyImported(string id)
    {
        _open?.MarkImported(id);
        ReloadIfOpen();
    }

    sealed class Controller
    {
        readonly Activity _activity;
        readonly Action _importRequested;
        readonly Dialog _dialog;
        readonly Typeface _displayFont;
        readonly Typeface _bodyFont;
        readonly Typeface _bodyBold;
        readonly LinearLayout _list;
        readonly Dictionary<string, bool> _enabled = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, bool> _expanded = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _imported = new(StringComparer.OrdinalIgnoreCase);
        bool _stubsOpened;

        public Controller(Activity activity, Action importRequested)
        {
            _activity = activity;
            _importRequested = importRequested;
            _dialog = new Dialog(activity);
            _displayFont = LoadTypeface(activity, "Fonts/Bungee-Regular.ttf", Typeface.DefaultBold!);
            _bodyFont = LoadTypeface(activity, "Fonts/Nunito-SemiBold.ttf", Typeface.Default!);
            _bodyBold = LoadTypeface(activity, "Fonts/Nunito-ExtraBold.ttf", Typeface.DefaultBold!);
            _list = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        }

        public void Show()
        {
            _dialog.RequestWindowFeature((int)WindowFeatures.NoTitle);
            _dialog.SetCanceledOnTouchOutside(false);

            var root = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
            root.SetPadding(Dp(22), Dp(15), Dp(22), Dp(15));
            root.Background = RoundedBackground(Card, Color.Argb(170, 255, 176, 32), Dp(1), Dp(3));

            root.AddView(Text("MODS", 19, Wumpa, _displayFont));
            var hint = Text(
                "Import a .zip with mod.json at the root. Enable packs here; C# hooks apply on the next game start. Asset packs can hot-reload in-game.",
                10.5f, Muted, _bodyFont);
            hint.SetLineSpacing(0, 1.08f);
            root.AddView(hint, Margin(top: 5, bottom: 8));

            var scroll = new ScrollView(_activity)
            {
                FillViewport = true,
                VerticalScrollBarEnabled = true,
            };
            scroll.AddView(_list);
            root.AddView(scroll, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, 0, 1f));

            var actions = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
            actions.SetGravity(GravityFlags.Center);
            var import = Button("IMPORT ZIP", primary: false);
            var save = Button("SAVE", primary: true);
            var back = Button("BACK", primary: false);
            actions.AddView(import, Weighted());
            actions.AddView(save, Weighted());
            actions.AddView(back, Weighted());
            root.AddView(actions, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, Dp(40))
            {
                TopMargin = Dp(7),
            });

            import.Click += (_, _) => _importRequested();
            save.Click += (_, _) =>
            {
                var ids = _enabled.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
                ConfigManager.Game.ModsConfigured = true;
                ConfigManager.Game.ActiveMods = ids;
                ConfigManager.SaveGame();
                Toast.MakeText(_activity, "Saved. Restart the game to apply C# hooks.",
                    ToastLength.Short)?.Show();
                _dialog.Dismiss();
            };
            back.Click += (_, _) => _dialog.Dismiss();

            RebuildList();

            _dialog.SetContentView(root);
            _dialog.DismissEvent += (_, _) =>
            {
                if (ReferenceEquals(_open, this)) _open = null;
                _dialog.Dispose();
            };
            _open = this;
            _dialog.Show();
            ConfigureWindow(_dialog, _activity, 0.78f,
                (int)((_activity.Resources?.DisplayMetrics?.HeightPixels ?? 900) * 0.89f), 0.78f);
        }

        public void Reload() => _activity.RunOnUiThread(RebuildList);

        public void MarkImported(string id)
        {
            _imported.Add(id);
            _enabled[id] = true;
        }

        void RebuildList()
        {
            _list.RemoveAllViews();

            var discovered = ModLoader.DiscoverInfos();
            var configured = ConfigManager.Game.ModsConfigured;
            var active = new HashSet<string>(
                ConfigManager.Game.ActiveMods ?? [], StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (discovered.Count == 0)
            {
                _list.AddView(Text(
                    "No mods yet. Import a .zip whose root contains mod.json.",
                    12, Muted, _bodyFont), Margin(top: 12, bottom: 12));
                return;
            }

            foreach (var mod in discovered)
            {
                seen.Add(mod.Id);
                if (_enabled.ContainsKey(mod.Id)) continue;
                _enabled[mod.Id] = _imported.Contains(mod.Id) || !configured || active.Contains(mod.Id);
            }

            foreach (var stale in _enabled.Keys.Where(id => !seen.Contains(id)).ToList())
                _enabled.Remove(stale);

            var groups = discovered
                .GroupBy(m => m.ResolvedCategory, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => Rank(g.Key))
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var group in groups)
            {
                var key = group.Key.ToLowerInvariant();
                if (!_expanded.ContainsKey(key))
                    _expanded[key] = key != "stub" || _stubsOpened;
                var open = _expanded[key];

                var header = CategoryHeader(CategoryTitle(key), group.Count(), open);
                header.Click += (_, _) =>
                {
                    _expanded[key] = !_expanded[key];
                    if (key == "stub") _stubsOpened = _expanded[key];
                    RebuildList();
                };
                _list.AddView(header, new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent, Dp(40))
                {
                    TopMargin = Dp(6),
                    BottomMargin = Dp(4),
                });

                if (!open) continue;

                foreach (var mod in group.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                {
                    _list.AddView(ModRow(mod, _enabled[mod.Id]), new LinearLayout.LayoutParams(
                        ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
                    {
                        TopMargin = Dp(4),
                        BottomMargin = Dp(4),
                    });
                }
            }
        }

        LinearLayout CategoryHeader(string title, int count, bool expanded)
        {
            var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
            row.Clickable = true;
            row.Focusable = true;
            row.SetPadding(Dp(12), 0, Dp(12), 0);
            row.SetGravity(GravityFlags.CenterVertical);
            row.Background = RoundedBackground(Color.Rgb(18, 48, 38), Color.Argb(70, 255, 160, 40), Dp(1), Dp(2));
            var arrow = expanded ? "▼" : "▶";
            row.AddView(Text($"{arrow}  {title.ToUpperInvariant()}", 12, Wumpa, _bodyBold),
                new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            row.AddView(Text(count == 1 ? "1 mod" : $"{count} mods", 11, Muted, _bodyFont));
            return row;
        }

        LinearLayout ModRow(ModInfo mod, bool enabled)
        {
            var row = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
            row.SetPadding(Dp(12), Dp(8), Dp(12), Dp(8));
            row.Background = RoundedBackground(Row, Color.Argb(55, 255, 180, 80), Dp(1), Dp(2));

            var top = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
            top.SetGravity(GravityFlags.CenterVertical);

            var toggle = new Switch(_activity)
            {
                Text = string.IsNullOrWhiteSpace(mod.Version) ? mod.Name : $"{mod.Name}  v{mod.Version}",
                TextSize = 13,
                Checked = enabled,
            };
            toggle.SetTypeface(_bodyBold, TypefaceStyle.Normal);
            toggle.SetTextColor(Sand);
            toggle.CheckedChange += (_, e) => _enabled[mod.Id] = e.IsChecked;
            top.AddView(toggle, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            if (!AndroidMods.IsStockSample(mod))
            {
                var remove = Button("DEL", primary: false);
                remove.TextSize = 9;
                remove.Click += (_, _) =>
                {
                    var ask = new AlertDialog.Builder(_activity);
                    ask.SetMessage($"Remove {mod.Name}?");
                    ask.SetPositiveButton("Remove", (_, _) =>
                    {
                        AndroidMods.Remove(mod);
                        _enabled.Remove(mod.Id);
                        RebuildList();
                    });
                    ask.SetNegativeButton("Cancel", (_, _) => { });
                    ask.Show();
                };
                top.AddView(remove, new LinearLayout.LayoutParams(Dp(56), Dp(32))
                {
                    LeftMargin = Dp(6),
                });
            }

            row.AddView(top);
            var meta = string.IsNullOrWhiteSpace(mod.Author) ? mod.Id : $"{mod.Id}  ·  {mod.Author}";
            row.AddView(Text(meta, 10, Muted, _bodyFont), Margin(top: 2));
            return row;
        }

        Button Button(string value, bool primary)
        {
            var button = new Button(_activity)
            {
                Text = value,
                TextSize = 10,
                Gravity = GravityFlags.Center,
                StateListAnimator = null,
            };
            button.SetTypeface(_bodyBold, TypefaceStyle.Normal);
            button.SetTextColor(primary ? Night : Sand);
            button.SetMinHeight(0);
            button.SetMinWidth(0);
            button.Background = RoundedBackground(
                primary ? Wumpa : Color.Rgb(31, 19, 13),
                Color.Argb(210, 255, 176, 32), Dp(1), Dp(2));
            return button;
        }

        TextView Text(string value, float size, Color color, Typeface font)
        {
            var text = new TextView(_activity)
            {
                Text = value,
                TextSize = size,
                Gravity = GravityFlags.Left | GravityFlags.CenterVertical,
            };
            text.SetTypeface(font, TypefaceStyle.Normal);
            text.SetTextColor(color);
            text.SetIncludeFontPadding(false);
            return text;
        }

        LinearLayout.LayoutParams Weighted() =>
            new(0, ViewGroup.LayoutParams.MatchParent, 1f)
            {
                LeftMargin = Dp(4),
                RightMargin = Dp(4),
            };

        LinearLayout.LayoutParams Margin(int top = 0, int bottom = 0) =>
            new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            {
                TopMargin = Dp(top),
                BottomMargin = Dp(bottom),
            };

        int Dp(float value) =>
            (int)(value * (_activity.Resources?.DisplayMetrics?.Density ?? 1f) + 0.5f);
    }

    static int Rank(string key) => key.ToLowerInvariant() switch
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
        _ => string.IsNullOrWhiteSpace(key)
            ? "Installed"
            : char.ToUpperInvariant(key[0]) + key[1..],
    };

    static void ConfigureWindow(Dialog dialog, Activity activity, float widthFraction, int height, float dim)
    {
        if (dialog.Window == null) return;
        dialog.Window.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
        dialog.Window.AddFlags(WindowManagerFlags.DimBehind);
        dialog.Window.SetDimAmount(dim);
        var displayWidth = activity.Resources?.DisplayMetrics?.WidthPixels ?? 1200;
        dialog.Window.SetLayout((int)(displayWidth * widthFraction), height);
        dialog.Window.SetGravity(GravityFlags.Center);
    }

    static GradientDrawable RoundedBackground(Color fill, Color stroke, int strokeWidth, float radius)
    {
        var drawable = new GradientDrawable();
        drawable.SetColor(fill);
        drawable.SetCornerRadius(radius);
        drawable.SetStroke(strokeWidth, stroke);
        return drawable;
    }

    static Typeface LoadTypeface(Activity activity, string assetPath, Typeface fallback)
    {
        try { return Typeface.CreateFromAsset(activity.Assets, assetPath) ?? fallback; }
        catch { return fallback; }
    }
}
