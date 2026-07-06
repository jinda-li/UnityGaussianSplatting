Shader "VRInteraction/InfoOrbPanelReveal"
{
    Properties
    {
        _MainTex ("Background Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (0.05, 0.08, 0.1, 0.85)
        _EdgeColor ("Edge Glow Color", Color) = (0.6, 1.0, 1.0, 1.0)
        _EdgeWidth ("Edge Glow Width", Range(0.001, 0.3)) = 0.05
        // Driven at runtime via MaterialPropertyBlock (InfoOrbController): 0 = fully closed
        // (hidden at the center), 1 = fully revealed out to the panel's edge.
        _Progress ("Reveal Progress", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float4 _MainTex_ST;

            half4 _Color;
            half4 _EdgeColor;
            float _EdgeWidth;
            float _Progress;

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // Distance from panel center, normalized so 1.0 reaches the inscribed circle -
                // this is the center-to-edge wipe the orb's text/background reveals along.
                float dist = length(IN.uv - 0.5) * 2.0;

                float mask = 1.0 - smoothstep(_Progress - _EdgeWidth, _Progress, dist);
                float edge = smoothstep(_Progress - _EdgeWidth, _Progress, dist)
                           - smoothstep(_Progress, _Progress + _EdgeWidth, dist);

                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
                half3 color = tex.rgb * _Color.rgb + _EdgeColor.rgb * edge;
                half alpha = saturate((tex.a * _Color.a) * mask + edge * _EdgeColor.a);

                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}
