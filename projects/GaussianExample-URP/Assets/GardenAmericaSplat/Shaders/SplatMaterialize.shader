// "Splat Materialize" render shader for the GardenAmericaSplat gameplay.
// A separate shader from "Gaussian Splatting/Stylized Splats" so the old oil-
// paint spray gameplay is left completely untouched.
//
// Look: the world begins as tiny DEBUG POINTS in each splat's original color (uniform screen-
// space dots, like the renderer's point mode - not gaussian ellipses), and only
// a fraction of them render, so it reads as raw sampled particle data. Paint
// progress (fed by SplatMaterializePaint.compute) grows each point into a full
// splat and materializes it into the SAME stylized oil-paint look as
// StylizedSplats (brush-textured strokes, size-based stroke/gaussian mix,
// churning random-flip) with its real color. The instant a splat is "born" it
// plays a one-shot bloom (swell + oversaturate + flash) off the birth timestamp
// in _SplatPaintProgress.y.
//
// Assign this to the target GaussianSplatRenderer's "Splats Shader" field.
// All params come from SplatMaterializeController via Shader.SetGlobal*.
Shader "Gaussian Splatting/Splat Materialize"
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

// paint state: x = progress 0..1, y = birth time (progress crossed the bloom
// threshold), large negative sentinel while still dormant
StructuredBuffer<float2> _SplatPaintProgress;
uint _SplatPaintValid;
float _MaterializePreview; // editor: show everything fully materialized

// clock shared with the compute pass
float _PaintTime;

// dormant look
half3 _DormantColorOffset;      // additive offset over the original splat color
float _DormantPointSize;        // dormant point size in screen pixels (uniform, like debug points)
float _DormantVisibleFraction;  // 0..1 fraction of still-dormant points that render
float _DormantDrift;            // metres of idle bob while dormant
float _DormantDriftSpeed;

// one-shot bloom
float _PopDuration;
float _PopSize;
float _PopSaturation;
float _PopFlash;
half3 _PopColor; // HDR-capable

// stylized painted look (same knobs as StylizedSplats)
float _StyleSizeMin;
float _StyleSizeMax;
float _StyleAlphaCut;
float _StyleAlphaGamma;
float _StyleRandomFlip;
float _StyleFlipJitter;
Texture2D _StylizedBrushTex;
SamplerState sampler_StylizedBrushTex;

struct v2f
{
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
    half4 style : TEXCOORD1; // x = styleAmount (0=stylized,1=gaussian), y = opacity, z = flip sign, w = stroke rotation (radians)
    float4 vertex : SV_POSITION;
};

// HLSL-style smoothstep (edge0->0, edge1->1)
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

        float2 axis1 = view.axis1, axis2 = view.axis2;
        bool discardSplat = false;

        float2 paintData = float2(0, -1e9);
        if (_SplatPaintValid != 0)
            paintData = _SplatPaintProgress[instID];
        float paint = paintData.x;
        if (_MaterializePreview != 0)
            paint = 1;
        float lit = saturate(paint);

        // start sparse: hide a fraction of the still-dormant points. Cheaper,
        // and reads as raw sampled data. Any paint at all makes a point appear
        // and materialize, so a burst brings the hidden ones in with the wave.
        if (paint <= 1e-5 && HashInstance(instID ^ 0x1234567u) > _DormantVisibleFraction)
            discardSplat = true;

        // Keep the original splat color as the base, with an optional additive
        // offset that fades away as the splat materializes.
        o.col.rgb += _DormantColorOffset * (1 - lit);

        // idle drift while dormant, damped to nothing as the splat is painted
        float3 worldPos = mul(unity_ObjectToWorld, float4(LoadSplatPos(instID), 1)).xyz;
        float h = HashInstance(instID);
        float bob = 1 - lit;
        float3 drift = float3(
            sin(_Time.y * _DormantDriftSpeed        + h * 6.2831853),
            sin(_Time.y * _DormantDriftSpeed * 1.13 + h * 12.566370),
            cos(_Time.y * _DormantDriftSpeed * 0.87 + h * 3.1415927)) * (_DormantDrift * bob);
        worldPos += drift;

        // size-based stroke/gaussian mix (like StylizedSplats), but forced to a
        // plain gaussian dot while dormant so the debug points stay clean.
        SplatData splat = LoadSplatData(instID);
        float worldScale = length(unity_ObjectToWorld._m00_m10_m20);
        float worldSize = max(max(splat.scale.x, splat.scale.y), splat.scale.z) * worldScale;
        float realStyle = SmoothStepEdge(_StyleSizeMin, _StyleSizeMax, worldSize);
        float styleAmount = lerp(1.0, realStyle, lit);

        // dormant look = small uniform screen-space point (like the renderer's
        // debug points), not the elongated gaussian ellipse. Blend to the real
        // anisotropic axes as the splat materializes. Uniform tiny points also
        // cost far less overdraw than full ellipses.
        float2 pointAxis1 = float2(_DormantPointSize, 0);
        float2 pointAxis2 = float2(0, _DormantPointSize);
        axis1 = lerp(pointAxis1, axis1, lit);
        axis2 = lerp(pointAxis2, axis2, lit);

        // one-shot bloom for _PopDuration seconds after birth: swell,
        // oversaturate, flash, then settle. A bell curve needs no end state -
        // once popT leaves (0,1) the splat is just its painted self, and a
        // dormant splat sits at popT >= 1 forever thanks to the sentinel.
        float popT = saturate((_PaintTime - paintData.y) / max(_PopDuration, 1e-4));
        if (popT > 0 && popT < 1)
        {
            float bell = sin(popT * 3.14159265);
            float ease = bell * bell;

            float grow = 1 + _PopSize * ease;
            axis1 *= grow;
            axis2 *= grow;

            half popLum = dot(o.col.rgb, half3(0.299h, 0.587h, 0.114h));
            half3 overSat = popLum + (o.col.rgb - popLum) * (1 + _PopSaturation);
            o.col.rgb = lerp(o.col.rgb, overSat, ease);
            o.col.rgb += _PopColor * (_PopFlash * ease);
        }

        float4 clip = mul(UNITY_MATRIX_VP, float4(worldPos, 1));
        if (clip.w <= 0)
            discardSplat = true;

        o.style.x = styleAmount;
        o.style.y = o.col.a; // opacity
        bool flipOn = _StyleRandomFlip != 0;
        o.style.z = (flipOn && HashInstance(instID) > 0.5) ? -1.0 : 1.0;
        o.style.w = flipOn
            ? (HashInstance(instID ^ 0x9E3779B9u) * 2.0 - 1.0) * _StyleFlipJitter * 1.5707963
            : 0.0;

        uint idx = vtxID;
        float2 quadPos = float2(idx & 1, (idx >> 1) & 1) * 2.0 - 1.0;
        quadPos *= 2;
        o.pos = quadPos;

        float2 deltaScreenPos = (quadPos.x * axis1 + quadPos.y * axis2) * 2 / _ScreenParams.xy;
        o.vertex = clip;
        o.vertex.xy += deltaScreenPos * clip.w;

        if (_SplatBitsValid)
        {
            uint wordIdx = instID / 32;
            uint bitIdx = instID & 31;
            uint selVal = _SplatSelectedBits.Load(wordIdx * 4);
            if (selVal & (1 << bitIdx))
                o.col.a = -1;
        }

        if (discardSplat)
            o.vertex = asfloat(0x7fc00000);
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

    // stylized brush stroke: sample the brush texture, cut/gamma-shape its
    // alpha, then blend stroke <-> gaussian by styleAmount (1 = gaussian dot,
    // used while dormant; size-based when painted).
    float2 uv = i.pos * 0.25 + 0.5;
    if (i.style.z < 0)
        uv.x = 1 - uv.x;
    if (i.style.w != 0)
    {
        float s, c;
        sincos(i.style.w, s, c);
        float2 d = uv - 0.5;
        uv = float2(d.x * c - d.y * s, d.x * s + d.y * c) + 0.5;
    }
    half brush = _StylizedBrushTex.Sample(sampler_StylizedBrushTex, uv).a;
    brush *= all(uv == saturate(uv)) ? 1 : 0;
    half strokeAlpha = saturate((brush - _StyleAlphaCut) / max(1 - _StyleAlphaCut, 1e-4));
    strokeAlpha = pow(strokeAlpha, max(_StyleAlphaGamma, 1e-3));
    half opacity = i.style.y;
    strokeAlpha *= opacity;

    half finalAlpha = lerp(strokeAlpha, saturate(gaussAlpha * opacity), i.style.x);

    i.col = ApplySelectedTint(i.col, finalAlpha);
    if (i.col.a < 1.0/255.0)
        discard;

    return half4(i.col.rgb * i.col.a, i.col.a);
}
ENDCG
        }
    }
}
