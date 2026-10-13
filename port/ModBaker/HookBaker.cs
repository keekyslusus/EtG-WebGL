using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace EtgModBaker;

/// <summary>
/// MonoMod.RuntimeDetour `new Hook(from, to)` calls -> static trampolines. `from`/`to` are recovered by
/// evaluating the reflection code in front of the constructor (typeof(X).GetMethod("Name", flags));
/// the original body moves to a static `Name_orig_bakedN(self, args...)` and the target's body becomes
/// `return to(new OrigDelegate(Name_orig_bakedN), self, args...)`. At runtime the Hook facade is a no-op.
/// </summary>
internal sealed class HookBaker
{
    private readonly CecilWorld _world;
    private readonly List<string> _problems;
    private static int _counter;

    public HookBaker(CecilWorld world, List<string> problems)
    {
        _world = world;
        _problems = problems;
    }

    // Values on the evaluation stack.
    private sealed record TypeVal(TypeDefinition Type);
    private sealed record MethodVal(MethodDefinition Method);
    private sealed record ArrayVal(List<object> Items);
    private sealed class Unknown { public static readonly Unknown I = new(); }

    public void Bake(ModuleDefinition module)
    {
        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                var stack = new List<object>();
                foreach (var ins in method.Body.Instructions)
                {
                    if (ins.OpCode == OpCodes.Newobj && ins.Operand is MethodReference ctor && ctor.DeclaringType.FullName == "MonoMod.RuntimeDetour.Hook")
                    {
                        string where = $"{module.Assembly.Name.Name}: Hook in {method.FullName}";
                        int argc = ctor.Parameters.Count;
                        var args = stack.Count >= argc ? stack.Skip(stack.Count - argc).ToList() : null;
                        if (args == null || !(args[0] is MethodVal from) || !(args[1] is MethodVal to) || argc > 2 && !(argc == 3 && args[2] == null))
                        {
                            _problems.Add($"{where}: can't evaluate the hook's arguments ({ctor.FullName})");
                        }
                        else
                        {
                            try
                            {
                                Apply(from.Method, to.Method);
                                Console.WriteLine($"   {where}: {from.Method.FullName} -> {to.Method.FullName}");
                            }
                            catch (Exception e)
                            {
                                _problems.Add($"{where}: {Baker.Flatten(e)}");
                            }
                        }
                        stack.Clear();
                        continue;
                    }
                    Step(ins, stack);
                }
            }
        }
    }

    // Just enough of a stack machine for typeof(X).GetMethod(...) chains; anything else resets it.
    private void Step(Instruction ins, List<object> stack)
    {
        object Pop() { var v = stack.Last(); stack.RemoveAt(stack.Count - 1); return v; }
        var code = ins.OpCode.Code;
        switch (code)
        {
            case Code.Ldtoken when ins.Operand is TypeReference tr:
                stack.Add(new TypeVal(_world.FindType(tr)));
                return;
            case Code.Ldstr:
                stack.Add(ins.Operand);
                return;
            case Code.Ldnull:
                stack.Add(null);
                return;
            case Code.Castclass:
            case Code.Isinst:
            case Code.Nop:
                return;
            case Code.Dup when stack.Count > 0:
                stack.Add(stack.Last());
                return;
            case Code.Newarr when stack.Count > 0 && stack.Last() is int n:
                Pop();
                stack.Add(new ArrayVal(Enumerable.Repeat<object>(null, n).ToList()));
                return;
            case Code.Stelem_Ref when stack.Count >= 3 && stack[stack.Count - 3] is ArrayVal arr && stack[stack.Count - 2] is int idx:
            {
                var v = Pop(); Pop(); Pop();
                arr.Items[idx] = v;
                return;
            }
        }
        if (ins.OpCode.OperandType == OperandType.InlineI || ins.OpCode.OperandType == OperandType.ShortInlineI || code >= Code.Ldc_I4_M1 && code <= Code.Ldc_I4_8)
        {
            if (code >= Code.Ldc_I4_M1 && code <= Code.Ldc_I4_8)
            {
                stack.Add(code - Code.Ldc_I4_0);
            }
            else
            {
                stack.Add(Convert.ToInt32(ins.Operand));
            }
            return;
        }
        if ((code == Code.Call || code == Code.Callvirt) && ins.Operand is MethodReference m)
        {
            string name = m.DeclaringType.FullName + "::" + m.Name;
            int argc = m.Parameters.Count + (m.HasThis ? 1 : 0);
            if (stack.Count >= argc && stack.Skip(stack.Count - argc).All(v => !(v is Unknown)))
            {
                var args = stack.Skip(stack.Count - argc).ToList();
                stack.RemoveRange(stack.Count - argc, argc);
                object result = Unknown.I;
                if (name == "System.Type::GetTypeFromHandle")
                {
                    result = args[0];
                }
                else if (name == "System.Type::GetMethod" && args[0] is TypeVal t && args[1] is string mname)
                {
                    int flags = m.Parameters.Count >= 2 && m.Parameters[1].ParameterType.Name == "BindingFlags" ? (int)args[2] : 16 | 4 | 8; // Public|Instance|Static
                    var types = args.OfType<ArrayVal>().FirstOrDefault();
                    result = new MethodVal(FindMethod(t.Type, mname, flags, types));
                }
                else if (name == "HarmonyLib.AccessTools::Method" && args[0] is TypeVal at && args[1] is string aname)
                {
                    result = new MethodVal(FindMethod(at.Type, aname, 16 | 32 | 4 | 8, args.Count > 2 ? args[2] as ArrayVal : null));
                }
                stack.Add(result);
                return;
            }
        }
        stack.Clear();
        stack.Add(Unknown.I);
    }

    private MethodDefinition FindMethod(TypeDefinition type, string name, int flags, ArrayVal paramTypes)
    {
        bool pub = (flags & 16) != 0, nonPub = (flags & 32) != 0, inst = (flags & 4) != 0, stat = (flags & 8) != 0, declaredOnly = (flags & 2) != 0;
        var found = new List<MethodDefinition>();
        for (var t = type; t != null; t = declaredOnly ? null : t.BaseType == null ? null : _world.FindType(t.BaseType))
        {
            found.AddRange(t.Methods.Where(md => md.Name == name
                && (md.IsPublic ? pub : nonPub)
                && (md.IsStatic ? stat : inst)
                && (paramTypes == null || md.Parameters.Select(p => p.ParameterType.FullName)
                    .SequenceEqual(paramTypes.Items.Select(i => (i as TypeVal)?.Type.FullName)))));
            if (found.Count > 0) break;
        }
        if (found.Count != 1) throw new InvalidOperationException($"{type.FullName}.GetMethod(\"{name}\") matches {found.Count} methods");
        return found[0];
    }

    private void Apply(MethodDefinition target, MethodDefinition hook)
    {
        if (target.HasGenericParameters || target.DeclaringType.HasGenericParameters || target.DeclaringType.IsValueType)
        {
            throw new NotSupportedException("generic or struct hook targets");
        }
        var module = target.Module;
        int extra = target.HasThis ? 1 : 0;
        if (hook.Parameters.Count != target.Parameters.Count + extra + 1)
        {
            throw new InvalidOperationException("hook signature doesn't match the target");
        }

        // Original body -> static clone (self first).
        var clone = new MethodDefinition($"{target.Name}_orig_baked{_counter++}", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, target.ReturnType);
        var argMap = new Dictionary<ParameterDefinition, ParameterDefinition>();
        if (target.HasThis)
        {
            var self = new ParameterDefinition("self", ParameterAttributes.None, target.DeclaringType);
            clone.Parameters.Add(self);
            argMap[target.Body.ThisParameter] = self;
        }
        foreach (var p in target.Parameters)
        {
            var np = new ParameterDefinition(p.Name, p.Attributes, p.ParameterType);
            clone.Parameters.Add(np);
            argMap[p] = np;
        }
        MoveBody(target, clone, argMap);
        target.DeclaringType.Methods.Add(clone);

        // Target body -> call the hook with a delegate to the clone.
        var imp = new Importer(_world, target);
        var delegateType = imp.Type(hook.Parameters[0].ParameterType);
        var delegateCtor = new MethodReference(".ctor", module.TypeSystem.Void, delegateType) { HasThis = true };
        delegateCtor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
        delegateCtor.Parameters.Add(new ParameterDefinition(module.TypeSystem.IntPtr));

        var body = new MethodBody(target);
        var il = body.GetILProcessor();
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldftn, clone);
        il.Emit(OpCodes.Newobj, delegateCtor);
        if (target.HasThis) il.Emit(OpCodes.Ldarg, body.ThisParameter);
        foreach (var p in target.Parameters) il.Emit(OpCodes.Ldarg, p);
        il.Emit(OpCodes.Call, imp.Method(hook));
        il.Emit(OpCodes.Ret);
        target.Body = body;
        body.OptimizeMacros();
        _world.MarkModified(module);
    }

    private static void MoveBody(MethodDefinition from, MethodDefinition to, Dictionary<ParameterDefinition, ParameterDefinition> argMap)
    {
        var src = from.Body;
        src.SimplifyMacros();
        var body = new MethodBody(to) { InitLocals = src.InitLocals };
        var varMap = new Dictionary<VariableDefinition, VariableDefinition>();
        foreach (var v in src.Variables)
        {
            var nv = new VariableDefinition(v.VariableType);
            varMap[v] = nv;
            body.Variables.Add(nv);
        }
        var insMap = new Dictionary<Instruction, Instruction>();
        foreach (var ins in src.Instructions)
        {
            var ni = Instruction.Create(OpCodes.Nop);
            ni.OpCode = ins.OpCode;
            insMap[ins] = ni;
            body.Instructions.Add(ni);
        }
        foreach (var ins in src.Instructions)
        {
            insMap[ins].Operand = ins.Operand switch
            {
                Instruction t => insMap[t],
                Instruction[] ts => ts.Select(t => insMap[t]).ToArray(),
                VariableDefinition v => varMap[v],
                ParameterDefinition p => argMap[p],
                var o => o,
            };
        }
        foreach (var eh in src.ExceptionHandlers)
        {
            body.ExceptionHandlers.Add(new ExceptionHandler(eh.HandlerType)
            {
                TryStart = eh.TryStart == null ? null : insMap[eh.TryStart],
                TryEnd = eh.TryEnd == null ? null : insMap[eh.TryEnd],
                HandlerStart = eh.HandlerStart == null ? null : insMap[eh.HandlerStart],
                HandlerEnd = eh.HandlerEnd == null ? null : insMap[eh.HandlerEnd],
                FilterStart = eh.FilterStart == null ? null : insMap[eh.FilterStart],
                CatchType = eh.CatchType,
            });
        }
        to.Body = body;
        body.OptimizeMacros();
    }
}
