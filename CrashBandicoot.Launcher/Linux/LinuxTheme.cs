using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace CrashBandicoot.Launcher.Linux;

/// <summary>
/// Colors, fonts and brush helpers for the Linux launcher. Values match
/// <c>Ui/NativeTheme.cs</c> so both launchers look the same.
/// </summary>
static class LinuxTheme
{
    public static readonly Color Night = Color.FromRgb(6, 16, 24);
    public static readonly Color JungleTop = Color.FromRgb(10, 40, 24);
    public static readonly Color JungleWarm = Color.FromRgb(18, 10, 8);
    public static readonly Color Wumpa = Color.FromRgb(255, 138, 0);
    public static readonly Color WumpaHot = Color.FromRgb(255, 176, 32);
    public static readonly Color Sand = Color.FromRgb(244, 228, 188);
    public static readonly Color Danger = Color.FromRgb(255, 59, 59);
    public static readonly Color Ok = Color.FromRgb(125, 255, 154);
    public static readonly Color CardTop = Color.FromRgb(22, 51, 37);
    public static readonly Color CardBottom = Color.FromRgb(12, 22, 24);
    public static readonly Color WoodFrame = Color.FromRgb(184, 111, 36);
    public static readonly Color CrateCore = Color.FromRgb(58, 32, 8);
    public static readonly Color MarkYellow = Color.FromRgb(255, 225, 74);
    public static readonly Color MarkRed = Color.FromRgb(224, 24, 24);
    public static readonly Color Field = Color.FromRgb(30, 20, 12);

    // Fonts are embedded as AvaloniaResource (see the csproj), so they work from the single-file binary.
    const string FontFolder = "avares://CrashBandicoot/Ui/fonts";
    public static readonly FontFamily Bungee = new(FontFolder + "#Bungee");
    public static readonly FontFamily Nunito = new(FontFolder + "#Nunito");
    public const string DefaultFamilyName = FontFolder + "#Nunito";

    public static readonly Typeface BungeeFace = new(Bungee);
    public static readonly Typeface NunitoFace = new(Nunito, FontStyle.Normal, FontWeight.SemiBold);
    public static readonly Typeface NunitoBoldFace = new(Nunito, FontStyle.Normal, FontWeight.ExtraBold);

    public static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    public static Color A(byte alpha, Color c) => Color.FromArgb(alpha, c.R, c.G, c.B);
    public static Color Argb(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);

    public static IBrush Brush(Color c) => new SolidColorBrush(c);
    public static IBrush Brush(byte a, byte r, byte g, byte b) => new SolidColorBrush(Color.FromArgb(a, r, g, b));
    public static IBrush Brush(byte alpha, Color c) => new SolidColorBrush(A(alpha, c));

    public static FormattedText Text(string text, Typeface face, double size, IBrush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush);

    /// <summary>
    /// Linear gradient laid out like GDI+ <c>LinearGradientBrush(rect, c1, c2, angle)</c>:
    /// the angle is clockwise from the x axis and the colors span the whole rectangle.
    /// Only valid for filling exactly <paramref name="r"/> (points are relative to it).
    /// </summary>
    public static LinearGradientBrush GdiLinear(Rect r, double angleDeg, params (double Offset, Color Color)[] stops)
    {
        var rad = angleDeg * Math.PI / 180.0;
        var d = new Vector(Math.Cos(rad), Math.Sin(rad));
        Point[] corners = [r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight];
        var start = corners.MinBy(p => p.X * d.X + p.Y * d.Y);
        var end = corners.MaxBy(p => p.X * d.X + p.Y * d.Y);
        var len = (end.X - start.X) * d.X + (end.Y - start.Y) * d.Y;
        var stop = start + d * len;
        RelativePoint Rel(Point p) => new(
            r.Width > 0 ? (p.X - r.X) / r.Width : 0,
            r.Height > 0 ? (p.Y - r.Y) / r.Height : 0,
            RelativeUnit.Relative);
        var brush = new LinearGradientBrush
        {
            StartPoint = Rel(start),
            EndPoint = Rel(stop),
        };
        foreach (var (offset, color) in stops)
            brush.GradientStops.Add(new GradientStop(color, offset));
        return brush;
    }

    /// <summary>Card background: same green-to-night gradient as the Windows sheets.</summary>
    public static IBrush CardBrush() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(1, 0.1, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 0.9, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(CardTop, 0),
            new GradientStop(CardBottom, 1),
        },
    };

    /// <summary>Recolor the Fluent controls we use as-is (ComboBox, ScrollViewer).</summary>
    public static void AddResources(IResourceDictionary res)
    {
        res["SystemAccentColor"] = Wumpa;
        res["SystemAccentColorLight1"] = WumpaHot;
        res["SystemAccentColorLight2"] = WumpaHot;
        res["SystemAccentColorLight3"] = WumpaHot;
        res["SystemAccentColorDark1"] = Wumpa;
        res["SystemAccentColorDark2"] = Wumpa;
        res["SystemAccentColorDark3"] = Wumpa;

        var field = Brush(Field);
        var fieldHot = Brush(Argb(255, 48, 32, 20));
        var border = Brush(140, 255, 200, 120);
        var sand = Brush(Sand);
        res["ComboBoxBackground"] = field;
        res["ComboBoxBackgroundPointerOver"] = fieldHot;
        res["ComboBoxBackgroundPressed"] = fieldHot;
        res["ComboBoxBackgroundUnfocused"] = field;
        res["ComboBoxBackgroundBorderBrushFocused"] = Brush(WumpaHot);
        res["ComboBoxBackgroundBorderBrushUnfocused"] = border;
        res["ComboBoxBorderBrush"] = border;
        res["ComboBoxBorderBrushPointerOver"] = Brush(WumpaHot);
        res["ComboBoxBorderBrushPressed"] = Brush(WumpaHot);
        res["ComboBoxForeground"] = sand;
        res["ComboBoxForegroundFocused"] = sand;
        res["ComboBoxForegroundFocusedPressed"] = sand;
        res["ComboBoxDropDownGlyphForeground"] = sand;
        res["ComboBoxDropDownGlyphForegroundFocused"] = sand;
        res["ComboBoxDropDownGlyphForegroundFocusedPressed"] = sand;
        res["ComboBoxDropDownBackground"] = Brush(Argb(255, 24, 18, 12));
        res["ComboBoxDropDownBorderBrush"] = border;
        res["ComboBoxItemForeground"] = sand;
        res["ComboBoxItemForegroundPointerOver"] = sand;
        res["ComboBoxItemForegroundPressed"] = sand;
        res["ComboBoxItemForegroundSelected"] = Brush(Argb(255, 42, 18, 0));
        res["ComboBoxItemForegroundSelectedPointerOver"] = Brush(Argb(255, 42, 18, 0));
        res["ComboBoxItemForegroundSelectedPressed"] = Brush(Argb(255, 42, 18, 0));
        res["ComboBoxItemBackgroundPointerOver"] = fieldHot;
        res["ComboBoxItemBackgroundPressed"] = fieldHot;
        res["ComboBoxItemBackgroundSelected"] = Brush(Wumpa);
        res["ComboBoxItemBackgroundSelectedPointerOver"] = Brush(WumpaHot);
        res["ComboBoxItemBackgroundSelectedPressed"] = Brush(WumpaHot);
        res["ScrollBarThumbBackgroundColor"] = A(160, Sand);
        res["ScrollBarThumbFillPointerOver"] = Brush(WumpaHot);
        res["ScrollBarThumbFillPressed"] = Brush(Wumpa);
    }
}
