using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace EtgModBaker;

/// <summary>
/// Mono runs unverifiable IL that passes one Unity component where a method expects another (Alexandria's
/// PlayerController.Update manipulator hands `this` to CheckIfCustomDodgeRollDodgesProjectiles(bool,
/// SpeculativeRigidbody), which only uses its .gameObject). IL2CPP's typed C++ doesn't compile that.
/// Such an argument - the last one, loaded right before the call - becomes .GetComponent&lt;Expected&gt;():
/// the same GameObject, so the same result as on Mono. Other mismatches are reported.
/// </summary>
internal sealed class ArgumentFixer
{
    private readonly CecilWorld _world;
    private readonly List<string> _problems;

    public ArgumentFixer(CecilWorld world, List<string> problems)
    {
        _world = world;
        _problems = problems;
    }

    public void Fix(MethodDefinition method)
    {
        var body = method.Body;
        var il = body.GetILProcessor();
        foreach (var call in body.Instructions.ToList())
        {
            if (!(call.OpCode == OpCodes.Call || call.OpCode == OpCodes.Callvirt) || !(call.Operand is MethodReference target) || target.Parameters.Count == 0)
            {
                continue;
            }
            var load = call.Previous;
            var argType = LoadedType(load, method);
            var paramType = target.Parameters[target.Parameters.Count - 1].ParameterType;
            if (argType == null || paramType.IsValueType || paramType.IsGenericParameter || argType.IsValueType || paramType is TypeSpecification || argType is TypeSpecification)
            {
                continue;
            }
            var argDef = argType.Resolve();
            var paramDef = paramType.Resolve();
            if (argDef == null || paramDef == null || IsAssignable(argDef, paramDef))
            {
                continue;
            }
            string where = $"{method.FullName}: {argType.FullName} passed as {paramType.FullName} to {target.Name}";
            if (!Inherits(argDef, "UnityEngine.Component") || !Inherits(paramDef, "UnityEngine.Component"))
            {
                _problems.Add("unverifiable argument (IL2CPP won't compile it): " + where);
                continue;
            }
            var component = _world.Module("UnityEngine.CoreModule").GetType("UnityEngine.Component");
            var getComponent = component.Methods.Single(m => m.Name == "GetComponent" && m.HasGenericParameters && m.Parameters.Count == 0);
            var instance = new GenericInstanceMethod(new Importer(_world, method).Method(getComponent));
            instance.GenericArguments.Add(paramType);
            il.InsertBefore(call, il.Create(OpCodes.Callvirt, instance));
            System.Console.WriteLine("   argument fixed: " + where + " -> GetComponent<" + paramType.Name + ">()");
        }
        body.OptimizeMacros();
    }

    private static TypeReference LoadedType(Instruction ins, MethodDefinition method)
    {
        if (ins == null) return null;
        int arg = -1;
        switch (ins.OpCode.Code)
        {
            case Code.Ldarg_0: arg = 0; break;
            case Code.Ldarg_1: arg = 1; break;
            case Code.Ldarg_2: arg = 2; break;
            case Code.Ldarg_3: arg = 3; break;
            case Code.Ldarg:
            case Code.Ldarg_S:
                var p = (ParameterDefinition)ins.Operand;
                return p == method.Body.ThisParameter ? method.DeclaringType : p.ParameterType;
            case Code.Ldloc:
            case Code.Ldloc_S:
                return ((VariableDefinition)ins.Operand).VariableType;
            case Code.Ldloc_0: return method.Body.Variables.Count > 0 ? method.Body.Variables[0].VariableType : null;
            case Code.Ldloc_1: return method.Body.Variables.Count > 1 ? method.Body.Variables[1].VariableType : null;
            case Code.Ldloc_2: return method.Body.Variables.Count > 2 ? method.Body.Variables[2].VariableType : null;
            case Code.Ldloc_3: return method.Body.Variables.Count > 3 ? method.Body.Variables[3].VariableType : null;
            default: return null;
        }
        if (method.HasThis)
        {
            if (arg == 0) return method.DeclaringType;
            arg--;
        }
        return arg < method.Parameters.Count ? method.Parameters[arg].ParameterType : null;
    }

    private static bool IsAssignable(TypeDefinition from, TypeDefinition to)
    {
        for (var t = from; t != null; t = t.BaseType?.Resolve())
        {
            if (t.FullName == to.FullName) return true;
            if (t.Interfaces.Any(i => i.InterfaceType.FullName == to.FullName)) return true;
        }
        return to.FullName == "System.Object";
    }

    private static bool Inherits(TypeDefinition type, string baseName)
    {
        for (var t = type; t != null; t = t.BaseType?.Resolve())
        {
            if (t.FullName == baseName) return true;
        }
        return false;
    }
}
