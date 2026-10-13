using System.Reflection;

namespace WebShim
{
    /// <summary>
    /// Stand-in for delegates mods compile with Reflection.Emit (DynamicMethod), which IL2CPP doesn't have:
    /// Mod the Gungeon API's ReflectionHelper.CreateDelegate makes an `object (object target, object[] args)`
    /// that calls a method or constructor. The patcher turns its body into
    /// `new DynamicMethodDelegate(new ReflectionInvoker(method).Invoke)` (ReflectionInvokerBodies).
    /// </summary>
    public sealed class ReflectionInvoker
    {
        private readonly MethodBase m_method;
        private readonly int m_parameterCount;

        public ReflectionInvoker(MethodBase method)
        {
            m_method = method;
            m_parameterCount = method.GetParameters().Length;
        }

        public object Invoke(object target, object[] args)
        {
            if ((args == null ? 0 : args.Length) != m_parameterCount)
            {
                throw new TargetParameterCountException();
            }
            ConstructorInfo constructor = m_method as ConstructorInfo;
            if (constructor != null)
            {
                return constructor.Invoke(args);
            }
            return m_method.Invoke(target, args);
        }
    }
}
