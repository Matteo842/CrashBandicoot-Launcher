using RecompOne.Runtime.Config;

namespace CrashBandicoot.Launcher.Ui;

/// <summary>Click-to-capture key binding, matching the in-game Input settings remap.</summary>
sealed class KeyCaptureBox : Control
{
    static KeyCaptureBox? _active;
    string _boundKey = "";
    bool _listening;

    public KeyCaptureBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable | ControlStyles.UserMouse, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        BackColor = Color.FromArgb(30, 20, 12);
        ForeColor = NativeTheme.Sand;
    }

    public static bool AnyListening => _active is { _listening: true };
    public static KeyCaptureBox? Active => _active is { _listening: true } box ? box : null;

    public static void CancelActive() => _active?.CancelListen();

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string BoundKey
    {
        get => _boundKey;
        set
        {
            var next = KeyBindingNames.Canonical(value);
            if (_boundKey == next) return;
            _boundKey = next;
            Invalidate();
        }
    }

    public void BeginListen()
    {
        if (_active != null && _active != this)
            _active.CancelListen();
        _active = this;
        _listening = true;
        if (!Focused) Focus();
        Invalidate();
    }

    public void CancelListen()
    {
        if (!_listening && _active != this) return;
        _listening = false;
        if (_active == this) _active = null;
        Invalidate();
    }

    public bool HandleCmdKey(Keys keyData)
    {
        if (!_listening) return false;
        var code = keyData & Keys.KeyCode;
        if (code is Keys.None) return true;
        if (code == Keys.Escape)
        {
            CancelListen();
            return true;
        }

        var name = KeyBindingNames.NameFromVirtualKey((int)code);
        if (name == null) return true;
        BoundKey = name;
        CancelListen();
        return true;
    }

    protected override bool IsInputKey(Keys keyData)
    {
        if (_listening) return true;
        var code = keyData & Keys.KeyCode;
        if (code is Keys.Enter or Keys.Space) return true;
        return base.IsInputKey(keyData);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_listening)
            return HandleCmdKey(keyData);
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_listening)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            HandleCmdKey(e.KeyData);
            return;
        }

        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            BeginListen();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            Focus();
            BeginListen();
        }
        base.OnMouseDown(e);
    }

    protected override void OnLeave(EventArgs e)
    {
        if (_listening) CancelListen();
        base.OnLeave(e);
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        if (_listening) CancelListen();
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _active == this)
            CancelListen();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var box = new Rectangle(0, 0, Width - 1, Height - 1);
        var hot = _listening || Focused;
        using var border = new Pen(hot ? NativeTheme.WumpaHot : Color.FromArgb(140, 255, 200, 120), 1f);
        g.DrawRectangle(border, box);

        string text;
        Color color;
        if (_listening)
        {
            text = "[press key...]";
            color = NativeTheme.Wumpa;
        }
        else if (string.IsNullOrEmpty(_boundKey))
        {
            text = "unbound";
            color = Color.FromArgb(140, NativeTheme.Sand);
        }
        else
        {
            text = _boundKey;
            color = ForeColor;
        }

        var font = Font;
        var size = g.MeasureString(text, font);
        var x = 8f;
        var y = Math.Max(0, (Height - size.Height) / 2f);
        using var br = new SolidBrush(color);
        g.DrawString(text, font, br, x, y);
    }
}
