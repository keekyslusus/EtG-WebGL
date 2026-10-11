using System.Text;

namespace ShaderDiff;

/// <summary>report.md (summary grouped by likely cause) + failures/&lt;shader&gt;/&lt;variant&gt;.* (disassembly of both
/// programs, the decompiled CGPROGRAM, inputs and outputs of example samples).</summary>
public sealed class Report(List<VariantSummary> results, string outDir, int seed, int trials, TimeSpan elapsed)
{
    public static string Cause(VariantSummary r)
    {
        if (r.Outputs.Any(m => m.Kind == Diff.Missing))
            return "Output missing in the decompilation";
        var kinds = r.Outputs.GroupBy(m => m.Kind).ToDictionary(g => g.Key, g => g.Sum(o => o.Count));
        var total = kinds.Values.Sum();
        if (kinds.ContainsKey(Diff.Discard))
            return "Discard (clip) differs";
        if (kinds.GetValueOrDefault(Diff.Sign) * 3 >= total)
            return "Sign flipped";
        if (kinds.ContainsKey(Diff.NaN))
            return "NaN/Inf on one side only";
        return r.MismatchFraction < 0.02
            ? "Rare value mismatches (< 2% of samples: likely a threshold/floor/texel edge, check before blaming the decompiler)"
            : "Wrong values";
    }

    static string Comp(int c) => c >= 0 ? "." + "xyzw"[c] : "";

    public void Write()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Shader differential test");
        sb.AppendLine();
        sb.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm}, seed {seed}, {trials} trials x {Gpu.Samples} samples per variant, " +
                      $"tolerance {Comparer.AbsTol} + {Comparer.RelTol} * |value|, {elapsed.TotalSeconds:F0} s.");
        sb.AppendLine();
        sb.AppendLine("Original = game DXBC; recompiled = decompiled CG (after `fix_shaders.py`) compiled with d3dcompiler_47 vs_4_0/ps_4_0, " +
                      "both on D3D11 WARP with the same random constants, textures and inputs. The GLES rewrites of fix_shaders.py " +
                      "are under #if on the platform, so this also checks they are no-ops on D3D11.");
        sb.AppendLine();
        sb.AppendLine("| status | variants | shaders |");
        sb.AppendLine("|---|---|---|");
        foreach (var g in results.GroupBy(r => r.Status).OrderBy(g => g.Key))
            sb.AppendLine($"| {g.Key} | {g.Count()} | {g.Select(r => r.Shader).Distinct().Count()} |");
        sb.AppendLine($"| **total** | {results.Count} | {results.Select(r => r.Shader).Distinct().Count()} |");
        sb.AppendLine();

        var bad = results.Where(r => r.Status == Status.Mismatch).ToList();
        if (bad.Count > 0)
        {
            sb.AppendLine("## Mismatches by likely cause");
            sb.AppendLine();
            sb.AppendLine("*Required* = zeroing that one input (constant, texture, interpolant/attribute) makes every mismatch of the " +
                          "worst trial disappear; *contributing* = removes at least half. Zeroing can also hide a bug trivially " +
                          "(e.g. a texture that multiplies the whole result), so read it as a hint. Details, inputs and the " +
                          "disassembly of both programs: the linked `failures/<shader>/<variant>.md`.");
            sb.AppendLine();
            foreach (var cause in bad.GroupBy(Cause).OrderByDescending(g => g.Select(r => r.Shader).Distinct().Count()))
            {
                sb.AppendLine($"### {cause.Key}");
                sb.AppendLine();
                sb.AppendLine($"{cause.Count()} variants in {cause.Select(r => r.Shader).Distinct().Count()} shaders.");
                var req = cause.SelectMany(r => r.Required).GroupBy(x => x).OrderByDescending(g => g.Count()).Take(12)
                    .Select(g => $"`{g.Key}` x{g.Count()}").ToList();
                if (req.Count > 0)
                    sb.AppendLine($"Most often required: {string.Join(", ", req)}.");
                sb.AppendLine();
                foreach (var shader in cause.GroupBy(r => r.Shader))
                {
                    sb.AppendLine($"- **{shader.Key}**");
                    foreach (var r in shader)
                    {
                        var outputs = string.Join(", ", r.Outputs.Select(o => o.Output + Comp(o.Component)).Distinct());
                        var link = r.Details != null ? $"[{r.Id}]({r.Details.Replace(" ", "%20")})" : r.Id;
                        sb.AppendLine($"  - {link} — {r.MismatchedSamples}/{r.SamplesCompared} samples " +
                                      $"({r.MismatchedTrials}/{trials} trials), {outputs}" +
                                      (r.Required.Count > 0 ? $"; required: {string.Join(", ", r.Required)}" : "") +
                                      (r.Contributing.Count > 0 ? $"; contributing: {string.Join(", ", r.Contributing)}" : ""));
                        var ex = r.Examples.FirstOrDefault(m => m.Kind != Diff.Missing);
                        if (ex != null)
                            sb.AppendLine($"    e.g. trial {ex.Trial} sample {ex.Sample}: {ex.Output}{Comp(ex.Component)} " +
                                          $"original {Comparer.Format(ex.Original)} vs recompiled {Comparer.Format(ex.Recompiled)}");
                    }
                }
                sb.AppendLine();
            }
        }

        foreach (var (status, title) in new[]
                 {
                     (Status.Timeout, "Timeouts (a program ran 30 s without finishing: an endless or huge loop)"),
                     (Status.CompileError, "Decompiled variant does not compile for D3D11"),
                     (Status.NoProgram, "No CGPROGRAM for the pass"),
                     (Status.NoDxbc, "No DXBC in the program data"),
                     (Status.Skipped, "Skipped"),
                     (Status.HarnessError, "Harness errors"),
                 })
        {
            var list = results.Where(r => r.Status == status).ToList();
            if (list.Count == 0)
                continue;
            sb.AppendLine($"## {title}");
            sb.AppendLine();
            foreach (var g in list.GroupBy(r => r.Message).OrderByDescending(g => g.Count()))
            {
                sb.AppendLine($"- {g.Count()} variants: `{g.Key.Replace("\n", " / ")}`");
                foreach (var shader in g.GroupBy(r => r.Shader))
                    sb.AppendLine($"  - {shader.Key}: {string.Join(", ", shader.Select(r => r.Id))}");
            }
            sb.AppendLine();
        }

        var clean = results.GroupBy(r => r.Shader).Where(g => g.All(r => r.Status == Status.Ok)).Select(g => g.Key)
            .Order(StringComparer.Ordinal).ToList();
        sb.AppendLine($"## Clean shaders ({clean.Count})");
        sb.AppendLine();
        sb.AppendLine(string.Join(", ", clean));
        File.WriteAllText(Path.Combine(outDir, "report.md"), sb.ToString());
    }

    /// <summary>Writes failures/&lt;shader&gt;/&lt;variant&gt;.md + .asm/.hlsl next to it; returns the .md path relative to outDir.</summary>
    public static string WriteDetails(VariantResult r, string outDir, int seed, int trials)
    {
        var rel = $"failures/{r.Shader}/{r.Variant.Index:D3}-{r.Variant.Stage}";
        var path = Path.Combine(outDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (r.Original != null)
            File.WriteAllText(path + ".original.asm", HlslCompiler.Disassemble(r.Original));
        if (r.Recompiled != null)
            File.WriteAllText(path + ".recompiled.asm", HlslCompiler.Disassemble(r.Recompiled));
        if (r.Program != null)
            File.WriteAllText(path + ".cgprogram.hlsl", r.Program.Source);

        var summary = r.ToSummary(null);
        var sb = new StringBuilder();
        sb.AppendLine($"# {r.Shader} — {r.Id}");
        sb.AppendLine();
        sb.AppendLine($"Cause guess: {Cause(summary)}. {r.MismatchedSamples}/{r.SamplesCompared} samples differ in {r.MismatchedTrials}/{trials} trials.");
        sb.AppendLine($"Required: {string.Join(", ", r.Required)}; contributing: {string.Join(", ", r.Contributing)}.");
        sb.AppendLine($"Reproduce: `bash port/shaderdiff.sh --shader \"{r.Shader}\" --variant {r.Variant.Index} --seed {seed} --trials {trials}`.");
        sb.AppendLine($"Files: `{Path.GetFileName(path)}.original.asm`, `.recompiled.asm`, `.cgprogram.hlsl` " +
                      $"(the pass's CGPROGRAM; line 1 = line {r.Program?.Line} of the .shader).");
        sb.AppendLine();
        sb.AppendLine("| output | kind | count |");
        sb.AppendLine("|---|---|---|");
        foreach (var o in summary.Outputs)
            sb.AppendLine($"| {o.Output}{Comp(o.Component)} | {o.Kind} | {o.Count} |");
        sb.AppendLine();

        var sig = r.OriginalDxbc;
        foreach (var trialGroup in r.Mismatches.Where(m => m.Sample >= 0).GroupBy(m => m.Trial).Take(2))
        {
            var data = new TrialData(seed, trialGroup.Key);
            sb.AppendLine($"## Trial {trialGroup.Key} (profile {data.Profile})");
            sb.AppendLine();
            sb.AppendLine("Constants (as the original program sees them):");
            sb.AppendLine("```");
            var layout = ProgramLayout.FromDump(r.Variant);
            foreach (var cb in layout.CBuffers)
                foreach (var v in cb.Vars)
                {
                    var words = Enumerable.Range(0, v.Size / 4).Select(i => data.Word(v.Name, i, v.Kind))
                        .Select(w => v.Kind == ValueKind.Float ? BitConverter.UInt32BitsToSingle(w).ToString("G5") : ((int)w).ToString());
                    sb.AppendLine($"cb{cb.Slot} {cb.Name}.{v.Name} @{v.Offset} = {string.Join(", ", words)}");
                }
            sb.AppendLine("```");
            sb.AppendLine("Textures (random 16x16 RGBA32F with random mips, point/wrap): " +
                          string.Join(", ", layout.Textures.Select(t => $"{t.Name} (t{t.Slot}, {t.Dim})")));
            sb.AppendLine();
            foreach (var sampleGroup in trialGroup.GroupBy(m => m.Sample).Take(4))
            {
                var s = sampleGroup.Key;
                sb.AppendLine($"Sample {s}:");
                sb.AppendLine("```");
                if (sig != null)
                    foreach (var el in sig.Inputs.Where(i => !i.IsGenerated))
                    {
                        var vals = Enumerable.Range(0, el.ComponentCount).Select(c => el.IsSV("SV_Position") && r.Variant.Stage == "ps"
                            ? Comparer.Format(Gpu.PositionWord(data, s, c))
                            : Comparer.Format(Gpu.InputWord(data, el, s, c), el.Type));
                        sb.AppendLine($"in  {el.Key} = ({string.Join(", ", vals)})");
                    }
                foreach (var m in sampleGroup.Take(8))
                    sb.AppendLine($"out {m.Output}{Comp(m.Component)}: original {Comparer.Format(m.Original)}, recompiled {Comparer.Format(m.Recompiled)} ({m.Kind})");
                sb.AppendLine("```");
            }
        }
        File.WriteAllText(path + ".md", sb.ToString());
        return rel + ".md";
    }
}
