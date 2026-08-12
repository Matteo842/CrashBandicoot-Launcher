using System.Runtime.InteropServices;
using RecompOne.Runtime.Hle;
using Silk.NET.OpenGL;

namespace CrashBandicoot.AndroidRuntime;

/// <summary>
/// Immutable snapshot of the GLES driver. Extension discovery lives here so
/// the game and the ROM-free GPU lab always select the exact same paths.
/// </summary>
sealed class AndroidGlesInfo
{
    public const string ForceFramebufferFetchExtra = "gpu_framebuffer_fetch";

    public required string Vendor { get; init; }
    public required string Renderer { get; init; }
    public required string Version { get; init; }
    public required string ShadingLanguageVersion { get; init; }
    public required string[] Extensions { get; init; }
    public required GlesFramebufferFetchPath FramebufferFetchPath { get; init; }
    public string? TextureBarrierFunction { get; init; }
    public bool QcomShadingRateAvailable { get; init; }

    nint TextureBarrierAddress { get; init; }
    nint ShadingRateAddress { get; init; }

    public string FramebufferFetchLabel => FramebufferFetchPath switch
    {
        GlesFramebufferFetchPath.Ext => "EXT coherent",
        GlesFramebufferFetchPath.Arm => "ARM coherent",
        _ => "fallback",
    };

    public static AndroidGlesInfo Capture(GL gl, AndroidEglContext egl, string? forcedPath = null)
    {
        var extensions = ReadString(gl, StringName.Extensions);
        var extensionSet = new HashSet<string>(
            extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

        var hasExtFetch = extensionSet.Contains("GL_EXT_shader_framebuffer_fetch");
        var hasArmFetch = extensionSet.Contains("GL_ARM_shader_framebuffer_fetch");
        var fetchPath = forcedPath?.Trim().ToLowerInvariant() switch
        {
            "ext" when hasExtFetch => GlesFramebufferFetchPath.Ext,
            "arm" when hasArmFetch => GlesFramebufferFetchPath.Arm,
            "fallback" or "none" => GlesFramebufferFetchPath.None,
            _ => hasExtFetch
                ? GlesFramebufferFetchPath.Ext
                : hasArmFetch
                    ? GlesFramebufferFetchPath.Arm
                    : GlesFramebufferFetchPath.None,
        };

        string? barrierFunction = extensionSet.Contains("GL_EXT_texture_barrier")
            ? "glTextureBarrierEXT"
            : extensionSet.Contains("GL_NV_texture_barrier")
                ? "glTextureBarrierNV"
                : null;
        var barrierAddress = barrierFunction == null ? 0 : egl.GetProcAddress(barrierFunction);
        var shadingRateAddress = extensionSet.Contains("GL_QCOM_shading_rate")
            ? egl.GetProcAddress("glShadingRateQCOM")
            : 0;

        return new AndroidGlesInfo
        {
            Vendor = ReadString(gl, StringName.Vendor),
            Renderer = ReadString(gl, StringName.Renderer),
            Version = ReadString(gl, StringName.Version),
            ShadingLanguageVersion = ReadString(gl, StringName.ShadingLanguageVersion),
            Extensions = extensionSet.Order(StringComparer.Ordinal).ToArray(),
            FramebufferFetchPath = fetchPath,
            TextureBarrierFunction = barrierAddress == 0 ? null : barrierFunction,
            TextureBarrierAddress = barrierAddress,
            QcomShadingRateAvailable = shadingRateAddress != 0,
            ShadingRateAddress = shadingRateAddress,
        };
    }

    public (bool textureBarrier, bool coarseShading) ConfigureBackend(GlBackend backend, int scale)
    {
        var textureBarrier = backend.ConfigureGlesTextureBarrier(TextureBarrierAddress);
        var coarseShading = backend.FramebufferFetchPath == GlesFramebufferFetchPath.None &&
                            scale >= 8 &&
                            backend.ConfigureGlesShadingRate(ShadingRateAddress);
        return (textureBarrier, coarseShading);
    }

    static unsafe string ReadString(GL gl, StringName name) =>
        Marshal.PtrToStringAnsi((nint)gl.GetString(name)) ?? string.Empty;
}
