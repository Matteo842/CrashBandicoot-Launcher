namespace RecompOne.Runtime.Host.Window;

public interface ISettingsSection
{
    string Id { get; }
    string Title { get; }
    int Order { get; }
    void Draw();
}
