using FullInspector.Internal;
using UnityEditor;

// Unity 6 imports a prefab by loading and re-serializing it, and FullInspector answers every OnBeforeSerialize with
// SaveState(): its serialized strings are rewritten from the fields it restored. A restore that failed during import
// (a reference to a prefab that isn't loaded yet arrives as a plain UnityEngine.Object, which FullSerializer can't
// assign to a GameObject field) is then saved into the import result - BossStatues lost its levelData that way. The
// port never edits FullInspector data, so outside Play Mode it isn't saved back at all and imports, bundles and builds
// keep the original strings. In Play Mode it stays on: Instantiate relies on SaveState to give clones the live state.
[InitializeOnLoad]
public static class EtgFullInspector
{
    static EtgFullInspector()
    {
        Apply(EditorApplication.isPlayingOrWillChangePlaymode);
        EditorApplication.playModeStateChanged += state =>
            Apply(state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.EnteredPlayMode);
    }

    private static void Apply(bool playing)
    {
        fiSerializationManager.DisableAutomaticSerialization = !playing;
    }
}
