using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using HarmonyLib.Public.Patching;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using ParameterAttributes = Mono.Cecil.ParameterAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace EtgModBaker;

/// <summary>
/// Patches on methods we can't rewrite (UnityEngine, mscorlib): the patched body goes into a static
/// wrapper `ModBaker.Redirects.Type_Method_N` in the patching mod, and every call to the original in
/// the game and mod assemblies is redirected to it. Only static targets.
///
/// In the game, Harmony changes the engine method itself, so engine code calling it is patched too.
/// The calls that matter are the generic overloads next to it: Object.Instantiate&lt;T&gt;(T, Vector3,
/// Quaternion) is `(T)Instantiate((Object)original, position, rotation)` - the game spawns projectiles
/// that way, and without Alexandria's FakePrefab postfix the clone of a mod's (inactive) projectile prefab
/// stays inactive. So every generic method of the same type calling a patched one is copied into the mod
/// (`Type_Method_GN`, body from the engine, inner calls redirected) and its callers go to the copy.
/// Instantiate&lt;T&gt;(T) uses engine internals instead; MtG API's MonoMod patch routes it through
/// Instantiate(Object), reproduced here by a hand-written generic wrapper.
///
/// The same goes for non-generic overloads of a patched method that forward to it: MtG API's finalizer on
/// Enum.Parse(Type, string, bool) resolves extended enum names (a mod character's PlayableCharacters value in the
/// save), and FullSerializer calls Enum.Parse(Type, string), which mscorlib forwards to the patched one. Such an
/// overload (same type and name, public, its body calling only public members) is copied like the generic ones.
/// </summary>
internal sealed class Redirector
{
    private static readonly Dictionary<string, MethodDefinition> Wrappers = new();
    private static readonly Dictionary<string, MethodDefinition> GenericForwarders = new();
    private static readonly Dictionary<TypeDefinition, TypeDefinition> Holders = new();
    private static int _counter;

    private readonly CecilWorld _world;

    public Redirector(CecilWorld world)
    {
        _world = world;
    }

    public static void AddWrapper(CecilWorld world, Transplanter transplant, MethodBase original)
    {
        var origDef = world.Lookup(original);
        if (!origDef.IsStatic || origDef.HasGenericParameters)
        {
            throw new NotSupportedException("only non-generic static engine/BCL methods can be redirected");
        }
        var info = Harmony.GetPatchInfo(original);
        var firstPatch = info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers).Concat(info.Transpilers).Concat(info.ILManipulators).First().PatchMethod;
        var host = world.Module(firstPatch.DeclaringType.Assembly.GetName().Name);

        var holder = host.GetType("ModBaker", "Redirects");
        if (holder == null)
        {
            holder = new TypeDefinition("ModBaker", "Redirects", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, host.TypeSystem.Object);
            host.Types.Add(holder);
        }
        if (!Holders.ContainsKey(origDef.DeclaringType))
        {
            Holders[origDef.DeclaringType] = holder;
        }
        var imp = new Importer(world, host);
        var wrapper = new MethodDefinition($"{origDef.DeclaringType.Name}_{origDef.Name}_{_counter++}", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, imp.Type(origDef.ReturnType));
        foreach (var p in origDef.Parameters)
        {
            wrapper.Parameters.Add(new ParameterDefinition(p.Name, ParameterAttributes.None, imp.Type(p.ParameterType)));
        }
        holder.Methods.Add(wrapper);

        // Harmony patches a body that just calls the original; that body then moves into the wrapper.
        var scratch = ModuleDefinition.CreateModule("ModBakerScratch", ModuleKind.Dll);
        var scratchType = new TypeDefinition("", "Scratch", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, scratch.TypeSystem.Object);
        scratch.Types.Add(scratchType);
        var src = new MethodDefinition("S", MethodAttributes.Public | MethodAttributes.Static, scratch.ImportReference(origDef.ReturnType));
        foreach (var p in origDef.Parameters)
        {
            src.Parameters.Add(new ParameterDefinition(p.Name, ParameterAttributes.None, scratch.ImportReference(p.ParameterType)));
        }
        scratchType.Methods.Add(src);
        var il = src.Body.GetILProcessor();
        foreach (var p in src.Parameters) il.Emit(OpCodes.Ldarg, p);
        il.Emit(OpCodes.Call, scratch.ImportReference(origDef));
        il.Emit(OpCodes.Ret);
        using (var ctx = new ILContext(src))
        {
            ctx.Invoke(c => HarmonyManipulator.Manipulate(original, c));
        }
        transplant.ReplaceBody(src, wrapper);

        Wrappers[Key(origDef)] = wrapper;
        if (origDef.DeclaringType.FullName == "UnityEngine.Object" && origDef.Name == "Instantiate" && origDef.Parameters.Count == 1)
        {
            var generic = origDef.DeclaringType.Methods.Single(m => m.Name == "Instantiate" && m.GenericParameters.Count == 1 && m.Parameters.Count == 1);
            GenericForwarders[Key(generic)] = AddGenericInstantiate(host, holder, wrapper, imp.Type(origDef.DeclaringType));
        }
    }

    /// <summary>
    /// The wrapper calls to an engine/BCL method go to (HookBaker: a MonoMod hook on such a method hooks the
    /// wrapper): the Harmony one if the method is patched, else a new one in `host` that just calls it.
    /// </summary>
    public static MethodDefinition WrapperFor(CecilWorld world, MethodDefinition origDef, ModuleDefinition host)
    {
        if (Wrappers.TryGetValue(Key(origDef), out var existing)) return existing;
        if (!origDef.IsStatic || origDef.HasGenericParameters)
        {
            throw new NotSupportedException("only non-generic static engine/BCL methods can be redirected");
        }
        var holder = host.GetType("ModBaker", "Redirects");
        if (holder == null)
        {
            holder = new TypeDefinition("ModBaker", "Redirects", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, host.TypeSystem.Object);
            host.Types.Add(holder);
        }
        if (!Holders.ContainsKey(origDef.DeclaringType))
        {
            Holders[origDef.DeclaringType] = holder;
        }
        var imp = new Importer(world, host);
        var wrapper = new MethodDefinition($"{origDef.DeclaringType.Name}_{origDef.Name}_{_counter++}", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, imp.Type(origDef.ReturnType));
        foreach (var p in origDef.Parameters)
        {
            wrapper.Parameters.Add(new ParameterDefinition(p.Name, ParameterAttributes.None, imp.Type(p.ParameterType)));
        }
        holder.Methods.Add(wrapper);
        var il = wrapper.Body.GetILProcessor();
        foreach (var p in wrapper.Parameters) il.Emit(OpCodes.Ldarg, p);
        il.Emit(OpCodes.Call, imp.Method(origDef));
        il.Emit(OpCodes.Ret);
        Wrappers[Key(origDef)] = wrapper;
        world.MarkModified(host);
        return wrapper;
    }

    // static T Object_Instantiate_Generic<T>(T original) where T : Object => (T)wrapper(original)
    private static MethodDefinition AddGenericInstantiate(ModuleDefinition host, TypeDefinition holder, MethodDefinition wrapper, TypeReference unityObject)
    {
        var g = new MethodDefinition("Object_Instantiate_Generic", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, host.TypeSystem.Void);
        var t = new GenericParameter("T", g);
        t.Constraints.Add(unityObject);
        g.GenericParameters.Add(t);
        g.ReturnType = t;
        g.Parameters.Add(new ParameterDefinition("original", ParameterAttributes.None, t));
        var il = g.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Box, t);
        il.Emit(OpCodes.Call, wrapper);
        il.Emit(OpCodes.Unbox_Any, t);
        il.Emit(OpCodes.Ret);
        holder.Methods.Add(g);
        return g;
    }

    /// <summary>
    /// Copies the generic engine methods and the forwarding overloads that call patched ones (see the class comment).
    /// </summary>
    public static void AddGenericForwarders(CecilWorld world, Transplanter transplant)
    {
        bool added = true;
        while (added) // a generic overload may call another generic one (Instantiate<T>(T, Transform))
        {
            added = false;
            foreach (var pair in Holders.ToList())
            {
                var engineType = pair.Key;
                var holder = pair.Value;
                var patchedNames = new HashSet<string>(engineType.Methods.Where(m => Wrappers.ContainsKey(Key(m))).Select(m => m.Name));
                foreach (var m in engineType.Methods.Where(m => m.IsStatic && m.IsPublic && m.HasBody
                             && (m.HasGenericParameters || (patchedNames.Contains(m.Name) && CallsOnlyPublic(m)))).ToList())
                {
                    if (GenericForwarders.ContainsKey(Key(m)) || Wrappers.ContainsKey(Key(m)) || !m.Body.Instructions.Any(i => i.Operand is MethodReference r && Target(r) != null))
                    {
                        continue;
                    }
                    var copy = new MethodDefinition($"{engineType.Name}_{m.Name}_G{_counter++}", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, holder.Module.TypeSystem.Void);
                    foreach (var gp in m.GenericParameters)
                    {
                        copy.GenericParameters.Add(new GenericParameter(gp.Name, copy) { Attributes = gp.Attributes });
                    }
                    holder.Methods.Add(copy);
                    var imp = new Importer(world, copy);
                    for (int i = 0; i < m.GenericParameters.Count; i++)
                    {
                        foreach (var c in m.GenericParameters[i].Constraints) copy.GenericParameters[i].Constraints.Add(imp.Type(c));
                    }
                    copy.ReturnType = imp.Type(m.ReturnType);
                    foreach (var p in m.Parameters)
                    {
                        copy.Parameters.Add(new ParameterDefinition(p.Name, ParameterAttributes.None, imp.Type(p.ParameterType)));
                    }
                    transplant.ReplaceBody(m, copy);
                    RedirectCalls(copy, imp);
                    if (m.HasGenericParameters)
                    {
                        GenericForwarders[Key(m)] = copy;
                    }
                    else
                    {
                        Wrappers[Key(m)] = copy; // Target() looks non-generic calls up there
                    }
                    Console.WriteLine($"   {(m.HasGenericParameters ? "generic" : "overload")} forwarder: {m.FullName} -> {holder.Module.Assembly.Name.Name} {copy.Name}");
                    added = true;
                }
            }
        }
    }

    // A copy of the body must not reach the engine's / mscorlib's internals from the mod.
    private static bool CallsOnlyPublic(MethodDefinition m)
    {
        foreach (var ins in m.Body.Instructions)
        {
            if (ins.Operand is MethodReference r)
            {
                var d = r.Resolve();
                if (d == null || !d.IsPublic || !d.DeclaringType.IsPublic) return false;
            }
            else if (ins.Operand is FieldReference f)
            {
                var d = f.Resolve();
                if (d == null || !d.IsPublic || !d.DeclaringType.IsPublic) return false;
            }
        }
        return true;
    }

    // The wrapper or forwarder that replaces a call to `r`, if any.
    private static MethodDefinition Target(MethodReference r)
    {
        if (r is GenericInstanceMethod gim)
        {
            return GenericForwarders.TryGetValue(Key(gim.ElementMethod), out var f) ? f : null;
        }
        return Wrappers.TryGetValue(Key(r), out var w) ? w : null;
    }

    private static int RedirectCalls(MethodDefinition method, Importer imp)
    {
        int count = 0;
        foreach (var ins in method.Body.Instructions)
        {
            if (!(ins.Operand is MethodReference r) || !(ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt || ins.OpCode == OpCodes.Ldftn)) continue;
            var target = Target(r);
            if (target == null || target == method) continue;
            MethodReference replacement;
            if (r is GenericInstanceMethod gim)
            {
                var ng = new GenericInstanceMethod(imp.Method(target));
                foreach (var arg in gim.GenericArguments) ng.GenericArguments.Add(arg);
                replacement = ng;
            }
            else
            {
                replacement = imp.Method(target);
            }
            ins.Operand = replacement;
            if (ins.OpCode != OpCodes.Ldftn) ins.OpCode = OpCodes.Call;
            count++;
        }
        return count;
    }

    private static string Key(MethodReference m)
    {
        var decl = m.DeclaringType;
        while (decl.DeclaringType != null) decl = decl.DeclaringType;
        string scope = decl.Scope switch
        {
            AssemblyNameReference a => a.Name,
            ModuleDefinition md => md.Assembly.Name.Name,
            _ => "?",
        };
        return $"{scope}|{m.DeclaringType.FullName}::{m.Name}|{Importer.Sig(m)}";
    }

    public void Run()
    {
        if (Wrappers.Count == 0) return;
        int redirected = 0;
        foreach (var module in _world.WritableModules())
        {
            var imp = new Importer(_world, module);
            int before = redirected;
            foreach (var type in module.GetTypes())
            {
                if (type.FullName == "ModBaker.Redirects") continue;
                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    redirected += RedirectCalls(method, imp);
                }
            }
            if (redirected != before) _world.MarkModified(module);
        }
        Console.WriteLine($"== Redirected {redirected} calls to {Wrappers.Count} patched engine/BCL methods and {GenericForwarders.Count} generic forwarders");
    }
}
