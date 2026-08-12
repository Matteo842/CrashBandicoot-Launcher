using Android.App;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace CrashBandicoot.AndroidRuntime;

static class TouchControlsDialog
{
    static readonly Color Night = Color.Rgb(6, 16, 24);
    static readonly Color Card = Color.Rgb(11, 35, 27);
    static readonly Color Wumpa = Color.Rgb(255, 138, 0);
    static readonly Color Sand = Color.Rgb(244, 228, 188);
    static readonly Color Muted = Color.Rgb(177, 188, 188);

    public static void Show(Activity activity)
    {
        var settings = new TouchControlSettings(activity);
        var displayFont = LoadTypeface(activity, "Fonts/Bungee-Regular.ttf", Typeface.DefaultBold!);
        var bodyFont = LoadTypeface(activity, "Fonts/Nunito-SemiBold.ttf", Typeface.Default!);
        var bodyBold = LoadTypeface(activity, "Fonts/Nunito-ExtraBold.ttf", Typeface.DefaultBold!);

        var dialog = new Dialog(activity);
        dialog.RequestWindowFeature((int)WindowFeatures.NoTitle);
        dialog.SetCanceledOnTouchOutside(false);

        var root = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        root.SetPadding(Dp(activity, 22), Dp(activity, 16), Dp(activity, 22), Dp(activity, 16));
        root.Background = RoundedBackground(Card, Color.Argb(170, 255, 176, 32),
            Dp(activity, 1), Dp(activity, 3));

        var card = new LinearLayout(activity) { Orientation = Orientation.Vertical };

        var title = Text(activity, "TOUCH CONTROLS", 19, Wumpa, displayFont);
        card.AddView(title);

        var explanation = Text(activity,
            "Crash 1 uses the D-pad, X, Square/Circle, Triangle, and Start. " +
            "Shoulder buttons are optional and stay hidden by default. " +
            "A Bluetooth or USB controller hides this overlay automatically.",
            11, Muted, bodyFont);
        explanation.SetLineSpacing(0, 1.08f);
        card.AddView(explanation, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = Dp(activity, 6),
            BottomMargin = Dp(activity, 7),
        });

        var enabled = new Switch(activity)
        {
            Text = "Touch controls enabled",
            TextSize = 12,
            Checked = settings.Enabled,
        };
        enabled.SetTypeface(bodyBold, TypefaceStyle.Normal);
        enabled.SetTextColor(Sand);
        enabled.CheckedChange += (_, e) => settings.SetEnabled(e.IsChecked);
        card.AddView(enabled, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(activity, 36)));

        var colors = new Switch(activity)
        {
            Text = "Colored buttons",
            TextSize = 12,
            Checked = settings.UseColors,
        };
        colors.SetTypeface(bodyBold, TypefaceStyle.Normal);
        colors.SetTextColor(Sand);
        colors.CheckedChange += (_, e) => settings.SetUseColors(e.IsChecked);
        card.AddView(colors, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(activity, 36)));

        AddSlider(activity, card, "Opacity", 20, 100,
            (int)MathF.Round(settings.Opacity * 100f), value => settings.SetOpacity(value / 100f),
            bodyFont, bodyBold);
        AddSlider(activity, card, "Size", 70, 140,
            (int)MathF.Round(settings.Scale * 100f), value => settings.SetScale(value / 100f),
            bodyFont, bodyBold);

        var shoulders = new Switch(activity)
        {
            Text = "Show L1 / L2 / R1 / R2",
            TextSize = 12,
            Checked = settings.ShowShoulders,
        };
        shoulders.SetTypeface(bodyBold, TypefaceStyle.Normal);
        shoulders.SetTextColor(Sand);
        shoulders.CheckedChange += (_, e) => settings.SetShowShoulders(e.IsChecked);
        card.AddView(shoulders, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(activity, 40)));

        var scroll = new ScrollView(activity);
        scroll.AddView(card);
        root.AddView(scroll, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f));

        var actions = new LinearLayout(activity)
        {
            Orientation = Orientation.Horizontal,
        };
        actions.SetGravity(GravityFlags.Center);
        var edit = Button(activity, "EDIT POSITIONS", bodyBold);
        var reset = Button(activity, "RESET", bodyBold);
        var back = Button(activity, "BACK", bodyBold);
        actions.AddView(edit, WeightedButtonLayout(activity));
        actions.AddView(reset, WeightedButtonLayout(activity));
        actions.AddView(back, WeightedButtonLayout(activity));
        root.AddView(actions, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(activity, 40))
        {
            TopMargin = Dp(activity, 7),
        });

        edit.Click += (_, _) =>
        {
            dialog.Dismiss();
            ShowEditor(activity, settings, bodyBold);
        };
        reset.Click += (_, _) =>
        {
            settings.Reset();
            dialog.Dismiss();
            Show(activity);
        };
        back.Click += (_, _) => dialog.Dismiss();

        dialog.SetContentView(root);
        dialog.DismissEvent += (_, _) => dialog.Dispose();
        AndroidGamepad.BindDialog(dialog);
        dialog.Show();
        ConfigureWindow(dialog, activity, 0.68f,
            (int)((activity.Resources?.DisplayMetrics?.HeightPixels ?? 900) * 0.86f), 0.76f);
    }

    static void ShowEditor(Activity activity, TouchControlSettings settings, Typeface bodyBold)
    {
        var dialog = new Dialog(activity);
        dialog.RequestWindowFeature((int)WindowFeatures.NoTitle);
        dialog.SetCanceledOnTouchOutside(false);

        var root = new FrameLayout(activity);
        root.SetBackgroundColor(Night);
        root.Background = RoundedBackground(Night, Color.Argb(170, 255, 176, 32),
            Dp(activity, 1), Dp(activity, 3));

        var editor = new TouchControllerView(activity, settings, editing: true);
        root.AddView(editor, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        var toolbar = new LinearLayout(activity)
        {
            Orientation = Orientation.Horizontal,
        };
        toolbar.SetGravity(GravityFlags.Center);
        toolbar.SetPadding(Dp(activity, 10), Dp(activity, 5), Dp(activity, 10), Dp(activity, 5));
        toolbar.Background = RoundedBackground(Color.Argb(220, 5, 14, 20),
            Color.Argb(150, 255, 176, 32), Dp(activity, 1), Dp(activity, 2));

        var hint = Text(activity, "DRAG THE GROUPS", 11, Sand, bodyBold);
        hint.Gravity = GravityFlags.Center;
        toolbar.AddView(hint, new LinearLayout.LayoutParams(Dp(activity, 150), Dp(activity, 34)));
        var reset = Button(activity, "RESET", bodyBold);
        var done = Button(activity, "DONE", bodyBold);
        toolbar.AddView(reset, new LinearLayout.LayoutParams(Dp(activity, 92), Dp(activity, 34))
        {
            LeftMargin = Dp(activity, 6),
        });
        toolbar.AddView(done, new LinearLayout.LayoutParams(Dp(activity, 92), Dp(activity, 34))
        {
            LeftMargin = Dp(activity, 6),
        });
        root.AddView(toolbar, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent,
            GravityFlags.Top | GravityFlags.CenterHorizontal)
        {
            TopMargin = Dp(activity, 7),
        });

        reset.Click += (_, _) => editor.ResetLayout();
        done.Click += (_, _) =>
        {
            settings.CommitLayout();
            dialog.Dismiss();
        };

        dialog.SetContentView(root);
        dialog.DismissEvent += (_, _) => dialog.Dispose();
        AndroidGamepad.BindDialog(dialog);
        dialog.Show();
        ConfigureWindow(dialog, activity, 0.96f,
            (int)((activity.Resources?.DisplayMetrics?.HeightPixels ?? 900) * 0.86f), 0.72f);
    }

    static void AddSlider(
        Activity activity,
        LinearLayout parent,
        string name,
        int minimum,
        int maximum,
        int current,
        Action<int> changed,
        Typeface bodyFont,
        Typeface bodyBold)
    {
        var header = new LinearLayout(activity)
        {
            Orientation = Orientation.Horizontal,
        };
        header.SetGravity(GravityFlags.CenterVertical);
        var label = Text(activity, name, 12, Sand, bodyBold);
        var value = Text(activity, $"{current}%", 11, Muted, bodyFont);
        value.Gravity = GravityFlags.Right | GravityFlags.CenterVertical;
        header.AddView(label, new LinearLayout.LayoutParams(0,
            ViewGroup.LayoutParams.WrapContent, 1f));
        header.AddView(value, new LinearLayout.LayoutParams(Dp(activity, 62),
            ViewGroup.LayoutParams.WrapContent));
        parent.AddView(header);

        var slider = new SeekBar(activity)
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
        parent.AddView(slider, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(activity, 30)));
    }

    static TextView Text(Activity activity, string value, float size, Color color, Typeface font)
    {
        var text = new TextView(activity)
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

    static Button Button(Activity activity, string value, Typeface font)
    {
        var button = new Button(activity)
        {
            Text = value,
            TextSize = 10,
            Gravity = GravityFlags.Center,
            StateListAnimator = null,
        };
        button.SetTypeface(font, TypefaceStyle.Normal);
        button.SetTextColor(Sand);
        button.SetMinHeight(0);
        button.SetMinWidth(0);
        button.Background = RoundedBackground(Color.Rgb(31, 19, 13),
            Color.Argb(210, 255, 176, 32), Dp(activity, 1), Dp(activity, 2));
        return button;
    }

    static LinearLayout.LayoutParams WeightedButtonLayout(Activity activity) =>
        new(0, ViewGroup.LayoutParams.MatchParent, 1f)
        {
            LeftMargin = Dp(activity, 3),
            RightMargin = Dp(activity, 3),
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

    static GradientDrawable RoundedBackground(
        Color fill, Color stroke, int strokeWidth, float radius)
    {
        var drawable = new GradientDrawable();
        drawable.SetColor(fill);
        drawable.SetCornerRadius(radius);
        drawable.SetStroke(strokeWidth, stroke);
        return drawable;
    }

    static int Dp(Activity activity, float value) =>
        (int)(value * (activity.Resources?.DisplayMetrics?.Density ?? 1f) + 0.5f);

    static Typeface LoadTypeface(Activity activity, string assetPath, Typeface fallback)
    {
        try { return Typeface.CreateFromAsset(activity.Assets, assetPath) ?? fallback; }
        catch { return fallback; }
    }
}
