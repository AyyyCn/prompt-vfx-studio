using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Installed only in the disposable authoring project.
[InitializeOnLoad]
public static class PromptVfxStudioHarness
{
    const string Active = "PromptVfxStudio.Capturing";
    const int Width = 832, Height = 468;
    static readonly int[] Frames = { 12, 42, 88, 141 };
    static string Output => Environment.GetEnvironmentVariable("VFX_STUDIO_OUTPUT");
    static string Id => Environment.GetEnvironmentVariable("VFX_STUDIO_ID");
    static string Folder => "Assets/" + Id;
    static int startFrame, tile;
    static Camera cam;
    static RenderTexture target;
    static Texture2D sheet;

    static PromptVfxStudioHarness() { EditorApplication.update += Capture; }

    public static void Run()
    {
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            renderer.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>(
                "Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
            var pipeline = UniversalRenderPipelineAsset.Create(renderer);
            AssetDatabase.CreateAsset(pipeline, "Assets/Editor/PreviewPipeline.asset");
            AssetDatabase.AddObjectToAsset(renderer, pipeline);
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            var builder = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(Id + ".BuildEffect"))
                .FirstOrDefault(t => t != null);
            if (builder == null) throw new InvalidOperationException("Implement " + Id + ".BuildEffect.");
            var build = builder.GetMethod("Build", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (build == null) throw new InvalidOperationException("BuildEffect needs public static void Build().");
            build.Invoke(null, null);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            if (!AssetDatabase.LoadAssetAtPath<GameObject>(Folder + "/Effect.prefab"))
                throw new InvalidOperationException("Builder did not save " + Folder + "/Effect.prefab.");
            foreach (var guid in AssetDatabase.FindAssets("t:Shader", new[] { Folder }))
            {
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(guid));
                if (ShaderUtil.ShaderHasError(shader))
                    throw new InvalidOperationException(string.Join("\n", ShaderUtil.GetShaderMessages(shader).Select(m => m.message)));
            }
            SessionState.SetBool(Active, true);
            EditorApplication.EnterPlaymode();
        }
        catch (Exception error) { Fail(error); }
    }

    static void Capture()
    {
        if (!SessionState.GetBool(Active, false) || !EditorApplication.isPlaying || Time.frameCount < 2) return;
        try
        {
            if (!cam)
            {
                Time.captureDeltaTime = 1f / 30;
                cam = new GameObject("Studio camera").AddComponent<Camera>();
                cam.tag = "MainCamera";
                cam.transform.position = new Vector3(0, 0, -12);
                cam.orthographic = true;
                cam.orthographicSize = 2.65f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(.027f, .029f, .052f);
                target = RenderTexture.GetTemporary(Width, Height, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = target;
                sheet = new Texture2D(Width * 2, Height * 2, TextureFormat.RGB24, false);
                UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Folder + "/Effect.prefab"));
                startFrame = Time.frameCount;
            }
            if (Time.frameCount - startFrame < Frames[tile]) return;
            cam.Render();
            var oldTarget = RenderTexture.active;
            RenderTexture.active = target;
            sheet.ReadPixels(new Rect(0, 0, Width, Height), (tile % 2) * Width, (1 - tile / 2) * Height);
            RenderTexture.active = oldTarget;
            if (++tile < Frames.Length) return;
            SessionState.SetBool(Active, false);
            sheet.Apply();
            var pixels = sheet.GetPixels32();
            var background = pixels[0];
            int visible = pixels.Count(p => Math.Abs(p.r-background.r) + Math.Abs(p.g-background.g) + Math.Abs(p.b-background.b) > 30);
            if (visible < Width * Height / 300) throw new InvalidOperationException("Unity captured an effectively blank frame sheet. Fix framing or shading.");
            File.WriteAllBytes(Path.Combine(Output, "frames.jpg"), sheet.EncodeToJPG(93));
            // Omit author-time builder code. Runtime scripts and all native effect assets travel together.
            var paths = AssetDatabase.GetAllAssetPaths().Where(p => p.StartsWith(Folder + "/", StringComparison.Ordinal)
                && !p.Contains("/Editor/") && !AssetDatabase.IsValidFolder(p)).ToArray();
            AssetDatabase.ExportPackage(paths, Path.Combine(Output, "Effect.unitypackage"), ExportPackageOptions.Default);
            File.WriteAllText(Path.Combine(Output, "ready"), SystemInfo.graphicsDeviceName);
            EditorApplication.Exit(0);
        }
        catch (Exception error) { Fail(error); }
    }

    static void Fail(Exception error)
    {
        SessionState.SetBool(Active, false);
        Debug.LogException(error);
        EditorApplication.Exit(1);
    }
}
