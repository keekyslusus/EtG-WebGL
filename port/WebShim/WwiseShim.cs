using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace WebShim
{
    /// <summary>
    /// Wwise replacement. The patcher turns every P/Invoke of AkSoundEnginePINVOKE into a call to the
    /// method of the same name and signature here when one exists (the rest stay stubs that return 0),
    /// and AkSoundEngine.GetAkGameObjectID into GetAkGameObjectID below. Calls go through
    /// EtgWebShim.jslib to the browser engine (StreamingAssets/webaudio/etg-audio.js, built by
    /// port/audio/build_audio.py), which plays the converted soundbank with Web Audio.
    ///
    /// AkSoundEngine.IsInitialized() stays false, as before: the game then never auto-adds AkGameObj
    /// components, and the code paths that only run with a live Wwise stay off. Positions of objects
    /// with playing 3D sounds and the listener (the game's AkAudioListener) are sent once per frame instead.
    /// Outside the WebGL player (editor Play Mode) everything here is a no-op.
    /// </summary>
    public static class WwiseShim
    {
        private const int AK_Success = 1;

        [DllImport("__Internal")] private static extern void EtgAudioInit(string baseUrl);
        [DllImport("__Internal")] private static extern uint EtgAudioPostEvent(uint eventId, int obj, float x, float y, float z);
        [DllImport("__Internal")] private static extern void EtgAudioSetSwitch(uint group, uint value, int obj);
        [DllImport("__Internal")] private static extern void EtgAudioSetState(uint group, uint state);
        [DllImport("__Internal")] private static extern void EtgAudioSetRtpc(uint rtpc, float value, int obj, int fadeMs);
        [DllImport("__Internal")] private static extern void EtgAudioResetRtpc(uint rtpc, int obj);
        [DllImport("__Internal")] private static extern void EtgAudioStopAll(int obj);
        [DllImport("__Internal")] private static extern void EtgAudioStopPlayingId(uint playingId, int fadeMs);
        [DllImport("__Internal")] private static extern void EtgAudioActionOnEvent(uint eventId, int actionType, int obj, int fadeMs, uint playingId);
        [DllImport("__Internal")] private static extern void EtgAudioPostTrigger(uint trigger, int obj);
        [DllImport("__Internal")] private static extern int EtgAudioSetPosition(int obj, float x, float y, float z);
        [DllImport("__Internal")] private static extern void EtgAudioSetListener(float x, float y, float z);
        [DllImport("__Internal")] private static extern void EtgAudioUnregister(int obj);

        private static readonly bool Enabled = Application.platform == RuntimePlatform.WebGLPlayer;
        private static bool s_initialized;

        // Every game object the game passed to an AkSoundEngine call, by the id it was given.
        private static readonly Dictionary<int, GameObject> s_objects = new Dictionary<int, GameObject>();
        // Objects that may have 3D sounds playing; their positions are sent every frame.
        private static readonly List<int> s_moving = new List<int>();
        private static readonly List<int> s_scratch = new List<int>();
        private static float s_nextSweep;

        private static void EnsureInit()
        {
            if (s_initialized || !Enabled)
            {
                return;
            }
            s_initialized = true;
            EtgAudioInit(Application.streamingAssetsPath + "/webaudio/");
            var go = new GameObject("EtgAudio");
            go.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<WwiseShimUpdater>();
        }

        /// <summary>Replaces AkSoundEngine.GetAkGameObjectID (same ids: instance id, -1 for null).</summary>
        public static ulong GetAkGameObjectID(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return unchecked((ulong)-1L);
            }
            int id = gameObject.GetInstanceID();
            if (Enabled && !s_objects.ContainsKey(id))
            {
                s_objects[id] = gameObject;
            }
            return unchecked((ulong)id);
        }

        private static int Obj(ulong akId) => unchecked((int)akId);

        /// <summary>Wwise's ID for a name: 32-bit FNV-1 of the lower-case string.</summary>
        public static uint Hash(string name)
        {
            if (name == null)
            {
                return 0;
            }
            uint h = 2166136261;
            for (int i = 0; i < name.Length; i++)
            {
                char c = char.ToLowerInvariant(name[i]);
                h *= 16777619;
                h ^= (byte)c;
            }
            return h;
        }

        private static uint Post(uint eventId, ulong akObj)
        {
            if (!Enabled || eventId == 0)
            {
                return 0;
            }
            EnsureInit();
            int obj = Obj(akObj);
            GameObject go;
            Vector3 p = Vector3.zero;
            if (s_objects.TryGetValue(obj, out go) && go != null)
            {
                p = go.transform.position;
                if (!s_moving.Contains(obj))
                {
                    s_moving.Add(obj);
                }
            }
            return EtgAudioPostEvent(eventId, obj, p.x, p.y, p.z);
        }

        private static int SetRtpc(uint rtpc, float value, int obj, int fadeMs)
        {
            if (Enabled)
            {
                EnsureInit();
                EtgAudioSetRtpc(rtpc, value, obj, fadeMs);
            }
            return AK_Success;
        }

        private static int ActionOnEvent(uint eventId, int actionType, int obj, int fadeMs, uint playingId)
        {
            if (Enabled)
            {
                EnsureInit();
                EtgAudioActionOnEvent(eventId, actionType, obj, fadeMs, playingId);
            }
            return AK_Success;
        }

        // --- AkSoundEnginePINVOKE replacements (names and signatures must match exactly) -------

        public static uint CSharp_GetIDFromString__SWIG_0(string s) => Hash(s);

        // Mods' banks (Expand the Gungeon: a TextAsset in its bundle) are converted at build time like the game's and
        // loaded by the engine with them (port/audio/build_audio.py --mods): the bank is "loaded" already.
        public static int CSharp_LoadAndDecodeBankFromMemory(IntPtr data, uint size, bool decodeBank, string name, bool saveDecoded, out uint bankId)
        {
            bankId = Hash(name);
            return AK_Success;
        }

        public static uint CSharp_PostEvent__SWIG_0(uint e, ulong o, uint flags, IntPtr cb, IntPtr cookie, uint nExt, IntPtr ext, uint playingId) => Post(e, o);
        public static uint CSharp_PostEvent__SWIG_1(uint e, ulong o, uint flags, IntPtr cb, IntPtr cookie, uint nExt, IntPtr ext) => Post(e, o);
        public static uint CSharp_PostEvent__SWIG_2(uint e, ulong o, uint flags, IntPtr cb, IntPtr cookie, uint nExt) => Post(e, o);
        public static uint CSharp_PostEvent__SWIG_3(uint e, ulong o, uint flags, IntPtr cb, IntPtr cookie) => Post(e, o);
        public static uint CSharp_PostEvent__SWIG_4(uint e, ulong o, uint flags) => Post(e, o);
        public static uint CSharp_PostEvent__SWIG_5(uint e, ulong o) => Post(e, o);
        public static uint CSharp_PostEvent__SWIG_6(string e, ulong o, uint flags, IntPtr cb, IntPtr cookie, uint nExt, IntPtr ext, uint playingId) => Post(Hash(e), o);
        public static uint CSharp_PostEvent__SWIG_7(string e, ulong o, uint flags, IntPtr cb, IntPtr cookie, uint nExt, IntPtr ext) => Post(Hash(e), o);
        public static uint CSharp_PostEvent__SWIG_8(string e, ulong o, uint flags, IntPtr cb, IntPtr cookie, uint nExt) => Post(Hash(e), o);
        public static uint CSharp_PostEvent__SWIG_9(string e, ulong o, uint flags, IntPtr cb, IntPtr cookie) => Post(Hash(e), o);
        public static uint CSharp_PostEvent__SWIG_10(string e, ulong o, uint flags) => Post(Hash(e), o);
        public static uint CSharp_PostEvent__SWIG_11(string e, ulong o) => Post(Hash(e), o);

        public static int CSharp_ExecuteActionOnEvent__SWIG_0(uint e, int type, ulong o, int fade, int curve, uint playingId) => ActionOnEvent(e, type, Obj(o), fade, playingId);
        public static int CSharp_ExecuteActionOnEvent__SWIG_1(uint e, int type, ulong o, int fade, int curve) => ActionOnEvent(e, type, Obj(o), fade, 0);
        public static int CSharp_ExecuteActionOnEvent__SWIG_2(uint e, int type, ulong o, int fade) => ActionOnEvent(e, type, Obj(o), fade, 0);
        public static int CSharp_ExecuteActionOnEvent__SWIG_3(uint e, int type, ulong o) => ActionOnEvent(e, type, Obj(o), 0, 0);
        public static int CSharp_ExecuteActionOnEvent__SWIG_4(uint e, int type) => ActionOnEvent(e, type, -1, 0, 0);
        public static int CSharp_ExecuteActionOnEvent__SWIG_5(string e, int type, ulong o, int fade, int curve, uint playingId) => ActionOnEvent(Hash(e), type, Obj(o), fade, playingId);
        public static int CSharp_ExecuteActionOnEvent__SWIG_6(string e, int type, ulong o, int fade, int curve) => ActionOnEvent(Hash(e), type, Obj(o), fade, 0);
        public static int CSharp_ExecuteActionOnEvent__SWIG_7(string e, int type, ulong o, int fade) => ActionOnEvent(Hash(e), type, Obj(o), fade, 0);
        public static int CSharp_ExecuteActionOnEvent__SWIG_8(string e, int type, ulong o) => ActionOnEvent(Hash(e), type, Obj(o), 0, 0);
        public static int CSharp_ExecuteActionOnEvent__SWIG_9(string e, int type) => ActionOnEvent(Hash(e), type, -1, 0, 0);

        public static void CSharp_StopAll__SWIG_0(ulong o)
        {
            if (Enabled)
            {
                EnsureInit();
                EtgAudioStopAll(Obj(o));
            }
        }

        public static void CSharp_StopAll__SWIG_1()
        {
            if (Enabled)
            {
                EnsureInit();
                EtgAudioStopAll(-1);
            }
        }

        public static void CSharp_StopPlayingID__SWIG_0(uint id, int fade, int curve) => StopPlayingId(id, fade);
        public static void CSharp_StopPlayingID__SWIG_1(uint id, int fade) => StopPlayingId(id, fade);
        public static void CSharp_StopPlayingID__SWIG_2(uint id) => StopPlayingId(id, 0);

        private static void StopPlayingId(uint id, int fade)
        {
            if (Enabled && id != 0)
            {
                EnsureInit();
                EtgAudioStopPlayingId(id, fade);
            }
        }

        public static int CSharp_SetRTPCValue__SWIG_0(uint id, float v, ulong o, int fade, int curve, bool bypass) => SetRtpc(id, v, Obj(o), fade);
        public static int CSharp_SetRTPCValue__SWIG_1(uint id, float v, ulong o, int fade, int curve) => SetRtpc(id, v, Obj(o), fade);
        public static int CSharp_SetRTPCValue__SWIG_2(uint id, float v, ulong o, int fade) => SetRtpc(id, v, Obj(o), fade);
        public static int CSharp_SetRTPCValue__SWIG_3(uint id, float v, ulong o) => SetRtpc(id, v, Obj(o), 0);
        public static int CSharp_SetRTPCValue__SWIG_4(uint id, float v) => SetRtpc(id, v, -1, 0);
        public static int CSharp_SetRTPCValue__SWIG_5(string id, float v, ulong o, int fade, int curve, bool bypass) => SetRtpc(Hash(id), v, Obj(o), fade);
        public static int CSharp_SetRTPCValue__SWIG_6(string id, float v, ulong o, int fade, int curve) => SetRtpc(Hash(id), v, Obj(o), fade);
        public static int CSharp_SetRTPCValue__SWIG_7(string id, float v, ulong o, int fade) => SetRtpc(Hash(id), v, Obj(o), fade);
        public static int CSharp_SetRTPCValue__SWIG_8(string id, float v, ulong o) => SetRtpc(Hash(id), v, Obj(o), 0);
        public static int CSharp_SetRTPCValue__SWIG_9(string id, float v) => SetRtpc(Hash(id), v, -1, 0);

        public static int CSharp_ResetRTPCValue__SWIG_0(uint id, ulong o, int fade, int curve, bool bypass) => ResetRtpc(id, Obj(o));
        public static int CSharp_ResetRTPCValue__SWIG_1(uint id, ulong o, int fade, int curve) => ResetRtpc(id, Obj(o));
        public static int CSharp_ResetRTPCValue__SWIG_2(uint id, ulong o, int fade) => ResetRtpc(id, Obj(o));
        public static int CSharp_ResetRTPCValue__SWIG_3(uint id, ulong o) => ResetRtpc(id, Obj(o));
        public static int CSharp_ResetRTPCValue__SWIG_4(uint id) => ResetRtpc(id, -1);
        public static int CSharp_ResetRTPCValue__SWIG_5(string id, ulong o, int fade, int curve, bool bypass) => ResetRtpc(Hash(id), Obj(o));
        public static int CSharp_ResetRTPCValue__SWIG_6(string id, ulong o, int fade, int curve) => ResetRtpc(Hash(id), Obj(o));
        public static int CSharp_ResetRTPCValue__SWIG_7(string id, ulong o, int fade) => ResetRtpc(Hash(id), Obj(o));
        public static int CSharp_ResetRTPCValue__SWIG_8(string id, ulong o) => ResetRtpc(Hash(id), Obj(o));
        public static int CSharp_ResetRTPCValue__SWIG_9(string id) => ResetRtpc(Hash(id), -1);

        private static int ResetRtpc(uint id, int obj)
        {
            if (Enabled)
            {
                EnsureInit();
                EtgAudioResetRtpc(id, obj);
            }
            return AK_Success;
        }

        public static int CSharp_SetSwitch__SWIG_0(uint group, uint value, ulong o) => SetSwitch(group, value, Obj(o));
        public static int CSharp_SetSwitch__SWIG_1(string group, string value, ulong o) => SetSwitch(Hash(group), Hash(value), Obj(o));

        private static int SetSwitch(uint group, uint value, int obj)
        {
            if (Enabled)
            {
                EnsureInit();
                EtgAudioSetSwitch(group, value, obj);
            }
            return AK_Success;
        }

        public static int CSharp_SetState__SWIG_0(uint group, uint state) => SetState(group, state);
        public static int CSharp_SetState__SWIG_1(string group, string state) => SetState(Hash(group), Hash(state));

        private static int SetState(uint group, uint state)
        {
            if (Enabled)
            {
                EnsureInit();
                EtgAudioSetState(group, state);
            }
            return AK_Success;
        }

        public static int CSharp_PostTrigger__SWIG_0(uint trigger, ulong o) => PostTrigger(trigger, Obj(o));
        public static int CSharp_PostTrigger__SWIG_1(string trigger, ulong o) => PostTrigger(Hash(trigger), Obj(o));

        private static int PostTrigger(uint trigger, int obj)
        {
            if (Enabled)
            {
                EnsureInit();
                EtgAudioPostTrigger(trigger, obj);
            }
            return AK_Success;
        }

        public static int CSharp_SetObjectPosition(ulong o, float x, float y, float z, float fx, float fy, float fz, float ux, float uy, float uz)
        {
            if (Enabled && s_initialized)
            {
                EtgAudioSetPosition(Obj(o), x, y, z);
            }
            return AK_Success;
        }

        public static int CSharp_UnregisterGameObjInternal(ulong o)
        {
            if (Enabled && s_initialized)
            {
                Forget(Obj(o));
            }
            return AK_Success;
        }

        private static void Forget(int obj)
        {
            s_objects.Remove(obj);
            s_moving.Remove(obj);
            EtgAudioUnregister(obj);
        }

        private static Type s_listenerType;
        private static Component s_listener;
        private static float s_nextListenerLookup;

        /// <summary>
        /// Where Wwise's listener would be: the game's AkAudioListener (a "listener" object at the primary
        /// player's feet, or on the GameManager when there is no player), found by name since WebShim can't
        /// reference the game's assembly. The main camera until there is one.
        /// </summary>
        private static bool ListenerPosition(out Vector3 position)
        {
            if (s_listener == null && Time.unscaledTime >= s_nextListenerLookup)
            {
                s_nextListenerLookup = Time.unscaledTime + 0.5f;
                if (s_listenerType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        s_listenerType = asm.GetType("AkAudioListener");
                        if (s_listenerType != null)
                        {
                            break;
                        }
                    }
                }
                if (s_listenerType != null)
                {
                    s_listener = UnityEngine.Object.FindObjectOfType(s_listenerType) as Component;
                }
            }
            if (s_listener != null)
            {
                position = s_listener.transform.position;
                return true;
            }
            Camera cam = Camera.main;
            position = cam != null ? cam.transform.position : Vector3.zero;
            return cam != null;
        }

        /// <summary>Per-frame position updates and cleanup of destroyed objects (WwiseShimUpdater).</summary>
        internal static void LateTick()
        {
            Vector3 l;
            if (ListenerPosition(out l))
            {
                EtgAudioSetListener(l.x, l.y, l.z);
            }

            // Objects stay in s_moving while the engine reports 3D voices on them.
            for (int i = s_moving.Count - 1; i >= 0; i--)
            {
                int id = s_moving[i];
                GameObject go;
                if (!s_objects.TryGetValue(id, out go) || go == null)
                {
                    s_moving.RemoveAt(i);
                    continue;
                }
                Vector3 p = go.transform.position;
                if (EtgAudioSetPosition(id, p.x, p.y, p.z) == 0)
                {
                    s_moving.RemoveAt(i);
                }
            }

            // Destroyed objects: like Wwise after UnregisterGameObj, their sounds keep playing
            // where they were, but their switches and RTPCs are forgotten.
            if (Time.unscaledTime >= s_nextSweep)
            {
                s_nextSweep = Time.unscaledTime + 2f;
                s_scratch.Clear();
                foreach (KeyValuePair<int, GameObject> kv in s_objects)
                {
                    if (kv.Value == null)
                    {
                        s_scratch.Add(kv.Key);
                    }
                }
                foreach (int id in s_scratch)
                {
                    Forget(id);
                }
            }
        }
    }

    /// <summary>Drives WwiseShim once per frame; created by WwiseShim on first use.</summary>
    public class WwiseShimUpdater : MonoBehaviour
    {
        private void LateUpdate()
        {
            WwiseShim.LateTick();
        }
    }
}
