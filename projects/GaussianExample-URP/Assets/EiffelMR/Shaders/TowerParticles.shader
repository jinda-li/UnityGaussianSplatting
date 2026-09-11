// "Tower Particles" - the splat render shader used while the tower is a
// miniature in the bubble.
//
// The tower in the bubble is not a model of a tower, it is the splat. A
// gaussian splat already IS a cloud of particles, so showing it as drifting
// points is not a different object being substituted in - it is the same data,
// drawn as the raw samples it is made of. That is the whole reason this is a
// render shader on the splat rather than a Unity particle system next to it:
// every point that drifts in the bubble is a point that ends up in the world
// the player lands in.
//
// _TowerSolidify drives the whole thing:
//   0  small uniform screen-space dots, sparse, drifting, tinted
//   1  the splat's real anisotropic gaussians, still, untinted
// ThrownTower ramps it along the throw, so the tower condenses out of its own
// particles while it grows.
//
// Assign to the GaussianSplatRenderer's "Shader Splats" field; every parameter
// is a global set by TowerParticles.cs.
Shader "Gaussian Splatting/Tower Particles"
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

float _TowerSolidify;        // 0 = particles, 1 = real splats
float _TowerStagger;         // 0..1 width of the per-splat solidify window
float _TowerParticleSize;    // screen pixels, so it is immune to the 0.0009 table scale
float _TowerParticleFraction;// fraction of points drawn while fully dispersed
float _TowerDrift;           // SPLAT-LOCAL units, so drift is a constant fraction of the tower
float _TowerDriftSpeed;
float _TowerSwirl;           // vertical rise, splat-local units per second
half3 _TowerParticleTint;    // additive, fades out as the splat solidifies
float _TowerSparkle;         // brightness shimmer while dispersed

struct v2f
{
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
    half opacity : TEXCOORD1;
    float4 vertex : SV_POSITION;
};

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
    if (centerClipPos.w <= 0)
    {
        o.vertex = asfloat(0x7fc00000); // NaN discards the primitive
        FlipProjectionIfBackbuffer(o.vertex);
        return o;
    }

    o.col.r = f16tof32(view.color.x >> 16);
    o.col.g = f16tof32(view.color.x);
    o.col.b = f16tof32(view.color.y >> 16);
    o.col.a = f16tof32(view.color.y);

    float2 axis1 = view.axis1, axis2 = view.axis2;
    bool discardSplat = false;

    float h = HashInstance(instID);

    // Stagger, so the tower condenses point by point instead of every splat
    // inflating in lockstep - a uniform ramp reads as one object being scaled,
    // which is exactly the swap this whole design exists to avoid.
    float w = max(_TowerStagger, 1e-3);
    float from = h * (1.0 - w);
    float lit = saturate((_TowerSolidify - from) / w);
    lit = lit * lit * (3.0 - 2.0 * lit);

    float loose = 1.0 - lit;

    // Sparse while dispersed. Points come back in as they solidify, so the
    // cloud thickens into the tower rather than the tower fading up.
    if (loose > 0.999 && HashInstance(instID ^ 0x1234567u) > _TowerParticleFraction)
        discardSplat = true;

    // Drift in SPLAT-LOCAL space, before the object transform. The miniature
    // sits at ~0.0009 world scale and ends at 1.0; a world-space offset that
    // looks right in the hand would be three orders of magnitude too small once
    // the world is at 1:1. Local-space drift is the same fraction of the tower
    // at every size.
    float3 localPos = LoadSplatPos(instID);
    float ph = h * 6.2831853;
    float3 drift = float3(
        sin(_Time.y * _TowerDriftSpeed        + ph),
        sin(_Time.y * _TowerDriftSpeed * 1.13 + ph * 2.0),
        cos(_Time.y * _TowerDriftSpeed * 0.87 + ph * 0.5)) * (_TowerDrift * loose);
    // slow rise, wrapped, so the cloud is never static even when nothing moves
    drift.y += frac(_Time.y * _TowerSwirl * (0.5 + h) * 0.1) * (_TowerDrift * 2.0 * loose);
    float3 worldPos = mul(unity_ObjectToWorld, float4(localPos + drift, 1)).xyz;

    o.col.rgb += _TowerParticleTint * loose;
    o.col.rgb *= 1.0 + _TowerSparkle * loose * sin(_Time.y * 3.7 + ph * 3.0);

    // Uniform screen-space dot while dispersed, real anisotropic gaussian once
    // solid. Screen-space size means a 29 cm miniature and a 324 m tower both
    // draw legible particles; a world-space size does not survive that range.
    float2 pointAxis1 = float2(_TowerParticleSize, 0);
    float2 pointAxis2 = float2(0, _TowerParticleSize);
    axis1 = lerp(pointAxis1, axis1, lit);
    axis2 = lerp(pointAxis2, axis2, lit);

    float4 clip = mul(UNITY_MATRIX_VP, float4(worldPos, 1));
    if (clip.w <= 0)
        discardSplat = true;

    o.opacity = o.col.a;

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
    half gaussAlpha = exp(-dot(i.pos, i.pos));
    i.col = ApplySelectedTint(i.col, saturate(gaussAlpha * i.opacity));
    if (i.col.a < 1.0/255.0)
        discard;
    return half4(i.col.rgb * i.col.a, i.col.a);
}
ENDCG
        }
    }
}
