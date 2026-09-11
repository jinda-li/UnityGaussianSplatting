Shader "EiffelMR/LandingRingGlow"
{
    // The target on the floor: an annulus with a soft inner wash and a travelling
    // pulse, drawn on a single quad.
    //
    // A quad rather than a torus mesh because it has to sit flat on a floor that
    // may not be flat - passthrough floors are a plane estimate and a ring with
    // thickness catches on the error. A flat card with the ring in the alpha
    // never intersects anything.
    Properties
    {
        _BaseColor ("Colour", Color) = (0.35, 0.72, 1.0, 0.9)
        _EmissionColor ("Emission", Color) = (0.4, 0.8, 1.0, 1)
        _Radius ("Ring Radius", Range(0.1, 0.5)) = 0.40
        _Thickness ("Ring Thickness", Range(0.005, 0.2)) = 0.035
        _Softness ("Edge Softness", Range(0.001, 0.1)) = 0.018
        _FillAlpha ("Inner Fill", Range(0, 1)) = 0.13
        _PulseSpeed ("Pulse Speed", Range(0, 4)) = 0.8
        _PulseWidth ("Pulse Width", Range(0.01, 0.4)) = 0.12
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "RingForward"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha One          // additive: it is a light on the floor
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _EmissionColor;
                float _Radius;
                float _Thickness;
                float _Softness;
                float _FillAlpha;
                float _PulseSpeed;
                float _PulseWidth;
            CBUFFER_END

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float r = length(IN.uv - 0.5);

                // The ring itself.
                float inner = _Radius - _Thickness * 0.5;
                float outer = _Radius + _Thickness * 0.5;
                float ring = smoothstep(inner - _Softness, inner, r) *
                             (1.0 - smoothstep(outer, outer + _Softness, r));

                // A faint wash inside it, so the area reads as a target rather
                // than as a hoop lying on the ground.
                float fill = (1.0 - smoothstep(inner - _Softness, inner, r)) * _FillAlpha;

                // A pulse travelling outwards. This is what makes it read as
                // "throw here" instead of as decoration.
                float phase = frac(_Time.y * _PulseSpeed);
                float pulse = smoothstep(_PulseWidth, 0.0, abs(r - phase * _Radius)) *
                              (1.0 - smoothstep(inner, outer, r)) * 0.5;

                float a = saturate(ring + fill + pulse);
                if (a <= 0.002)
                    discard;

                float3 col = _BaseColor.rgb + _EmissionColor.rgb * (ring + pulse);
                return half4(col * a, a * _BaseColor.a);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
