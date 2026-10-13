using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using MonoMod.Cil;

namespace EtgModBaker;

/// <summary>
/// Copies a method body built elsewhere (a MonoMod DynamicMethodDefinition: a static method in its
/// own module whose parameter 0 is `this` for instance methods) into a method of one of our modules,
/// importing every operand so it points at the assemblies the game actually ships.
/// </summary>
internal sealed class Transplanter
{
    private readonly CecilWorld _world;

    public Transplanter(CecilWorld world)
    {
        _world = world;
    }

    public void ReplaceBody(MethodDefinition src, MethodDefinition dst)
    {
        var module = dst.Module;
        var imp = new Importer(_world, dst);

        // Parameter map. src is static; for an instance dst, src parameter 0 is `this`.
        var dstArgs = new List<ParameterDefinition>();
        if (dst.HasThis) dstArgs.Add(dst.Body.ThisParameter);
        dstArgs.AddRange(dst.Parameters);
        if (dstArgs.Count != src.Parameters.Count)
        {
            throw new InvalidOperationException($"parameter count mismatch {src.FullName} -> {dst.FullName}");
        }
        var argMap = new Dictionary<ParameterDefinition, ParameterDefinition>();
        for (int i = 0; i < dstArgs.Count; i++) argMap[src.Parameters[i]] = dstArgs[i];

        var body = new MethodBody(dst);
        var varMap = new Dictionary<VariableDefinition, VariableDefinition>();
        foreach (var v in src.Body.Variables)
        {
            var nv = new VariableDefinition(imp.Type(v.VariableType));
            varMap[v] = nv;
            body.Variables.Add(nv);
        }
        body.InitLocals = src.Body.InitLocals || body.Variables.Count > 0;

        var insMap = new Dictionary<Instruction, Instruction>();
        foreach (var ins in src.Body.Instructions)
        {
            // Placeholder operand, fixed below once every instruction exists.
            var ni = Instruction.Create(OpCodes.Nop);
            ni.OpCode = ins.OpCode;
            insMap[ins] = ni;
            body.Instructions.Add(ni);
        }
        foreach (var ins in src.Body.Instructions)
        {
            insMap[ins].Operand = MapOperand(ins, imp, insMap, varMap, argMap, dstArgs, body.Variables);
        }
        foreach (var eh in src.Body.ExceptionHandlers)
        {
            body.ExceptionHandlers.Add(new ExceptionHandler(eh.HandlerType)
            {
                TryStart = Map(insMap, eh.TryStart),
                TryEnd = Map(insMap, eh.TryEnd),
                HandlerStart = Map(insMap, eh.HandlerStart),
                HandlerEnd = Map(insMap, eh.HandlerEnd),
                FilterStart = Map(insMap, eh.FilterStart),
                CatchType = eh.CatchType == null ? null : imp.Type(eh.CatchType),
            });
        }
        dst.Body = body;
        // Short branches may no longer reach after patches were inserted.
        body.SimplifyMacros();
        body.OptimizeMacros();
        _world.MarkModified(module);
    }

    private static Instruction Map(Dictionary<Instruction, Instruction> map, Instruction i) => i == null ? null : map[i];

    private static object MapOperand(Instruction ins, Importer imp, Dictionary<Instruction, Instruction> insMap,
        Dictionary<VariableDefinition, VariableDefinition> varMap, Dictionary<ParameterDefinition, ParameterDefinition> argMap,
        List<ParameterDefinition> dstArgs, IList<VariableDefinition> vars)
    {
        object op = ins.Operand;
        switch (op)
        {
            case null: return null;
            case Instruction target: return insMap[target];
            case Instruction[] targets: return targets.Select(t => insMap[t]).ToArray();
            // IL manipulators branch to MonoMod labels; MonoMod resolves them when generating.
            case ILLabel label: return insMap[label.Target];
            case ILLabel[] labels: return labels.Select(l => insMap[l.Target]).ToArray();
            case VariableDefinition v: return varMap[v];
            case ParameterDefinition p: return argMap[p];
            case TypeReference t: return imp.Type(t);
            case MethodReference m: return imp.Method(m);
            case FieldReference f: return imp.Field(f);
            case CallSite cs: throw new NotSupportedException("calli in patched body");
        }
        // HarmonyX and IL manipulators emit argument/local indexes as raw ints/bytes; MonoMod
        // converts them when it generates the DynamicMethod, Cecil can't write them.
        if (op is int || op is byte || op is sbyte)
        {
            int index = Convert.ToInt32(op);
            switch (ins.OpCode.OperandType)
            {
                case OperandType.InlineArg:
                case OperandType.ShortInlineArg:
                    return dstArgs[index];
                case OperandType.InlineVar:
                case OperandType.ShortInlineVar:
                    return vars[index];
            }
        }
        return op;
    }
}
