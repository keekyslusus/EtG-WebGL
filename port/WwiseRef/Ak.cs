using System.Runtime.InteropServices;

namespace WwiseRef;

/// <summary>The SWIG entry points of the Unity integration's AkSoundEngine.dll that the harness uses
/// (same signatures as the game's AkSoundEnginePINVOKE).</summary>
internal static class Ak
{
    private const string Dll = "AkSoundEngine";
    private const UnmanagedType W = UnmanagedType.LPWStr;

    [DllImport(Dll)] public static extern IntPtr CSharp_new_AkMemSettings();
    [DllImport(Dll)] public static extern void CSharp_AkMemSettings_uMaxNumPools_set(IntPtr s, uint v);
    [DllImport(Dll)] public static extern IntPtr CSharp_new_AkDeviceSettings();
    [DllImport(Dll)] public static extern void CSharp_GetDefaultDeviceSettings(IntPtr s);
    [DllImport(Dll)] public static extern IntPtr CSharp_new_AkStreamMgrSettings();
    [DllImport(Dll)] public static extern void CSharp_AkStreamMgrSettings_uMemorySize_set(IntPtr s, uint v);
    [DllImport(Dll)] public static extern IntPtr CSharp_new_AkInitSettings();
    [DllImport(Dll)] public static extern void CSharp_GetDefaultInitSettings(IntPtr s);
    [DllImport(Dll)] public static extern void CSharp_AkInitSettings_uDefaultPoolSize_set(IntPtr s, uint v);
    [DllImport(Dll)] public static extern void CSharp_AkInitSettings_uMonitorPoolSize_set(IntPtr s, uint v);
    [DllImport(Dll)] public static extern void CSharp_AkInitSettings_uMonitorQueuePoolSize_set(IntPtr s, uint v);
    [DllImport(Dll)] public static extern void CSharp_AkInitSettings_szPluginDLLPath_set(IntPtr s, [MarshalAs(W)] string v);
    [DllImport(Dll)] public static extern void CSharp_AkInitSettings_bUseLEngineThread_set(IntPtr s, bool v);
    [DllImport(Dll)] public static extern IntPtr CSharp_AkInitSettings_settingsMainOutput_get(IntPtr s);
    [DllImport(Dll)] public static extern void CSharp_AkOutputSettings_audioDeviceShareset_set(IntPtr s, uint v);
    [DllImport(Dll)] public static extern void CSharp_AkOutputSettings_channelConfig_set(IntPtr s, IntPtr config);
    [DllImport(Dll)] public static extern IntPtr CSharp_new_AkChannelConfig__SWIG_0();
    [DllImport(Dll)] public static extern void CSharp_AkChannelConfig_SetStandard(IntPtr config, uint mask);
    [DllImport(Dll)] public static extern uint CSharp_AkChannelConfig_uNumChannels_get(IntPtr config);
    [DllImport(Dll)] public static extern IntPtr CSharp_GetSpeakerConfiguration__SWIG_1();
    [DllImport(Dll)] public static extern IntPtr CSharp_new_AkPlatformInitSettings();
    [DllImport(Dll)] public static extern void CSharp_GetDefaultPlatformInitSettings(IntPtr s);
    [DllImport(Dll)] public static extern void CSharp_AkPlatformInitSettings_uLEngineDefaultPoolSize_set(IntPtr s, uint v);
    [DllImport(Dll)] public static extern void CSharp_AkPlatformInitSettings_fLEngineDefaultPoolRatioThreshold_set(IntPtr s, float v);
    [DllImport(Dll)] public static extern void CSharp_AkPlatformInitSettings_uSampleRate_set(IntPtr s, uint v);
    [DllImport(Dll)] public static extern IntPtr CSharp_new_AkMusicSettings();
    [DllImport(Dll)] public static extern void CSharp_GetDefaultMusicSettings(IntPtr s);
    [DllImport(Dll)] public static extern IntPtr CSharp_new_AkSpatialAudioInitSettings();
    [DllImport(Dll)] public static extern void CSharp_AkSpatialAudioInitSettings_uPoolSize_set(IntPtr s, uint v);
    [DllImport(Dll)] public static extern int CSharp_Init(IntPtr mem, IntPtr stream, IntPtr device, IntPtr init, IntPtr platform, IntPtr music, IntPtr spatial, uint preparePoolSize);
    [DllImport(Dll)] public static extern void CSharp_Term();
    [DllImport(Dll)] public static extern int CSharp_SetBasePath([MarshalAs(W)] string path);
    [DllImport(Dll)] public static extern int CSharp_UnregisterGameObjInternal(ulong obj);
    [DllImport(Dll)] public static extern int CSharp_SetCurrentLanguage([MarshalAs(W)] string lang);
    [DllImport(Dll)] public static extern int CSharp_LoadBank__SWIG_0([MarshalAs(W)] string name, int pool, out uint bankId);
    [DllImport(Dll)] public static extern int CSharp_LoadAndDecodeBankFromMemory(IntPtr data, uint size, bool decode, [MarshalAs(W)] string name, bool saveDecoded, out uint bankId);
    [DllImport(Dll)] public static extern int CSharp_RegisterGameObjInternal(ulong obj);
    [DllImport(Dll)] public static extern int CSharp_AddDefaultListener(ulong obj);
    [DllImport(Dll)] public static extern int CSharp_SetObjectPosition(ulong obj, float x, float y, float z, float fx, float fy, float fz, float ux, float uy, float uz);
    [DllImport(Dll)] public static extern uint CSharp_PostEvent__SWIG_5(uint eventId, ulong obj);
    [DllImport(Dll)] public static extern int CSharp_SetState__SWIG_0(uint group, uint state);
    [DllImport(Dll)] public static extern int CSharp_SetSwitch__SWIG_0(uint group, uint value, ulong obj);
    [DllImport(Dll)] public static extern int CSharp_SetRTPCValue__SWIG_4(uint rtpc, float value);
    [DllImport(Dll)] public static extern int CSharp_SetRTPCValue__SWIG_0(uint rtpc, float value, ulong obj, int fadeMs, int curve, bool bypassInterpolation);
    public const ulong InvalidGameObject = ulong.MaxValue; // AK_INVALID_GAME_OBJECT: global RTPC value
    [DllImport(Dll)] public static extern void CSharp_StopAll__SWIG_1();
    [DllImport(Dll)] public static extern int CSharp_SetBusEffect__SWIG_0(uint bus, uint fxIndex, uint fxShareSet);
    [DllImport(Dll)] public static extern uint CSharp_DynamicSequenceOpen__SWIG_3(ulong obj);
    [DllImport(Dll)] public static extern IntPtr CSharp_DynamicSequenceLockPlaylist(uint playingId);
    [DllImport(Dll)] public static extern int CSharp_DynamicSequenceUnlockPlaylist(uint playingId);
    [DllImport(Dll)] public static extern int CSharp_AkPlaylist_Enqueue__SWIG_4(IntPtr playlist, uint audioNode);
    [DllImport(Dll)] public static extern int CSharp_DynamicSequencePlay__SWIG_2(uint playingId);
    [DllImport(Dll)] public static extern int CSharp_DynamicSequenceClose(uint playingId);
    [DllImport(Dll)] public static extern int CSharp_SetActorMixerEffect(uint node, uint fxIndex, uint fxShareSet);
    [DllImport(Dll)] public static extern void CSharp_SetRandomSeed(uint seed);
    [DllImport(Dll)] public static extern int CSharp_RenderAudio__SWIG_1();
    [DllImport(Dll)] public static extern int CSharp_StartOutputCapture([MarshalAs(W)] string file);
    [DllImport(Dll)] public static extern int CSharp_StopOutputCapture();

    public delegate void ErrorLogger([MarshalAs(UnmanagedType.LPStr)] string message);
    [DllImport(Dll)] public static extern void CSharp_SetErrorLogger__SWIG_0(ErrorLogger logger);
    [DllImport(Dll)] public static extern int CSharp_Suspend__SWIG_0(bool renderAnyway);
    public static readonly ErrorLogger Logger = m => Console.WriteLine("   wwise says: " + m);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetDllDirectory(string path);

    public const int Success = 1;
    public const uint SystemShareset = 3859886410; // "System" audio device (Init.bnk); "No_Output" mixes silence

    public static uint Hash(string name)
    {
        uint h = 2166136261;
        foreach (char c in name.ToLowerInvariant())
        {
            h *= 16777619;
            h ^= (byte)c;
        }
        return h;
    }

    public static void Check(int result, string what)
    {
        if (result != Success)
        {
            throw new InvalidOperationException($"{what} failed: AKRESULT {result}");
        }
    }

    /// <summary>Initializes Wwise like AkSoundEngineController.Init, then suspends the output: nothing reaches
    /// the sound card, and without the engine thread RenderAudio() renders as the (virtual, see Clock) time
    /// advances. Output capture file names are relative to banksDir (AddBasePath is not implemented).</summary>
    public static void Init(string pluginsDir, string banksDir, bool engineThread = false)
    {
        var mem = CSharp_new_AkMemSettings();
        CSharp_AkMemSettings_uMaxNumPools_set(mem, 20);
        var device = CSharp_new_AkDeviceSettings();
        CSharp_GetDefaultDeviceSettings(device);
        var stream = CSharp_new_AkStreamMgrSettings();
        CSharp_AkStreamMgrSettings_uMemorySize_set(stream, 2048 * 1024);
        var init = CSharp_new_AkInitSettings();
        CSharp_GetDefaultInitSettings(init);
        CSharp_AkInitSettings_uDefaultPoolSize_set(init, 16384 * 1024);
        CSharp_AkInitSettings_uMonitorPoolSize_set(init, 128 * 1024);
        CSharp_AkInitSettings_uMonitorQueuePoolSize_set(init, 64 * 1024);
        CSharp_AkInitSettings_szPluginDLLPath_set(init, pluginsDir + Path.DirectorySeparatorChar);
        CSharp_AkInitSettings_bUseLEngineThread_set(init, engineThread);
        var output = CSharp_AkInitSettings_settingsMainOutput_get(init);
        CSharp_AkOutputSettings_audioDeviceShareset_set(output, SystemShareset);
        var stereo = CSharp_new_AkChannelConfig__SWIG_0();
        CSharp_AkChannelConfig_SetStandard(stereo, 0x3); // front left + front right
        CSharp_AkOutputSettings_channelConfig_set(output, stereo);
        var platform = CSharp_new_AkPlatformInitSettings();
        CSharp_GetDefaultPlatformInitSettings(platform);
        CSharp_AkPlatformInitSettings_uLEngineDefaultPoolSize_set(platform, 16384 * 1024);
        CSharp_AkPlatformInitSettings_fLEngineDefaultPoolRatioThreshold_set(platform, 0.95f);
        CSharp_AkPlatformInitSettings_uSampleRate_set(platform, 48000);
        var music = CSharp_new_AkMusicSettings();
        CSharp_GetDefaultMusicSettings(music);
        var spatial = CSharp_new_AkSpatialAudioInitSettings();
        CSharp_AkSpatialAudioInitSettings_uPoolSize_set(spatial, 8194 * 1024);
        Check(CSharp_Init(mem, stream, device, init, platform, music, spatial, 0), "Init");
        CSharp_SetErrorLogger__SWIG_0(Logger);
        Check(CSharp_Suspend__SWIG_0(true), "Suspend"); // keep rendering (on Clock), send nothing to the sound card
        Check(CSharp_SetBasePath(banksDir + Path.DirectorySeparatorChar), "SetBasePath");
        CSharp_SetCurrentLanguage("English(US)");
        Check(CSharp_LoadBank__SWIG_0("Init.bnk", -1, out _), "LoadBank Init.bnk");
        Check(CSharp_LoadBank__SWIG_0("SFX.bnk", -1, out _), "LoadBank SFX.bnk");
        uint channels = CSharp_AkChannelConfig_uNumChannels_get(CSharp_GetSpeakerConfiguration__SWIG_1());
        if (channels != 2)
        {
            throw new InvalidOperationException($"main output has {channels} channels, expected 2");
        }
    }
}
