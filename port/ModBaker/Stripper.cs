using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace EtgModBaker;

/// <summary>
/// Removes from the mods what only ran while patching: IL manipulators (and their lambdas / closure
/// classes) use Mono.Cecil and MonoMod.Utils, which don't exist in the web build. Everything that uses
/// those assemblies is "tainted"; a tainted member still used by untainted code is a problem.
/// </summary>
internal sealed class Stripper
{
    private static readonly HashSet<string> StrippedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mono.Cecil", "Mono.Cecil.Rocks", "MonoMod.Utils",
    };

    // Transpilers' world: CodeInstruction & co. (System.Reflection.Emit).
    private static readonly HashSet<string> StrippedHarmonyTypes = new()
    {
        "HarmonyLib.CodeInstruction", "HarmonyLib.CodeInstructionExtensions", "HarmonyLib.CodeMatcher", "HarmonyLib.CodeMatch",
        "HarmonyLib.Transpilers", "HarmonyLib.ILGenerator",
    };

    private readonly List<string> _problems;

    public Stripper(List<string> problems)
    {
        _problems = problems;
    }

    public void Strip(ModuleDefinition module)
    {
        var taintedTypes = new HashSet<TypeDefinition>();
        var taintedMethods = new HashSet<MethodDefinition>();
        var taintedFields = new HashSet<FieldDefinition>();
        var types = module.GetTypes().ToList();

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var type in types)
            {
                if (!taintedTypes.Contains(type) && (Bad(type.BaseType) || type.Interfaces.Any(i => Bad(i.InterfaceType))
                    || type.DeclaringType != null && taintedTypes.Contains(type.DeclaringType)))
                {
                    changed |= taintedTypes.Add(type);
                }
                foreach (var field in type.Fields)
                {
                    if (!taintedFields.Contains(field) && Bad(field.FieldType))
                    {
                        changed |= taintedFields.Add(field);
                        // A closure class holding e.g. an ILLabel only exists for a manipulator.
                        if (type.IsNested && type.Name.Contains("<")) changed |= taintedTypes.Add(type);
                    }
                }
                foreach (var method in type.Methods)
                {
                    if (taintedMethods.Contains(method)) continue;
                    if (taintedTypes.Contains(type) || UsesBad(method))
                    {
                        changed |= taintedMethods.Add(method);
                    }
                }
            }

            bool Bad(TypeReference t) => t != null && (IsStripped(t) || t.Resolve() is TypeDefinition d && d.Module == module && taintedTypes.Contains(d));
            bool UsesBad(MethodDefinition m)
            {
                if (Bad(m.ReturnType) || m.Parameters.Any(p => Bad(p.ParameterType))) return true;
                if (!m.HasBody) return false;
                if (m.Body.Variables.Any(v => Bad(v.VariableType))) return true;
                foreach (var ins in m.Body.Instructions)
                {
                    switch (ins.Operand)
                    {
                        case TypeReference t when Bad(t): return true;
                        case MethodReference mr when Bad(mr.DeclaringType) || mr.Resolve() is MethodDefinition md && taintedMethods.Contains(md): return true;
                        case FieldReference fr when Bad(fr.DeclaringType) || Bad(fr.FieldType) || fr.Resolve() is FieldDefinition fd && taintedFields.Contains(fd): return true;
                    }
                }
                return false;
            }
        }

        // The tainted set is closed under use: untainted code never calls a tainted member. What can
        // still reach one is the runtime itself: virtual calls, constructors, Unity messages.
        int removed = 0;
        foreach (var type in types.Where(t => !taintedTypes.Contains(t)))
        {
            foreach (var m in type.Methods.Where(taintedMethods.Contains).ToList())
            {
                if (m.IsVirtual || m.IsConstructor || UnityMessages.Contains(m.Name))
                {
                    _problems.Add($"{module.Assembly.Name.Name}: {m.FullName} needs Mono.Cecil/MonoMod.Utils at runtime");
                }
                type.Methods.Remove(m);
                removed++;
            }
            foreach (var f in type.Fields.Where(taintedFields.Contains).ToList())
            {
                type.Fields.Remove(f);
                removed++;
            }
        }
        foreach (var type in taintedTypes.Where(t => t.DeclaringType == null || !taintedTypes.Contains(t.DeclaringType)).ToList())
        {
            if (type.DeclaringType != null) type.DeclaringType.NestedTypes.Remove(type);
            else module.Types.Remove(type);
            removed++;
        }
        foreach (var r in module.AssemblyReferences.Where(a => StrippedAssemblies.Contains(a.Name)).ToList())
        {
            module.AssemblyReferences.Remove(r);
        }
        Console.WriteLine($"   {module.Assembly.Name.Name}: stripped {removed} patch-time members/types");
    }

    private static readonly HashSet<string> UnityMessages = new()
    {
        "Awake", "Start", "Update", "LateUpdate", "FixedUpdate", "OnEnable", "OnDisable", "OnDestroy", "OnGUI",
    };

    private static bool IsStripped(TypeReference t)
    {
        switch (t)
        {
            case GenericInstanceType g:
                return IsStripped(g.ElementType) || g.GenericArguments.Any(IsStripped);
            case TypeSpecification s:
                return IsStripped(s.ElementType);
            case GenericParameter:
                return false;
        }
        var outer = t;
        while (outer.DeclaringType != null) outer = outer.DeclaringType;
        return outer.Scope is AssemblyNameReference a && (StrippedAssemblies.Contains(a.Name)
            || a.Name == "0Harmony" && StrippedHarmonyTypes.Contains(outer.FullName));
    }
}
