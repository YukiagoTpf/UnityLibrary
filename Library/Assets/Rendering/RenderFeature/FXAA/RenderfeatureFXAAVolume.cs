using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[Serializable]
[VolumeComponentMenu("Post-processing Custom/FXAA")]
[VolumeRequiresRendererFeatures(typeof(RenderfeatureFXAA))]
[SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
[DisplayInfo(name = "FXAA")]
public sealed class FXAAVolume : VolumeComponent, IPostProcessComponent
{
    [Tooltip("Enable FXAA render feature pass.")]
    public BoolParameter enable = new BoolParameter(false);

    [Tooltip("Final blend amount between source color and FXAA output.")]
    public ClampedFloatParameter intensity = new ClampedFloatParameter(1f, 0f, 1f);

    [Tooltip("Sub-pixel aliasing removal amount.")]
    public ClampedFloatParameter subpix = new ClampedFloatParameter(0.65f, 0f, 1f);

    [Tooltip("Relative local contrast threshold.")]
    public ClampedFloatParameter edgeThreshold = new ClampedFloatParameter(0.15f, 0.063f, 0.333f);

    [Tooltip("Absolute local contrast threshold.")]
    public ClampedFloatParameter edgeThresholdMin = new ClampedFloatParameter(0.03f, 0f, 0.0833f);

    [Tooltip("Use console-style FXAA path instead of quality path.")]
    public BoolParameter useConsolePath = new BoolParameter(false);

    public bool IsActive()
    {
        return enable.value && intensity.value > 0f;
    }

    public bool IsTileCompatible() => false;
}
