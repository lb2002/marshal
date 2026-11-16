using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace Lotse.EditorTools
{
    /// <summary>
    /// The project's Android quality level uses Mobile_RPAsset, whose renderer (Mobile_Renderer) has URP's Tile-Only
    /// Mode on. That mode is meant for the headset's tiled GPU; for these offscreen renders on the desktop GPU the
    /// renders swap in an in-memory copy of the same pipeline asset whose renderer copy has it off. Every other
    /// setting (MSAA, HDR off, render scale, the on-tile post-processing feature) stays the device's. Nothing is
    /// written to disk: the copies are not assets, and the original pipeline is put back afterwards.
    /// </summary>
    static class OffscreenUrp
    {
        public static string Describe()
        {
            var rp = GraphicsSettings.currentRenderPipeline;
            if (!(rp is UniversalRenderPipelineAsset urp)) return "render pipeline: " + (rp != null ? rp.name : "built-in");
            var data = urp.rendererDataList.Length > 0 ? urp.rendererDataList[0] as UniversalRendererData : null;
            return $"render pipeline: {urp.name}, renderer {(data != null ? data.name : "?")}, tile-only {(data != null && data.tileOnlyMode)}, " +
                   $"MSAA {urp.msaaSampleCount}, HDR {urp.supportsHDR}, graphics {SystemInfo.graphicsDeviceType}";
        }

        /// <summary>Turns Tile-Only Mode off for this session; returns what puts the original back (or null: nothing changed).</summary>
        public static Action TileOnlyOff()
        {
            var fromQuality = QualitySettings.renderPipeline;
            var original = fromQuality != null ? fromQuality : GraphicsSettings.defaultRenderPipeline;
            if (!(original is UniversalRenderPipelineAsset urp)) return null;
            var data = urp.rendererDataList.Length > 0 ? urp.rendererDataList[0] as UniversalRendererData : null;
            if (data == null || !data.tileOnlyMode) return null;

            var dataCopy = Object.Instantiate(data);
            dataCopy.name = data.name + " (offscreen)";
            dataCopy.hideFlags = HideFlags.DontSave;
            dataCopy.tileOnlyMode = false;
            var copy = Object.Instantiate(urp);
            copy.name = urp.name + " (offscreen)";
            copy.hideFlags = HideFlags.DontSave;
            var list = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", BindingFlags.Instance | BindingFlags.NonPublic);
            if (list == null) throw new InvalidOperationException("UniversalRenderPipelineAsset.m_RendererDataList not found");
            var renderers = (ScriptableRendererData[])((ScriptableRendererData[])list.GetValue(copy)).Clone();
            renderers[0] = dataCopy;
            list.SetValue(copy, renderers);

            if (fromQuality != null) QualitySettings.renderPipeline = copy;
            else GraphicsSettings.defaultRenderPipeline = copy;
            return () =>
            {
                if (fromQuality != null) QualitySettings.renderPipeline = fromQuality;
                else GraphicsSettings.defaultRenderPipeline = original;
                Object.DestroyImmediate(copy);
                Object.DestroyImmediate(dataCopy);
            };
        }
    }
}
