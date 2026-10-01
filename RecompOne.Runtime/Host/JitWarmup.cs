using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace RecompOne.Runtime.Host;

/// <summary>
/// The recompiled game DLL is built on the player's machine, so it cannot ship
/// precompiled (ReadyToRun). Every function is JIT-compiled the first time it
/// runs — e.g. ~35 ms on a level entry on a 7800X3D, far more on Steam Deck.
/// Compile everything up front on a low-priority background thread instead.
/// </summary>
public static class JitWarmup
{
    static int _started;

    /// <summary>Short status for the Dev HUD (the launcher has no visible console).</summary>
    public static string Status { get; private set; } = "off";

    public static void Start(params Assembly?[] assemblies)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        var list = assemblies.Where(a => a != null).Cast<Assembly>().ToArray();
        if (list.Length == 0) return;

        var thread = new Thread(() => Run(list))
        {
            IsBackground = true,
            Name = "JitWarmup",
            Priority = ThreadPriority.BelowNormal,
        };
        Status = "running";
        thread.Start();
    }

    static void Run(Assembly[] assemblies)
    {
        long start = Stopwatch.GetTimestamp();
        int count = 0;
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                 BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (var asm in assemblies)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.OfType<Type>().ToArray(); }

            foreach (var type in types)
            {
                if (type.ContainsGenericParameters) continue;
                foreach (var method in type.GetMethods(All))
                {
                    if (method.IsAbstract || method.ContainsGenericParameters) continue;
                    // P/Invoke stubs would try to load native libraries (Android-only, Win32-only).
                    if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0) continue;
                    if ((method.MethodImplementationFlags & MethodImplAttributes.InternalCall) != 0) continue;
                    try
                    {
                        RuntimeHelpers.PrepareMethod(method.MethodHandle);
                        count++;
                    }
                    catch { /* ignore — the method still JITs normally on first call */ }
                }
            }
        }
        double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Status = $"{count} in {ms:0} ms";
        Console.WriteLine($"[JitWarmup] prepared {count} methods in {ms:0} ms");
    }
}
