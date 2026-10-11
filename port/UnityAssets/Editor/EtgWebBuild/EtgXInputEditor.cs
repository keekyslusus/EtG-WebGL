using System;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;

// Real XInput for Play Mode: the patched game calls WebShim.XInputShim, which in the browser reads
// the Gamepad API and in the editor calls the delegates installed here. They live in an editor
// script because a DllImport of xinput1_4 inside WebShim.dll would be compiled into the WebGL
// player too. Same C# 4 restrictions as EtgWebBuild.cs.
[InitializeOnLoad]
public static class EtgXInputEditor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Vibration
    {
        public ushort Left;
        public ushort Right;
    }

    private static class XInput14
    {
        [DllImport("xinput1_4.dll")]
        public static extern uint XInputGetState(uint index, IntPtr state);

        [DllImport("xinput1_4.dll")]
        public static extern uint XInputSetState(uint index, ref Vibration vibration);
    }

    // Windows 7 has no xinput1_4; 9.1.0 ships with every Windows since Vista.
    private static class XInput910
    {
        [DllImport("xinput9_1_0.dll")]
        public static extern uint XInputGetState(uint index, IntPtr state);

        [DllImport("xinput9_1_0.dll")]
        public static extern uint XInputSetState(uint index, ref Vibration vibration);
    }

    // Runs after every domain reload, including the one on entering Play Mode.
    static EtgXInputEditor()
    {
        if (Application.platform != RuntimePlatform.WindowsEditor)
        {
            return;
        }
        bool use14 = true;
        IntPtr probe = Marshal.AllocHGlobal(16);
        try
        {
            XInput14.XInputGetState(0, probe);
        }
        catch (DllNotFoundException)
        {
            use14 = false;
        }
        finally
        {
            Marshal.FreeHGlobal(probe);
        }

        if (use14)
        {
            WebShim.XInputShim.NativeGetState = XInput14.XInputGetState;
            WebShim.XInputShim.NativeSetState = delegate(uint index, float left, float right)
            {
                Vibration v = ToVibration(left, right);
                XInput14.XInputSetState(index, ref v);
            };
        }
        else
        {
            WebShim.XInputShim.NativeGetState = XInput910.XInputGetState;
            WebShim.XInputShim.NativeSetState = delegate(uint index, float left, float right)
            {
                Vibration v = ToVibration(left, right);
                XInput910.XInputSetState(index, ref v);
            };
        }
    }

    private static Vibration ToVibration(float left, float right)
    {
        Vibration v;
        v.Left = (ushort)(Mathf.Clamp01(left) * 65535f);
        v.Right = (ushort)(Mathf.Clamp01(right) * 65535f);
        return v;
    }
}
