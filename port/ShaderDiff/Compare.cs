namespace ShaderDiff;

public enum Diff { Value, Sign, NaN, Discard, Missing }

public sealed record Mismatch(int Trial, int Sample, string Output, int Component, uint Original, uint Recompiled, Diff Kind);

public static class Comparer
{
    public const float RelTol = 2e-3f, AbsTol = 1e-4f;

    /// <summary>Compares every component the original program outputs; returns all mismatches.</summary>
    public static List<Mismatch> Compare(int trial, Dxbc original, Outputs a, Outputs b)
    {
        var list = new List<Mismatch>();
        foreach (var el in original.Outputs)
        {
            if (!a.TryGetValue(el.Key, out var av))
                continue;
            if (!b.TryGetValue(el.Key, out var bv))
            {
                list.Add(new Mismatch(trial, -1, el.Key, -1, 0, 0, Diff.Missing));
                continue;
            }
            var isFloat = el.Type is CompType.Float or CompType.Unknown;
            for (var s = 0; s < Gpu.Samples; s++)
                for (var c = 0; c < el.ComponentCount; c++)
                {
                    var x = av[s * 4 + c];
                    var y = bv[s * 4 + c];
                    var kind = Classify(x, y, isFloat);
                    if (kind != null)
                        list.Add(new Mismatch(trial, s, el.Key, c, x, y, kind.Value));
                }
        }
        return list;
    }

    static Diff? Classify(uint x, uint y, bool isFloat)
    {
        if (x == y)
            return null;
        if (x == Gpu.Sentinel || y == Gpu.Sentinel)
            return Diff.Discard;
        if (!isFloat)
            return Diff.Value;
        var fx = BitConverter.UInt32BitsToSingle(x);
        var fy = BitConverter.UInt32BitsToSingle(y);
        var nx = !float.IsFinite(fx);
        var ny = !float.IsFinite(fy);
        if (nx || ny)
            return nx && ny && (float.IsNaN(fx) == float.IsNaN(fy)) && (float.IsNaN(fx) || fx == fy) ? null : Diff.NaN;
        var d = Math.Abs(fx - fy);
        var m = Math.Max(Math.Abs(fx), Math.Abs(fy));
        if (d <= AbsTol + RelTol * m)
            return null;
        if (Math.Sign(fx) == -Math.Sign(fy) && Math.Abs(fx + fy) <= AbsTol + RelTol * m)
            return Diff.Sign;
        return Diff.Value;
    }

    public static string Format(uint bits, CompType type = CompType.Float)
    {
        if (bits == Gpu.Sentinel)
            return "discard";
        return type switch
        {
            CompType.UInt => bits.ToString(),
            CompType.SInt => ((int)bits).ToString(),
            _ => BitConverter.UInt32BitsToSingle(bits).ToString("G6"),
        };
    }
}
