using UnityEngine;

namespace WebShim
{
    /// <summary>Called by patched catch handlers that used to discard the exception (Patcher LogSwallowedExceptions).</summary>
    public static class ExceptionShim
    {
        public static void LogCaught(object exception)
        {
            Debug.LogWarning("[WebShim] caught and ignored: " + exception);
        }
    }
}
