using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace ShaderDiff;

/// <summary>Per-sample output values: key = semantic + index ("TEXCOORD1", "SV_TARGET0"), value = raw 32-bit
/// words, 4 per sample, relative to the element's first component.</summary>
public sealed class Outputs : Dictionary<string, uint[]>;

/// <summary>
/// Runs one vertex or pixel program on D3D11 WARP over <see cref="Samples"/> random inputs.
/// Vertex programs: points through the input assembler, outputs captured with stream output (no rasterizer).
/// Pixel programs: one point per pixel of a 16x16 float render target; a generated pass-through vertex shader
/// feeds the interpolants (points are not interpolated, so every pixel gets exactly its vertex's values).
/// Discarded pixels keep the clear value <see cref="Sentinel"/>.
/// </summary>
public sealed class Gpu : IDisposable
{
    public const int W = 16, H = 16, Samples = W * H;
    public const uint Sentinel = 0xFFC0DEAD; // a NaN no shader produces

    readonly ID3D11Device _dev;
    readonly ID3D11DeviceContext _ctx;
    readonly ID3D11SamplerState _sampler, _comparisonSampler;
    readonly ID3D11RasterizerState _raster;
    readonly Dictionary<string, (byte[] Bytecode, ID3D11VertexShader Shader)> _feeders = [];

    public Gpu()
    {
        D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.None,
            [FeatureLevel.Level_11_0], out _dev!, out _ctx!).CheckError();
        _sampler = _dev.CreateSamplerState(SamplerDescription.PointWrap);
        _comparisonSampler = _dev.CreateSamplerState(new SamplerDescription(Filter.ComparisonMinMagMipPoint, TextureAddressMode.Wrap,
            0, 1, ComparisonFunction.LessEqual, 0, float.MaxValue));
        _raster = _dev.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { DepthClipEnable = false });
    }

    public void Dispose()
    {
        foreach (var f in _feeders.Values)
            f.Shader.Dispose();
        _raster.Dispose();
        _sampler.Dispose();
        _comparisonSampler.Dispose();
        _ctx.Dispose();
        _dev.Dispose();
    }

    // ---------------------------------------------------------------- vertex programs

    public Outputs RunVertex(byte[] bytecode, Dxbc sig, ProgramLayout layout, Dictionary<string, ValueKind> kinds,
        TrialData data, IReadOnlyList<SigElement> vertexInputs)
    {
        _ctx.ClearState();
        // one 16-byte slot per input semantic of either program, same order for both
        var stride = vertexInputs.Count * 16;
        var vb = new uint[Samples * vertexInputs.Count * 4];
        for (var s = 0; s < Samples; s++)
            for (var e = 0; e < vertexInputs.Count; e++)
                for (var c = 0; c < 4; c++)
                    vb[(s * vertexInputs.Count + e) * 4 + c] = InputWord(data, vertexInputs[e], s, c);

        var elements = new List<InputElementDescription>();
        foreach (var el in sig.Inputs.Where(i => !i.IsGenerated))
        {
            var idx = IndexOfKey(vertexInputs, el.Key);
            elements.Add(new InputElementDescription(el.Semantic, (uint)el.SemanticIndex, FormatOf(el.Type), (uint)(idx * 16), 0));
        }

        using var vs = _dev.CreateVertexShader(bytecode);
        using var layoutObj = elements.Count > 0 ? _dev.CreateInputLayout(elements.ToArray(), bytecode) : null;
        using var vbuf = vertexInputs.Count > 0 ? CreateBuffer(vb, BindFlags.VertexBuffer) : null;

        // stream output: one entry per component (StartComponent counts from the semantic, not the register)
        var decl = new List<SODeclarationEntry>();
        var outOffsets = new List<(SigElement El, int Comp, int Word)>();
        foreach (var el in sig.Outputs)
            for (var c = 0; c < 4; c++)
                if ((el.Mask & (1 << c)) != 0)
                {
                    outOffsets.Add((el, c - el.FirstComponent, decl.Count));
                    decl.Add(new SODeclarationEntry { Stream = 0, SemanticName = el.Semantic, SemanticIndex = (uint)el.SemanticIndex, StartComponent = (byte)(c - el.FirstComponent), ComponentCount = 1, OutputSlot = 0 });
                }
        var soStride = decl.Count * 4;
        ID3D11GeometryShader gs;
        unsafe
        {
            fixed (byte* p = bytecode)
                gs = _dev.CreateGeometryShaderWithStreamOutput((IntPtr)p, (nuint)bytecode.Length, decl.ToArray(), (uint)decl.Count,
                    [(uint)soStride], 1, 0xFFFFFFFF /* D3D11_SO_NO_RASTERIZED_STREAM */, null);
        }
        using var _gs = gs;
        using var so = _dev.CreateBuffer(new BufferDescription((uint)(soStride * Samples), BindFlags.StreamOutput));

        var cleanup = BindResources(layout, kinds, data, vertex: true, ComparisonSlots(bytecode));
        try
        {
            _ctx.IASetPrimitiveTopology(PrimitiveTopology.PointList);
            if (layoutObj != null)
            {
                _ctx.IASetInputLayout(layoutObj);
                _ctx.IASetVertexBuffers(0, 1, [vbuf!], [(uint)stride], [0u]);
            }
            _ctx.VSSetShader(vs);
            _ctx.GSSetShader(gs);
            _ctx.SOSetTargets(1, [so], [0u]);
            _ctx.Draw(Samples, 0);
            _ctx.SOSetTargets(0, [], []);
            var words = ReadBuffer(so, soStride * Samples);
            var result = new Outputs();
            foreach (var (el, comp, word) in outOffsets)
            {
                if (!result.TryGetValue(el.Key, out var arr))
                    result[el.Key] = arr = new uint[Samples * 4];
                for (var s = 0; s < Samples; s++)
                    arr[s * 4 + comp] = words[s * decl.Count + word];
            }
            return result;
        }
        finally
        {
            foreach (var d in cleanup)
                d.Dispose();
        }
    }

    // ---------------------------------------------------------------- pixel programs

    public Outputs RunPixel(byte[] bytecode, Dxbc sig, ProgramLayout layout, Dictionary<string, ValueKind> kinds, TrialData data)
    {
        _ctx.ClearState();
        var (feederCode, feeder, fed) = Feeder(sig.Inputs);
        // per sample: one uint4 per fed element (first = SV_Position)
        var buf = new uint[Samples * fed.Count * 4];
        for (var s = 0; s < Samples; s++)
            for (var e = 0; e < fed.Count; e++)
                for (var c = 0; c < 4; c++)
                    buf[(s * fed.Count + e) * 4 + c] = fed[e] == null ? PositionWord(data, s, c) : InputWord(data, fed[e]!, s, c);
        using var dataBuf = CreateBuffer(buf, BindFlags.ShaderResource);
        using var dataView = _dev.CreateShaderResourceView(dataBuf,
            new ShaderResourceViewDescription(dataBuf, Format.R32G32B32A32_UInt, 0, (uint)(Samples * fed.Count)));

        using var ps = _dev.CreatePixelShader(bytecode);
        var targets = sig.Outputs.Where(o => o.IsSV("SV_Target")).ToList();
        if (sig.Outputs.Any(o => !o.IsSV("SV_Target")))
            throw new NotSupportedException("pixel output " + string.Join(", ", sig.Outputs.Where(o => !o.IsSV("SV_Target"))));
        var rtCount = targets.Count == 0 ? 0 : targets.Max(t => t.Register) + 1;
        var textures = new List<ID3D11Texture2D>();
        var views = new List<ID3D11RenderTargetView>();
        var cleanup = BindResources(layout, kinds, data, vertex: false, ComparisonSlots(bytecode));
        try
        {
            for (var i = 0; i < rtCount; i++)
            {
                var type = targets.FirstOrDefault(t => t.Register == i)?.Type ?? CompType.Float;
                var tex = _dev.CreateTexture2D(new Texture2DDescription(FormatOf(type), W, H, 1, 1, BindFlags.RenderTarget));
                textures.Add(tex);
                var rtv = _dev.CreateRenderTargetView(tex);
                views.Add(rtv);
                var sentinel = BitConverter.UInt32BitsToSingle(Sentinel);
                // ClearRenderTargetView converts through float; for a float target the NaN payload survives
                _ctx.ClearRenderTargetView(rtv, new Color4(sentinel, sentinel, sentinel, sentinel));
            }
            _ctx.IASetPrimitiveTopology(PrimitiveTopology.PointList);
            _ctx.VSSetShader(feeder);
            _ctx.VSSetShaderResources(0, 1, [dataView]);
            _ctx.PSSetShader(ps);
            _ctx.RSSetState(_raster);
            _ctx.RSSetViewport(0, 0, W, H, 0, 1);
            _ctx.OMSetRenderTargets(views.ToArray(), null);
            _ctx.Draw(Samples, 0);

            var result = new Outputs();
            foreach (var t in targets)
            {
                var words = ReadTexture(textures[t.Register]);
                var arr = new uint[Samples * 4];
                for (var s = 0; s < Samples; s++)
                    for (var c = 0; c < 4; c++)
                        arr[s * 4 + c] = words[s * 4 + c];
                result[t.Key] = arr;
            }
            return result;
        }
        finally
        {
            foreach (var v in views) v.Dispose();
            foreach (var t in textures) t.Dispose();
            foreach (var d in cleanup) d.Dispose();
        }
    }

    /// <summary>A vertex shader whose output signature is the pixel program's input signature (same
    /// registers and masks), reading the values from a buffer by SV_VertexID. Element null = SV_Position.</summary>
    (byte[] Code, ID3D11VertexShader Shader, List<SigElement?> Fed) Feeder(List<SigElement> psInputs)
    {
        var inputs = psInputs.Where(i => !i.IsGenerated).OrderBy(i => i.Register).ThenBy(i => i.FirstComponent).ToList();
        var fed = new List<SigElement?> { null };
        var sb = new StringBuilder("Buffer<uint4> D : register(t0);\nstruct O {\n");
        var body = new StringBuilder();
        var hasPos = false;
        var n = 0;
        foreach (var el in inputs)
        {
            if (el.IsSV("SV_Position"))
            {
                sb.Append("  float4 pos : SV_Position;\n");
                hasPos = true;
                continue;
            }
            fed.Add(el);
            var type = el.Type switch { CompType.UInt => "uint", CompType.SInt => "int", _ => "float" };
            var cast = el.Type switch { CompType.UInt => "", CompType.SInt => "asint", _ => "asfloat" };
            var count = el.ComponentCount;
            var swz = "xyzw"[..count];
            sb.Append($"  {(el.Type == CompType.Float ? "" : "nointerpolation ")}{type}{(count > 1 ? count : "")} e{n} : {el.Semantic}{el.SemanticIndex};\n");
            body.Append($"  o.e{n} = {cast}(D[id * K + {fed.Count - 1}].{swz});\n");
            n++;
        }
        if (!hasPos)
            sb.Append("  float4 pos : SV_Position;\n");
        sb.Append("};\n");
        sb.Append($"static const uint K = {fed.Count};\n");
        sb.Append("O main(uint id : SV_VertexID) {\n  O o;\n  o.pos = asfloat(D[id * K]);\n").Append(body).Append("  return o;\n}\n");
        var src = sb.ToString();
        if (!_feeders.TryGetValue(src, out var f))
        {
            var code = HlslCompiler.CompileHelper(src, "main", "vs_4_0");
            // the generated outputs must land on the same registers/masks the pixel program reads
            var outSig = new Dxbc(code).Outputs;
            foreach (var el in inputs)
            {
                var o = outSig.FirstOrDefault(x => x.Key == el.Key);
                if (o == null || o.Register != el.Register || (o.Mask & el.Mask) != el.Mask)
                    throw new NotSupportedException($"harness: cannot reproduce pixel input {el} (r{el.Register} mask {el.Mask:X}) - got r{o?.Register} mask {o?.Mask:X}");
            }
            f = (code, _dev.CreateVertexShader(code));
            _feeders[src] = f;
        }
        return (f.Bytecode, f.Shader, fed);
    }

    // ---------------------------------------------------------------- shared

    internal static uint InputWord(TrialData data, SigElement el, int sample, int comp) => el.Type switch
    {
        CompType.UInt => data.Word("in:" + el.Key, sample * 4 + comp, ValueKind.UInt),
        CompType.SInt => data.Word("in:" + el.Key, sample * 4 + comp, ValueKind.Int),
        _ => BitConverter.SingleToUInt32Bits(data.Float("in:" + el.Key, sample * 4 + comp)),
    };

    /// <summary>Clip-space position of the pixel center for sample s, with random depth and w.</summary>
    internal static uint PositionWord(TrialData data, int s, int comp)
    {
        var u = (data.Float("in:SV_POSITION_RAW", s * 2) + 10) % 1f;
        var w = 0.5f + 1.5f * Math.Abs((data.Float("in:SV_POSITION_RAW", s * 2 + 1) + 10) % 1f);
        var v = comp switch
        {
            0 => ((s % W + 0.5f) / W * 2 - 1) * w,
            1 => (1 - (s / W + 0.5f) / H * 2) * w,
            2 => u * w,
            _ => w,
        };
        return BitConverter.SingleToUInt32Bits(v);
    }

    static int IndexOfKey(IReadOnlyList<SigElement> list, string key)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i].Key == key)
                return i;
        throw new InvalidOperationException("no input " + key);
    }

    static Format FormatOf(CompType t) => t switch
    {
        CompType.UInt => Format.R32G32B32A32_UInt,
        CompType.SInt => Format.R32G32B32A32_SInt,
        _ => Format.R32G32B32A32_Float,
    };

    List<IDisposable> BindResources(ProgramLayout layout, Dictionary<string, ValueKind> kinds, TrialData data, bool vertex,
        HashSet<int> comparisonSlots)
    {
        var disposables = new List<IDisposable>();
        foreach (var cb in layout.CBuffers)
        {
            var size = Math.Max(16, (cb.Size + 15) & ~15);
            foreach (var v in cb.Vars)
                size = Math.Max(size, (v.Offset + v.Size + 15) & ~15);
            var words = new uint[size / 4];
            foreach (var v in cb.Vars)
            {
                var kind = kinds.TryGetValue(v.Name, out var k) ? k : v.Kind;
                for (var i = 0; i < v.Size / 4; i++)
                    words[v.Offset / 4 + i] = data.Word(v.Name, i, kind);
            }
            var buf = CreateBuffer(words, BindFlags.ConstantBuffer);
            disposables.Add(buf);
            if (vertex) _ctx.VSSetConstantBuffers((uint)cb.Slot, 1, [buf]);
            else _ctx.PSSetConstantBuffers((uint)cb.Slot, 1, [buf]);
        }
        foreach (var t in layout.Textures)
        {
            var (res, view) = CreateTexture(t, data);
            disposables.Add(view);
            disposables.Add(res);
            if (vertex) _ctx.VSSetShaderResources((uint)t.Slot, 1, [view]);
            else _ctx.PSSetShaderResources((uint)t.Slot, 1, [view]);
        }
        // sample_c needs a comparison sampler where the program declares one (shadow maps)
        var samplers = Enumerable.Range(0, 16)
            .Select(i => comparisonSlots.Contains(i) ? _comparisonSampler : _sampler).ToArray();
        if (vertex) _ctx.VSSetSamplers(0, 16, samplers);
        else _ctx.PSSetSamplers(0, 16, samplers);
        return disposables;
    }

    readonly Dictionary<byte[], HashSet<int>> _comparisonSlots = new(ReferenceEqualityComparer.Instance);

    /// <summary>Sampler slots declared "dcl_sampler sN, mode_comparison".</summary>
    HashSet<int> ComparisonSlots(byte[] bytecode)
    {
        if (!_comparisonSlots.TryGetValue(bytecode, out var slots))
        {
            slots = System.Text.RegularExpressions.Regex.Matches(HlslCompiler.Disassemble(bytecode), @"dcl_sampler s(\d+), mode_comparison")
                .Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
            _comparisonSlots[bytecode] = slots;
        }
        return slots;
    }

    /// <summary>Random RGBA32F texture with a full mip chain, every level (and face) independent random data, so an
    /// explicit or dropped LOD changes the result. Sampled point/wrap/mip-point.</summary>
    (ID3D11Resource, ID3D11ShaderResourceView) CreateTexture(TexBinding t, TrialData data)
    {
        const int S = 16, S3 = 8;
        var handles = new List<GCHandle>();
        SubresourceData Level(int subresource, int w, int h, int d)
        {
            var texels = new float[w * h * d * 4];
            for (var i = 0; i < texels.Length; i++)
                texels[i] = data.Texel(t.Name, subresource * 100000L + i);
            var handle = GCHandle.Alloc(texels, GCHandleType.Pinned);
            handles.Add(handle);
            return new SubresourceData(handle.AddrOfPinnedObject(), (uint)(w * 16), (uint)(w * h * 16));
        }
        try
        {
            if (t.Dim == TexDim.Tex3D)
            {
                var levels = Log2(S3) + 1;
                var subs = Enumerable.Range(0, levels).Select(m => Level(m, S3 >> m, S3 >> m, S3 >> m)).ToArray();
                var tex = _dev.CreateTexture3D(new Texture3DDescription(Format.R32G32B32A32_Float, S3, S3, S3, (uint)levels, BindFlags.ShaderResource), subs);
                return (tex, _dev.CreateShaderResourceView(tex));
            }
            else
            {
                var levels = Log2(S) + 1;
                var layers = t.Dim switch { TexDim.Cube => 6, TexDim.Tex2DArray => 4, TexDim.CubeArray => 12, _ => 1 };
                var subs = new SubresourceData[layers * levels];
                for (var l = 0; l < layers; l++)
                    for (var m = 0; m < levels; m++)
                        subs[l * levels + m] = Level(l * levels + m, S >> m, S >> m, 1);
                var cube = t.Dim is TexDim.Cube or TexDim.CubeArray;
                var tex = _dev.CreateTexture2D(new Texture2DDescription(Format.R32G32B32A32_Float, S, S, (uint)layers, (uint)levels,
                    BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, 1, 0,
                    cube ? ResourceOptionFlags.TextureCube : ResourceOptionFlags.None), subs);
                ShaderResourceViewDescription? desc = t.Dim switch
                {
                    TexDim.Cube => new ShaderResourceViewDescription(tex, ShaderResourceViewDimension.TextureCube, Format.R32G32B32A32_Float, 0, (uint)levels, 0, 1),
                    TexDim.Tex2DArray => new ShaderResourceViewDescription(tex, ShaderResourceViewDimension.Texture2DArray, Format.R32G32B32A32_Float, 0, (uint)levels, 0, (uint)layers),
                    TexDim.CubeArray => new ShaderResourceViewDescription(tex, ShaderResourceViewDimension.TextureCubeArray, Format.R32G32B32A32_Float, 0, (uint)levels, 0, 2),
                    _ => null,
                };
                return (tex, _dev.CreateShaderResourceView(tex, desc));
            }
        }
        finally
        {
            foreach (var h in handles)
                h.Free();
        }
    }

    static int Log2(int x) => System.Numerics.BitOperations.Log2((uint)x);

    ID3D11Buffer CreateBuffer(uint[] words, BindFlags flags)
    {
        var handle = GCHandle.Alloc(words, GCHandleType.Pinned);
        try
        {
            var desc = new BufferDescription((uint)(words.Length * 4), flags);
            return _dev.CreateBuffer(desc, new SubresourceData(handle.AddrOfPinnedObject(), 0, 0));
        }
        finally { handle.Free(); }
    }

    uint[] ReadBuffer(ID3D11Buffer buf, int bytes)
    {
        using var staging = _dev.CreateBuffer(new BufferDescription((uint)bytes, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        _ctx.CopyResource(staging, buf);
        var map = _ctx.Map(staging, MapMode.Read, MapFlags.None);
        try
        {
            var result = new uint[bytes / 4];
            Marshal.Copy(map.DataPointer, (int[])(object)result, 0, result.Length);
            return result;
        }
        finally { _ctx.Unmap(staging); }
    }

    uint[] ReadTexture(ID3D11Texture2D tex)
    {
        var d = tex.Description;
        using var staging = _dev.CreateTexture2D(new Texture2DDescription(d.Format, d.Width, d.Height, 1, 1, BindFlags.None,
            ResourceUsage.Staging, CpuAccessFlags.Read));
        _ctx.CopyResource(staging, tex);
        var map = _ctx.Map(staging, 0, MapMode.Read, MapFlags.None);
        try
        {
            var result = new int[W * H * 4];
            for (var y = 0; y < H; y++)
                Marshal.Copy(map.DataPointer + (int)(y * map.RowPitch), result, y * W * 4, W * 4);
            return (uint[])(object)result;
        }
        finally { _ctx.Unmap(staging, 0); }
    }
}
