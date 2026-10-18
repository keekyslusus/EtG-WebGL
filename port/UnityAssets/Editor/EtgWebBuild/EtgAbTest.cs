using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// D3D11-vs-GLES A/B test (port/abtest.sh runs it in batchmode, once per graphics API, then compares).
// Draws the same frames on whatever API the editor runs on and writes PNGs + manifest.jsonl: every material in
// the project (and every shader without one) on a quad, rendered into a RenderTexture by a camera, with a few
// lighting setups (gallery/).
// Command line: -executeMethod EtgAbTest.Run -abOut <dir> -abApi d3d11|gles30 [-abFilter <regex on asset path>]
// Unity 6 has no Graphics Emulation (2017.4 ran this under "WebGL 2.0"): GLES runs with the device's own caps
// (shader level 46 on a desktop GPU against WebGL 2's 35), so a SubShader / #pragma target above 3.5 can be picked
// here and not in the browser.
// Same C# 4 restrictions as EtgWebBuild.cs. The game's types are reached by reflection, like EtgWebDebug.
public static class EtgAbTest
{
    private const int GallerySize = 192;
    private static readonly Color Background = new Color(0.2f, 0.2f, 0.25f, 1f);
    // _Time & co. as the gallery renders see them; the engine's value depends on how long the editor has run.
    private static readonly Vector4 FixedTime = new Vector4(0.6125f, 12.25f, 24.5f, 36.75f);

    private const string ProbeDir = "Assets/Editor/EtgWebBuild/";

    private static string outDir;
    private static StreamWriter manifest;

    // ---------------------------------------------------------------- entry point

    public static void Run()
    {
        try
        {
            outDir = Arg("-abOut", null);
            if (outDir == null)
            {
                throw new Exception("-abOut <dir> is required");
            }
            outDir = Path.GetFullPath(outDir);
            Directory.CreateDirectory(outDir);
            string api = Arg("-abApi", "");
            GraphicsDeviceType expected = api == "d3d11" ? GraphicsDeviceType.Direct3D11
                : api == "gles30" ? GraphicsDeviceType.OpenGLES3 : GraphicsDeviceType.Null;

            WriteEnvironment(api);
            if (SystemInfo.graphicsDeviceType != expected)
            {
                Debug.LogError("[AbTest] graphics API is " + SystemInfo.graphicsDeviceType + ", expected " + expected + " (-abApi " + api + ")");
                EditorApplication.Exit(3);
                return;
            }

            RunGallery(Arg("-abFilter", null));
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogError("[AbTest] failed: " + e);
            EditorApplication.Exit(2);
        }
    }

    private static string Arg(string name, string fallback)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }
        return fallback;
    }

    private static void WriteEnvironment(string api)
    {
        StringBuilder sb = new StringBuilder("{");
        Json(sb, "api", api); sb.Append(',');
        Json(sb, "device", SystemInfo.graphicsDeviceType.ToString()); sb.Append(',');
        Json(sb, "deviceVersion", SystemInfo.graphicsDeviceVersion); sb.Append(',');
        Json(sb, "deviceName", SystemInfo.graphicsDeviceName); sb.Append(',');
        Json(sb, "shaderLevel", SystemInfo.graphicsShaderLevel.ToString()); sb.Append(',');
        Json(sb, "tier", Graphics.activeTier.ToString()); sb.Append(',');
        Json(sb, "unity", Application.unityVersion); sb.Append(',');
        Json(sb, "colorSpace", QualitySettings.activeColorSpace.ToString()); sb.Append(',');
        Json(sb, "webglDefines", WebGLShaderDefines()); sb.Append(',');
        Json(sb, "started", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.Append("}\n");
        File.WriteAllText(Path.Combine(outDir, "environment.json"), sb.ToString());
        Debug.Log("[AbTest] " + sb);
    }

    // The engine's built-in defines for the WebGL player's shaders (Unity 6: UNITY_LIGHT_PROBE_PROXY_VOLUME & co. come
    // from here, not from a list of APIs in UnityShaderVariables.cginc); the editor's own GLES device may differ.
    public static string WebGLShaderDefines()
    {
        StringBuilder sb = new StringBuilder();
        foreach (GraphicsTier tier in new[] { GraphicsTier.Tier1, GraphicsTier.Tier2, GraphicsTier.Tier3 })
        {
            BuiltinShaderDefine[] defines = ShaderUtil.GetShaderPlatformKeywordsForBuildTarget(
                UnityEditor.Rendering.ShaderCompilerPlatform.GLES3x, BuildTarget.WebGL, tier);
            sb.Append(sb.Length > 0 ? "; " : "").Append(tier).Append(':');
            foreach (BuiltinShaderDefine d in defines)
            {
                sb.Append(' ').Append(d);
            }
        }
        return sb.ToString();
    }

    /// <summary>-executeMethod EtgAbTest.LogWebGLShaderDefines: just the defines, into the editor log.</summary>
    public static void LogWebGLShaderDefines()
    {
        Debug.Log("[AbTest] WebGL shader defines: " + WebGLShaderDefines());
        EditorApplication.Exit(0);
    }

    // ---------------------------------------------------------------- manifest

    private static void Json(StringBuilder sb, string key, string value)
    {
        sb.Append('"').Append(key).Append("\":");
        if (value == null)
        {
            sb.Append("null");
            return;
        }
        sb.Append('"');
        foreach (char c in value)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        sb.Append('"');
    }

    private static void Record(string kind, string id, string file, string status, Dictionary<string, string> fields)
    {
        StringBuilder sb = new StringBuilder("{");
        Json(sb, "kind", kind); sb.Append(',');
        Json(sb, "id", id); sb.Append(',');
        Json(sb, "file", file); sb.Append(',');
        Json(sb, "status", status);
        if (fields != null)
        {
            foreach (KeyValuePair<string, string> p in fields)
            {
                sb.Append(',');
                Json(sb, p.Key, p.Value);
            }
        }
        sb.Append('}');
        manifest.WriteLine(sb.ToString());
        manifest.Flush();
    }

    private static void OpenManifest(bool append)
    {
        manifest = new StreamWriter(Path.Combine(outDir, "manifest.jsonl"), append, new UTF8Encoding(false));
    }

    // ---------------------------------------------------------------- gallery

    private sealed class Setup
    {
        public string Name;
        public bool LitOnly;   // only for shaders with Unity lighting passes
        public Action Apply;
    }

    private sealed class ShaderInfo
    {
        public bool Lit;
        public List<KeyValuePair<string, string>> Undeclared = new List<KeyValuePair<string, string>>(); // name, type
    }

    private static GameObject quad;
    private static Camera camera;
    private static RenderTexture target;
    private static Texture2D readback;
    private static Texture2D noise;
    private static readonly List<Light> lights = new List<Light>();

    private static void RunGallery(string filter)
    {
        DateTime started = DateTime.Now;
        string dir = Path.Combine(outDir, "gallery");
        Directory.CreateDirectory(dir);
        OpenManifest(false);
        Regex filterRe = string.IsNullOrEmpty(filter) ? null : new Regex(filter, RegexOptions.IgnoreCase);

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        BuildStage();
        Setup[] setups = Setups();
        Dictionary<Shader, ShaderInfo> infos = new Dictionary<Shader, ShaderInfo>();

        // everything that will be drawn: materials, then shaders no material uses
        List<KeyValuePair<string, Material>> items = new List<KeyValuePair<string, Material>>();
        HashSet<Shader> used = new HashSet<Shader>();
        foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                continue;
            }
            if (m.shader != null)
            {
                used.Add(m.shader);
            }
            items.Add(new KeyValuePair<string, Material>(path, m));
        }
        foreach (string guid in AssetDatabase.FindAssets("t:Shader", new[] { "Assets" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Shader s = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (s != null && !used.Contains(s) && !path.StartsWith(ProbeDir))
            {
                items.Add(new KeyValuePair<string, Material>(path, null));
            }
        }
        items.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

        // what an empty frame looks like per setup (for "empty on both" in the report), and the probe
        quad.SetActive(false);
        foreach (Setup setup in setups)
        {
            setup.Apply();
            string file = "gallery/_background__" + setup.Name + ".png";
            Save(RenderStage(), file);
            Record("background", "_background", file, "ok", Fields("setup", setup.Name));
        }
        quad.SetActive(true);
        // diagnostics: what the engine feeds built-in uniforms and lit passes on this API
        Shader probe = AssetDatabase.LoadAssetAtPath<Shader>(ProbeDir + "AbTestProbe.shader");
        Shader lightProbe = AssetDatabase.LoadAssetAtPath<Shader>(ProbeDir + "AbTestLightProbe.shader");
        foreach (Setup setup in setups)
        {
            setup.Apply();
            if (probe != null && !setup.LitOnly)
            {
                DrawItem("_probe", null, new Material(probe), setup, new ShaderInfo(), true);
            }
            if (lightProbe != null)
            {
                DrawItem("_lightprobe", null, new Material(lightProbe), setup, new ShaderInfo(), true);
            }
        }

        int done = 0;
        foreach (KeyValuePair<string, Material> item in items)
        {
            if (filterRe != null && !filterRe.IsMatch(item.Key))
            {
                continue;
            }
            Material source = item.Value;
            Shader shader = source != null ? source.shader : AssetDatabase.LoadAssetAtPath<Shader>(item.Key);
            string id = Id(item.Key, source == null);
            if (shader == null)
            {
                Record("gallery", id, null, "no-shader", Fields("path", item.Key));
                continue;
            }
            ShaderInfo info;
            if (!infos.TryGetValue(shader, out info))
            {
                info = Analyze(shader);
                infos[shader] = info;
            }
            foreach (Setup setup in setups)
            {
                if (setup.LitOnly && !info.Lit)
                {
                    continue;
                }
                setup.Apply();
                Material mat = source != null ? new Material(source) : new Material(shader);
                DrawItem(id, item.Key, mat, setup, info, source == null);
            }
            if (++done % 50 == 0)
            {
                Debug.Log("[AbTest] gallery: " + done + "/" + items.Count + " (" + (int)(DateTime.Now - started).TotalSeconds + " s)");
            }
        }
        manifest.Close();
        Debug.Log("[AbTest] gallery done: " + done + " items in " + (int)(DateTime.Now - started).TotalSeconds + " s");
    }

    private static string Id(string assetPath, bool shaderOnly)
    {
        string p = assetPath.StartsWith("Assets/") ? assetPath.Substring(7) : assetPath;
        p = Path.ChangeExtension(p, null);
        return (shaderOnly ? "shader__" : "") + Regex.Replace(p, @"[^A-Za-z0-9_.-]+", "_");
    }

    private static Dictionary<string, string> Fields(params string[] kv)
    {
        Dictionary<string, string> d = new Dictionary<string, string>();
        for (int i = 0; i + 1 < kv.Length; i += 2)
        {
            d[kv[i]] = kv[i + 1];
        }
        return d;
    }

    private static void DrawItem(string id, string path, Material mat, Setup setup, ShaderInfo info, bool shaderOnly)
    {
        Shader shader = mat.shader;
        Dictionary<string, string> f = Fields(
            "setup", setup.Name, "path", path, "shader", shader.name,
            "keywords", string.Join(" ", mat.shaderKeywords),
            "queue", mat.renderQueue.ToString(), "passes", mat.passCount.ToString(),
            "shaderOnly", shaderOnly ? "1" : "0");
        string status = "ok";
        try
        {
            if (!shader.isSupported)
            {
                status = "unsupported";
            }
            else if (ShaderHasError(shader))
            {
                status = "shader-error";
            }
            List<string> filled = Fill(mat, info);
            if (filled.Count > 0)
            {
                f["filled"] = string.Join(" ", filled.ToArray());
            }
            quad.GetComponent<MeshRenderer>().sharedMaterial = mat;
            string file = "gallery/" + id + "__" + setup.Name + ".png";
            Save(RenderStage(), file);
            Record("gallery", id, file, status, f);
        }
        catch (Exception e)
        {
            f["error"] = e.GetType().Name + ": " + e.Message;
            Record("gallery", id, null, "exception", f);
        }
        finally
        {
            quad.GetComponent<MeshRenderer>().sharedMaterial = null;
            UnityEngine.Object.DestroyImmediate(mat);
        }
    }

    // internal in 2017.4
    private static readonly MethodInfo ShaderHasErrorMethod = typeof(ShaderUtil).GetMethod("ShaderHasError",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Shader) }, null);

    private static bool ShaderHasError(Shader shader)
    {
        return ShaderHasErrorMethod != null && (bool)ShaderHasErrorMethod.Invoke(null, new object[] { shader });
    }

    // Undeclared uniforms (set by game code at runtime: Shader.SetGlobal*, material.Set* on runtime copies) get
    // deterministic values in [0.25, 0.75]; empty texture slots get a noise texture. Otherwise most of them draw
    // black or nothing and prove nothing.
    private static List<string> Fill(Material mat, ShaderInfo info)
    {
        List<string> filled = new List<string>();
        Shader shader = mat.shader;
        int n = shader.GetPropertyCount();
        for (int i = 0; i < n; i++)
        {
            if (shader.GetPropertyType(i) == ShaderPropertyType.Texture)
            {
                string name = shader.GetPropertyName(i);
                if (mat.GetTexture(name) == null && shader.GetPropertyTextureDimension(i) == TextureDimension.Tex2D)
                {
                    mat.SetTexture(name, noise);
                    filled.Add(name + "=noise");
                }
            }
        }
        foreach (KeyValuePair<string, string> u in info.Undeclared)
        {
            string name = u.Key, type = u.Value;
            if (type == "tex")
            {
                mat.SetTexture(name, noise);
                filled.Add(name + "=noise");
                continue;
            }
            float h = Hash(name);
            if (type.EndsWith("x4") || type.EndsWith("x3"))
            {
                mat.SetMatrix(name, Matrix4x4.identity);
                filled.Add(name + "=I");
            }
            else if (type.EndsWith("2") || type.EndsWith("3") || type.EndsWith("4"))
            {
                Vector4 v = new Vector4(h, Hash(name + "y"), Hash(name + "z"), Hash(name + "w"));
                mat.SetVector(name, v);
                filled.Add(name + "=" + v.ToString("0.###"));
            }
            else
            {
                mat.SetFloat(name, h);
                filled.Add(name + "=" + h.ToString("0.###"));
            }
        }
        return filled;
    }

    private static float Hash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s)
        {
            h = (h ^ c) * 16777619;
        }
        return 0.25f + 0.5f * (h % 1000) / 999f;
    }

    private static readonly Regex UniformRe = new Regex(
        @"^[ \t]*(?:uniform[ \t]+)?(float|half|fixed|int|uint|bool)([234](?:x[234])?)?[ \t]+(\w+)[ \t]*;", RegexOptions.Multiline);
    private static readonly Regex SamplerRe = new Regex(
        @"^[ \t]*(?:sampler2D(?:_float|_half)?[ \t]+(\w+)[ \t]*;|Texture2D(?:<[^>]*>)?[ \t]+(\w+)[ \t]*;|UNITY_DECLARE_TEX2D(?:_NOSAMPLER)?\((\w+)\))", RegexOptions.Multiline);
    private static readonly Regex BuiltinRe = new Regex(
        @"^(unity_|_World|_Object|glstate_|_Time$|_SinTime$|_CosTime$|_ProjectionParams$|_ScreenParams$|_ZBufferParams$|_LightColor0$|" +
        @"_LightPositionRange$|_LightShadowData$|_LightMatrix0$|_LightTexture|_ShadowMapTexture$|_ShadowOffsets|_CameraDepthTexture$|" +
        @"_CameraDepthNormalsTexture$|_LightAsQuad$|_SpecCube|_Lightmap|_LightProbe|_PPLAmbient$|_LightPos$|_LightDir$|i$|tmp\d+$)|(_ST|_TexelSize|_HDR)$");

    private static ShaderInfo Analyze(Shader shader)
    {
        ShaderInfo info = new ShaderInfo();
        string path = AssetDatabase.GetAssetPath(shader);
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".shader") || !File.Exists(path))
        {
            return info;
        }
        string text = File.ReadAllText(path);
        info.Lit = Regex.IsMatch(text, "\"LightMode\"\\s*=\\s*\"(ForwardBase|ForwardAdd|Vertex|VertexLM|VertexLMRGBM)\"", RegexOptions.IgnoreCase);
        HashSet<string> declared = new HashSet<string>();
        int n = shader.GetPropertyCount();
        for (int i = 0; i < n; i++)
        {
            declared.Add(shader.GetPropertyName(i));
        }
        foreach (Match g in Regex.Matches(text, "GrabPass\\s*\\{\\s*\"(\\w+)\""))
        {
            declared.Add(g.Groups[1].Value);
        }
        declared.Add("_GrabTexture");
        HashSet<string> seen = new HashSet<string>();
        foreach (Match block in Regex.Matches(text, "CGPROGRAM(.*?)ENDCG", RegexOptions.Singleline))
        {
            string code = block.Groups[1].Value;
            foreach (Match m in UniformRe.Matches(code))
            {
                string name = m.Groups[3].Value;
                if (!declared.Contains(name) && !BuiltinRe.IsMatch(name) && !IsGameGlobal(name) && seen.Add(name))
                {
                    info.Undeclared.Add(new KeyValuePair<string, string>(name, m.Groups[1].Value + m.Groups[2].Value));
                }
            }
            foreach (Match m in SamplerRe.Matches(code))
            {
                string name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                if (!declared.Contains(name) && !BuiltinRe.IsMatch(name) && !IsGameGlobal(name) && seen.Add(name))
                {
                    info.Undeclared.Add(new KeyValuePair<string, string>(name, "tex"));
                }
            }
        }
        return info;
    }

    private static void BuildStage()
    {
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.35f, 0.35f, 0.35f, 1f);
        RenderSettings.fog = false;
        QualitySettings.pixelLightCount = 8;
        QualitySettings.shadows = ShadowQuality.Disable;

        GameObject camGo = new GameObject("AbTest Camera");
        camera = camGo.AddComponent<Camera>();
        camera.orthographic = true;
        camera.orthographicSize = 0.5f;
        camera.transform.position = new Vector3(0f, 0f, -2f);
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 10f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Background;
        camera.renderingPath = RenderingPath.Forward;
        camera.allowHDR = false;
        camera.allowMSAA = false;
        camera.enabled = false; // rendered by hand only
        // The engine sets _Time & co. per camera render (to 0 in batchmode Edit Mode, where nothing would animate):
        // Shader.SetGlobal* beforehand is overwritten, a command buffer inside the render is not (the probe shows it).
        CommandBuffer time = new CommandBuffer();
        time.name = "AbTest fixed time";
        Vector4 t = FixedTime;
        time.SetGlobalVector("_Time", t);
        time.SetGlobalVector("_SinTime", new Vector4(Mathf.Sin(t.w / 8f), Mathf.Sin(t.w / 4f), Mathf.Sin(t.w / 2f), Mathf.Sin(t.w)));
        time.SetGlobalVector("_CosTime", new Vector4(Mathf.Cos(t.w / 8f), Mathf.Cos(t.w / 4f), Mathf.Cos(t.w / 2f), Mathf.Cos(t.w)));
        time.SetGlobalVector("unity_DeltaTime", new Vector4(1f / 30f, 30f, 1f / 30f, 30f));
        camera.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, time);

        quad = new GameObject("AbTest Quad");
        Mesh mesh = new Mesh();
        mesh.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
        // At 192 px, with atlases of 128..4096 texels, every third pixel centre lands exactly on a texel boundary, and
        // D3D and GL interpolate UVs a few ulps apart: point sampling then picks the neighbour texel (scattered 255
        // differences). Shifted by a quarter texel of a 4096 texture, no pixel centre is within 0.008 texels of a
        // boundary for any power-of-two size (and the mip level, log2(size / 192), stays 0.085 away from rounding).
        const float e = 0.25f / 4096f;
        Vector2[] uv = { new Vector2(e, e), new Vector2(1f + e, e), new Vector2(1f + e, 1f + e), new Vector2(e, 1f + e) };
        mesh.uv = uv;
        mesh.uv2 = uv;
        mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
        mesh.tangents = new[] { new Vector4(1f, 0f, 0f, -1f), new Vector4(1f, 0f, 0f, -1f), new Vector4(1f, 0f, 0f, -1f), new Vector4(1f, 0f, 0f, -1f) };
        mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
        mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
        quad.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer mr = quad.AddComponent<MeshRenderer>();
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.lightProbeUsage = LightProbeUsage.Off;
        mr.reflectionProbeUsage = ReflectionProbeUsage.Off;

        target = new RenderTexture(GallerySize, GallerySize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
        target.antiAliasing = 1;
        target.filterMode = FilterMode.Point;
        target.Create();
        readback = new Texture2D(GallerySize, GallerySize, TextureFormat.RGBA32, false);

        // value noise in all four channels, point-sampled: identical on every API
        noise = new Texture2D(64, 64, TextureFormat.RGBA32, false);
        noise.filterMode = FilterMode.Point;
        noise.wrapMode = TextureWrapMode.Repeat;
        Color32[] px = new Color32[64 * 64];
        uint s = 12345;
        for (int i = 0; i < px.Length; i++)
        {
            s = s * 1664525 + 1013904223; byte r = (byte)(s >> 24);
            s = s * 1664525 + 1013904223; byte g = (byte)(s >> 24);
            s = s * 1664525 + 1013904223; byte b = (byte)(s >> 24);
            s = s * 1664525 + 1013904223; byte a = (byte)(64 + (s >> 25));
            px[i] = new Color32(r, g, b, a);
        }
        noise.SetPixels32(px);
        noise.Apply(false, false);

        // the globals the game's code sets (Shader.SetGlobal*), at their usual in-game values
        foreach (KeyValuePair<string, float> g in GameGlobalFloats)
        {
            Shader.SetGlobalFloat(g.Key, g.Value);
        }
        foreach (string tex in GameGlobalTextures)
        {
            Shader.SetGlobalTexture(tex, noise);
        }
        Shader.SetGlobalVector("_BossIntroTime", new Vector4(0.5f, 1f, 2f, 3f));
    }

    private static readonly Dictionary<string, float> GameGlobalFloats = new Dictionary<string, float>
    {
        { "_LowQualityMode", 0f }, { "_Tubiness", 0f }, { "_MeduziReflectionsEnabled", 0f }, { "_MapActive", 0f },
        { "_FullMapActive", 0f }, { "_GlobalReflectionsEnabled", 1f }, { "_ReflectionYFactor", 0f },
    };
    private static readonly string[] GameGlobalTextures = { "_EndTimesVortex", "_ReflMapFromPixelator", "_PreBackgroundTex" };

    private static bool IsGameGlobal(string name)
    {
        return GameGlobalFloats.ContainsKey(name) || Array.IndexOf(GameGlobalTextures, name) >= 0 || name == "_BossIntroTime";
    }

    private static Light AddLight(string name, LightType type, Color color, float intensity, Vector3 pos, Vector3 euler)
    {
        Light l = new GameObject("AbTest " + name).AddComponent<Light>();
        l.type = type;
        l.color = color;
        l.intensity = intensity;
        l.transform.position = pos;
        l.transform.eulerAngles = euler;
        l.renderMode = LightRenderMode.ForcePixel;
        l.shadows = LightShadows.None;
        l.range = 3f;
        l.spotAngle = 70f;
        lights.Add(l);
        return l;
    }

    private static void ClearLights()
    {
        foreach (Light l in lights)
        {
            UnityEngine.Object.DestroyImmediate(l.gameObject);
        }
        lights.Clear();
    }

    private static Setup[] Setups()
    {
        Texture2D cookie = new Texture2D(32, 32, TextureFormat.Alpha8, false);
        Color32[] c = new Color32[32 * 32];
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                c[y * 32 + x] = new Color32(0, 0, 0, (byte)((((x >> 3) ^ (y >> 3)) & 1) == 0 ? 255 : 80));
            }
        }
        cookie.SetPixels32(c);
        cookie.Apply(false, false);
        cookie.wrapMode = TextureWrapMode.Clamp;
        Cubemap cubeCookie = new Cubemap(16, TextureFormat.Alpha8, false);
        for (int face = 0; face < 6; face++)
        {
            Color[] fc = new Color[16 * 16];
            for (int i = 0; i < fc.Length; i++)
            {
                fc[i] = new Color(0f, 0f, 0f, ((i / 16 + i % 16 + face) & 4) == 0 ? 1f : 0.3f);
            }
            cubeCookie.SetPixels(fc, (CubemapFace)face);
        }
        cubeCookie.Apply(false, false);

        return new[]
        {
            // no Unity light: ambient only (ForwardBase still runs, with _LightColor0 = 0)
            new Setup { Name = "dark", Apply = ClearLights },
            // one light each, so a difference points at the light type: directional (ForwardBase, _LightColor0),
            // point and spot (ForwardAdd: attenuation textures, unity_WorldToLight)
            new Setup { Name = "dir", LitOnly = true, Apply = delegate
            {
                ClearLights();
                AddLight("Directional", LightType.Directional, new Color(1f, 0.9f, 0.75f), 0.7f, Vector3.zero, new Vector3(40f, -30f, 0f));
            } },
            new Setup { Name = "point", LitOnly = true, Apply = delegate
            {
                ClearLights();
                AddLight("Point", LightType.Point, new Color(0.4f, 0.7f, 1f), 1.5f, new Vector3(0.25f, 0.2f, -0.6f), Vector3.zero);
            } },
            new Setup { Name = "spot", LitOnly = true, Apply = delegate
            {
                ClearLights();
                AddLight("Spot", LightType.Spot, new Color(1f, 0.4f, 0.3f), 2f, new Vector3(-0.3f, -0.2f, -1.2f), new Vector3(-10f, 15f, 0f));
            } },
            // cookies: DIRECTIONAL_COOKIE, POINT_COOKIE, SPOT with a cookie
            new Setup { Name = "cookie", LitOnly = true, Apply = delegate
            {
                ClearLights();
                Light d = AddLight("DirectionalCookie", LightType.Directional, new Color(0.9f, 1f, 0.8f), 0.8f, Vector3.zero, new Vector3(20f, 10f, 0f));
                d.cookie = cookie;
                d.cookieSize2D = new Vector2(0.6f, 0.6f);
                Light p = AddLight("PointCookie", LightType.Point, new Color(1f, 0.8f, 0.5f), 1.5f, new Vector3(-0.2f, 0.25f, -0.5f), new Vector3(0f, 30f, 0f));
                p.cookie = cubeCookie;
                Light s = AddLight("SpotCookie", LightType.Spot, new Color(0.5f, 1f, 0.6f), 2f, new Vector3(0.3f, -0.3f, -1.2f), new Vector3(10f, -15f, 0f));
                s.cookie = cookie;
            } },
        };
    }

    private static Texture2D RenderStage()
    {
        camera.targetTexture = target;
        camera.Render();
        camera.targetTexture = null;
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = target;
        readback.ReadPixels(new Rect(0, 0, GallerySize, GallerySize), 0, 0, false);
        readback.Apply(false, false);
        RenderTexture.active = previous;
        return readback;
    }

    private static void Save(Texture2D tex, string relative)
    {
        File.WriteAllBytes(Path.Combine(outDir, relative.Replace('/', Path.DirectorySeparatorChar)), tex.EncodeToPNG());
    }
}
