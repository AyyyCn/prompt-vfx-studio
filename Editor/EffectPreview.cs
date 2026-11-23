using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Arcade.VfxAuthor.Editor
{
    sealed class EffectPreview : IDisposable
    {
        readonly PreviewRenderUtility preview;
        readonly EffectPlayback playback;
        readonly SurfaceBuild surface;
        readonly GeneratedSurfaceClock clock;
        float position;

        public EffectPreview(JObject recipe)
        {
            preview = new PreviewRenderUtility();
            try
            {
                if (SurfaceProgram.IsSurface(recipe)) surface = new SurfaceBuild(recipe);
                var root = surface != null ? surface.Root : EffectBuilder.Build(recipe);
                preview.AddSingleGO(root);
                playback = root.GetComponent<EffectPlayback>();
                clock = root.GetComponent<GeneratedSurfaceClock>();
                var camera = preview.camera;
                camera.transform.SetPositionAndRotation(new Vector3(0,0,-12),Quaternion.identity);
                camera.orthographic = true;
                camera.orthographicSize = 3.2f;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 24;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.035f,0.031f,0.06f);
                if (playback) playback.Restart();
            }
            catch { Dispose(); throw; }
        }

        public float Length => clock ? clock.duration : playback.length;

        public void Draw(Rect rect, float time)
        {
            if (Event.current.type != EventType.Repaint || rect.width < 1 || rect.height < 1) return;
            if (clock) clock.Sample(time);
            else
            {
                if (time < position) { playback.Restart(); position = 0; }
                playback.Advance(time - position);
            }
            position = time;
            preview.BeginPreview(rect, GUIStyle.none);
            preview.camera.orthographicSize = Mathf.Max(3.2f,4.3f * rect.height/rect.width);
            preview.Render(true);
            GUI.DrawTexture(rect, preview.EndPreview(), ScaleMode.StretchToFill, false);
        }

        public void Dispose() { preview.Cleanup(); surface?.Dispose(); }
    }
}
