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
