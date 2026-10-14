using System;
using System.Collections.Generic;
using UnityEngine;

namespace BepInEx
{
    /// <summary>BepInEx's input abstraction (Expand the Gungeon calls UnityInput.Current.ResetInputAxes()).</summary>
    public interface IInputSystem
    {
        bool GetKey(string name);
        bool GetKey(KeyCode key);
        bool GetKeyDown(string name);
        bool GetKeyDown(KeyCode key);
        bool GetKeyUp(string name);
        bool GetKeyUp(KeyCode key);
        bool GetMouseButton(int button);
        bool GetMouseButtonDown(int button);
        bool GetMouseButtonUp(int button);
        void ResetInputAxes();
        Vector3 mousePosition { get; }
        Vector2 mouseScrollDelta { get; }
        bool mousePresent { get; }
        bool anyKey { get; }
        bool anyKeyDown { get; }
        IEnumerable<KeyCode> SupportedKeyCodes { get; }
    }

    /// <summary>The game uses Unity's legacy input manager; BepInEx then forwards to UnityEngine.Input.</summary>
    public static class UnityInput
    {
        private static readonly IInputSystem s_current = new LegacyInputSystem();

        public static IInputSystem Current
        {
            get { return s_current; }
        }

        private sealed class LegacyInputSystem : IInputSystem
        {
            public bool GetKey(string name) { return Input.GetKey(name); }
            public bool GetKey(KeyCode key) { return Input.GetKey(key); }
            public bool GetKeyDown(string name) { return Input.GetKeyDown(name); }
            public bool GetKeyDown(KeyCode key) { return Input.GetKeyDown(key); }
            public bool GetKeyUp(string name) { return Input.GetKeyUp(name); }
            public bool GetKeyUp(KeyCode key) { return Input.GetKeyUp(key); }
            public bool GetMouseButton(int button) { return Input.GetMouseButton(button); }
            public bool GetMouseButtonDown(int button) { return Input.GetMouseButtonDown(button); }
            public bool GetMouseButtonUp(int button) { return Input.GetMouseButtonUp(button); }
            public void ResetInputAxes() { Input.ResetInputAxes(); }
            public Vector3 mousePosition { get { return Input.mousePosition; } }
            public Vector2 mouseScrollDelta { get { return Input.mouseScrollDelta; } }
            public bool mousePresent { get { return Input.mousePresent; } }
            public bool anyKey { get { return Input.anyKey; } }
            public bool anyKeyDown { get { return Input.anyKeyDown; } }

            public IEnumerable<KeyCode> SupportedKeyCodes
            {
                get { return (KeyCode[])Enum.GetValues(typeof(KeyCode)); }
            }
        }
    }
}
