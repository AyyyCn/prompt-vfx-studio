using System;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Arcade.VfxAuthor.Editor
{
    public sealed class PromptVfxWindow : EditorWindow
    {
        const string ExeKey = "PromptVfxStudio.ClaudeExecutable";
        [SerializeField] string brief = "";
        [SerializeField] string note = "";
        [SerializeField] string current = "";
        [SerializeField] string previous = "";
        int layerChoice;
        string[] layerOptions = Array.Empty<string>();
        [SerializeField] bool reviewNew = true;
        string status = "";
        string failure = "";
        bool settings, playing = true;
        float time;
        double lastTick;
        Vector2 scroll;
        EffectPreview preview, before;
        CancellationTokenSource pending;

        [MenuItem("Window/VFX/Surface VFX Authoring")]
        public static void Open() => GetWindow<PromptVfxWindow>("Surface VFX Authoring");

        void OnEnable()
        {
            minSize = new Vector2(460,390);
            lastTick = EditorApplication.timeSinceStartup;
            if (current.Length > 0) Rebuild();
            EditorApplication.update += Tick;
        }

        void OnDisable()
        {
            EditorApplication.update -= Tick;
            pending?.Cancel();
            pending = null;
            preview?.Dispose(); preview = null;
            before?.Dispose(); before = null;
        }

        void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            if (playing && preview != null)
            {
                time = Mathf.Repeat(time + (float)Math.Min(now-lastTick, 0.12), Length);
                Repaint();
            }
            lastTick = now;
        }

        float Length => Mathf.Max(preview?.Length ?? 0.01f,before?.Length ?? 0.01f);

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            if (GUILayout.Button("Open direct C# and shader authoring")) DirectAuthorWindow.Open();
            EditorGUILayout.LabelField("Effect brief", EditorStyles.boldLabel);
            brief = EditorGUILayout.TextArea(brief, GUILayout.MinHeight(48));
            using (new EditorGUI.DisabledScope(pending != null))
                reviewNew = EditorGUILayout.ToggleLeft("Review and refine Unity renders", reviewNew);
            using (new EditorGUI.DisabledScope(pending != null || string.IsNullOrWhiteSpace(brief)))
                if (GUILayout.Button("Generate effect")) Submit(false);

            if (preview != null)
            {
                EditorGUILayout.Space();
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (before != null) Pane("Before", before);
                    Pane(before == null ? "Preview" : "Revision", preview);
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(playing ? "Pause" : "Play", GUILayout.Width(65))) playing = !playing;
                    EditorGUI.BeginChangeCheck();
                    float value = EditorGUILayout.Slider(time,0,Length);
                    if (EditorGUI.EndChangeCheck()) { time = value; playing = false; }
                }
                EditorGUILayout.LabelField("Art direction",EditorStyles.boldLabel);
                layerChoice = EditorGUILayout.Popup("Edit scope",layerChoice,layerOptions);
                note = EditorGUILayout.TextArea(note,GUILayout.MinHeight(38));
                using (new EditorGUILayout.HorizontalScope())
                using (new EditorGUI.DisabledScope(pending != null))
                {
                    using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(note)))
                        if (GUILayout.Button("Revise effect")) Submit(true);
                    if (before != null && GUILayout.Button("Restore previous"))
                    {
                        current = previous; previous = ""; Rebuild();
                        status = "Previous effect restored."; failure = "";
                    }
                    if (GUILayout.Button("Save prefab…")) Save();
                }
            }
            if (pending != null && GUILayout.Button("Cancel request")) pending.Cancel();
            if (failure.Length > 0) EditorGUILayout.HelpBox(failure,MessageType.Error);
            else if (status.Length > 0) EditorGUILayout.HelpBox(status,MessageType.Info);
            settings = EditorGUILayout.Foldout(settings,"Model settings");
            if (settings)
            {
                string path = EditorPrefs.GetString(ExeKey, ModelAuthor.DefaultExecutable());
                var next = EditorGUILayout.TextField("Claude executable",path);
                if (next != path) EditorPrefs.SetString(ExeKey,next);
                EditorGUILayout.HelpBox("Uses your Claude Code login. Visual review sends rendered previews to the model and can run two refinement rounds. Saved prefabs play locally.",MessageType.Info);
            }
            EditorGUILayout.EndScrollView();
        }

        void Pane(string label, EffectPreview pane)
        {
            using (new EditorGUILayout.VerticalScope())
            {
                EditorGUILayout.LabelField(label,EditorStyles.miniBoldLabel);
                var rect = GUILayoutUtility.GetRect(30, Mathf.Clamp(position.height * 0.4f, 130, 440), GUILayout.ExpandWidth(true));
                pane.Draw(rect,time);
            }
        }

        async void Submit(bool revise)
        {
            var request = new CancellationTokenSource();
            pending = request;
            status = revise ? "Revising the selected effect…" : "Writing geometry and shaders…";
            failure = "";
            try
            {
                var source = revise ? JObject.Parse(current) : null;
                string exe = EditorPrefs.GetString(ExeKey,ModelAuthor.DefaultExecutable());
                string target = revise && layerChoice > 0 ? layerOptions[layerChoice] : null;
                var result = source != null && !SurfaceProgram.IsSurface(source)
                    ? await ModelAuthor.Request(note,source,exe,request.Token,target)
                    : await SurfaceProgram.Request(revise ? note : brief,source,exe,request.Token,target,reviewNew,
                        message => { if (this && pending == request) { status=message; Repaint(); } });
                if (!this || pending != request || request.IsCancellationRequested) return;
                previous = revise ? current : "";
                current = result.ToString(Formatting.None);
                Rebuild();
                status = (string)result["name"] + "  ·  " + string.Join(", ",result["layers"].Select(l => (string)l["id"]));
                if(result["visualReview"]?["summary"] != null) status += "\n"+(string)result["visualReview"]["summary"];
                if (revise) note = "";
            }
            catch (OperationCanceledException) { status = "Request cancelled."; }
            catch (Exception e) { failure = e.Message; }
            finally
            {
                if (pending == request) pending = null;
                request.Dispose();
                if (this) Repaint();
            }
        }

        void Rebuild()
        {
            preview?.Dispose(); before?.Dispose();
            var recipe = JObject.Parse(current);
            layerOptions = new[]{"Whole effect"}.Concat(recipe["layers"].Select(layer => (string)layer["id"])).ToArray();
            layerChoice = 0;
            preview = new EffectPreview(recipe);
            before = previous.Length == 0 ? null : new EffectPreview(JObject.Parse(previous));
            time = 0; playing = true;
        }

        void Save()
        {
            try
            {
                var recipe = JObject.Parse(current);
                string name = string.Concat(((string)recipe["name"]).Select(c => char.IsLetterOrDigit(c) || c == ' ' ? c : '_'));
                var path = EditorUtility.SaveFilePanelInProject("Save effect prefab",name,"prefab","Choose a project folder.");
                if (path.Length == 0) return;
                var asset = SurfaceProgram.IsSurface(recipe) ? SurfaceBuild.Save(recipe,path) : EffectBuilder.Save(recipe,path);
                EditorGUIUtility.PingObject(asset);
                status = "Saved " + path; failure = "";
            }
            catch (Exception e) { failure = e.Message; }
        }
    }
}
