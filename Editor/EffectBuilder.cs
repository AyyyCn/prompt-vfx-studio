using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Arcade.VfxAuthor.Editor
{
    static class EffectBuilder
    {
        public static GameObject Build(JObject recipe)
        {
            var root = new GameObject((string)recipe["name"]);
            root.SetActive(false);
            try
            {
                var effect = root.AddComponent<EffectPlayback>();
                effect.recipe = recipe.ToString(Formatting.None);
                effect.layers = recipe["layers"].Select(layer => BuildLayer(root.transform, layer)).ToArray();
                effect.length = recipe["layers"].Max(l => N(l,"delay") + N(l,"duration") + N(l,"lifetime")) + 0.1f;
                root.SetActive(true);
                return root;
            }
            catch { UnityEngine.Object.DestroyImmediate(root); throw; }
        }

        static float N(JToken layer, string key) => EffectRecipe.Value(layer, key);

        static EmitterPath BuildLayer(Transform parent, JToken layer)
        {
            string id = (string)layer["id"];
            var go = new GameObject(id);
            go.transform.SetParent(parent, false);
            var path = new EmitterPath {
                particles = go.AddComponent<ParticleSystem>(),
                origin = new Vector3(N(layer,"x"), N(layer,"y")),
                travel = new Vector3(N(layer,"travelX"), N(layer,"travelY")),
                delay = N(layer,"delay"), duration = N(layer,"duration"), arc = N(layer,"arc")
            };
            go.transform.localPosition = path.origin;
            var ps = path.particles;
            ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            uint seed = 2166136261;
            foreach (char c in id) seed = unchecked((seed ^ c) * 16777619u);
            ps.randomSeed = seed == 0 ? 1 : seed;
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.duration = path.duration;
            main.startDelay = path.delay;
            main.startLifetime = N(layer,"lifetime");
            main.startSpeed = 0;
            main.startSize = N(layer,"size");
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 1280;
            ColorUtility.TryParseHtmlString((string)layer["color"], out var tint);
            tint.a = N(layer,"opacity");
            main.startColor = tint;
            var emission = ps.emission;
            emission.rateOverTime = N(layer,"rate");
            short count = (short)N(layer,"burst");
            if (count > 0) emission.SetBursts(new[] { new ParticleSystem.Burst(0, count) });

            var shape = ps.shape;
            string kind = (string)layer["shape"] ?? "point";
            shape.enabled = kind != "point";
            if (shape.enabled)
            {
                shape.shapeType = kind == "box" ? ParticleSystemShapeType.Box : ParticleSystemShapeType.Circle;
                shape.radius = N(layer,"radius");
                shape.radiusThickness = 0.08f;
                if (kind == "box") shape.scale = new Vector3(2, 2, 0.01f) * N(layer,"radius");
            }
            var velocity = ps.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            velocity.x = N(layer,"vx");
            velocity.y = N(layer,"vy");
            velocity.z = 0f;
            velocity.radial = N(layer,"radial");
            var noise = ps.noise;
            noise.enabled = N(layer,"noise") > 0;
            noise.strength = N(layer,"noise");
            noise.frequency = 0.65f;
            var rotation = ps.rotationOverLifetime;
            rotation.enabled = N(layer,"spin") != 0;
            rotation.z = N(layer,"spin") * Mathf.Deg2Rad;
            string profile = (string)layer["profile"] ?? "shrink";
            var sizing = ps.sizeOverLifetime;
            sizing.enabled = profile != "steady";
            var curve = profile == "grow" ? AnimationCurve.EaseInOut(0,0.03f,1,1)
                : profile == "pulse" ? new AnimationCurve(new Keyframe(0,0.05f),new Keyframe(0.22f,1),new Keyframe(1,0.08f))
                : AnimationCurve.EaseInOut(0,1,1,0.1f);
            sizing.size = new ParticleSystem.MinMaxCurve(1f,curve);
            var fade = ps.colorOverLifetime;
            fade.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(new[] {new GradientColorKey(Color.white,0),new GradientColorKey(Color.white,1)},
                new[] {new GradientAlphaKey(1,0),new GradientAlphaKey(0.9f,0.57f),new GradientAlphaKey(0,1)});
            fade.color = gradient;
            string sprite = (string)layer["sprite"];
            string materialName = char.ToUpperInvariant(sprite[0]) + sprite.Substring(1);
            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = Material(materialName);
            var trails = ps.trails;
            trails.enabled = N(layer,"trail") > 0;
            if (trails.enabled)
            {
                trails.lifetime = N(layer,"trail");
                trails.minVertexDistance = 0.055f;
                trails.widthOverTrail = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0,1,1,0));
                renderer.trailMaterial = Material("Glow");
            }
            return path;
        }

        static Material Material(string name) =>
            AssetDatabase.LoadAssetAtPath<Material>($"{EffectRecipe.Package}/Shaders/{name}.mat")
            ?? throw new InvalidOperationException($"Missing particle material {name}.");

        public static GameObject Save(JObject recipe, string path)
        {
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) || !path.EndsWith(".prefab",StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose a prefab path inside Assets.");
            var root = Build(recipe);
            try
            {
                var saved = PrefabUtility.SaveAsPrefabAsset(root, path, out bool ok);
                if (!ok || !saved) throw new InvalidOperationException($"Could not save {path}.");
                return saved;
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
