using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

namespace Lotse.App
{
    /// <summary>
    /// Sharp text on the headset (the first device test found it blurry): the eye textures rendered at 1.4 times the
    /// default (URP's render scale, which URP hands to the XR system every frame, and XRSettings for good measure), no
    /// dynamic resolution, no foveated rendering, MSAA 4x from the URP asset, no camera post-processing and no FXAA (it
    /// softens thin strokes). Logged once under [Lotse] so the device log shows what the headset really uses.
    /// </summary>
    public static class DisplayQuality
    {
        public const float RenderScale = 1.4f;

        public static void Apply(Camera camera)
        {
            var urp = UniversalRenderPipeline.asset;
            if (urp != null) urp.renderScale = RenderScale;
            XRSettings.eyeTextureResolutionScale = RenderScale;
            var foveation = "not set (editor)";
            if (!Application.isEditor)
            {
                try
                {
                    OVRManager.foveatedRenderingLevel = OVRManager.FoveatedRenderingLevel.Off;
                    OVRManager.useDynamicFoveatedRendering = false;
                    if (OVRManager.instance != null) OVRManager.instance.enableDynamicResolution = false;
                    foveation = OVRManager.foveatedRenderingLevel + (OVRManager.useDynamicFoveatedRendering ? " (dynamic)" : "");
                }
                catch (System.Exception e)
                {
                    foveation = "unavailable: " + e.Message;
                }
            }
            var post = "no camera";
            if (camera != null)
            {
                var data = camera.GetUniversalAdditionalCameraData();
                data.renderPostProcessing = false;
                data.antialiasing = AntialiasingMode.None;
                post = "post-processing " + data.renderPostProcessing + ", camera AA " + data.antialiasing;
            }
            Debug.Log($"[Lotse] display: render scale {(urp != null ? urp.renderScale : 0f):0.00}, XR eye texture scale " +
                      $"{XRSettings.eyeTextureResolutionScale:0.00}, eye texture {XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}, " +
                      $"MSAA {(urp != null ? urp.msaaSampleCount : 0)}x, foveation {foveation}, {post}, fonts {Fonts.Describe()}");
        }
    }
}
