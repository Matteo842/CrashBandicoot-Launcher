using System.Runtime.InteropServices;
using Android.Opengl;
using Android.Views;
using Silk.NET.Core.Contexts;

namespace CrashBandicoot.AndroidRuntime;

/// <summary>
/// OpenGL ES context and window surface owned by the game thread. Rendering is
/// swapped straight to Android, avoiding a GPU readback and Bitmap upload every
/// frame.
/// </summary>
sealed class AndroidEglContext : INativeContext
{
    const int EglOpenGles3BitKhr = 0x0040;
    const int EglContextClientVersion = 0x3098;

    readonly EGLDisplay _display;
    readonly EGLSurface _surface;
    readonly EGLContext _context;
    nint _glesLibrary;
    bool _disposed;

    public int SurfaceWidth => QuerySurface(EGL14.EglWidth);
    public int SurfaceHeight => QuerySurface(EGL14.EglHeight);

    public AndroidEglContext(Surface nativeWindow)
    {
        _display = EGL14.EglGetDisplay(EGL14.EglDefaultDisplay)
                   ?? throw new InvalidOperationException("EGL display non disponibile.");
        var major = new int[1];
        var minor = new int[1];
        if (!EGL14.EglInitialize(_display, major, 0, minor, 0))
            throw Error("eglInitialize");

        var configAttributes = new[]
        {
            EGL14.EglRenderableType, EglOpenGles3BitKhr,
            EGL14.EglSurfaceType, EGL14.EglWindowBit,
            EGL14.EglRedSize, 8,
            EGL14.EglGreenSize, 8,
            EGL14.EglBlueSize, 8,
            EGL14.EglAlphaSize, 8,
            EGL14.EglNone,
        };
        var configs = new EGLConfig[1];
        var count = new int[1];
        if (!EGL14.EglChooseConfig(_display, configAttributes, 0,
                configs, 0, configs.Length, count, 0) || count[0] == 0 || configs[0] == null)
            throw Error("eglChooseConfig");

        var surfaceAttributes = new[] { EGL14.EglNone };
        _surface = EGL14.EglCreateWindowSurface(_display, configs[0], nativeWindow,
                       surfaceAttributes, 0)
                   ?? throw Error("eglCreateWindowSurface");

        var contextAttributes = new[]
        {
            EglContextClientVersion, 3,
            EGL14.EglNone,
        };
        _context = EGL14.EglCreateContext(_display, configs[0], EGL14.EglNoContext,
                       contextAttributes, 0)
                   ?? throw Error("eglCreateContext");

        if (!EGL14.EglMakeCurrent(_display, _surface, _surface, _context))
            throw Error("eglMakeCurrent");
        EGL14.EglSwapInterval(_display, 1);

        NativeLibrary.TryLoad("libGLESv3.so", out _glesLibrary);
    }

    public nint GetProcAddress(string proc, int? slot = null)
    {
        var address = EglGetProcAddress(proc);
        if (address == 0 && _glesLibrary != 0)
            NativeLibrary.TryGetExport(_glesLibrary, proc, out address);
        return address;
    }

    public bool TryGetProcAddress(string proc, out nint addr, int? slot = null)
    {
        addr = GetProcAddress(proc, slot);
        return addr != 0;
    }

    public void SwapBuffers()
    {
        if (!EGL14.EglSwapBuffers(_display, _surface))
            throw Error("eglSwapBuffers");
    }

    int QuerySurface(int attribute)
    {
        var value = new int[1];
        return EGL14.EglQuerySurface(_display, _surface, attribute, value, 0) ? value[0] : 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        EGL14.EglMakeCurrent(_display, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
        EGL14.EglDestroyContext(_display, _context);
        EGL14.EglDestroySurface(_display, _surface);
        EGL14.EglTerminate(_display);
        if (_glesLibrary != 0) NativeLibrary.Free(_glesLibrary);
        _glesLibrary = 0;
    }

    static InvalidOperationException Error(string operation) =>
        new($"{operation} fallita (EGL 0x{EGL14.EglGetError():X}).");

    [DllImport("libEGL.so", EntryPoint = "eglGetProcAddress")]
    static extern nint EglGetProcAddress([MarshalAs(UnmanagedType.LPUTF8Str)] string procname);
}
