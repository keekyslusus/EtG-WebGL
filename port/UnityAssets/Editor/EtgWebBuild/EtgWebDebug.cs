using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Play Mode diagnostics written to the console / Editor.log, so rendering state can be compared
// with the original game without a debugger. Same C# 4 restrictions as EtgWebBuild.cs.
public static class EtgWebDebug
{
    [MenuItem("EtG Web/Debug/Dump Lighting _F5")]
    public static void DumpLighting()
    {
        StringBuilder sb = new StringBuilder("[EtG Debug] ==== Lighting dump ====\n");

        sb.AppendLine("Ambient: mode=" + RenderSettings.ambientMode + " light=" + RenderSettings.ambientLight +
                      " intensity=" + RenderSettings.ambientIntensity + " sky=" + RenderSettings.ambientSkyColor);
        SphericalHarmonicsL2 sh = RenderSettings.ambientProbe;
        sb.AppendLine("AmbientProbe DC (r,g,b): " + sh[0, 0] + ", " + sh[1, 0] + ", " + sh[2, 0]);
        sb.AppendLine("Quality: " + QualitySettings.names[QualitySettings.GetQualityLevel()] + " pixelLights=" + QualitySettings.pixelLightCount +
                      " colorSpace=" + QualitySettings.activeColorSpace + " gfx=" + SystemInfo.graphicsDeviceType);

        Light[] lights = UnityEngine.Object.FindObjectsOfType<Light>();
        Dictionary<string, int> lightTypes = new Dictionary<string, int>();
        foreach (Light l in lights)
        {
            string key = l.type + "/" + l.renderMode + (l.isActiveAndEnabled ? "" : "/off");
            int n;
            lightTypes.TryGetValue(key, out n);
            lightTypes[key] = n + 1;
        }
        sb.Append("Lights: " + lights.Length + " ");
        foreach (KeyValuePair<string, int> p in lightTypes)
        {
            sb.Append(p.Key + "=" + p.Value + " ");
        }
        sb.AppendLine();
        for (int i = 0; i < lights.Length && i < 12; i++)
        {
            Light l = lights[i];
            sb.AppendLine("  light '" + l.name + "' " + l.type + " color=" + l.color + " intensity=" + l.intensity + " range=" + l.range +
                          " mode=" + l.renderMode + " mask=" + l.cullingMask + " enabled=" + l.isActiveAndEnabled);
        }

        foreach (Camera c in Camera.allCameras)
        {
            List<string> comps = new List<string>();
            foreach (MonoBehaviour mb in c.GetComponents<MonoBehaviour>())
            {
                comps.Add(mb == null ? "<missing>" : mb.GetType().Name + (mb.enabled ? "" : "(off)"));
            }
            sb.AppendLine("Camera '" + c.name + "' depth=" + c.depth + " mask=" + c.cullingMask + " path=" + c.actualRenderingPath +
                          " clear=" + c.clearFlags + " target=" + (c.targetTexture == null ? "screen" : c.targetTexture.format + " " + c.targetTexture.width + "x" + c.targetTexture.height) +
                          " hdr=" + c.allowHDR + " [" + string.Join(", ", comps.ToArray()) + "]");
        }

        // Shaders in use by visible renderers, with unsupported ones flagged.
        Dictionary<Shader, int> shaders = new Dictionary<Shader, int>();
        foreach (Renderer r in UnityEngine.Object.FindObjectsOfType<Renderer>())
        {
            if (!r.isVisible)
            {
                continue;
            }
            foreach (Material m in r.sharedMaterials)
            {
                if (m == null || m.shader == null)
                {
                    continue;
                }
                int n;
                shaders.TryGetValue(m.shader, out n);
                shaders[m.shader] = n + 1;
            }
        }
        sb.AppendLine("Visible shaders: " + shaders.Count);
        foreach (KeyValuePair<Shader, int> p in shaders)
        {
            sb.AppendLine("  " + (p.Key.isSupported ? "   " : "!! ") + p.Key.name + " x" + p.Value);
        }

        DumpComponentFields(sb, "Pixelator");
        DumpComponentFields(sb, "Dungeon"); // PlayerIsLight / PlayerLightIntensity for dark rooms
        // EtG's own lights (Pixelator renders them into the lighting buffer; no Unity Light involved)
        DumpVisibleComponents(sb, "ShadowSystem", 25);
        DumpVisibleComponents(sb, "AdditionalBraveLight", 25);
        Debug.Log(sb.ToString());
    }

    private static bool s_shadowQuadsHidden;

    // Each ShadowSystem draws a quad with Brave/Tinted Multiply (Double) over its light: 0.5 keeps the
    // scene, more brightens, less darkens. Hiding them shows whether a dark spot comes from there.
    [MenuItem("EtG Web/Debug/Toggle ShadowSystem Quads _F11")]
    public static void ToggleShadowQuads()
    {
        s_shadowQuadsHidden = !s_shadowQuadsHidden;
        int n = 0;
        foreach (MonoBehaviour mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
        {
            if (mb != null && mb.GetType().Name == "ShadowSystem" && mb.GetComponent<Renderer>() != null)
            {
                mb.GetComponent<Renderer>().enabled = !s_shadowQuadsHidden;
                n++;
            }
        }
        Debug.Log("[EtG Debug] ShadowSystem quads " + (s_shadowQuadsHidden ? "hidden" : "shown") + ": " + n);
    }

    // Everything drawn over the screen centre: put an artifact there and see which object/shader it is.
    [MenuItem("EtG Web/Debug/Dump Renderers At Center _F12")]
    public static void DumpRenderersAtCenter()
    {
        Camera cam = Camera.main;
        if (cam == null)
        {
            Debug.LogWarning("[EtG Debug] No main camera");
            return;
        }
        Vector3 centre = cam.ViewportToWorldPoint(new Vector3(0.5f, 0.5f, 0f));
        StringBuilder sb = new StringBuilder("[EtG Debug] ==== Renderers at screen centre " + centre + " ====\n");
        List<Renderer> hits = new List<Renderer>();
        foreach (Renderer r in UnityEngine.Object.FindObjectsOfType<Renderer>())
        {
            Bounds b = r.bounds;
            if (r.enabled && r.gameObject.activeInHierarchy && centre.x >= b.min.x && centre.x <= b.max.x && centre.y >= b.min.y && centre.y <= b.max.y)
            {
                hits.Add(r);
            }
        }
        hits.Sort(delegate(Renderer a, Renderer b) { return a.bounds.size.sqrMagnitude.CompareTo(b.bounds.size.sqrMagnitude); });
        foreach (Renderer r in hits)
        {
            Material m = r.sharedMaterial;
            sb.AppendLine("  '" + GetPath(r.transform) + "' layer=" + LayerMask.LayerToName(r.gameObject.layer) + " size=" + r.bounds.size +
                          " visible=" + r.isVisible + " shader=" + (m == null || m.shader == null ? "null" : m.shader.name) +
                          " mat=" + (m == null ? "null" : m.name) + " queue=" + (m == null ? 0 : m.renderQueue) +
                          " keywords=" + (m == null ? "" : string.Join(" ", m.shaderKeywords)));
        }
        sb.AppendLine(hits.Count + " renderers");
        Debug.Log(sb.ToString());
    }

    private static string GetPath(Transform t)
    {
        string path = t.name;
        for (int i = 0; i < 3 && t.parent != null; i++)
        {
            t = t.parent;
            path = t.name + "/" + path;
        }
        return path;
    }

    // Bisecting the Pixelator stack: switch one stage off at a time and see what an artifact follows.
    [MenuItem("EtG Web/Debug/Toggle Pixelator GBuffer Lights _F7")]
    public static void TogglePixelatorGBuffer() { ToggleField("Pixelator", "DoRenderGBuffer"); }

    [MenuItem("EtG Web/Debug/Toggle Pixelator Occlusion Layer _F8")]
    public static void TogglePixelatorOcclusion() { ToggleField("Pixelator", "DoOcclusionLayer"); }

    [MenuItem("EtG Web/Debug/Toggle Bloom _F9")]
    public static void ToggleBloom() { ToggleEnabled("SENaturalBloomAndDirtyLens"); }

    [MenuItem("EtG Web/Debug/Toggle Fullscreen Effect _F10")]
    public static void ToggleFullscreenEffect() { ToggleEnabled("GenericFullscreenEffect"); }

    private static void ToggleField(string typeName, string fieldName)
    {
        foreach (MonoBehaviour mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
        {
            if (mb == null || mb.GetType().Name != typeName)
            {
                continue;
            }
            FieldInfo f = mb.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            bool value = !(bool)f.GetValue(mb);
            f.SetValue(mb, value);
            Debug.Log("[EtG Debug] " + typeName + "." + fieldName + " = " + value);
            return;
        }
        Debug.LogWarning("[EtG Debug] No active " + typeName);
    }

    private static void ToggleEnabled(string typeName)
    {
        int n = 0;
        bool value = false;
        foreach (MonoBehaviour mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
        {
            if (mb != null && mb.GetType().Name == typeName)
            {
                value = !mb.enabled;
                mb.enabled = value;
                n++;
            }
        }
        Debug.Log("[EtG Debug] " + typeName + " enabled = " + value + " (" + n + ")");
    }

    // Saves the shadow/light texture of the ShadowSystem closest to the screen centre to etg/debug/.
    [MenuItem("EtG Web/Debug/Save Nearest Light Texture _F6")]
    public static void SaveNearestLightTexture()
    {
        Camera cam = Camera.main;
        MonoBehaviour best = null;
        float bestDistance = float.MaxValue;
        foreach (MonoBehaviour mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
        {
            if (mb == null || mb.GetType().Name != "ShadowSystem" || !mb.isActiveAndEnabled || cam == null)
            {
                continue;
            }
            Vector3 vp = cam.WorldToViewportPoint(mb.transform.position);
            float d = new Vector2(vp.x - 0.5f, vp.y - 0.5f).sqrMagnitude;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = mb;
            }
        }
        Renderer renderer = best == null ? null : best.GetComponent<Renderer>();
        RenderTexture rt = renderer == null ? null : renderer.sharedMaterial.mainTexture as RenderTexture;
        if (rt == null)
        {
            Debug.LogWarning("[EtG Debug] No ShadowSystem with a RenderTexture near the screen centre");
            return;
        }
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = previous;

        Color min = new Color(float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue);
        Color max = new Color(float.MinValue, float.MinValue, float.MinValue, float.MinValue);
        Color[] pixels = tex.GetPixels();
        int nan = 0;
        foreach (Color c in pixels)
        {
            if (float.IsNaN(c.r) || float.IsNaN(c.g) || float.IsNaN(c.b) || float.IsNaN(c.a))
            {
                nan++;
                continue;
            }
            min = new Color(Mathf.Min(min.r, c.r), Mathf.Min(min.g, c.g), Mathf.Min(min.b, c.b), Mathf.Min(min.a, c.a));
            max = new Color(Mathf.Max(max.r, c.r), Mathf.Max(max.g, c.g), Mathf.Max(max.b, c.b), Mathf.Max(max.a, c.a));
        }
        Texture2D png = new Texture2D(rt.width, rt.height * 2, TextureFormat.RGBA32, false);
        for (int y = 0; y < rt.height; y++)
        {
            for (int x = 0; x < rt.width; x++)
            {
                Color c = pixels[y * rt.width + x];
                png.SetPixel(x, y + rt.height, new Color(c.r, c.g, c.b, 1f)); // top: rgb
                png.SetPixel(x, y, new Color(c.a, c.a, c.a, 1f));              // bottom: alpha
            }
        }
        string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../debug"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "light_" + best.transform.position.x.ToString("0") + "_" + best.transform.position.y.ToString("0") + ".png");
        File.WriteAllBytes(file, png.EncodeToPNG());
        Material mat = renderer.sharedMaterial;
        Debug.Log("[EtG Debug] Light texture " + rt.width + "x" + rt.height + " " + rt.format + " of '" + best.name + "' at " + best.transform.position +
                  " min=" + min + " max=" + max + " NaN=" + nan + " quadShader=" + mat.shader.name +
                  " tint=" + (mat.HasProperty("_TintColor") ? mat.GetColor("_TintColor").ToString() : "-") + " -> " + file);
        UnityEngine.Object.DestroyImmediate(tex);
        UnityEngine.Object.DestroyImmediate(png);
    }

    // Every active component of this type whose position is inside the main camera's view.
    private static void DumpVisibleComponents(StringBuilder sb, string typeName, int max)
    {
        Camera cam = Camera.main;
        int shown = 0, total = 0;
        foreach (MonoBehaviour mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
        {
            if (mb == null || mb.GetType().Name != typeName || !mb.isActiveAndEnabled)
            {
                continue;
            }
            total++;
            if (cam != null)
            {
                Vector3 vp = cam.WorldToViewportPoint(mb.transform.position);
                if (vp.x < -0.1f || vp.x > 1.1f || vp.y < -0.1f || vp.y > 1.1f)
                {
                    continue;
                }
            }
            if (shown++ >= max)
            {
                continue;
            }
            sb.Append("  " + typeName + " '" + mb.name + "' at " + mb.transform.position + ": ");
            AppendFields(sb, mb);
            sb.AppendLine();
        }
        sb.AppendLine(typeName + ": " + shown + " in view, " + total + " active");
    }

    private static void AppendFields(StringBuilder sb, MonoBehaviour mb)
    {
        foreach (FieldInfo f in mb.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            Type t = f.FieldType;
            bool simple = t.IsPrimitive || t.IsEnum || t == typeof(Color) || t == typeof(Vector2) || t == typeof(Vector3) || t == typeof(Vector4);
            bool shown = f.IsPublic || f.GetCustomAttributes(typeof(SerializeField), true).Length > 0;
            if (simple && shown)
            {
                sb.Append(f.Name + "=" + f.GetValue(mb) + "  ");
            }
            else if (shown && typeof(UnityEngine.Object).IsAssignableFrom(t))
            {
                UnityEngine.Object o = f.GetValue(mb) as UnityEngine.Object;
                Material m = o as Material;
                sb.Append(f.Name + "=" + (o == null ? "null" : m != null && m.shader != null ? o.name + "<" + m.shader.name + ">" : o.name) + "  ");
            }
        }
    }

    // Public and serialized fields of the first active component with this type name (game types
    // live in EtgGame.dll, which editor code can't reference directly).
    private static void DumpComponentFields(StringBuilder sb, string typeName)
    {
        foreach (MonoBehaviour mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
        {
            if (mb == null || mb.GetType().Name != typeName)
            {
                continue;
            }
            sb.AppendLine(typeName + " on '" + mb.name + "':");
            AppendFields(sb, mb);
            sb.AppendLine();
            return;
        }
        sb.AppendLine(typeName + ": none active");
    }
}
