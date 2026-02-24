# URP SSAO 技术说明

## TL;DR
- `SSAO`（Screen Space Ambient Occlusion）用于补充局部接触阴影，提升接触区和凹陷区域的层次感。
- URP 通过 `ScreenSpaceAmbientOcclusion` RendererFeature 注入 SSAO Pass，执行 AO 估计与滤波后输出结果。
- 该方案支持可调成本（`Samples`、`BlurQuality`、`Downsample`），但受限于屏幕空间可见信息。

## 术语说明
- `AO`：Ambient Occlusion，描述环境遮蔽导致的局部变暗。
- `SSAO`：在屏幕空间计算 AO，依赖当前帧深度/法线。
- `Falloff`：AO 有效距离上限。
- `AfterOpaque`：在不透明物体之后直接混合 AO 到颜色目标。
- `Downsample`：低分辨率计算 AO 后再上采样。

## 1. 模块职责
URP SSAO 模块主要负责：
1. 管理运行时配置与依赖资源（Shader、Material、Blue Noise）。
2. 在相机渲染流程中插入 SSAO Pass。
3. 输出 AO 结果供光照系统采样，或直接混合回颜色目标。

核心代码位置：
- Feature：`.../Runtime/RendererFeatures/ScreenSpaceAmbientOcclusion.cs`
- Pass：`.../Runtime/Passes/ScreenSpaceAmbientOcclusionPass.cs`
- Shader：`.../Shaders/Utils/ScreenSpaceAmbientOcclusion.shader`
- 算法：`.../ShaderLibrary/SSAO.hlsl`
- 光照采样：`.../ShaderLibrary/AmbientOcclusion.hlsl`

## 2. 执行流程
1. `AddRenderPasses()` 判断当前相机是否执行 SSAO。
2. `TryPrepareResources()` 检查资源是否齐全。
3. `Setup()` 配置输入（`Depth`/`DepthNormals`）、执行时机和滤波质量。
4. `RecordRenderGraph()` 执行 AO 估计、滤波与结果输出。

流程概括：
`资源准备 -> Pass 配置 -> AO 估计 -> 滤波 -> 输出`

## 3. 输出模式
### 3.1 全局 AO 纹理模式
- 条件：`AfterOpaque = false`
- 输出：`_ScreenSpaceOcclusionTexture` + `_SCREEN_SPACE_OCCLUSION`
- 用途：URP Lit 路径在光照阶段统一采样 AO。

### 3.2 直接混合模式
- 条件：`AfterOpaque = true`
- 输出：AfterOpaque Pass 直接混合回颜色目标。

## 4. 关键配置项
- `Source`
  - `DepthNormals`：法线直接可用，稳定性更高。
  - `Depth`：需要重建法线，边缘稳定性相对较弱。
- `Samples`（Low/Medium/High）：影响采样稳定性与成本。
- `BlurQuality`（Low/Medium/High）：影响噪点控制与成本。
- `Downsample`：降低成本，但可能损失边缘质量。

## 5. 距离与天空像素处理
以下像素会跳过 AO 计算：
- 天空像素（无有效几何深度）。
- 距离超过 `Falloff` 的像素。

目的：减少无效计算并降低远距离噪点。
实现位置：`.../SSAO.hlsl:354`、`.../SSAO.hlsl:360`

## 6. 实现细节
### 6.1 遮蔽判定的法线来源
- `DepthNormals` 路径：`SampleSceneNormals` 直接取法线（`.../SSAO.hlsl:335`，`.../ScreenSpaceAmbientOcclusion.shader:38`）。
- `Depth` 路径：
  - 先用深度重建视空间位置 `ReconstructViewPos`（`.../SSAO.hlsl:240`）。
  - 再用 `ReconstructNormal` 从邻域深度重建法线（`.../SSAO.hlsl:276`）。

`Depth` 法线重建分级：
- Low：`ddx/ddy` 叉积（最快，抗噪最弱）。
- Medium：3-tap 邻域择优。
- High：5-tap 邻域择优，边缘鲁棒性更好（`.../SSAO.hlsl:286` 到 `.../SSAO.hlsl:313`）。

### 6.2 样本分布与半球约束
采样方向由 `PickSamplePoint` 生成（`.../SSAO.hlsl:185`）：
- `_BLUE_NOISE`：蓝噪声扰动，降低规则噪声（`.../SSAO.hlsl:189`）。
- `_INTERLEAVED_GRADIENT`：交错梯度噪声（`.../SSAO.hlsl:199`）。

样本向量会做半球约束并乘 `RADIUS`，再按像素密度缩放（`.../SSAO.hlsl:194` 到 `.../SSAO.hlsl:215`）。

### 6.3 遮蔽强度计算
核心循环在 `SSAO()`（`.../SSAO.hlsl:348` 到 `.../SSAO.hlsl:433`）。

单样本贡献可抽象为：
```text
inside = |zDist - linearDepth_s| < RADIUS && sampleNotSky
a1 = max(dot(v_s2, normal_o) - beta * depth_o, 0)
a2 = dot(v_s2, v_s2) + epsilon
ao += a1 / a2 * inside
```

关键项：
- `beta`（`kBeta`）抑制自阴影噪声（`.../SSAO.hlsl:118`，`.../SSAO.hlsl:430`）。
- `epsilon`（`kEpsilon`）避免除零与过大增益（`.../SSAO.hlsl:119`，`.../SSAO.hlsl:432`）。

最终映射：
```text
ao = PositivePow(saturate(ao * INTENSITY * falloff * 1/sampleCount), kContrast)
```
对应：`.../SSAO.hlsl:444`。

### 6.4 AO 与法线打包
`PackAONormal()` 将 AO 与法线一起写入 `half4`（`.../SSAO.hlsl:142`）。
后续双边滤波可直接复用法线，减少额外采样依赖（`.../SSAO.hlsl:149`）。

## 7. 高频信息处理与稳定性
### 7.1 采样噪声抑制
- 通过蓝噪声或交错梯度噪声打散采样分布（`.../SSAO.hlsl:189`，`.../SSAO.hlsl:199`）。
- `Samples` 切换 4/8/12 样本（`.../SSAO.hlsl:42`，`.../SSAO.hlsl:44`，`.../SSAO.hlsl:46`）。

### 7.2 几何感知双边滤波（High）
- `CompareNormal = smoothstep(kGeometryCoeff, 1, dot(n0, n1))`（`.../SSAO.hlsl:168`，`.../SSAO.hlsl:170`）。
- 法线差异大时降低权重，保护几何边界。
- 具体核权重见 `Blur()`（`.../SSAO.hlsl:456` 到 `.../SSAO.hlsl:480`）。

### 7.3 Gaussian/Kawase 质量分支
- Medium：Gaussian 分离卷积（`.../SSAO.hlsl:543` 到 `.../SSAO.hlsl:590`）。
- Low：Kawase 四点近似（`.../SSAO.hlsl:604` 到 `.../SSAO.hlsl:648`）。
- 稳定性一般为：Bilateral > Gaussian > Kawase；性能通常反向。

### 7.4 Downsample 的深度采样对齐
`ADJUSTED_DEPTH_UV` 在降采样时将深度采样对齐到 texel 中心（`.../SSAO.hlsl:217`，`.../SSAO.hlsl:221`），降低上采样时错位抖动。

### 7.5 Pass 侧关键字与参数稳定策略
- `SSAOMaterialParams` 将设置映射到关键字（`.../ScreenSpaceAmbientOcclusionPass.cs:89`）。
- `m_SSAOParamsPrev` 缓存前帧参数，减少重复关键字切换（`.../ScreenSpaceAmbientOcclusionPass.cs:281` 到 `.../ScreenSpaceAmbientOcclusionPass.cs:297`）。

## 8. 局限性与可能问题
- 仅依赖屏幕内可见信息，无法表达屏幕外遮挡。
- `Depth` 重建法线在高反差边缘可能不稳定。
- 大半径 + 低采样可能引入脏边与闪烁。

## 9. 接入建议
1. 在 URP Renderer Data 中启用 `ScreenSpaceAmbientOcclusion` Feature。
2. 初始建议：`Source=DepthNormals`，`Samples=Medium`，`BlurQuality=Medium`。
3. 性能不足时优先启用 `Downsample` 或降低 `Samples`。
4. 使用开关对比和 GPU profiling 验证收益与成本。

## 10. 问题排查
- 无效果：
  - 确认 Feature 启用。
  - 确认 `Intensity`、`Radius`、`Falloff` > 0。
  - 确认 Shader/Material/Blue Noise 资源完整。
- 效果偏脏：
  - 提高 `Samples` 或 `BlurQuality`。
  - 减小 `Radius`。
  - 优先使用 `DepthNormals`。
- 性能超预算：
  - 启用 `Downsample`。
  - 降低 `Samples`。
  - 降低 `BlurQuality`。

## 11. 关键代码证据
- 默认参数：`.../ScreenSpaceAmbientOcclusion.cs:12-22`
- Feature 生命周期：`.../ScreenSpaceAmbientOcclusion.cs:143`、`.../ScreenSpaceAmbientOcclusion.cs:166`
- 资源准备：`.../ScreenSpaceAmbientOcclusion.cs:187-217`
- 输入与事件：`.../ScreenSpaceAmbientOcclusionPass.cs:151-197`
- RenderGraph 主流程：`.../ScreenSpaceAmbientOcclusionPass.cs:349-458`
- AO 与滤波实现：`.../SSAO.hlsl:348`、`.../SSAO.hlsl:456`、`.../SSAO.hlsl:543`、`.../SSAO.hlsl:604`
- 光照侧读取：`.../AmbientOcclusion.hlsl:25-31`

## 12. 版本信息
- URP 包版本：`com.unity.render-pipelines.universal 17.3.0`
- Unity 版本：`6000.3`
- 代码根路径：`F:/Project/UnityProject/Library/Packages/com.unity.render-pipelines.universal@1e87cf1dccb8`

