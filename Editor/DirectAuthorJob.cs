using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Arcade.VfxAuthor.Editor
{
    internal static class DirectAuthorJob
    {
        internal static async Task Run(string job, string prompt, string key, string model, HttpClient client, CancellationToken cancel)
        {
            string token = "VfxStudio_" + Guid.NewGuid().ToString("N").Substring(0,12);
            string scratch = Path.Combine(Path.GetTempPath(), "prompt-vfx-" + token);
            string effect = Path.Combine(scratch, "Assets", token);
            string tools = Path.Combine(PackageInfo.FindForAssembly(typeof(DirectAuthorJob).Assembly).resolvedPath, "Tools~");
            string unity = EditorApplication.applicationPath;
            if (Directory.Exists(unity)) unity = Path.Combine(unity, "Contents/MacOS/Unity");
            string urp = PackageInfo.GetAllRegisteredPackages().First(p => p.name == "com.unity.render-pipelines.universal").version;
            Directory.CreateDirectory(job);
            Directory.CreateDirectory(Path.Combine(scratch,"Assets/Editor"));
            Directory.CreateDirectory(Path.Combine(scratch,"Packages"));
            Directory.CreateDirectory(Path.Combine(scratch,"ProjectSettings"));
            File.WriteAllText(Path.Combine(job,"prompt.txt"), prompt);
            File.WriteAllText(Path.Combine(job,"scratch.txt"), scratch);
            File.WriteAllText(Path.Combine(job,"prefab-path.txt"), "Assets/" + token + "/Effect.prefab");
            File.WriteAllText(Path.Combine(scratch,"ProjectSettings/ProjectVersion.txt"), "m_EditorVersion: " + Application.unityVersion + "\n");
            var deps = new JObject { ["com.unity.render-pipelines.universal"] = urp, ["com.unity.modules.particlesystem"] = "1.0.0" };
            File.WriteAllText(Path.Combine(scratch,"Packages/manifest.json"), new JObject { ["dependencies"] = deps }.ToString());
            string contract = File.ReadAllText(Path.Combine(tools, "contract.txt")).Replace("EFFECT_ID", token);
            JArray prior = null;
            byte[] image = null;
            for (int draft = 1; draft <= 2; draft++)
            {
                string dest = Path.Combine(job, "draft-" + draft);
                Directory.CreateDirectory(dest);
                string errors = "";
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    Status(job, $"Draft {draft}/2 · " + (attempt == 0 ? "requesting source from OpenAI…" : "requesting a repair…"));
                    var files = await DirectApi.Generate(client, key, model, contract, prompt, prior, errors, image, cancel);
                    cancel.ThrowIfCancellationRequested();
                    prior = files;
                    try
                    {
                        WriteSources(files, effect);
                        File.Copy(Path.Combine(tools,"StudioHarness.cs"), Path.Combine(scratch,"Assets/Editor/StudioHarness.cs"), true);
                        Status(job, $"Draft {draft}/2 · compiling and rendering in Unity…");
                        errors = await Validate(unity, scratch, dest, token, attempt, cancel);
                    }
                    catch (FormatException e) { errors = e.Message; }
                    if (errors.Length == 0) break;
                    if (attempt == 2) throw new InvalidOperationException("Unity could not validate the draft. " + errors);
                }
                image = File.ReadAllBytes(Path.Combine(dest, "frames.jpg"));
            }
            Status(job, "Complete · compare the previews and import your choice.");
        }

        internal static void Status(string job, string message) => File.WriteAllText(Path.Combine(job, "status.txt"), message);

        static readonly Regex SourcePath = new Regex(@"^(?:[A-Za-z0-9_-][A-Za-z0-9 _-]*/)*[A-Za-z0-9_-][A-Za-z0-9 _.\-]*\.(?:cs|shader|hlsl|cginc|compute|shaderinc)$", RegexOptions.IgnoreCase);
        static void WriteSources(JArray files, string effect)
        {
            var paths = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files.OfType<JObject>())
            {
                string path = (string)file["path"] ?? "";
                string source = (string)file["content"];
                if (!SourcePath.IsMatch(path) || path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                    || path.Split('/').Any(p => Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase))
                    || paths.ContainsKey(path) || string.IsNullOrWhiteSpace(source))
                    throw new FormatException("Return unique relative Unity source paths with nonempty contents. Invalid file: " + path);
                paths.Add(path, source);
            }
            if (!paths.ContainsKey("Editor/BuildEffect.cs")) throw new FormatException("Include Editor/BuildEffect.cs in the complete source set.");
            // Retire prior generated assets too, so an obsolete prefab cannot pass a failed rebuild.
            if (Directory.Exists(effect)) Directory.Move(effect, Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(effect)), "previous-" + Guid.NewGuid().ToString("N")));
            foreach (var file in paths)
            {
                string path = Path.Combine(effect, file.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, file.Value);
            }
        }

        static async Task<string> Validate(string unity, string scratch, string dest, string token, int attempt, CancellationToken cancel)
        {
            string log = Path.Combine(dest, $"unity-{attempt}.log");
            var start = new ProcessStartInfo(unity, $"-batchmode -projectPath \"{scratch}\" -executeMethod PromptVfxStudioHarness.Run -logFile \"{log}\"")
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = scratch };
            start.EnvironmentVariables.Remove("OPENAI_API_KEY");
            start.EnvironmentVariables["VFX_STUDIO_OUTPUT"] = dest;
            start.EnvironmentVariables["VFX_STUDIO_ID"] = token;
            string marker = Path.Combine(dest, "ready");
            File.Delete(marker);
            using var process = Process.Start(start);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            using var stopChild = timeout.Token.Register(() => {
                try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
            });
            try
            {
                while (!process.HasExited) await Task.Delay(200, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                cancel.ThrowIfCancellationRequested();
                throw new TimeoutException("The scratch Unity Editor timed out. Inspect its log in the job folder.");
            }
            cancel.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested) throw new TimeoutException("The scratch Unity Editor timed out. Inspect its log in the job folder.");
            if (process.ExitCode == 0 && File.Exists(marker) && File.Exists(Path.Combine(dest,"frames.jpg"))
                && File.Exists(Path.Combine(dest,"Effect.unitypackage"))) return "";
            var lines = File.Exists(log) ? File.ReadLines(log).Where(l => Regex.IsMatch(l, "error|exception|failed|missing", RegexOptions.IgnoreCase)).ToList() : new List<string>();
            string details = string.Join("\n", lines.Skip(Math.Max(0, lines.Count - 40)));
            if (details.Length > 6500) details = details.Substring(details.Length - 6500);
            return $"Unity exited {process.ExitCode} before completing the prefab and its previews.\n" + details;
        }
    }
}
