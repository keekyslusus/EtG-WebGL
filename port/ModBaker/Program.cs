using System;
using System.IO;
using System.Linq;

namespace EtgModBaker;

/// <summary>
/// Bakes BepInEx mods into the game's assemblies so they can run where nothing can be patched at
/// runtime (IL2CPP / WebGL): HarmonyX itself builds every patched method body (prefixes, postfixes,
/// finalizers, transpilers, IL manipulators - exactly like it would in the game) and the result is
/// written into the game DLLs with Cecil. MonoMod Hooks become static trampolines.
///
/// Usage:
///   ModBaker bake &lt;game Managed dir&gt; &lt;output dir&gt; &lt;facade dir&gt; &lt;plugin dir&gt;...   (plugin dirs in load order)
///   ModBaker inventory &lt;mod.dll&gt;...
///   ModBaker resources &lt;dll dir&gt; &lt;output dir&gt;   (IL2CPP *-resources.dat files, see ResourceFiles)
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length >= 2 && args[0] == "inventory")
        {
            return Inventory.Run(args.Skip(1), new[] { "0Harmony", "MonoMod", "BepInEx", "Mono.Cecil", "HarmonyX", "Ionic", "Newtonsoft", "System.Xml" });
        }
        if (args.Length == 3 && args[0] == "resources")
        {
            return ResourceFiles.Write(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        }
        if (args.Length >= 3 && args[0] == "scan")
        {
            return Inventory.Run(new[] { args[1] }, args.Skip(2).ToArray(), showMethods: true);
        }
        if (args.Length >= 5 && args[0] == "bake")
        {
            try
            {
                return new Baker(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), args.Skip(4).Select(Path.GetFullPath).ToArray()).Run();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 1;
            }
        }
        Console.Error.WriteLine("Usage: ModBaker bake <game Managed dir> <output dir> <facade dir> <plugin dir>...\n       ModBaker inventory <mod.dll>...");
        return 2;
    }
}
