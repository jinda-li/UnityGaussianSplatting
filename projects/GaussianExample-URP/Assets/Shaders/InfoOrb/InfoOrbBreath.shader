Shader "VRInteraction/InfoOrbBreath"
{
    Properties
    {
        _Color ("Core Color", Color) = (0.4, 0.9, 1.0, 0.6)
        _RimColor ("Rim Color", Color) = (0.6, 1.0, 1.0, 1.0)
        _RimPower ("Rim Power", Range(0.5, 8)) = 2.5
        _RimIntensity ("Rim Intensity", Range(0, 10)) = 3.0
        _BreathSpeed ("Breath Speed", Range(0, 10)) = 1.5
        _BreathAmount ("Breath Amount", Range(0, 1)) = 0.35
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }
        ZWrite On
        Cull Back

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
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 viewDirWS : TEXCOORD1;
            };

            half4 _Color;
            half4 _RimColor;
            float _RimPower;
            float _RimIntensity;
            float _BreathSpeed;
            float _BreathAmount;

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionHCS = posInputs.positionCS;
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.viewDirWS = GetWorldSpaceViewDir(posInputs.positionWS);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 normalWS = normalize(IN.normalWS);
                float3 viewDirWS = normalize(IN.viewDirWS);

                // Breathing pulse - purely time driven, identical near or far, no script needed.
                float breath = 0.5 + 0.5 * sin(_Time.y * _BreathSpeed);
                float breathMul = 1.0 - _BreathAmount + _BreathAmount * breath;

                float fresnel = pow(saturate(1.0 - dot(normalWS, viewDirWS)), _RimPower);

                half3 core = _Color.rgb * breathMul;
                half3 rim = _RimColor.rgb * fresnel * _RimIntensity * breathMul;

                half alpha = saturate(_Color.a + fresnel * _RimColor.a);
                return half4(core + rim, alpha);
            }
            ENDHLSL
        }
    }
}
