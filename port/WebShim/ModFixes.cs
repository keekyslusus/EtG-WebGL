using System.Collections;

namespace WebShim
{
    /// <summary>
    /// Null-safe stand-ins for single calls in mod code (Patcher NullSafeCalls): a call on a field the mod only fills
    /// in some paths. The mod bug is in the Steam game too; there Mono throws, logs and goes on. In a WebGL release
    /// build (exceptions: explicitly thrown only) a call on null isn't checked and can take the runtime down.
    /// </summary>
    public static class ModFixes
    {
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
