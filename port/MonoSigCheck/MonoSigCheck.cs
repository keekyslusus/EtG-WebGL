// Offline check: does the editor's Mono (2.x, the runtime Play Mode uses) survive reflection over every method
// of the patched / mod-baked DLLs? Exception.get_StackTrace calls MethodBase.GetParameters() on each frame, and
// Mono 2.x aborts the whole editor (g_assert "mono_class_from_mono_type: implement me 0x00") when a parameter has
// the HasDefault flag but no Constant row. Run with Unity's Mono/bin/mono.exe (see port/monosigcheck.sh).
//
// "<assembly> <token>" of every method goes to a progress file before the method is touched; when Mono aborts,
// the driver script adds the last one to the skip file and runs again, so one bad method does not end the run.
//
// Usage: mono MonoSigCheck.exe <progress file> <skip file> <dll>... (references resolve from the dlls' folders
// and from MONO_PATH)
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

static class MonoSigCheck
{
    static readonly List<string> SearchDirs = new List<string>();

    static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: MonoSigCheck <progress file> <skip file> <dll>...");
            return 2;
        }
        string progressPath = args[0];
        var skip = new Dictionary<string, bool>();
        if (File.Exists(args[1]))
            foreach (string line in File.ReadAllLines(args[1]))
                if (line.Trim().Length > 0)
                    skip[line.Trim()] = true;

        for (int i = 2; i < args.Length; i++)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(args[i]));
            if (!SearchDirs.Contains(dir))
                SearchDirs.Add(dir);
        }
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;

        int methods = 0, failures = 0;
        using (var progress = new StreamWriter(progressPath, false))
        {
            progress.AutoFlush = true;
            for (int i = 2; i < args.Length; i++)
            {
                Assembly asm;
                try { asm = Assembly.LoadFrom(args[i]); }
                catch (Exception e)
                {
                    Console.WriteLine("LOAD-FAIL " + Path.GetFileName(args[i]) + ": " + e.Message);
                    failures++;
                    continue;
                }
                string asmName = Path.GetFileName(args[i]);
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }

                foreach (Type t in types)
                {
                    if (t == null)
                        continue;
                    MethodBase[] members;
                    try
                    {
                        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                            | BindingFlags.Static | BindingFlags.DeclaredOnly;
                        var list = new List<MethodBase>();
                        list.AddRange(t.GetMethods(all));
                        list.AddRange(t.GetConstructors(all));
                        members = list.ToArray();
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("TYPE-FAIL " + asmName + " " + t.FullName + ": " + e.Message);
                        failures++;
                        continue;
                    }
                    foreach (MethodBase m in members)
                    {
                        string key = asmName + " 0x" + m.MetadataToken.ToString("x8");
                        if (skip.ContainsKey(key))
                            continue;
                        progress.WriteLine(key + " " + t.FullName + "::" + m.Name);
                        methods++;
                        try { Describe(m); }
                        catch (Exception e)
                        {
                            // Managed failures don't kill the editor, but they would hide the real stack trace.
                            Console.WriteLine("REFLECT-FAIL " + key + " " + t.FullName + "::" + m.Name + ": "
                                + e.GetType().Name + ": " + e.Message);
                            failures++;
                        }
                    }
                }
            }
            progress.WriteLine("DONE");
        }
        Console.WriteLine("checked " + methods + " methods, " + failures + " managed failures");
        return 0;
    }

    // Mirrors what Exception.GetFullNameForStackTrace touches for a frame.
    static void Describe(MethodBase m)
    {
        var sb = new StringBuilder();
        ParameterInfo[] p = m.GetParameters();
        sb.Append(m.DeclaringType.ToString()).Append('.').Append(m.Name);
        if (m.IsGenericMethod)
            foreach (Type g in m.GetGenericArguments())
                sb.Append(g.Name);
        foreach (ParameterInfo pi in p)
        {
            Type pt = pi.ParameterType;
            if (pt.IsClass && pt.Namespace != String.Empty)
                sb.Append(pt.Namespace);
            sb.Append(pt.Name).Append(pi.Name);
            if ((pi.Attributes & ParameterAttributes.HasDefault) != 0)
                sb.Append(pi.DefaultValue);
        }
    }

    static Assembly Resolve(object sender, ResolveEventArgs e)
    {
        string name = new AssemblyName(e.Name).Name;
        foreach (string dir in SearchDirs)
        {
            string path = Path.Combine(dir, name + ".dll");
            if (File.Exists(path))
                return Assembly.LoadFrom(path);
        }
        return null;
    }
}
