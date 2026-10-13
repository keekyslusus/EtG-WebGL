using System;
using System.Reflection;

namespace MonoMod.RuntimeDetour
{
    /// <summary>The hooks of the baked mods are static trampolines in the game's code (port/ModBaker).</summary>
    public class Hook : IDisposable
    {
        public Hook(MethodBase from, MethodInfo to)
        {
        }

        public Hook(MethodBase from, MethodInfo to, object target)
        {
        }

        public Hook(MethodBase from, Delegate to)
        {
        }

        public bool IsApplied
        {
            get { return true; }
        }

        public void Apply()
        {
        }

        public void Undo()
        {
        }

        public void Dispose()
        {
        }
    }
}
