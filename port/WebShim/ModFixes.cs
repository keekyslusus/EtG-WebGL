using System.Collections;

namespace WebShim
{
    /// <summary>
    /// Helpers the Patcher's mod bug fixes call (NullSafeCalls, SpriteIdClamps). The bugs are in the Steam game too;
    /// there Mono throws, logs and goes on. In a WebGL release build (exceptions: explicitly thrown only) a call on null
    /// or an index out of range isn't checked and can take the runtime down.
    /// </summary>
    public static class ModFixes
    {
        /// <summary><paramref name="index"/> limited to 0 .. <paramref name="count"/> - 1 (Patcher SpriteIdClamps).</summary>
        public static int ClampIndex(int index, int count)
        {
            return index < 0 || count <= 0 ? 0 : index >= count ? count - 1 : index;
        }

        /// <summary>
        /// <c>Object.Destroy(obj)</c> that first moves <paramref name="keep"/> out of <paramref name="obj"/> (a GameObject)
        /// if it is still inside: Unity 6 disables the components of everything under a GameObject at the Destroy call,
        /// even what is reparented before the end of the frame (Patcher DetachBeforeDestroy).
        /// </summary>
        public static void DestroyDetaching(UnityEngine.Object obj, UnityEngine.Component keep)
        {
            UnityEngine.GameObject go = obj as UnityEngine.GameObject;
            if (go != null && keep != null && keep.transform != go.transform && keep.transform.IsChildOf(go.transform))
            {
                keep.transform.SetParent(go.transform.parent, true);
            }
            UnityEngine.Object.Destroy(obj);
        }

        /// <summary><c>list.Clear()</c> that does nothing on null.</summary>
        public static void ClearIfNotNull(IList list)
        {
            if (list != null)
            {
                list.Clear();
            }
        }
    }
}
