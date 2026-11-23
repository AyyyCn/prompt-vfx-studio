using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Arcade.VfxAuthor.Editor
{
    static class EffectRecipe
    {
        public const string Package = "Packages/com.arcade.vfx-author";
        static JObject recipeSchema;
        public static JObject Schema => recipeSchema ??= JObject.Parse(File.ReadAllText(Path.Combine(
            PackageInfo.FindForAssembly(typeof(EffectRecipe).Assembly).resolvedPath, "Editor/RecipeSchema.json")));
        static JObject LayerSchema => (JObject)Schema["properties"]["layers"]["items"];

        public static JObject PatchSchema(string target = null)
        {
            var setters = (JObject)LayerSchema.DeepClone();
            setters.Remove("required");
            ((JObject)setters["properties"]).Remove("id");
            var edit = new JObject {
                ["type"] = "object", ["additionalProperties"] = false,
                ["required"] = new JArray("layer", "set"),
                ["properties"] = new JObject { ["layer"] = LayerSchema["properties"]["id"].DeepClone(), ["set"] = setters }
            };
            if (target != null) edit["properties"]["layer"]["enum"] = new JArray(target);
            return new JObject {
                ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("edits"),
                ["properties"] = new JObject { ["edits"] = new JObject {
                    ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 8, ["items"] = edit } }
            };
        }

        public static JObject Apply(JObject previous, JObject answer, string target = null)
        {
            var recipe = (JObject)(previous ?? answer).DeepClone();
            if (previous != null)
            {
                Check(answer, PatchSchema(target), "revision");
                foreach (var edit in answer["edits"])
                {
                    var layer = recipe["layers"].FirstOrDefault(item => (string)item["id"] == (string)edit["layer"]);
                    if (layer == null) throw new FormatException($"Unknown layer '{edit["layer"]}'. Use an existing id.");
                    var fields = (JObject)edit["set"];
                    if (!fields.HasValues) throw new FormatException("An edit needs at least one changed field.");
                    foreach (var field in fields.Properties()) layer[field.Name] = field.Value.DeepClone();
                }
                if (JToken.DeepEquals(previous, recipe)) throw new FormatException("Revision did not change any layer.");
            }
            Check(recipe, Schema, "effect");
            var ids = new HashSet<string>();
            foreach (JObject layer in recipe["layers"])
            {
                if (!ids.Add((string)layer["id"])) throw new FormatException("Each layer needs a unique id.");
                if (Value(layer,"rate") == 0 && Value(layer,"burst") == 0)
                    throw new FormatException($"Layer '{layer["id"]}' must emit via rate or burst.");
            }
            return recipe;
        }

        public static float Value(JToken layer, string key) =>
            (float)(layer[key] ?? LayerSchema["properties"][key]["default"]);

        static void Check(JToken value, JToken schema, string path)
        {
            bool valid;
            switch ((string)schema["type"])
            {
                case "object":
                    if (!(value is JObject obj)) break;
                    foreach (var key in schema["required"] ?? new JArray())
                        if (obj[(string)key] == null) throw new FormatException($"Missing {path}.{key}.");
                    foreach (var field in obj.Properties())
                    {
                        var rules = schema["properties"][field.Name];
                        if (rules == null) throw new FormatException($"Unknown field {path}.{field.Name}.");
                        Check(field.Value, rules, path + "." + field.Name);
                    }
                    return;
                case "array":
                    if (!(value is JArray array) || array.Count < (int)schema["minItems"] || array.Count > (int)schema["maxItems"]) break;
                    foreach (var item in array) Check(item, schema["items"], path + "[]");
                    return;
                case "integer":
                case "number":
                    valid = value.Type == JTokenType.Integer || (value.Type == JTokenType.Float && (string)schema["type"] == "number");
                    if (valid && (double)value >= (double)schema["minimum"] && (double)value <= (double)schema["maximum"]) return;
                    break;
                case "string":
                    if (value.Type != JTokenType.String) break;
                    var s = (string)value;
                    if (s.Length >= ((int?)schema["minLength"] ?? 0) && s.Length <= ((int?)schema["maxLength"] ?? 128)
                        && (schema["enum"] == null || schema["enum"].Values<string>().Contains(s))
                        && (schema["pattern"] == null || Regex.IsMatch(s, (string)schema["pattern"]))) return;
                    break;
            }
            throw new FormatException($"Invalid value at {path}. Use the types and bounds in the schema.");
        }
    }
}
