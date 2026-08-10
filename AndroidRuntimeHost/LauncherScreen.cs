using Android.App;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace CrashBandicoot.AndroidRuntime;

sealed class LauncherScreen : FrameLayout
{
    readonly Activity _activity;
    readonly Color _orange = Color.Rgb(255, 138, 31);
    readonly Color _ink = Color.Rgb(6, 16, 24);
    readonly Color _pale = Color.Rgb(223, 235, 242);
    readonly Color _muted = Color.Rgb(135, 158, 172);
    readonly Color _good = Color.Rgb(80, 207, 139);
    readonly Color _bad = Color.Rgb(255, 105, 97);

    readonly Typeface _displayFont;
    readonly Typeface _boldFont;
    readonly Typeface _bodyFont;
    readonly Button _selectButton;
    readonly Button _startButton;
    readonly TextView _statusDot;
    readonly TextView _statusTitle;
    readonly TextView _statusDetail;

    public event Action? SelectDiscRequested;
    public event Action? StartGameRequested;

    public LauncherScreen(Activity activity) : base(activity)
    {
        _activity = activity;
        _displayFont = LoadTypeface("Fonts/Bungee-Regular.ttf", Typeface.DefaultBold!);
        _boldFont = LoadTypeface("Fonts/Nunito-ExtraBold.ttf", Typeface.DefaultBold!);
        _bodyFont = LoadTypeface("Fonts/Nunito-SemiBold.ttf", Typeface.Default!);

        _selectButton = ActionButton("SELEZIONA CARTELLA DISCO", primary: true);
        _startButton = ActionButton("AVVIA GIOCO", primary: false);
        _statusDot = Label("●", 15, _muted, _boldFont);
        _statusTitle = Label("Nessun disco selezionato", 16, _pale, _boldFont);
        _statusDetail = Label(
            "Seleziona una cartella con un dump CUE/BIN legale. Nessun dato del gioco è incluso nell'APK.",
            13,
            _muted,
            _bodyFont);

        SetBackgroundColor(Color.Rgb(4, 13, 20));
        SetPadding(Dp(24), Dp(18), Dp(24), Dp(18));
        AddView(BuildTopBar());
        AddView(BuildContent());

        _selectButton.Click += (_, _) => SelectDiscRequested?.Invoke();
        _startButton.Click += (_, _) => StartGameRequested?.Invoke();
        _startButton.Enabled = false;
        _startButton.Alpha = 0.56f;
    }

    public void SetBusy(bool busy, string? detail = null)
    {
        _selectButton.Enabled = !busy;
        _selectButton.Alpha = busy ? 0.62f : 1f;
        _startButton.Enabled = !busy && _startButton.Tag?.ToString() == "ready";
        _startButton.Alpha = _startButton.Enabled ? 1f : 0.56f;
        _selectButton.Text = busy ? "CONTROLLO FILE DEL DISCO…" : "SELEZIONA CARTELLA DISCO";
        if (!busy) return;

        _statusDot.SetTextColor(_orange);
        _statusTitle.Text = "Preparazione in corso";
        _statusDetail.Text = detail ?? "Controllo, copia e ricompilazione avvengono interamente in questa app.";
    }

    public void ShowDisc(bool ready, string title, string detail)
    {
        _statusDot.SetTextColor(ready ? _good : _bad);
        _statusTitle.Text = title;
        _statusDetail.Text = detail;
        _startButton.Tag = ready ? "ready" : null;
        _startButton.Enabled = ready;
        _startButton.Alpha = ready ? 1f : 0.56f;
    }

    public void ShowError(string message)
    {
        SetBusy(false);
        _statusDot.SetTextColor(_bad);
        _statusTitle.Text = "Avvio non riuscito";
        _statusDetail.Text = message;
    }

    View BuildTopBar()
    {
        var bar = new LinearLayout(_activity)
        {
            Orientation = Orientation.Horizontal,
            LayoutParameters = new LayoutParams(LayoutParams.MatchParent, Dp(34), GravityFlags.Top),
        };
        bar.SetGravity(GravityFlags.CenterVertical);
        var left = Label("UNOFFICIAL FAN PROJECT", 11, _orange, _boldFont);
        left.LetterSpacing = 0.16f;
        var right = Label("ANDROID  ·  SINGLE APK  ·  0.2.0 DEV", 11, _muted, _boldFont);
        right.LetterSpacing = 0.10f;
        bar.AddView(left);
        bar.AddView(new Space(_activity), new LinearLayout.LayoutParams(0, 1, 1));
        bar.AddView(right);
        return bar;
    }

    View BuildContent()
    {
        var content = new LinearLayout(_activity)
        {
            Orientation = Orientation.Horizontal,
            LayoutParameters = new LayoutParams(LayoutParams.MatchParent, LayoutParams.MatchParent)
            {
                TopMargin = Dp(34),
            },
        };
        content.SetGravity(GravityFlags.CenterVertical);
        content.AddView(BuildHero(), new LinearLayout.LayoutParams(0, LayoutParams.MatchParent, 0.92f));
        content.AddView(new Space(_activity), new LinearLayout.LayoutParams(Dp(24), 1));
        content.AddView(BuildDiscCard(), new LinearLayout.LayoutParams(0, LayoutParams.MatchParent, 1.08f));
        return content;
    }

    View BuildHero()
    {
        var hero = new LinearLayout(_activity)
        {
            Orientation = Orientation.Vertical,
        };
        hero.SetGravity(GravityFlags.CenterVertical);
        hero.SetPadding(Dp(12), Dp(6), Dp(8), Dp(6));

        var crash = Label("CRASH", 24, _orange, _displayFont);
        crash.SetIncludeFontPadding(true);
        hero.AddView(crash);
        var bandicoot = Label("BANDICOOT", 31, Color.White, _displayFont);
        bandicoot.SetIncludeFontPadding(true);
        bandicoot.SetShadowLayer(14, 0, Dp(3), Color.Argb(130, 0, 0, 0));
        hero.AddView(bandicoot);
        var recompiled = Label("RECOMPILED", 15, _pale, _boldFont);
        recompiled.LetterSpacing = 0.24f;
        hero.AddView(recompiled);
        hero.AddView(Label(
            "Launcher e runtime PS1 riuniti in un'unica app Android.",
            12,
            _muted,
            _bodyFont), new LinearLayout.LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent)
        {
            TopMargin = Dp(6),
            BottomMargin = Dp(12),
        });

        hero.AddView(_startButton, new LinearLayout.LayoutParams(LayoutParams.MatchParent, Dp(44)));
        hero.AddView(_selectButton, new LinearLayout.LayoutParams(LayoutParams.MatchParent, Dp(44))
        {
            TopMargin = Dp(7),
        });

        return hero;
    }

    View BuildDiscCard()
    {
        var card = new LinearLayout(_activity)
        {
            Orientation = Orientation.Vertical,
            Background = Rounded(Color.Rgb(8, 24, 34), Dp(18), Color.Rgb(31, 61, 77)),
        };
        card.SetPadding(Dp(24), Dp(14), Dp(24), Dp(14));

        var eyebrow = Label("UNIFIED ANDROID PORT", 12, _orange, _boldFont);
        eyebrow.LetterSpacing = 0.18f;
        card.AddView(eyebrow);
        card.AddView(Label("Configurazione disco", 22, Color.White, _boldFont),
            new LinearLayout.LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent)
            {
                TopMargin = Dp(3),
            });
        card.AddView(Label(
            "Scegli la cartella con un file .cue e il relativo .bin. Android conserverà l'accesso senza permessi generali alla memoria.",
            12,
            _muted,
            _bodyFont), new LinearLayout.LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent)
        {
            TopMargin = Dp(5),
            BottomMargin = Dp(12),
        });

        var status = new LinearLayout(_activity)
        {
            Orientation = Orientation.Horizontal,
            Background = Rounded(Color.Rgb(5, 18, 27), Dp(13), Color.Rgb(26, 53, 68)),
        };
        status.SetGravity(GravityFlags.Top);
        status.SetPadding(Dp(16), Dp(10), Dp(16), Dp(10));
        status.AddView(_statusDot, new LinearLayout.LayoutParams(Dp(26), LayoutParams.WrapContent));
        var copy = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        copy.AddView(_statusTitle);
        copy.AddView(_statusDetail, new LinearLayout.LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent)
        {
            TopMargin = Dp(4),
        });
        status.AddView(copy, new LinearLayout.LayoutParams(0, LayoutParams.WrapContent, 1));
        card.AddView(status, new LinearLayout.LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent));
        card.AddView(new Space(_activity), new LinearLayout.LayoutParams(1, 0, 1));
        var footer = Label("LAUNCHER + STORAGE + RECOMPILER + RUNTIME  ·  CONNECTED", 10, _muted, _boldFont);
        footer.LetterSpacing = 0.08f;
        card.AddView(footer);
        return card;
    }

    Button ActionButton(string text, bool primary)
    {
        var button = new Button(_activity)
        {
            Text = text,
            TextSize = primary ? 14 : 12,
            Typeface = _boldFont,
            LetterSpacing = 0.07f,
            Gravity = GravityFlags.Center,
            StateListAnimator = null,
            Background = primary
                ? Rounded(_orange, Dp(12))
                : Rounded(Color.Rgb(13, 35, 47), Dp(12), Color.Rgb(35, 67, 83)),
        };
        button.SetMinHeight(0);
        button.SetMinWidth(0);
        button.SetTextColor(primary ? _ink : _pale);
        button.SetPadding(Dp(14), 0, Dp(14), 0);
        return button;
    }

    TextView Label(string text, float size, Color color, Typeface font)
    {
        var label = new TextView(_activity)
        {
            Text = text,
            TextSize = size,
            Typeface = font,
        };
        label.SetIncludeFontPadding(false);
        label.SetTextColor(color);
        label.SetLineSpacing(0, 1.1f);
        return label;
    }

    GradientDrawable Rounded(Color fill, float radius, Color? stroke = null)
    {
        var drawable = new GradientDrawable();
        drawable.SetColor(fill);
        drawable.SetCornerRadius(radius);
        if (stroke.HasValue) drawable.SetStroke(Dp(1), stroke.Value);
        return drawable;
    }

    Typeface LoadTypeface(string assetPath, Typeface fallback)
    {
        try { return Typeface.CreateFromAsset(_activity.Assets, assetPath) ?? fallback; }
        catch { return fallback; }
    }

    int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density + 0.5f);
}
