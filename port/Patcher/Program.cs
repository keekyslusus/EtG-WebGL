using Mono.Cecil;
using Mono.Cecil.Cil;

namespace EtgWebPatcher;

/// <summary>
/// Rewrites EtG's managed assemblies so they can run under Unity WebGL:
///  - every P/Invoke (Wwise, Steam, Galaxy, InControl native, user32...) becomes a managed stub
///    returning default values, so nothing tries to call a native library that doesn't exist in wasm;
///    Wwise calls WebShim.WwiseShim implements are forwarded there instead (sound via Web Audio),
///    and XInput (XInputDotNet) goes to WebShim.XInputShim (browser Gamepad API / editor XInput);
///  - System.Threading.Thread usage is redirected to WebShim.ThreadShim (runs bodies synchronously);
///  - AssetBundle.LoadFromFile is redirected to WebShim.BundleShim (bundles preloaded at boot), and so
///    are instance AssetBundle calls (LoadAsset..., GetAllScenePaths) for the editor's bundle simulation;
///  - File.Move/Delete/Copy/Write* are redirected to WebShim.FileShim (flush IndexedDB after saves);
///  - assemblies with names Unity reserves for its own script assemblies (Assembly-CSharp...) are
///    renamed, otherwise the editor silently ignores them and every MonoBehaviour shows as missing.
///
/// Usage: Patcher &lt;game Managed dir&gt; &lt;WebShim.dll&gt; &lt;output dir&gt; [&lt;mods dir&gt;]
/// The optional mods dir (port/ModBaker output) holds game assemblies with the mods baked in, which
/// replace the originals, and the mods with their runtime libraries, which are patched as well.
/// </summary>
internal static class Program
{
    // Assemblies we own the patching of. Everything else (UnityEngine.*, System.*, mscorlib)
    // is supplied by the Unity editor at build time and must not be touched.
    private static readonly string[] Targets =
    {
        "Assembly-CSharp.dll",
        "Assembly-CSharp-firstpass.dll",
        "Assembly-UnityScript.dll",
        "GalaxyCSharp.dll",
        "SonyNP.dll",
        "SonyPS4SavedGames.dll",
        "PlayMaker.dll",
        "dfScriptLite.dll",
        "ConditionalExpression.dll",
    };

    // Unity 2017 won't load plugins named like its own script assemblies. Scene/prefab references
    // survive the rename because they point at the DLL's .meta GUID + a hash of namespace/class name.
    // Order matters for string replacement: longer names first.
    public static readonly (string From, string To)[] Renames =
    {
        ("Assembly-CSharp-firstpass", "EtgGameFirstpass"),
        ("Assembly-CSharp", "EtgGame"),
        ("Assembly-UnityScript", "EtgUnityScript"),
    };

    // Methods whose whole body is replaced by a call to another (static, same signature) method.
    // AttemptSoundEngineInitializationAsync loads SFX.bnk with a completion callback that the
    // stubbed Wwise never fires, and FoyerPreloader spins on GameManager.AUDIO_ENABLED forever.
    // The synchronous variant sets AUDIO_ENABLED right away.
    private static readonly (string Type, string Method, string Target)[] BodyRedirects =
    {
        ("GameManager", "AttemptSoundEngineInitializationAsync", "AttemptSoundEngineInitialization"),
    };

    // Methods whose body becomes "return default". WindowsResolutionManager.TrySetDisplay moves the
    // Win32 window; with user32 stubbed its Position? lookups come back null and .Value throws - from
    // GameManager.Awake as soon as SlotA.options holds a non-fullscreen mode, which aborts the rest of
    // Awake (input stops working after the main menu). There is no window to move in a browser.
    private static readonly (string Type, string Method)[] StubbedMethods =
    {
        ("BraveOptionsMenuItem/WindowsResolutionManager", "TrySetDisplay"),
        // Newtonsoft.Json (shipped with Mod the Gungeon API) compiles DynamicMethods when it thinks it may;
        // IL2CPP has no Reflection.Emit. false = its late-bound reflection path.
        ("Newtonsoft.Json.Serialization.JsonTypeReflector", "get_DynamicCodeGeneration"),
    };

    // Static methods whose body becomes a call to the WebShim method of the same name and signature.
    // AkSoundEngine.GetAkGameObjectID: WwiseShim needs the GameObject behind each id for positions.
    private static readonly (string Type, string Method, string ShimType)[] ShimForwards =
    {
        ("AkSoundEngine", "GetAkGameObjectID", "WebShim.WwiseShim"),
    };

    // Classes whose P/Invoke externs are forwarded to the WebShim method of the same name and
    // signature. Required: every extern of the class must have a match (else the patch fails),
    // otherwise unmatched externs are stubbed like the rest.
    private static readonly (string Type, string ShimType, bool Required)[] PInvokeForwards =
    {
        ("AkSoundEnginePINVOKE", "WebShim.WwiseShim", false),
        ("XInputDotNetPure.Imports", "WebShim.XInputShim", true),
    };

    // Instance methods whose body becomes a call to a static method of the same assembly with the
    // remaining arguments (this dropped). XInputDeviceManager.GetState dequeues what its worker
    // thread polled; that thread never runs here (NeverStartThreadsIn), so poll right away.
    private static readonly (string Type, string Method, string TargetType, string TargetMethod)[] InstanceToStatic =
    {
        ("InControl.XInputDeviceManager", "GetState", "XInputDotNetPure.GamePad", "GetState"),
    };

    // Methods whose UnityEngine.Application.platform reads go to XInputShim.PlatformForXInput:
    // InControl only enables XInput on Windows.
    private static readonly HashSet<string> XInputPlatformChecks = new()
    {
        "InControl.InControlManager::OnEnable",
        "InControl.XInputDeviceManager::CheckPlatformSupport",
    };

    // Integer literals in mod code replaced by a WebShim call (same value type), found by the literal and
    // the method it's passed to. Alexandria's PrefabAPI writes AssetBundles at runtime, for Windows.
    private static readonly (string Type, string Method, int Literal, string NextCall, string ShimType, string ShimMethod)[] LiteralShims =
    {
        ("Alexandria.PrefabAPI.AssetBundleBuilder", "BuildBundleFile", 19, "Write", "WebShim.BundleShim", "RuntimeBundleTargetPlatform"),
    };

    // Methods whose `catch { }` handlers (exception discarded) log what they caught: mods that swallow
    // errors where something IL2CPP-specific may go wrong. Logging only, behaviour unchanged.
    private static readonly (string Type, string Method)[] LogSwallowedExceptions =
    {
        ("ETGMod/Assets", "SetupSpritesFromFolder"),
    };

    // Static `Delegate F(MethodBase)` methods that compile the delegate with Reflection.Emit (none on IL2CPP):
    // the body becomes `return new <ReturnType>(new WebShim.ReflectionInvoker(method).Invoke)`. The
    // delegate type must take (object target, object[] args) and return object.
    private static readonly (string Type, string Method)[] ReflectionInvokerBodies =
    {
        ("ReflectionHelper", "CreateDelegate"), // Mod the Gungeon API
    };

    // Types whose threads loop forever polling hardware; running them synchronously would hang.
    private static readonly HashSet<string> NeverStartThreadsIn = new()
    {
        "InControl.XInputDeviceManager",
    };

    private static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "verify")
        {
            return Verify(args[1], args.Skip(2).ToArray());
        }
        if (args.Length != 3 && args.Length != 4)
        {
            Console.Error.WriteLine("Usage: Patcher <game Managed dir> <WebShim.dll> <output dir> [<mods dir>]");
            return 2;
        }
        string managedDir = Path.GetFullPath(args[0]);
        string shimPath = Path.GetFullPath(args[1]);
        string outDir = Path.GetFullPath(args[2]);
        string? modsDir = args.Length == 4 ? Path.GetFullPath(args[3]) : null;
        Directory.CreateDirectory(outDir);

        var resolver = new DefaultAssemblyResolver();
        if (modsDir != null)
        {
            resolver.AddSearchDirectory(modsDir);
        }
        resolver.AddSearchDirectory(managedDir);
        resolver.AddSearchDirectory(Path.GetDirectoryName(shimPath)!);
        // Mods with enum default values of game types: writing them resolves EtgGame (renamed, already written).
        resolver.AddSearchDirectory(outDir);
        var readParams = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate };

        using var shim = AssemblyDefinition.ReadAssembly(shimPath, readParams);
        var shimApi = new ShimApi(shim.MainModule);

        var targets = Targets.ToList();
        if (modsDir != null)
        {
            targets.AddRange(Directory.GetFiles(modsDir, "*.dll").Select(Path.GetFileName)
                .Where(n => !targets.Contains(n, StringComparer.OrdinalIgnoreCase) && !n!.Equals(Path.GetFileName(shimPath), StringComparison.OrdinalIgnoreCase))!);
        }
        bool ok = true;
        foreach (string name in targets)
        {
            string input = modsDir != null && File.Exists(Path.Combine(modsDir, name)) ? Path.Combine(modsDir, name) : Path.Combine(managedDir, name);
            if (!File.Exists(input))
            {
                Console.WriteLine($"[skip] {name}: not found");
                continue;
            }
            using var asm = AssemblyDefinition.ReadAssembly(input, readParams);
            var report = new Report(name);
            foreach (var module in asm.Modules)
            {
                PatchModule(module, shimApi, report);
                RetargetStubbedUnityTypes(module, shimApi, report);
                RenameReservedAssemblies(asm, module, report);
            }
            asm.Write(Path.Combine(outDir, asm.Name.Name + ".dll"));
            report.Print();
            ok &= report.Unhandled.Count == 0;
        }

        File.Copy(shimPath, Path.Combine(outDir, Path.GetFileName(shimPath)), overwrite: true);
        Console.WriteLine(ok ? "Done." : "Done, but some usages were not handled (see above).");
        return ok ? 0 : 1;
    }

    private static void PatchModule(ModuleDefinition module, ShimApi shim, Report report)
    {
        foreach (var type in AllTypes(module))
        {
            foreach (var method in type.Methods)
            {
                if (method.IsPInvokeImpl)
                {
                    string lib = method.PInvokeInfo?.Module.Name ?? "?";
                    var (_, shimType, required) = PInvokeForwards.FirstOrDefault(f => f.Type == type.FullName);
                    var forward = shimType != null ? shim.Matching(shimType, method) : null;
                    if (required && forward == null)
                    {
                        throw new InvalidOperationException($"{shimType} has no method matching {method.FullName}");
                    }
                    if (StubPInvoke(method))
                    {
                        if (forward != null)
                        {
                            EmitForwardBody(module, method, forward);
                            report.Count($"pinvoke forwarded to {forward.DeclaringType.Name}: {lib}");
                        }
                        else
                        {
                            report.Count("pinvoke stubbed: " + lib);
                        }
                    }
                    continue;
                }
                if (method.HasBody)
                {
                    RewriteCalls(module, type, method, shim, report);
                }
            }
        }
        foreach (var (typeName, methodName, targetName) in BodyRedirects)
        {
            var type = module.GetType(typeName);
            var method = type?.Methods.SingleOrDefault(m => m.Name == methodName);
            var target = type?.Methods.SingleOrDefault(m => m.Name == targetName && m.Parameters.Count == method?.Parameters.Count);
            if (method == null || target == null)
            {
                continue;
            }
            if (!method.IsStatic || !target.IsStatic || method.Parameters.Count != 0 || method.ReturnType.MetadataType != MetadataType.Void)
            {
                throw new InvalidOperationException($"Body redirect {typeName}.{methodName} only supports static void ()");
            }
            method.Body = new MethodBody(method);
            var il = method.Body.GetILProcessor();
            il.Emit(OpCodes.Call, target);
            il.Emit(OpCodes.Ret);
            report.Count($"body redirected: {typeName}.{methodName} -> {targetName}");
        }
        foreach (var (typeName, methodName, shimType) in ShimForwards)
        {
            foreach (var method in module.GetType(typeName)?.Methods.Where(m => m.Name == methodName && m.HasBody) ?? [])
            {
                var target = shim.Matching(shimType, method)
                    ?? throw new InvalidOperationException($"{shimType}.{methodName} with the signature of {method.FullName} not found in WebShim");
                EmitForwardBody(module, method, target);
                report.Count($"body forwarded: {typeName}.{methodName} -> {target.DeclaringType.Name}.{target.Name}");
            }
        }
        foreach (var (typeName, methodName, targetTypeName, targetName) in InstanceToStatic)
        {
            foreach (var method in module.GetType(typeName)?.Methods.Where(m => m.Name == methodName && m.HasBody && m.HasThis) ?? [])
            {
                var target = module.GetType(targetTypeName)?.Methods.SingleOrDefault(m => m.Name == targetName && m.IsStatic
                        && m.Parameters.Count == method.Parameters.Count && m.ReturnType.FullName == method.ReturnType.FullName)
                    ?? throw new InvalidOperationException($"{targetTypeName}.{targetName} matching {method.FullName} not found");
                method.Body = new MethodBody(method);
                var il = method.Body.GetILProcessor();
                foreach (var p in method.Parameters)
                {
                    il.Emit(OpCodes.Ldarg, p);
                }
                il.Emit(OpCodes.Call, target);
                il.Emit(OpCodes.Ret);
                report.Count($"body redirected: {typeName}.{methodName} -> {targetTypeName}.{targetName}");
            }
        }
        foreach (var (typeName, methodName, literal, nextCall, shimType, shimMethod) in LiteralShims)
        {
            foreach (var method in module.GetType(typeName)?.Methods.Where(m => m.Name == methodName && m.HasBody) ?? [])
            {
                var target = shim.Find(shimType, shimMethod, []);
                int count = 0;
                foreach (var ins in method.Body.Instructions)
                {
                    if (ins.OpCode.Code is Code.Ldc_I4 or Code.Ldc_I4_S && Convert.ToInt32(ins.Operand) == literal
                        && ins.Next?.Operand is MethodReference next && next.Name == nextCall)
                    {
                        ins.OpCode = OpCodes.Call;
                        ins.Operand = module.ImportReference(target);
                        count++;
                    }
                }
                if (count != 1)
                {
                    throw new InvalidOperationException($"{typeName}.{methodName}: expected one {literal} passed to {nextCall}, found {count}");
                }
                report.Count($"literal {literal} -> {target.DeclaringType.Name}.{target.Name}: {typeName}.{methodName}");
            }
        }
        foreach (var (typeName, methodName) in ReflectionInvokerBodies)
        {
            foreach (var method in module.GetType(typeName)?.Methods.Where(m => m.Name == methodName && m.HasBody && m.IsStatic
                && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.Reflection.MethodBase") ?? [])
            {
                var invokerType = shim.Type("WebShim.ReflectionInvoker");
                var ctor = invokerType.Methods.Single(m => m.IsConstructor && !m.IsStatic);
                var invoke = invokerType.Methods.Single(m => m.Name == "Invoke");
                var delegateCtor = method.ReturnType.Resolve().Methods.Single(m => m.IsConstructor && m.Parameters.Count == 2);
                method.Body = new MethodBody(method);
                var il = method.Body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Newobj, module.ImportReference(ctor));
                il.Emit(OpCodes.Ldftn, module.ImportReference(invoke));
                il.Emit(OpCodes.Newobj, module.ImportReference(delegateCtor));
                il.Emit(OpCodes.Ret);
                report.Count($"Reflection.Emit delegate -> ReflectionInvoker: {typeName}.{methodName}");
            }
        }
        foreach (var (typeName, methodName) in LogSwallowedExceptions)
        {
            foreach (var method in module.GetType(typeName)?.Methods.Where(m => m.Name == methodName && m.HasBody) ?? [])
            {
                var log = module.ImportReference(shim.Find("WebShim.ExceptionShim", "LogCaught", ["System.Object"]));
                foreach (var handler in method.Body.ExceptionHandlers.Where(h => h.HandlerType == ExceptionHandlerType.Catch && h.HandlerStart.OpCode == OpCodes.Pop))
                {
                    // pop -> call LogCaught(object): same stack effect.
                    handler.HandlerStart.OpCode = OpCodes.Call;
                    handler.HandlerStart.Operand = log;
                    report.Count($"swallowed exceptions logged: {typeName}.{methodName}");
                }
            }
        }
        foreach (var (typeName, methodName) in StubbedMethods)
        {
            foreach (var method in module.GetType(typeName)?.Methods.Where(m => m.Name == methodName && m.HasBody) ?? [])
            {
                EmitDefaultBody(method);
                report.Count($"stubbed: {typeName}.{methodName}");
            }
        }

        // Custom attributes on generic parameter constraints (C# 8 nullability, e.g. in Newtonsoft.Json):
        // the IKVM reader of Unity 2017's mcs rejects the whole assembly ("does not contain valid
        // metadata", CS0009) and with it every script compile. They mean nothing at runtime.
        foreach (var type in AllTypes(module))
        {
            foreach (var gp in type.GenericParameters.Concat(type.Methods.SelectMany(m => m.GenericParameters)))
            {
                foreach (var constraint in gp.Constraints.Where(c => c.HasCustomAttributes))
                {
                    constraint.CustomAttributes.Clear();
                    report.Count("constraint attributes removed (mcs can't read them)");
                }
            }
        }

        // Drop references to native libraries nobody calls any more.
        var stillUsed = AllTypes(module).SelectMany(t => t.Methods)
            .Where(m => m.IsPInvokeImpl && m.PInvokeInfo != null)
            .Select(m => m.PInvokeInfo.Module).ToHashSet();
        foreach (var modRef in module.ModuleReferences.Where(r => !stillUsed.Contains(r)).ToList())
        {
            module.ModuleReferences.Remove(modRef);
        }
    }

    /// <summary>
    /// Resolves every type/member the patched assemblies reference against the libraries the
    /// WebGL build actually links with (searchDirs, first match wins), and lists what's missing.
    /// UnityLinker dies on the first such reference, so this finds all of them in one go.
    /// Usage: Patcher verify &lt;patched dir&gt; &lt;search dir&gt;...
    /// </summary>
    private static int Verify(string patchedDir, string[] searchDirs)
    {
        var resolver = new DefaultAssemblyResolver();
        foreach (var dir in resolver.GetSearchDirectories())
        {
            resolver.RemoveSearchDirectory(dir);
        }
        // Patched dir first: an older copy of WebShim etc. may also sit in a search dir.
        foreach (var dir in searchDirs.Prepend(patchedDir))
        {
            resolver.AddSearchDirectory(dir);
        }
        var readParams = new ReaderParameters { AssemblyResolver = resolver };

        int problems = 0;
        foreach (string file in Directory.GetFiles(patchedDir, "*.dll").OrderBy(f => f))
        {
            using var asm = AssemblyDefinition.ReadAssembly(file, readParams);
            var missingTypes = new SortedDictionary<string, int>();
            var missingMembers = new SortedDictionary<string, int>();
            var missingAssemblies = new SortedSet<string>();
            foreach (var module in asm.Modules)
            {
                foreach (var reference in module.AssemblyReferences)
                {
                    try { resolver.Resolve(reference); }
                    catch (AssemblyResolutionException) { missingAssemblies.Add(reference.FullName); }
                }
                foreach (var typeRef in module.GetTypeReferences())
                {
                    if (SafeResolve(typeRef) == null && !missingAssemblies.Contains(typeRef.Scope.ToString()))
                    {
                        string key = $"{typeRef.FullName}  [{typeRef.Scope.Name}]";
                        missingTypes[key] = missingTypes.GetValueOrDefault(key) + 1;
                    }
                }
                foreach (var memberRef in module.GetMemberReferences())
                {
                    var declaring = memberRef.DeclaringType;
                    if (declaring is ArrayType)
                    {
                        continue; // array pseudo-methods
                    }
                    if (SafeResolve(declaring.GetElementType()) == null)
                    {
                        // Listed so stubs for the missing type can be written with the right shape.
                        string key = $"  on missing type: {memberRef}";
                        missingMembers[key] = missingMembers.GetValueOrDefault(key) + 1;
                        continue;
                    }
                    if (SafeResolve(memberRef) == null)
                    {
                        string key = $"{declaring.GetElementType().FullName}::{memberRef.Name}  ({memberRef})";
                        missingMembers[key] = missingMembers.GetValueOrDefault(key) + 1;
                    }
                }
            }
            if (missingAssemblies.Count + missingTypes.Count + missingMembers.Count == 0)
            {
                Console.WriteLine($"== {Path.GetFileName(file)}: OK");
                continue;
            }
            Console.WriteLine($"== {Path.GetFileName(file)}");
            foreach (var a in missingAssemblies) Console.WriteLine($"   MISSING ASSEMBLY {a}");
            foreach (var (t, _) in missingTypes) Console.WriteLine($"   MISSING TYPE     {t}");
            foreach (var (m, _) in missingMembers) Console.WriteLine($"   MISSING MEMBER   {m}");
            problems += missingAssemblies.Count + missingTypes.Count + missingMembers.Count;
        }
        Console.WriteLine(problems == 0 ? "All references resolve." : $"{problems} unresolved references.");
        return problems == 0 ? 0 : 1;
    }

    private static TypeDefinition? SafeResolve(TypeReference type)
    {
        try { return type.Resolve(); }
        catch (AssemblyResolutionException) { return null; }
    }

    private static IMemberDefinition? SafeResolve(MemberReference member)
    {
        try { return member.Resolve(); }
        catch (AssemblyResolutionException) { return null; }
    }

    // UnityEngine types removed from the WebGL player; WebShim/UnityStubs.cs defines look-alikes.
    private static readonly HashSet<string> StubbedUnityTypes = new()
    {
        "Network", "NetworkView", "NetworkViewID", "NetworkPlayer", "NetworkMessageInfo",
        "NetworkConnectionError", "NetworkDisconnection", "NetworkLogLevel", "NetworkPeerType",
        "MasterServer", "MasterServerEvent", "HostData", "BitStream", "RPC",
        "MovieTexture", "ProceduralMaterial",
    };

    // Class library types missing from the WebGL profile, implemented in WebShim/SystemStubs.cs.
    private static readonly HashSet<string> StubbedSystemTypes = new()
    {
        "System.ComponentModel.BindingList`1", "System.ComponentModel.AddingNewEventArgs", "System.ComponentModel.AddingNewEventHandler",
    };

    /// <summary>
    /// Points references to WebGL-missing UnityEngine types at WebShim's stubs. Cecil shares one
    /// TypeReference instance per TypeRef row, so changing its scope fixes every signature using it.
    /// </summary>
    private static void RetargetStubbedUnityTypes(ModuleDefinition module, ShimApi shim, Report report)
    {
        AssemblyNameReference? shimRef = null;
        foreach (var typeRef in module.GetTypeReferences())
        {
            bool unity = typeRef.Namespace == "UnityEngine" && StubbedUnityTypes.Contains(typeRef.Name)
                && typeRef.Scope is AssemblyNameReference us && us.Name.StartsWith("UnityEngine");
            bool system = StubbedSystemTypes.Contains(typeRef.FullName) && typeRef.Scope is AssemblyNameReference ss && ss.Name == "System";
            if (!unity && !system)
            {
                continue;
            }
            if (shimRef == null)
            {
                shimRef = module.AssemblyReferences.FirstOrDefault(r => r.Name == shim.AssemblyName.Name);
                if (shimRef == null)
                {
                    shimRef = new AssemblyNameReference(shim.AssemblyName.Name, shim.AssemblyName.Version);
                    module.AssemblyReferences.Add(shimRef);
                }
            }
            typeRef.Scope = shimRef;
            report.Count($"type retargeted to WebShim stub: {typeRef.FullName}");
        }
    }

    private static string? RenamedOrNull(string assemblyName)
    {
        foreach (var (from, to) in Renames)
        {
            if (assemblyName == from)
            {
                return to;
            }
        }
        return null;
    }

    private static readonly System.Text.RegularExpressions.Regex ReservedNameInString =
        new(@"Assembly-CSharp-firstpass|Assembly-CSharp(?![\w-])|Assembly-UnityScript(?![\w-])");

    /// <summary>
    /// Renames the assembly itself, its references to other renamed assemblies, and string
    /// literals naming them (e.g. PlayMaker's Type.GetType(name + ",Assembly-CSharp")).
    /// </summary>
    private static void RenameReservedAssemblies(AssemblyDefinition asm, ModuleDefinition module, Report report)
    {
        string? newName = RenamedOrNull(asm.Name.Name);
        if (newName != null && module == asm.MainModule)
        {
            report.Count($"assembly renamed: {asm.Name.Name} -> {newName}");
            asm.Name.Name = newName;
            module.Name = newName + ".dll";
        }
        foreach (var reference in module.AssemblyReferences)
        {
            string? renamed = RenamedOrNull(reference.Name);
            if (renamed != null)
            {
                report.Count($"reference renamed: {reference.Name} -> {renamed}");
                reference.Name = renamed;
            }
        }
        foreach (var method in AllTypes(module).SelectMany(t => t.Methods).Where(m => m.HasBody))
        {
            foreach (var ins in method.Body.Instructions)
            {
                if (ins.OpCode == OpCodes.Ldstr && ins.Operand is string s && ReservedNameInString.IsMatch(s))
                {
                    ins.Operand = ReservedNameInString.Replace(s, m => RenamedOrNull(m.Value)!);
                    report.Count("string literal renamed");
                }
            }
        }
    }

    /// <summary>Turns an extern P/Invoke into a managed method that zeroes out-params and returns default.</summary>
    private static bool StubPInvoke(MethodDefinition method)
    {
        string libName = method.PInvokeInfo?.Module.Name ?? "";
        if (libName == "__Internal")
        {
            return false; // statically linked into the WebGL build (jslib); leave alone
        }

        // Order matters: Cecil's PInvokeInfo setter turns IsPInvokeImpl back on.
        method.PInvokeInfo = null;
        method.IsPInvokeImpl = false;
        method.IsPreserveSig = false;
        method.IsInternalCall = false;
        method.ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed;
        EmitDefaultBody(method);
        return true;
    }

    /// <summary>Replaces the body with a call to a static method taking the same arguments.</summary>
    private static void EmitForwardBody(ModuleDefinition module, MethodDefinition method, MethodDefinition target)
    {
        var body = new MethodBody(method);
        method.Body = body;
        var il = body.GetILProcessor();
        if (method.HasThis)
        {
            throw new InvalidOperationException($"forwarding instance method {method.FullName} is not supported");
        }
        foreach (var p in method.Parameters)
        {
            il.Emit(OpCodes.Ldarg, p);
        }
        il.Emit(OpCodes.Call, module.ImportReference(target));
        il.Emit(OpCodes.Ret);
    }

    /// <summary>Replaces the body with one that zeroes out-params and returns default.</summary>
    private static void EmitDefaultBody(MethodDefinition method)
    {
        var body = new MethodBody(method) { InitLocals = true };
        method.Body = body;
        var il = body.GetILProcessor();

        foreach (var p in method.Parameters)
        {
            if (p.IsOut && p.ParameterType is ByReferenceType byRef)
            {
                il.Emit(OpCodes.Ldarg, p);
                il.Emit(OpCodes.Initobj, byRef.ElementType);
            }
        }

        var ret = method.ReturnType;
        if (ret.MetadataType != MetadataType.Void)
        {
            var local = new VariableDefinition(ret);
            body.Variables.Add(local);
            il.Emit(OpCodes.Ldloca, local);
            il.Emit(OpCodes.Initobj, ret);
            il.Emit(OpCodes.Ldloc, local);
        }
        il.Emit(OpCodes.Ret);
    }

    private static void RewriteCalls(ModuleDefinition module, TypeDefinition type, MethodDefinition method, ShimApi shim, Report report)
    {
        foreach (var ins in method.Body.Instructions)
        {
            if (ins.Operand is not MethodReference target)
            {
                continue;
            }
            if (ins.OpCode != OpCodes.Call && ins.OpCode != OpCodes.Callvirt && ins.OpCode != OpCodes.Newobj)
            {
                continue;
            }
            string declaring = target.DeclaringType.FullName;
            string sig = $"{target.Name}({string.Join(",", target.Parameters.Select(p => p.ParameterType.FullName))})";
            string where = $"{type.FullName}::{method.Name}";

            MethodDefinition? replacement = null;
            switch (declaring)
            {
                case "System.Threading.Thread":
                    replacement = sig switch
                    {
                        ".ctor(System.Threading.ThreadStart)" => shim.Thread("Create", "System.Threading.ThreadStart"),
                        ".ctor(System.Threading.ParameterizedThreadStart)" => shim.Thread("Create", "System.Threading.ParameterizedThreadStart"),
                        "Start()" => NeverStartThreadsIn.Contains(type.FullName)
                            ? shim.Thread("StartNever", "System.Threading.Thread")
                            : shim.Thread("Start", "System.Threading.Thread"),
                        "Start(System.Object)" => shim.Thread("Start", "System.Threading.Thread", "System.Object"),
                        "Join()" => shim.Thread("Join", "System.Threading.Thread"),
                        "Join(System.Int32)" => shim.Thread("Join", "System.Threading.Thread", "System.Int32"),
                        "Sleep(System.Int32)" => shim.Thread("Sleep", "System.Int32"),
                        "get_IsAlive()" => shim.Thread("IsAlive", "System.Threading.Thread"),
                        "Abort()" => shim.Thread("Abort", "System.Threading.Thread"),
                        // Harmless on WebGL: naming, priority, background flag, current thread queries.
                        "set_Name(System.String)" or "set_IsBackground(System.Boolean)" or "set_Priority(System.Threading.ThreadPriority)"
                            or "get_CurrentThread()" or "get_ManagedThreadId()" or "get_Name()" or "MemoryBarrier()" => null,
                        _ => Unhandled(report, $"Thread.{sig} in {where}"),
                    };
                    break;

                case "UnityEngine.AssetBundle":
                    if (!target.HasThis)
                    {
                        // LoadFromMemory works as is (Alexandria's PrefabAPI, see LiteralShims).
                        replacement = sig == "LoadFromFile(System.String)"
                            ? shim.Bundle("LoadFromFile", "System.String")
                            : target.Name.StartsWith("LoadFrom") && sig != "LoadFromMemory(System.Byte[])" ? Unhandled(report, $"AssetBundle.{sig} in {where}") : null;
                        break;
                    }
                    // Instance calls become BundleShim.X(bundle, args) so the editor's Play Mode can
                    // answer them for simulated bundles; real bundles get the original call.
                    if (target is GenericInstanceMethod generic)
                    {
                        if (sig == "LoadAsset(System.String)")
                        {
                            var shimGeneric = new GenericInstanceMethod(module.ImportReference(shim.BundleGeneric("LoadAsset", "UnityEngine.AssetBundle", "System.String")));
                            foreach (var argument in generic.GenericArguments)
                            {
                                shimGeneric.GenericArguments.Add(argument);
                            }
                            ins.OpCode = OpCodes.Call;
                            ins.Operand = shimGeneric;
                            report.Count($"{declaring}.{target.Name}<T> -> BundleShim.{target.Name}<T>");
                        }
                        else
                        {
                            Unhandled(report, $"AssetBundle.{sig}<> in {where}");
                        }
                        continue;
                    }
                    replacement = sig switch
                    {
                        "LoadAsset(System.String)" => shim.Bundle("LoadAsset", "UnityEngine.AssetBundle", "System.String"),
                        "LoadAsset(System.String,System.Type)" => shim.Bundle("LoadAsset", "UnityEngine.AssetBundle", "System.String", "System.Type"),
                        "GetAllScenePaths()" => shim.Bundle("GetAllScenePaths", "UnityEngine.AssetBundle"),
                        "GetAllAssetNames()" => shim.Bundle("GetAllAssetNames", "UnityEngine.AssetBundle"),
                        "Unload(System.Boolean)" => shim.Bundle("Unload", "UnityEngine.AssetBundle", "System.Boolean"),
                        _ => Unhandled(report, $"AssetBundle.{sig} in {where}"),
                    };
                    break;

                case "System.IO.File":
                    replacement = sig switch
                    {
                        "Move(System.String,System.String)" => shim.File("Move", "System.String", "System.String"),
                        "Delete(System.String)" => shim.File("Delete", "System.String"),
                        "Copy(System.String,System.String)" => shim.File("Copy", "System.String", "System.String"),
                        "Copy(System.String,System.String,System.Boolean)" => shim.File("Copy", "System.String", "System.String", "System.Boolean"),
                        "WriteAllText(System.String,System.String)" => shim.File("WriteAllText", "System.String", "System.String"),
                        "WriteAllBytes(System.String,System.Byte[])" => shim.File("WriteAllBytes", "System.String", "System.Byte[]"),
                        "Create(System.String,System.Int32,System.IO.FileOptions)" => shim.File("Create", "System.String", "System.Int32", "System.IO.FileOptions"),
                        _ => null,
                    };
                    break;

                case "System.String":
                    // EtG's obfuscator emitted ((string)(object)stringBuilder).Length in its string
                    // decryptor. Mono tolerates calling String::get_Length on a StringBuilder;
                    // IL2CPP's typed C++ doesn't compile. Call StringBuilder::get_Length instead.
                    if (sig == "get_Length()" && LoadedLocal(ins.Previous, method.Body)?.VariableType.FullName == "System.Text.StringBuilder")
                    {
                        var sb = new TypeReference("System.Text", "StringBuilder", module, module.TypeSystem.CoreLibrary);
                        ins.Operand = new MethodReference("get_Length", module.TypeSystem.Int32, sb) { HasThis = true };
                        ins.OpCode = OpCodes.Callvirt;
                        report.Count("String.get_Length on StringBuilder -> StringBuilder.get_Length");
                    }
                    break;

                case "UnityEngine.Application":
                    replacement = sig switch
                    {
                        "get_persistentDataPath()" => shim.File("PersistentDataPath"),
                        "get_platform()" when XInputPlatformChecks.Contains(where) => shim.XInput("PlatformForXInput"),
                        _ => null,
                    };
                    break;

                case "UnityEngine.Input":
                    replacement = sig == "GetJoystickNames()" && type.FullName == "InControl.UnityInputDeviceManager"
                        ? shim.XInput("GetJoystickNames")
                        : null;
                    break;

                case "UnityEngine.Screen":
                    replacement = sig == "get_resolutions()" ? shim.Screen("GetResolutions") : null;
                    break;

                case "UnityEngine.WWW":
                    replacement = sig == "GetMovieTexture()" ? shim.Media("GetMovieTexture", "UnityEngine.WWW") : null;
                    break;

                case "System.Threading.ThreadPool":
                    Unhandled(report, $"ThreadPool.{sig} in {where}");
                    break;
            }

            if (replacement == null)
            {
                continue;
            }
            ins.OpCode = OpCodes.Call;
            ins.Operand = module.ImportReference(replacement);
            report.Count($"{declaring}.{target.Name} -> {replacement.DeclaringType.Name}.{replacement.Name}");
        }
    }

    /// <summary>The local variable an ldloc instruction pushes, or null if it isn't an ldloc.</summary>
    private static VariableDefinition? LoadedLocal(Instruction? ins, MethodBody body)
    {
        if (ins == null)
        {
            return null;
        }
        switch (ins.OpCode.Code)
        {
            case Code.Ldloc_0: return body.Variables[0];
            case Code.Ldloc_1: return body.Variables[1];
            case Code.Ldloc_2: return body.Variables[2];
            case Code.Ldloc_3: return body.Variables[3];
            case Code.Ldloc:
            case Code.Ldloc_S: return ins.Operand as VariableDefinition;
            default: return null;
        }
    }

    private static MethodDefinition? Unhandled(Report report, string what)
    {
        report.Unhandled.Add(what);
        return null;
    }

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        var stack = new Stack<TypeDefinition>(module.Types);
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            yield return t;
            foreach (var nested in t.NestedTypes)
            {
                stack.Push(nested);
            }
        }
    }

    private sealed class ShimApi(ModuleDefinition shim)
    {
        public MethodDefinition Thread(string name, params string[] parameters) => Find("WebShim.ThreadShim", name, parameters);
        public MethodDefinition Bundle(string name, params string[] parameters) => Find("WebShim.BundleShim", name, parameters);
        public MethodDefinition BundleGeneric(string name, params string[] parameters) => Find("WebShim.BundleShim", name, parameters, genericArity: 1);
        public MethodDefinition File(string name, params string[] parameters) => Find("WebShim.FileShim", name, parameters);
        public MethodDefinition Media(string name, params string[] parameters) => Find("WebShim.MediaShim", name, parameters);
        public MethodDefinition Screen(string name, params string[] parameters) => Find("WebShim.ScreenShim", name, parameters);
        public MethodDefinition XInput(string name, params string[] parameters) => Find("WebShim.XInputShim", name, parameters);
        public TypeDefinition Type(string fullName) => shim.GetType(fullName) ?? throw new InvalidOperationException(fullName + " not found in WebShim");
        public AssemblyNameDefinition AssemblyName => shim.Assembly.Name;

        /// <summary>A public static method of typeName with the same name, parameter and return types.</summary>
        public MethodDefinition? Matching(string typeName, MethodDefinition method)
        {
            var type = shim.GetType(typeName) ?? throw new InvalidOperationException($"{typeName} not found in WebShim");
            return type.Methods.SingleOrDefault(m => m.Name == method.Name && m.IsStatic && m.IsPublic
                && m.ReturnType.FullName == method.ReturnType.FullName
                && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(method.Parameters.Select(p => p.ParameterType.FullName)));
        }

        public MethodDefinition Find(string typeName, string name, string[] parameters, int genericArity = 0)
        {
            var type = shim.GetType(typeName) ?? throw new InvalidOperationException($"{typeName} not found in WebShim");
            return type.Methods.SingleOrDefault(m => m.Name == name && m.IsStatic && m.GenericParameters.Count == genericArity
                       && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters))
                   ?? throw new InvalidOperationException($"{typeName}.{name}({string.Join(",", parameters)}) not found in WebShim");
        }
    }

    private sealed class Report(string assembly)
    {
        private readonly SortedDictionary<string, int> _counts = new();
        public readonly List<string> Unhandled = new();

        public void Count(string what) => _counts[what] = _counts.GetValueOrDefault(what) + 1;

        public void Print()
        {
            Console.WriteLine($"== {assembly}");
            if (_counts.Count == 0 && Unhandled.Count == 0)
            {
                Console.WriteLine("   (nothing to patch)");
            }
            foreach (var (what, n) in _counts)
            {
                Console.WriteLine($"   {n,5}  {what}");
            }
            foreach (var u in Unhandled.Distinct())
            {
                Console.WriteLine($"   UNHANDLED  {u}");
            }
        }
    }
}
