using System.Collections.Generic;
using UnityEngine;
using UnityEngine.LowLevel;

namespace WebShim
{
    /// <summary>
    /// <c>Object.Destroy</c> for the game and the mods (the Patcher sends every call here). Unity 6 disables the
    /// components of a destroyed GameObject's whole hierarchy inside the call by clearing their enabled flags, so what is
    /// moved out of it before the destruction stays disabled for good; Unity 2017.4 didn't leave survivors like that.
    /// EtG relies on it: GameUIRoot.RemoveControlFromMotionGroups destroys a motion group's panel and then moves its
    /// control to a new one (the pickup notification in non-English languages showed only the item's sprite), and dozens
    /// of other places move or re-enable objects after destroying their parent.
    /// Everything else stays Unity 6's: the destroyed objects get no more updates (deferring the destruction kept them
    /// running through LateUpdate: AIBulletBank.LateUpdate threw), coroutines go on (deactivating the hierarchy stopped
    /// the notification's). So the behaviours and renderers below the destroyed GameObject that were enabled are noted,
    /// Unity destroys it as usual, and next frame those whose object survived (was moved out) are enabled again. (Unity
    /// colliders: EtG has its own physics, and dfGUI's controls switch their BoxCollider themselves.)
    /// </summary>
    public static class DestroyShim
    {
        private struct ReviveMarker { }

        private sealed class Pending
        {
            public GameObject Root;
            public readonly List<Component> Enabled = new List<Component>();
        }

        private static readonly List<Pending> s_pending = new List<Pending>();
        private static readonly Stack<Pending> s_pool = new Stack<Pending>();
        private static readonly List<Behaviour> s_behaviours = new List<Behaviour>();
        private static readonly List<Renderer> s_renderers = new List<Renderer>();
        private static bool s_installed;

        public static void Destroy(Object obj)
        {
            GameObject go = obj as GameObject;
            if (go != null && Application.isPlaying && go.transform.childCount > 0)
            {
                Note(go);
            }
            Object.Destroy(obj);
        }

        public static void Destroy(Object obj, float t)
        {
            if (t > 0f)
            {
                Object.Destroy(obj, t); // the hierarchy is torn down when the timer runs out; what left it by then is fine
                return;
            }
            Destroy(obj);
        }

        private static void Note(GameObject root)
        {
            Install();
            Pending p = s_pool.Count > 0 ? s_pool.Pop() : new Pending();
            p.Root = root;
            Transform rootTransform = root.transform;
            root.GetComponentsInChildren(true, s_behaviours);
            foreach (Behaviour b in s_behaviours)
            {
                if (b.enabled && b.transform != rootTransform)
                {
                    p.Enabled.Add(b);
                }
            }
            root.GetComponentsInChildren(true, s_renderers);
            foreach (Renderer r in s_renderers)
            {
                if (r.enabled && r.transform != rootTransform)
                {
                    p.Enabled.Add(r);
                }
            }
            s_behaviours.Clear();
            s_renderers.Clear();
            if (p.Enabled.Count == 0)
            {
                p.Root = null;
                s_pool.Push(p);
                return;
            }
            s_pending.Add(p);
        }

        /// <summary>Start of a frame: enables again what Unity disabled under a GameObject destroyed since and that survived.</summary>
        private static void Revive()
        {
            if (s_pending.Count == 0)
            {
                return;
            }
            for (int i = s_pending.Count - 1; i >= 0; i--)
            {
                Pending p = s_pending[i];
                if (p.Root != null && Application.isPlaying)
                {
                    continue; // destroyed late in a frame: not torn down yet
                }
                if (Application.isPlaying)
                {
                    foreach (Component c in p.Enabled)
                    {
                        if (c == null)
                        {
                            continue; // went with the hierarchy
                        }
                        Behaviour b = c as Behaviour;
                        if (b != null)
                        {
                            b.enabled = true;
                            continue;
                        }
                        Renderer r = c as Renderer;
                        if (r != null)
                        {
                            r.enabled = true;
                        }
                    }
                }
                p.Enabled.Clear();
                p.Root = null;
                s_pending.RemoveAt(i);
                s_pool.Push(p);
            }
        }

        private static void Install()
        {
            if (s_installed)
            {
                return;
            }
            s_installed = true;
            PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
            for (int i = 0; i < root.subSystemList.Length; i++)
            {
                if (root.subSystemList[i].type == typeof(UnityEngine.PlayerLoop.Initialization))
                {
                    PlayerLoopSystem[] subs = root.subSystemList[i].subSystemList ?? new PlayerLoopSystem[0];
                    PlayerLoopSystem[] withRevive = new PlayerLoopSystem[subs.Length + 1];
                    withRevive[0] = new PlayerLoopSystem { type = typeof(ReviveMarker), updateDelegate = Revive };
                    System.Array.Copy(subs, 0, withRevive, 1, subs.Length);
                    root.subSystemList[i].subSystemList = withRevive;
                }
            }
            PlayerLoop.SetPlayerLoop(root);
        }
    }
}
