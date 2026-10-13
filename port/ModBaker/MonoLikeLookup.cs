using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace EtgModBaker;

/// <summary>
/// HarmonyX finds patch targets with Type.GetMethod(name, flags, binder, types, modifiers). Mono (the
/// game) ignores generic method definitions there, .NET Framework (this process) reports an ambiguous
/// match when e.g. BraveResources has both Load(string, string) and Load&lt;T&gt;(string, string).
/// </summary>
internal static class MonoLikeLookup
{
    public static void Install()
    {
        var target = AccessTools.Method(typeof(AccessTools), nameof(AccessTools.DeclaredMethod), new[] { typeof(Type), typeof(string), typeof(Type[]), typeof(Type[]) });
        new Harmony("modbaker.monolike").Patch(target, finalizer: new HarmonyMethod(typeof(MonoLikeLookup), nameof(Finalizer)));
    }

    private static Exception Finalizer(Exception __exception, Type type, string name, Type[] parameters, ref MethodInfo __result)
    {
        if (__exception is AmbiguousMatchException && parameters != null)
        {
            var exact = type.GetMethods(AccessTools.allDeclared)
                .Where(m => m.Name == name && !m.IsGenericMethodDefinition && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters))
                .ToList();
            if (exact.Count == 1)
            {
                __result = exact[0];
                return null;
            }
        }
        return __exception;
    }
}
