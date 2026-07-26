namespace RecompOne.Runtime.Host.Window;

public interface IDevMenuSection
{
    string Id { get; }
    string Title { get; }
    int Order { get; }
    void Draw();
}
