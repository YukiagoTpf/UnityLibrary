using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;
using static UnityEngine.Rendering.RenderGraphModule.Util.RenderGraphUtils;
#if UNITY_EDITOR
using UnityEditor;
#endif


[Serializable]
// FXAA 全屏后处理 RenderFeature。
public sealed class RenderfeatureFXAA : ScriptableRendererFeature
{
    // 逻辑常量：材质、注入点与输入需求写死，避免 Feature 侧参数膨胀。
    private const string k_MaterialAssetPath = "Assets/YukiLibrary/Rendering/RenderFeature/FXAA/RenderFeature_FXAA.mat";
    private const int k_QualityPassIndex = 0;
    private const int k_ConsolePassIndex = 1;
    private const RenderPassEvent k_InjectionPoint = RenderPassEvent.AfterRenderingPostProcessing;
    private const bool k_FetchColorBuffer = true;
    private const bool k_BindDepthStencilAttachment = false;
    private const ScriptableRenderPassInput k_Requirements = ScriptableRenderPassInput.None;
    private const bool k_RunInSceneView = true;

    // 复用的全屏渲染 Pass 实例。
    private FXAAPass m_Pass;
    // 从固定路径加载的材质。
    [SerializeField, HideInInspector]
    private Material m_Material;
    // 避免重复打印材质缺失警告。
    private bool m_HasLoggedMissingMaterialWarning;

    public override void Create()
    {
        m_Pass ??= new FXAAPass();
        EnsureMaterialLoaded();
    }

    private void EnsureMaterialLoaded()
    {
        if (m_Material != null)
        {
            return;
        }

#if UNITY_EDITOR
        m_Material = AssetDatabase.LoadAssetAtPath<Material>(k_MaterialAssetPath);
#endif

#if UNITY_EDITOR
        if (m_Material != null)
        {
            EditorUtility.SetDirty(this);
        }
#endif
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        // RenderFeature 面板开关关闭时直接跳过。
        if (!isActive)
        {
            return;
        }

        EnsureMaterialLoaded();

        // 材质为空时跳过，并只警告一次。
        if (m_Material == null)
        {
            if (!m_HasLoggedMissingMaterialWarning)
            {
                Debug.LogWarning($"{nameof(RenderfeatureFXAA)} on {name} is skipped because material at '{k_MaterialAssetPath}' is missing.");
                m_HasLoggedMissingMaterialWarning = true;
            }
            return;
        }

        m_HasLoggedMissingMaterialWarning = false;

        ref readonly CameraData cameraData = ref renderingData.cameraData;
        // 跳过预览/反射相机。
        if (cameraData.cameraType == CameraType.Preview || cameraData.cameraType == CameraType.Reflection)
        {
            return;
        }

        // 根据配置决定是否在 SceneView 执行。
        if (!k_RunInSceneView && cameraData.isSceneViewCamera)
        {
            return;
        }

        // 仅当对应 Volume 打开时执行。
        FXAAVolume volume = VolumeManager.instance.stack?.GetComponent<FXAAVolume>();
        if (volume == null || !volume.IsActive())
        {
            return;
        }

        int materialPassIndex = volume.useConsolePath.value ? k_ConsolePassIndex : k_QualityPassIndex;

        // 材质 pass 索引校验。
        if (materialPassIndex < 0 || materialPassIndex >= m_Material.passCount)
        {
            Debug.LogWarning($"{nameof(RenderfeatureFXAA)} on {name} is skipped because pass index {materialPassIndex} is invalid for material {m_Material.name}.");
            return;
        }

        m_Pass.renderPassEvent = k_InjectionPoint;
        m_Pass.ConfigureInput(k_Requirements);
        m_Pass.Setup(
            m_Material,
            materialPassIndex,
            k_FetchColorBuffer,
            k_BindDepthStencilAttachment,
            volume.intensity.value,
            volume.subpix.value,
            volume.edgeThreshold.value,
            volume.edgeThresholdMin.value);
        renderer.EnqueuePass(m_Pass);
    }

    private sealed class FXAAPass : ScriptableRenderPass
    {
        // RenderGraph 中使用的 Pass 名称与中间纹理名。
        private const string k_MainPassName = "RenderfeatureFXAA.Main";
        private const string k_OutputColorName = "_RenderfeatureFXAAOutput";

        // 常用 shader 属性 ID 缓存。
        private static readonly int s_BlitTextureId = Shader.PropertyToID("_BlitTexture");
        private static readonly int s_BlitScaleBiasId = Shader.PropertyToID("_BlitScaleBias");
        private static readonly int s_IntensityId = Shader.PropertyToID("_Intensity");
        private static readonly int s_FxaaSubpixId = Shader.PropertyToID("_FxaaSubpix");
        private static readonly int s_FxaaEdgeThresholdId = Shader.PropertyToID("_FxaaEdgeThreshold");
        private static readonly int s_FxaaEdgeThresholdMinId = Shader.PropertyToID("_FxaaEdgeThresholdMin");
        private static readonly MaterialPropertyBlock s_SharedPropertyBlock = new MaterialPropertyBlock();

        private Material m_Material;
        private int m_MaterialPassIndex;
        private bool m_FetchColorBuffer;
        private bool m_BindDepthStencilAttachment;
        private float m_Intensity;
        private float m_Subpix;
        private float m_EdgeThreshold;
        private float m_EdgeThresholdMin;

        // RenderGraph Pass 数据。
        private sealed class MainPassData
        {
            internal Material material;
            internal int materialPassIndex;
            internal TextureHandle source;
            internal float intensity;
            internal float subpix;
            internal float edgeThreshold;
            internal float edgeThresholdMin;
        }

        internal void Setup(
            Material material,
            int materialPassIndex,
            bool fetchColorBuffer,
            bool bindDepthStencilAttachment,
            float intensity,
            float subpix,
            float edgeThreshold,
            float edgeThresholdMin)
        {
            m_Material = material;
            m_MaterialPassIndex = materialPassIndex;
            m_FetchColorBuffer = fetchColorBuffer;
            m_BindDepthStencilAttachment = bindDepthStencilAttachment;
            m_Intensity = intensity;
            m_Subpix = subpix;
            m_EdgeThreshold = edgeThreshold;
            m_EdgeThresholdMin = edgeThresholdMin;

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

            if (m_FetchColorBuffer)
            {
                // 后缓冲不可直接作为 FXAA 输入纹理读取。
                if (resourceData.isActiveTargetBackBuffer)
                {
                    Debug.LogWarning($"{nameof(RenderfeatureFXAA)} is skipped because active target is back buffer.");
                    return;
                }
            }

            TextureHandle source = resourceData.activeColorTexture;
            TextureDesc destinationDesc = renderGraph.GetTextureDesc(source);
            destinationDesc.name = k_OutputColorName;
            destinationDesc.clearBuffer = false;
            TextureHandle destination = renderGraph.CreateTexture(destinationDesc);

            // 始终使用 Raster Pass，确保每次执行都能绑定当前 Volume 混合后的参数。
            AddRasterPass(
                renderGraph,
                resourceData,
                source,
                destination,
                m_Intensity,
                m_Subpix,
                m_EdgeThreshold,
                m_EdgeThresholdMin);

            // 将 FXAA 输出作为后续渲染阶段的活动颜色目标（ping-pong）。
            resourceData.cameraColor = destination;
        }

        private void AddRasterPass(
            RenderGraph renderGraph,
            UniversalResourceData resourceData,
            TextureHandle source,
            TextureHandle destination,
            float intensity,
            float subpix,
            float edgeThreshold,
            float edgeThresholdMin)
        {
            using var builder = renderGraph.AddRasterRenderPass<MainPassData>(k_MainPassName, out MainPassData passData, profilingSampler);

            passData.material = m_Material;
            passData.materialPassIndex = m_MaterialPassIndex;
            passData.source = source;
            passData.intensity = intensity;
            passData.subpix = subpix;
            passData.edgeThreshold = edgeThreshold;
            passData.edgeThresholdMin = edgeThresholdMin;

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
                ExecuteMainPass(
                    context.cmd,
                    data.source,
                    data.material,
                    data.materialPassIndex,
                    data.intensity,
                    data.subpix,
                    data.edgeThreshold,
                    data.edgeThresholdMin);
            });
        }

        private static void ExecuteMainPass(
            RasterCommandBuffer cmd,
            TextureHandle source,
            Material material,
            int materialPassIndex,
            float intensity,
            float subpix,
            float edgeThreshold,
            float edgeThresholdMin)
        {
            s_SharedPropertyBlock.Clear();

            if (source.IsValid())
            {
                s_SharedPropertyBlock.SetTexture(s_BlitTextureId, source);
            }

            // 保持与 Blit.hlsl 的全屏参数约定兼容。
            s_SharedPropertyBlock.SetVector(s_BlitScaleBiasId, new Vector4(1f, 1f, 0f, 0f));
            s_SharedPropertyBlock.SetFloat(s_IntensityId, intensity);
            s_SharedPropertyBlock.SetFloat(s_FxaaSubpixId, subpix);
            s_SharedPropertyBlock.SetFloat(s_FxaaEdgeThresholdId, edgeThreshold);
            s_SharedPropertyBlock.SetFloat(s_FxaaEdgeThresholdMinId, edgeThresholdMin);

            // 绘制三角形全屏（3 顶点）。
            cmd.DrawProcedural(Matrix4x4.identity, material, materialPassIndex, MeshTopology.Triangles, 3, 1, s_SharedPropertyBlock);
        }
    }
}
