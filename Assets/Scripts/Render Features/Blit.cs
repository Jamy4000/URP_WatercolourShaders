using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

/*
 * Blit Renderer Feature                                                https://github.com/Cyanilux/URP_BlitRenderFeature
 * ------------------------------------------------------------------------------------------------------------------------
 * Based on the Blit from the UniversalRenderingExamples
 * https://github.com/Unity-Technologies/UniversalRenderingExamples/tree/master/Assets/Scripts/Runtime/RenderPasses
 *
 * Extended to allow for :
 * - Specific access to selecting a source and destination (via current camera's color / texture id / render texture object
 * - Automatic switching to using _AfterPostProcessTexture for After Rendering event, in order to correctly handle the blit after post processing is applied
 * - Setting a _InverseView matrix (cameraToWorldMatrix), for shaders that might need it to handle calculations from screen space to world.
 *     e.g. reconstruct world pos from depth : https://twitter.com/Cyanilux/status/1269353975058501636
 * ------------------------------------------------------------------------------------------------------------------------
 * @Cyanilux
*/
public class Blit : ScriptableRendererFeature {

    public class BlitPass : ScriptableRenderPass {

        public Material blitMaterial = null;
        public FilterMode filterMode { get; set; }

        private BlitSettings settings;

        private RenderTargetIdentifier source { get; set; }
        private RenderTargetIdentifier destination { get; set; }

        int m_TemporaryColorTexture;
        int m_DestinationTexture;
        string m_ProfilerTag;

        public BlitPass(RenderPassEvent renderPassEvent, BlitSettings settings, string tag) {
            this.renderPassEvent = renderPassEvent;
            this.settings = settings;
            blitMaterial = settings.blitMaterial;
            m_ProfilerTag = tag;
            m_TemporaryColorTexture = Shader.PropertyToID("_TemporaryColorTexture");
            if (settings.dstType == Target.TextureID) {
                m_DestinationTexture = Shader.PropertyToID(settings.dstTextureId);
            }
        }

        public void Setup(RenderTargetIdentifier source, RenderTargetIdentifier destination) {
            this.source = source;
            this.destination = destination;
        }

        private class PassData {
            public TextureHandle source;
            public TextureHandle destination;
            public Material material;
            public int passIndex;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) {
            var resourceData = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();

            if (settings.setInverseViewMatrix)
                Shader.SetGlobalMatrix("_InverseView", cameraData.camera.cameraToWorldMatrix);

            TextureHandle activeColor = resourceData.activeColorTexture;

            var texDesc = renderGraph.GetTextureDesc(resourceData.cameraColor);
            texDesc.name = "_TemporaryColorTexture";
            texDesc.clearBuffer = false;
            TextureHandle colorCopy = renderGraph.CreateTexture(texDesc);
            renderGraph.AddBlitPass(activeColor, colorCopy, Vector2.one, Vector2.zero, passName: "WatercolorBlit Copy Color");

            using (var builder = renderGraph.AddUnsafePass<PassData>(m_ProfilerTag, out var passData)) {
                passData.source = colorCopy;
                passData.destination = activeColor;
                passData.material = blitMaterial;
                passData.passIndex = settings.blitMaterialPassIndex;

                builder.UseTexture(colorCopy, AccessFlags.Read);
                builder.UseTexture(activeColor, AccessFlags.Write);

                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => {
                    CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    // cmd.Blit handles matrix setup and _MainTex binding — matches Execute() path behavior
                    cmd.Blit((RenderTargetIdentifier)data.source, (RenderTargetIdentifier)data.destination, data.material, data.passIndex);
                });
            }
        }

#pragma warning disable CS0672, CS0618
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
            CommandBuffer cmd = CommandBufferPool.Get(m_ProfilerTag);

            RenderTextureDescriptor opaqueDesc = renderingData.cameraData.cameraTargetDescriptor;
            opaqueDesc.depthBufferBits = 0;

            if (settings.setInverseViewMatrix) {
                Shader.SetGlobalMatrix("_InverseView", renderingData.cameraData.camera.cameraToWorldMatrix);
            }

            if (settings.dstType == Target.TextureID) {
                cmd.GetTemporaryRT(m_DestinationTexture, opaqueDesc, filterMode);
            }

            //Debug.Log($"src = {source},     dst = {destination} ");
            // Can't read and write to same color target, use a TemporaryRT
            if (source == destination || (settings.srcType == settings.dstType && settings.srcType == Target.CameraColor)) {
                cmd.GetTemporaryRT(m_TemporaryColorTexture, opaqueDesc, filterMode);
                cmd.Blit(source, new RenderTargetIdentifier(m_TemporaryColorTexture), blitMaterial, settings.blitMaterialPassIndex);
                cmd.Blit(new RenderTargetIdentifier(m_TemporaryColorTexture), destination);
            } else {
                cmd.Blit(source, destination, blitMaterial, settings.blitMaterialPassIndex);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
#pragma warning restore CS0672, CS0618

        public override void FrameCleanup(CommandBuffer cmd) {
            if (settings.dstType == Target.TextureID) {
                cmd.ReleaseTemporaryRT(m_DestinationTexture);
            }
            if (source == destination || (settings.srcType == settings.dstType && settings.srcType == Target.CameraColor)) {
                cmd.ReleaseTemporaryRT(m_TemporaryColorTexture);
            }
        }
    }

    [System.Serializable]
    public class BlitSettings {
        public RenderPassEvent Event = RenderPassEvent.AfterRenderingOpaques;

        public Material blitMaterial = null;
        public int blitMaterialPassIndex = 0;
        public bool setInverseViewMatrix = false;

        public Target srcType = Target.CameraColor;
        public string srcTextureId = "_CameraColorTexture";
        public RenderTexture srcTextureObject;

        public Target dstType = Target.CameraColor;
        public string dstTextureId = "_BlitPassTexture";
        public RenderTexture dstTextureObject;
    }

    public enum Target {
        CameraColor,
        TextureID,
        RenderTextureObject
    }

    public BlitSettings settings = new BlitSettings();

    BlitPass blitPass;

    private RenderTargetIdentifier srcIdentifier, dstIdentifier;

    public override void Create() {
        var passIndex = settings.blitMaterial != null ? settings.blitMaterial.passCount - 1 : 1;
        settings.blitMaterialPassIndex = Mathf.Clamp(settings.blitMaterialPassIndex, -1, passIndex);
        blitPass = new BlitPass(settings.Event, settings, name);

        if (settings.Event == RenderPassEvent.AfterRenderingPostProcessing) {
            Debug.LogWarning("Note that the \"After Rendering Post Processing\"'s Color target doesn't seem to work? (or might work, but doesn't contain the post processing) :( -- Use \"After Rendering\" instead!");
        }

        UpdateSrcIdentifier();
        UpdateDstIdentifier();
    }

    private void UpdateSrcIdentifier() {
        srcIdentifier = UpdateIdentifier(settings.srcType, settings.srcTextureId, settings.srcTextureObject);
    }

    private void UpdateDstIdentifier() {
        dstIdentifier = UpdateIdentifier(settings.dstType, settings.dstTextureId, settings.dstTextureObject);
    }

    private RenderTargetIdentifier UpdateIdentifier(Target type, string s, RenderTexture obj) {
        if (type == Target.RenderTextureObject) {
            return obj;
        } else if (type == Target.TextureID) {
            return s;
        }
        return new RenderTargetIdentifier();
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {

        if (settings.blitMaterial == null) {
            Debug.LogWarningFormat("Missing Blit Material. {0} blit pass will not execute. Check for missing reference in the assigned renderer.", GetType().Name);
            return;
        }

        if (settings.Event == RenderPassEvent.AfterRenderingPostProcessing) {
        } else if (settings.Event == RenderPassEvent.AfterRendering && renderingData.postProcessingEnabled) {
            // If event is AfterRendering, and src/dst is using CameraColor, switch to _AfterPostProcessTexture instead.
            if (settings.srcType == Target.CameraColor) {
                settings.srcType = Target.TextureID;
                settings.srcTextureId = "_AfterPostProcessTexture";
                UpdateSrcIdentifier();
            }
            if (settings.dstType == Target.CameraColor) {
                settings.dstType = Target.TextureID;
                settings.dstTextureId = "_AfterPostProcessTexture";
                UpdateDstIdentifier();
            }
        } else {
            // If src/dst is using _AfterPostProcessTexture, switch back to CameraColor
            if (settings.srcType == Target.TextureID && settings.srcTextureId == "_AfterPostProcessTexture") {
                settings.srcType = Target.CameraColor;
                settings.srcTextureId = "";
                UpdateSrcIdentifier();
            }
            if (settings.dstType == Target.TextureID && settings.dstTextureId == "_AfterPostProcessTexture") {
                settings.dstType = Target.CameraColor;
                settings.dstTextureId = "";
                UpdateDstIdentifier();
            }
        }

#pragma warning disable CS0618
        var src = (settings.srcType == Target.CameraColor) ? (RenderTargetIdentifier)renderer.cameraColorTargetHandle : srcIdentifier;
        var dest = (settings.dstType == Target.CameraColor) ? (RenderTargetIdentifier)renderer.cameraColorTargetHandle : dstIdentifier;
#pragma warning restore CS0618

        blitPass.Setup(src, dest);
        renderer.EnqueuePass(blitPass);
    }
}