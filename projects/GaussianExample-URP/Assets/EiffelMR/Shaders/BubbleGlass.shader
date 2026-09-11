Shader "EiffelMR/BubbleGlass"
{
    // A soap-bubble shell: blue tinted, refracting what is behind it, brightest
    // at the rim, with a thin-film iridescence that drifts as you move around
    // it.
    //
    // Refraction is screen-space. It samples the camera's opaque texture with
    // the UV pushed sideways by the surface normal, which is the cheap trick
    // every bubble shader uses - it is not real refraction and it does not need
    // to be, because the thing behind the bubble here is passthrough video of
    // the player's own room and nobody can check the maths on that. The URP
    // asset must have Opaque Texture enabled or the sample returns black.
    //
    // Written by hand rather than in Shader Graph so it can be diffed and so the
    // Quest build does not depend on a graph asset importing correctly.
    Properties
    {
        _Tint ("Tint", Color) = (0.35, 0.62, 1.0, 1.0)
        _RimColor ("Rim Colour", Color) = (0.75, 0.9, 1.0, 1.0)
        _RimPower ("Rim Power", Range(0.5, 8)) = 3.0
        _RimIntensity ("Rim Intensity", Range(0, 6)) = 2.2
        _Refraction ("Refraction", Range(0, 0.2)) = 0.045
        _Iridescence ("Iridescence", Range(0, 1)) = 0.35
        _FilmScale ("Film Scale", Range(1, 40)) = 12
        _Opacity ("Base Opacity", Range(0, 1)) = 0.18
        _Pop ("Pop", Range(0, 1)) = 0
        [Toggle] _UseReflections ("Reflection Probe", Float) = 1
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
            Name "BubbleForward"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            // Both faces: a bubble you can put your hand inside has to keep its
            // far wall, and with a single-sided shell the silhouette collapses
            // the moment the head enters the sphere.
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma shader_feature_local_fragment _USEREFLECTIONS_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 screenPos : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _Tint;
                float4 _RimColor;
                float _RimPower;
                float _RimIntensity;
                float _Refraction;
                float _Iridescence;
                float _FilmScale;
                float _Opacity;
                float _Pop;
            CBUFFER_END

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                // Popping is a quick swell and thin, not a fade: the shell
                // stretches, goes glassy, and is gone.
                float swell = 1.0 + _Pop * 0.35;
                float3 posOS = IN.positionOS.xyz * swell;

                float3 positionWS = TransformObjectToWorld(posOS);
                OUT.positionWS = positionWS;
                OUT.positionHCS = TransformWorldToHClip(positionWS);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.screenPos = ComputeScreenPos(OUT.positionHCS);
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float3 normalWS = normalize(IN.normalWS);
                float3 viewWS = normalize(GetWorldSpaceViewDir(IN.positionWS));
                // Cull Off means back faces arrive with the normal pointing
                // away; flip them so rim and refraction behave on both walls.
                normalWS *= sign(dot(normalWS, viewWS));

                float ndv = saturate(dot(normalWS, viewWS));
                float fresnel = pow(1.0 - ndv, _RimPower);

                float2 screenUV = IN.screenPos.xy / max(IN.screenPos.w, 1e-5);
                float2 offset = normalWS.xy * _Refraction * (1.0 - ndv);
                float3 behind = SampleSceneColor(screenUV + offset);

                // Thin-film: the interference colour depends on how much film
                // the ray crosses, which is what the grazing-angle term is.
                float film = frac(_FilmScale * (1.0 - ndv) + _Time.y * 0.05);
                float3 iris = float3(
                    0.5 + 0.5 * cos(6.2831853 * (film + 0.00)),
                    0.5 + 0.5 * cos(6.2831853 * (film + 0.33)),
                    0.5 + 0.5 * cos(6.2831853 * (film + 0.67)));

                float3 col = behind * _Tint.rgb;
                col = lerp(col, col * iris, _Iridescence * fresnel);
                col += _RimColor.rgb * fresnel * _RimIntensity;

            #ifdef _USEREFLECTIONS_ON
                float3 reflectWS = reflect(-viewWS, normalWS);
                float3 probe = GlossyEnvironmentReflection(reflectWS, IN.positionWS, 0.08, 1.0, screenUV);
                col += probe * fresnel * 0.6;
            #endif

                float alpha = saturate(_Opacity + fresnel * _Tint.a);
                alpha *= 1.0 - _Pop;
                return half4(col, alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
