using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11.Shader;

namespace ShaderDiff;

public enum TexDim { Tex2D = 2, Tex3D = 3, Cube = 4, Tex2DArray = 5, CubeArray = 6 }

public sealed record CbVar(string Name, int Offset, int Size, ValueKind Kind);
public sealed record CbLayout(string Name, int Slot, int Size, List<CbVar> Vars);
public sealed record TexBinding(string Name, int Slot, TexDim Dim);

/// <summary>Where a program expects its constants and textures: from the dump JSON for the original
/// (its RDEF is stripped), from D3DReflect for the recompiled decompilation.</summary>
public sealed class ProgramLayout
{
    public List<CbLayout> CBuffers { get; } = [];
    public List<TexBinding> Textures { get; } = [];
    public List<string> Unsupported { get; } = [];

    public static ProgramLayout FromDump(DumpVariant v)
    {
        var layout = new ProgramLayout();
        foreach (var cb in v.Cbuffers)
        {
            var binding = v.CbufferBindings.FirstOrDefault(b => b.Name == cb.Name);
            if (binding == null)
                continue;
            var vars = cb.Params.Select(p => new CbVar(p.Name, p.Offset, p.ByteSize, KindOf(p.Type))).ToList();
            layout.CBuffers.Add(new CbLayout(cb.Name, binding.Slot, cb.Size, vars));
        }
        foreach (var t in v.Textures)
            layout.Textures.Add(new TexBinding(t.Name, t.Slot, (TexDim)t.Dim));
        foreach (var b in v.Buffers)
            layout.Unsupported.Add("buffer " + b.Name);
        foreach (var u in v.Uavs)
            layout.Unsupported.Add("uav " + u.Name);
        return layout;
    }

    public static ProgramLayout FromReflection(byte[] bytecode)
    {
        var layout = new ProgramLayout();
        using var refl = Compiler.Reflect<ID3D11ShaderReflection>(bytecode);
        var slots = new Dictionary<string, int>();
        foreach (var r in refl.BoundResources)
        {
            switch (r.Type)
            {
                case ShaderInputType.ConstantBuffer:
                    slots[r.Name] = (int)r.BindPoint;
                    break;
                case ShaderInputType.Texture:
                    var dim = r.Dimension switch
                    {
                        ShaderResourceViewDimension.Texture2D => TexDim.Tex2D,
                        ShaderResourceViewDimension.Texture3D => TexDim.Tex3D,
                        ShaderResourceViewDimension.TextureCube => TexDim.Cube,
                        ShaderResourceViewDimension.Texture2DArray => TexDim.Tex2DArray,
                        ShaderResourceViewDimension.TextureCubeArray => TexDim.CubeArray,
                        _ => (TexDim)0,
                    };
                    if (dim == 0)
                        layout.Unsupported.Add($"texture {r.Name} ({r.Dimension})");
                    else
                        layout.Textures.Add(new TexBinding(r.Name, (int)r.BindPoint, dim));
                    break;
                case ShaderInputType.Sampler:
                    break;
                default:
                    layout.Unsupported.Add($"{r.Type} {r.Name}");
                    break;
            }
        }
        foreach (var cb in refl.ConstantBuffers)
        {
            var desc = cb.Description;
            if (!slots.TryGetValue(desc.Name, out var slot))
                continue;
            var vars = new List<CbVar>();
            foreach (var v in cb.Variables)
            {
                var vd = v.Description;
                var kind = v.VariableType.Description.Type switch
                {
                    ShaderVariableType.Int => ValueKind.Int,
                    ShaderVariableType.UInt => ValueKind.UInt,
                    ShaderVariableType.Bool => ValueKind.Bool,
                    _ => ValueKind.Float,
                };
                vars.Add(new CbVar(vd.Name, (int)vd.StartOffset, (int)vd.Size, kind));
            }
            layout.CBuffers.Add(new CbLayout(desc.Name, slot, (int)desc.Size, vars));
        }
        return layout;
    }

    static ValueKind KindOf(string unityType) => unityType switch
    {
        "Int" or "Short" => ValueKind.Int,
        "UInt" => ValueKind.UInt,
        "Bool" => ValueKind.Bool,
        _ => ValueKind.Float,
    };

    /// <summary>Variable kinds by name; the original's decide what bits both programs get.</summary>
    public Dictionary<string, ValueKind> Kinds() =>
        CBuffers.SelectMany(c => c.Vars).GroupBy(v => v.Name).ToDictionary(g => g.Key, g => g.First().Kind);
}
