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
    const int EglBadSurface = 0x300D;
    const int MaxSurfaceRecreateFailures = 120;

    readonly EGLDisplay _display;
    readonly EGLConfig _config;
    readonly EGLContext _context;
    readonly Func<Surface> _surfaceSource;
    EGLSurface _surface;
    nint _glesLibrary;
    bool _disposed;
    int _surfaceRecreateFailures;

    public int SurfaceWidth => QuerySurface(EGL14.EglWidth);
    public int SurfaceHeight => QuerySurface(EGL14.EglHeight);

    public AndroidEglContext(Surface nativeWindow, Func<Surface> surfaceSource)
    {
        _surfaceSource = surfaceSource;

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
        _config = configs[0];

        _surface = CreateWindowSurface(nativeWindow);

        var contextAttributes = new[]
        {
            EglContextClientVersion, 3,
            EGL14.EglNone,
        };
        _context = EGL14.EglCreateContext(_display, _config, EGL14.EglNoContext,
                       contextAttributes, 0)
                   ?? throw Error("eglCreateContext");

        if (!EGL14.EglMakeCurrent(_display, _surface, _surface, _context))
            throw Error("eglMakeCurrent");
        // FrameClock is paced from AudioTrack's 44.1 kHz playback head. A
        // second blocking 60 Hz pacer here steals up to a full display period
        // from the game/VBlank thread under load, which makes the SPU music
        // sequencer fall to ~50 Hz while audio output itself stays steady.
        EGL14.EglSwapInterval(_display, 0);

        NativeLibrary.TryLoad("libGLESv3.so", out _glesLibrary);
    }

    EGLSurface CreateWindowSurface(Surface nativeWindow)
    {
        // The emulated vblank is 60 Hz. Request that cadence explicitly on
        // 90/120 Hz phones so the compositor follows the software 60 Hz
        // frame pacer instead of presenting the same cadence at a higher mode.
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
            nativeWindow.SetFrameRate(
                60f, (int)SurfaceFrameRateCompatibility.Default);

        var surfaceAttributes = new[] { EGL14.EglNone };
        return EGL14.EglCreateWindowSurface(_display, _config, nativeWindow,
                   surfaceAttributes, 0)
               ?? throw Error("eglCreateWindowSurface");
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

    /// <summary>
    /// Returns false when the frame was dropped because the TextureView surface
    /// was recreated under us (immersive-mode relayout, display change): the
    /// window surface is rebuilt from the current SurfaceTexture and the next
    /// frame presents normally again.
    /// </summary>
    public bool SwapBuffers()
    {
        if (EGL14.EglSwapBuffers(_display, _surface))
        {
            _surfaceRecreateFailures = 0;
            return true;
        }

        var error = EGL14.EglGetError();
        if (error != EglBadSurface)
            throw new InvalidOperationException($"eglSwapBuffers fallita (EGL 0x{error:X}).");

        if (!RecreateWindowSurface())
            return false; // surface mid-relayout; frame dropped, retry next frame

        _surfaceRecreateFailures = 0;
        Android.Util.Log.Info("CrashGPU", "EGL window surface recreated after surface loss.");
        return false;
    }

    bool RecreateWindowSurface()
    {
        try
        {
            var fresh = _surfaceSource();
            var newSurface = CreateWindowSurface(fresh);
            var oldSurface = _surface;
            if (!EGL14.EglMakeCurrent(_display, newSurface, newSurface, _context))
            {
                EGL14.EglDestroySurface(_display, newSurface);
                return FailRecreate();
            }
            EGL14.EglSwapInterval(_display, 0);
            EGL14.EglDestroySurface(_display, oldSurface);
            _surface = newSurface;
            return true;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("CrashGPU", $"EGL surface recreate failed: {ex.Message}");
            return FailRecreate();
        }
    }

    bool FailRecreate()
    {
        // Keep throwing only after a sustained failure streak: transient misses
        // just drop frames while the TextureView is mid-relayout.
        if (++_surfaceRecreateFailures >= MaxSurfaceRecreateFailures)
            throw new InvalidOperationException("La superficie video Android non è recuperabile.");
        return false;
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
