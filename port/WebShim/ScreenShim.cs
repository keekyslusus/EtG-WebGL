using System.Collections.Generic;
using UnityEngine;

namespace WebShim
{
    /// <summary>
    /// Screen.resolutions is empty on WebGL, and EtG's options menu indexes into it
    /// (ArgumentOutOfRange in BraveOptionsMenuItem, IndexOutOfRange in GetRecommendedResolution).
    /// Offer the current canvas size plus common 16:9 sizes up to it.
    /// </summary>
    public static class ScreenShim
    {
        private static readonly int[][] Common =
        {
            new[] { 960, 540 }, new[] { 1280, 720 }, new[] { 1600, 900 }, new[] { 1920, 1080 },
            new[] { 2560, 1440 }, new[] { 3840, 2160 },
        };

        public static Resolution[] GetResolutions()
        {
            Resolution[] real = Screen.resolutions;
            if (real != null && real.Length > 0)
            {
                return real;
            }
            Resolution current = Screen.currentResolution;
            int maxW = Mathf.Max(Screen.width, current.width);
            int maxH = Mathf.Max(Screen.height, current.height);
            var list = new List<Resolution>();
            foreach (int[] size in Common)
            {
                if (size[0] <= maxW && size[1] <= maxH)
                {
                    list.Add(Make(size[0], size[1], current.refreshRate));
                }
            }
            Resolution canvas = Make(Screen.width, Screen.height, current.refreshRate);
            if (!list.Exists(r => r.width == canvas.width && r.height == canvas.height))
            {
                list.Add(canvas);
            }
            list.Sort((a, b) => (a.width * a.height).CompareTo(b.width * b.height));
            return list.ToArray();
        }

        private static Resolution Make(int width, int height, int refreshRate)
        {
            var r = new Resolution();
            r.width = width;
            r.height = height;
            r.refreshRate = refreshRate > 0 ? refreshRate : 60;
            return r;
        }
    }
}
