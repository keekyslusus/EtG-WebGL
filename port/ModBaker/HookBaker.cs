using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace EtgModBaker;

/// <summary>
/// MonoMod.RuntimeDetour `new Hook(from, to[, target])` calls -> static trampolines. `from`/`to` are recovered
/// by evaluating the reflection code in front of the constructor (typeof(X).GetMethod("Name", flags), also
/// through locals); the original body moves to a static `Name_orig_bakedN(self, args...)`.
///
/// Mods create and dispose hooks while the game runs (console commands, items picked up and dropped), so a
/// trampoline doesn't call the hook unconditionally: every (from, to) pair gets a slot, a static field
/// `ModBaker.BakedHooks::slotN` (a `MonoMod.RuntimeDetour.BakedHook` of the facade) in `to`'s assembly, and
/// the target's body becomes
///     if (slot.Applied != 0) return to([(T)slot.TargetAs(typeof(T)),] [new OrigDelegate(Name_orig_bakedN),] self, args...);
///     return Name_orig_bakedN(self, args...);
/// Every constructor call is rewritten to the facade's overload that takes the slot: the Hook object turns
/// it on when it is applied (the constructor, Apply) and off on Undo/Dispose, as the real detour would be.
/// Two pairs on the same method chain like MonoMod detours, in bake order.
/// </summary>
internal sealed class HookBaker
{
    private const string HookType = "MonoMod.RuntimeDetour.Hook";
    private const string SlotType = "MonoMod.RuntimeDetour.BakedHook";

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
    private sealed class Unknown
    {
        public static readonly Unknown I = new();
        public override string ToString() => "?";
    }

    private sealed record Site(string Where, MethodDefinition Caller, Instruction Newobj, MethodDefinition From, MethodDefinition To, bool HasTarget);

    public void Bake(IEnumerable<ModuleDefinition> modules)
    {
        var sites = new List<Site>();
        foreach (var module in modules)
        {
            foreach (var type in module.GetTypes())
            {
                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    Evaluate(module, method, sites);
                }
            }
        }

        // One slot and one trampoline per (from, to) pair, in discovery order.
        var slots = new Dictionary<(MethodDefinition, MethodDefinition), FieldDefinition>();
        foreach (var site in sites)
        {
            var key = (site.From, site.To);
            if (!slots.TryGetValue(key, out var slot))
            {
                try
                {
                    slot = AddSlot(site.To, site.From);
                    Apply(site.From, site.To, slot);
                    Console.WriteLine($"   {site.Where}: {site.From.FullName} -> {site.To.FullName}");
                }
                catch (Exception e)
                {
                    _problems.Add($"{site.Where}: {site.From.FullName} -> {site.To.FullName}: {Baker.Flatten(e)}");
                    slot = null;
                }
                slots[key] = slot;
            }
            if (slot != null)
            {
                RewriteSite(site, slot);
            }
        }
    }

    // ---- Evaluation: just enough of a stack machine for typeof(X).GetMethod(...) chains ----

    private void Evaluate(ModuleDefinition module, MethodDefinition method, List<Site> sites)
    {
        var body = method.Body;
        var targets = new HashSet<Instruction>();
        foreach (var ins in body.Instructions)
        {
            if (ins.Operand is Instruction t) targets.Add(t);
            if (ins.Operand is Instruction[] ts) targets.UnionWith(ts);
        }
        var handlerStarts = new HashSet<Instruction>(body.ExceptionHandlers.Select(h => h.HandlerStart).Where(h => h != null));
        targets.UnionWith(body.ExceptionHandlers.Select(h => h.FilterStart).Where(h => h != null));

        var stack = new List<object>();
        var locals = new Dictionary<int, object>();
        foreach (var ins in body.Instructions)
        {
            if (handlerStarts.Contains(ins))
            {
                stack.Clear();
                stack.Add(Unknown.I); // the exception
            }
            else if (targets.Contains(ins))
            {
                stack.Clear(); // what's on the stack here depends on the path taken
            }

            if (ins.OpCode == OpCodes.Newobj && ins.Operand is MethodReference ctor && ctor.DeclaringType.FullName == HookType)
            {
                string where = $"{module.Assembly.Name.Name}: Hook in {method.FullName}";
                int argc = ctor.Parameters.Count;
                var args = stack.Count >= argc ? stack.Skip(stack.Count - argc).ToList() : null;
                string sig = string.Join(",", ctor.Parameters.Select(p => p.ParameterType.Name));
                if (sig != "MethodBase,MethodInfo" && sig != "MethodBase,MethodInfo,Object")
                {
                    _problems.Add($"{where}: unsupported constructor Hook({sig})");
                }
                else if (args == null || !(args[0] is MethodVal from) || !(args[1] is MethodVal to))
                {
                    string seen = args == null ? $"{stack.Count} known values" : string.Join(", ", args.Select(Describe));
                    _problems.Add($"{where}: can't evaluate the hook's arguments ({seen})");
                }
                else
                {
                    sites.Add(new Site(where, method, ins, from.Method, to.Method, argc == 3));
                }
            }
            Step(ins, stack, locals, method);
        }
    }

    private static string Describe(object v) => v switch
    {
        null => "null",
        MethodVal m => m.Method.FullName,
        TypeVal t => "typeof(" + t.Type.FullName + ")",
        _ => v.ToString(),
    };

    private void Step(Instruction ins, List<object> stack, Dictionary<int, object> locals, MethodDefinition method)
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
            case Code.Stelem_Ref when stack.Count >= 3 && stack[stack.Count - 3] is ArrayVal arr && stack[stack.Count - 2] is int idx && idx < arr.Items.Count:
            {
                var v = Pop(); Pop(); Pop();
                arr.Items[idx] = v;
                return;
            }
        }
        int? local = LocalIndex(ins, method);
        if (local != null)
        {
            if (ins.OpCode.StackBehaviourPop == StackBehaviour.Pop1)
            {
                locals[local.Value] = stack.Count > 0 ? Pop() : Unknown.I; // stloc
            }
            else
            {
                stack.Add(ins.OpCode.Code is Code.Ldloca or Code.Ldloca_S ? Unknown.I : locals.TryGetValue(local.Value, out var v) ? v : Unknown.I);
            }
            return;
        }
        if (code >= Code.Ldc_I4_M1 && code <= Code.Ldc_I4_8)
        {
            stack.Add(code - Code.Ldc_I4_0);
            return;
        }
        if (code == Code.Ldc_I4 || code == Code.Ldc_I4_S)
        {
            stack.Add(Convert.ToInt32(ins.Operand));
            return;
        }
        if ((code == Code.Call || code == Code.Callvirt) && ins.Operand is MethodReference m)
        {
            string name = m.DeclaringType.FullName + "::" + m.Name;
            int argc = m.Parameters.Count + (m.HasThis ? 1 : 0);
            if (stack.Count >= argc)
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
                if (m.ReturnType.MetadataType != MetadataType.Void) stack.Add(result);
                return;
            }
            stack.Clear();
            return;
        }
        // Anything else: the right number of unknowns.
        int pops = Pops(ins);
        if (pops < 0 || pops > stack.Count)
        {
            stack.Clear();
        }
        else
        {
            stack.RemoveRange(stack.Count - pops, pops);
        }
        int pushes = Pushes(ins);
        for (int i = 0; i < pushes; i++) stack.Add(Unknown.I);
        if (ins.OpCode.FlowControl is FlowControl.Branch or FlowControl.Return or FlowControl.Throw)
        {
            stack.Clear();
        }
    }

    private static int? LocalIndex(Instruction ins, MethodDefinition method) => ins.OpCode.Code switch
    {
        Code.Ldloc_0 or Code.Stloc_0 => 0,
        Code.Ldloc_1 or Code.Stloc_1 => 1,
        Code.Ldloc_2 or Code.Stloc_2 => 2,
        Code.Ldloc_3 or Code.Stloc_3 => 3,
        Code.Ldloc or Code.Ldloc_S or Code.Stloc or Code.Stloc_S or Code.Ldloca or Code.Ldloca_S => ((VariableDefinition)ins.Operand).Index,
        _ => null,
    };

    // -1: unknown (variable).
    private static int Pops(Instruction ins)
    {
        if (ins.Operand is MethodReference m && (ins.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj))
        {
            return m.Parameters.Count + (m.HasThis && ins.OpCode.Code != Code.Newobj ? 1 : 0);
        }
        return ins.OpCode.StackBehaviourPop switch
        {
            StackBehaviour.Pop0 => 0,
            StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
            StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8
                or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
            StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8
                or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3,
            _ => -1,
        };
    }

    private static int Pushes(Instruction ins)
    {
        if (ins.Operand is MethodReference m && (ins.OpCode.Code is Code.Call or Code.Callvirt))
        {
            return m.ReturnType.MetadataType == MetadataType.Void ? 0 : 1;
        }
        return ins.OpCode.StackBehaviourPush switch
        {
            StackBehaviour.Push0 => 0,
            StackBehaviour.Push1_push1 => 2,
            _ => 1,
        };
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

    // ---- Baking ----

    // public static class ModBaker.BakedHooks { public static readonly BakedHook slotN = new BakedHook(); }
    private FieldDefinition AddSlot(MethodDefinition hook, MethodDefinition target)
    {
        var module = hook.Module;
        var holder = module.GetType("ModBaker", "BakedHooks");
        var slotType = new Importer(_world, module).Type(_world.FacadeType(SlotType));
        if (holder == null)
        {
            holder = new TypeDefinition("ModBaker", "BakedHooks",
                TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, module.TypeSystem.Object);
            module.Types.Add(holder);
            var cctor = new MethodDefinition(".cctor", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig
                | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
            cctor.Body.GetILProcessor().Emit(OpCodes.Ret);
            holder.Methods.Add(cctor);
        }
        var field = new FieldDefinition($"slot{holder.Fields.Count}_{target.DeclaringType.Name}_{target.Name}",
            FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly, slotType);
        holder.Fields.Add(field);
        var slotCtor = new MethodReference(".ctor", module.TypeSystem.Void, slotType) { HasThis = true };
        var init = holder.Methods.Single(m => m.IsConstructor && m.IsStatic).Body;
        var ret = init.Instructions.Last();
        var il = init.GetILProcessor();
        il.InsertBefore(ret, il.Create(OpCodes.Newobj, slotCtor));
        il.InsertBefore(ret, il.Create(OpCodes.Stsfld, field));
        _world.MarkModified(module);
        return field;
    }

    // newobj Hook(from, to[, target]) -> ldsfld slot; newobj Hook(from, to[, target], slot)
    private void RewriteSite(Site site, FieldDefinition slot)
    {
        var module = site.Caller.Module;
        var imp = new Importer(_world, site.Caller);
        var old = (MethodReference)site.Newobj.Operand;
        var hookType = imp.Type(_world.FacadeType(HookType));
        var ctor = new MethodReference(".ctor", module.TypeSystem.Void, hookType) { HasThis = true };
        foreach (var p in old.Parameters) ctor.Parameters.Add(new ParameterDefinition(imp.Type(p.ParameterType)));
        ctor.Parameters.Add(new ParameterDefinition(imp.Type(slot.FieldType)));
        site.Caller.Body.SimplifyMacros(); // an inserted instruction can push a short branch out of range
        var il = site.Caller.Body.GetILProcessor();
        var load = il.Create(OpCodes.Ldsfld, imp.Field(slot));
        il.InsertBefore(site.Newobj, load);
        // Branches to the constructor call now land on the slot load (the arguments are already pushed).
        foreach (var ins in site.Caller.Body.Instructions)
        {
            if (ins.Operand == site.Newobj) ins.Operand = load;
            if (ins.Operand is Instruction[] ts) ins.Operand = ts.Select(t => t == site.Newobj ? load : t).ToArray();
        }
        site.Newobj.Operand = ctor;
        site.Caller.Body.OptimizeMacros();
        _world.MarkModified(module);
    }

    private void Apply(MethodDefinition target, MethodDefinition hook, FieldDefinition slot)
    {
        if (!_world.IsWritable(target.Module.Assembly.Name.Name))
        {
            // Engine/BCL method: hook the wrapper every call to it is redirected to (Redirector).
            target = Redirector.WrapperFor(_world, target, hook.Module);
        }
        if (target.HasGenericParameters || target.DeclaringType.HasGenericParameters || target.DeclaringType.IsValueType)
        {
            throw new NotSupportedException("generic or struct hook targets");
        }
        var module = target.Module;
        int argc = target.Parameters.Count + (target.HasThis ? 1 : 0);
        // MonoMod lets a hook leave out the orig delegate: it then replaces the method outright.
        bool withOrig = hook.Parameters.Count == argc + 1;
        if (!withOrig && hook.Parameters.Count != argc)
        {
            throw new InvalidOperationException($"hook signature doesn't match the target ({hook.Parameters.Count} hook parameters for {argc} [+ orig])");
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

        // Target body -> the hook (with a delegate to the clone) while the slot is applied, else the clone.
        var imp = new Importer(_world, target);
        var slotRef = imp.Field(slot);
        var slotDef = _world.FacadeType(SlotType);
        var applied = imp.Field(slotDef.Fields.Single(f => f.Name == "Applied"));

        var body = new MethodBody(target);
        var il = body.GetILProcessor();
        var callOriginal = il.Create(OpCodes.Nop);
        il.Emit(OpCodes.Ldsfld, slotRef);
        il.Emit(OpCodes.Ldfld, applied);
        il.Emit(OpCodes.Brfalse, callOriginal);
        if (hook.HasThis)
        {
            // MonoMod calls an instance hook on the Hook's target object; mods pass a Type there when the hook
            // doesn't use `this`. IL2CPP null-checks `this` even on a non-virtual call: BakedHook.TargetAs gives the
            // target if it is of the hook's class, else a stand-in instance of it.
            var hookType = imp.Type(hook.DeclaringType);
            il.Emit(OpCodes.Ldsfld, slotRef);
            il.Emit(OpCodes.Ldtoken, hookType);
            il.Emit(OpCodes.Call, imp.Method(_world.FindType(new TypeReference("System", "Type", module, module.TypeSystem.CoreLibrary))
                .Methods.Single(m => m.Name == "GetTypeFromHandle")));
            il.Emit(OpCodes.Callvirt, imp.Method(slotDef.Methods.Single(m => m.Name == "TargetAs")));
            il.Emit(OpCodes.Castclass, hookType);
        }
        if (withOrig)
        {
            var delegateType = imp.Type(hook.Parameters[0].ParameterType);
            var delegateCtor = new MethodReference(".ctor", module.TypeSystem.Void, delegateType) { HasThis = true };
            delegateCtor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
            delegateCtor.Parameters.Add(new ParameterDefinition(module.TypeSystem.IntPtr));
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldftn, clone);
            il.Emit(OpCodes.Newobj, delegateCtor);
        }
        if (target.HasThis) il.Emit(OpCodes.Ldarg, body.ThisParameter);
        foreach (var p in target.Parameters) il.Emit(OpCodes.Ldarg, p);
        il.Emit(OpCodes.Call, imp.Method(hook));
        il.Emit(OpCodes.Ret);
        il.Append(callOriginal);
        if (target.HasThis) il.Emit(OpCodes.Ldarg, body.ThisParameter);
        foreach (var p in target.Parameters) il.Emit(OpCodes.Ldarg, p);
        il.Emit(OpCodes.Call, clone);
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
