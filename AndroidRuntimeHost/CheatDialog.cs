using Android.App;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Host.Cheats;
using RecompOne.Runtime.Host.Window;

namespace CrashBandicoot.AndroidRuntime;

/// <summary>Android counterpart of the desktop launcher Cheat sheet.</summary>
static class CheatDialog
{
    static readonly Color Night = Color.Rgb(6, 16, 24);
    static readonly Color Card = Color.Rgb(11, 35, 27);
    static readonly Color Wumpa = Color.Rgb(255, 138, 0);
    static readonly Color Sand = Color.Rgb(244, 228, 188);
    static readonly Color Muted = Color.Rgb(177, 188, 188);

    public static void Show(Activity activity)
    {
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

        root.AddView(Text(activity, "CHEAT", 19, Wumpa, displayFont));
        var intro = Text(activity,
            "NTSC-U toggles. Applied while the game is running. One-shots (99 lives, instant save) stay in the in-game Developer Menu.",
            10.5f, Muted, bodyFont);
        intro.SetLineSpacing(0, 1.08f);
        root.AddView(intro, Margin(activity, top: 6, bottom: 10));

        var lives = AddSwitch(activity, root, "Infinite Lives", CheatConfig.InfiniteLives, bodyBold);
        root.AddView(Hint(activity, "Keeps map lives at 99 and freezes the active level lives counter.", bodyFont));

        var wumpa = AddSwitch(activity, root, "Infinite Wumpa", CheatConfig.InfiniteWumpa, bodyBold);
        root.AddView(Hint(activity, "Freezes Wumpa at 99 when a single active lives slot is found.", bodyFont));

        var levelSelect = AddSwitch(activity, root, "Level Select", CheatConfig.LevelSelect, bodyBold);
        root.AddView(Hint(activity, "Unlocks the level select flag in RAM — use on the warp map.", bodyFont));

        var actions = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        actions.SetGravity(GravityFlags.Center);
        var save = Button(activity, "SAVE", bodyBold, primary: true);
        var back = Button(activity, "BACK", bodyBold, primary: false);
        actions.AddView(save, Weighted(activity));
        actions.AddView(back, Weighted(activity));
        root.AddView(actions, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(activity, 40))
        {
            TopMargin = Dp(activity, 14),
        });

        save.Click += (_, _) =>
        {
            CheatConfig.InfiniteLives = lives.Checked;
            CheatConfig.InfiniteWumpa = wumpa.Checked;
            CheatConfig.LevelSelect = levelSelect.Checked;
            ConfigManager.SaveView(Array.Empty<IPanel>());
            dialog.Dismiss();
        };
        back.Click += (_, _) => dialog.Dismiss();

        dialog.SetContentView(root);
        dialog.DismissEvent += (_, _) => dialog.Dispose();
        AndroidGamepad.BindDialog(dialog);
        dialog.Show();
        ConfigureWindow(dialog, activity, 0.62f, ViewGroup.LayoutParams.WrapContent, 0.78f);
    }

    static Switch AddSwitch(Activity activity, LinearLayout parent, string label, bool value, Typeface font)
    {
        var toggle = new Switch(activity)
        {
            Text = label,
            TextSize = 13,
            Checked = value,
        };
        toggle.SetTypeface(font, TypefaceStyle.Normal);
        toggle.SetTextColor(Sand);
        parent.AddView(toggle, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(activity, 40))
        {
            TopMargin = Dp(activity, 4),
        });
        return toggle;
    }

    static TextView Hint(Activity activity, string value, Typeface font)
    {
        var hint = Text(activity, value, 9.5f, Muted, font);
        hint.SetLineSpacing(0, 1.05f);
        return hint;
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

    static Button Button(Activity activity, string value, Typeface font, bool primary)
    {
        var button = new Button(activity)
        {
            Text = value,
            TextSize = 10,
            Gravity = GravityFlags.Center,
            StateListAnimator = null,
        };
        button.SetTypeface(font, TypefaceStyle.Normal);
        button.SetTextColor(primary ? Night : Sand);
        button.SetMinHeight(0);
        button.SetMinWidth(0);
        button.Background = RoundedBackground(
            primary ? Wumpa : Color.Rgb(31, 19, 13),
            Color.Argb(210, 255, 176, 32), Dp(activity, 1), Dp(activity, 2));
        return button;
    }

    static LinearLayout.LayoutParams Weighted(Activity activity) =>
        new(0, ViewGroup.LayoutParams.MatchParent, 1f)
        {
            LeftMargin = Dp(activity, 4),
            RightMargin = Dp(activity, 4),
        };

    static LinearLayout.LayoutParams Margin(Activity activity, int top = 0, int bottom = 0) =>
        new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = Dp(activity, top),
            BottomMargin = Dp(activity, bottom),
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

    static int Dp(Activity activity, float value) =>
        (int)(value * (activity.Resources?.DisplayMetrics?.Density ?? 1f) + 0.5f);

    static Typeface LoadTypeface(Activity activity, string assetPath, Typeface fallback)
    {
        try { return Typeface.CreateFromAsset(activity.Assets, assetPath) ?? fallback; }
        catch { return fallback; }
    }
}
