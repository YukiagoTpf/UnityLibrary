
## TL;DR
- 目标：把主方向光（Main Light）的实时阴影先解析到屏幕空间纹理，再让不透明物体在后续光照阶段直接按屏幕坐标读取阴影衰减。
- 核心思路：新增一个全屏 Pass 输出 `_ScreenSpaceShadowmapTexture`，并在该 Pass 后切换全局关键词到 `_MAIN_LIGHT_SHADOWS_SCREEN`；到透明阶段前再切回普通主光阴影关键词。
- 主要收益：减少不透明阶段逐像素的主光阴影坐标/级联选择开销，统一屏幕空间复用；代价是新增一次全屏阴影解析 Pass 和一张屏幕分辨率纹理。

## 术语说明
- 屏幕空间阴影（Screen Space Shadows）：先把阴影结果写入屏幕分辨率纹理，再在后续着色阶段采样这张纹理。
- 主光阴影关键词：`_MAIN_LIGHT_SHADOWS`、`_MAIN_LIGHT_SHADOWS_CASCADE`、`_MAIN_LIGHT_SHADOWS_SCREEN`，用于控制 shader 分支。

## 1. 文档范围
- 模块路径：`Packages/com.unity.render-pipelines.universal@1e87cf1dccb8/Runtime/RendererFeatures/ScreenSpaceShadows.cs`
- 包含内容：RendererFeature 触发条件、RenderGraph Pass 行为、shader 计算路径、关键词切换策略、shader 变体裁剪链路。
- 不包含内容：主光阴影贴图（ShadowMap）生成流程、Additional Lights 阴影流程、具体项目场景调优。
- 读者对象：程序、图形程序、技术美术。
- 基线版本：URP `17.3.0`（Unity `6000.3`）。证据：`Packages/com.unity.render-pipelines.universal@1e87cf1dccb8/package.json`。

## 2. 问题与目标
- 问题现象：默认主光阴影路径在材质 shader 中常需执行世界坐标到阴影坐标变换与级联判断。
- 根因判断：主光阴影采样逻辑分散在着色阶段，且每个像素都要做阴影坐标准备（尤其是级联阴影）。
- 目标指标：
  1. 在不透明阶段改用 `_ScreenSpaceShadowmapTexture` 直接采样主光阴影衰减。
  2. 与 URP 现有主光阴影关键词体系兼容（屏幕空间与普通阴影路径可切换）。
- 非目标：
  1. 不提供额外 UI 参数（当前设置类为空）。
  2. 不改变透明物体主光阴影策略。

## 3. 方案概览
### 3.1 核心思路
先执行一次全屏 `ScreenSpaceShadows` Pass，把每个屏幕像素对应的主光阴影衰减写入 `_ScreenSpaceShadowmapTexture`，随后通过全局关键词让不透明阶段走 `_MAIN_LIGHT_SHADOWS_SCREEN` 分支。

### 3.2 关键改动点
- `ScreenSpaceShadowsPass` 创建屏幕空间阴影纹理并全局暴露：`_ScreenSpaceShadowmapTexture`。证据：`Runtime/RendererFeatures/ScreenSpaceShadows.cs:206`、`Runtime/RendererFeatures/ScreenSpaceShadows.cs:223`。
- Pass 执行后立即切换关键词：关闭普通主光阴影关键词，打开屏幕空间关键词。证据：`Runtime/RendererFeatures/ScreenSpaceShadows.cs:245`-`Runtime/RendererFeatures/ScreenSpaceShadows.cs:247`。
- 在透明阶段前用 `ScreenSpaceShadowsPostPass` 回切关键词，避免透明物体误走屏幕空间分支。证据：`Runtime/RendererFeatures/ScreenSpaceShadows.cs:300`-`Runtime/RendererFeatures/ScreenSpaceShadows.cs:305`。

### 3.3 适用与不适用
- 适用：有主方向光实时阴影、希望在不透明阶段复用屏幕空间阴影结果的项目。
- 不适用：只输出离屏深度（无颜色目标）的相机，或无主光阴影的相机。

## 4. 架构与边界
- 上游调用方：URP `ScriptableRenderer` 在 `AddRenderPasses` 阶段调度 Feature。证据：`Runtime/RendererFeatures/ScreenSpaceShadows.cs:50`。
- 下游依赖：
  1. `Shaders/Utils/ScreenSpaceShadows.shader`（全屏阴影解析 shader）。
  2. `ShaderLibrary/Shadows.hlsl`（主光阴影采样逻辑与屏幕空间分支）。
  3. 全局关键词定义与注册：`Runtime/UniversalRenderPipelineCore.cs:956`-`Runtime/UniversalRenderPipelineCore.cs:958`、`Runtime/UniversalRenderPipelineCore.cs:1160`-`Runtime/UniversalRenderPipelineCore.cs:1166`。
- 边界定义：
  1. 本模块负责“主光阴影从 ShadowMap 解析为屏幕纹理 + 阶段性关键词切换”。
  2. 不负责主光 ShadowMap 本身的生成，也不负责 Additional Lights 阴影。

## 5. 核心流程
### 5.1 主流程（端到端）
1. `AddRenderPasses` 做前置校验：
   - 相机若为离屏深度纹理则直接返回。证据：`Runtime/RendererFeatures/ScreenSpaceShadows.cs:52`；条件定义见 `Runtime/UniversalRenderer.cs:688`。
   - 材质加载失败则报错并跳过。证据：`Runtime/RendererFeatures/ScreenSpaceShadows.cs:55`-`Runtime/RendererFeatures/ScreenSpaceShadows.cs:60`。
2. 判断是否允许主光阴影（支持主光阴影且 `mainLightIndex != -1`），满足则入队两个 Pass。证据：`Runtime/RendererFeatures/ScreenSpaceShadows.cs:63`-`Runtime/RendererFeatures/ScreenSpaceShadows.cs:76`。
3. `ScreenSpaceShadowsPass`（RenderGraph）创建 `_ScreenSpaceShadowmapTexture`，执行全屏 blit 写入阴影衰减，并设置全局关键词到 `_MAIN_LIGHT_SHADOWS_SCREEN`。证据：`Runtime/RendererFeatures/ScreenSpaceShadows.cs:206`、`Runtime/RendererFeatures/ScreenSpaceShadows.cs:244`-`Runtime/RendererFeatures/ScreenSpaceShadows.cs:247`。
4. `ScreenSpaceShadowsPostPass` 在透明阶段前回切关键词，恢复 `_MAIN_LIGHT_SHADOWS` 或 `_MAIN_LIGHT_SHADOWS_CASCADE`。证据：`Runtime/RendererFeatures/ScreenSpaceShadows.cs:295`-`Runtime/RendererFeatures/ScreenSpaceShadows.cs:305`。

### 5.2 分支流程与条件
- `renderer.usesDeferredLighting == true` -> 阴影 Pass 放在 `BeforeRenderingGbuffer`。证据：`Runtime/RendererFeatures/ScreenSpaceShadows.cs:68`-`Runtime/RendererFeatures/ScreenSpaceShadows.cs:72`。
- 否则（Forward）-> Pass 放在 `AfterRenderingPrePasses + 1`，确保晚于深度预拷贝。证据：`Runtime/RendererFeatures/ScreenSpaceShadows.cs:72`。
- RenderGraph 使用 `AddUnsafePass`，避免与其他 Pass 合并引发 Deferred 输入附件/依赖问题。证据：`Runtime/RendererFeatures/ScreenSpaceShadows.cs:208`-`Runtime/RendererFeatures/ScreenSpaceShadows.cs:214`。

## 6. 关键接口与数据
- 入口接口：
  - `ScreenSpaceShadows.AddRenderPasses(...)`：决定是否启用该特性并入队 Pass。证据：`Runtime/RendererFeatures/ScreenSpaceShadows.cs:50`。
  - `ScreenSpaceShadowsPass.RecordRenderGraph(...)`：分配纹理、写入全局纹理、执行全屏阴影解析。证据：`Runtime/RendererFeatures/ScreenSpaceShadows.cs:190`。
  - `ScreenSpaceShadowsPostPass.RecordRenderGraph(...)`：回切阴影关键词。证据：`Runtime/RendererFeatures/ScreenSpaceShadows.cs:327`。
- 外部契约：
  - 输入：相机深度（`ConfigureInput(ScriptableRenderPassInput.Depth)`）。证据：`Runtime/RendererFeatures/ScreenSpaceShadows.cs:144`。
  - 输出：全局纹理 `_ScreenSpaceShadowmapTexture` 与主光阴影相关全局关键词状态。
- 核心数据与状态：
  - 纹理格式优先 `R8_UNorm`，不支持时回退 `B8G8R8A8_UNorm`。证据：`Runtime/RendererFeatures/ScreenSpaceShadows.cs:203`-`Runtime/RendererFeatures/ScreenSpaceShadows.cs:205`。
  - 纹理描述固定 `msaaSamples = 1`、`depthStencilFormat = None`。证据：`Runtime/RendererFeatures/ScreenSpaceShadows.cs:199`-`Runtime/RendererFeatures/ScreenSpaceShadows.cs:200`。

## 7. 实现要点
- 关键实现路径：
  1. 全屏 shader 从深度重建世界坐标，再转主光阴影坐标并计算实时阴影衰减。证据：`Shaders/Utils/ScreenSpaceShadows.shader:23`-`Shaders/Utils/ScreenSpaceShadows.shader:35`。
  2. 阴影值写入单通道/回退 RGBA 纹理，后续在 `Shadows.hlsl` 的屏幕空间分支采样。证据：`ShaderLibrary/Shadows.hlsl:218`-`ShaderLibrary/Shadows.hlsl:231`。
- 参数与开关：
  - 运行时设置类 `ScreenSpaceShadowsSettings` 为空，当前无用户参数。证据：`Runtime/RendererFeatures/ScreenSpaceShadows.cs:8`-`Runtime/RendererFeatures/ScreenSpaceShadows.cs:10`。
  - Inspector 明确提示“暂无选项”。证据：`Editor/RendererFeatures/ScreenSpaceShadowsEditor.cs:15`。
- 兼容性与约束：
  - 屏幕空间阴影仅用于主方向光路径（注释明确）。证据：`Shaders/Utils/ScreenSpaceShadows.shader:32`。
  - 透明材质不走 `_MAIN_LIGHT_SHADOWS_SCREEN` 分支（条件含 `!_SURFACE_TYPE_TRANSPARENT`）。证据：`ShaderLibrary/Shadows.hlsl:358`-`ShaderLibrary/Shadows.hlsl:360`、`ShaderLibrary/Shadows.hlsl:378`-`ShaderLibrary/Shadows.hlsl:381`。

## 7A. 实现细节（图形学效果）
- 算法主路径：
  - 路径 A（本 Feature 的解析 Pass）：
    1. `LoadSceneDepth` 读取深度。
    2. `ComputeWorldSpacePosition` 重建世界坐标。
    3. `TransformWorldToShadowCoord` 转主光阴影坐标。
    4. `MainLightRealtimeShadow` 计算阴影衰减并输出。证据：`Shaders/Utils/ScreenSpaceShadows.shader:23`-`Shaders/Utils/ScreenSpaceShadows.shader:35`。
  - 路径 B（后续物体着色阶段）：
    - 当 `_MAIN_LIGHT_SHADOWS_SCREEN` 打开且不是透明表面，`MainLightRealtimeShadow` 直接调用 `SampleScreenSpaceShadowmap`。证据：`ShaderLibrary/Shadows.hlsl:378`-`ShaderLibrary/Shadows.hlsl:381`。
- 核心计算（伪代码）：
```text
deviceDepth = LoadSceneDepth(pixel)
if !UNITY_REVERSED_Z:
    deviceDepth = deviceDepth * 2 - 1
worldPos = ComputeWorldSpacePosition(uv, deviceDepth, invVP)
shadowCoord = TransformWorldToShadowCoord(worldPos)
attenuation = MainLightRealtimeShadow(shadowCoord)
out = attenuation
```
- 高频信息处理：
  - 本模块未引入时域/空域去噪。
  - 软阴影平滑来自主光阴影采样函数（`_SHADOWS_SOFT_*` 变体），不是额外屏幕空间滤波。证据：`Shaders/Utils/ScreenSpaceShadows.shader:49` 与 `ShaderLibrary/Shadows.hlsl:234` 之后的不同采样质量函数。
- 质量分档：
  - `ScreenSpaceShadows.shader` 编译 ` _ / _SHADOWS_SOFT / _SHADOWS_SOFT_LOW / _SHADOWS_SOFT_MEDIUM / _SHADOWS_SOFT_HIGH`。证据：`Shaders/Utils/ScreenSpaceShadows.shader:49`。
  - 分档代价由 ShadowMap 采样 tap 数决定（低/中/高对应不同滤波采样复杂度）。证据：`ShaderLibrary/Shadows.hlsl:234`-`ShaderLibrary/Shadows.hlsl:255`。
- 参数影响链路：
  - 阴影软化档位提升 -> 单像素 ShadowMap 采样数增加 -> 阴影边缘更平滑但 GPU 成本上升。
  - 输出纹理格式从 `R8_UNorm` 回退到 `B8G8R8A8_UNorm` -> 显存与带宽成本上升。证据：`Runtime/RendererFeatures/ScreenSpaceShadows.cs:203`-`Runtime/RendererFeatures/ScreenSpaceShadows.cs:205`。

## 8. 效果与性能
- 画面或行为变化：
  - 不透明阶段主光阴影采样来源从 ShadowMap 直接路径切换为屏幕空间纹理路径。
  - 透明阶段恢复普通主光阴影关键词，保持透明对象兼容行为。
- 代价与副作用：
  - 新增一次全屏 Pass 和一张屏幕分辨率阴影纹理。
  - 依赖全局关键词时序，若自定义 Pass 在中间改写关键词，可能导致阴影路径异常。

## 9. 错误处理与风险
- 错误类别：
  - shader/material 丢失：打印错误并跳过该 Feature。证据：`Runtime/RendererFeatures/ScreenSpaceShadows.cs:55`-`Runtime/RendererFeatures/ScreenSpaceShadows.cs:60`、`Runtime/RendererFeatures/ScreenSpaceShadows.cs:192`-`Runtime/RendererFeatures/ScreenSpaceShadows.cs:195`。
- 重试与回退：
  - 格式不支持时从 `R8_UNorm` 回退到 `B8G8R8A8_UNorm`。证据：`Runtime/RendererFeatures/ScreenSpaceShadows.cs:203`-`Runtime/RendererFeatures/ScreenSpaceShadows.cs:205`。
- 当前风险：
  - 使用 `AddUnsafePass` 是已知问题规避策略，后续 URP 内部实现若变更，行为可能调整。证据：`Runtime/RendererFeatures/ScreenSpaceShadows.cs:208`-`Runtime/RendererFeatures/ScreenSpaceShadows.cs:214`。

## 10. 接入与验证
- 接入步骤：
  1. 在 Universal Renderer 中启用 `Screen Space Shadows` Renderer Feature。
  2. 确认场景存在启用实时阴影的主方向光。
  3. 确认相机不是只输出 `RenderTextureFormat.Depth` 的离屏深度相机。
- 调试/验证方法：
  1. 使用 Frame Debugger 检查是否出现 `Blit Screen Space Shadows` 和 `Set Screen Space Shadow Keywords` Pass。
  2. 在不透明阶段确认 `_MAIN_LIGHT_SHADOWS_SCREEN` 生效；透明阶段前被关闭并恢复普通主光阴影关键词。
  3. 检查 `_ScreenSpaceShadowmapTexture` 是否被设置为全局纹理。

## 11. 代码证据
- `Packages/com.unity.render-pipelines.universal@1e87cf1dccb8/Runtime/RendererFeatures/ScreenSpaceShadows.cs`：Feature 入口、Pass 调度、关键词切换、RenderGraph 资源创建。
- `Packages/com.unity.render-pipelines.universal@1e87cf1dccb8/Shaders/Utils/ScreenSpaceShadows.shader`：全屏阴影解析 shader。
- `Packages/com.unity.render-pipelines.universal@1e87cf1dccb8/ShaderLibrary/Shadows.hlsl`：屏幕空间与普通阴影采样分支。
- `Packages/com.unity.render-pipelines.universal@1e87cf1dccb8/Runtime/UniversalRenderPipelineCore.cs`：主光阴影关键词定义。
- `Packages/com.unity.render-pipelines.universal@1e87cf1dccb8/Editor/ShaderBuildPreprocessor.cs`、`Packages/com.unity.render-pipelines.universal@1e87cf1dccb8/Editor/ShaderScriptableStripper.cs`：Feature 对应 shader 变体保留/裁剪链路。

## 12. 假设与待确认
- 假设：
  - 本文基于本地包源码 `com.unity.render-pipelines.universal@1e87cf1dccb8`（URP 17.3.0），不同版本实现可能不同。
- 待确认问题：
  - 若项目插入了会修改主光阴影关键词的自定义 Pass，需逐帧验证是否干扰 `ScreenSpaceShadowsPostPass` 的回切顺序。

## 13. 变更记录
- 2026-02-06：基于本地 URP 17.3.0 源码完成文档初版。
