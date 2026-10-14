using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace EtgWebPatcher;

/// <summary>
/// Assembly.GetCallingAssembly() walks the call stack. IL2CPP on WebGL has no native stack: it only knows the frames
/// of the "stack trace sentries" a development build (FullWithStacktrace) pushes in every method; a release build has
/// none, and the call returns mscorlib. Mods load their sprites that way (87 methods in Alexandria: the resources of
/// the mod that called it; Expand the Gungeon's ExtractEmbeddedResource) - in a release build they find nothing.
///
/// So the caller is passed along explicitly: every call (in any patched assembly) to a method that uses
/// GetCallingAssembly() is preceded by `WebShim.CallerAssembly.Set(typeof(&lt;calling type&gt;).TypeHandle)`, the method
/// takes the value in its first instruction (CallerAssembly.Take(), which also clears it), and each
/// GetCallingAssembly() there returns that value. Methods reached another way (a delegate, reflection) find nothing
/// to take and fall back to the real call. Methods of compiler-generated classes (lambdas, iterators) aren't called
/// by the code that names them, so they keep the real call.
/// </summary>
internal static partial class Program
{
    private const string GetCallingAssembly = "System.Reflection.Assembly System.Reflection.Assembly::GetCallingAssembly()";

    private static bool CallsGetCallingAssembly(MethodDefinition m) =>
        m.HasBody && m.Body.Instructions.Any(i => i.OpCode.Code == Code.Call && i.Operand is MethodReference r && r.FullName == GetCallingAssembly);

    private static bool CompilerGenerated(TypeDefinition t)
    {
        for (; t != null; t = t.DeclaringType)
        {
            if (t.Name.Contains('<')) return true;
        }
        return false;
    }

    /// <summary>Methods (FullName) of all the assemblies to patch whose callers must name themselves.</summary>
    private static HashSet<string> CollectCallerAssemblyUsers(IEnumerable<string> inputs, ReaderParameters readParams)
    {
        var result = new HashSet<string>();
        foreach (string input in inputs)
        {
            using var asm = AssemblyDefinition.ReadAssembly(input, readParams);
            foreach (var type in asm.Modules.SelectMany(AllTypes).Where(t => !CompilerGenerated(t)))
            {
                foreach (var m in type.Methods.Where(CallsGetCallingAssembly))
                {
                    result.Add(m.FullName);
                }
            }
        }
        return result;
    }

    private static void PassCallerAssembly(ModuleDefinition module, HashSet<string> users, ShimApi shim, Report report)
    {
        if (users.Count == 0) return;
        var set = module.ImportReference(shim.Find("WebShim.CallerAssembly", "Set", ["System.RuntimeTypeHandle"]));
        var take = module.ImportReference(shim.Find("WebShim.CallerAssembly", "Take", []));
        foreach (var type in AllTypes(module).ToList())
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                // Callers: name the calling type right before the call (after its arguments; before any prefix).
                var calls = method.Body.Instructions.Where(i => i.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj
                    && i.Operand is MethodReference r && users.Contains(r.GetElementMethod().FullName)).ToList();
                if (calls.Count > 0)
                {
                    // Long branch forms first: inserted instructions can push a short branch's target out of its range.
                    method.Body.SimplifyMacros();
                    var il = method.Body.GetILProcessor();
                    var callerType = TopLevel(type); // any type of the calling assembly will do
                    foreach (var call in calls)
                    {
                        var at = call;
                        while (at.Previous != null && at.Previous.OpCode.OpCodeType == OpCodeType.Prefix) at = at.Previous;
                        var first = il.Create(OpCodes.Ldtoken, callerType);
                        il.InsertBefore(at, first);
                        il.InsertBefore(at, il.Create(OpCodes.Call, set));
                        Retarget(method.Body, at, first);
                    }
                    method.Body.OptimizeMacros();
                    foreach (var _ in calls) report.Count("calls naming their assembly for GetCallingAssembly");
                }

                // Users: take the caller in the first instruction.
                if (!CompilerGenerated(type) && CallsGetCallingAssembly(method))
                {
                    var body = method.Body;
                    body.SimplifyMacros();
                    var il = body.GetILProcessor();
                    var local = new VariableDefinition(new TypeReference("System.Reflection", "Assembly", module, module.TypeSystem.CoreLibrary));
                    body.Variables.Add(local);
                    var start = body.Instructions[0];
                    il.InsertBefore(start, il.Create(OpCodes.Call, take));
                    il.InsertBefore(start, il.Create(OpCodes.Stloc, local));
                    foreach (var ins in body.Instructions.Where(i => i.OpCode.Code == Code.Call && i.Operand is MethodReference r && r.FullName == GetCallingAssembly).ToList())
                    {
                        // GetCallingAssembly() -> taken ?? GetCallingAssembly()
                        var real = il.Create(OpCodes.Call, (MethodReference)ins.Operand);
                        var useTaken = il.Create(OpCodes.Ldloc, local);
                        var end = il.Create(OpCodes.Nop);
                        ins.OpCode = OpCodes.Ldloc;
                        ins.Operand = local;
                        il.InsertAfter(ins, il.Create(OpCodes.Brtrue, useTaken));
                        var next = ins.Next;
                        il.InsertAfter(next, real);
                        il.InsertAfter(real, il.Create(OpCodes.Br, end));
                        il.InsertAfter(real.Next, useTaken);
                        il.InsertAfter(useTaken, end);
                    }
                    body.OptimizeMacros();
                    report.Count("methods whose GetCallingAssembly takes the caller passed in");
                }
            }
        }
    }

    private static TypeDefinition TopLevel(TypeDefinition type)
    {
        while (type.DeclaringType != null) type = type.DeclaringType;
        return type;
    }

    private static void Retarget(MethodBody body, Instruction from, Instruction to)
    {
        foreach (var ins in body.Instructions)
        {
            if (ins.Operand == from) ins.Operand = to;
            else if (ins.Operand is Instruction[] targets) ins.Operand = targets.Select(t => t == from ? to : t).ToArray();
        }
        foreach (var h in body.ExceptionHandlers)
        {
            if (h.TryStart == from) h.TryStart = to;
            if (h.TryEnd == from) h.TryEnd = to;
            if (h.HandlerStart == from) h.HandlerStart = to;
            if (h.HandlerEnd == from) h.HandlerEnd = to;
            if (h.FilterStart == from) h.FilterStart = to;
        }
    }
}
