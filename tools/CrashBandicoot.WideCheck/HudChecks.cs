using System.Reflection;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Memory;

// Small synthetic guest objects exercise the actual range tracking without a
// disc, a window, timing-dependent input, or access to the player's saves.
static class HudChecks
{
    public static int Run()
    {
        var memory = new PSMemory();
        const uint obj = 0x80070000, entry = 0x80071000, header = 0x80071100;
        const uint context = 0x80072000, prim = 0x80073000;
        int checks = 0;
        void Check(bool pass, string name)
        {
            if (!pass) throw new InvalidOperationException(name);
            checks++;
        }
        object? Call(string name, params object[] args) => typeof(FramePacing)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
        int Pickup(float x) => (int)Call("NativeWidePickupShift", obj, x, 86)!;
        void Tail(uint address) => memory.WriteU32(context + 0x78, address);
        float oldAspect = GpuHle.WideAspect;
        try
        {
            Call("ResetNativeWideHud");
            GpuHle.WideAspect = 16f / 9f;
            GpuHle.RefreshWideFov();
            memory.WriteU32(0x8005840C, context);
            memory.WriteU32(obj + 0x20, entry);
            memory.WriteU32(entry + 16, header);
            memory.WriteU32(header, 4); // DispC
            memory.WriteU32(obj + 0x80, unchecked((uint)(-130 << 8))); // 8.8 from centre
            Tail(prim);
            Call("NoteNativeWideHudTransform", memory, obj);
            Tail(prim + 0x40);
            FramePacing.CloseNativeWideHudRange(memory);
            Check(FramePacing.NativeWideHudRangeOf(prim & 0x1FFFFC) == 0, "HUD range start");
            Check(FramePacing.NativeWideHudRangeOf((prim + 0x40) & 0x1FFFFC) == -1, "Fade outside HUD range");
            Check(FramePacing.NativeWideHudShift(0, 126, 0, 512, 86) == -86, "Fruit follows left edge");
            Check(FramePacing.NativeWideHudShift(0, 210, 0, 512, 86) == -86, "Digits retain icon spacing");
            Check(FramePacing.NativeWideHudShift(0, 200, 0, 512, 86) == -86, "Spinning fruit ignores prim wobble");
            Check(FramePacing.NativeWideHudShift(-1, 386, 0, 512, 86) == 86, "Lives follow right edge");
            Check(FramePacing.NativeWideHudShift(-1, 256, 0, 512, 86) == 0, "Token strip stays centred");
            Check(FramePacing.NativeWideHudShift(-1, 550, 0, 512, 86) == 86, "Parked token stays beyond wide edge");
            Check(FramePacing.NativeWideHudShift(0, 126, 0, 512, 0) == 0, "4:3 ignores cached wide shift");
            Check(FramePacing.NativeWideHudShift(-1, 526, 400, 512, 86) == -86, "Draw-area origin");
            Check(!(bool)Call("KeepRealDt", memory, obj)!, "HUD gates to 30 Hz");
            memory.WriteU32(obj + 0x80, 0);
            Call("ResetNativeWideHud");
            Tail(prim);
            Call("NoteNativeWideHudTransform", memory, obj);
            Tail(prim + 0x40);
            FramePacing.CloseNativeWideHudRange(memory);
            Check(FramePacing.NativeWideHudShift(0, 180, 0, 512, 86) == 0, "Tawna stays centred despite side prim");
            Check(FramePacing.NativeWideHudShift(0, 330, 0, 512, 86) == 0, "Tawna ignores opposite prim");
            Call("ResetNativeWideHud");
            memory.WriteU32(obj + 0x80, 0x01000000); // world X — no 8.8 HUD trans
            Tail(prim);
            Call("NoteNativeWideHudTransform", memory, obj);
            Tail(prim + 0x40);
            FramePacing.CloseNativeWideHudRange(memory);
            Check(FramePacing.NativeWideHudShift(0, 180, 0, 512, 86) == 0, "Tawna without 8.8 trans ignores side prim");
            Call("ResetNativeWideHud");
            Tail(prim);
            Call("NoteNativeWideHudTransform", memory, obj);
            Tail(prim + 0x40);
            FramePacing.CloseNativeWideHudRange(memory);
            Check(FramePacing.NativeWideHudShift(0, 550, 0, 512, 86) == 86, "Parked token without 8.8 trans still shifts");
            memory.WriteU32(obj + 0x80, 0);
            Call("ResetNativeWideHud");
            Check(Pickup(0.2f) == 0 && Pickup(-0.3f) == 0, "Tawna portrait prims stay centred");
            Call("ResetNativeWideHud");
            Check(Pickup(0.8f) == 0, "Pickup starts at its world position");
            Check(Pickup(0f) == 0, "Tawna flight reaches centred strip");
            Check(Pickup(-0.5f) == -86, "Cross-screen fruit reaches left counter");
            Call("ResetNativeWideHud");
            Check(Pickup(-0.8f) == 0 && Pickup(0.5f) == 86, "Cross-screen life reaches right counter");
            memory.WriteU32(header, 3); // FruiC in world space
            Tail(prim);
            Call("NoteNativeWideHudTransform", memory, obj);
            Tail(prim + 0x40);
            FramePacing.CloseNativeWideHudRange(memory);
            Check(FramePacing.NativeWideHudRangeOf(prim & 0x1FFFFC) == -1, "World fruit remains world geometry");
            Check((bool)Call("KeepRealDt", memory, obj)!, "World fruit stays Euler");
            memory.WriteU32(obj + 0xCC, 0x200); // FLAG_2D
            Tail(prim);
            Call("NoteNativeWideHudTransform", memory, obj);
            Tail(prim + 0x40);
            FramePacing.CloseNativeWideHudRange(memory);
            Check(FramePacing.NativeWideHudRangeOf(prim & 0x1FFFFC) == 0, "Flying fruit is HUD");
            Check(!(bool)Call("KeepRealDt", memory, obj)!, "Flying pickup gates to 30 Hz");
            GpuHle.CurrentHudRange = 0;
            FramePacing.FinishNativeWideDraw();
            Check(FramePacing.NativeWideHudRangeOf(prim & 0x1FFFFC) == -1 && GpuHle.CurrentHudRange == -1,
                "Draw completion discards HUD ownership");
            GpuHle.WideAspect = 0;
            GpuHle.RefreshWideFov();
            Tail(prim);
            Call("NoteNativeWideHudTransform", memory, obj);
            Tail(prim + 0x40);
            FramePacing.CloseNativeWideHudRange(memory);
            Check(FramePacing.NativeWideHudRangeOf(prim & 0x1FFFFC) == -1, "4:3 does not capture HUD ranges");
            Console.WriteLine($"HUD checks passed: {checks}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"HUD check failed: {ex.Message}");
            return 1;
        }
        finally
        {
            Call("ResetNativeWideHud");
            GpuHle.WideAspect = oldAspect;
            GpuHle.RefreshWideFov();
        }
    }
}
