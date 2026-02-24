Shader "Yuki/RenderFeature/FXAA"
{
    Properties
    {
        _Intensity("Intensity", Range(0.0, 2.0)) = 1.0
        _FxaaSubpix("FXAA Subpix", Range(0.0, 1.0)) = 0.65
        _FxaaEdgeThreshold("FXAA Edge Threshold", Range(0.063, 0.333)) = 0.15
        _FxaaEdgeThresholdMin("FXAA Edge Threshold Min", Range(0.0, 0.0833)) = 0.03
    }

    HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        float _Intensity;
        float _FxaaSubpix;
        float _FxaaEdgeThreshold;
        float _FxaaEdgeThresholdMin;

        // Preset controls edge search taps (Quality path only).
        #ifndef FXAA_QUALITY_PRESET
        #define FXAA_QUALITY_PRESET 15
        #endif

        #if (FXAA_QUALITY_PRESET == 10)
            #define FXAA_QUALITY_STEP_COUNT 3
        #elif (FXAA_QUALITY_PRESET == 11)
            #define FXAA_QUALITY_STEP_COUNT 4
        #elif (FXAA_QUALITY_PRESET == 12)
            #define FXAA_QUALITY_STEP_COUNT 5
        #elif (FXAA_QUALITY_PRESET == 13)
            #define FXAA_QUALITY_STEP_COUNT 6
        #elif (FXAA_QUALITY_PRESET == 14)
            #define FXAA_QUALITY_STEP_COUNT 7
        #elif (FXAA_QUALITY_PRESET == 15)
            #define FXAA_QUALITY_STEP_COUNT 8
        #else
            #error Unsupported FXAA_QUALITY_PRESET. Use 10..15.
        #endif

        float GetQualityStep(int index)
        {
            #if (FXAA_QUALITY_PRESET == 10)
                if (index == 0) return 1.5;
                if (index == 1) return 3.0;
                return 12.0;
            #elif (FXAA_QUALITY_PRESET == 11)
                if (index == 0) return 1.0;
                if (index == 1) return 1.5;
                if (index == 2) return 3.0;
                return 12.0;
            #elif (FXAA_QUALITY_PRESET == 12)
                if (index == 0) return 1.0;
                if (index == 1) return 1.5;
                if (index == 2) return 2.0;
                if (index == 3) return 4.0;
                return 12.0;
            #elif (FXAA_QUALITY_PRESET == 13)
                if (index == 0) return 1.0;
                if (index == 1) return 1.5;
                if (index == 2) return 2.0;
                if (index == 3) return 2.0;
                if (index == 4) return 4.0;
                return 12.0;
            #elif (FXAA_QUALITY_PRESET == 14)
                if (index == 0) return 1.0;
                if (index == 1) return 1.5;
                if (index == 2) return 2.0;
                if (index == 3) return 2.0;
                if (index == 4) return 2.0;
                if (index == 5) return 4.0;
                return 12.0;
            #else // 15
                if (index == 0) return 1.0;
                if (index == 1) return 1.5;
                if (index == 2) return 2.0;
                if (index == 3) return 2.0;
                if (index == 4) return 2.0;
                if (index == 5) return 2.0;
                if (index == 6) return 4.0;
                return 12.0;
            #endif
        }

        float ComputeLuma(float3 color)
        {
            return dot(color, float3(0.299, 0.587, 0.114));
        }

        float3 SampleColor(float2 uv)
        {
            return SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;
        }

        float SampleLuma(float2 uv)
        {
            return ComputeLuma(SampleColor(uv));
        }

        struct FxaaCrossData
        {
            float m;
            float n;
            float s;
            float w;
            float e;
        };

        struct FxaaDiagData
        {
            float nw;
            float ne;
            float sw;
            float se;
        };

        struct FxaaEdgeData
        {
            bool horzSpan;
            float lengthSign;
            float gradientScaled;
            float lumaLocalPair;
            bool lumaMLTZero;
            float2 offNP;
            float2 posBase;
        };

        struct FxaaSearchData
        {
            float2 posN;
            float2 posP;
            float lumaEndN;
            float lumaEndP;
        };

        FxaaCrossData SampleCrossLuma(float2 uv, float2 rcpFrame, float lumaM)
        {
            FxaaCrossData cross;
            cross.m = lumaM;
            cross.n = SampleLuma(uv + float2(0.0, -rcpFrame.y));
            cross.s = SampleLuma(uv + float2(0.0, rcpFrame.y));
            cross.w = SampleLuma(uv + float2(-rcpFrame.x, 0.0));
            cross.e = SampleLuma(uv + float2(rcpFrame.x, 0.0));
            return cross;
        }

        FxaaDiagData SampleDiagLuma(float2 uv, float2 rcpFrame)
        {
            FxaaDiagData diag;
            diag.nw = SampleLuma(uv + float2(-rcpFrame.x, -rcpFrame.y));
            diag.ne = SampleLuma(uv + float2(rcpFrame.x, -rcpFrame.y));
            diag.sw = SampleLuma(uv + float2(-rcpFrame.x, rcpFrame.y));
            diag.se = SampleLuma(uv + float2(rcpFrame.x, rcpFrame.y));
            return diag;
        }

        bool IsEarlyExit(FxaaCrossData cross, out float range)
        {
            float maxSM = max(cross.s, cross.m);
            float minSM = min(cross.s, cross.m);
            float maxESM = max(cross.e, maxSM);
            float minESM = min(cross.e, minSM);
            float maxWN = max(cross.n, cross.w);
            float minWN = min(cross.n, cross.w);
            float rangeMax = max(maxWN, maxESM);
            float rangeMin = min(minWN, minESM);

            range = rangeMax - rangeMin;
            float threshold = max(_FxaaEdgeThresholdMin, rangeMax * _FxaaEdgeThreshold);
            return range < threshold;
        }

        FxaaEdgeData BuildEdgeData(FxaaCrossData cross, FxaaDiagData diag, float2 uv, float2 rcpFrame)
        {
            FxaaEdgeData data;

            float edgeHorz1 = (-2.0 * cross.m) + (cross.n + cross.s);
            float edgeVert1 = (-2.0 * cross.m) + (cross.w + cross.e);
            float edgeHorz2 = (-2.0 * cross.e) + (diag.ne + diag.se);
            float edgeVert2 = (-2.0 * cross.n) + (diag.nw + diag.ne);
            float edgeHorz3 = (-2.0 * cross.w) + (diag.nw + diag.sw);
            float edgeVert3 = (-2.0 * cross.s) + (diag.sw + diag.se);

            float edgeHorz = abs(edgeHorz3) + (abs(edgeHorz1) * 2.0 + abs(edgeHorz2));
            float edgeVert = abs(edgeVert3) + (abs(edgeVert1) * 2.0 + abs(edgeVert2));

            data.horzSpan = edgeHorz >= edgeVert;
            data.lengthSign = data.horzSpan ? rcpFrame.y : rcpFrame.x;

            float lumaN = data.horzSpan ? cross.n : cross.w;
            float lumaS = data.horzSpan ? cross.s : cross.e;
            float gradientN = lumaN - cross.m;
            float gradientS = lumaS - cross.m;
            bool pairN = abs(gradientN) >= abs(gradientS);
            float gradient = max(abs(gradientN), abs(gradientS));

            if (pairN)
            {
                data.lengthSign = -data.lengthSign;
            }

            float lumaNN = lumaN + cross.m;
            float lumaSS = lumaS + cross.m;
            data.lumaLocalPair = pairN ? lumaNN : lumaSS;
            data.gradientScaled = gradient * 0.25;
            data.lumaMLTZero = (cross.m - data.lumaLocalPair * 0.5) < 0.0;

            data.offNP = data.horzSpan ? float2(rcpFrame.x, 0.0) : float2(0.0, rcpFrame.y);
            data.posBase = uv;
            if (data.horzSpan)
            {
                data.posBase.y += data.lengthSign * 0.5;
            }
            else
            {
                data.posBase.x += data.lengthSign * 0.5;
            }

            return data;
        }

        FxaaSearchData SearchEdgeEndPoints(FxaaEdgeData edge)
        {
            FxaaSearchData result;
            result.posN = edge.posBase;
            result.posP = edge.posBase;
            result.lumaEndN = 0.0;
            result.lumaEndP = 0.0;

            float distN = 0.0;
            float distP = 0.0;
            bool doneN = false;
            bool doneP = false;

            [unroll(8)]
            for (int i = 0; i < 8; ++i)
            {
                if (i >= FXAA_QUALITY_STEP_COUNT)
                {
                    break;
                }

                float stepDistance = GetQualityStep(i);

                if (!doneN)
                {
                    distN += stepDistance;
                    result.posN = edge.posBase - edge.offNP * distN;
                    result.lumaEndN = SampleLuma(result.posN) - edge.lumaLocalPair * 0.5;
                    doneN = abs(result.lumaEndN) >= edge.gradientScaled;
                }

                if (!doneP)
                {
                    distP += stepDistance;
                    result.posP = edge.posBase + edge.offNP * distP;
                    result.lumaEndP = SampleLuma(result.posP) - edge.lumaLocalPair * 0.5;
                    doneP = abs(result.lumaEndP) >= edge.gradientScaled;
                }
            }

            return result;
        }

        float ResolvePixelOffset(FxaaEdgeData edge, FxaaSearchData search, float2 uv)
        {
            float dstN = edge.horzSpan ? (uv.x - search.posN.x) : (uv.y - search.posN.y);
            float dstP = edge.horzSpan ? (search.posP.x - uv.x) : (search.posP.y - uv.y);
            float spanLength = dstN + dstP;

            if (spanLength <= 1e-5)
            {
                return 0.0;
            }

            bool goodSpanN = (search.lumaEndN < 0.0) != edge.lumaMLTZero;
            bool goodSpanP = (search.lumaEndP < 0.0) != edge.lumaMLTZero;
            bool directionN = dstN < dstP;
            float dst = min(dstN, dstP);
            bool goodSpan = directionN ? goodSpanN : goodSpanP;

            float pixelOffset = (dst * (-rcp(spanLength))) + 0.5;
            return goodSpan ? pixelOffset : 0.0;
        }

        float ResolveSubpixelOffset(FxaaCrossData cross, FxaaDiagData diag, float range)
        {
            float subpixNSWE = cross.n + cross.s + cross.w + cross.e;
            float subpixNWSWNESE = diag.nw + diag.sw + diag.ne + diag.se;
            float subpixA = subpixNSWE * 2.0 + subpixNWSWNESE;
            float subpixB = subpixA * (1.0 / 12.0) - cross.m;
            float subpixC = saturate(abs(subpixB) * rcp(max(range, 1e-6)));
            float subpixD = (-2.0 * subpixC) + 3.0;
            float subpixE = subpixC * subpixC;
            float subpixF = subpixD * subpixE;
            float subpixG = subpixF * subpixF;
            return subpixG * _FxaaSubpix;
        }

        float3 ApplyFxaaQuality(float2 uv, float2 rcpFrame, FxaaCrossData cross, float range)
        {
            FxaaDiagData diag = SampleDiagLuma(uv, rcpFrame);
            FxaaEdgeData edge = BuildEdgeData(cross, diag, uv, rcpFrame);
            FxaaSearchData search = SearchEdgeEndPoints(edge);

            float pixelOffset = ResolvePixelOffset(edge, search, uv);
            float subpixelOffset = ResolveSubpixelOffset(cross, diag, range);
            float finalOffset = max(pixelOffset, subpixelOffset);

            float2 finalUv = uv;
            if (edge.horzSpan)
            {
                finalUv.y += finalOffset * edge.lengthSign;
            }
            else
            {
                finalUv.x += finalOffset * edge.lengthSign;
            }

            return SampleColor(finalUv);
        }

        float3 ApplyFxaaConsole(float2 uv, float2 rcpFrame, float lumaM)
        {
            FxaaDiagData diag = SampleDiagLuma(uv, rcpFrame);

            float lumaMin = min(lumaM, min(min(diag.nw, diag.ne), min(diag.sw, diag.se)));
            float lumaMax = max(lumaM, max(max(diag.nw, diag.ne), max(diag.sw, diag.se)));

            float2 dir;
            dir.x = -((diag.nw + diag.ne) - (diag.sw + diag.se));
            dir.y = ((diag.nw + diag.sw) - (diag.ne + diag.se));

            // Console-style directional reduction to stabilize short edges.
            float dirReduce = max((diag.nw + diag.ne + diag.sw + diag.se) * (0.25 * 0.125), 1.0 / 128.0);
            float rcpDirMin = rcp(min(abs(dir.x), abs(dir.y)) + dirReduce);
            dir = clamp(dir * rcpDirMin, -8.0, 8.0) * rcpFrame;

            float3 rgbA = 0.5 * (
                SampleColor(uv + dir * (1.0 / 3.0 - 0.5)) +
                SampleColor(uv + dir * (2.0 / 3.0 - 0.5)));

            float3 rgbB = rgbA * 0.5 + 0.25 * (
                SampleColor(uv + dir * -0.5) +
                SampleColor(uv + dir * 0.5));

            float lumaB = ComputeLuma(rgbB);
            return (lumaB < lumaMin || lumaB > lumaMax) ? rgbA : rgbB;
        }

        half4 FragQuality(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            float2 uv = UnityStereoTransformScreenSpaceTex(input.texcoord);
            float2 rcpFrame = _BlitTexture_TexelSize.xy;
            if (any(rcpFrame <= 0.0))
            {
                rcpFrame = rcp(_ScreenParams.xy);
            }

            float4 source = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
            float lumaM = ComputeLuma(source.rgb);
            FxaaCrossData cross = SampleCrossLuma(uv, rcpFrame, lumaM);

            float range;
            if (IsEarlyExit(cross, range))
            {
                return source;
            }

            float3 fxaaColor = ApplyFxaaQuality(uv, rcpFrame, cross, range);
            float blend = saturate(_Intensity);
            float3 finalColor = lerp(source.rgb, fxaaColor, blend);
            return half4(finalColor, source.a);
        }

        half4 FragConsole(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            float2 uv = UnityStereoTransformScreenSpaceTex(input.texcoord);
            float2 rcpFrame = _BlitTexture_TexelSize.xy;
            if (any(rcpFrame <= 0.0))
            {
                rcpFrame = rcp(_ScreenParams.xy);
            }

            float4 source = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
            float lumaM = ComputeLuma(source.rgb);
            FxaaCrossData cross = SampleCrossLuma(uv, rcpFrame, lumaM);

            float range;
            if (IsEarlyExit(cross, range))
            {
                return source;
            }

            float3 fxaaColor = ApplyFxaaConsole(uv, rcpFrame, lumaM);
            float blend = saturate(_Intensity);
            float3 finalColor = lerp(source.rgb, fxaaColor, blend);
            return half4(finalColor, source.a);
        }
    ENDHLSL

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
        }

        Pass
        {
            Name "FXAAQuality"
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One Zero

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragQuality
            ENDHLSL
        }

        Pass
        {
            Name "FXAAConsole"
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One Zero

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragConsole
            ENDHLSL
        }
    }
}
