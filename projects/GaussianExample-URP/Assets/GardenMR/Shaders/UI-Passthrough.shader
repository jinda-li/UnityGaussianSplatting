// Copy of Unity's built-in "UI/Default" shader with two changes for GardenMR's world-space
// menu, which sits directly over live MR passthrough:
//
//   1. Blend equation now writes destination alpha too (`, One OneMinusSrcAlpha`). Unity's
//      compositor treats the projection layer as PREMULTIPLIED alpha
//      (final = eye.rgb + passthrough.rgb * (1 - eye.a)). The stock UI/Default blend op only
//      touches RGB, so alpha compounds across stacked/nested UI elements (dst.a = a^2, ...)
//      and passthrough reads through more than the authored alpha suggests — a nominal 50%
//      panel ends up letting through ~28% of the room instead of the correct ~15%.
//      Verified once on-device with a flat 50% white square against a mid-grey wall
//      (see docs/plans/2026-08-07-multi-environment-mr-browser.md, Phase 2.5); if that test
//      instead reads too DARK, the runtime is submitting non-premultiplied alpha and this
//      should become `Blend One OneMinusSrcAlpha` with the fragment premultiplying color.rgb
//      by color.a before returning.
//
//   2. Rounded-corner + stroke SDF added directly in the fragment shader (no 9-slice sprite
//      needed). _Size must be set to the RectTransform's world/local unit size (NOT pixels)
//      for the corners to read as round rather than elliptical.
//
// Everything else (Stencil block, ColorMask, ZTest, UNITY_UI_CLIP_RECT / UNITY_UI_ALPHACLIP
// keywords) is kept byte-for-byte identical to UI/Default — those are what make
// Mask / RectMask2D work, and must not be touched.
Shader "UI/Passthrough"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255

        _ColorMask ("Color Mask", Float) = 15

        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0

        [Header(Rounded Corners)]
        _Size ("Rect Size (local units, xy)", Vector) = (1, 1, 0, 0)
        _Radius ("Corner Radius (local units)", Float) = 0.02
        _StrokeWidth ("Stroke Width (local units, 0 = off)", Float) = 0
        _StrokeColor ("Stroke Color", Color) = (1, 1, 1, 0.12)
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "Default"
        CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                fixed4 color    : COLOR;
                float2 texcoord  : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            fixed4 _Color;
            fixed4 _TextureSampleAdd;
            float4 _ClipRect;
            float4 _MainTex_ST;

            float2 _Size;
            float _Radius;
            float _StrokeWidth;
            fixed4 _StrokeColor;

            v2f vert(appdata_t v)
            {
                v2f OUT;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.worldPosition = v.vertex;
                OUT.vertex = UnityObjectToClipPos(OUT.worldPosition);

                OUT.texcoord = TRANSFORM_TEX(v.texcoord.xy, _MainTex);

                OUT.color = v.color * _Color;
                return OUT;
            }

            sampler2D _MainTex;

            // Signed distance to a rounded rect of half-size (_Size*0.5 - _Radius), inset by
            // _Radius. p is centered on the rect (texcoord 0..1 remapped to -halfSize..halfSize
            // in local units, NOT pixels, so _Size must be authored in the same units as the
            // RectTransform / mesh scale).
            float RoundedRectSdf(float2 p, float2 halfSize, float radius)
            {
                float2 q = abs(p) - halfSize + radius;
                return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                half4 color = (tex2D(_MainTex, IN.texcoord) + _TextureSampleAdd) * IN.color;

                float2 p = (IN.texcoord - 0.5) * _Size;
                float dist = RoundedRectSdf(p, _Size * 0.5, _Radius);
                float aa = max(fwidth(dist), 1e-5);
                color.a *= 1.0 - smoothstep(0.0, aa, dist);

                if (_StrokeWidth > 0.0)
                {
                    float strokeDist = abs(dist + _StrokeWidth * 0.5) - _StrokeWidth * 0.5;
                    float strokeAlpha = (1.0 - smoothstep(0.0, aa, strokeDist)) * _StrokeColor.a;
                    color.rgb = lerp(color.rgb, _StrokeColor.rgb, strokeAlpha);
                    color.a = max(color.a, strokeAlpha);
                }

                #ifdef UNITY_UI_CLIP_RECT
                color.a *= UnityGet2DClipping(IN.worldPosition.xy, _ClipRect);
                #endif

                #ifdef UNITY_UI_ALPHACLIP
                clip (color.a - 0.001);
                #endif

                return color;
            }
        ENDCG
        }
    }
}
