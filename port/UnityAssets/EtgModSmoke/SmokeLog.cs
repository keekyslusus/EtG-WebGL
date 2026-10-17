using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

// Error capture for the mod smoke test: every error / exception / assert in the Unity log (and every
// exception a step itself throws) is attributed to the step running at the time, grouped by signature
// and classified. Same C# 4 restrictions as the editor scripts (Unity 2017.4, legacy runtime).
namespace EtgModSmoke
{
    public static class Category
    {
        public const string Port = "port";         // ошибка порта: access checks, facades, shims, baked hooks
        public const string Mod = "mod";           // вероятно баг мода: thrown in a mod's own code
        public const string PlayMode = "playmode"; // only in the editor (prefab assets, constructor restrictions)
        public const string Other = "other";       // the game's code or unclear
        public const string Smoke = "smoke";       // the smoke test itself
    }

    [Serializable]
    public class ErrorGroup
    {
        public string category;
        public string type;
        public string message;
        public string stack;   // first frames
        public int count;
        public int frames;     // distinct frames it was logged in
        public int maxPerFrame;
        public string firstStep;
        public List<string> steps = new List<string>(); // global groups only (first 20)
        public int stepCount;

        [NonSerialized] public int lastFrame = -1;
        [NonSerialized] public HashSet<string> stepSet = new HashSet<string>();
        [NonSerialized] public int inFrame;

        public void Add(int frame)
        {
            count++;
            if (frame != lastFrame)
            {
                lastFrame = frame;
                frames++;
                inFrame = 0;
            }
            inFrame++;
            if (inFrame > maxPerFrame)
            {
                maxPerFrame = inFrame;
            }
        }
    }

    [Serializable]
    public class StepResult
    {
        public string kind;    // startup, run, item, shop, enemy, floor, flow
        public string id;
        public string name;
        public string owner;
        public string detail;  // gun / active / passive / boss ...
        public string status = "pending"; // pending, ok, errors, timeout, skipped, failed
        public float seconds;
        public int frames;
        public int errorFrames;
        public List<string> notes = new List<string>();
        public List<ErrorGroup> errors = new List<ErrorGroup>();

        [NonSerialized] public Dictionary<string, ErrorGroup> bySignature = new Dictionary<string, ErrorGroup>();
        [NonSerialized] public bool carried; // result taken over from the previous attempt (port/modsmoke.sh resume)
        [NonSerialized] public int firstFrame;
        [NonSerialized] public int lastErrorFrame = -1;
        // level loads: LoopDungeonGenerator retries a flow forever; an exception in the loading coroutine ends it
        [NonSerialized] public int generationFailures;
        [NonSerialized] public bool loadCoroutineDied;

        public string Key { get { return kind + ":" + id; } }

        public int Count(string category)
        {
            int n = 0;
            foreach (ErrorGroup g in errors)
            {
                if (g.category == category)
                {
                    n += g.count;
                }
            }
            return n;
        }
    }

    public static class SmokeLog
    {
        public const string Prefix = "[ModSmoke] ";

        public static StepResult Current;
        public static readonly Dictionary<string, ErrorGroup> Global = new Dictionary<string, ErrorGroup>();
        public static readonly Dictionary<string, int> Filtered = new Dictionary<string, int>();
        private static readonly Dictionary<string, string> s_typeAssembly = new Dictionary<string, string>();
        private static readonly HashSet<string> s_modAssemblies = new HashSet<string>();
        private static bool s_installed;

        // Known noise of the game itself (unrelated to mods), see port/tasks/mod-smoke-test.md.
        private static readonly string[] Noise =
        {
            "Cannot override system-specified headers",
            "DataPrivacy",
            "Something is attempting to get a NONE save flag!",
            "INSTANTIATING AMMONOMICON",
            "Doing startup foyer checks!",
            "No mid game save found",
            "Unable to find clip 'select_idle'",
            "Material doesn't have a float or range property '_StencilVal'",
        };

        // The editor protects assets and constructors; a player build (WebGL included) doesn't.
        private static readonly string[] PlayModeOnly =
        {
            "is not allowed to be called from a MonoBehaviour constructor",
            "is not allowed to be called during serialization",
            "on prefab object",
            "resides in a prefab",
            "Destroying assets is not permitted",
            // BundleShim hands out runtime copies of mod-bundle prefabs in Play Mode; in the player they are assets
            "UnloadAsset can only be used on assets",
        };

        // Exceptions the port causes (Mono's access checks against baked code, missing members after rewriting).
        private static readonly string[] PortExceptions =
        {
            "MethodAccessException", "FieldAccessException", "TypeLoadException", "MissingMethodException",
            "MissingFieldException", "MissingMemberException", "EntryPointNotFoundException", "DllNotFoundException",
            "BadImageFormatException", "InvalidProgramException", "VerificationException",
        };

        // Assemblies written by the port (facades of BepInEx / HarmonyX / MonoMod, runtime shims).
        private static readonly string[] PortAssemblies = { "WebShim", "BepInEx", "0Harmony", "MonoMod.RuntimeDetour", "MonoMod.Utils" };

        public static void Install()
        {
            if (s_installed)
            {
                return;
            }
            s_installed = true;
            Application.logMessageReceived += OnLog;
        }

        public static void Uninstall()
        {
            if (s_installed)
            {
                Application.logMessageReceived -= OnLog;
                s_installed = false;
            }
        }

        public static IEnumerable<string> ModAssemblies { get { return s_modAssemblies; } }

        public static bool IsModAssembly(Assembly a)
        {
            return a != null && s_modAssemblies.Contains(a.GetName().Name);
        }

        // Mods = assemblies referencing BepInEx, minus the port's own facades. Also maps type names
        // to assemblies so stack frames can be attributed.
        public static void ScanAssemblies()
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = a.GetName().Name;
                bool isPort = Array.IndexOf(PortAssemblies, name) >= 0;
                bool isMod = false;
                if (!isPort)
                {
                    foreach (AssemblyName r in a.GetReferencedAssemblies())
                    {
                        if (r.Name == "BepInEx")
                        {
                            isMod = true;
                            break;
                        }
                    }
                }
                if (isMod)
                {
                    s_modAssemblies.Add(name);
                }
                if (!isMod && !isPort && !name.StartsWith("EtgGame") && name != "dfScriptLite" && name != "PlayMaker")
                {
                    continue;
                }
                Type[] types;
                try
                {
                    types = a.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types;
                }
                foreach (Type t in types)
                {
                    if (t != null && t.FullName != null)
                    {
                        s_typeAssembly[t.FullName] = name;
                        if (!string.IsNullOrEmpty(t.Namespace))
                        {
                            string root = t.Namespace.Split('.')[0];
                            string other;
                            if (!s_namespaceRoots.TryGetValue(root, out other))
                            {
                                s_namespaceRoots[root] = name;
                            }
                            else if (other != name)
                            {
                                s_namespaceRoots[root] = null; // shared: not telling
                            }
                        }
                    }
                }
            }
        }

        // Root namespace -> its only assembly, for frames of types not found by name (generated, renamed).
        private static readonly Dictionary<string, string> s_namespaceRoots = new Dictionary<string, string>();

        // Error groups of the previous attempt (resume): counted on together with the new ones.
        public static void AddPrevious(ErrorGroup g)
        {
            string signature = g.category + "|" + g.type + "|" + g.message + "|" + FirstLine(g.stack) + "|prev";
            Global[signature] = g;
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (Current != null && type == LogType.Log && condition != null && condition.StartsWith("Failed generation iteration"))
            {
                Current.generationFailures++;
            }
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
            {
                return;
            }
            if (condition != null && condition.StartsWith(Prefix))
            {
                return;
            }
            string noise = NoiseMatch(condition, stackTrace);
            if (noise != null)
            {
                int n;
                Filtered.TryGetValue(noise, out n);
                Filtered[noise] = n + 1;
                return;
            }
            string errorType = type == LogType.Exception ? ExceptionType(condition) : (type == LogType.Assert ? "Assert" : "LogError");
            Record(errorType, condition, stackTrace);
        }

        public static void RecordException(Exception e, string where)
        {
            Exception inner = e;
            while (inner is TargetInvocationException && inner.InnerException != null)
            {
                inner = inner.InnerException;
            }
            Record(inner.GetType().Name, inner.GetType().Name + ": " + inner.Message + " [" + where + "]", SafeStackTrace(inner));
        }

        // Exception.StackTrace calls GetParameters() on every frame, and the editor's Mono 2.x aborts the whole
        // process there on a method with a malformed parameter (HasDefault without a Constant row: "mono_class_
        // from_mono_type: implement me 0x00"). Only type and method names are touched here, so the exception
        // still gets recorded; port/monosigcheck.sh finds such methods offline.
        public static string SafeStackTrace(Exception e)
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                foreach (System.Diagnostics.StackFrame f in new System.Diagnostics.StackTrace(e, true).GetFrames() ?? new System.Diagnostics.StackFrame[0])
                {
                    MethodBase m = f.GetMethod();
                    if (m == null)
                    {
                        continue;
                    }
                    sb.Append("  at ").Append(m.DeclaringType != null ? m.DeclaringType.FullName + "." : "").Append(m.Name).Append(" (...)");
                    string file = f.GetFileName();
                    if (!string.IsNullOrEmpty(file))
                    {
                        sb.Append(" in ").Append(file).Append(':').Append(f.GetFileLineNumber());
                    }
                    sb.Append('\n');
                }
            }
            catch (Exception walk)
            {
                sb.Append("  (stack walk failed: ").Append(walk.GetType().Name).Append(")\n");
            }
            return sb.ToString();
        }

        private static void Record(string errorType, string condition, string stackTrace)
        {
            StepResult step = Current;
            string message = FirstLine(condition);
            string stack = TopFrames(stackTrace, 6);
            string category = Classify(errorType, condition, stackTrace);
            // An exception out of the level loading coroutine ends it, and the load with it. Not the editor's
            // constructor refusals: Unity logs those and the AddComponent / load goes on.
            if (step != null && category != Category.PlayMode && errorType != "LogError" && errorType != "Assert" && KillsLoad(stackTrace))
            {
                step.loadCoroutineDied = true;
            }
            string signature = category + "|" + errorType + "|" + message + "|" + FirstLine(stack);
            int frame = Time.frameCount;

            ErrorGroup global;
            if (!Global.TryGetValue(signature, out global))
            {
                global = new ErrorGroup { category = category, type = errorType, message = message, stack = stack, firstStep = step == null ? "-" : step.Key };
                Global[signature] = global;
            }
            global.Add(frame);
            if (step != null && global.stepSet.Add(step.Key))
            {
                global.stepCount++;
                if (global.steps.Count < 20)
                {
                    global.steps.Add(step.Key);
                }
            }
            if (step == null)
            {
                return;
            }
            ErrorGroup local;
            if (!step.bySignature.TryGetValue(signature, out local))
            {
                local = new ErrorGroup { category = category, type = errorType, message = message, stack = stack, firstStep = step.Key };
                step.bySignature[signature] = local;
                step.errors.Add(local);
            }
            local.Add(frame);
            if (step.lastErrorFrame != frame)
            {
                step.lastErrorFrame = frame;
                step.errorFrames++;
            }
        }

        // Did this exception unwind into the level loading coroutine? Not when the engine called the throwing code
        // on the way (Awake / OnEnable via Instantiate, AddComponent, SetActive): Unity logs those and returns.
        private static bool KillsLoad(string stackTrace)
        {
            if (stackTrace == null)
            {
                return false;
            }
            foreach (string frame in Frames(stackTrace))
            {
                if (frame.Contains("LoadNextLevelAsync_CR"))
                {
                    return true;
                }
                if (frame.StartsWith("UnityEngine.Object:Instantiate") || frame.StartsWith("UnityEngine.Object.Instantiate") ||
                    frame.StartsWith("UnityEngine.GameObject:AddComponent") || frame.StartsWith("UnityEngine.GameObject.AddComponent") ||
                    frame.StartsWith("UnityEngine.GameObject:SetActive") || frame.StartsWith("UnityEngine.GameObject.SetActive") ||
                    frame.StartsWith("UnityEngine.GameObject:SendMessage") || frame.StartsWith("UnityEngine.Component:SendMessage"))
                {
                    return false;
                }
            }
            return false;
        }

        private static string NoiseMatch(string condition, string stack)
        {
            foreach (string n in Noise)
            {
                if (condition != null && condition.Contains(n))
                {
                    return n;
                }
            }
            if (stack != null && stack.Contains("dfInputManager.processMouseInput") && condition != null && condition.StartsWith("NullReferenceException"))
            {
                return "dfInputManager.processMouseInput NRE";
            }
            return null;
        }

        private static string ExceptionType(string condition)
        {
            if (string.IsNullOrEmpty(condition))
            {
                return "Exception";
            }
            int colon = condition.IndexOf(':');
            string head = colon > 0 ? condition.Substring(0, colon) : condition;
            return head.IndexOf(' ') < 0 && head.Length < 80 ? head : "Exception";
        }

        // MonoBehaviours whose constructor / field initializers the editor refused ("... is not allowed to be called
        // from a MonoBehaviour constructor ... Called from MonoBehaviour 'X'"): the fields they set stay null in
        // Play Mode, so later errors in their own code are consequences of that, not bugs (in the player they work).
        private static readonly HashSet<string> s_ctorRestricted = new HashSet<string>();
        private static readonly System.Text.RegularExpressions.Regex CalledFrom =
            new System.Text.RegularExpressions.Regex("Called from MonoBehaviour '([^']+)'");

        public static string Classify(string errorType, string condition, string stackTrace)
        {
            foreach (string p in PlayModeOnly)
            {
                if (condition != null && condition.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    System.Text.RegularExpressions.Match m = CalledFrom.Match(condition);
                    if (m.Success)
                    {
                        s_ctorRestricted.Add(m.Groups[1].Value);
                    }
                    return Category.PlayMode;
                }
            }
            if (s_ctorRestricted.Count > 0)
            {
                int n = 0;
                foreach (string frame in Frames(stackTrace))
                {
                    if (s_ctorRestricted.Contains(ShortTypeName(frame)))
                    {
                        return Category.PlayMode;
                    }
                    if (++n >= 3)
                    {
                        break;
                    }
                }
            }
            foreach (string p in PortExceptions)
            {
                if (errorType == p || (condition != null && condition.Contains(p)))
                {
                    return Category.Port;
                }
            }
            // The first frames decide: a facade / shim -> port, a mod's code -> mod. The baker's own pass-through
            // code (ModBaker.Access trampolines, Redirects, hook slots) is port only where the error starts in it;
            // further down a stack it just means a patched or hooked method was on the way.
            int checkedFrames = 0;
            foreach (string frame in Frames(stackTrace))
            {
                if (frame.StartsWith("EtgModSmoke."))
                {
                    // thrown by the test itself, or by the game when the test called it
                    return checkedFrames == 0 ? Category.Smoke : Category.Other;
                }
                if (frame.StartsWith("ModBaker.") || frame.StartsWith("MonoMod.RuntimeDetour.BakedHook"))
                {
                    if (checkedFrames == 0)
                    {
                        return Category.Port;
                    }
                    continue;
                }
                string asm = FrameAssembly(frame);
                if (asm == null)
                {
                    continue;
                }
                if (Array.IndexOf(PortAssemblies, asm) >= 0)
                {
                    return Category.Port;
                }
                if (s_modAssemblies.Contains(asm))
                {
                    return Category.Mod;
                }
                if (++checkedFrames >= 4)
                {
                    break;
                }
            }
            return Category.Other;
        }

        // Frames of a Unity log stack ("Type:Method(args)", "Type.Method () (at file:line)") or of a
        // .NET one ("  at Type.Method (args) [0x0] in file:0"), engine logging frames skipped.
        public static IEnumerable<string> Frames(string stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace))
            {
                yield break;
            }
            foreach (string raw in stackTrace.Split('\n'))
            {
                string s = raw.Trim();
                if (s.StartsWith("at "))
                {
                    s = s.Substring(3);
                }
                if (s.Length == 0 || s.StartsWith("(wrapper") || s.StartsWith("UnityEngine.Debug") || s.StartsWith("UnityEngine.Logger") ||
                    s.StartsWith("UnityEngine.DebugLogHandler") || s.StartsWith("EtgModSmoke.SmokeLog") ||
                    s.StartsWith("BepInEx.Logging.") || s.StartsWith("BepInEx.ManualLogSource"))
                {
                    continue;
                }
                yield return s;
            }
        }

        // "Ns.Outer+Inner:Method(...)" / "Ns.Type.Method ()" -> "Inner" / "Type"
        private static string ShortTypeName(string frame)
        {
            int paren = frame.IndexOf('(');
            string head = (paren > 0 ? frame.Substring(0, paren) : frame).Trim();
            int colon = head.IndexOf(':');
            string type = colon > 0 ? head.Substring(0, colon) : (head.LastIndexOf('.') > 0 ? head.Substring(0, head.LastIndexOf('.')) : head);
            int cut = Math.Max(type.LastIndexOf('.'), type.LastIndexOf('+'));
            return cut >= 0 ? type.Substring(cut + 1) : type;
        }

        private static string FrameAssembly(string frame)
        {
            int paren = frame.IndexOf('(');
            string head = (paren > 0 ? frame.Substring(0, paren) : frame).Trim();
            int colon = head.IndexOf(':');
            string type;
            if (colon > 0)
            {
                type = head.Substring(0, colon);
            }
            else
            {
                int dot = head.LastIndexOf('.');
                type = dot > 0 ? head.Substring(0, dot) : head;
            }
            int bracket = type.IndexOf('[');
            if (bracket > 0)
            {
                type = type.Substring(0, bracket);
            }
            // Mono writes nested types with '/' in some traces, Type.FullName with '+'.
            type = type.Replace('/', '+');
            string asm;
            if (s_typeAssembly.TryGetValue(type, out asm))
            {
                return asm;
            }
            // compiler-generated nested classes (iterators, closures) of a known type
            int plus = type.IndexOf('+');
            if (plus > 0 && s_typeAssembly.TryGetValue(type.Substring(0, plus), out asm))
            {
                return asm;
            }
            // only for mods: WebShim has stand-ins in the UnityEngine namespace, the engine's own frames aren't scanned
            int dot2 = type.IndexOf('.');
            if (dot2 > 0 && s_namespaceRoots.TryGetValue(type.Substring(0, dot2), out asm) && asm != null && s_modAssemblies.Contains(asm))
            {
                return asm;
            }
            return null;
        }

        private static string TopFrames(string stackTrace, int n)
        {
            StringBuilder sb = new StringBuilder();
            int i = 0;
            foreach (string f in Frames(stackTrace))
            {
                if (i++ >= n)
                {
                    break;
                }
                sb.Append(f.Length > 200 ? f.Substring(0, 200) : f).Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        public static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }
            int nl = s.IndexOf('\n');
            string line = (nl >= 0 ? s.Substring(0, nl) : s).Trim();
            return line.Length > 300 ? line.Substring(0, 300) + "..." : line;
        }
    }
}
