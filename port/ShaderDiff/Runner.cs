namespace ShaderDiff;

public enum Status { Ok, Mismatch, CompileError, NoProgram, NoDxbc, Skipped, Timeout, HarnessError }

public sealed record OutputDiff(string Output, int Component, Diff Kind, int Count);

/// <summary>What a worker reports per variant (one JSON line), and what the report is built from.</summary>
public sealed class VariantSummary
{
    public string Shader { get; set; } = "";
    public int Index { get; set; }
    public string Stage { get; set; } = "";
    public int SubShader { get; set; }
    public int Pass { get; set; }
    public string PassName { get; set; } = "";
    public string[] Keywords { get; set; } = [];
    public Status Status { get; set; }
    public string Message { get; set; } = "";
    public int SamplesCompared { get; set; }
    public int MismatchedSamples { get; set; }
    public int MismatchedTrials { get; set; }
    public List<string> Required { get; set; } = [];
    public List<string> Contributing { get; set; } = [];
    public List<OutputDiff> Outputs { get; set; } = [];
    public List<Mismatch> Examples { get; set; } = [];
    public string? Details { get; set; }

    public static VariantSummary For(string shader, DumpVariant v) => new()
    {
        Shader = shader, Index = v.Index, Stage = v.Stage, SubShader = v.SubShader, Pass = v.Pass,
        PassName = v.PassName, Keywords = v.Keywords,
    };

    public string Id => $"{Index:D3} {Stage} sub{SubShader}/pass{Pass}" +
                        (PassName.Length > 0 ? $" \"{PassName}\"" : "") +
                        (Keywords.Length > 0 ? " [" + string.Join(" ", Keywords) + "]" : "");

    public double MismatchFraction => SamplesCompared == 0 ? 0 : (double)MismatchedSamples / SamplesCompared;
}

public sealed class VariantResult
{
    public required string Shader { get; init; }
    public required DumpVariant Variant { get; init; }
    public Status Status { get; set; }
    public string Message { get; set; } = "";
    public List<Mismatch> Mismatches { get; } = [];
    public int SamplesCompared { get; set; }
    public int MismatchedSamples { get; set; }
    public int MismatchedTrials { get; set; }
    /// <summary>Inputs that, zeroed, make the mismatch go away (all of it / at least half of it).</summary>
    public List<string> Required { get; } = [];
    public List<string> Contributing { get; } = [];
    public byte[]? Original { get; set; }
    public byte[]? Recompiled { get; set; }
    public CgProgram? Program { get; set; }
    public Dxbc? OriginalDxbc { get; set; }

    public string Id => $"{Variant.Index:D3} {Variant.Stage} sub{Variant.SubShader}/pass{Variant.Pass}" +
                        (Variant.PassName.Length > 0 ? $" \"{Variant.PassName}\"" : "") +
                        (Variant.Keywords.Length > 0 ? " [" + string.Join(" ", Variant.Keywords) + "]" : "");

    public double MismatchFraction => SamplesCompared == 0 ? 0 : (double)MismatchedSamples / SamplesCompared;

    public VariantSummary ToSummary(string? details)
    {
        var s = VariantSummary.For(Shader, Variant);
        s.Status = Status;
        s.Message = Message;
        s.SamplesCompared = SamplesCompared;
        s.MismatchedSamples = MismatchedSamples;
        s.MismatchedTrials = MismatchedTrials;
        s.Required = Required;
        s.Contributing = Contributing;
        s.Outputs = Mismatches.GroupBy(m => (m.Output, m.Component, m.Kind))
            .Select(g => new OutputDiff(g.Key.Output, g.Key.Component, g.Key.Kind, g.Count()))
            .OrderBy(o => o.Output).ThenBy(o => o.Component).ToList();
        s.Examples = Mismatches.Take(5).ToList();
        s.Details = details;
        return s;
    }
}

public sealed class Runner(HlslCompiler compiler, int trials, int seed)
{
    /// <summary>Called before every GPU run, so a hang can be attributed (see the coordinator in Program.cs).</summary>
    public Action<string>? Progress { get; set; }

    public VariantResult Run(Gpu gpu, string shader, DumpVariant v, string dumpDir, List<CgProgram> programs)
    {
        var r = new VariantResult { Shader = shader, Variant = v };
        try
        {
            RunInner(gpu, r, dumpDir, programs);
        }
        catch (Exception e)
        {
            r.Status = Status.HarnessError;
            // the first frame of ours says which D3D call refused
            var at = e.StackTrace?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("ShaderDiff.")) ?? "";
            r.Message = $"{e.GetType().Name}: {e.Message.Split('\n')[0].Trim()} {at}";
        }
        return r;
    }

    void RunInner(Gpu gpu, VariantResult r, string dumpDir, List<CgProgram> programs)
    {
        var v = r.Variant;
        if (v.Dxbc == null)
        {
            r.Status = Status.NoDxbc;
            r.Message = "no DXBC container in the program data";
            return;
        }
        var program = programs.FirstOrDefault(p => p.SubShader == v.SubShader && p.Pass == v.Pass);
        if (program == null)
        {
            r.Status = Status.NoProgram;
            r.Message = "no CGPROGRAM for this pass in the decompiled text";
            return;
        }
        r.Program = program;
        r.Original = File.ReadAllBytes(Path.Combine(dumpDir, v.Dxbc));
        var compiled = compiler.Compile(program, r.Shader, v.Keywords, v.Stage);
        if (compiled.Bytecode == null)
        {
            r.Status = Status.CompileError;
            r.Message = FirstErrors(compiled.Messages);
            return;
        }
        r.Recompiled = compiled.Bytecode;

        var origLayout = ProgramLayout.FromDump(v);
        var recLayout = ProgramLayout.FromReflection(r.Recompiled);
        var unsupported = origLayout.Unsupported.Concat(recLayout.Unsupported).Distinct().ToList();
        if (unsupported.Count > 0)
        {
            r.Status = Status.Skipped;
            r.Message = "unsupported resources: " + string.Join(", ", unsupported);
            return;
        }
        var origSig = new Dxbc(r.Original);
        var recSig = new Dxbc(r.Recompiled);
        r.OriginalDxbc = origSig;
        var kinds = origLayout.Kinds();
        foreach (var (k, kind) in recLayout.Kinds())
            kinds.TryAdd(k, kind);
        var vertexInputs = origSig.Inputs.Concat(recSig.Inputs).Where(i => !i.IsGenerated)
            .GroupBy(i => i.Key).Select(g => g.First()).ToList();

        List<Mismatch> RunTrial(TrialData data, string phase)
        {
            Outputs a, b;
            var what = phase + (data.Zeroed.Count > 0 ? $" with {string.Join(", ", data.Zeroed)} = 0" : "");
            Progress?.Invoke($"original program, trial {data.Trial}{what}");
            if (v.Stage == "vs")
                a = gpu.RunVertex(r.Original!, origSig, origLayout, kinds, data, vertexInputs);
            else
                a = gpu.RunPixel(r.Original!, origSig, origLayout, kinds, data);
            Progress?.Invoke($"recompiled program, trial {data.Trial}{what}");
            if (v.Stage == "vs")
                b = gpu.RunVertex(r.Recompiled!, recSig, recLayout, kinds, data, vertexInputs);
            else
                b = gpu.RunPixel(r.Recompiled!, recSig, recLayout, kinds, data);
            return Comparer.Compare(data.Trial, origSig, a, b);
        }

        var perTrial = new Dictionary<int, List<Mismatch>>();
        for (var t = 0; t < trials; t++)
        {
            var m = RunTrial(new TrialData(seed, t), "");
            r.SamplesCompared += Gpu.Samples;
            if (m.Count == 0)
                continue;
            perTrial[t] = m;
            r.Mismatches.AddRange(m);
            r.MismatchedTrials++;
            r.MismatchedSamples += m.Select(x => x.Sample).Distinct().Count();
        }
        r.Status = r.Mismatches.Count == 0 ? Status.Ok : Status.Mismatch;
        if (r.Status == Status.Ok)
            return;

        // influence: zero one input at a time in the worst trial
        var (worst, worstList) = perTrial.MaxBy(kv => kv.Value.Count);
        var baseline = worstList.Count;
        var items = origLayout.CBuffers.Concat(recLayout.CBuffers).SelectMany(c => c.Vars).Select(x => x.Name)
            .Concat(origLayout.Textures.Concat(recLayout.Textures).Select(t => "tex:" + t.Name))
            .Concat((v.Stage == "vs" ? vertexInputs : origSig.Inputs.Where(i => !i.IsGenerated)).Select(i => "in:" + i.Key))
            .Distinct().ToList();
        foreach (var item in items)
        {
            var data = new TrialData(seed, worst);
            data.Zeroed.Add(item);
            var count = RunTrial(data, " (influence analysis)").Count;
            var name = item.StartsWith("in:") ? "input " + item[3..] : item.StartsWith("tex:") ? "texture " + item[4..] : item;
            if (count == 0)
                r.Required.Add(name);
            else if (count <= baseline / 2)
                r.Contributing.Add(name);
        }
    }

    static string FirstErrors(string messages)
    {
        var lines = messages.Split('\n').Select(l => l.Trim()).Where(l => l.Contains("error")).Take(3).ToList();
        return lines.Count > 0 ? string.Join("\n", lines) : messages.Trim();
    }
}
