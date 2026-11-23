using System;
using UnityEngine;

namespace Arcade.VfxAuthor
{
    [Serializable]
    public sealed class EmitterPath
    {
        public ParticleSystem particles;
        public Vector3 origin;
        public Vector3 travel;
        public float arc;
        public float delay;
        public float duration = 1f;
    }

    // The prefab contains ordinary Particle Systems. This component synchronizes moving
    // emitters and also drives the exact same simulation when scrubbing in the editor.
    public sealed class EffectPlayback : MonoBehaviour
    {
        public EmitterPath[] layers = Array.Empty<EmitterPath>();
        public float length = 3f;
        public bool repeat;
        [HideInInspector, TextArea] public string recipe;
        public float TimeInEffect { get; private set; }
        float accumulator;
        const float Step = 1f / 60f;

        void OnEnable() => Restart();

        void Update()
        {
            if (repeat && TimeInEffect >= length) Restart();
            Advance(Time.deltaTime);
        }

        public void Restart()
        {
            TimeInEffect = 0;
            accumulator = 0;
            foreach (var layer in layers)
            {
                if (!layer.particles) continue;
                layer.particles.transform.localPosition = layer.origin;
                layer.particles.Simulate(0, false, true, false);
            }
        }

        public void Advance(float seconds)
        {
            if (TimeInEffect >= length) return;
            accumulator += Mathf.Max(0, seconds);
            while (accumulator + 0.000001f >= Step && TimeInEffect < length)
            {
                accumulator -= Step;
                TimeInEffect += Step;
                foreach (var layer in layers)
                {
                    if (!layer.particles) continue;
                    float t = Mathf.Clamp01((TimeInEffect - layer.delay) / Mathf.Max(0.01f, layer.duration));
                    layer.particles.transform.localPosition = layer.origin + layer.travel * t
                        + Vector3.up * (4f * layer.arc * t * (1f - t));
                    layer.particles.Simulate(Step, false, false, false);
                }
            }
        }
    }
}
