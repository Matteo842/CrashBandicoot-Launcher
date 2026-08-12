using Android.App;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using RecompOne.Runtime.Config;

namespace CrashBandicoot.AndroidRuntime;

/// <summary>Android counterpart of the desktop launcher's Settings sheet.</summary>
static class SettingsDialog
{
    static readonly Color Night = Color.Rgb(6, 16, 24);
    static readonly Color Card = Color.Rgb(11, 35, 27);
    static readonly Color Wumpa = Color.Rgb(255, 138, 0);
    static readonly Color Sand = Color.Rgb(244, 228, 188);
    static readonly Color Muted = Color.Rgb(177, 188, 188);

    static readonly int[] ResolutionValues = [1, 2, 4, 8];
    static readonly string[] ResolutionLabels = ["Native (1x)", "2x", "4x", "8x (4K)"];
    static readonly string[] FilterLabels = ["Off", "Bilinear", "Sharp bilinear", "Soft smooth"];

    public static void Show(Activity activity, Action? settingsApplied = null)
    {
        var game = ConfigManager.Game;
        var view = ConfigManager.View;
        var displayFont = LoadTypeface(activity, "Fonts/Bungee-Regular.ttf", Typeface.DefaultBold!);
        var bodyFont = LoadTypeface(activity, "Fonts/Nunito-SemiBold.ttf", Typeface.Default!);
        var bodyBold = LoadTypeface(activity, "Fonts/Nunito-ExtraBold.ttf", Typeface.DefaultBold!);

        var dialog = new Dialog(activity);
        dialog.RequestWindowFeature((int)WindowFeatures.NoTitle);
        dialog.SetCanceledOnTouchOutside(false);

        var root = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        root.SetPadding(Dp(activity, 22), Dp(activity, 15), Dp(activity, 22), Dp(activity, 15));
        root.Background = RoundedBackground(Card, Color.Argb(170, 255, 176, 32),
            Dp(activity, 1), Dp(activity, 3));

        var card = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        card.AddView(Text(activity, "SETTINGS", 19, Wumpa, displayFont));
        card.AddView(Text(activity,
            "Le stesse opzioni del launcher Windows, salvate nel profilo del runtime Android.",
            10.5f, Muted, bodyFont), Margin(activity, top: 5, bottom: 8));

        var volume = AddSlider(activity, card, "Master volume", 0, 100,
            (int)MathF.Round(game.MasterVolume * 100f), bodyFont, bodyBold);
        card.AddView(Hint(activity,
            "Si applica all'avvio della sessione di gioco e resta salvato nel profilo Android.", bodyFont));

        var muted = AddSwitch(activity, card, "Muted", game.Muted, bodyBold);
        var fullscreen = AddSwitch(activity, card, "Fullscreen", view.Fullscreen, bodyBold);
        card.AddView(Hint(activity,
            "Nasconde completamente le barre di stato e navigazione durante il gioco.", bodyFont));

        var widescreen = AddSwitch(activity, card, "Widescreen 16:9", view.Widescreen, bodyBold);
        card.AddView(Hint(activity,
            "Hack attivo solo nel gameplay; menu, mappa e filmati restano in 4:3.", bodyFont));

        var resolution = AddChoice(activity, card, "Internal resolution",
            ResolutionLabels, IndexOf(ResolutionValues, view.InternalResolution), bodyFont, bodyBold);
        card.AddView(Hint(activity,
            "Viene conservata per il renderer GPU; il renderer software Android attuale resta nativo 1x.", bodyFont));

        var filter = AddChoice(activity, card, "Texture filter",
            FilterLabels, Math.Clamp(view.TextureFilter, 0, FilterLabels.Length - 1), bodyFont, bodyBold);
        var filterStrength = AddSlider(activity, card, "Filter strength", 0, 100,
            (int)MathF.Round(view.TextureFilterStrength * 100f), bodyFont, bodyBold);

        void SyncFilterStrength()
        {
            var active = filter.SelectedItemPosition > 0;
            filterStrength.Enabled = active;
            filterStrength.Alpha = active ? 1f : 0.38f;
        }
        filter.ItemSelected += (_, _) => SyncFilterStrength();
        SyncFilterStrength();

        var dedither = AddSwitch(activity, card, "Dedither", view.Dedither, bodyBold);
        var dejitter = AddSwitch(activity, card, "Dejitter", view.Dejitter, bodyBold);
        card.AddView(Hint(activity,
            "I filtri texture si disattivano automaticamente nei menu e nei filmati. Dedither e dejitter restano globali.",
            bodyFont));

        var scroll = new ScrollView(activity)
        {
            FillViewport = true,
            VerticalScrollBarEnabled = true,
        };
        scroll.AddView(card);
        root.AddView(scroll, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f));

        var actions = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        actions.SetGravity(GravityFlags.Center);
        var save = Button(activity, "SAVE", bodyBold, primary: true);
        var back = Button(activity, "BACK", bodyBold, primary: false);
        actions.AddView(save, WeightedButtonLayout(activity));
        actions.AddView(back, WeightedButtonLayout(activity));
        root.AddView(actions, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(activity, 40))
        {
            TopMargin = Dp(activity, 7),
        });

        save.Click += (_, _) =>
        {
            game.MasterVolume = volume.Progress / 100f;
            game.Muted = muted.Checked;
            view.Fullscreen = fullscreen.Checked;
            view.Widescreen = widescreen.Checked;
            view.InternalResolution = ResolutionValues[Math.Clamp(
                resolution.SelectedItemPosition, 0, ResolutionValues.Length - 1)];
            view.TextureFilter = Math.Clamp(filter.SelectedItemPosition, 0, FilterLabels.Length - 1);
            view.TextureFilterStrength = filterStrength.Progress / 100f;
            view.Dedither = dedither.Checked;
            view.Dejitter = dejitter.Checked;

            ConfigManager.SaveGame();
            ConfigManager.SaveView(Array.Empty<RecompOne.Runtime.Host.Window.IPanel>());
            settingsApplied?.Invoke();
            dialog.Dismiss();
        };
        back.Click += (_, _) => dialog.Dismiss();

        dialog.SetContentView(root);
        dialog.DismissEvent += (_, _) => dialog.Dispose();
        dialog.Show();
        ConfigureWindow(dialog, activity, 0.76f,
            (int)((activity.Resources?.DisplayMetrics?.HeightPixels ?? 900) * 0.89f), 0.78f);
    }

    static Switch AddSwitch(Activity activity, LinearLayout parent, string label, bool value, Typeface font)
    {
        var toggle = new Switch(activity)
        {
            Text = label,
            TextSize = 12,
            Checked = value,
        };
        toggle.SetTypeface(font, TypefaceStyle.Normal);
        toggle.SetTextColor(Sand);
        parent.AddView(toggle, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(activity, 34)));
        return toggle;
    }

    static SeekBar AddSlider(Activity activity, LinearLayout parent, string name,
        int minimum, int maximum, int current, Typeface bodyFont, Typeface bodyBold)
    {
        var header = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        var label = Text(activity, name, 12, Sand, bodyBold);
        var value = Text(activity, $"{current}%", 11, Muted, bodyFont);
        value.Gravity = GravityFlags.Right | GravityFlags.CenterVertical;
        header.AddView(label, new LinearLayout.LayoutParams(0,
            ViewGroup.LayoutParams.WrapContent, 1f));
        header.AddView(value, new LinearLayout.LayoutParams(Dp(activity, 62),
            ViewGroup.LayoutParams.WrapContent));
        parent.AddView(header, Margin(activity, top: 3));

        var slider = new SeekBar(activity)
        {
            Max = maximum - minimum,
            Progress = Math.Clamp(current - minimum, 0, maximum - minimum),
        };
        slider.ProgressChanged += (_, e) => value.Text = $"{minimum + e.Progress}%";
        parent.AddView(slider, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(activity, 29)));
        return slider;
    }

    static Spinner AddChoice(Activity activity, LinearLayout parent, string name,
        string[] choices, int selected, Typeface bodyFont, Typeface bodyBold)
    {
        var row = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.AddView(Text(activity, name, 12, Sand, bodyBold), new LinearLayout.LayoutParams(
            0, ViewGroup.LayoutParams.WrapContent, 1f));

        var spinner = new Spinner(activity, SpinnerMode.Dropdown);
        var adapter = new ArrayAdapter<string>(activity,
            Android.Resource.Layout.SimpleSpinnerItem, choices);
        adapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
        spinner.Adapter = adapter;
        spinner.SetSelection(Math.Clamp(selected, 0, choices.Length - 1));
        row.AddView(spinner, new LinearLayout.LayoutParams(Dp(activity, 190), Dp(activity, 42)));
        parent.AddView(row);
        return spinner;
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

    static LinearLayout.LayoutParams WeightedButtonLayout(Activity activity) =>
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

    static int IndexOf(int[] values, int value)
    {
        var index = Array.IndexOf(values, value);
        return index < 0 ? 0 : index;
    }

    static int Dp(Activity activity, float value) =>
        (int)(value * (activity.Resources?.DisplayMetrics?.Density ?? 1f) + 0.5f);

    static Typeface LoadTypeface(Activity activity, string assetPath, Typeface fallback)
    {
        try { return Typeface.CreateFromAsset(activity.Assets, assetPath) ?? fallback; }
        catch { return fallback; }
    }
}
