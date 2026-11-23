using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Arcade.VfxAuthor.Editor
{
    [InitializeOnLoad]
    internal static class DraftImport
    {
        const string Prefix = "PromptVFX.Import.";
        static string Read(string key) => SessionState.GetString(Prefix + key, "");
        static void Write(string key, string value) => SessionState.SetString(Prefix + key, value);
        internal static bool Busy => Read("package").Length > 0;
        internal static string Notice(string job) => Read("job") == job ? Read("message") : "";

        static DraftImport()
        {
            AssetDatabase.importPackageCompleted += name => {
                if (Matches(name)) Write("completed", "yes");
            };
            AssetDatabase.importPackageFailed += (name, error) => {
                if (Matches(name)) Finish("Unity could not import the package. " + error);
            };
            AssetDatabase.importPackageCancelled += name => {
                if (Matches(name)) Finish("Import cancelled.");
            };
            EditorApplication.update += CheckResult;
        }

        static bool Matches(string name) => Busy && string.Equals(Path.GetFileNameWithoutExtension(name),
            Path.GetFileNameWithoutExtension(Read("package")), StringComparison.OrdinalIgnoreCase);

        internal static string PrefabPath(string job)
        {
            string manifest = Path.Combine(job, "prefab-path.txt");
            if (File.Exists(manifest)) return File.ReadAllText(manifest).Trim();
            string pointer = Path.Combine(job, "scratch.txt");
            if (!File.Exists(pointer)) return "";
            string scratch = File.ReadAllText(pointer).Trim();
            // Released API versions included the effect ID in the scratch directory name.
            var id = Regex.Match(Path.GetFileName(scratch), @"VfxStudio_[0-9a-f]{12}$");
            if (id.Success) return "Assets/" + id.Value + "/Effect.prefab";
            string assets = Path.Combine(scratch, "Assets");
            if (!Directory.Exists(assets)) return "";
            string dir = Directory.GetDirectories(assets, "VfxStudio_*").FirstOrDefault();
            return dir == null ? "" : "Assets/" + Path.GetFileName(dir) + "/Effect.prefab";
        }

        internal static void Begin(string job, int draft, string path)
        {
            if (Busy) return;
            Write("job", job);
            string archive = Path.Combine(job, "draft-" + draft, "Effect.unitypackage");
            if (!File.Exists(archive)) { Finish("The exported package is missing. Open the job files to check the draft."); return; }
            Write("package", archive);
            Write("prefab", path);
            Write("label", draft == 1 ? "first draft" : "visual revision");
            Write("completed", "");
            Write("message", "Importing the " + Read("label") + "…");
            Write("start", DateTime.UtcNow.Ticks.ToString());
            EditorApplication.delayCall += () => {
                try { AssetDatabase.ImportPackage(archive, false); }
                catch (Exception ex) { Finish("Could not open the package. " + ex.Message); }
            };
        }

        static void Finish(string message)
        {
            Write("package", "");
            Write("message", message);
        }

        static void CheckResult()
        {
            if (!Busy || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (Read("completed") == "yes")
            {
                var path = Read("prefab");
                var asset = path.Length == 0 ? null : AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (asset) Select(asset);
                Finish(asset ? "Imported the " + Read("label") + ". Drag the selected prefab into the Hierarchy to play it.\n" + path :
                    "Unity finished importing. Search for Effect t:Prefab in the Project window.");
            }
            else if (long.TryParse(Read("start"), out var ticks) && DateTime.UtcNow.Ticks - ticks > TimeSpan.FromMinutes(3).Ticks)
                Finish("Unity did not report completion. Check the Console or select the existing prefab below.");
        }

        internal static void Select(GameObject prefab)
        {
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = prefab;
            EditorGUIUtility.PingObject(prefab);
        }
    }
}
