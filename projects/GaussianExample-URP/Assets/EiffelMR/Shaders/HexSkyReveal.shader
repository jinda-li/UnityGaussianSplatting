Shader "EiffelMR/HexSkyReveal"
{
    // The world arriving one hexagon at a time.
    //
    // Drawn on a large inverted sphere around the player. Every direction falls
    // into a hexagonal cell; each cell gets a stable random number, and a cell
    // becomes opaque when the reveal front passes its number. So the sky fills
    // in as a spreading patchwork rather than a dissolve, and because the order
    // is random-but-fixed it looks like tiles being placed, not noise fading up.
    //
    // The front is biased by the angle from _Origin, so the patchwork spreads
    // outwards from the point the miniature landed rather than appearing
    // everywhere at once.
    //
    // Colour comes from a cubemap - normally the same sky the splat was trained
    // under, so that when the reveal finishes the shell and the splat agree.
    Properties
    {
        _Cube ("Sky Cubemap", Cube) = "grey" {}
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        _Reveal ("Reveal", Range(0, 1)) = 0
        _HexScale ("Hex Scale", Range(4, 120)) = 34
        _EdgeWidth ("Edge Width", Range(0, 0.3)) = 0.055
        _EdgeColor ("Edge Colour", Color) = (0.55, 0.85, 1.0, 1)
        _EdgeIntensity ("Edge Intensity", Range(0, 8)) = 2.5
        _Directional ("Spread From Origin", Range(0, 1)) = 0.65
        _Origin ("Spread Origin (world dir)", Vector) = (0, 0, 1, 0)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent-100"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "HexReveal"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            // Seen from inside the shell.
            Cull Front

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 dirWS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURECUBE(_Cube);
            SAMPLER(sampler_Cube);

            CBUFFER_START(UnityPerMaterial)
                float4 _Tint;
                float _Reveal;
                float _HexScale;
                float _EdgeWidth;
                float4 _EdgeColor;
                float _EdgeIntensity;
                float _Directional;
                float4 _Origin;
            CBUFFER_END

            // Axial hex grid. Returns the cell's id in xy and the normalised
            // distance to the cell edge in z (0 at the centre, 1 at the edge).
            float3 HexCell(float2 p)
            {
                const float2 s = float2(1.0, 1.7320508);   // 1, sqrt(3)
                float4 hexCentre = round(float4(p, p - float2(0.5, 1.0)) / s.xyxy);
                float4 offset = float4(p - hexCentre.xy * s, p - (hexCentre.zw + 0.5) * s);
                float distA = dot(offset.xy, offset.xy);
                float distB = dot(offset.zw, offset.zw);
                bool nearer = distA < distB;
                float2 local = nearer ? offset.xy : offset.zw;
                float2 id = nearer ? hexCentre.xy : hexCentre.zw + 0.5;
                // Distance to the nearest of the three hex edge directions.
                float2 a = abs(local);
                float edge = max(dot(a, s * 0.5), a.x);
                return float3(id, edge);
            }

            float Hash21(float2 p)
            {
                p = frac(p * float2(233.34, 851.73));
                p += dot(p, p + 23.45);
                return frac(p.x * p.y);
            }

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS = TransformWorldToHClip(positionWS);
                OUT.dirWS = positionWS - GetCameraPositionWS();
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float3 dir = normalize(IN.dirWS);

                // Equirectangular parametrisation, so the hex grid is stable in
                // world space and does not swim when the head moves. The poles
                // are compressed, which nobody looking at a skyline notices.
                float2 uv = float2(atan2(dir.z, dir.x) / 6.2831853 + 0.5,
                                   acos(clamp(dir.y, -1, 1)) / 3.1415927);
                float3 cell = HexCell(uv * _HexScale * float2(2.0, 1.0));

                float rnd = Hash21(cell.xy);
                // Bias the order so tiles near the landing direction go first.
                float toward = saturate(0.5 + 0.5 * dot(dir, normalize(_Origin.xyz)));
                float order = lerp(rnd, saturate(rnd * 0.45 + (1.0 - toward) * 0.55),
                                   _Directional);

                // A little softness so tiles pop in over a few frames rather
                // than in one, which reads as placement rather than flicker.
                // Nothing at all before the reveal starts. Without this the
                // lowest-ordered tiles are already solid at _Reveal = 0, so a
                // scene sitting idle shows a scatter of grey hexagons hanging in
                // the room - which is what it looked like the first time.
                float fill = _Reveal <= 0.0005 ? 0.0 : smoothstep(order, order - 0.06, _Reveal);
                if (fill <= 0.001)
                    discard;

                float3 sky = SAMPLE_TEXTURECUBE(_Cube, sampler_Cube, dir).rgb * _Tint.rgb;

                // Glowing edge only on tiles that just arrived.
                float rim = smoothstep(1.0 - _EdgeWidth, 1.0, cell.z);
                float fresh = saturate(1.0 - abs(_Reveal - order) * 14.0);
                sky += _EdgeColor.rgb * rim * fresh * _EdgeIntensity;

                return half4(sky, fill);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
