using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;
using static UnityEngine.Rendering.RenderGraphModule.Util.RenderGraphUtils;

[Serializable]
// 全屏渲染模板的 RenderFeature，用于在指定注入点执行材质全屏绘制。
public sealed class RenderfeatureFullScreenTemplate : ScriptableRendererFeature
{
    [Serializable]
    // 可序列化配置项，提供给 Inspector 设置。
    public sealed class Settings
    {
        // 全屏 Pass 使用的材质。
        [Tooltip("The material used by the fullscreen pass.")]
        public Material material;

        // 材质中的 Pass 索引。
        [Tooltip("The material pass index used by the fullscreen pass.")]
        [Min(0)]
        public int materialPassIndex = 0;

        // 渲染注入时机。
        [Tooltip("Injection point for this render pass.")]
        public RenderPassEvent injectionPoint = RenderPassEvent.AfterRenderingPostProcessing;

        // 是否复制当前颜色缓冲作为 _BlitTexture 供采样。
        [Tooltip("Copy active color as _BlitTexture for shader sampling.")]
        public bool fetchColorBuffer = true;

        // 是否将当前深度/模板作为渲染附件绑定。
        [Tooltip("Bind active depth-stencil as render target attachment.")]
        public bool bindDepthStencilAttachment = false;

        // Pass 依赖的相机纹理输入。
        [Tooltip("Additional camera textures required by the pass.")]
        public ScriptableRenderPassInput requirements = ScriptableRenderPassInput.None;

        // 是否在 SceneView 相机执行。
        [Tooltip("Whether this pass runs for Scene View camera.")]
        public bool runInSceneView = true;
    }

    [SerializeField] private Settings settings = new Settings();

    // 复用的全屏渲染 Pass 实例。
    private FullScreenTemplatePass m_Pass;
    // 避免重复打印材质缺失警告。
    private bool m_HasLoggedMissingMaterialWarning;

    public override void Create()
    {
        m_Pass ??= new FullScreenTemplatePass();
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        // 材质为空时跳过，并只警告一次。
        if (settings.material == null)
        {
            if (!m_HasLoggedMissingMaterialWarning)
            {
                Debug.LogWarning($"{nameof(RenderfeatureFullScreenTemplate)} on {name} is skipped because material is null.");
                m_HasLoggedMissingMaterialWarning = true;
            }
            return;
        }

        m_HasLoggedMissingMaterialWarning = false;

        // 材质 pass 索引校验。
        if (settings.materialPassIndex < 0 || settings.materialPassIndex >= settings.material.passCount)
        {
            Debug.LogWarning($"{nameof(RenderfeatureFullScreenTemplate)} on {name} is skipped because pass index {settings.materialPassIndex} is invalid for material {settings.material.name}.");
            return;
        }

        ref readonly CameraData cameraData = ref renderingData.cameraData;
        // 跳过预览/反射相机。
        if (cameraData.cameraType == CameraType.Preview || cameraData.cameraType == CameraType.Reflection)
        {
            return;
        }

        // 根据配置决定是否在 SceneView 执行。
        if (!settings.runInSceneView && cameraData.isSceneViewCamera)
        {
            return;
        }

        m_Pass.renderPassEvent = settings.injectionPoint;
        m_Pass.ConfigureInput(settings.requirements);
        m_Pass.Setup(settings.material, settings.materialPassIndex, settings.fetchColorBuffer, settings.bindDepthStencilAttachment);
        renderer.EnqueuePass(m_Pass);
    }

    private sealed class FullScreenTemplatePass : ScriptableRenderPass
    {
        // RenderGraph 中使用的 Pass 名称与中间纹理名。
        private const string k_CopyPassName = "RenderfeatureFullScreenTemplate.CopyColor";
        private const string k_MainPassName = "RenderfeatureFullScreenTemplate.Main";
        private const string k_CopyColorName = "_RenderfeatureFullScreenTemplateColorCopy";

        // 常用 shader 属性 ID 缓存。
        private static readonly int s_BlitTextureId = Shader.PropertyToID("_BlitTexture");
        private static readonly int s_BlitScaleBiasId = Shader.PropertyToID("_BlitScaleBias");
        private static readonly MaterialPropertyBlock s_SharedPropertyBlock = new MaterialPropertyBlock();

        private Material m_Material;
        private int m_MaterialPassIndex;
        private bool m_FetchColorBuffer;
        private bool m_BindDepthStencilAttachment;

        // RenderGraph Pass 数据。
        private sealed class MainPassData
        {
            internal Material material;
            internal int materialPassIndex;
            internal TextureHandle source;
        }

        internal void Setup(Material material, int materialPassIndex, bool fetchColorBuffer, bool bindDepthStencilAttachment)
        {
            m_Material = material;
            m_MaterialPassIndex = materialPassIndex;
            m_FetchColorBuffer = fetchColorBuffer;
            m_BindDepthStencilAttachment = bindDepthStencilAttachment;

            // 当需要读取 activeColorTexture 时，必须启用中间纹理。
            requiresIntermediateTexture = m_FetchColorBuffer;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (m_Material == null)
            {
                return;
            }

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

            TextureHandle destination = resourceData.activeColorTexture;
            TextureHandle source = TextureHandle.nullHandle;

            if (m_FetchColorBuffer)
            {
                // 后缓冲不可直接作为源纹理读取。
                if (resourceData.isActiveTargetBackBuffer)
                {
                    Debug.LogWarning($"{nameof(RenderfeatureFullScreenTemplate)} is skipped because active target is back buffer.");
                    return;
                }

                source = resourceData.activeColorTexture;
                TextureDesc colorDesc = renderGraph.GetTextureDesc(source);
                colorDesc.name = k_CopyColorName;
                colorDesc.clearBuffer = false;

                // 拷贝颜色缓冲，提供给 shader 采样。
                TextureHandle copiedColor = renderGraph.CreateTexture(colorDesc);
                renderGraph.AddCopyPass(source, copiedColor, k_CopyPassName);
                source = copiedColor;
            }

            // 需要附加输入或深度时，用 Raster Pass，便于绑定附件。
            bool useRasterPass = input != ScriptableRenderPassInput.None || m_BindDepthStencilAttachment;
            if (useRasterPass)
            {
                AddRasterPass(renderGraph, resourceData, source, destination);
                return;
            }

            // 否则直接用 Blit Pass。
            BlitMaterialParameters blitParameters = new(source, destination, m_Material, m_MaterialPassIndex);
            renderGraph.AddBlitPass(blitParameters, k_MainPassName);
        }

        private void AddRasterPass(
            RenderGraph renderGraph,
            UniversalResourceData resourceData,
            TextureHandle source,
            TextureHandle destination)
        {
            using var builder = renderGraph.AddRasterRenderPass<MainPassData>(k_MainPassName, out MainPassData passData, profilingSampler);

            passData.material = m_Material;
            passData.materialPassIndex = m_MaterialPassIndex;
            passData.source = source;

            if (passData.source.IsValid())
            {
                // 读取源纹理（通常为复制后的颜色）。
                builder.UseTexture(passData.source, AccessFlags.Read);
            }

            // 根据 ScriptableRenderPassInput 绑定需要的相机纹理。
            bool needsColor = (input & ScriptableRenderPassInput.Color) != ScriptableRenderPassInput.None;
            bool needsDepth = (input & ScriptableRenderPassInput.Depth) != ScriptableRenderPassInput.None;
            bool needsNormal = (input & ScriptableRenderPassInput.Normal) != ScriptableRenderPassInput.None;
            bool needsMotion = (input & ScriptableRenderPassInput.Motion) != ScriptableRenderPassInput.None;

            if (needsColor && resourceData.cameraOpaqueTexture.IsValid())
            {
                builder.UseTexture(resourceData.cameraOpaqueTexture);
            }

            if (needsDepth && resourceData.cameraDepthTexture.IsValid())
            {
                builder.UseTexture(resourceData.cameraDepthTexture);
            }

            if (needsNormal && resourceData.cameraNormalsTexture.IsValid())
            {
                builder.UseTexture(resourceData.cameraNormalsTexture);
            }

            if (needsMotion)
            {
                if (resourceData.motionVectorColor.IsValid())
                {
                    builder.UseTexture(resourceData.motionVectorColor);
                }

                if (resourceData.motionVectorDepth.IsValid())
                {
                    builder.UseTexture(resourceData.motionVectorDepth);
                }
            }

            builder.SetRenderAttachment(destination, 0, AccessFlags.Write);

            if (m_BindDepthStencilAttachment && resourceData.activeDepthTexture.IsValid())
            {
                // 绑定深度/模板附件，供深度测试或写入。
                builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Write);
            }

            builder.SetRenderFunc(static (MainPassData data, RasterGraphContext context) =>
            {
                ExecuteMainPass(context.cmd, data.source, data.material, data.materialPassIndex);
            });
        }

        private static void ExecuteMainPass(RasterCommandBuffer cmd, TextureHandle source, Material material, int materialPassIndex)
        {
            s_SharedPropertyBlock.Clear();

            if (source.IsValid())
            {
                s_SharedPropertyBlock.SetTexture(s_BlitTextureId, source);
            }

            // 保持与 Blit.hlsl 的全屏参数约定兼容。
            s_SharedPropertyBlock.SetVector(s_BlitScaleBiasId, new Vector4(1f, 1f, 0f, 0f));

            // 绘制三角形全屏（3 顶点）。
            cmd.DrawProcedural(Matrix4x4.identity, material, materialPassIndex, MeshTopology.Triangles, 3, 1, s_SharedPropertyBlock);
        }
    }
}

