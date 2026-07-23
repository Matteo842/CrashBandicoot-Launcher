namespace RecompOne.Runtime.Host.Window;

public interface IPanel
{
    string Name { get; }
    bool IsOpen { get; set; }
    void Draw();
}
