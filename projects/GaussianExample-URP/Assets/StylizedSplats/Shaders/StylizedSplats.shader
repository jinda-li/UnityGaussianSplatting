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
float _StyleFlipJitter;
float _BaseSaturation;
float _BaseLift;
float _StylizedPreviewPainted;

// size-cull extension globals (independent of _StylizedEnable)
float _SizeCullEnable;
float _SizeCullMax;

Texture2D _StylizedBrushTex;
SamplerState sampler_StylizedBrushTex;

StructuredBuffer<float> _SplatPaintProgress;
uint _SplatPaintValid;

// world-reveal globals (set by SplatWorldReveal.cs)
float  _RevealEnable;
float3 _RevealCenter;
float  _RevealRadius;
float  _RevealEdgeWidth;
float  _RevealSizeOvershoot;
float  _RevealFlashIntensity;
float  _RevealRippleAmplitude;
float  _RevealRippleFrequency;
float  _RevealRippleSpeed;
float  _RevealBobAmplitude;
half3  _RevealUnrealTint;
float  _RevealUnrealSaturation;
half3  _RevealEdgeColor;       // HDR-capable (values may exceed 1)
float  _RevealVanguardDistance;
float  _RevealFireflyFraction;
float  _RevealFireflySize;     // pixels
float  _RevealFireflyTwinkleSpeed;
float  _RevealFireflyIntensity; // global swarm fade-in, 0 at reveal start
half3  _RevealFireflyColor;    // HDR-capable

struct v2f
{
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
    half4 style : TEXCOORD1; // x = styleAmount (0=stylized,1=gaussian), y = opacity, z = flip sign, w = stroke rotation (radians)
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

        // reveal may rewrite geometry inputs without touching the raw view data
        float4 centerClipPos2 = centerClipPos;
        float2 axis1 = view.axis1, axis2 = view.axis2;
        bool revealDiscard = false;

        // world-space splat size, needed by both the stylize gradient and the
        // size-cull feature below; computed once and shared between them.
        float worldSize = 0;
        bool sizeCullDiscard = false;
        if (_StylizedEnable != 0 || _SizeCullEnable != 0)
        {
            SplatData splat = LoadSplatData(instID);
            float worldScale = length(unity_ObjectToWorld._m00_m10_m20);
            worldSize = max(max(splat.scale.x, splat.scale.y), splat.scale.z) * worldScale;
        }
        if (_SizeCullEnable != 0 && worldSize > _SizeCullMax)
            sizeCullDiscard = true;

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
            if (_StylizedPreviewPainted != 0)
                paint = 1; // editor preview: skip base saturation/lift entirely
            o.col.rgb = lerp(baseCol, o.col.rgb, saturate(paint));

            styleAmount = SmoothStepEdge(_StyleSizeMin, _StyleSizeMax, worldSize);
        }

        // world-materialization reveal: expanding wavefront from _RevealCenter.
        // Lives outside the _StylizedEnable gate so it works in both modes.
        if (_RevealEnable != 0)
        {
            float3 worldPos = mul(unity_ObjectToWorld, float4(LoadSplatPos(instID), 1)).xyz;
            float  d = distance(worldPos, _RevealCenter);
            float  p = saturate((_RevealRadius - d) / max(_RevealEdgeWidth, 1e-4));
            float  hash = HashInstance(instID);

            if (p >= 1)
            {
                // fully revealed: untouched -> bit-exact original
            }
            else if (p <= 0)
            {
                // ahead of the wavefront: hidden, except vanguard fireflies.
                // A firefly must not pop in at full brightness the instant it
                // enters the band, so it fades over the band: 0 at the outer
                // rim, 1 at the wavefront. _RevealFireflyIntensity fades the
                // whole swarm up from nothing at the start of the reveal (and
                // back down at the end of a reverse), which is what keeps the
                // first frames from flashing a ball of fireflies at the center.
                float vg = saturate((_RevealRadius + _RevealVanguardDistance - d)
                                    / max(_RevealVanguardDistance, 1e-4));
                float fade = vg * vg * _RevealFireflyIntensity;
                bool firefly = (fade > 1e-3) && (hash < _RevealFireflyFraction);
                if (!firefly)
                {
                    revealDiscard = true;
                }
                else
                {
                    // never shrink to a sub-pixel speck - that aliases into
                    // its own kind of flicker
                    float size = _RevealFireflySize * lerp(0.35, 1, fade);
                    axis1 = float2(size, 0);
                    axis2 = float2(0, size);
                    float tw = 0.5 + 0.5 * sin(_Time.y * _RevealFireflyTwinkleSpeed * (0.7 + 0.6 * hash)
                                               + hash * 6.2831853);
                    o.col.rgb = _RevealFireflyColor;
                    o.col.a   = tw * tw * fade;
                    styleAmount = 1; // force gaussian path, never a warped brush stroke
                }
            }
            else
            {
                // birth animation, 0 < p < 1
                float sizeScale = p * (1 + _RevealSizeOvershoot * sin(p * 3.14159265));
                axis1 *= sizeScale;
                axis2 *= sizeScale;

                float edgeW = sin(p * 3.14159265); // peaks mid-birth, 0 at both ends
                half  lum = dot(o.col.rgb, half3(0.299h, 0.587h, 0.114h));
                half3 unreal = lerp(lum * _RevealUnrealTint, o.col.rgb, _RevealUnrealSaturation);
                o.col.rgb = lerp(unreal, o.col.rgb, p);
                o.col.rgb += _RevealEdgeColor * (edgeW * _RevealFlashIntensity);
                o.col.a *= p;

                float decay = 1 - p;
                float wavePhase = d * _RevealRippleFrequency - _Time.y * _RevealRippleSpeed
                                + hash * 6.2831853;
                float3 radialDir = (worldPos - _RevealCenter) / max(d, 1e-4);
                float3 offset = radialDir * (sin(wavePhase) * _RevealRippleAmplitude * decay)
                              + float3(0, 1, 0) * (sin(_Time.y * _RevealRippleSpeed * 0.7
                                                     + hash * 6.2831853) * _RevealBobAmplitude * decay);
                centerClipPos2 = mul(UNITY_MATRIX_VP, float4(worldPos + offset, 1));
                if (centerClipPos2.w <= 0)
                    revealDiscard = true;
            }
        }

        o.style.x = styleAmount;
        o.style.y = o.col.a; // opacity
        bool flipOn = _StyleRandomFlip != 0;
        o.style.z = (flipOn && HashInstance(instID) > 0.5) ? -1.0 : 1.0;
        // extra per-splat stroke rotation, applied only while the flip is on, so
        // toggling the flip wobbles the strokes instead of just mirroring them
        o.style.w = flipOn
            ? (HashInstance(instID ^ 0x9E3779B9u) * 2.0 - 1.0) * _StyleFlipJitter * 1.5707963
            : 0.0;

        uint idx = vtxID;
        float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
        quadPos *= 2;

        o.pos = quadPos;

        float2 deltaScreenPos = (quadPos.x * axis1 + quadPos.y * axis2) * 2 / _ScreenParams.xy;
        o.vertex = centerClipPos2;
        o.vertex.xy += deltaScreenPos * centerClipPos2.w;

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

        if (revealDiscard || sizeCullDiscard)
            o.vertex = asfloat(0x7fc00000); // NaN discards the primitive
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
    if (i.style.w != 0)
    {
        float s, c;
        sincos(i.style.w, s, c);
        float2 d = uv - 0.5;
        uv = float2(d.x * c - d.y * s, d.x * s + d.y * c) + 0.5;
    }
    half brush = _StylizedBrushTex.Sample(sampler_StylizedBrushTex, uv).a;
    // rotation can push the corners outside the brush quad; don't let the
    // sampler's edge/wrap behaviour smear the stroke
    brush *= all(uv == saturate(uv)) ? 1 : 0;
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
