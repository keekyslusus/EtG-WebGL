namespace ShaderDiff;

public enum ValueKind { Float, Int, UInt, Bool }

/// <summary>
/// The random inputs of one draw ("trial"), shared by the original and the recompiled shader. Every value is
/// a pure function of (seed, trial, name, word index), so both sides see the same numbers whatever their
/// layout, and a failure can be reproduced from the seed and trial number alone.
/// Profiles cycle per trial: 0 = U[0,1], 1 = U[-1,1], 2 = special values (0, ±1, 0.5, 2...), 3 = U[-8,8].
/// </summary>
public sealed class TrialData(int seed, int trial)
{
    public const int Profiles = 4;
    public int Seed { get; } = seed;
    public int Trial { get; } = trial;
    public int Profile => Trial % Profiles;

    /// <summary>Names forced to zero (influence analysis): cbuffer variables, "tex:name", "in:SEMANTIC0".</summary>
    public HashSet<string> Zeroed { get; } = [];

    static readonly float[] Specials = [0f, 1f, -1f, 0.5f, -0.5f, 2f, 0.25f, 4f, 0f, 1f];

    double Uniform(string key, long word)
    {
        var h = Fnv(key) ^ ((ulong)(uint)Seed << 32) ^ (ulong)(uint)Trial * 0x9E3779B97F4A7C15UL;
        return (SplitMix(h + (ulong)word * 0xD1B54A32D192ED03UL) >> 11) * (1.0 / (1UL << 53));
    }

    public float Float(string key, long word)
    {
        if (Zeroed.Contains(key))
            return 0f;
        var u = Uniform(key, word);
        return Profile switch
        {
            0 => (float)u,
            1 => (float)(u * 2 - 1),
            2 => Specials[(int)(u * Specials.Length) % Specials.Length],
            _ => (float)(u * 16 - 8),
        };
    }

    /// <summary>A 32-bit constant buffer word of the given kind.</summary>
    public uint Word(string key, long word, ValueKind kind)
    {
        if (Zeroed.Contains(key))
            return 0;
        var u = Uniform(key, word);
        return kind switch
        {
            ValueKind.Bool => u < 0.5 ? 0u : 1u,
            ValueKind.Int => unchecked((uint)((int)(u * 7) - (Profile == 0 ? 0 : 3))),
            ValueKind.UInt => (uint)(u * 5),
            _ => BitConverter.SingleToUInt32Bits(Float(key, word)),
        };
    }

    /// <summary>Texel component; textures hold [0,1] except in the wide profile.</summary>
    public float Texel(string name, long index)
    {
        var key = "tex:" + name;
        if (Zeroed.Contains(key))
            return 0f;
        var u = Uniform(key, index);
        return Profile == 3 ? (float)(u * 4 - 2) : (float)u;
    }

    static ulong Fnv(string s)
    {
        var h = 14695981039346656037UL;
        foreach (var c in s)
        {
            h ^= c;
            h *= 1099511628211UL;
        }
        return h;
    }

    static ulong SplitMix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
