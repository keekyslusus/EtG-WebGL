using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace WebShim
{
    /// <summary>
    /// WebGL (Unity 2017.4) has no threads. The patcher rewrites
    /// new Thread(...) / Start / Join / Sleep / IsAlive / Abort in game code
    /// to these methods, which run the thread body synchronously on Start (ThreadPool work items: at once).
    /// </summary>
    public static class ThreadShim
    {
        private struct Pending
        {
            public ThreadStart Start;
            public ParameterizedThreadStart ParamStart;
        }

        private static readonly Dictionary<Thread, Pending> s_pending = new Dictionary<Thread, Pending>();

        public static Thread Create(ThreadStart start)
        {
            // Constructing a Thread object is fine on WebGL; only starting it is not.
            var t = new Thread(start);
            s_pending[t] = new Pending { Start = start };
            return t;
        }

        public static Thread Create(ParameterizedThreadStart start)
        {
            var t = new Thread(start);
            s_pending[t] = new Pending { ParamStart = start };
            return t;
        }

        public static void Start(Thread t)
        {
            Run(t, null);
        }

        public static void Start(Thread t, object parameter)
        {
            Run(t, parameter);
        }

        /// <summary>Used for threads that loop forever (e.g. XInput polling): never run them.</summary>
        public static void StartNever(Thread t)
        {
            s_pending.Remove(t);
        }

        private static void Run(Thread t, object parameter)
        {
            Pending p;
            if (!s_pending.TryGetValue(t, out p))
            {
                Debug.LogWarning("[WebShim] Thread.Start on unknown thread, ignoring");
                return;
            }
            s_pending.Remove(t);
            try
            {
                if (p.Start != null) p.Start();
                else if (p.ParamStart != null) p.ParamStart(parameter);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        public static void Join(Thread t) { }

        public static bool Join(Thread t, int millisecondsTimeout) { return true; }

        public static void Sleep(int millisecondsTimeout) { }

        public static bool IsAlive(Thread t) { return false; }

        public static void Abort(Thread t) { s_pending.Remove(t); }

        /// <summary>ThreadPool.QueueUserWorkItem (Ionic.Zip's parallel deflate, shipped with Expand the Gungeon): runs the item now.</summary>
        public static bool QueueUserWorkItem(WaitCallback callback, object state)
        {
            try
            {
                callback(state);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            return true;
        }
    }
}
