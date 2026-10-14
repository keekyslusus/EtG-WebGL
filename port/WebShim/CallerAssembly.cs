using System;
using System.Reflection;

namespace WebShim
{
    /// <summary>
    /// Assembly.GetCallingAssembly() without a call stack (IL2CPP WebGL release builds): the patcher makes every
    /// call to a method that uses it name its calling type first, and the method take it on entry (see
    /// port/Patcher/CallerAssembly.cs).
    /// </summary>
    public static class CallerAssembly
    {
        private static Assembly s_next;

        public static void Set(RuntimeTypeHandle callerType)
        {
            s_next = Type.GetTypeFromHandle(callerType).Assembly;
        }

        public static Assembly Take()
        {
            Assembly next = s_next;
            s_next = null;
            return next;
        }
    }
}
