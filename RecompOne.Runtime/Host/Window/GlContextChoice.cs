using RecompOne.Runtime.Config;
using Silk.NET.Windowing;

namespace RecompOne.Runtime.Host;

/// <summary>
/// One way to open the game window's GL context (#167, #80). The renderer runs on
/// OpenGL 4.3 Core, on OpenGL 3.3 Core with blit copies, and on OpenGL ES 3.0+
/// with framebuffer fetch or dual-source blending. <see cref="Egl"/> asks GLFW for
/// an EGL context instead of GLX: some ARM boards create ES contexts only there.
/// </summary>
readonly record struct GlContextChoice(bool Es, int Major, int Minor, bool Egl = false)
{
    public GraphicsAPI Api => new(Es ? ContextAPI.OpenGLES : ContextAPI.OpenGL,
        ContextProfile.Core, ForwardCompatible ? ContextFlags.ForwardCompatible : ContextFlags.Default,
        new APIVersion(Major, Minor));

    /// <summary>
    /// macOS gives a 3.2+ Core context only when it is asked forward-compatible,
    /// and then returns its newest one (4.1) for any version up to 4.1 (#14).
    /// </summary>
    public bool ForwardCompatible => !Es && OperatingSystem.IsMacOS();

    public string Name => Es
        ? $"OpenGL ES {Major}.{Minor}{(Egl ? " (EGL)" : "")}"
        : $"OpenGL {Major}.{Minor} Core";

    /// <summary>Contexts to try, in order, for the Renderer setting.</summary>
    public static List<GlContextChoice> Chain(int graphicsApi)
    {
        // macOS stops at OpenGL 4.1 and has no OpenGL ES or EGL (#14): 4.1 Core runs
        // the 3.3 path (blit copies), whatever the Renderer setting says.
        if (OperatingSystem.IsMacOS())
            return [new(false, 4, 1), new(false, 3, 3)];

        GlContextChoice[] gl43 = [new(false, 4, 3)];
        GlContextChoice[] gl33 = [new(false, 3, 3)];
        // Mesa returns its newest ES version for any request, other drivers need
        // the exact one: Raspberry Pi 4/5 (v3d) stop at ES 3.1. Windows has no
        // libEGL unless ANGLE is installed, so only WGL is tried there.
        var es = new List<GlContextChoice> { new(true, 3, 2), new(true, 3, 1), new(true, 3, 0) };
        if (!OperatingSystem.IsWindows())
            es.AddRange([new(true, 3, 2, true), new(true, 3, 1, true), new(true, 3, 0, true)]);

        IEnumerable<GlContextChoice> preferred = graphicsApi switch
        {
            ViewConfig.GraphicsApiGl33 => gl33,
            ViewConfig.GraphicsApiGles => es,
            _ => gl43,
        };
        // A chosen renderer that fails still falls back, so a wrong pick cannot stop the game.
        return preferred.Concat(gl43).Concat(gl33).Concat(es).Distinct().ToList();
    }
}
