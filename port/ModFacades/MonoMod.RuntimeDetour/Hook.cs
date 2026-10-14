using System;
using System.Reflection;

namespace MonoMod.RuntimeDetour
{
    /// <summary>
    /// State of one baked hook (a from/to pair, port/ModBaker HookBaker): the trampoline in the hooked
    /// method calls the hook while <see cref="Applied"/> is non-zero, on <see cref="Target"/> for an
    /// instance hook method; otherwise it runs the original body.
    /// </summary>
    public sealed class BakedHook
    {
        /// <summary>Number of applied Hook objects for this pair.</summary>
        public int Applied;
        public object Target;
        private object m_standIn;

        /// <summary>
        /// `this` for an instance hook method: the Hook's target if it is one of the hook's class, else an uninitialized
        /// instance of that class. Mods pass `typeof(X)` as the target of hooks that don't use `this` (Mono calls them on
        /// the Type object); IL2CPP null-checks `this` even for non-virtual calls, so null won't do.
        /// </summary>
        public object TargetAs(Type type)
        {
            if (Target != null && type.IsInstanceOfType(Target))
            {
                return Target;
            }
            return m_standIn ?? (m_standIn = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type));
        }
    }

    /// <summary>
    /// The hooks of the baked mods are static trampolines in the game's code (port/ModBaker). The baker
    /// rewrites every `new Hook(...)` of a mod to the overloads taking the pair's <see cref="BakedHook"/>,
    /// so creating, applying, undoing and disposing a Hook switches its trampoline like the real detour.
    /// </summary>
    public class Hook : IDisposable
    {
        private readonly BakedHook m_slot;
        private readonly object m_target;
        private bool m_applied;

        public Hook(MethodBase from, MethodInfo to)
        {
        }

        public Hook(MethodBase from, MethodInfo to, object target)
        {
        }

        public Hook(MethodBase from, Delegate to)
        {
        }

        public Hook(MethodBase from, MethodInfo to, BakedHook slot) : this(from, to, null, slot)
        {
        }

        public Hook(MethodBase from, MethodInfo to, object target, BakedHook slot)
        {
            m_slot = slot;
            m_target = target;
            Apply(); // MonoMod applies a hook on construction (no HookConfig.ManualApply here)
        }

        /// <summary>Unbaked hooks (none should be left) count as applied: the baker reports what it can't bake.</summary>
        public bool IsApplied
        {
            get { return m_slot == null || m_applied; }
        }

        public bool IsValid
        {
            get { return true; }
        }

        public void Apply()
        {
            if (m_slot == null || m_applied)
            {
                return;
            }
            m_applied = true;
            m_slot.Target = m_target;
            m_slot.Applied++;
        }

        public void Undo()
        {
            if (m_slot == null || !m_applied)
            {
                return;
            }
            m_applied = false;
            m_slot.Applied--;
        }

        public void Dispose()
        {
            Undo();
        }
    }
}
