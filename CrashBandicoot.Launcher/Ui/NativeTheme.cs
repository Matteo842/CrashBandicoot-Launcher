using System.Drawing.Text;

namespace CrashBandicoot.Launcher.Ui;

/// <summary>Shared colors and fonts for the native WinForms launcher UI.</summary>
static class NativeTheme
{
    public static readonly Color Night = Color.FromArgb(6, 16, 24);
    public static readonly Color JungleTop = Color.FromArgb(10, 40, 24);
    public static readonly Color JungleWarm = Color.FromArgb(18, 10, 8);
    public static readonly Color Wumpa = Color.FromArgb(255, 138, 0);
    public static readonly Color WumpaHot = Color.FromArgb(255, 176, 32);
    public static readonly Color Sand = Color.FromArgb(244, 228, 188);
    public static readonly Color Danger = Color.FromArgb(255, 59, 59);
    public static readonly Color Ok = Color.FromArgb(125, 255, 154);
    public static readonly Color CardTop = Color.FromArgb(22, 51, 37);
    public static readonly Color CardBottom = Color.FromArgb(12, 22, 24);
    public static readonly Color WoodLight = Color.FromArgb(208, 137, 58);
    public static readonly Color WoodMid = Color.FromArgb(196, 122, 42);
    public static readonly Color WoodDark = Color.FromArgb(138, 74, 18);
    public static readonly Color WoodDeep = Color.FromArgb(92, 48, 8);
    public static readonly Color WoodFrame = Color.FromArgb(184, 111, 36);
    public static readonly Color CrateCore = Color.FromArgb(58, 32, 8);
    public static readonly Color MarkYellow = Color.FromArgb(255, 225, 74);
    public static readonly Color MarkRed = Color.FromArgb(224, 24, 24);

    static PrivateFontCollection? _fonts;
    static FontFamily? _bungee;
    static FontFamily? _nunito;
    static FontFamily? _nunitoExtra;

    public static FontFamily Bungee => _bungee ?? FontFamily.GenericSansSerif;
    public static FontFamily Nunito => _nunito ?? FontFamily.GenericSansSerif;
    public static FontFamily NunitoExtra => _nunitoExtra ?? _nunito ?? FontFamily.GenericSansSerif;

    public static void LoadFonts(string uiDirectory)
    {
        _fonts?.Dispose();
        _fonts = new PrivateFontCollection();
        _bungee = _nunito = _nunitoExtra = null;

        TryAdd(Path.Combine(uiDirectory, "fonts", "Bungee-Regular.ttf"), ref _bungee);
        TryAdd(Path.Combine(uiDirectory, "fonts", "Nunito-SemiBold.ttf"), ref _nunito);
        TryAdd(Path.Combine(uiDirectory, "fonts", "Nunito-ExtraBold.ttf"), ref _nunitoExtra);
    }

    static void TryAdd(string path, ref FontFamily? family)
    {
        if (_fonts == null || !File.Exists(path)) return;
        try
        {
            _fonts.AddFontFile(path);
            if (_fonts.Families.Length > 0)
                family = _fonts.Families[^1];
        }
        catch
        {
            // fall back to system fonts
        }
    }

    public static Font MakeBungee(float size, FontStyle style = FontStyle.Regular) =>
        new(Bungee, size, style, GraphicsUnit.Pixel);

    public static Font MakeNunito(float size, bool extraBold = false) =>
        new(extraBold ? NunitoExtra : Nunito, size, FontStyle.Regular, GraphicsUnit.Pixel);

    public static void DisposeFonts()
    {
        _fonts?.Dispose();
        _fonts = null;
        _bungee = _nunito = _nunitoExtra = null;
    }
}
