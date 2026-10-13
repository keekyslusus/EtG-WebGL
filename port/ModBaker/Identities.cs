using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace EtgModBaker;

/// <summary>
/// Last touches on the mods before they're written: [Harmony*] attributes go (the facade 0Harmony
/// doesn't have them, nothing reads them any more), and every assembly reference is made to name
/// exactly the assembly that ships - mods were built against 0Harmony 2.7..2.10 and a signed
/// Newtonsoft.Json 13, the web build has the facades and MtG API's unsigned Newtonsoft.Json 11.
/// </summary>
internal static class Identities
{
    public static Dictionary<string, AssemblyNameDefinition> Collect(IEnumerable<string> dlls)
    {
        var result = new Dictionary<string, AssemblyNameDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (string dll in dlls)
        {
            using var module = ModuleDefinition.ReadModule(dll);
            result[module.Assembly.Name.Name] = module.Assembly.Name;
        }
        return result;
    }

    public static void Apply(ModuleDefinition module, Dictionary<string, AssemblyNameDefinition> shipped, bool stripHarmonyAttributes)
    {
        foreach (var reference in module.AssemblyReferences)
        {
            if (shipped.TryGetValue(reference.Name, out var actual))
            {
                reference.Version = actual.Version;
                reference.PublicKeyToken = actual.PublicKeyToken ?? new byte[0];
                reference.PublicKey = null;
                reference.HasPublicKey = false;
            }
        }
        if (!stripHarmonyAttributes)
        {
            return;
        }
        foreach (var type in module.GetTypes())
        {
            Strip(type.CustomAttributes);
            foreach (var m in type.Methods)
            {
                Strip(m.CustomAttributes);
                foreach (var p in m.Parameters) Strip(p.CustomAttributes);
            }
        }
    }

    private static void Strip(Mono.Collections.Generic.Collection<CustomAttribute> attributes)
    {
        foreach (var a in attributes.Where(a => Scope(a.AttributeType) == "0Harmony").ToList())
        {
            attributes.Remove(a);
        }
    }

    private static string Scope(TypeReference t)
    {
        while (t.DeclaringType != null) t = t.DeclaringType;
        return t.Scope is AssemblyNameReference a ? a.Name : null;
    }
}
