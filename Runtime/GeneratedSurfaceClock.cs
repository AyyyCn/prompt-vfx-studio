using UnityEngine;

namespace Arcade.VfxAuthor
{
    public sealed class GeneratedSurfaceClock : MonoBehaviour
    {
        [Min(1)] public float duration = 6;
        public Renderer[] surfaces = System.Array.Empty<Renderer>();
        [HideInInspector, TextArea] public string source;
        float elapsed;
        MaterialPropertyBlock block;
        static readonly int TimeId = Shader.PropertyToID("_EffectTime");
        void OnEnable() { elapsed = 0; Sample(0); }
        void Update() { elapsed = Mathf.Repeat(elapsed + Time.deltaTime, duration); Sample(elapsed); }
        public void Sample(float seconds)
        {
            block ??= new MaterialPropertyBlock();
            foreach (var surface in surfaces)
            {
                if (!surface) continue;
                surface.GetPropertyBlock(block);
                block.SetFloat(TimeId, seconds);
                surface.SetPropertyBlock(block);
            }
        }
    }
}
