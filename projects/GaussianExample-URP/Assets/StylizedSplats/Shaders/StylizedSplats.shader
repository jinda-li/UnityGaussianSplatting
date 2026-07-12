// Superset of "Gaussian Splatting/Render Splats" (package, unmodified).
// When _StylizedEnable is 0, output is pixel-identical to the stock shader.
// Assign this shader to GaussianSplatRenderer.m_ShaderSplats (Resources foldout)
// instead of forking the package. Style params are set via Shader.SetGlobal*
// from StylizedSplatsController, not material properties, so they apply
// uniformly without per-material setup.
Shader "Gaussian Splatting/Stylized Splats"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite Off
            Blend OneMinusDstAlpha One
            Cull Off

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc

#include "Packages/org.nesnausk.gaussian-splatting/Shaders/GaussianSplatting.hlsl"

StructuredBuffer<uint> _OrderBuffer;
StructuredBuffer<SplatViewData> _SplatViewData;
ByteAddressBuffer _SplatSelectedBits;
uint _SplatBitsValid;

// stylized extension globals
float _StylizedEnable;
float _StyleSizeMin;
float _StyleSizeMax;
float _StyleAlphaCut;
float _StyleAlphaGamma;
float _StyleRandomFlip;
float _BaseSaturation;
float _BaseLift;

Texture2D _StylizedBrushTex;
SamplerState sampler_StylizedBrushTex;

StructuredBuffer<float> _SplatPaintProgress;
uint _SplatPaintValid;

struct v2f
{
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
    half4 style : TEXCOORD1; // x = styleAmount (0=stylized,1=gaussian), y = opacity, z = flip sign, w unused
    float4 vertex : SV_POSITION;
};

// HLSL-style smoothstep (edge0->0, edge1->1), unlike Unity's Mathf.SmoothStep
float SmoothStepEdge(float edge0, float edge1, float x)
{
    float t = saturate((x - edge0) / max(edge1 - edge0, 1e-5));
    return t * t * (3.0 - 2.0 * t);
}

float HashInstance(uint idx)
{
    uint h = idx * 747796405u + 2891336453u;
    h = ((h >> ((h >> 28u) + 4u)) ^ h) * 277803737u;
    h = (h >> 22u) ^ h;
    return frac(h / 4294967296.0);
}

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o = (v2f)0;
    instID = _OrderBuffer[instID];
    SplatViewData view = _SplatViewData[instID];
    float4 centerClipPos = view.pos;
    bool behindCam = centerClipPos.w <= 0;
    if (behindCam)
    {
        o.vertex = asfloat(0x7fc00000); // NaN discards the primitive
    }
    else
    {
        o.col.r = f16tof32(view.color.x >> 16);
        o.col.g = f16tof32(view.color.x);
        o.col.b = f16tof32(view.color.y >> 16);
        o.col.a = f16tof32(view.color.y);

        // _StylizedEnable == 0 must stay pixel-identical to the stock shader,
        // so all of the new behavior is gated behind it.
        float styleAmount = 1; // 1 = full gaussian (stock look)
        if (_StylizedEnable != 0)
        {
            // base look: desaturate, lift toward white (unpainted-canvas feel;
            // 1 would be pure white fog - keep some luminance shading so the
            // scene stays readable), then let paint progress bring color back
            half lum = dot(o.col.rgb, half3(0.299h, 0.587h, 0.114h));
            half3 desat = lerp(lum.xxx, o.col.rgb, saturate(_BaseSaturation));
            half3 baseCol = lerp(desat, half3(1, 1, 1), saturate(_BaseLift));
            float paint = 0;
            if (_SplatPaintValid != 0)
                paint = _SplatPaintProgress[instID];
            o.col.rgb = lerp(baseCol, o.col.rgb, saturate(paint));

            SplatData splat = LoadSplatData(instID);
            float worldScale = length(unity_ObjectToWorld._m00_m10_m20);
            float worldSize = max(max(splat.scale.x, splat.scale.y), splat.scale.z) * worldScale;
            styleAmount = SmoothStepEdge(_StyleSizeMin, _StyleSizeMax, worldSize);
        }
        o.style.x = styleAmount;
        o.style.y = o.col.a; // opacity
        o.style.z = (_StyleRandomFlip != 0 && HashInstance(instID) > 0.5) ? -1.0 : 1.0;

        uint idx = vtxID;
        float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
        quadPos *= 2;

        o.pos = quadPos;

        float2 deltaScreenPos = (quadPos.x * view.axis1 + quadPos.y * view.axis2) * 2 / _ScreenParams.xy;
        o.vertex = centerClipPos;
        o.vertex.xy += deltaScreenPos * centerClipPos.w;

        // is this splat selected?
        if (_SplatBitsValid)
        {
            uint wordIdx = instID / 32;
            uint bitIdx = instID & 31;
            uint selVal = _SplatSelectedBits.Load(wordIdx * 4);
            if (selVal & (1 << bitIdx))
            {
                o.col.a = -1;
            }
        }
    }
    FlipProjectionIfBackbuffer(o.vertex);
    return o;
}

half4 ApplySelectedTint(half4 col, half alpha)
{
    if (col.a >= 0)
    {
        col.a = saturate(alpha * col.a);
    }
    else
    {
        // "selected" splat: magenta outline, increase opacity, magenta tint
        half3 selectedColor = half3(1,0,1);
        col.a = alpha;
        if (col.a > 7.0/255.0)
        {
            if (col.a < 10.0/255.0)
            {
                col.a = 1;
                col.rgb = selectedColor;
            }
            col.a = saturate(col.a + 0.3);
        }
        col.rgb = lerp(col.rgb, selectedColor, 0.5);
    }
    return col;
}

half4 frag (v2f i) : SV_Target
{
    float gaussPower = -dot(i.pos, i.pos);
    half gaussAlpha = exp(gaussPower);

    half4 res;
    if (_StylizedEnable == 0)
    {
        i.col = ApplySelectedTint(i.col, gaussAlpha);
        if (i.col.a < 1.0/255.0)
            discard;
        res = half4(i.col.rgb * i.col.a, i.col.a);
        return res;
    }

    float2 uv = i.pos * 0.25 + 0.5;
    if (i.style.z < 0)
        uv.x = 1 - uv.x;
    half brush = _StylizedBrushTex.Sample(sampler_StylizedBrushTex, uv).a;
    half strokeAlpha = saturate((brush - _StyleAlphaCut) / max(1 - _StyleAlphaCut, 1e-4));
    strokeAlpha = pow(strokeAlpha, max(_StyleAlphaGamma, 1e-3));
    half opacity = i.style.y;
    strokeAlpha *= opacity;

    half finalAlpha = lerp(strokeAlpha, saturate(gaussAlpha * opacity), i.style.x);

    i.col = ApplySelectedTint(i.col, finalAlpha);
    if (i.col.a < 1.0/255.0)
        discard;

    res = half4(i.col.rgb * i.col.a, i.col.a);
    return res;
}
ENDCG
        }
    }
}
