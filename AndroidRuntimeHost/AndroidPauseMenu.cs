using Android.App;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Host.Cheats;

namespace CrashBandicoot.AndroidRuntime;

/// <summary>
/// Android counterpart of the desktop Start pause menu (<see cref="StartPauseMenu"/>):
/// Start opens it where retail Crash would pause, the game thread is held at the
/// frame clock while it is up, and it is driven by touch or pad (D-pad / stick
/// up-down, Cross picks, Circle or Start resumes, Select exits like the retail pause).
/// </summary>
sealed class AndroidPauseMenu : FrameLayout
{
    static readonly Color Panel = Color.Argb(205, 20, 20, 20);
    static readonly Color Sand = Color.Rgb(244, 228, 188);
    static readonly Color Wumpa = Color.Rgb(255, 176, 32);
    static readonly Color Night = Color.Rgb(6, 16, 24);

    readonly Activity _activity;
    readonly Action _freeze;
    readonly Action _unfreeze;
    readonly Action _openDevMenu;
    readonly LinearLayout _list;
    readonly TextView _hint;
    readonly Typeface _titleFont;
    readonly Typeface _rowFont;
    readonly List<(Button view, Action pick, bool enabled)> _rows = new();
    int _selected;
    volatile bool _open;

    /// <summary>Open or opening; safe to read from the game thread.</summary>
    public bool IsOpen => _open;

    public AndroidPauseMenu(Activity activity, Action freeze, Action unfreeze, Action openDevMenu)
        : base(activity)
    {
        _activity = activity;
        _freeze = freeze;
        _unfreeze = unfreeze;
        _openDevMenu = openDevMenu;
        _titleFont = Load("Fonts/Bungee-Regular.ttf", Typeface.DefaultBold!);
        _rowFont = Load("Fonts/Nunito-ExtraBold.ttf", Typeface.DefaultBold!);

        Visibility = ViewStates.Gone;
        Clickable = true;
        SetBackgroundColor(Color.Argb(140, 0, 0, 0));

        var card = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        card.SetPadding(Dp(18), Dp(10), Dp(18), Dp(8));
        var bg = new GradientDrawable();
        bg.SetColor(Panel);
        bg.SetStroke(Dp(1), Color.Argb(90, 255, 255, 255));
        bg.SetCornerRadius(Dp(6));
        card.Background = bg;

        var title = new TextView(activity) { Text = "PAUSED", TextSize = 18, Gravity = GravityFlags.Center };
        title.SetTypeface(_titleFont, TypefaceStyle.Normal);
        title.SetTextColor(Wumpa);
        card.AddView(title, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = Dp(4),
        });

        // Rows scroll when the phone is short (landscape is ~360 dp tall).
        _list = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        var scroll = new ScrollView(activity) { VerticalScrollBarEnabled = false };
        scroll.AddView(_list);
        card.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        _hint = new TextView(activity) { TextSize = 12, Gravity = GravityFlags.Center };
        _hint.SetTextColor(Color.Rgb(170, 170, 170));
        card.AddView(_hint, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = Dp(6),
        });

        var cardParams = new LayoutParams(Dp(320), ViewGroup.LayoutParams.WrapContent, GravityFlags.Center)
        {
            TopMargin = Dp(12),
            BottomMargin = Dp(12),
        };
        AddView(card, cardParams);
    }

    /// <summary>Open from the game thread (<see cref="StartPauseMenu.PlatformOpen"/>).</summary>
    public void RequestOpen()
    {
        if (_open) return;
        _open = true;
        _activity.RunOnUiThread(Show);
    }

    public void Open()
    {
        if (Visibility == ViewStates.Visible) return;
        _open = true;
        Show();
    }

    void Show()
    {
        RecompOne.Runtime.Hardware.Controller.SetVirtualPadState(0);
        Build();
        Visibility = ViewStates.Visible;
        BringToFront();
        _freeze();
    }

    public void Close()
    {
        if (Visibility != ViewStates.Visible) return;
        Visibility = ViewStates.Gone;
        _open = false;
        RecompOne.Runtime.Hardware.Controller.SetVirtualPadState(0);
        _unfreeze();
    }

    void Build()
    {
        _rows.Clear();
        _list.RemoveAllViews();
        bool mapPause = StartPauseMenu.MapPauseAllowed();
        bool canExit = mapPause || !CheatManager.IsOnTitleMenuMap();
        int slot = SaveStates.Slot;
        bool canLoad = SaveStates.GetInfo(slot) is { Compatible: true };

        Row("Resume", Close);
        Row($"Save State  (slot {slot})", () => { SaveStates.RequestSave(slot); Close(); Toast($"Saving slot {slot}…"); });
        Row($"Load State  (slot {slot})", () => { SaveStates.RequestLoad(slot); Close(); }, canLoad);
        Row(mapPause ? "Exit to Title" : "Exit to Map", () =>
        {
            Close();
            // Native Crash 1 path: Start (pause) then Select.
            ExitToMapInjector.Begin();
        }, canExit);
        Row("Developer Menu", () => { Close(); _openDevMenu(); });

        _hint.Text = "Cross choose · Circle/Start resume · Select " + (mapPause ? "title" : "map");
        _selected = 0;
        Highlight();
    }

    void Row(string label, Action pick, bool enabled = true)
    {
        var button = new Button(_activity)
        {
            Text = label,
            TextSize = 15,
            Gravity = GravityFlags.Center,
            StateListAnimator = null,
            Enabled = enabled,
        };
        button.SetTypeface(_rowFont, TypefaceStyle.Normal);
        button.SetMinHeight(0);
        button.SetMinWidth(0);
        button.SetAllCaps(false);
        int index = _rows.Count;
        button.Click += (_, _) => { _selected = index; pick(); };
        _rows.Add((button, pick, enabled));
        _list.AddView(button, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(42))
        {
            TopMargin = Dp(5),
        });
    }

    void Highlight()
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            var (view, _, enabled) = _rows[i];
            bool on = i == _selected;
            var bg = new GradientDrawable();
            bg.SetColor(on ? Color.Argb(220, 255, 176, 32) : Color.Argb(179, 42, 42, 42));
            bg.SetStroke(Dp(1), Color.Argb(80, 255, 255, 255));
            bg.SetCornerRadius(Dp(3));
            view.Background = bg;
            view.SetTextColor(!enabled ? Color.Rgb(110, 110, 110) : on ? Night : Sand);
        }
    }

    /// <summary>Pad buttons while the menu is open (UI thread). True = handled.</summary>
    public bool HandlePadKey(Keycode key)
    {
        switch (key)
        {
            case Keycode.DpadUp: Move(-1); return true;
            case Keycode.DpadDown: Move(1); return true;
            case Keycode.ButtonA:
            case Keycode.DpadCenter:
                var row = _rows[_selected];
                if (row.enabled) row.pick();
                return true;
            case Keycode.ButtonB:
            case Keycode.ButtonStart:
            case Keycode.Back:
                Close();
                return true;
            case Keycode.ButtonSelect:
                if (_rows.Count > 3 && _rows[3].enabled) _rows[3].pick();
                return true;
        }
        return true; // everything else stays out of the frozen game
    }

    /// <summary>Stick / hat up-down edge (−1 up, +1 down).</summary>
    public void HandlePadDirection(int delta) => Move(delta);

    void Move(int delta)
    {
        if (_rows.Count == 0) return;
        int next = _selected;
        for (int i = 0; i < _rows.Count; i++)
        {
            next = (next + delta + _rows.Count) % _rows.Count;
            if (_rows[next].enabled) break;
        }
        _selected = next;
        Highlight();
    }

    void Toast(string text) =>
        Android.Widget.Toast.MakeText(_activity, text, ToastLength.Short)?.Show();

    Typeface Load(string asset, Typeface fallback)
    {
        try { return Typeface.CreateFromAsset(_activity.Assets, asset) ?? fallback; }
        catch { return fallback; }
    }

    int Dp(int value) => (int)(value * (_activity.Resources?.DisplayMetrics?.Density ?? 1f) + 0.5f);
}
