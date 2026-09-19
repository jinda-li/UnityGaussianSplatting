// The solid tower, dissolving in as its point cloud settles (MeshWorld).
//
// _Dissolve 0 = nothing drawn, 1 = solid. The pattern is value noise over the
// tower's OWN space, cell size in tower metres, so it is fixed to the steel as
// the tower grows rather than crawling over it. A thin tinted rim at the
// dissolve front matches the tint of the dispersed points, which is what makes
// the two read as one object changing state.
//
// Lighting is deliberately small: main light with shadows, spherical-harmonic
// ambient, a touch of rim. The tower mesh has no UVs worth texturing with.
Shader "EiffelMR/TowerDissolve"
{
    Properties
    {
        _BaseColor ("Base colour", Color) = (0.38, 0.31, 0.26, 1)
        _Dissolve ("Dissolve", Range(0,1)) = 1
        _CellSize ("Noise cell (object units)", Float) = 3.0
        _EdgeWidth ("Edge width", Range(0,0.3)) = 0.06
        [HDR] _EdgeColor ("Edge colour", Color) = (3.2, 1.7, 0.6, 1)
        _Ambient ("Ambient scale", Range(0,2)) = 1.0
    }
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" "RenderPipeline"="UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
            float _Dissolve;
            float _CellSize;
            float _EdgeWidth;
            half4 _EdgeColor;
            float _Ambient;
        CBUFFER_END

        float Hash31(float3 p)
        {
            p = frac(p * 0.1031);
            p += dot(p, p.zyx + 31.32);
            return frac((p.x + p.y) * p.z);
        }

        float ValueNoise(float3 p)
        {
            float3 i = floor(p);
            float3 f = frac(p);
            f = f * f * (3.0 - 2.0 * f);
            float n000 = Hash31(i), n100 = Hash31(i + float3(1,0,0));
            float n010 = Hash31(i + float3(0,1,0)), n110 = Hash31(i + float3(1,1,0));
            float n001 = Hash31(i + float3(0,0,1)), n101 = Hash31(i + float3(1,0,1));
            float n011 = Hash31(i + float3(0,1,1)), n111 = Hash31(i + float3(1,1,1));
            return lerp(lerp(lerp(n000, n100, f.x), lerp(n010, n110, f.x), f.y),
                        lerp(lerp(n001, n101, f.x), lerp(n011, n111, f.x), f.y), f.z);
        }

        // Signed distance of this fragment past the dissolve front: < 0 is not
        // there yet.
        float DissolveFront(float3 posOS)
        {
            float n = ValueNoise(posOS / max(_CellSize, 1e-3)) * 0.7
                    + ValueNoise(posOS / max(_CellSize * 0.23, 1e-3)) * 0.3;
            // Remap so 0 hides everything and 1 shows everything.
            return _Dissolve * (1.0 + _EdgeWidth) - n - _EdgeWidth * 0.5;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionOS : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert (Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.positionOS = v.positionOS.xyz;
                return o;
            }

            half4 frag (Varyings i, bool front : SV_IsFrontFace) : SV_Target
            {
                float d = DissolveFront(i.positionOS);
                clip(d);

                float3 n = normalize(i.normalWS) * (front ? 1.0 : -1.0);
                float4 shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                Light main = GetMainLight(shadowCoord);
                float ndl = saturate(dot(n, main.direction));
                half3 lit = _BaseColor.rgb * (main.color * ndl * main.shadowAttenuation
                                              + SampleSH(n) * _Ambient);
                float edge = 1.0 - saturate(d / max(_EdgeWidth, 1e-4));
                lit += _EdgeColor.rgb * edge * step(_Dissolve, 0.999);
                return half4(lit, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            Cull Off
            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            float3 _LightDirection;

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionOS : TEXCOORD0; };

            Varyings vert (Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                float3 ws = TransformObjectToWorld(v.positionOS.xyz);
                float3 nws = TransformObjectToWorldNormal(v.normalOS);
                o.positionCS = TransformWorldToHClip(ApplyShadowBias(ws, nws, _LightDirection));
                #if UNITY_REVERSED_Z
                    o.positionCS.z = min(o.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    o.positionCS.z = max(o.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                o.positionOS = v.positionOS.xyz;
                return o;
            }
            half4 frag (Varyings i) : SV_Target
            {
                clip(DissolveFront(i.positionOS));
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            Cull Off
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionOS : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };
            Varyings vert (Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.positionOS = v.positionOS.xyz;
                return o;
            }
            half4 frag (Varyings i) : SV_Target
            {
                clip(DissolveFront(i.positionOS));
                return 0;
            }
            ENDHLSL
        }
    }
}
