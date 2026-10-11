using UnityEngine;
using UnityEngine.Rendering;

namespace WebShim
{
    /// <summary>
    /// EtG lights its levels with flat ambient: Dungeon sets RenderSettings.ambientLight from the
    /// tileset's deco settings and RoomHandler fades ambientIntensity for dark rooms. Shaders read
    /// ambient from the SH probe (ShadeSH9), and in the port that probe stays black - the scenes'
    /// ripped LightingData never produce one - so everything lit only by ambient (walls, floors)
    /// came out near-black. Rebuild the flat probe from the current settings before each camera
    /// renders, the way the engine does for Flat ambient in the original game.
    /// </summary>
    public static class AmbientProbeShim
    {
        private static bool s_installed;

        public static void Install()
        {
            if (s_installed)
            {
                return;
            }
            s_installed = true;
            Camera.onPreCull += OnPreCull;
        }

        private static void OnPreCull(Camera camera)
        {
            if (RenderSettings.ambientMode != AmbientMode.Flat)
            {
                return;
            }
            SphericalHarmonicsL2 probe = new SphericalHarmonicsL2();
            probe.AddAmbientLight(RenderSettings.ambientLight * RenderSettings.ambientIntensity);
            if (RenderSettings.ambientProbe != probe)
            {
                RenderSettings.ambientProbe = probe;
            }
        }
    }
}
