using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor.PackageManager;

namespace Arcade.VfxAuthor.Editor
{
    static class SurfaceProgram
    {
        public const string Kind = "surface-source-v1";
        public static bool IsSurface(JObject draft) => (string)draft?["kind"] == Kind;
        internal static string Resource(string file) => File.ReadAllText(Path.Combine(
            PackageInfo.FindForAssembly(typeof(SurfaceProgram).Assembly).resolvedPath,"Editor",file));

        public static async Task<JObject> Request(string brief, JObject previous, string exe, CancellationToken cancel, string target, bool visualReview = true, Action<string> status = null)
        {
            status?.Invoke("Authoring original shader and geometry source…");
            byte[][] evidence = null;
            if (previous != null && visualReview)
            {
                status?.Invoke("Reviewing the current Unity render and your edit note…");
                evidence = new[]{RenderReview.Capture(previous,(float)previous["duration"])};
            }
            var draft = await Author(brief,previous,exe,cancel,target,visualReview,evidence);
            if (previous != null || !visualReview) return draft;
            return await RenderReview.Refine(draft,brief,exe,cancel,status);
        }

        internal static async Task<JObject> Author(string brief,JObject previous,string exe,CancellationToken cancel,string target,bool quality,byte[][] images = null)
        {
            var schema = JObject.Parse(Resource("SurfaceSchema.json"));
            var layerSchema = (JObject)schema["properties"]["layers"]["items"];
            string request = brief;
            if (previous != null)
            {
                var clean = (JObject)previous.DeepClone();
                clean.Remove("visualReview");
                JToken current = target == null ? clean : clean["layers"].Single(l => (string)l["id"] == target);
                request = "Revise this draft.\n" + current.ToString(Formatting.None) + "\nRequested change: " + brief;
                if (target != null) request += "\nReturn only the replacement layer with the same id. Effect duration: " + previous["duration"];
            }
            string direction = Resource("SurfaceAuthoring.txt");
            for (int attempt = 0; ; attempt++)
            {
                var answer = await ModelAuthor.Infer(request,target == null ? schema : layerSchema,exe,cancel,direction,quality,images);
                cancel.ThrowIfCancellationRequested();
                try
                {
                    JObject draft;
                    if (target == null) draft = answer;
                    else
                    {
                        if ((string)answer["id"] != target) throw new FormatException("Keep the selected layer id.");
                        draft = (JObject)previous.DeepClone();
                        draft["layers"].Single(l => (string)l["id"] == target).Replace(answer);
                    }
                    draft.Remove("visualReview");
                    draft["kind"] = Kind;
                    Validate(draft);
                    // Compile the actual vertex/fragment pair on the editor's graphics backend.
                    using var check = new SurfaceBuild(draft);
                    return draft;
                }
                catch (FormatException ex) when (attempt < 2)
                {
                    request += "\nInvalid response:\n" + answer.ToString(Formatting.None) + "\nUnity diagnostic: " + ex.Message + "\nReturn corrected source.";
                }
            }
        }

        static readonly Regex Loop = new Regex(@"\bfor\s*\(\s*int\s+(\w+)\s*=\s*0\s*;\s*\1\s*<\s*(\d{1,2})\s*;\s*(?:\1\s*\+\+|\+\+\s*\1)\s*\)");
        static void CheckBody(string body)
        {
            string code = Regex.Replace(body,@"/\*[\s\S]*?\*/|//[^\n]*","");
            if (Regex.IsMatch(code,@"[#""\\]|\b(?:ENDHLSL|HLSLPROGRAM|CGPROGRAM|while|do|discard|register|cbuffer|asm|struct|SamplerState|RW\w+|Texture\w*|Buffer|groupshared)\b",RegexOptions.IgnoreCase))
                throw new FormatException("Use numeric HLSL functions and bounded for loops only.");
            int depth = 0;
            foreach (var c in code)
            {
                if (c == '{') depth++;
                if (c == '}' && --depth < 0) throw new FormatException("Unbalanced shader braces.");
            }
            if (depth != 0) throw new FormatException("Unbalanced shader braces.");
            var loops = Loop.Matches(code);
            if (loops.Count != Regex.Matches(code,@"\bfor\b").Count || loops.Cast<Match>().Any(m => int.Parse(m.Groups[2].Value) > 32))
                throw new FormatException("Use for(int k=0;k<N;k++) with a literal N of at most 32.");
        }

        public static void Validate(JObject draft)
        {
            if (!(draft["layers"] is JArray layers) || layers.Count < 1 || layers.Count > 5
                || string.IsNullOrWhiteSpace((string)draft["name"]) || !((float?)draft["duration"] >= 1 && (float?)draft["duration"] <= 20))
                throw new FormatException("Supply a name, duration of 1–20 seconds and 1–5 layers.");
            var ids = new HashSet<string>(); long vertices = 0;
            foreach (var l in layers)
            {
                string id = (string)l["id"] ?? "";
                if (!Regex.IsMatch(id,@"^[a-z][a-z0-9_-]{0,39}$") || !ids.Add(id))
                    throw new FormatException("Supply unique lowercase layer identifiers.");
                int u = Integer(l,"segmentsU",256), v = Integer(l,"segmentsV",128), copies = Integer(l,"copies",4096);
                vertices += (long)(u+1)*(v+1)*copies;
                if (vertices > 100000) throw new FormatException("Reduce mesh resolution or copies to fit the 100000 vertex budget.");
                if ((string)l["blend"] != "alpha" && (string)l["blend"] != "additive") throw new FormatException("Choose alpha or additive blending.");
                foreach (var key in new[]{"helpers","geometry","shading"})
                {
                    if (l[key]?.Type != JTokenType.String || ((string)l[key]).Length > 16000 ||
                        (key != "helpers" && string.IsNullOrWhiteSpace((string)l[key])))
                        throw new FormatException("Supply numeric shader source for " + key + ".");
                    CheckBody((string)l[key]);
                }
            }
        }

        static int Integer(JToken layer, string field, int max)
        {
            if (layer[field]?.Type != JTokenType.Integer || (long)layer[field] < 1 || (long)layer[field] > max)
                throw new FormatException($"Use an integer from 1 to {max} for {field}.");
            return (int)layer[field];
        }
    }
}
