using Android.Content;

namespace CrashBandicoot.AndroidRuntime;

enum TouchControlGroup
{
    Dpad,
    FaceButtons,
    SystemButtons,
    ShoulderButtons,
}

/// <summary>Persistent, device-independent layout for the touch controller.</summary>
sealed class TouchControlSettings
{
    const string PreferencesName = "touch_controls";
    readonly ISharedPreferences _preferences;

    public bool Enabled { get; private set; }
    public bool UseColors { get; private set; }
    public float Opacity { get; private set; }
    public float Scale { get; private set; }
    public bool ShowShoulders { get; private set; }
    public float DpadX { get; private set; }
    public float DpadY { get; private set; }
    public float FaceX { get; private set; }
    public float FaceY { get; private set; }
    public float SystemX { get; private set; }
    public float SystemY { get; private set; }
    public float ShouldersY { get; private set; }

    public TouchControlSettings(Context context)
    {
        _preferences = context.GetSharedPreferences(PreferencesName, FileCreationMode.Private)!;
        Load();
    }

    void Load()
    {
        Enabled = _preferences.GetBoolean(nameof(Enabled), true);
        UseColors = _preferences.GetBoolean(nameof(UseColors), true);
        Opacity = _preferences.GetFloat(nameof(Opacity), 1f);
        Scale = _preferences.GetFloat(nameof(Scale), 1f);
        ShowShoulders = _preferences.GetBoolean(nameof(ShowShoulders), false);
        DpadX = _preferences.GetFloat(nameof(DpadX), 0.105f);
        DpadY = _preferences.GetFloat(nameof(DpadY), 0.765f);
        FaceX = _preferences.GetFloat(nameof(FaceX), 0.895f);
        FaceY = _preferences.GetFloat(nameof(FaceY), 0.765f);
        SystemX = _preferences.GetFloat(nameof(SystemX), 0.5f);
        SystemY = _preferences.GetFloat(nameof(SystemY), 0.945f);
        ShouldersY = _preferences.GetFloat(nameof(ShouldersY), 0.115f);
    }

    public void SetEnabled(bool value)
    {
        Enabled = value;
        Save();
    }

    public void SetUseColors(bool value)
    {
        UseColors = value;
        Save();
    }

    public void SetOpacity(float value)
    {
        Opacity = Math.Clamp(value, 0.20f, 1f);
        Save();
    }

    public void SetScale(float value)
    {
        Scale = Math.Clamp(value, 0.70f, 1.40f);
        Save();
    }

    public void SetShowShoulders(bool value)
    {
        ShowShoulders = value;
        Save();
    }

    public void Move(TouchControlGroup group, float x, float y)
    {
        x = Math.Clamp(x, 0.02f, 0.98f);
        y = Math.Clamp(y, 0.04f, 0.98f);
        switch (group)
        {
            case TouchControlGroup.Dpad:
                DpadX = x;
                DpadY = y;
                break;
            case TouchControlGroup.FaceButtons:
                FaceX = x;
                FaceY = y;
                break;
            case TouchControlGroup.SystemButtons:
                SystemX = x;
                SystemY = y;
                break;
            case TouchControlGroup.ShoulderButtons:
                ShouldersY = y;
                break;
        }
    }

    public void CommitLayout() => Save();

    public void Reset()
    {
        Enabled = true;
        UseColors = true;
        Opacity = 1f;
        Scale = 1f;
        ShowShoulders = false;
        DpadX = 0.105f;
        DpadY = 0.765f;
        FaceX = 0.895f;
        FaceY = 0.765f;
        SystemX = 0.5f;
        SystemY = 0.945f;
        ShouldersY = 0.115f;
        Save();
    }

    void Save()
    {
        _preferences.Edit()!
            .PutBoolean(nameof(Enabled), Enabled)!
            .PutBoolean(nameof(UseColors), UseColors)!
            .PutFloat(nameof(Opacity), Opacity)!
            .PutFloat(nameof(Scale), Scale)!
            .PutBoolean(nameof(ShowShoulders), ShowShoulders)!
            .PutFloat(nameof(DpadX), DpadX)!
            .PutFloat(nameof(DpadY), DpadY)!
            .PutFloat(nameof(FaceX), FaceX)!
            .PutFloat(nameof(FaceY), FaceY)!
            .PutFloat(nameof(SystemX), SystemX)!
            .PutFloat(nameof(SystemY), SystemY)!
            .PutFloat(nameof(ShouldersY), ShouldersY)!
            .Apply();
    }
}
