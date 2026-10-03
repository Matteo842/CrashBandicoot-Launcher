using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CrashBandicoot.Launcher.Ui;
using static CrashBandicoot.Launcher.Linux.LinuxTheme;

namespace CrashBandicoot.Launcher.Linux;

/// <summary>
/// Linux launcher window: the painted main menu plus the same sheets as the
/// Windows launcher (Controls, Settings, Mods, Cheat, About, errors) and the
/// prepare overlay. The game itself runs as a child process (<see cref="GameSession"/>)
/// while this window is hidden.
/// </summary>
sealed partial class LauncherWindow : Window
{
    readonly LauncherStage _stage;
    readonly Panel _sheetHost;
    readonly Panel _prepHost;
    readonly TextBlock _prepTitle;
    readonly TextBlock _prepDetail;
    readonly TextBlock _prepNote;
    readonly Border _prepBarFill;
    readonly DispatcherTimer _anim;
    readonly LauncherGamepad _pad = new();

    /// <summary>Focus order of the open sheet (arrow keys / gamepad walk this list).</summary>
    readonly List<Control> _nav = [];
    Control? _sheetCard;
    float _prepPulse;
    bool _prepIndeterminate;
    int _padRetryTicks;

    public LauncherWindow()
    {
        Title = "Crash Bandicoot: Recompiled";
        Width = 1024;
        Height = 720;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush(Night);
        FontFamily = Nunito;
        FontWeight = FontWeight.SemiBold;
        Foreground = Brush(Sand);
        Icon = LinuxGui.LoadWindowIcon();

        _stage = new LauncherStage(LoadMap()) { Focusable = true, FocusAdorner = null };
        _stage.Activated += OnMenuActivated;

        _sheetHost = new Panel { Background = Brush(200, 4, 10, 12), IsVisible = false };

        _prepTitle = SheetTitle("Preparing game");
        _prepDetail = BodyText("Working from your disc…", 15, 210);
        _prepNote = BodyText(
            "First prepare writes into the game folder next to the program. Your .chd or .cue + .bin must still be present to play.",
            13, 140);
        _prepBarFill = new Border { Background = Brush(Wumpa), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        var prepCard = MakeCard(552, new StackPanel
        {
            Margin = new Thickness(36, 14, 36, 22),
            Spacing = 12,
            Children =
            {
                _prepTitle,
                _prepDetail,
                new Border
                {
                    Height = 12,
                    Background = Brush(20, 255, 255, 255),
                    Child = _prepBarFill,
                },
                _prepNote,
            },
        });
        _prepTitle.Margin = new Thickness(0, 0, 0, 14);
        _prepHost = new Panel { Background = Brush(200, 4, 10, 12), IsVisible = false, Children = { prepCard } };

        Content = new Panel { Children = { _stage, _sheetHost, _prepHost } };

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        _anim = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Tick());
        Opened += (_, _) => OnOpened();
        Closed += (_, _) =>
        {
            _anim.Stop();
            _pad.Dispose();
        };
    }

    static Bitmap? LoadMap()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Ui", "world_map.png");
            return File.Exists(path) ? new Bitmap(path) : null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Launcher] world map not loaded: {ex.Message}");
            return null;
        }
    }

    void OnOpened()
    {
        _pad.TryEnsureInit();
        _anim.Start();
        _stage.Focus();
        InitState();
    }

    void Tick()
    {
        if (!IsVisible) return;
        _stage.Animate();
        if (_prepIndeterminate && _prepHost.IsVisible)
        {
            _prepPulse = (_prepPulse + 0.02f) % 1f;
            _prepBarFill.Width = 480 * (0.15 + 0.85 * _prepPulse);
        }
        PollController();
    }

    // ── Input ─────────────────────────────────────────────────────────

    void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_prepHost.IsVisible || _gameRunning)
        {
            e.Handled = true;
            return;
        }

        if (KeyCaptureBox.Active is { } capture)
        {
            capture.HandleKey(e);
            return;
        }

        if (_sheetHost.IsVisible)
        {
            // An open dropdown handles its own arrows, Enter and Escape.
            if (OpenCombo() != null) return;
            switch (e.Key)
            {
                case Key.Escape:
                    CloseSheet();
                    break;
                case Key.Up:
                    MoveSheetFocus(-1);
                    break;
                case Key.Down:
                    MoveSheetFocus(1);
                    break;
                case Key.Tab:
                    MoveSheetFocus(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                    break;
                case Key.Left:
                    NudgeOrMove(-1);
                    break;
                case Key.Right:
                    NudgeOrMove(1);
                    break;
                case Key.Enter:
                case Key.Space:
                    ActivateSheetFocused();
                    break;
                default:
                    return;
            }
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Up:
            case Key.Left:
                _stage.MoveFocus(-1);
                break;
            case Key.Down:
            case Key.Right:
            case Key.Tab:
                _stage.MoveFocus(1);
                break;
            case Key.Enter:
            case Key.Space:
                _stage.ActivateFocused();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    void PollController()
    {
        if (_prepHost.IsVisible || _gameRunning) return;
        // SDL missing or broken: do not retry (and throw) on every tick.
        if (_padRetryTicks > 0)
        {
            _padRetryTicks--;
            return;
        }
        if (!_pad.TryEnsureInit())
        {
            _padRetryTicks = 150;
            return;
        }

        var action = _pad.Poll();
        if (action == LauncherPadAction.None) return;

        if (_sheetHost.IsVisible)
        {
            if (KeyCaptureBox.Active is { } capture)
            {
                if (action.HasFlag(LauncherPadAction.Cancel))
                    capture.CancelListen();
                return;
            }
            HandleSheetPad(action);
            return;
        }

        if (action.HasFlag(LauncherPadAction.Up) || action.HasFlag(LauncherPadAction.Left)) _stage.MoveFocus(-1);
        if (action.HasFlag(LauncherPadAction.Down) || action.HasFlag(LauncherPadAction.Right)) _stage.MoveFocus(1);
        if (action.HasFlag(LauncherPadAction.Confirm)) _stage.ActivateFocused();
    }

    void HandleSheetPad(LauncherPadAction action)
    {
        if (OpenCombo() is { } open)
        {
            if (action.HasFlag(LauncherPadAction.Up)) open.SelectedIndex = Math.Max(0, open.SelectedIndex - 1);
            if (action.HasFlag(LauncherPadAction.Down)) open.SelectedIndex = Math.Min(open.ItemCount - 1, open.SelectedIndex + 1);
            if (action.HasFlag(LauncherPadAction.Confirm) || action.HasFlag(LauncherPadAction.Cancel))
                open.IsDropDownOpen = false;
            return;
        }

        if (action.HasFlag(LauncherPadAction.Cancel))
        {
            CloseSheet();
            return;
        }
        if (action.HasFlag(LauncherPadAction.Up)) MoveSheetFocus(-1);
        if (action.HasFlag(LauncherPadAction.Down)) MoveSheetFocus(1);
        if (action.HasFlag(LauncherPadAction.Left)) NudgeOrMove(-1);
        if (action.HasFlag(LauncherPadAction.Right)) NudgeOrMove(1);
        if (action.HasFlag(LauncherPadAction.Confirm)) ActivateSheetFocused();
    }

    /// <summary>
    /// The sheet's dropdown that is open, if any. Its list lives in a popup, so
    /// keyboard focus is then outside the sheet and <see cref="FocusedNav"/> finds nothing.
    /// </summary>
    ComboBox? OpenCombo() => _nav.OfType<ComboBox>().FirstOrDefault(c => c.IsDropDownOpen);

    Control? FocusedNav()
    {
        if (FocusManager?.GetFocusedElement() is not Visual focused) return null;
        foreach (var c in _nav)
        {
            if (c == focused || c.IsVisualAncestorOf(focused))
                return c;
        }
        return null;
    }

    void MoveSheetFocus(int delta)
    {
        if (_nav.Count == 0) return;
        var current = FocusedNav();
        var index = current == null ? (delta > 0 ? -1 : 0) : _nav.IndexOf(current);
        for (var step = 0; step < _nav.Count; step++)
        {
            index = (index + delta + _nav.Count) % _nav.Count;
            if (_nav[index].IsEffectivelyVisible && _nav[index].IsEffectivelyEnabled)
            {
                FocusNav(_nav[index]);
                return;
            }
        }
    }

    static void FocusNav(Control c)
    {
        c.Focus(NavigationMethod.Directional);
        c.BringIntoView();
    }

    void NudgeOrMove(int direction)
    {
        switch (FocusedNav())
        {
            case INavItem item when item.Nudge(direction):
                return;
            case ComboBox combo when combo.ItemCount > 0:
                combo.SelectedIndex = Math.Clamp(combo.SelectedIndex + direction, 0, combo.ItemCount - 1);
                return;
            default:
                MoveSheetFocus(direction);
                return;
        }
    }

    void ActivateSheetFocused()
    {
        switch (FocusedNav())
        {
            case INavItem item:
                item.Activate();
                break;
            case ComboBox combo:
                combo.IsDropDownOpen = !combo.IsDropDownOpen;
                break;
        }
    }

    // ── Sheets / overlays ─────────────────────────────────────────────

    static Border MakeCard(double width, Control content) => new()
    {
        Width = width,
        Background = CardBrush(),
        BorderBrush = Brush(90, 255, 160, 40),
        BorderThickness = new Thickness(1.5),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Child = content,
    };

    static TextBlock SheetTitle(string text) => new()
    {
        Text = text,
        FontFamily = Bungee,
        FontWeight = FontWeight.Normal,
        FontSize = 31,
        Foreground = Brush(Wumpa),
        Margin = new Thickness(36, 24, 36, 18),
    };

    static TextBlock BodyText(string text, double size, byte alpha, bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = bold ? FontWeight.ExtraBold : FontWeight.SemiBold,
        Foreground = Brush(alpha, Sand),
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>Show a sheet card centered over the menu; <paramref name="nav"/> is its focus order.</summary>
    void OpenSheet(Control card, IEnumerable<Control> nav, Control? initialFocus = null)
    {
        KeyCaptureBox.CancelActive();
        _sheetHost.Children.Clear();
        _sheetHost.Children.Add(card);
        _sheetCard = card;
        SetNav(nav);
        _sheetHost.IsVisible = true;
        var first = initialFocus ?? _nav.FirstOrDefault();
        if (first != null)
            Dispatcher.UIThread.Post(() =>
            {
                if (_sheetCard == card) FocusNav(first);
            }, DispatcherPriority.Loaded);
    }

    void SetNav(IEnumerable<Control> nav)
    {
        _nav.Clear();
        _nav.AddRange(nav);
    }

    void CloseSheet()
    {
        KeyCaptureBox.CancelActive();
        _sheetHost.Children.Clear();
        _sheetHost.IsVisible = false;
        _sheetCard = null;
        _nav.Clear();
        _stage.Focus();
    }

    void ShowPrep(string title, string detail, double fraction, bool indeterminate = false, string? note = null)
    {
        _prepTitle.Text = title;
        _prepDetail.Text = detail;
        _prepNote.Text = note ??
            "First prepare writes into the game folder next to the program. Your .chd or .cue + .bin must still be present to play.";
        _prepIndeterminate = indeterminate;
        if (!indeterminate)
            _prepBarFill.Width = 480 * Math.Clamp(fraction, 0, 1);
        _prepHost.IsVisible = true;
    }

    void HidePrep()
    {
        _prepIndeterminate = false;
        _prepHost.IsVisible = false;
    }
}
