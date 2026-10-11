using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShaderDiff;

// tools/uscs/dump/<shader>/variants.json, written by USCSandbox --dump (VariantDumper.cs)
public sealed class DumpVariant
{
    public int SubShader { get; set; }
    public int Pass { get; set; }
    public string PassName { get; set; } = "";
    public string Stage { get; set; } = "";
    public string[] Keywords { get; set; } = [];
    public string? Dxbc { get; set; }
    public DumpCBuffer[] Cbuffers { get; set; } = [];
    public DumpBinding[] CbufferBindings { get; set; } = [];
    public DumpBinding[] Buffers { get; set; } = [];
    public DumpBinding[] Uavs { get; set; } = [];
    public DumpTexture[] Textures { get; set; } = [];
    public DumpSampler[] Samplers { get; set; } = [];

    [JsonIgnore] public int Index { get; set; }
}

public sealed class DumpCBuffer
{
    public string Name { get; set; } = "";
    public int Size { get; set; }
    public DumpParam[] Params { get; set; } = [];
}

public sealed class DumpParam
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "Float";
    public int Rows { get; set; }
    public int Columns { get; set; }
    public bool IsMatrix { get; set; }
    public int ArraySize { get; set; }
    public int Offset { get; set; }

    /// <summary>Bytes the parameter covers in its constant buffer (D3D packing: arrays and matrix columns start on a register).</summary>
    public int ByteSize
    {
        get
        {
            if (IsMatrix)
            {
                // Unity reports matrices with rows = cols = dimension; stored as registers of 16 bytes
                var regs = Columns * Math.Max(1, ArraySize);
                return (regs - 1) * 16 + Rows * 4;
            }
            var elem = Rows * Columns * 4;
            return ArraySize > 0 ? (ArraySize - 1) * 16 + elem : elem;
        }
    }
}

public sealed class DumpBinding
{
    public string Name { get; set; } = "";
    public int Slot { get; set; }
}

public sealed class DumpTexture
{
    public string Name { get; set; } = "";
    public int Slot { get; set; }
    public int SamplerSlot { get; set; }
    public int Dim { get; set; }
    public bool MultiSampled { get; set; }
}

public sealed class DumpSampler
{
    public int Slot { get; set; }
    public uint State { get; set; }
}

public static class DumpReader
{
    static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static List<DumpVariant> Read(string dir)
    {
        var list = JsonSerializer.Deserialize<List<DumpVariant>>(File.ReadAllText(Path.Combine(dir, "variants.json")), Options)!;
        for (var i = 0; i < list.Count; i++)
            list[i].Index = i;
        return list;
    }
}
