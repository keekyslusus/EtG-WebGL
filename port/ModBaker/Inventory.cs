using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace EtgModBaker;

/// <summary>Lists what the mod assemblies use from assemblies that don't exist in the web build.</summary>
internal static class Inventory
{
    public static int Run(IEnumerable<string> dlls, string[] interesting, bool showMethods = false)
    {
        foreach (string path in dlls)
        {
            var module = ModuleDefinition.ReadModule(path);
            var uses = new Dictionary<string, int>();
            MethodDefinition current = null;
            void Note(MemberReference m, string scope)
            {
                if (!interesting.Any(i => scope.Equals(i, StringComparison.OrdinalIgnoreCase) || !showMethods && scope.StartsWith(i, StringComparison.OrdinalIgnoreCase))) return;
                string key = $"[{scope}] {m.FullName}" + (showMethods && current != null ? "   in " + current.FullName : "");
                uses[key] = uses.TryGetValue(key, out int n) ? n + 1 : 1;
            }
            foreach (var type in module.GetTypes())
            {
                if (type.BaseType != null) Note(type.BaseType, ScopeOf(type.BaseType));
                foreach (var ca in type.CustomAttributes) Note(ca.AttributeType, ScopeOf(ca.AttributeType));
                foreach (var method in type.Methods)
                {
                    foreach (var ca in method.CustomAttributes) Note(ca.AttributeType, ScopeOf(ca.AttributeType));
                    if (!method.HasBody) continue;
                    current = method;
                    foreach (var ins in method.Body.Instructions)
                    {
                        if (ins.Operand is MemberReference mr && !(mr is TypeDefinition || mr is MethodDefinition || mr is FieldDefinition))
                        {
                            string scope = mr is TypeReference tr ? ScopeOf(tr) : ScopeOf(mr.DeclaringType);
                            Note(mr, scope);
                        }
                    }
                }
            }
            Console.WriteLine($"== {Path.GetFileName(path)}  refs: {string.Join(", ", module.AssemblyReferences.Select(a => a.Name))}");
            foreach (var kv in uses.OrderBy(k => k.Key)) Console.WriteLine($"  {kv.Value,4} {kv.Key}");
        }
        return 0;
    }

    private static string ScopeOf(TypeReference t)
    {
        while (t is TypeSpecification ts) t = ts.ElementType;
        return t?.Scope switch
        {
            AssemblyNameReference a => a.Name,
            ModuleDefinition m => m.Assembly.Name.Name,
            _ => "?",
        };
    }
}
