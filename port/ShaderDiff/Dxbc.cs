using System.Text;

namespace ShaderDiff;

public enum CompType { Unknown = 0, UInt = 1, SInt = 2, Float = 3 }

public sealed record SigElement(string Semantic, int SemanticIndex, int SystemValue, CompType Type, int Register, int Mask, int RwMask)
{
    public string Key => $"{Semantic.ToUpperInvariant()}{SemanticIndex}";
    public int FirstComponent => System.Numerics.BitOperations.TrailingZeroCount(Mask);
    public int ComponentCount => System.Numerics.BitOperations.PopCount((uint)Mask);
    public bool IsSV(string name) => Semantic.Equals(name, StringComparison.OrdinalIgnoreCase);
    /// <summary>System values the pipeline generates (not fed from a vertex buffer / previous stage).</summary>
    public bool IsGenerated => Semantic.ToUpperInvariant() is "SV_VERTEXID" or "SV_INSTANCEID" or "SV_ISFRONTFACE"
        or "SV_PRIMITIVEID" or "SV_SAMPLEINDEX" or "SV_COVERAGE";
    public override string ToString() => $"{Semantic}{SemanticIndex}";
}

/// <summary>Reads the parts of a DXBC container ShaderDiff needs: input/output signatures and the
/// shader model of the SHDR/SHEX chunk. Unity strips RDEF (reflection), so these are all we get from
/// the original bytecode; resource layout comes from the dump JSON instead.</summary>
public sealed class Dxbc
{
    public List<SigElement> Inputs { get; } = [];
    public List<SigElement> Outputs { get; } = [];
    public int ProgramType { get; private set; } = -1; // 0 = ps, 1 = vs
    public byte[] Bytes { get; }

    public Dxbc(byte[] bytes)
    {
        Bytes = bytes;
        if (Encoding.ASCII.GetString(bytes, 0, 4) != "DXBC")
            throw new InvalidDataException("not a DXBC container");
        var count = BitConverter.ToInt32(bytes, 28);
        for (var i = 0; i < count; i++)
        {
            var off = BitConverter.ToInt32(bytes, 32 + i * 4);
            var fourCc = Encoding.ASCII.GetString(bytes, off, 4);
            var data = off + 8;
            switch (fourCc)
            {
                case "ISGN": ReadSignature(bytes, data, Inputs); break;
                case "OSGN": ReadSignature(bytes, data, Outputs); break;
                case "SHDR":
                case "SHEX":
                    ProgramType = BitConverter.ToInt32(bytes, data) >> 16;
                    break;
            }
        }
    }

    static void ReadSignature(byte[] b, int start, List<SigElement> list)
    {
        var n = BitConverter.ToInt32(b, start);
        for (var i = 0; i < n; i++)
        {
            var e = start + 8 + i * 24;
            var nameOff = BitConverter.ToInt32(b, e);
            var end = Array.IndexOf(b, (byte)0, start + nameOff);
            var name = Encoding.ASCII.GetString(b, start + nameOff, end - start - nameOff);
            list.Add(new SigElement(name,
                BitConverter.ToInt32(b, e + 4),
                BitConverter.ToInt32(b, e + 8),
                (CompType)BitConverter.ToInt32(b, e + 12),
                BitConverter.ToInt32(b, e + 16),
                b[e + 20], b[e + 21]));
        }
    }
}
