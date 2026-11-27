using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Host.Window;
using static CrashBandicoot.Launcher.Linux.LinuxTheme;

namespace CrashBandicoot.Launcher.Linux;

/// <summary>Sheet control reachable with arrows / gamepad (see <c>LauncherWindow</c> navigation).</summary>
interface INavItem
{
    /// <summary>Enter, Space or gamepad confirm.</summary>
    void Activate();

    /// <summary>Left/Right on the focused item. Returns false when the item does not use it.</summary>
    bool Nudge(int direction) => false;
}

/// <summary>Base for the custom-painted sheet controls: focusable, hand cursor, repaint on hover/focus.</summary>
abstract class ThemeControl : Control
{
    static ThemeControl()
    {
        AffectsRender<ThemeControl>(IsFocusedProperty, IsPointerOverProperty);
    }

    protected ThemeControl()
    {
        Focusable = true;
        // Focus is drawn by each control (orange border), not by the Fluent adorner.
        FocusAdorner = null;
        Cursor = HandCursor;
    }

    protected bool Hot => IsFocused || IsPointerOver;

    protected static bool IsLeftPress(PointerPressedEventArgs e, Visual v) =>
        e.GetCurrentPoint(v).Properties.IsLeftButtonPressed;
}

/// <summary>Checkbox row: small square + label, like the Windows ThemeCheck.</summary>
sealed class ThemeCheck : ThemeControl, INavItem
{
    readonly FormattedText _label;
    bool _checked;

    public ThemeCheck(string text, double fontSize = 17)
    {
        _label = Text(text, NunitoFace, fontSize, Brush(Sand));
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
    }

    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            InvalidateVisual();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Activate() => Checked = !Checked;

    protected override Size MeasureOverride(Size availableSize)
    {
        _label.MaxTextWidth = double.IsInfinity(availableSize.Width) ? 4000 : Math.Max(1, availableSize.Width - 38);
        _label.MaxLineCount = 1;
        _label.Trimming = TextTrimming.CharacterEllipsis;
        return new Size(34 + _label.Width + 4, Math.Max(30, _label.Height + 6));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsLeftPress(e, this)) return;
        e.Handled = true;
        Focus(NavigationMethod.Pointer);
        Checked = !Checked;
    }

    public override void Render(DrawingContext g)
    {
        var h = Bounds.Height;
        g.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        var square = new Rect(0, Math.Max(0, (h - 26) / 2), 26, 26);
        g.FillRectangle(Brush(CardTop), square);
        var box = new Rect(square.X + 1, square.Y + 1, 23, 23);
        g.FillRectangle(_checked ? Brush(255, 70, 200, 90) : Brushes.Black, box.Deflate(3));
        g.DrawRectangle(null, new Pen(Hot ? Brush(WumpaHot) : Brush(220, 255, 200, 120), 2), box);

        g.DrawText(_label, new Point(34, Math.Max(0, (h - _label.Height) / 2)));
    }
}

/// <summary>Orange slider with a percent column, like the Windows ThemeSlider.</summary>
sealed class ThemeSlider : ThemeControl, INavItem
{
    const int PercentCol = 52;
    int _value;
    bool _dragging;

    public int Minimum { get; init; }
    public int Maximum { get; init; } = 100;
    public int Step { get; init; } = 5;

    public ThemeSlider()
    {
        Width = 340;
        Height = 28;
    }

    public int Value
    {
        get => _value;
        set
        {
            var v = Snap(Math.Clamp(value, Minimum, Maximum));
            if (v == _value) return;
            _value = v;
            InvalidateVisual();
        }
    }

    public void Activate() { }

    public bool Nudge(int direction)
    {
        Value += direction * Step;
        return true;
    }

    int Snap(int v)
    {
        if (Step <= 1) return v;
        var snapped = (int)Math.Round(v / (double)Step) * Step;
        return Math.Clamp(snapped, Minimum, Maximum);
    }

    Rect TrackRect => new(6, Bounds.Height / 2 - 3, Math.Max(1, Bounds.Width - PercentCol - 12), 6);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsLeftPress(e, this)) return;
        var p = e.GetPosition(this);
        if (p.X >= Bounds.Width - PercentCol) return;
        e.Handled = true;
        Focus(NavigationMethod.Pointer);
        _dragging = true;
        e.Pointer.Capture(this);
        SetFromPointer(p.X);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) SetFromPointer(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    void SetFromPointer(double x)
    {
        var track = TrackRect;
        var t = Math.Clamp((x - track.X) / Math.Max(1, track.Width), 0, 1);
        Value = Minimum + (int)Math.Round(t * (Maximum - Minimum));
    }

    public override void Render(DrawingContext g)
    {
        var size = Bounds.Size;
        g.FillRectangle(Brushes.Transparent, new Rect(size));
        var track = TrackRect;
        g.DrawRectangle(Brush(50, 255, 255, 255), null, track, 3, 3);

        if (IsFocused)
            g.DrawRectangle(null, new Pen(Brush(WumpaHot), 1.5), new Rect(size).Deflate(0.75));

        var t = Maximum == Minimum ? 0 : (_value - Minimum) / (double)(Maximum - Minimum);
        var fillW = (int)(track.Width * t);
        if (fillW > 0)
        {
            var fill = new Rect(track.X, track.Y, Math.Max(fillW, 1), track.Height);
            var orange = GdiLinear(fill, 0, (0, Wumpa), (1, WumpaHot));
            g.DrawRectangle(orange, null, fill, fillW < 8 ? 0 : 3, fillW < 8 ? 0 : 3);
        }

        var thumb = new Rect(track.X + fillW - 8, size.Height / 2 - 8, 16, 16);
        g.DrawEllipse(Brush(Sand), new Pen(Brush(Wumpa), 2), thumb);

        var pct = Text($"{_value}%", NunitoBoldFace, 14, Brush(Sand));
        g.DrawText(pct, new Point(size.Width - 2 - pct.Width, (size.Height - pct.Height) / 2));
    }
}

/// <summary>Click-to-capture key binding (Silk key names, same as the in-game remap).</summary>
sealed class KeyCaptureBox : ThemeControl, INavItem
{
    static KeyCaptureBox? _active;
    string _boundKey = "";
    bool _listening;

    public KeyCaptureBox()
    {
        Width = 340;
        Height = 30;
    }

    public static KeyCaptureBox? Active => _active is { _listening: true } box ? box : null;

    public static void CancelActive() => _active?.CancelListen();

    public string BoundKey
    {
        get => _boundKey;
        set
        {
            var next = KeyBindingNames.Canonical(value);
            if (_boundKey == next) return;
            _boundKey = next;
            InvalidateVisual();
        }
    }

    public void Activate() => BeginListen();

    public void BeginListen()
    {
        if (_active != null && _active != this)
            _active.CancelListen();
        _active = this;
        _listening = true;
        if (!IsFocused) Focus(NavigationMethod.Directional);
        InvalidateVisual();
    }

    public void CancelListen()
    {
        if (!_listening && _active != this) return;
        _listening = false;
        if (_active == this) _active = null;
        InvalidateVisual();
    }

    /// <summary>Handle a key while listening. Escape cancels; unknown keys keep listening.</summary>
    public void HandleKey(KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            CancelListen();
            return;
        }

        var name = LinuxKeyNames.FromKeyEvent(e);
        if (name == null) return;
        BoundKey = name;
        CancelListen();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsLeftPress(e, this)) return;
        e.Handled = true;
        Focus(NavigationMethod.Pointer);
        BeginListen();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        if (_listening) CancelListen();
        base.OnLostFocus(e);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_active == this) CancelListen();
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext g)
    {
        var box = new Rect(Bounds.Size);
        var hot = _listening || IsFocused || IsPointerOver;
        g.FillRectangle(Brush(Field), box);
        g.DrawRectangle(null, new Pen(hot ? Brush(WumpaHot) : Brush(140, 255, 200, 120)), box.Deflate(0.5));

        string text;
        IBrush brush;
        if (_listening)
        {
            text = "[press key...]";
            brush = Brush(Wumpa);
        }
        else if (string.IsNullOrEmpty(_boundKey))
        {
            text = "unbound";
            brush = Brush(140, Sand);
        }
        else
        {
            text = _boundKey;
            brush = Brush(Sand);
        }

        var ft = Text(text, NunitoFace, 16, brush);
        g.DrawText(ft, new Point(8, Math.Max(0, (box.Height - ft.Height) / 2)));
    }
}

/// <summary>Flat primary (orange) or ghost (dark) button, like the Windows ThemedButton.</summary>
sealed class ThemeButton : ThemeControl, INavItem
{
    string _text;
    readonly bool _primary;
    bool _pressed;

    public ThemeButton(string text, bool primary, double width = 160)
    {
        _text = text;
        _primary = primary;
        Width = width;
        Height = 42;
    }

    public string Caption
    {
        get => _text;
        set
        {
            _text = value;
            InvalidateVisual();
        }
    }

    public event EventHandler? Click;

    public void Activate() => Click?.Invoke(this, EventArgs.Empty);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsLeftPress(e, this)) return;
        e.Handled = true;
        _pressed = true;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed) return;
        _pressed = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
        if (new Rect(Bounds.Size).Contains(e.GetPosition(this)))
            Activate();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pressed = false;
        InvalidateVisual();
    }

    public override void Render(DrawingContext g)
    {
        var r = new Rect(Bounds.Size);
        Color back;
        if (_primary)
            back = _pressed ? Color.FromRgb(230, 120, 0) : IsPointerOver ? WumpaHot : Wumpa;
        else
            back = _pressed ? Color.FromRgb(60, 40, 24) : IsPointerOver ? Color.FromRgb(48, 32, 20) : Color.FromRgb(28, 18, 12);
        g.FillRectangle(Brush(back), r);

        if (IsFocused)
            g.DrawRectangle(null, new Pen(Brush(_primary ? Sand : WumpaHot), 2), r.Deflate(1));
        else
            g.DrawRectangle(null, new Pen(_primary ? Brush(220, 255, 180, 60) : Brush(140, 255, 200, 120)), r.Deflate(0.5));

        var fg = _primary ? Brush(Color.FromRgb(42, 18, 0)) : Brush(Sand);
        var ft = Text(_text, NunitoBoldFace, 17, fg);
        g.DrawText(ft, new Point((r.Width - ft.Width) / 2, (r.Height - ft.Height) / 2));
    }
}

/// <summary>
/// Row of tabs on top of a sheet (Settings), like the Windows SheetTabStrip: selected tab
/// orange like Save, others dark like Back. Left/Right when focused, L1/R1 or Ctrl+Tab switch tabs.
/// </summary>
sealed class SheetTabs : ThemeControl, INavItem
{
    const double Gap = 8;
    readonly string[] _tabs;
    int _selected;
    int _hover = -1;

    public SheetTabs(string[] tabs, int selected)
    {
        _tabs = tabs;
        _selected = Math.Clamp(selected, 0, tabs.Length - 1);
        Height = 40;
    }

    public event EventHandler? SelectedIndexChanged;

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            var v = Math.Clamp(value, 0, _tabs.Length - 1);
            if (v == _selected) return;
            _selected = v;
            InvalidateVisual();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Previous/next tab, wrapping around.</summary>
    public void Step(int direction) =>
        SelectedIndex = (_selected + direction + _tabs.Length) % _tabs.Length;

    /// <summary>Confirm enters the page; the window handles it (see ActivateSheetFocused).</summary>
    public void Activate() { }

    public bool Nudge(int direction)
    {
        Step(direction);
        return true;
    }

    Rect TabRect(int i)
    {
        var w = (Bounds.Width - Gap * (_tabs.Length - 1)) / _tabs.Length;
        return new Rect(i * (w + Gap), 0, Math.Max(1, w), Bounds.Height);
    }

    int HitTest(Point p)
    {
        for (var i = 0; i < _tabs.Length; i++)
            if (TabRect(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsLeftPress(e, this)) return;
        var hit = HitTest(e.GetPosition(this));
        if (hit < 0) return;
        e.Handled = true;
        Focus(NavigationMethod.Pointer);
        SelectedIndex = hit;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var hit = HitTest(e.GetPosition(this));
        if (hit == _hover) return;
        _hover = hit;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = -1;
        InvalidateVisual();
    }

    public override void Render(DrawingContext g)
    {
        g.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        for (var i = 0; i < _tabs.Length; i++)
        {
            var r = TabRect(i);
            var selected = i == _selected;
            var back = selected
                ? (i == _hover ? WumpaHot : Wumpa)
                : (i == _hover ? Color.FromRgb(48, 32, 20) : Color.FromRgb(28, 18, 12));
            g.FillRectangle(Brush(back), r);

            if (selected && IsFocused)
                g.DrawRectangle(null, new Pen(Brush(Sand), 2), r.Deflate(1));
            else
                g.DrawRectangle(null, new Pen(selected ? Brush(220, 255, 180, 60) : Brush(140, 255, 200, 120)), r.Deflate(0.5));

            var fg = selected ? Brush(Color.FromRgb(42, 18, 0)) : Brush(Sand);
            var ft = Text(_tabs[i].ToUpperInvariant(), BungeeFace, 16, fg);
            g.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, (r.Height - ft.Height) / 2));
        }
    }
}

/// <summary>Collapsible category header in the Mods sheet.</summary>
sealed class ModGroupHeader : ThemeControl, INavItem
{
    readonly string _title;
    readonly int _count;
    readonly bool _expanded;

    public ModGroupHeader(string title, int count, bool expanded)
    {
        _title = title.ToUpperInvariant();
        _count = count;
        _expanded = expanded;
        Height = 40;
    }

    public event EventHandler? Toggled;

    public void Activate() => Toggled?.Invoke(this, EventArgs.Empty);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsLeftPress(e, this)) return;
        e.Handled = true;
        Activate();
    }

    public override void Render(DrawingContext g)
    {
        var r = new Rect(Bounds.Size);
        g.FillRectangle(Brush(36, CardTop), r);
        g.DrawRectangle(null, new Pen(Hot ? Brush(WumpaHot) : Brush(70, 255, 160, 40)), r.Deflate(0.5));

        // Arrow as geometry: Nunito has no ▼/▶ glyphs.
        var ax = 18.0;
        var ay = r.Height / 2;
        var arrow = new StreamGeometry();
        using (var ctx = arrow.Open())
        {
            if (_expanded)
            {
                ctx.BeginFigure(new Point(ax - 6, ay - 4), true);
                ctx.LineTo(new Point(ax + 6, ay - 4));
                ctx.LineTo(new Point(ax, ay + 5));
            }
            else
            {
                ctx.BeginFigure(new Point(ax - 4, ay - 6), true);
                ctx.LineTo(new Point(ax + 5, ay));
                ctx.LineTo(new Point(ax - 4, ay + 6));
            }
            ctx.EndFigure(true);
        }
        g.DrawGeometry(Brush(Wumpa), null, arrow);

        var title = Text(_title, NunitoBoldFace, 16, Brush(Wumpa));
        g.DrawText(title, new Point(34, (r.Height - title.Height) / 2));

        var count = Text(_count == 1 ? "1 mod" : $"{_count} mods", NunitoFace, 13, Brush(150, Sand));
        g.DrawText(count, new Point(r.Width - 12 - count.Width, (r.Height - count.Height) / 2));
    }
}

/// <summary>Modal notice card; the second button unlocks after a short delay.</summary>
sealed class GlyphToast : Window
{
    readonly ThemeButton _go;
    readonly DispatcherTimer _tick;
    int _left = 10;

    GlyphToast(int k)
    {
        Title = TextShaper.Run(4);
        Width = 660;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush(Color.FromRgb(36, 10, 10));
        FontFamily = Nunito;
        FontWeight = FontWeight.SemiBold;
        Foreground = Brush(Sand);
        Icon = LinuxGui.LoadWindowIcon();

        var open = new ThemeButton(TextShaper.Run(7), primary: true, width: 200);
        open.Click += (_, _) => OpenLink(TextShaper.Run(2));
        _go = new ThemeButton(Caption(), primary: false, width: 250) { IsEnabled = false, Opacity = 0.45 };
        _go.Click += (_, _) => Close();

        Content = new Border
        {
            BorderBrush = Brush(Danger),
            BorderThickness = new Thickness(3),
            Padding = new Thickness(34, 26, 34, 28),
            Child = new StackPanel
            {
                Spacing = 18,
                Children =
                {
                    new TextBlock
                    {
                        Text = TextShaper.Run(4),
                        FontFamily = Bungee,
                        FontSize = 30,
                        Foreground = Brush(Danger),
                    },
                    new TextBlock
                    {
                        Text = TextShaper.Run(k),
                        FontSize = 17,
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 24,
                    },
                    new TextBlock
                    {
                        Text = TextShaper.Run(9),
                        FontSize = 18,
                        FontWeight = FontWeight.ExtraBold,
                        Foreground = Brush(WumpaHot),
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 14,
                        Margin = new Thickness(0, 6, 0, 0),
                        Children = { open, _go },
                    },
                },
            },
        };

        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space && FocusManager?.GetFocusedElement() is INavItem item)
            {
                item.Activate();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => Step());
        Opened += (_, _) =>
        {
            _tick.Start();
            open.Focus();
        };
        Closing += (_, e) =>
        {
            if (_left > 0) e.Cancel = true;
        };
        Closed += (_, _) => _tick.Stop();
    }

    string Caption() => _left > 0 ? $"{TextShaper.Run(8)} ({_left})" : TextShaper.Run(8);

    void Step()
    {
        if (_left > 0) _left--;
        _go.Caption = Caption();
        if (_left > 0) return;
        _tick.Stop();
        _go.IsEnabled = true;
        _go.Opacity = 1;
    }

    static void OpenLink(string url)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open")
                { UseShellExecute = false };
            psi.ArgumentList.Add(url);
            using var _ = System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Launcher] xdg-open failed: {ex.Message}");
        }
    }

    public static void Pop(Window owner, int k)
    {
        try
        {
            var w = new GlyphToast(k);
            if (owner.IsVisible)
                _ = w.ShowDialog(owner);
            else
                w.Show();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Launcher] notice failed: {ex.Message}");
        }
    }
}
