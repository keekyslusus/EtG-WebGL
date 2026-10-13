using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using HarmonyLib.Public.Patching;
using Mono.Cecil;
using MonoMod.Cil;
using MonoMod.Utils;

namespace EtgModBaker;

internal sealed class Baker
{
    // Game assemblies mods may patch; the rest of Managed (mscorlib, System*, UnityEngine*) comes
    // from the Unity editor at build time and can't be changed.
    private static readonly string[] GameAssemblies =
    {
        "Assembly-CSharp", "Assembly-CSharp-firstpass", "Assembly-UnityScript", "PlayMaker",
        "dfScriptLite", "ConditionalExpression", "GalaxyCSharp", "SonyNP", "SonyPS4SavedGames",
    };

    private readonly string _managed;
    private readonly string _outDir;
    private readonly string[] _pluginDirs;
    private readonly string _facadeDir;
    private readonly Dictionary<string, string> _dllByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly CecilWorld _world;
    private readonly List<string> _problems = new();

    public Baker(string managed, string outDir, string facadeDir, string[] pluginDirs)
    {
        _facadeDir = facadeDir;
        _managed = managed;
        _outDir = outDir;
        _pluginDirs = pluginDirs;
        _world = new CecilWorld(_dllByName);
    }

    public int Run()
    {
        IndexAssemblies();
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            string name = new AssemblyName(e.Name).Name;
            return _dllByName.TryGetValue(name, out string path) ? Assembly.LoadFrom(path) : null;
        };
        MonoLikeLookup.Install();
        // Harmony asks every resolver in turn; ours is added last and always wins, so nothing is
        // detoured in this process: patch infos are only recorded and the bodies are built below.
        PatchManager.ResolvePatcher += (_, e) => e.MethodPatcher = new RecordingPatcher(e.Original);

        var mods = FindMods();
        Console.WriteLine($"== Mods (load order): {string.Join(", ", mods.Select(m => m.Name))}");
        foreach (var mod in mods)
        {
            _world.MarkWritable(mod.Name);
        }
        foreach (string game in GameAssemblies)
        {
            _world.MarkWritable(game);
        }

        foreach (var mod in mods)
        {
            RegisterHarmonyPatches(mod);
        }

        var transplant = new Transplanter(_world);
        BakeHarmonyPatches(transplant);
        Redirector.AddGenericForwarders(_world, transplant);
        BakeReversePatches(transplant);
        foreach (var mod in mods)
        {
            new HookBaker(_world, _problems).Bake(_world.Module(mod.Name));
        }
        new Redirector(_world).Run();
        foreach (var mod in mods)
        {
            new Stripper(_problems).Strip(_world.Module(mod.Name));
        }
        new AccessFixer(_world, _problems).Run(_world.WritableModules());

        foreach (string name in _world.ModifiedOrMods(mods.Select(m => m.Name)))
        {
            var module = _world.Module(name);
            foreach (var tr in module.GetTypeReferences())
            {
                if (tr.Scope is AssemblyNameReference a && a.Name == module.Assembly.Name.Name)
                {
                    _problems.Add($"{name}: reference to its own type {tr.FullName}");
                }
            }
        }
        // What the web build ships: the facades, then whatever the plugin folders bring along
        // (mods, Newtonsoft.Json...). BepInEx's own core folder is only for running HarmonyX here.
        var shipped = Identities.Collect(_pluginDirs.Where(d => !d.Replace('\\', '/').EndsWith("BepInEx/core"))
            .SelectMany(d => Directory.GetFiles(d, "*.dll", SearchOption.AllDirectories)));
        foreach (var kv in Identities.Collect(Directory.GetFiles(_facadeDir, "*.dll"))) shipped[kv.Key] = kv.Value;
        var modNames = new HashSet<string>(mods.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(_outDir);
        foreach (string name in _world.ModifiedOrMods(mods.Select(m => m.Name)))
        {
            Identities.Apply(_world.Module(name), shipped, stripHarmonyAttributes: modNames.Contains(name));
            string path = Path.Combine(_outDir, name + ".dll");
            _world.Module(name).Assembly.Write(path);
            Console.WriteLine($"   wrote {name}.dll");
        }

        // Libraries the mods need at runtime (Newtonsoft.Json...) and the facades go along.
        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
        {
            foreach (var r in _world.Module(mod.Name).AssemblyReferences) needed.Add(r.Name);
        }
        foreach (string dir in _pluginDirs.Where(d => !d.Replace('\\', '/').EndsWith("BepInEx/core")))
        {
            foreach (string dll in Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                if (needed.Contains(name) && !modNames.Contains(name) && !_dllByName[name].StartsWith(_managed, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(dll, Path.Combine(_outDir, name + ".dll"), true);
                    Console.WriteLine($"   copied {name}.dll");
                }
            }
        }
        foreach (string dll in Directory.GetFiles(_facadeDir, "*.dll").Where(f => !Path.GetFileName(f).Equals("WebShim.dll", StringComparison.OrdinalIgnoreCase)))
        {
            File.Copy(dll, Path.Combine(_outDir, Path.GetFileName(dll)), true);
            Console.WriteLine($"   copied facade {Path.GetFileName(dll)}");
        }

        if (_problems.Count > 0)
        {
            Console.WriteLine($"== {_problems.Count} problem(s):");
            foreach (string p in _problems) Console.WriteLine("   " + p);
            return 1;
        }
        Console.WriteLine("== Done, no problems");
        return 0;
    }

    private void IndexAssemblies()
    {
        // Plugin dirs first so a mod's own copy of a library wins over nothing; the game's Managed
        // dir wins for anything the game ships (UnityEngine, mscorlib...).
        foreach (string dir in _pluginDirs)
        {
            foreach (string dll in Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories))
            {
                _dllByName[Path.GetFileNameWithoutExtension(dll)] = dll;
            }
        }
        foreach (string dll in Directory.GetFiles(_managed, "*.dll"))
        {
            _dllByName[Path.GetFileNameWithoutExtension(dll)] = dll;
        }
    }

    private sealed record Mod(string Name, string Path, Assembly Assembly);

    // A mod = an assembly in a plugin dir with a [BepInPlugin] type. Libraries next to it
    // (Newtonsoft.Json, Ionic.Zip...) are left alone.
    private List<Mod> FindMods()
    {
        var mods = new List<Mod>();
        foreach (string dir in _pluginDirs)
        {
            foreach (string dll in Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories))
            {
                using var module = ModuleDefinition.ReadModule(dll);
                bool isPlugin = module.Types.Any(t => t.CustomAttributes.Any(a => a.AttributeType.FullName == "BepInEx.BepInPlugin"));
                if (isPlugin)
                {
                    string name = module.Assembly.Name.Name;
                    mods.Add(new Mod(name, dll, Assembly.LoadFrom(dll)));
                }
            }
        }
        return mods;
    }

    private static IEnumerable<Type> LoadableTypes(Assembly asm)
    {
        try
        {
            return asm.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            foreach (var le in e.LoaderExceptions.Take(5)) Console.WriteLine("   (type load) " + le.Message);
            return e.Types.Where(t => t != null);
        }
    }

    private static readonly FieldInfo PatchMethodsField = typeof(PatchClassProcessor).GetField("patchMethods", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly Type AttributePatchType = typeof(Harmony).Assembly.GetType("HarmonyLib.AttributePatch");
    private static readonly FieldInfo AttributePatchKind = AttributePatchType.GetField("type", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo AttributePatchInfo = AttributePatchType.GetField("info", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo GetOriginalMethod = typeof(Harmony).Assembly.GetType("HarmonyLib.PatchTools")
        .GetMethod("GetOriginalMethod", BindingFlags.Static | BindingFlags.NonPublic);

    private readonly List<(string Mod, HarmonyMethod Info)> _reversePatches = new();

    // Same as Harmony.PatchAll(assembly), but one patch method at a time: HarmonyX gives up on the
    // whole class at the first patch it can't apply, and a class often mixes game and mscorlib targets.
    // Reverse patches are only collected here (BakeReversePatches).
    private void RegisterHarmonyPatches(Mod mod)
    {
        var harmony = new Harmony("modbaker." + mod.Name);
        int count = 0;
        foreach (var type in LoadableTypes(mod.Assembly))
        {
            var processor = harmony.CreateClassProcessor(type);
            if (!(PatchMethodsField.GetValue(processor) is System.Collections.IList list)) continue;
            var entries = list.Cast<object>().ToList();
            foreach (var entry in entries)
            {
                var info = (HarmonyMethod)AttributePatchInfo.GetValue(entry);
                if ((HarmonyPatchType?)AttributePatchKind.GetValue(entry) == HarmonyPatchType.ReversePatch)
                {
                    _reversePatches.Add((mod.Name, info));
                    continue;
                }
                list.Clear();
                list.Add(entry);
                try
                {
                    processor.Patch();
                    count++;
                }
                catch (Exception e)
                {
                    string where = $"{mod.Name}: {type.FullName}::{info.method?.Name}";
                    if (AcceptedFailures.Any(a => where.Contains(a)))
                    {
                        Console.WriteLine($"   (skipped, accepted) {where}: {Flatten(e)}");
                    }
                    else
                    {
                        _problems.Add($"{where}: {Flatten(e)}");
                    }
                }
            }
        }
        Console.WriteLine($"   {mod.Name}: {count} patch methods registered");
    }

    // Patches that can't exist here and whose loss is understood.
    private static readonly string[] AcceptedFailures =
    {
        // Postfix on Mono's private Enum.FormatFlags (ToString of [Flags] enums with values added at
        // runtime). mscorlib can't be patched and nothing calls FormatFlags directly.
        "ETGModCompatibility::FixFlagFormatting_Postfix",
    };

    private void BakeHarmonyPatches(Transplanter transplant)
    {
        var originals = Harmony.GetAllPatchedMethods().ToList();
        Console.WriteLine($"== Baking Harmony patches into {originals.Count} methods");
        foreach (var original in originals)
        {
            var info = Harmony.GetPatchInfo(original);
            string asm = original.DeclaringType.Assembly.GetName().Name;
            string desc = $"{original.DeclaringType.FullName}::{original.Name} [{asm}] " +
                $"pre {info.Prefixes.Count} post {info.Postfixes.Count} fin {info.Finalizers.Count} trans {info.Transpilers.Count} ilm {info.ILManipulators.Count}";
            if (original.DeclaringType.Assembly == typeof(Harmony).Assembly)
            {
                continue; // our own MonoLikeLookup
            }
            if (!_world.IsWritable(asm))
            {
                // Engine/BCL method: patched copy in a wrapper, calls to it redirected (Redirector).
                try
                {
                    Redirector.AddWrapper(_world, transplant, original);
                    Console.WriteLine("   " + desc + " (wrapper)");
                }
                catch (Exception e)
                {
                    _problems.Add($"{desc}: {Flatten(e)}");
                }
                continue;
            }
            try
            {
                using var dmd = new DynamicMethodDefinition(original);
                using (var ctx = new ILContext(dmd.Definition))
                {
                    // Invoke turns branch targets into ILLabels for the manipulators (like ILHook does).
                    ctx.Invoke(c => HarmonyManipulator.Manipulate(original, c));
                }
                var target = _world.Lookup(original);
                transplant.ReplaceBody(dmd.Definition, target);
                new ArgumentFixer(_world, _problems).Fix(target);
                Console.WriteLine("   " + desc);
            }
            catch (Exception e)
            {
                _problems.Add($"{desc}: {Flatten(e)}");
            }
        }
    }

    // [HarmonyReversePatch] stand-ins get a copy of the original body (Original type, no transpilers).
    private void BakeReversePatches(Transplanter transplant)
    {
        foreach (var (mod, info) in _reversePatches)
        {
            string desc = $"{mod}: reverse patch {info.method.DeclaringType.FullName}::{info.method.Name}";
            try
            {
                if (info.reversePatchType == HarmonyReversePatchType.Snapshot)
                {
                    throw new NotSupportedException("Snapshot reverse patches");
                }
                var original = (MethodBase)GetOriginalMethod.Invoke(null, new object[] { info })
                    ?? throw new InvalidOperationException("original not found");
                using var dmd = new DynamicMethodDefinition(original);
                transplant.ReplaceBody(dmd.Definition, _world.Lookup(info.method));
                Console.WriteLine($"   {desc} <- {original.DeclaringType.FullName}::{original.Name}");
            }
            catch (Exception e)
            {
                _problems.Add($"{desc}: {Flatten(e)}");
            }
        }
    }

    internal static string Flatten(Exception e)
    {
        if (Environment.GetEnvironmentVariable("MODBAKER_DEBUG") == "1") return e.ToString();
        while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
        string s = $"{e.GetType().Name}: {e.Message}";
        return e.InnerException != null ? s + " <- " + Flatten(e.InnerException) : s;
    }

    /// <summary>Records patches without touching the running method.</summary>
    private sealed class RecordingPatcher : MethodPatcher
    {
        public RecordingPatcher(MethodBase original) : base(original) { }
        public override DynamicMethodDefinition PrepareOriginal() => null;
        public override MethodBase DetourTo(MethodBase replacement) => null;
        public override DynamicMethodDefinition CopyOriginal() => new DynamicMethodDefinition(Original);
    }
}
