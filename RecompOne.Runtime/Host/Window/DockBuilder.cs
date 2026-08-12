using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace RecompOne.Runtime.Host.Window;

internal static unsafe class DockBuilder
{
    const string Lib = "cimgui";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] 
    static extern void igDockBuilderRemoveNode(uint nodeId);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] 
    static extern void igDockBuilderAddNode(uint nodeId, int flags);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern void igDockBuilderSetNodeSize(uint nodeId, Vector2 size);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern void igDockBuilderDockWindow(byte* windowName, uint nodeId);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern void igDockBuilderFinish(uint nodeId);

    public static void SetupCenterLayout(uint dockId, Vector2 size, string windowName, ImGuiDockNodeFlags flags)
    {
        igDockBuilderRemoveNode(dockId);
        igDockBuilderAddNode(dockId, (int)flags);
        igDockBuilderSetNodeSize(dockId, size);

        var bytes = System.Text.Encoding.UTF8.GetBytes(windowName + "\0");
        fixed (byte* p = bytes) igDockBuilderDockWindow(p, dockId);

        igDockBuilderFinish(dockId);
    }
}
