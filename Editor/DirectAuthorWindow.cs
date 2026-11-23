using System;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Arcade.VfxAuthor.Editor
{
    public sealed class DirectAuthorWindow : EditorWindow
    {
        [SerializeField] string prompt = "";
        [SerializeField] string model = DirectApi.DefaultModel;
        [NonSerialized] static string apiKey;
        static CancellationTokenSource active;
        static string activeJob;
        [SerializeField] string job = "";
        [SerializeField] int selected;
        Texture2D preview;
        string previewPath, status = "", error = "";
        Vector2 scroll;
        bool modelSettings;
        double nextPoll;

        [MenuItem("Window/VFX/Prompt VFX Studio")]
        static void OpenStudio() => Open();

        [MenuItem("Window/VFX/Direct VFX Authoring")]
        public static void Open() => GetWindow<DirectAuthorWindow>("Direct VFX Authoring");

        void OnEnable()
        {
            if (job.Length == 0) job = SessionState.GetString("DirectVFX.LastJob", "");
            EditorApplication.update += Poll;
        }
        void OnDisable()
        {
            EditorApplication.update -= Poll;
            if (preview) DestroyImmediate(preview);
        }
        void Poll()
        {
            if (job.Length == 0 || EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + 1;
            var file = Path.Combine(job, "status.txt");
            if (File.Exists(file)) status = File.ReadAllText(file);
            Repaint();
        }
        static void Interrupt()
        {
            active?.Cancel();
            if (activeJob != null)
                DirectAuthorJob.Status(activeJob, "Stopped · interrupted by the editor. Completed drafts are available below.");
        }

        async void StartJob(string key)
        {
            if (active != null) return;
            var folder = Path.GetFullPath(Path.Combine("Library/PromptVfxJobs", Guid.NewGuid().ToString("N")));
            var cancel = new CancellationTokenSource();
            active = cancel;
            try
            {
                Directory.CreateDirectory(folder);
                job = activeJob = folder;
                status = "Starting…";
                selected = 0;
                error = "";
                SessionState.SetString("DirectVFX.LastJob", job);
                AssemblyReloadEvents.beforeAssemblyReload += Interrupt;
                EditorApplication.quitting += Interrupt;
                await DirectAuthorJob.Run(job, prompt.Trim(), key.Trim(), model.Trim(), DirectApi.Client, cancel.Token);
            }
            catch (Exception ex)
            {
                string message = cancel.IsCancellationRequested ? "Generation cancelled." :
                    ex is OperationCanceledException ? "The API request timed out. Try a shorter prompt or retry." : ex.Message;
                error = message;
                if (Directory.Exists(folder)) DirectAuthorJob.Status(folder, "Stopped · " + message);
            }
            finally
            {
                AssemblyReloadEvents.beforeAssemblyReload -= Interrupt;
                EditorApplication.quitting -= Interrupt;
                active = null;
                activeJob = null;
                cancel.Dispose();
                if (this) Repaint();
            }
        }
        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Describe the effect", EditorStyles.boldLabel);
            prompt = EditorGUILayout.TextArea(prompt, GUILayout.MinHeight(62));
            if (string.IsNullOrWhiteSpace(prompt) && GUILayout.Button("Try an example prompt"))
                prompt = "A moon jelly pulses through dark water. Its translucent blue bell contracts, then its fine pink tentacles catch up.";
            string environmentKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            apiKey = EditorGUILayout.PasswordField("OpenAI API key", apiKey ?? "");
            string key = string.IsNullOrWhiteSpace(apiKey) ? environmentKey : apiKey;
            if (string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(environmentKey))
                EditorGUILayout.LabelField("Using OPENAI_API_KEY from the editor environment.", EditorStyles.miniLabel);
            if (string.IsNullOrWhiteSpace(key))
                EditorGUILayout.LabelField("Enter your API key to enable generation.", EditorStyles.miniLabel);
            modelSettings = EditorGUILayout.Foldout(modelSettings, "Model settings", true);
            if (modelSettings) model = EditorGUILayout.TextField("API model", model);
            EditorGUILayout.HelpBox("OpenAI receives your brief, generated source, repair diagnostics and four Unity preview frames. A separate Unity Editor runs the generated C# locally. Choose a draft to import.", MessageType.Info);
            using (new EditorGUI.DisabledScope(active != null || string.IsNullOrWhiteSpace(prompt)
                || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(model)))
                if (GUILayout.Button("Generate effect")) StartJob(key);
            if (active != null && GUILayout.Button("Cancel generation")) active.Cancel();
            if (error.Length > 0) EditorGUILayout.HelpBox(error, MessageType.Error);
            if (job.Length > 0)
            {
                EditorGUILayout.LabelField(status, EditorStyles.wordWrappedLabel);
                bool firstReady = File.Exists(Path.Combine(job, "draft-1", "ready"));
                bool revisionReady = File.Exists(Path.Combine(job, "draft-2", "ready"));
                if (firstReady && !revisionReady) selected = 0;
                if (revisionReady) selected = GUILayout.Toolbar(selected, new[] {"First draft", "Visual revision"});
                else if (firstReady) EditorGUILayout.LabelField("First draft", EditorStyles.boldLabel);
                var folder = Path.Combine(job, "draft-" + (selected + 1));
                if (File.Exists(Path.Combine(folder, "ready")))
                {
                    var file = Path.Combine(folder, "frames.jpg");
                    if (previewPath != file || !preview)
                    {
                        if (preview) DestroyImmediate(preview);
                        preview = new Texture2D(2, 2);
                        preview.LoadImage(File.ReadAllBytes(file));
                        previewPath = file;
                    }
                    GUI.DrawTexture(EditorGUILayout.GetControlRect(false, Mathf.Max(150, (position.width-24) * 9/16f)), preview, ScaleMode.ScaleToFit);
                    if (GUILayout.Button("Review and import this prefab"))
                        AssetDatabase.ImportPackage(Path.Combine(folder,"Effect.unitypackage"), true);
                }
                if (GUILayout.Button("Show job files")) EditorUtility.RevealInFinder(job);
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
