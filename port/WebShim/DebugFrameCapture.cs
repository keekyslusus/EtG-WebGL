using System;
using System.IO;
using UnityEngine;

namespace WebShim
{
    /// <summary>
    /// Editor-only diagnostics: sits on the main camera right before the bloom effect (EtgWebDebug
    /// moves it there) and, when asked, reads the HDR frame bloom is about to blur. Reports the
    /// hottest pixels and NaN/Inf counts and writes a preview with them marked to etg/debug/.
    /// </summary>
    public class DebugFrameCapture : MonoBehaviour
    {
        public static bool CaptureRequested;

        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            if (CaptureRequested)
            {
                CaptureRequested = false;
                try
                {
                    Capture(source);
                }
                catch (Exception e)
                {
                    Debug.LogError("[EtG Debug] Frame capture failed: " + e);
                }
            }
            Graphics.Blit(source, destination);
        }


        // Values at the corners, edge midpoints and centre: enough to tell which texels a pass reads.
        private static string Grid(Color[] pixels, int w, int h)
        {
            int[] xs = { 0, 1, w / 2, w - 2, w - 1 };
            int[] ys = { 0, 1, h / 2, h - 2, h - 1 };
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (int y in ys)
            {
                sb.Append("\n    y=" + y + ":");
                foreach (int x in xs)
                {
                    Color c = pixels[y * w + x];
                    sb.Append(" (" + x + ")" + c.r.ToString("0.###") + "/" + c.g.ToString("0.###") + "/" + c.b.ToString("0.###"));
                }
            }
            return sb.ToString();
        }

        private static void Capture(RenderTexture source)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = source;
            Texture2D tex = new Texture2D(source.width, source.height, TextureFormat.RGBAFloat, false);
            tex.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;

            Color[] pixels = tex.GetPixels();
            int w = source.width, h = source.height;
            int nan = 0, inf = 0, over10 = 0, over100 = 0;
            float maxValue = float.MinValue;
            int maxIndex = 0;
            Color maxColor = Color.black;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color c = pixels[i];
                if (float.IsNaN(c.r) || float.IsNaN(c.g) || float.IsNaN(c.b))
                {
                    nan++;
                    continue;
                }
                if (float.IsInfinity(c.r) || float.IsInfinity(c.g) || float.IsInfinity(c.b))
                {
                    inf++;
                    continue;
                }
                float m = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                if (m > 10f) over10++;
                if (m > 100f) over100++;
                if (m > maxValue)
                {
                    maxValue = m;
                    maxIndex = i;
                    maxColor = c;
                }
            }

            // Preview: plain image clamped to 1; NaN/Inf magenta, >100 cyan, >10 green.
            Texture2D png = new Texture2D(w, h, TextureFormat.RGBA32, false);
            for (int i = 0; i < pixels.Length; i++)
            {
                Color c = pixels[i];
                Color o;
                if (float.IsNaN(c.r) || float.IsNaN(c.g) || float.IsNaN(c.b) || float.IsInfinity(c.r) || float.IsInfinity(c.g) || float.IsInfinity(c.b))
                {
                    o = Color.magenta;
                }
                else
                {
                    float m = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                    o = m > 100f ? Color.cyan : m > 10f ? Color.green : new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), 1f);
                }
                png.SetPixel(i % w, i / w, o);
            }
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../debug"));
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "bloom_input_" + Time.frameCount + ".png");
            File.WriteAllBytes(file, png.EncodeToPNG());
            Debug.Log("[EtG Debug] Bloom input " + w + "x" + h + " " + source.format + ": NaN=" + nan + " Inf=" + inf +
                      " >10=" + over10 + " >100=" + over100 + " max=" + maxColor + " at (" + (maxIndex % w) + "," + (maxIndex / w) + ") -> " + file + Grid(pixels, w, h));
            Destroy(tex);
            Destroy(png);
            ReplayBloom(source);
        }

        /// <summary>
        /// Re-runs SENaturalBloomAndDirtyLens.OnRenderImage step by step with its own material and
        /// logs every intermediate texture, to see which pass blows up.
        /// </summary>
        private static void ReplayBloom(RenderTexture source)
        {
            Camera cam = Camera.main;
            Component bloom = cam == null ? null : cam.GetComponent("SENaturalBloomAndDirtyLens");
            if (bloom == null)
            {
                return;
            }
            System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            Material material = bloom.GetType().GetField("material", all).GetValue(bloom) as Material;
            if (material == null)
            {
                // OnDisable destroys it (e.g. after F9); the component recreates it the same way.
                material = new Material((Shader)bloom.GetType().GetField("shader", all).GetValue(bloom));
                material.SetFloat("_BloomIntensity", Mathf.Exp((float)bloom.GetType().GetField("bloomIntensity", all).GetValue(bloom)) - 1f);
                material.SetFloat("_LensDirtIntensity", Mathf.Exp((float)bloom.GetType().GetField("lensDirtIntensity", all).GetValue(bloom)) - 1f);
            }
            int iterations = (int)bloom.GetType().GetProperty("IterationCount", all).GetValue(bloom, null);
            float blurSize = (float)bloom.GetType().GetField("blurSize", all).GetValue(bloom);
            System.Text.StringBuilder sb = new System.Text.StringBuilder("[EtG Debug] Bloom replay (iterations=" + iterations +
                " _BloomIntensity=" + material.GetFloat("_BloomIntensity") + " _LensDirtIntensity=" + material.GetFloat("_LensDirtIntensity") +
                " lensDirt=" + (material.GetTexture("_LensDirt") == null ? "null" : material.GetTexture("_LensDirt").name) + ")\n");
            System.Collections.Generic.List<RenderTexture> keep = new System.Collections.Generic.List<RenderTexture>();
            int w = source.width / 2, h = source.height / 2;
            RenderTexture src = source;
            for (int i = 0; i < 6; i++)
            {
                RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, source.format);
                rt.filterMode = FilterMode.Bilinear;
                Graphics.Blit(src, rt, material, 1);
                sb.AppendLine("  level " + i + " downsample " + Stats(rt, "bloom_level" + i + "_" + Time.frameCount));
                src = rt;
                float scale = i <= 1 ? 0.5f : i == 2 ? 0.75f : 1f;
                for (int j = 0; j < iterations; j++)
                {
                    material.SetFloat("_BlurSize", (blurSize * 0.5f + j) * scale);
                    RenderTexture a = RenderTexture.GetTemporary(w, h, 0, source.format);
                    Graphics.Blit(rt, a, material, 2);
                    sb.AppendLine("  level " + i + " blur V " + Stats(a));
                    RenderTexture b = RenderTexture.GetTemporary(w, h, 0, source.format);
                    Graphics.Blit(a, b, material, 3);
                    sb.AppendLine("  level " + i + " blur H " + Stats(b));
                    RenderTexture.ReleaseTemporary(a);
                    keep.Add(rt);
                    rt = b;
                }
                material.SetTexture("_Bloom" + i, rt);
                keep.Add(rt);
                src = rt;
                w /= 2;
                h /= 2;
            }
            RenderTexture result = RenderTexture.GetTemporary(source.width, source.height, 0, source.format);
            Graphics.Blit(source, result, material, 0);
            sb.AppendLine("  composite " + Stats(result));
            RenderTexture.ReleaseTemporary(result);
            foreach (RenderTexture rt in keep)
            {
                RenderTexture.ReleaseTemporary(rt);
            }
            Debug.Log(sb.ToString());
        }

        private static string Stats(RenderTexture rt)
        {
            return Stats(rt, null);
        }

        // pngName != null: also writes the texture (value / 4, clamped) to etg/debug/<pngName>.png
        private static string Stats(RenderTexture rt, string pngName)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;
            Color sum = Color.clear, max = new Color(float.MinValue, float.MinValue, float.MinValue, float.MinValue);
            int bad = 0;
            Color[] pixels = tex.GetPixels();
            foreach (Color c in pixels)
            {
                if (float.IsNaN(c.r + c.g + c.b) || float.IsInfinity(c.r + c.g + c.b))
                {
                    bad++;
                    continue;
                }
                sum += c;
                max = new Color(Mathf.Max(max.r, c.r), Mathf.Max(max.g, c.g), Mathf.Max(max.b, c.b), Mathf.Max(max.a, c.a));
            }
            if (pngName != null)
            {
                Texture2D png = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color c = pixels[i] * 0.25f;
                    png.SetPixel(i % rt.width, i / rt.width, new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), 1f));
                }
                string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../debug"));
                File.WriteAllBytes(Path.Combine(dir, pngName + ".png"), png.EncodeToPNG());
                Destroy(png);
            }
            Destroy(tex);
            Color mean = sum / Mathf.Max(1, pixels.Length - bad);
            return rt.width + "x" + rt.height + " mean=" + mean + " max=" + max + " NaN/Inf=" + bad + (pngName != null && pngName.StartsWith("bloom_level0") ? Grid(pixels, rt.width, rt.height) : "");
        }
    }
}
