using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace EtgWebPatcher;

/// <summary>
/// The game and the mods were compiled against Unity 2017.4; the port is built with Unity 6.6. Unity's own API updater
/// only half-fixes such assemblies (it doesn't move types between engine modules, and gives up on the whole set when one
/// assembly fails), so the patcher writes what 6.6 expects and the updater finds nothing to do
/// (port/tasks/unity6-migration.md).
/// </summary>
internal static partial class Program
{
    // Engine types that moved to another module since 2017.4 (the old scope doesn't resolve: the editor unloads the
    // whole assembly as "broken", UnityLinker stops on it).
    private static readonly Dictionary<string, string> MovedUnityTypes = new()
    {
        ["UnityEngine.AssetBundle"] = "UnityEngine.AssetBundleModule",
        ["UnityEngine.AssetBundleManifest"] = "UnityEngine.AssetBundleModule",
        ["UnityEngine.Input"] = "UnityEngine.InputLegacyModule",
        ["UnityEngine.Touch"] = "UnityEngine.InputLegacyModule",
        ["UnityEngine.TouchPhase"] = "UnityEngine.InputLegacyModule",
        ["UnityEngine.TouchType"] = "UnityEngine.InputLegacyModule",
        ["UnityEngine.Gyroscope"] = "UnityEngine.InputLegacyModule",
        ["UnityEngine.DeviceOrientation"] = "UnityEngine.InputLegacyModule",
        ["UnityEngine.Analytics.AnalyticsSessionInfo"] = "UnityEngine.UnityAnalyticsModule",
    };

    // Engine types renamed (UnityUpgradable): every signature using them changes with the TypeRef.
    private static readonly Dictionary<string, string> RenamedUnityTypes = new()
    {
        ["UnityEngine.PhysicMaterial"] = "PhysicsMaterial", // Collider.material / sharedMaterial
        ["UnityEngine.PhysicMaterialCombine"] = "PhysicsMaterialCombine",
    };

    // Engine members removed or renamed, as the API updater would rewrite them. FirstParameter (full type name) picks
    // the overload when the old name has several with different successors; null = every overload.
    private static readonly (string Type, string Name, string? FirstParameter, string NewName)[] RenamedUnityMembers =
    {
        ("UnityEngine.Camera", "get_hdr", null, "get_allowHDR"),
        ("UnityEngine.Camera", "set_hdr", null, "set_allowHDR"),
        ("UnityEngine.Graphics", "DrawProceduralIndirect", "UnityEngine.MeshTopology", "DrawProceduralIndirectNow"),
        ("UnityEngine.Texture2D", "Resize", null, "Reinitialize"),
    };

    // Engine assemblies that are packages now, under another name.
    private static readonly Dictionary<string, string> RenamedUnityAssemblies = new()
    {
        ["UnityEngine.Timeline"] = "Unity.Timeline", // com.unity.timeline
    };

    // Methods returning a UnityEngine.Object whose result goes through WebShim.UnityNull.Normalize. Unity 6's editor reads
    // an empty entry ({fileID: 0}) of a List<UnityEngine.Object> as a "fake null" object of type UnityEngine.Object, not
    // null: FullInspector's object references then fail to land in GameObject fields ("Object of type
    // 'UnityEngine.Object' cannot be converted to type 'UnityEngine.GameObject'", BossStatueController.levelData).
    private static readonly (string Type, string Method)[] NormalizedUnityNullReturns =
    {
        ("FullInspector.Internal.ListSerializationOperator", "RetrieveObjectReference"),
    };

    private static void NormalizeUnityNullReturns(ModuleDefinition module, ShimApi shim, Report report)
    {
        foreach (var (typeName, methodName) in NormalizedUnityNullReturns)
        {
            foreach (var method in module.GetType(typeName)?.Methods.Where(m => m.Name == methodName && m.HasBody) ?? [])
            {
                var normalize = module.ImportReference(shim.Find("WebShim.UnityNull", "Normalize", ["UnityEngine.Object"]));
                method.Body.SimplifyMacros();
                var il = method.Body.GetILProcessor();
                foreach (var ret in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
                {
                    // Insert before the ret by turning the ret into the call: branches to the ret then run the call too.
                    ret.OpCode = OpCodes.Call;
                    ret.Operand = normalize;
                    il.InsertAfter(ret, il.Create(OpCodes.Ret));
                }
                method.Body.OptimizeMacros();
                report.Count($"fake-null result normalized: {typeName}.{methodName}");
            }
        }
    }

    private static void RetargetForUnity6(ModuleDefinition module, Report report)
    {
        foreach (var reference in module.AssemblyReferences)
        {
            if (RenamedUnityAssemblies.TryGetValue(reference.Name, out var renamed))
            {
                report.Count($"reference renamed: {reference.Name} -> {renamed}");
                reference.Name = renamed;
                reference.Version = new Version(0, 0, 0, 0);
            }
        }

        var moduleRefs = new Dictionary<string, AssemblyNameReference>();
        foreach (var typeRef in module.GetTypeReferences())
        {
            if (typeRef.DeclaringType != null || typeRef.Scope is not AssemblyNameReference scope || !scope.Name.StartsWith("UnityEngine"))
            {
                continue;
            }
            string fullName = typeRef.FullName;
            if (RenamedUnityTypes.TryGetValue(fullName, out var newName))
            {
                typeRef.Name = newName;
                report.Count($"type renamed: {fullName} -> {newName}");
            }
            if (MovedUnityTypes.TryGetValue(fullName, out var target) && scope.Name != target)
            {
                if (!moduleRefs.TryGetValue(target, out var targetRef))
                {
                    targetRef = module.AssemblyReferences.FirstOrDefault(r => r.Name == target);
                    if (targetRef == null)
                    {
                        targetRef = new AssemblyNameReference(target, new Version(0, 0, 0, 0));
                        module.AssemblyReferences.Add(targetRef);
                    }
                    moduleRefs[target] = targetRef;
                }
                typeRef.Scope = targetRef;
                report.Count($"type moved: {fullName} {scope.Name} -> {target}");
            }
        }
        // An engine module nothing points to any more (the API updater would remove it, i.e. rewrite the DLL).
        var used = module.GetTypeReferences().Select(t => t.Scope).ToHashSet();
        foreach (var reference in module.AssemblyReferences.Where(r => r.Name.StartsWith("UnityEngine.") && !used.Contains(r)).ToList())
        {
            module.AssemblyReferences.Remove(reference);
            report.Count($"unused reference removed: {reference.Name}");
        }

        foreach (var memberRef in module.GetMemberReferences().OfType<MethodReference>())
        {
            string declaring = memberRef.DeclaringType.GetElementType().FullName;
            foreach (var (type, name, firstParameter, newName) in RenamedUnityMembers)
            {
                if (declaring == type && memberRef.Name == name
                    && (firstParameter == null || memberRef.Parameters.FirstOrDefault()?.ParameterType.FullName == firstParameter))
                {
                    memberRef.Name = newName;
                    report.Count($"member renamed: {type}::{name} -> {newName}");
                }
            }
        }
    }
}
