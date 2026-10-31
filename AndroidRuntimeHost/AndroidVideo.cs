using RecompOne.Runtime.Hle;

namespace CrashBandicoot.AndroidRuntime;

/// <summary>The game thread's renderer plus its window surface (GLES or Vulkan).</summary>
interface IAndroidVideo : IDisposable
{
    IGpuBackend Backend { get; }
    /// <summary>Short label for logs and the status line.</summary>
    string Name { get; }
    int SurfaceWidth { get; }
    int SurfaceHeight { get; }

    /// <summary>Pick the display image (end of the PS1 frame).</summary>
    (int w, int h, float aspect) Prepare(int dispX, int dispY, int w, int h, bool rgb24, int surfaceWidth, int surfaceHeight);
    /// <summary>Draw it into the window surface.</summary>
    void Composite(int surfaceWidth, int surfaceHeight, float aspect);
    /// <summary>Hand the frame to the compositor.</summary>
    void Swap();
    void SetExpectedSize(int width, int height);
}

/// <summary>The OpenGL ES path (EGL window surface + <see cref="GlBackend"/>).</summary>
sealed class AndroidGlVideo(AndroidEglContext egl, GlBackend backend) : IAndroidVideo
{
    public IGpuBackend Backend => backend;
    public GlBackend Gl => backend;
    public string Name => "GLES";
    public int SurfaceWidth => egl.SurfaceWidth;
    public int SurfaceHeight => egl.SurfaceHeight;

    public (int w, int h, float aspect) Prepare(int dispX, int dispY, int w, int h, bool rgb24, int surfaceWidth, int surfaceHeight)
    {
        var presented = backend.PresentDisplay(dispX, dispY, w, h, rgb24, surfaceWidth, surfaceHeight);
        return (presented.w, presented.h, presented.aspect);
    }

    public void Composite(int surfaceWidth, int surfaceHeight, float aspect)
        => backend.PresentToDefaultFramebuffer(surfaceWidth, surfaceHeight, aspect);

    public void Swap() => egl.SwapBuffers();
    public void SetExpectedSize(int width, int height) => egl.SetExpectedSize(width, height);

    // MainActivity owns and disposes the GL objects (they predate this wrapper).
    public void Dispose() { }
}
