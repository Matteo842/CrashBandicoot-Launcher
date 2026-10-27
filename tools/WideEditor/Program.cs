// Crash Bandicoot 16:9 editor: pick a level, then fix the widescreen side bands by hand
// (copy/move/rotate scenery triangles, draw new textured quads, hide polygons).
// Saves RecompOne.Runtime/WideEdits/level_NN.json, which the game embeds and, when a
// copy sits next to the built game, reloads live.
using Silk.NET.Maths;
using Silk.NET.Windowing;
using WideEditor;

var options = WindowOptions.Default with
{
    Title = "Crash Bandicoot - Editor 16:9",
    Size = new Vector2D<int>(1600, 900),
    WindowState = WindowState.Maximized,
    API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
    VSync = true,
};
var window = Window.Create(options);
Editor? editor = null;
window.Load += () => editor = new Editor(window, args);
window.Render += dt => editor?.Frame(dt);
window.Closing += () =>
{
    editor?.Close();
    editor?.Dispose();
    editor = null;
};
try
{
    window.Run();
}
catch (Exception e)
{
    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "wideeditor.log"), $"{DateTime.Now}: {e}\n");
    throw;
}
window.Dispose();
