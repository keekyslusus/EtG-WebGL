using System.Text;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;

namespace ShaderDiff;

/// <summary>A CGPROGRAM block of a decompiled .shader, addressed the way the dump addresses programs.</summary>
public sealed record CgProgram(int SubShader, int Pass, string Source, int Line);

public static class ShaderText
{
    /// <summary>Splits USCSandbox output into CGPROGRAM blocks keyed by (subshader, pass); pass numbering
    /// counts UsePass lines like USCSandbox does.</summary>
    public static List<CgProgram> Programs(string text)
    {
        var result = new List<CgProgram>();
        var lines = text.Split('\n');
        int sub = -1, pass = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t == "SubShader {") { sub++; pass = -1; }
            else if (t == "Pass {" || t.StartsWith("UsePass ")) pass++;
            else if (t == "CGPROGRAM")
            {
                var sb = new StringBuilder();
                var start = i + 1;
                for (i++; i < lines.Length && lines[i].Trim() != "ENDCG"; i++)
                    sb.Append(lines[i]).Append('\n');
                result.Add(new CgProgram(sub, pass, sb.ToString(), start + 1));
            }
        }
        return result;
    }
}

/// <summary>Compiles decompiled CG the way Unity 2017.4 does for d3d11: HLSLSupport.cginc and
/// UnityShaderVariables.cginc auto-included, platform defines, the variant's keywords defined to 1.</summary>
public sealed class HlslCompiler(string cgIncludes)
{
    // Unity 2017.4 d3d11 platform/tier defines (see "Compiled code" in the inspector of any shader)
    static readonly string[] PlatformDefines =
    [
        "SHADER_API_D3D11", "SHADER_API_DESKTOP", "UNITY_ENABLE_REFLECTION_BUFFERS", "UNITY_USE_DITHER_MASK_FOR_ALPHABLENDED_SHADOWS",
        "UNITY_PBS_USE_BRDF1", "UNITY_SPECCUBE_BOX_PROJECTION", "UNITY_SPECCUBE_BLENDING", "UNITY_ENABLE_DETAIL_NORMALMAP",
        "UNITY_LIGHT_PROBE_PROXY_VOLUME", "UNITY_LIGHTMAP_FULL_HDR",
    ];

    public sealed record Result(byte[]? Bytecode, string Messages);

    public Result Compile(CgProgram program, string shaderName, string[] keywords, string stage)
    {
        var macros = PlatformDefines.Select(d => new ShaderMacro(d, "1"))
            .Append(new ShaderMacro("SHADER_TARGET", "25"))
            .Append(new ShaderMacro("UNITY_VERSION", "201740"))
            .Concat(keywords.Select(k => new ShaderMacro(k, "1")))
            .Append(new ShaderMacro(null!, null!)) // D3DCompile reads the array up to a null entry
            .ToArray();
        var source = "#include \"HLSLSupport.cginc\"\n#include \"UnityShaderVariables.cginc\"\n"
                     + $"#line {program.Line} \"{shaderName.Replace('\\', '/')}.shader\"\n" + program.Source;
        using var include = new IncludeHandler(cgIncludes);
        var entry = stage == "vs" ? "vert" : "frag";
        var profile = stage == "vs" ? "vs_4_0" : "ps_4_0";
        var hr = Compiler.Compile(source, macros, include, entry, shaderName, profile, ShaderFlags.EnableBackwardsCompatibility,
            out Blob? blob, out Blob? errors);
        var messages = errors?.AsString() ?? "";
        errors?.Dispose();
        if (hr.Failure || blob == null)
            return new Result(null, messages.Length > 0 ? messages : hr.ToString());
        var bytes = blob.AsBytes();
        blob.Dispose();
        return new Result(bytes, messages);
    }

    /// <summary>Compiles a stand-alone helper shader (the harness pass-through vertex shader).</summary>
    public static byte[] CompileHelper(string source, string entry, string profile)
    {
        var hr = Compiler.Compile(source, [new ShaderMacro(null!, null!)], null!, entry, "helper", profile, ShaderFlags.None, out Blob? blob, out Blob? errors);
        if (hr.Failure || blob == null)
            throw new InvalidOperationException("helper shader: " + errors?.AsString() + "\n" + source);
        errors?.Dispose();
        var bytes = blob.AsBytes();
        blob.Dispose();
        return bytes;
    }

    public static string Disassemble(byte[] bytecode)
    {
        unsafe
        {
            fixed (byte* p = bytecode)
            {
                using var blob = Compiler.Disassemble((nint)p, (nuint)bytecode.Length, DisasmFlags.None, null!);
                return blob.AsString();
            }
        }
    }

    sealed class IncludeHandler(string dir) : CallbackBase, Include
    {
        public Stream Open(IncludeType type, string fileName, Stream? parentStream)
        {
            var path = Path.Combine(dir, fileName);
            return new MemoryStream(File.ReadAllBytes(path));
        }

        public void Close(Stream stream) => stream.Dispose();
    }
}
