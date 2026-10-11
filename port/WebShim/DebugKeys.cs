using System;
using UnityEngine;

namespace WebShim
{
    /// <summary>
    /// F5-F12 for the editor-only debug actions (EtgWebDebug registers them). Read in Play Mode by
    /// the game itself: Unity 2017's menu hotkeys mis-fire in this project (its Component menu
    /// overflows the 1000-item limit) - F9 once started a player build instead of a toggle.
    /// </summary>
    public class DebugKeys : MonoBehaviour
    {
        /// <summary>Index 0 = F5 ... 7 = F12. Set by editor code; never set in a player.</summary>
        public static readonly Action[] Actions = new Action[8];

        public static void Install()
        {
            if (!Application.isEditor)
            {
                return;
            }
            GameObject go = new GameObject("[WebShim DebugKeys]");
            go.hideFlags = HideFlags.HideInHierarchy;
            DontDestroyOnLoad(go);
            go.AddComponent<DebugKeys>();
        }

        private void Update()
        {
            for (int i = 0; i < Actions.Length; i++)
            {
                if (Actions[i] != null && Input.GetKeyDown(KeyCode.F5 + i))
                {
                    Actions[i]();
                }
            }
        }
    }
}
