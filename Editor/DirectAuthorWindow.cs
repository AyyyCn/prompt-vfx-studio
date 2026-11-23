using System;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Arcade.VfxAuthor.Editor
{
    public sealed class DirectAuthorWindow : EditorWindow
    {
        enum Coverage { FromPrompt, LocalVolume, GlobalAroundCamera }
        [SerializeField] Coverage coverage;
        [SerializeField] string prompt = "";
        [SerializeField] string model = DirectApi.DefaultModel;
        [NonSerialized] static string apiKey;
        static CancellationTokenSource active;
        static string activeJob;
        [SerializeField] string job = "";
        [SerializeField] int selected;
        Texture2D preview;
        string previewPath, status = "", error = "";
        string cachedJob, prefabPath;
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
                string brief = prompt.Trim();
                if (coverage == Coverage.GlobalAroundCamera)
                    brief += "\nPlacement: global around the camera by default. Support switching to a local volume in the prefab Inspector.";
                else if (coverage == Coverage.LocalVolume)
                    brief += "\nPlacement: keep the effect local to its authored transform by default.";
                await DirectAuthorJob.Run(job, brief, key.Trim(), model.Trim(), DirectApi.Client, cancel.Token);
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
            coverage = (Coverage)EditorGUILayout.EnumPopup(new GUIContent("Coverage",
                "Choose how the generated prefab occupies the scene. Global keeps an effect volume around a moving camera."), coverage);
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
                    if (cachedJob != job)
                    {
                        prefabPath = DraftImport.PrefabPath(job);
                        cachedJob = job;
                    }
                    var prefab = string.IsNullOrEmpty(prefabPath) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                    var label = selected == 0 ? "first draft" : "visual revision";
                    using (new EditorGUI.DisabledScope(DraftImport.Busy || active != null || EditorApplication.isPlayingOrWillChangePlaymode))
                    {
                        if (GUILayout.Button("Import " + label + " and select prefab"))
                        {
                            bool existingFolder = !string.IsNullOrEmpty(prefabPath) &&
                                AssetDatabase.IsValidFolder(Path.GetDirectoryName(prefabPath).Replace('\\', '/'));
                            if (!existingFolder || EditorUtility.DisplayDialog("Import " + label + "?",
                                "This job already has assets in the project. Importing the selected draft can replace files in " +
                                Path.GetDirectoryName(prefabPath) + ".", "Import selected draft", "Cancel"))
                                DraftImport.Begin(job, selected + 1, prefabPath);
                        }
                    }
                    if (active != null) EditorGUILayout.LabelField("Finish or cancel generation before importing.", EditorStyles.wordWrappedMiniLabel);
                    if (EditorApplication.isPlayingOrWillChangePlaymode) EditorGUILayout.LabelField("Exit Play Mode to import.", EditorStyles.miniLabel);
                    string notice = DraftImport.Notice(job);
                    if (notice.Length > 0) EditorGUILayout.HelpBox(notice, MessageType.Info);
                    if (prefab && GUILayout.Button("Select existing prefab in Project")) DraftImport.Select(prefab);
                }
                if (GUILayout.Button("Show job files")) EditorUtility.RevealInFinder(job);
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
