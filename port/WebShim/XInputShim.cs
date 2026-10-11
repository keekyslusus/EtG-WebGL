using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace WebShim
{
    /// <summary>
    /// Gamepads. EtG reads controllers through InControl's XInputDeviceManager, which calls the
    /// native XInputInterface32/64.dll (XInputDotNet) - stubbed like every P/Invoke, so no
    /// controller ever showed up. The patcher forwards XInputDotNetPure.Imports' externs here:
    ///  - WebGL: the browser Gamepad API (EtgWebShim.jslib). The "standard" mapping is the Xbox
    ///    layout, so any pad the browser maps (Xbox, DualShock/DualSense, Switch Pro...) works.
    ///  - Editor Play Mode: the real XInput, through delegates the editor script EtgXInputEditor
    ///    installs (a DllImport of xinput1_4 here would also be compiled into the WebGL player).
    /// The patcher also makes InControl enable XInput on WebGL (PlatformForXInput), polls it on the
    /// main thread instead of its worker thread, and hides Unity's own joystick list on WebGL
    /// (GetJoystickNames) so a browser pad isn't attached twice as an unknown Unity device.
    /// </summary>
    public static class XInputShim
    {
        private const uint ErrorSuccess = 0;
        private const uint ErrorDeviceNotConnected = 1167;

        /// <summary>XInputGetState(index, XINPUT_STATE*) in the editor; null = no controller.</summary>
        public static Func<uint, IntPtr, uint> NativeGetState;

        /// <summary>XInputSetState(index, left, right) in the editor, motors 0..1.</summary>
        public static Action<uint, float, float> NativeSetState;

        [DllImport("__Internal")]
        private static extern uint EtgGamepadGetState(uint index, IntPtr state);

        [DllImport("__Internal")]
        private static extern void EtgGamepadSetState(uint index, float leftMotor, float rightMotor);

        // GamePad.GetState allocates the 16-byte XINPUT_STATE with AllocHGlobal for every call and
        // never frees it (~14 MB/hour at 4 pads x 60 fps). It reads the struct right after we
        // return, and everything runs on the main thread, so free the previous one on the next call.
        private static IntPtr _lastState;

        public static uint XInputGamePadGetState32(uint playerIndex, IntPtr state)
        {
            return GetState(playerIndex, state);
        }

        public static uint XInputGamePadGetState64(uint playerIndex, IntPtr state)
        {
            return GetState(playerIndex, state);
        }

        public static void XInputGamePadSetState32(uint playerIndex, float leftMotor, float rightMotor)
        {
            SetState(playerIndex, leftMotor, rightMotor);
        }

        public static void XInputGamePadSetState64(uint playerIndex, float leftMotor, float rightMotor)
        {
            SetState(playerIndex, leftMotor, rightMotor);
        }

        private static uint GetState(uint playerIndex, IntPtr state)
        {
            if (_lastState != IntPtr.Zero && _lastState != state)
            {
                Marshal.FreeHGlobal(_lastState);
            }
            _lastState = state;

            if (Application.platform == RuntimePlatform.WebGLPlayer)
            {
                return EtgGamepadGetState(playerIndex, state);
            }
            if (NativeGetState != null)
            {
                return NativeGetState(playerIndex, state);
            }
            // XInputDotNet reads the struct even for a disconnected pad: hand it zeros.
            for (int i = 0; i < 16; i += 4)
            {
                Marshal.WriteInt32(state, i, 0);
            }
            return ErrorDeviceNotConnected;
        }

        private static void SetState(uint playerIndex, float leftMotor, float rightMotor)
        {
            if (Application.platform == RuntimePlatform.WebGLPlayer)
            {
                EtgGamepadSetState(playerIndex, leftMotor, rightMotor);
            }
            else if (NativeSetState != null)
            {
                NativeSetState(playerIndex, leftMotor, rightMotor);
            }
        }

        /// <summary>
        /// Replaces Application.platform in InControlManager.OnEnable and
        /// XInputDeviceManager.CheckPlatformSupport: both only turn XInput on for Windows.
        /// </summary>
        public static RuntimePlatform PlatformForXInput()
        {
            RuntimePlatform platform = Application.platform;
            return platform == RuntimePlatform.WebGLPlayer ? RuntimePlatform.WindowsPlayer : platform;
        }

        /// <summary>
        /// Replaces Input.GetJoystickNames in InControl's UnityInputDeviceManager. In the browser
        /// every pad already comes through XInput here; Unity's list holds the same pads, which match
        /// no InControl profile and would only be attached as unknown devices.
        /// </summary>
        public static string[] GetJoystickNames()
        {
            return Application.platform == RuntimePlatform.WebGLPlayer ? new string[0] : Input.GetJoystickNames();
        }
    }
}
