using Silk.NET.Input;

namespace WideEditor;

/// <summary>
/// Input context handed to Silk's ImGuiController. Its key table (2.22) throws
/// NotImplementedException for Unknown, World1, World2 and F25, and the throw escapes
/// the GLFW key callback and kills the game (#152: ABNT2 '/', '\' and keypad '.').
/// Those keys never reach ImGui; everything else is passed through unchanged.
/// </summary>
sealed class ImGuiInputContext : IInputContext
{
    readonly IInputContext _inner;
    readonly IKeyboard[] _keyboards;

    public ImGuiInputContext(IInputContext inner)
    {
        _inner = inner;
        _keyboards = inner.Keyboards.Count > 0 ? [new FilteredKeyboard(inner.Keyboards[0])] : [];
    }

    public nint Handle => _inner.Handle;
    public IReadOnlyList<IGamepad> Gamepads => _inner.Gamepads;
    public IReadOnlyList<IJoystick> Joysticks => _inner.Joysticks;
    public IReadOnlyList<IKeyboard> Keyboards => _keyboards;
    public IReadOnlyList<IMouse> Mice => _inner.Mice;
    public IReadOnlyList<IInputDevice> OtherDevices => _inner.OtherDevices;

    public event Action<IInputDevice, bool>? ConnectionChanged
    {
        add => _inner.ConnectionChanged += value;
        remove => _inner.ConnectionChanged -= value;
    }

    // The real context belongs to HostWindow, which disposes it on close.
    public void Dispose() { }

    static bool ImGuiKnows(Key key) =>
        key is not (Key.Unknown or Key.World1 or Key.World2 or Key.F25) && Enum.IsDefined(key);

    sealed class FilteredKeyboard : IKeyboard
    {
        readonly IKeyboard _inner;

        public FilteredKeyboard(IKeyboard inner)
        {
            _inner = inner;
            inner.KeyDown += (_, key, scancode) =>
            {
                if (ImGuiKnows(key)) KeyDown?.Invoke(this, key, scancode);
            };
            inner.KeyUp += (_, key, scancode) =>
            {
                if (ImGuiKnows(key)) KeyUp?.Invoke(this, key, scancode);
            };
        }

        public event Action<IKeyboard, Key, int>? KeyDown;
        public event Action<IKeyboard, Key, int>? KeyUp;

        public event Action<IKeyboard, char>? KeyChar
        {
            add => _inner.KeyChar += value;
            remove => _inner.KeyChar -= value;
        }

        public string Name => _inner.Name;
        public int Index => _inner.Index;
        public bool IsConnected => _inner.IsConnected;
        public IReadOnlyList<Key> SupportedKeys => _inner.SupportedKeys;

        public string ClipboardText
        {
            get => _inner.ClipboardText;
            set => _inner.ClipboardText = value;
        }

        public bool IsKeyPressed(Key key) => _inner.IsKeyPressed(key);
        public bool IsScancodePressed(int scancode) => _inner.IsScancodePressed(scancode);
        public void BeginInput() => _inner.BeginInput();
        public void EndInput() => _inner.EndInput();
    }
}
