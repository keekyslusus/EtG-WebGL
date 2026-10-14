using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace EtgModBaker;

/// <summary>
/// Baked code calls members other assemblies don't expose: the game calls the mods' private patch
/// methods, reverse patch copies in a mod call the game's internals, IL manipulators emit calls to private
/// helpers. IL2CPP doesn't check access, but Mono does (MethodAccessException in the editor's Play Mode).
/// Making those members public would change what mods find by reflection (BindingFlags.NonPublic), so
/// each such use goes through a trampoline instead: a public static method in a public class
/// `ModBaker.Access` of the member's own assembly, which may access it.
/// </summary>
internal sealed class AccessFixer
{
    private readonly CecilWorld _world;
    private readonly List<string> _problems;
    private readonly Dictionary<string, MethodDefinition> _trampolines = new();
    private int _counter;

    public AccessFixer(CecilWorld world, List<string> problems)
    {
        _world = world;
        _problems = problems;
    }

    public void Run(IEnumerable<ModuleDefinition> modules)
    {
        int fixedCount = 0;
        foreach (var module in modules.ToList())
        {
            foreach (var type in module.GetTypes().ToList())
            {
                if (type.Namespace == "ModBaker" && type.Name == "Access") continue;
                foreach (var method in type.Methods.Where(m => m.HasBody).ToList())
                {
                    bool changed = false;
                    foreach (var ins in method.Body.Instructions)
                    {
                        try
                        {
                            changed |= Fix(ins, method);
                        }
                        catch (Exception e)
                        {
                            _problems.Add($"access: {method.FullName}: {ins}: {Baker.Flatten(e)}");
                        }
                    }
                    if (changed)
                    {
                        fixedCount++;
                        _world.MarkModified(module);
                    }
                }
            }
        }
        Console.WriteLine($"== Access: {_trampolines.Count} trampolines for {fixedCount} methods (Mono checks access, IL2CPP doesn't)");
    }

    // Replaced by port/ModFacades, where what the baked code calls is public.
    private static readonly HashSet<string> Facades = new(StringComparer.OrdinalIgnoreCase) { "0Harmony", "BepInEx", "MonoMod.RuntimeDetour" };

    private bool Fix(Instruction ins, MethodDefinition caller)
    {
        var module = caller.Module;
        if (ins.Operand is MemberReference mr && mr.DeclaringType?.Scope is AssemblyNameReference scope && Facades.Contains(scope.Name)) return false;
        switch (ins.Operand)
        {
            case MethodReference r when ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt || ins.OpCode == OpCodes.Newobj:
            {
                var def = SafeResolve(r);
                if (def == null || Accessible(def, def.DeclaringType, caller)) return false;
                if (r.DeclaringType is GenericInstanceType || def.DeclaringType.HasGenericParameters)
                {
                    throw new NotSupportedException("member of a generic type");
                }
                var t = MethodTrampoline(def, ins.OpCode == OpCodes.Newobj, ins.OpCode == OpCodes.Callvirt);
                var imp = new Importer(_world, caller);
                if (r is GenericInstanceMethod gim)
                {
                    var ng = new GenericInstanceMethod(imp.Method(t));
                    foreach (var a in gim.GenericArguments) ng.GenericArguments.Add(a);
                    ins.Operand = ng;
                }
                else
                {
                    ins.Operand = imp.Method(t);
                }
                ins.OpCode = OpCodes.Call;
                return true;
            }
            case MethodReference r when ins.OpCode == OpCodes.Ldftn || ins.OpCode == OpCodes.Ldvirtftn:
            {
                var def = SafeResolve(r);
                if (def == null || Accessible(def, def.DeclaringType, caller)) return false;
                throw new NotSupportedException("delegate to an inaccessible method");
            }
            case FieldReference f:
            {
                var def = SafeResolve(f);
                if (def == null || Accessible(def, def.DeclaringType, caller)) return false;
                if (f.DeclaringType is GenericInstanceType || def.DeclaringType.HasGenericParameters)
                {
                    throw new NotSupportedException("field of a generic type");
                }
                var code = ins.OpCode.Code;
                if (code == Code.Ldflda || code == Code.Ldsflda)
                {
                    throw new NotSupportedException("address of an inaccessible field");
                }
                var t = FieldTrampoline(def, code == Code.Stfld || code == Code.Stsfld);
                ins.Operand = new Importer(_world, caller).Method(t);
                ins.OpCode = OpCodes.Call;
                return true;
            }
        }
        return false;
    }

    private static MethodDefinition SafeResolve(MethodReference r)
    {
        try { return r.Resolve(); } catch (AssemblyResolutionException) { return null; }
    }

    private static FieldDefinition SafeResolve(FieldReference f)
    {
        try { return f.Resolve(); } catch (AssemblyResolutionException) { return null; }
    }

    // ECMA-335 accessibility, for the member and every enclosing type: public; internal / protected internal within
    // the assembly or with InternalsVisibleTo; private (and any nested type) from inside the declaring (enclosing)
    // type, nested types included; protected from a subclass. The same assembly counts too: baked code (Harmony
    // bodies moved into the game, hook trampolines) reaches the private members of other types there as well.
    private static bool Accessible(IMemberDefinition member, TypeDefinition declaring, MethodDefinition caller)
    {
        bool internals = declaring.Module == caller.Module || InternalsVisibleTo(declaring.Module, caller.Module);
        var callerType = caller.DeclaringType;
        bool Within(TypeDefinition outer)
        {
            for (var c = callerType; c != null; c = c.DeclaringType)
            {
                if (c == outer) return true;
            }
            return false;
        }
        for (var t = declaring; t != null; t = t.DeclaringType)
        {
            bool typeOk = !t.IsNested
                ? t.IsPublic || internals
                : t.IsNestedPublic
                    || (internals && (t.IsNestedAssembly || t.IsNestedFamilyOrAssembly))
                    || Within(t.DeclaringType)
                    || ((t.IsNestedFamily || t.IsNestedFamilyOrAssembly || (internals && t.IsNestedFamilyAndAssembly)) && Derives(callerType, t.DeclaringType));
            if (!typeOk) return false;
        }
        switch (member)
        {
            case MethodDefinition m:
                return m.IsPublic || (internals && (m.IsAssembly || m.IsFamilyOrAssembly)) || Within(declaring)
                    || ((m.IsFamily || m.IsFamilyOrAssembly || (internals && m.IsFamilyAndAssembly)) && Derives(callerType, declaring));
            case FieldDefinition f:
                return f.IsPublic || (internals && (f.IsAssembly || f.IsFamilyOrAssembly)) || Within(declaring)
                    || ((f.IsFamily || f.IsFamilyOrAssembly || (internals && f.IsFamilyAndAssembly)) && Derives(callerType, declaring));
        }
        return true;
    }

    private static bool Derives(TypeDefinition type, TypeDefinition baseType)
    {
        for (var t = type; t != null; t = t.DeclaringType)
        {
            for (var b = t; b != null; b = SafeBase(b))
            {
                if (b.FullName == baseType.FullName && b.Module.Assembly.Name.Name == baseType.Module.Assembly.Name.Name) return true;
            }
        }
        return false;
    }

    private static TypeDefinition SafeBase(TypeDefinition t)
    {
        try { return t.BaseType?.Resolve(); } catch (AssemblyResolutionException) { return null; }
    }

    private static bool InternalsVisibleTo(ModuleDefinition owner, ModuleDefinition caller) =>
        owner.Assembly.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.InternalsVisibleToAttribute"
            && a.ConstructorArguments.Count == 1 && ((string)a.ConstructorArguments[0].Value).Split(',')[0].Trim() == caller.Assembly.Name.Name);

    private TypeDefinition Holder(ModuleDefinition module)
    {
        if (!_world.IsWritable(module.Assembly.Name.Name))
        {
            throw new NotSupportedException("inaccessible member of an assembly that can't be changed: " + module.Assembly.Name.Name);
        }
        var holder = module.GetType("ModBaker", "Access");
        if (holder == null)
        {
            holder = new TypeDefinition("ModBaker", "Access", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, module.TypeSystem.Object);
            module.Types.Add(holder);
            _world.MarkModified(module);
        }
        return holder;
    }

    // public static R Name_N([Self self,] args) => [self.]Name(args)   (or `new Type(args)` for constructors)
    private MethodDefinition MethodTrampoline(MethodDefinition target, bool newobj, bool callvirt)
    {
        string key = $"m|{target.FullName}|{target.Module.Assembly.Name.Name}|{newobj}|{callvirt}";
        if (_trampolines.TryGetValue(key, out var existing)) return existing;
        var module = target.Module;
        var t = new MethodDefinition($"{target.DeclaringType.Name}_{(newobj ? "New" : target.Name)}_{_counter++}",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Void);
        foreach (var gp in target.GenericParameters)
        {
            t.GenericParameters.Add(new GenericParameter(gp.Name, t) { Attributes = gp.Attributes });
        }
        for (int i = 0; i < target.GenericParameters.Count; i++)
        {
            foreach (var c in target.GenericParameters[i].Constraints) t.GenericParameters[i].Constraints.Add(MapGp(c, t));
        }
        t.ReturnType = newobj ? (TypeReference)target.DeclaringType : MapGp(target.ReturnType, t);
        if (!newobj && target.HasThis)
        {
            t.Parameters.Add(new ParameterDefinition("self", ParameterAttributes.None,
                target.DeclaringType.IsValueType ? new ByReferenceType(target.DeclaringType) : (TypeReference)target.DeclaringType));
        }
        foreach (var p in target.Parameters)
        {
            t.Parameters.Add(new ParameterDefinition(p.Name, ParameterAttributes.None, MapGp(p.ParameterType, t)));
        }
        var il = t.Body.GetILProcessor();
        foreach (var p in t.Parameters) il.Emit(OpCodes.Ldarg, p);
        MethodReference call = target;
        if (target.HasGenericParameters)
        {
            var gim = new GenericInstanceMethod(target);
            foreach (var gp in t.GenericParameters) gim.GenericArguments.Add(gp);
            call = gim;
        }
        il.Emit(newobj ? OpCodes.Newobj : callvirt && target.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, call);
        il.Emit(OpCodes.Ret);
        t = Place(t, target.DeclaringType, ReachableFromAssembly(target));
        _trampolines[key] = t;
        Console.WriteLine($"   trampoline {module.Assembly.Name.Name} {t.Name} -> {target.FullName}");
        return t;
    }

    // public static T get_Field_N([Self self]) / public static void set_Field_N([Self self,] T value)
    private MethodDefinition FieldTrampoline(FieldDefinition field, bool store)
    {
        string key = $"f|{field.FullName}|{field.Module.Assembly.Name.Name}|{store}";
        if (_trampolines.TryGetValue(key, out var existing)) return existing;
        var module = field.Module;
        var t = new MethodDefinition($"{field.DeclaringType.Name}_{(store ? "set" : "get")}_{field.Name}_{_counter++}",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, store ? module.TypeSystem.Void : field.FieldType);
        if (!field.IsStatic)
        {
            t.Parameters.Add(new ParameterDefinition("self", ParameterAttributes.None,
                field.DeclaringType.IsValueType ? new ByReferenceType(field.DeclaringType) : (TypeReference)field.DeclaringType));
        }
        if (store) t.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, field.FieldType));
        var il = t.Body.GetILProcessor();
        foreach (var p in t.Parameters) il.Emit(OpCodes.Ldarg, p);
        il.Emit(field.IsStatic ? (store ? OpCodes.Stsfld : OpCodes.Ldsfld) : (store ? OpCodes.Stfld : OpCodes.Ldfld), field);
        il.Emit(OpCodes.Ret);
        t = Place(t, field.DeclaringType, ReachableFromAssembly(field));
        _trampolines[key] = t;
        Console.WriteLine($"   trampoline {module.Assembly.Name.Name} {t.Name} -> {field.FullName}");
        return t;
    }

    // Mono allows a private (or protected, private protected) member only to its own type and the types nested in it -
    // not to ModBaker.Access next to it. Such a trampoline goes into a public class `ModBakerAccess` nested in the
    // member's type, and forwarders lead out of every enclosing type that code elsewhere can't see (a private nested
    // type, e.g. a lambda's closure class) up to ModBaker.Access. Returns the method callers use.
    private MethodDefinition Place(MethodDefinition trampoline, TypeDefinition declaring, bool reachable)
    {
        if (reachable)
        {
            Holder(declaring.Module).Methods.Add(trampoline);
            return trampoline;
        }
        if (declaring.HasGenericParameters)
        {
            throw new NotSupportedException("private member of a generic type");
        }
        var current = trampoline;
        NestedHolder(declaring).Methods.Add(current);
        for (var t = declaring; t.DeclaringType != null; t = t.DeclaringType)
        {
            if (t.IsNestedPublic || t.IsNestedAssembly || t.IsNestedFamilyOrAssembly) continue;
            if (t.DeclaringType.HasGenericParameters) throw new NotSupportedException("private type nested in a generic type");
            current = Forwarder(current, NestedHolder(t.DeclaringType));
        }
        return Forwarder(current, Holder(declaring.Module));
    }

    // The member and every enclosing type can be reached from a top-level class of the same assembly.
    private static bool ReachableFromAssembly(IMemberDefinition member)
    {
        bool ok = member switch
        {
            MethodDefinition m => m.IsPublic || m.IsAssembly || m.IsFamilyOrAssembly,
            FieldDefinition f => f.IsPublic || f.IsAssembly || f.IsFamilyOrAssembly,
            _ => true,
        };
        for (var t = member.DeclaringType; ok && t.IsNested; t = t.DeclaringType)
        {
            ok = t.IsNestedPublic || t.IsNestedAssembly || t.IsNestedFamilyOrAssembly;
        }
        return ok;
    }

    private TypeDefinition NestedHolder(TypeDefinition type)
    {
        var holder = type.NestedTypes.FirstOrDefault(n => n.Name == "ModBakerAccess");
        if (holder == null)
        {
            holder = new TypeDefinition("", "ModBakerAccess", TypeAttributes.NestedPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, type.Module.TypeSystem.Object);
            type.NestedTypes.Add(holder);
            _world.MarkModified(type.Module);
        }
        return holder;
    }

    // public static R Name(args) => inner(args), with inner's generic parameters.
    private static MethodDefinition Forwarder(MethodDefinition inner, TypeDefinition holder)
    {
        var f = new MethodDefinition(inner.Name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, holder.Module.TypeSystem.Void);
        foreach (var gp in inner.GenericParameters) f.GenericParameters.Add(new GenericParameter(gp.Name, f) { Attributes = gp.Attributes });
        for (int i = 0; i < inner.GenericParameters.Count; i++)
        {
            foreach (var c in inner.GenericParameters[i].Constraints) f.GenericParameters[i].Constraints.Add(MapGp(c, f));
        }
        f.ReturnType = MapGp(inner.ReturnType, f);
        foreach (var p in inner.Parameters) f.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, MapGp(p.ParameterType, f)));
        var il = f.Body.GetILProcessor();
        foreach (var p in f.Parameters) il.Emit(OpCodes.Ldarg, p);
        MethodReference call = inner;
        if (inner.HasGenericParameters)
        {
            var gim = new GenericInstanceMethod(inner);
            foreach (var gp in f.GenericParameters) gim.GenericArguments.Add(gp);
            call = gim;
        }
        il.Emit(OpCodes.Call, call);
        il.Emit(OpCodes.Ret);
        holder.Methods.Add(f);
        return f;
    }

    // A method's own generic parameters (!!n) in its signature -> the trampoline's.
    private static TypeReference MapGp(TypeReference type, MethodDefinition owner)
    {
        switch (type)
        {
            case GenericParameter gp when gp.Type == GenericParameterType.Method:
                return owner.GenericParameters[gp.Position];
            case ByReferenceType b: return new ByReferenceType(MapGp(b.ElementType, owner));
            case ArrayType a: return new ArrayType(MapGp(a.ElementType, owner), a.Rank);
            case GenericInstanceType g:
            {
                var ng = new GenericInstanceType(g.ElementType);
                foreach (var arg in g.GenericArguments) ng.GenericArguments.Add(MapGp(arg, owner));
                return ng;
            }
            default: return type;
        }
    }
}
