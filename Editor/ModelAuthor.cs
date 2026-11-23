using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Arcade.VfxAuthor.Editor
{
    static class ModelAuthor
    {
        const string Direction = "You are a Unity particle artist. Return structured data for an editable multilayer effect. "
            + "Work in an XY plane facing the camera, framing x=-4..4 and y=-2.5..2.5. Use visually distinct, legible layers and timings. "
            + "Each layer needs positive emission rate or burst. Size is world diameter. Units are meters and seconds. "
            + "travelX/travelY and arc move an emitter, leaving its particles in world space. vx/vy move individual particles. "
            + "A comet head is a dense stream of short-lived overlapping particles on a moving emitter; coordinate path and duration across its layers. "
            + "Use fewer, brighter focal elements and lower-opacity accents. IDs are permanent. On revision change only fields needed for the note. A note about one layer should change one layer; preserve other accents even if they share its timing.";

        public static string DefaultExecutable()
        {
            var configured = Environment.GetEnvironmentVariable("CLAUDE_BIN");
            if (!string.IsNullOrWhiteSpace(configured)) return configured;
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/bin/claude");
            return File.Exists(local) ? local : "claude";
        }

        public static async Task<JObject> Request(string brief, JObject previous, string executable, CancellationToken cancel, string target = null)
        {
            if (string.IsNullOrWhiteSpace(brief)) throw new ArgumentException("Enter an effect description or revision.");
            var schema = previous == null ? EffectRecipe.Schema : EffectRecipe.PatchSchema(target);
            string request = previous == null ? "Create this effect:\n" + brief
                : "Current recipe:\n" + previous.ToString(Formatting.None) + "\nRevision note:\n" + brief;
            if (target != null) request += "\nOnly edit layer: " + target;
            for (int attempt = 0; ; attempt++)
            {
                var answer = await Infer(request, schema, executable, cancel, Direction);
                cancel.ThrowIfCancellationRequested();
                try { return EffectRecipe.Apply(previous, answer, target); }
                catch (FormatException e) when (attempt == 0)
                {
                    request += "\nPrevious response:\n" + answer.ToString(Formatting.None) + "\nCorrect this validation error: " + e.Message;
                }
            }
        }

        static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        internal static async Task<JObject> Infer(string request, JObject schema, string executable, CancellationToken cancel, string direction, bool quality = false, byte[][] images = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), "unity-vfx-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                bool multimodal = images != null && images.Length > 0;
                string formats = multimodal ? "--input-format stream-json --output-format stream-json --verbose" : "--output-format json";
                string model = quality ? "opus --effort high" : "sonnet --effort low";
                var info = new ProcessStartInfo {
                    FileName = executable, WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    Arguments = "-p --model " + model + " " + formats + " --tools \"\" --strict-mcp-config "
                        + "--disable-slash-commands --no-session-persistence --system-prompt " + Quote(direction)
                        + " --json-schema " + Quote(schema.ToString(Formatting.None))
                };
                using var process = new Process { StartInfo = info };
                try { process.Start(); }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    throw new InvalidOperationException("Could not start Claude Code. Install it, sign in, and select its executable in Model settings.", ex);
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(TimeSpan.FromMinutes(quality ? 8 : 3));
                using var kill = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) {} });
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                if (multimodal)
                {
                    var content = new JArray();
                    foreach (var image in images)
                        content.Add(new JObject { ["type"]="image", ["source"]=new JObject {
                            ["type"]="base64", ["media_type"]="image/jpeg", ["data"]=Convert.ToBase64String(image) } });
                    content.Add(new JObject { ["type"]="text", ["text"]=request });
                    var message = new JObject { ["type"]="user", ["message"]=new JObject { ["role"]="user", ["content"]=content } };
                    await process.StandardInput.WriteLineAsync(message.ToString(Formatting.None));
                }
                else await process.StandardInput.WriteAsync(request);
                process.StandardInput.Close();
                await Task.WhenAll(output, errors);
                await Task.Run(() => process.WaitForExit());
                cancel.ThrowIfCancellationRequested();
                if (timeout.IsCancellationRequested) throw new TimeoutException("Claude timed out. Retry with a shorter brief.");
                var raw = output.Result.Trim();
                if (raw.Length == 0) throw new InvalidOperationException("Claude returned no output. Check the executable and its login. " + Trim(errors.Result));
                JObject envelope = null;
                if (multimodal)
                {
                    using var lines = new StringReader(raw);
                    string line;
                    while ((line=lines.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var item=JObject.Parse(line);
                        if ((string)item["type"]=="result") envelope=item;
                    }
                    if (envelope==null) throw new InvalidOperationException("Visual review ended without a result. " + Trim(errors.Result));
                }
                else envelope=JObject.Parse(raw);
                if (process.ExitCode != 0 || (bool?)envelope["is_error"] == true)
                    throw new InvalidOperationException("Claude request failed. " + Trim((string)envelope["result"] ?? errors.Result));
                if (envelope["structured_output"] is JObject data) return data;
                throw new InvalidOperationException("Claude returned no structured effect. " + Trim((string)envelope["result"] ?? "Retry the request."));
            }
            finally { try { Directory.Delete(directory, true); } catch (IOException) {} }
        }

        static string Trim(string text) => text.Length <= 450 ? text.Trim() : text.Substring(0,450).Trim();
    }
}
