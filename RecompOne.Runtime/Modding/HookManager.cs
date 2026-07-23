using System.Reflection;
using MonoMod.RuntimeDetour;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Modding;

public static class HookManager
{
    sealed class FunctionHooks
    {
        public readonly List<Func<CpuContext, IMemory, bool>> Pres = [];
        public readonly List<Action<CpuContext, IMemory>> Posts = [];
        public Action<Action<CpuContext, IMemory>, CpuContext, IMemory>? Replace;
        public ModInfo? ReplaceOwner;
        public Hook? Hook;
    }

    static readonly Dictionary<MethodInfo, FunctionHooks> _hooks = [];

    static readonly Type[] SigBasic = [typeof(CpuContext), typeof(IMemory)];
    static readonly Type[] SigOrig = [typeof(Action<CpuContext, IMemory>), typeof(CpuContext), typeof(IMemory)];
    public static int HookedFunctionCount => _hooks.Count;
    
    public static bool AddReplace(ModInfo mod, MethodInfo target, MethodInfo impl)
    {
        Action<Action<CpuContext, IMemory>, CpuContext, IMemory> replace;
        if (Matches(impl, typeof(void), SigOrig))
            replace = impl.CreateDelegate<Action<Action<CpuContext, IMemory>, CpuContext, IMemory>>();
        else if (Matches(impl, typeof(void), SigBasic))
        {
            var direct = impl.CreateDelegate<Action<CpuContext, IMemory>>();
            replace = (orig, c, m) => direct(c, m);
        }
        else
        {
            Console.Error.WriteLine($"[Mods] {mod.Id}: invalid replace signature on {Describe(impl)}");
            return false;
        }

        var hooks = Get(target);
        if (hooks.ReplaceOwner != null)
        {
            Console.Error.WriteLine($"[Mods] replace conflict on {target.Name}: {mod.Id} ignored, {hooks.ReplaceOwner.Id} already owns it");
            return false;
        }
        hooks.Replace = replace;
        hooks.ReplaceOwner = mod;
        return true;
    }
    
    public static bool AddPre(ModInfo mod, MethodInfo target, MethodInfo impl)
    {
        Func<CpuContext, IMemory, bool> pre;
        if (Matches(impl, typeof(bool), SigBasic))
            pre = impl.CreateDelegate<Func<CpuContext, IMemory, bool>>();
        else if (Matches(impl, typeof(void), SigBasic))
        {
            var direct = impl.CreateDelegate<Action<CpuContext, IMemory>>();
            pre = (c, m) => { direct(c, m); return true; };
        }
        else
        {
            Console.Error.WriteLine($"[Mods] {mod.Id}: invalid pre hook signature on {Describe(impl)}");
            return false;
        }
        Get(target).Pres.Add(pre);
        return true;
    }
    
    public static bool AddPost(ModInfo mod, MethodInfo target, MethodInfo impl)
    {
        if (!Matches(impl, typeof(void), SigBasic))
        {
            Console.Error.WriteLine($"[Mods] {mod.Id}: invalid post hook signature on {Describe(impl)}");
            return false;
        }
        Get(target).Posts.Add(impl.CreateDelegate<Action<CpuContext, IMemory>>());
        return true;
    }
    
    public static void Commit()
    {
        foreach (var (target, hooks) in _hooks)
        {
            if (hooks.Hook != null) continue;
            var state = hooks;
            hooks.Hook = new Hook(target,
                (Action<Action<CpuContext, IMemory>, CpuContext, IMemory>)((orig, c, m) => Invoke(state, orig, c, m)));
        }
    }

    static void Invoke(FunctionHooks hooks, Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        bool skip = false;
        for (int i = 0; i < hooks.Pres.Count; i++)
            if (!hooks.Pres[i](c, m)) skip = true;
        if (!skip)
        {
            if (hooks.Replace != null) hooks.Replace(orig, c, m);
            else orig(c, m);
        }
        for (int i = 0; i < hooks.Posts.Count; i++)
            hooks.Posts[i](c, m);
    }

    static FunctionHooks Get(MethodInfo target)
    {
        if (!_hooks.TryGetValue(target, out var hooks))
            _hooks[target] = hooks = new FunctionHooks();
        return hooks;
    }

    static bool Matches(MethodInfo impl, Type ret, Type[] args)
    {
        if (impl.ReturnType != ret) return false;
        var pars = impl.GetParameters();
        if (pars.Length != args.Length) return false;
        for (int i = 0; i < pars.Length; i++)
            if (pars[i].ParameterType != args[i]) return false;
        return true;
    }

    static string Describe(MethodInfo impl) => $"{impl.DeclaringType?.Name}.{impl.Name}";
}
