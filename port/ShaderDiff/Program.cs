using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShaderDiff;

// Differential test of decompiled shaders: every DX11 variant of the game's original bytecode against the
// decompiled CG recompiled with d3dcompiler_47, both run on D3D11 WARP with identical random inputs.
// Usage: see port/shaderdiff.sh (it prepares --src and calls this).
//
// The coordinator runs one worker process per shader (--worker). A shader whose loop never ends would hang
// WARP for good, so workers report progress before every GPU run; a worker that is silent for IdleTimeout
// is killed, the variant it was on is reported as a timeout, and the shader resumes after it (--start).
var opts = new Options();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--dump": opts.Dump = args[++i]; break;
        case "--src": opts.Src = args[++i]; break;
        case "--cginc": opts.CgInc = args[++i]; break;
        case "--out": opts.Out = args[++i]; break;
        case "--shader": opts.Filters.Add(args[++i]); break;
        case "--variant": opts.Variant = int.Parse(args[++i]); break;
        case "--trials": opts.Trials = int.Parse(args[++i]); break;
        case "--seed": opts.Seed = int.Parse(args[++i]); break;
        case "--jobs": opts.Jobs = int.Parse(args[++i]); break;
        case "--worker": opts.Worker = true; break;
        case "--start": opts.Start = int.Parse(args[++i]); break;
        default:
            Console.Error.WriteLine($"unknown argument {args[i]}");
            return 2;
    }
}
if (opts.Dump == null || opts.Src == null || opts.CgInc == null || opts.Out == null)
{
    Console.Error.WriteLine("ShaderDiff --dump <uscs dump dir> --src <fixed .shader dir> --cginc <Unity CGIncludes> --out <report dir>\n" +
                            "           [--shader <name|prefix*>]... [--variant <index>] [--trials 8] [--seed 1] [--jobs N]");
    return 2;
}
return opts.Worker ? Worker.Run(opts) : Coordinator.Run(opts);

sealed class Options
{
    public string? Dump, Src, CgInc, Out;
    public List<string> Filters { get; } = [];
    public int? Variant;
    public int Trials = 8, Seed = 1, Jobs = Math.Max(1, Environment.ProcessorCount / 2), Start;
    public bool Worker;

    public static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public IEnumerable<string> CommonArgs() =>
        ["--dump", Dump!, "--src", Src!, "--cginc", CgInc!, "--out", Out!, "--trials", Trials.ToString(), "--seed", Seed.ToString()];
}

static class Worker
{
    // stdout protocol: "P <variant> <what>" before each GPU run, "R <json VariantSummary>" per finished variant
    public static int Run(Options o)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var shader = o.Filters.Single();
        var dir = Path.Combine(o.Dump!, shader);
        var variants = DumpReader.Read(dir);
        var srcFile = Path.Combine(o.Src!, shader + ".shader");
        var programs = File.Exists(srcFile) ? ShaderText.Programs(File.ReadAllText(srcFile)) : [];
        var runner = new Runner(new HlslCompiler(o.CgInc!), o.Trials, o.Seed);
        using var gpu = new Gpu();
        foreach (var v in variants)
        {
            if (v.Index < o.Start || (o.Variant != null && v.Index != o.Variant))
                continue;
            runner.Progress = what => Console.WriteLine($"P {v.Index} {what}");
            var r = runner.Run(gpu, shader, v, dir, programs);
            string? details = null;
            if (r.Status == Status.Mismatch)
                details = Report.WriteDetails(r, o.Out!, o.Seed, o.Trials);
            Console.WriteLine("R " + JsonSerializer.Serialize(r.ToSummary(details), Options.Json));
        }
        return 0;
    }
}

static class Coordinator
{
    static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    public static int Run(Options o)
    {
        var shaders = Directory.EnumerateFiles(o.Dump!, "variants.json", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(o.Dump!, Path.GetDirectoryName(f)!).Replace('\\', '/'))
            .Where(name => o.Filters.Count == 0 || o.Filters.Any(f => f.EndsWith('*') ? name.StartsWith(f[..^1]) : name == f))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (shaders.Count == 0)
        {
            Console.Error.WriteLine("no shaders matched");
            return 1;
        }
        Directory.CreateDirectory(o.Out!);
        var failDir = Path.Combine(o.Out!, "failures");
        if (Directory.Exists(failDir))
            Directory.Delete(failDir, true);

        var results = new ConcurrentBag<VariantSummary>();
        var sw = Stopwatch.StartNew();
        var done = 0;
        // big shaders first so the long ones don't end up last on one core
        var order = shaders.OrderByDescending(s => new FileInfo(Path.Combine(o.Dump!, s, "variants.json")).Length).ToList();
        Parallel.ForEach(order, new ParallelOptions { MaxDegreeOfParallelism = o.Jobs }, shader =>
        {
            var local = RunShader(o, shader);
            foreach (var r in local)
                results.Add(r);
            var bad = local.Count(r => r.Status != Status.Ok);
            var n = Interlocked.Increment(ref done);
            Console.WriteLine($"[{n}/{shaders.Count}] {shader}: {local.Count} variants" + (bad > 0 ? $", {bad} not ok" : ""));
        });

        var all = results.OrderBy(r => r.Shader, StringComparer.Ordinal).ThenBy(r => r.Index).ToList();
        new Report(all, o.Out!, o.Seed, o.Trials, sw.Elapsed).Write();
        foreach (var s in Enum.GetValues<Status>())
        {
            var c = all.Count(r => r.Status == s);
            if (c > 0)
                Console.WriteLine($"{s}: {c}");
        }
        Console.WriteLine($"report: {Path.GetFullPath(Path.Combine(o.Out!, "report.md"))}");
        return 0;
    }

    static List<VariantSummary> RunShader(Options o, string shader)
    {
        var results = new List<VariantSummary>();
        var start = 0;
        while (true)
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
            };
            foreach (var a in o.CommonArgs().Concat(["--worker", "--shader", shader, "--start", start.ToString()]))
                psi.ArgumentList.Add(a);
            if (o.Variant != null)
            {
                psi.ArgumentList.Add("--variant");
                psi.ArgumentList.Add(o.Variant.ToString()!);
            }

            var gate = new object();
            var lastTime = DateTime.UtcNow;
            (int Index, string What)? last = null;
            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null)
                    return;
                lock (gate)
                {
                    lastTime = DateTime.UtcNow;
                    if (e.Data.StartsWith("P "))
                    {
                        var parts = e.Data.Split(' ', 3);
                        last = (int.Parse(parts[1]), parts[2]);
                    }
                    else if (e.Data.StartsWith("R "))
                    {
                        results.Add(JsonSerializer.Deserialize<VariantSummary>(e.Data[2..], Options.Json)!);
                        last = null;
                    }
                }
            };
            p.Start();
            p.BeginOutputReadLine();
            var timedOut = false;
            while (!p.WaitForExit(500))
            {
                lock (gate)
                {
                    if (DateTime.UtcNow - lastTime > IdleTimeout)
                    {
                        timedOut = true;
                        break;
                    }
                }
            }
            if (!timedOut)
            {
                p.WaitForExit(); // flush the remaining output events
                if (p.ExitCode != 0)
                    Console.Error.WriteLine($"{shader}: worker exited with {p.ExitCode}");
                return results;
            }
            p.Kill(true);
            p.WaitForExit();
            (int Index, string What) hung;
            lock (gate)
                hung = last ?? (results.Count > 0 ? results.Max(r => r.Index) + 1 : start, "setup");
            var variant = DumpReader.Read(Path.Combine(o.Dump!, shader)).First(v => v.Index == hung.Index);
            var summary = VariantSummary.For(shader, variant);
            summary.Status = Status.Timeout;
            summary.Message = $"no progress for {IdleTimeout.TotalSeconds:F0} s while running the {hung.What}";
            results.Add(summary);
            Console.WriteLine($"{shader}: variant {hung.Index} timed out ({hung.What})");
            start = hung.Index + 1;
            if (o.Variant != null)
                return results;
        }
    }
}
