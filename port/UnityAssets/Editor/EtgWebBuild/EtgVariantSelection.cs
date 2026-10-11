using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// C# 4 / Unity 2017.4. Invoked by port/variant_selection.py, no original assets edited.
public static class EtgVariantSelection
{
    [Serializable] public class Job
    {
        public int id;
        public string shader, material, clone, mode, setup;
        public int subshader, pass_index;
        public string[] keys;
    }
    [Serializable] public class Jobs { public Job[] jobs; }
    [Serializable] public class Observation
    {
        public int id, vs, ps;
        public string status;
        public string[] vsKeywords, psKeywords, engineKeywords;
    }
    [Serializable] public class EnvironmentInfo
    {
        public string unity, api, colorSpace;
        public int jobs;
    }
    private static Camera camera;
    private static MeshRenderer renderer;
    private static RenderTexture target;
    private static Texture2D readback, cookie;
    private static Cubemap cube;
    private static readonly List<Light> lights = new List<Light>();
    private static readonly string[] EngineKeys = new[]
    {
        "DIRECTIONAL", "POINT", "SPOT", "DIRECTIONAL_COOKIE", "POINT_COOKIE",
        "LIGHTPROBE_SH", "VERTEXLIGHT_ON", "SHADOWS_SCREEN"
    };

    public static void Run()
    {
        try
        {
            string dir = null;
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "-variantOut") dir = args[i + 1];
            if (dir == null) throw new Exception("-variantOut required");
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
                throw new Exception("Expected D3D11, got " + SystemInfo.graphicsDeviceType);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Jobs input = JsonUtility.FromJson<Jobs>(File.ReadAllText(Path.Combine(dir, "jobs.json")));
            File.WriteAllText(Path.Combine(dir, "environment.json"), JsonUtility.ToJson(new EnvironmentInfo
            {
                unity = Application.unityVersion, api = SystemInfo.graphicsDeviceVersion,
                colorSpace = QualitySettings.activeColorSpace.ToString(), jobs = input.jobs.Length
            }, true));
            EditorApplication.ExecuteMenuItem("Edit/Graphics Emulation/No Emulation");
            BuildStage();
            Dictionary<string, Material> materials = new Dictionary<string, Material>();
            Dictionary<string, string[]> engineCache = new Dictionary<string, string[]>();
            string activeSetup = null;
            using (StreamWriter output = new StreamWriter(Path.Combine(dir, "observed.jsonl"), false, new UTF8Encoding(false)))
            {
                foreach (Job job in input.jobs)
                {
                    Observation row = new Observation { id = job.id, status = "ok" };
                    try
                    {
                        string setup = job.mode + "/" + job.setup;
                        if (activeSetup != setup)
                        {
                            Setup(job.mode, job.setup);
                            activeSetup = setup;
                        }
                        string[] engine;
                        if (!engineCache.TryGetValue(setup, out engine))
                        {
                            Shader reference = Shader.Find("Hidden/EtgVariant/Engine" + job.mode);
                            if (reference == null) throw new Exception("Reference shader not imported");
                            Material probe = new Material(reference);
                            // Python generates these macros in lexicographic order, plus the full ENGINE inventory.
                            // Read their indices from the generated reference source, avoiding a duplicate inventory here.
                            string source = File.ReadAllText(AssetDatabase.GetAssetPath(reference));
                            List<string> keys = new List<string>();
                            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(source, @"#if defined\((\w+)\)"))
                                keys.Add(m.Groups[1].Value);
                            Color[] pixels = Render(probe);
                            if (Mathf.Abs(pixels[0].b - 173f) > 0.1f) throw new Exception("Reference pass did not draw: " + setup + " pixel=" + pixels[0] + " target=" + target.format);
                            List<string> found = new List<string>();
                            for (int k = 0; k < keys.Count; k++)
                                if (pixels[k + 1].g > 0.5f && Array.IndexOf(EngineKeys, keys[k]) >= 0) found.Add(keys[k]);
                            engine = found.ToArray();
                            engineCache[setup] = engine;
                            UnityEngine.Object.DestroyImmediate(probe);
                        }
                        row.engineKeywords = engine;
                        Material mat;
                        if (!materials.TryGetValue(job.clone, out mat))
                        {
                            mat = AssetDatabase.LoadAssetAtPath<Material>(job.clone);
                            if (mat == null || mat.shader == null) throw new Exception("Clone not loaded: " + job.clone);
                            materials[job.clone] = mat;
                        }
                        if (!mat.shader.isSupported)
                        {
                            row.status = "unsupported-shader";
                        }
                        else
                        {
                            Color[] pixels = Render(mat);
                            if (Mathf.Abs(pixels[0].b - 173f) > 0.1f) row.status = "pass-not-drawn";
                            else
                            {
                                row.vs = Mathf.RoundToInt(pixels[0].r);
                                row.ps = Mathf.RoundToInt(pixels[0].g);
                                List<string> v = new List<string>(), p = new List<string>();
                                for (int k = 0; k < job.keys.Length; k++)
                                {
                                    if (pixels[k + 1].r > 0.5f) v.Add(job.keys[k]);
                                    if (pixels[k + 1].g > 0.5f) p.Add(job.keys[k]);
                                }
                                row.vsKeywords = v.ToArray(); row.psKeywords = p.ToArray();
                            }
                        }
                    }
                    catch (Exception ex) { row.status = "exception: " + ex.Message; }
                    output.WriteLine(JsonUtility.ToJson(row));
                    if (job.id % 500 == 0) { output.Flush(); Debug.Log("[VariantSelection] " + job.id + "/" + input.jobs.Length); }
                }
            }
            Debug.Log("[VariantSelection] done: " + input.jobs.Length);
            EditorApplication.Exit(0);
        }
        catch (Exception ex)
        {
            Debug.LogError("[VariantSelection] " + ex);
            EditorApplication.Exit(2);
        }
    }

    private static void BuildStage()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = Color.gray;
        RenderSettings.fog = false;
        QualitySettings.pixelLightCount = 8;
        camera = new GameObject("Variant camera").AddComponent<Camera>();
        camera.orthographic = true; camera.orthographicSize = 0.5f;
        camera.transform.position = new Vector3(0, 0, -2);
        camera.nearClipPlane = 0.1f; camera.farClipPlane = 10;
        camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.clear;
        camera.renderingPath = RenderingPath.Forward; camera.allowHDR = true; camera.allowMSAA = false;
        camera.enabled = false;
        GameObject quad = new GameObject("Variant quad");
        Mesh mesh = new Mesh();
        mesh.vertices = new[] { new Vector3(-0.5f,-0.5f,0), new Vector3(0.5f,-0.5f,0), new Vector3(0.5f,0.5f,0), new Vector3(-0.5f,0.5f,0) };
        mesh.uv = new[] { new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1) };
        mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
        mesh.triangles = new[] { 0,2,1,0,3,2 }; mesh.RecalculateBounds();
        quad.AddComponent<MeshFilter>().sharedMesh = mesh;
        renderer = quad.AddComponent<MeshRenderer>();
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        target = new RenderTexture(64, 16, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        target.antiAliasing = 1; target.Create();
        readback = new Texture2D(64, 16, TextureFormat.RGBAFloat, false, true);
        cookie = new Texture2D(16, 16, TextureFormat.Alpha8, false);
        Color[] colors = new Color[256];
        for (int i = 0; i < colors.Length; i++) colors[i] = Color.white;
        cookie.SetPixels(colors); cookie.Apply();
        cube = new Cubemap(16, TextureFormat.Alpha8, false);
        for (int f = 0; f < 6; f++) cube.SetPixels(colors, (CubemapFace)f);
        cube.Apply();
    }

    private static Light Add(LightType type, float intensity)
    {
        Light l = new GameObject("Variant light").AddComponent<Light>();
        l.type = type; l.intensity = intensity; l.range = 10; l.spotAngle = 100;
        l.renderMode = LightRenderMode.ForcePixel; l.shadows = LightShadows.None;
        l.transform.position = new Vector3(0, 0, -1);
        lights.Add(l); return l;
    }

    private static void Setup(string mode, string setup)
    {
        foreach (Light l in lights) UnityEngine.Object.DestroyImmediate(l.gameObject);
        lights.Clear();
        QualitySettings.shadows = ShadowQuality.Disable;
        renderer.receiveShadows = setup == "shadows";
        renderer.lightProbeUsage = setup == "probe" ? LightProbeUsage.BlendProbes : LightProbeUsage.Off;
        if (setup == "dark") return;
        if (mode == "ForwardAdd") Add(LightType.Directional, 10f); // reserve the main light for the color-masked base
        Light light = Add(setup.StartsWith("point") ? LightType.Point : setup.StartsWith("spot") ? LightType.Spot : LightType.Directional, 1f);
        if (setup.EndsWith("cookie")) light.cookie = setup == "point_cookie" ? (Texture)cube : cookie;
        if (setup == "vertex")
        {
            for (int i = 0; i < 4; i++) Add(LightType.Point, 0.5f).renderMode = LightRenderMode.ForceVertex;
        }
        if (setup == "shadows")
        {
            QualitySettings.shadows = ShadowQuality.All; QualitySettings.shadowDistance = 20;
            light.shadows = LightShadows.Hard;
        }
    }

    private static Color[] Render(Material mat)
    {
        renderer.sharedMaterial = mat;
        camera.targetTexture = target; camera.aspect = 1f; camera.Render(); camera.targetTexture = null;
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = target;
        readback.ReadPixels(new Rect(0,0,64,16), 0,0, false); readback.Apply(false,false);
        RenderTexture.active = previous;
        Color[] row = new Color[64];
        for (int x = 0; x < 64; x++) row[x] = readback.GetPixel(x, 8);
        return row;
    }
}
