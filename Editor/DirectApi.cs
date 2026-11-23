using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Arcade.VfxAuthor.Editor
{
    internal static class DirectApi
    {
        internal const string DefaultModel = "gpt-5.4";
        internal static readonly HttpClient Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromMinutes(12) };

        static JObject FileSchema()
        {
            var file = new JObject {
                ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("path", "content"),
                ["properties"] = new JObject {
                    ["path"] = new JObject { ["type"] = "string" },
                    ["content"] = new JObject { ["type"] = "string" }
                }
            };
            return new JObject {
                ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("files"),
                ["properties"] = new JObject { ["files"] = new JObject { ["type"] = "array", ["items"] = file } }
            };
        }

        internal static async Task<JArray> Generate(HttpClient client, string key, string model, string contract,
            string prompt, JArray prior, string diagnostics, byte[] image, CancellationToken cancel)
        {
            var context = new StringBuilder(prompt);
            if (prior != null) context.Append("\nPrevious source files:\n").Append(prior.ToString(Formatting.None));
            if (image != null) context.Append("\nInspect the four Play Mode frames. Revise the source to improve the visible effect.");
            if (diagnostics.Length > 0) context.Append("\nCorrect these Unity diagnostics:\n").Append(diagnostics);
            var content = new JArray(new JObject { ["type"] = "input_text", ["text"] = context.ToString() });
            if (image != null)
                content.Add(new JObject { ["type"] = "input_image", ["image_url"] = "data:image/jpeg;base64," + Convert.ToBase64String(image) });
            var body = new JObject {
                ["model"] = model, ["store"] = false, ["instructions"] = contract,
                ["input"] = new JArray(new JObject { ["role"] = "user", ["content"] = content }),
                ["text"] = new JObject { ["format"] = new JObject { ["type"] = "json_schema",
                    ["name"] = "unity_effect_source", ["strict"] = true, ["schema"] = FileSchema() } }
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, cancel);
            if (!response.IsSuccessStatusCode)
            {
                string advice;
                switch (response.StatusCode)
                {
                    case HttpStatusCode.Unauthorized: advice = "Check your OpenAI API key."; break;
                    case HttpStatusCode.Forbidden: case HttpStatusCode.NotFound: advice = "Check the model ID and your API project's access."; break;
                    case (HttpStatusCode)429: advice = "Check API credits and rate limits before retrying."; break;
                    case HttpStatusCode.BadRequest: advice = "Choose a Responses model that supports image input and structured output, and check the prompt length."; break;
                    default: advice = "Retry after checking the API service and your connection."; break;
                }
                throw new InvalidOperationException($"OpenAI returned HTTP {(int)response.StatusCode}. {advice}");
            }
            var envelope = JObject.Parse(await response.Content.ReadAsStringAsync());
            if ((string)envelope["status"] != "completed")
                throw new InvalidOperationException("OpenAI did not finish the response. Try a shorter brief or another model.");
            var parts = (envelope["output"] as JArray ?? new JArray()).OfType<JObject>()
                .Where(item => (string)item["type"] == "message")
                .SelectMany(item => (item["content"] as JArray ?? new JArray()).OfType<JObject>()).ToList();
            if (parts.Any(p => (string)p["type"] == "refusal"))
                throw new InvalidOperationException("The model declined the request. Revise the effect description.");
            string json = string.Concat(parts.Where(p => (string)p["type"] == "output_text").Select(p => (string)p["text"]));
            if (string.IsNullOrWhiteSpace(json)) throw new FormatException("The response contained no source files.");
            return JObject.Parse(json)["files"] as JArray ?? throw new FormatException("The response needs a files array.");
        }
    }
}
