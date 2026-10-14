using System;
using System.Reflection;
using UnityEngine;

namespace WebShim
{
    /// <summary>
    /// Renderer.material / materials, patched in for every call (port/Patcher). In the editor's Play Mode a prefab that
    /// a player would load from a bundle is the project's asset (BundleShim's simulation), and the editor refuses
    /// `material` on an asset's renderer ("Not allowed to access Renderer.material on prefab object") and returns null:
    /// Expand the Gungeon reads `RewardManager.A_Chest ... renderer.material.shader` while it starts. There the shared
    /// material is returned instead (what reading it gives in a player; the asset is not instanced or changed).
    /// In a player this is the plain property.
    /// </summary>
    public static class EditorPrefabShim
    {
        private static Func<UnityEngine.Object, bool> s_isPersistent;

        public static Material GetMaterial(Renderer renderer)
        {
            return IsAsset(renderer) ? renderer.sharedMaterial : renderer.material;
        }

        public static Material[] GetMaterials(Renderer renderer)
        {
            return IsAsset(renderer) ? renderer.sharedMaterials : renderer.materials;
        }

        private static bool IsAsset(Renderer renderer)
        {
            if (!Application.isEditor || (object)renderer == null)
            {
                return false;
            }
            if (s_isPersistent == null)
            {
                Type utility = Type.GetType("UnityEditor.EditorUtility, UnityEditor");
                MethodInfo method = utility == null ? null : utility.GetMethod("IsPersistent", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(UnityEngine.Object) }, null);
                s_isPersistent = method == null ? (o => false) : (Func<UnityEngine.Object, bool>)Delegate.CreateDelegate(typeof(Func<UnityEngine.Object, bool>), method);
            }
            return renderer != null && s_isPersistent(renderer);
        }
    }
}
