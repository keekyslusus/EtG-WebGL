using System;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;

namespace EtgModBaker;

/// <summary>
/// IL2CPP serves Assembly.GetManifestResourceStream from &lt;data dir&gt;/Resources/&lt;Assembly&gt;.dll-resources.dat
/// (libil2cpp icalls System.Reflection/Assembly.cpp), but Unity 2017's WebGL packager leaves those
/// files out, so on WebGL every embedded resource is null - MtG API's item ID maps, the mods' sprites.
/// This writes them in il2cpp's format: int32 size of the record block, int32 count, per resource
/// int32 data size + int32 name size + UTF-8 name; then the data of each resource in that order.
/// The mod pack puts them at /Il2CppData/Resources (the data dir: metadata is /Il2CppData/Metadata).
/// </summary>
internal static class ResourceFiles
{
    public static int Write(string dllDir, string outDir)
    {
        Directory.CreateDirectory(outDir);
        foreach (string dll in Directory.GetFiles(dllDir, "*.dll"))
        {
            using var module = ModuleDefinition.ReadModule(dll);
            var resources = module.Resources.OfType<EmbeddedResource>().ToList();
            if (resources.Count == 0) continue;

            var records = new MemoryStream();
            var w = new BinaryWriter(records);
            w.Write(resources.Count);
            var datas = resources.Select(r => r.GetResourceData()).ToList();
            for (int i = 0; i < resources.Count; i++)
            {
                byte[] name = Encoding.UTF8.GetBytes(resources[i].Name);
                w.Write(datas[i].Length);
                w.Write(name.Length);
                w.Write(name);
            }
            w.Flush();

            // The file is named after the image il2cpp knows: the assembly's (patched) file name.
            string path = Path.Combine(outDir, Path.GetFileName(dll) + "-resources.dat");
            using (var f = new BinaryWriter(File.Create(path)))
            {
                f.Write((int)records.Length);
                f.Write(records.ToArray());
                foreach (var d in datas) f.Write(d);
            }
            Console.WriteLine($"   {Path.GetFileName(path)}: {resources.Count} resources, {new FileInfo(path).Length / 1024} KB");
        }
        return 0;
    }
}
