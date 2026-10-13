using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;

namespace EtgModBaker;

/// <summary>
/// The Cecil side: one ModuleDefinition per assembly (game, mods, the game's own BCL and UnityEngine),
/// all resolving each other, plus a type lookup that maps references made against .NET Framework
/// (HarmonyX runs here, so typeof(bool) is mscorlib 4.0) back to the assemblies the game ships.
/// </summary>
internal sealed class CecilWorld : IAssemblyResolver
{
    // Where types of the .NET Framework profile live in the game's own (Unity, .NET 3.5) BCL.
    private static readonly string[] Bcl = { "mscorlib", "System", "System.Core", "System.Xml", "System.Configuration", "System.Security", "Mono.Security" };

    private readonly Dictionary<string, string> _dllByName;
    private readonly Dictionary<string, AssemblyDefinition> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _writable = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _modified = new(StringComparer.OrdinalIgnoreCase);

    public CecilWorld(Dictionary<string, string> dllByName)
    {
        _dllByName = dllByName;
    }

    public void MarkWritable(string name) => _writable.Add(name);
    public bool IsWritable(string name) => _writable.Contains(name) && _dllByName.ContainsKey(name);
    public void MarkModified(ModuleDefinition module) => _modified.Add(module.Assembly.Name.Name);

    public IEnumerable<string> ModifiedOrMods(IEnumerable<string> mods) => _modified.Union(mods, StringComparer.OrdinalIgnoreCase);

    public IEnumerable<ModuleDefinition> WritableModules() =>
        _writable.Where(n => _dllByName.ContainsKey(n)).Select(Module).ToList();

    public ModuleDefinition Module(string name) => TryModule(name) ?? throw new FileNotFoundException("assembly not found: " + name);

    public ModuleDefinition TryModule(string name)
    {
        if (_loaded.TryGetValue(name, out var asm)) return asm.MainModule;
        if (!_dllByName.TryGetValue(name, out string path)) return null;
        asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
        {
            AssemblyResolver = this,
            ReadingMode = ReadingMode.Immediate,
            InMemory = true,
        });
        _loaded[name] = asm;
        return asm.MainModule;
    }

    // Tokens of a reflection MethodBase equal those Cecil reads from the same (unmodified) file. BCL
    // methods come from this process' .NET Framework mscorlib, not the game's: match by signature.
    public MethodDefinition Lookup(MethodBase method)
    {
        string name = method.Module.Assembly.GetName().Name;
        var module = Module(name);
        if (string.Equals(Path.GetFullPath(method.Module.Assembly.Location), Path.GetFullPath(_dllByName[name]), StringComparison.OrdinalIgnoreCase))
        {
            return (MethodDefinition)module.LookupToken(method.MetadataToken);
        }
        var type = module.GetType(method.DeclaringType.FullName.Replace('+', '/'))
            ?? throw new InvalidOperationException("type not found in the game's " + name + ": " + method.DeclaringType.FullName);
        var ps = method.GetParameters().Select(p => p.ParameterType.FullName.Replace('+', '/')).ToList();
        var matches = type.Methods.Where(m => m.Name == method.Name && m.IsStatic == method.IsStatic
            && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(ps)).ToList();
        if (matches.Count != 1)
        {
            throw new InvalidOperationException($"{matches.Count} matches in the game's {name} for {method.DeclaringType.FullName}::{method.Name}");
        }
        return matches[0];
    }

    /// <summary>The definition of a (non-generic-instance) type reference, searching first the assembly
    /// it names, then the game's BCL (types moved between mscorlib/System.Core across profiles).</summary>
    public TypeDefinition FindType(TypeReference t)
    {
        if (t is TypeDefinition td) return td;
        if (t.DeclaringType != null)
        {
            var outer = FindType(t.DeclaringType);
            return outer?.NestedTypes.FirstOrDefault(n => n.Name == t.Name);
        }
        string scope = t.Scope switch
        {
            AssemblyNameReference a => a.Name,
            ModuleDefinition m => m.Assembly.Name.Name,
            _ => null,
        };
        var candidates = new List<string>();
        if (scope != null) candidates.Add(scope);
        candidates.AddRange(Bcl);
        foreach (string name in candidates)
        {
            var module = TryModule(name);
            var def = module?.GetType(t.Namespace, t.Name);
            if (def != null) return def;
            // Type forwarders (e.g. a facade assembly).
            var exported = module?.ExportedTypes.FirstOrDefault(x => x.Namespace == t.Namespace && x.Name == t.Name);
            if (exported != null) return exported.Resolve();
        }
        return null;
    }

    AssemblyDefinition IAssemblyResolver.Resolve(AssemblyNameReference name) => TryModule(name.Name)?.Assembly;
    AssemblyDefinition IAssemblyResolver.Resolve(AssemblyNameReference name, ReaderParameters parameters) => TryModule(name.Name)?.Assembly;
    public void Dispose() { }
}
