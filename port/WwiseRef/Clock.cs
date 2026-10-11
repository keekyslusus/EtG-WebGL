using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WwiseRef;

/// <summary>
/// A virtual clock for AkSoundEngine.dll. Its "No_Output" sink mixes nothing, and the real "System" sink
/// only renders when the sound card pulls data (or, suspended with renderAnyway, as real time passes). So
/// the harness suspends the System sink and patches the DLL's import of QueryPerformanceCounter to this
/// clock, which RenderAudio() then sees advance by exactly one 1024-sample frame per call: the engine
/// renders offline, one frame per call, deterministically and as fast as it can.
/// </summary>
internal static unsafe class Clock
{
    private static long s_ticks;
    private static long s_frequency;
    private static long s_remainder;

    [DllImport("kernel32")] private static extern bool QueryPerformanceFrequency(out long frequency);
    [DllImport("kernel32")] private static extern bool QueryPerformanceCounter(out long counter);
    [DllImport("kernel32")] private static extern bool VirtualProtect(IntPtr address, nuint size, uint protect, out uint old);

    [UnmanagedCallersOnly]
    private static int VirtualQpc(long* counter)
    {
        *counter = Volatile.Read(ref s_ticks);
        return 1;
    }

    /// <summary>Points the IAT entry of KERNEL32!QueryPerformanceCounter in `module` at the virtual clock.</summary>
    public static void Install(string modulePath)
    {
        QueryPerformanceFrequency(out s_frequency);
        QueryPerformanceCounter(out s_ticks);
        IntPtr module = NativeLibrary.Load(modulePath);
        byte* b = (byte*)module;
        int pe = *(int*)(b + 0x3C);
        byte* opt = b + pe + 24;
        uint importRva = *(uint*)(opt + 120); // PE32+ data directory 1
        for (byte* desc = b + importRva; *(uint*)(desc + 12) != 0; desc += 20)
        {
            string dll = Marshal.PtrToStringAnsi((IntPtr)(b + *(uint*)(desc + 12)))!;
            if (!dll.Equals("KERNEL32.dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            ulong* names = (ulong*)(b + *(uint*)desc);
            ulong* slots = (ulong*)(b + *(uint*)(desc + 16));
            for (int i = 0; names[i] != 0; i++)
            {
                if ((names[i] >> 63) != 0)
                {
                    continue; // by ordinal
                }
                string fn = Marshal.PtrToStringAnsi((IntPtr)(b + (uint)names[i] + 2))!;
                if (fn != "QueryPerformanceCounter")
                {
                    continue;
                }
                VirtualProtect((IntPtr)(slots + i), 8, 0x04 /* PAGE_READWRITE */, out uint old);
                slots[i] = (ulong)(delegate* unmanaged<long*, int>)&VirtualQpc;
                VirtualProtect((IntPtr)(slots + i), 8, old, out _);
                return;
            }
        }
        throw new InvalidOperationException("QueryPerformanceCounter import not found in " + modulePath);
    }

    /// <summary>Advances the clock by samples / rate seconds.</summary>
    public static void Advance(int samples, int rate)
    {
        long num = s_frequency * samples + s_remainder;
        Interlocked.Add(ref s_ticks, num / rate);
        s_remainder = num % rate;
    }
}
