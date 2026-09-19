// The mesh-world tower as points (see TowerPointCloud.cs).
//
// Each point is four vertices at the same position; UV0 is the corner and UV1.x
// a per-point random. The quad is expanded in clip space to a fixed size in
// screen pixels. _Solidify 0 = sparse, tinted, drifting motes; 1 = every point
// settled on the tower's surface and faded out, handing over to the solid mesh.
Shader "EiffelMR/TowerPoints"
{
    Properties
    {
        _PointSize ("Point size (px)", Float) = 2.2
        _Fraction ("Dispersed fraction", Range(0,1)) = 0.55
        _Drift ("Drift (object units)", Float) = 2.5
        _DriftSpeed ("Drift speed", Float) = 0.9
        [HDR] _Tint ("Dispersed tint", Color) = (0.08, 0.16, 0.32, 1)
        _Sparkle ("Sparkle", Range(0,1)) = 0.3
        _Stagger ("Stagger", Range(0.05,1)) = 0.45
        _Solidify ("Solidify", Range(0,1)) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "Points"
            Tags { "LightMode"="UniversalForward" }
            ZWrite Off
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _PointSize;
                float _Fraction;
                float _Drift;
                float _DriftSpeed;
                half4 _Tint;
                float _Sparkle;
                float _Stagger;
                float _Solidify;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;
                float2 corner : TEXCOORD0;
                float2 rand : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float2 corner : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert (Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float h = v.rand.x;
                // Each point settles in its own window, so the tower condenses
                // point by point instead of every mote freezing at once.
                float w = max(_Stagger, 1e-3);
                float lit = saturate((_Solidify - h * (1.0 - w)) / w);
                lit = lit * lit * (3.0 - 2.0 * lit);
                float loose = 1.0 - lit;

                // Sparse while dispersed; the rest arrive as the cloud condenses.
                float visible = step(h, _Fraction) + lit;
                // ...and every point fades as it lands, because the solid mesh
                // is dissolving in underneath it.
                float alpha = saturate(visible) * (1.0 - smoothstep(0.55, 1.0, lit));

                float ph = h * 6.2831853;
                float t = _Time.y * _DriftSpeed;
                float3 drift = float3(sin(t + ph), sin(t * 1.13 + ph * 2.0),
                                      cos(t * 0.87 + ph * 0.5)) * (_Drift * loose);
                float3 posOS = v.positionOS.xyz + drift;

                float4 cs = TransformObjectToHClip(posOS);
                float2 px = v.corner * (_PointSize * (1.0 + 0.6 * loose));
                cs.xy += px * 2.0 / _ScreenParams.xy * cs.w;
                o.positionCS = cs;

                half3 c = v.color.rgb + _Tint.rgb * loose;
                c *= 1.0 + _Sparkle * loose * sin(_Time.y * 3.7 + ph * 3.0);
                o.color = half4(c, alpha);
                o.corner = v.corner;
                if (alpha <= 0.001)
                    o.positionCS = float4(0, 0, -2, 1);   // outside the clip volume
                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                float r2 = dot(i.corner, i.corner);
                half a = i.color.a * exp(-2.2 * r2);
                clip(a - 0.01);
                return half4(i.color.rgb, a);
            }
            ENDHLSL
        }
    }
}
