// SPDX-License-Identifier: MIT
Shader "Hidden/Gaussian Splatting/Composite"
{
    SubShader
    {
        // Pass 0: sorted path composite (un-premultiply + coverage alpha)
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc
#include "UnityCG.cginc"

struct v2f
{
    float4 vertex : SV_POSITION;
};

v2f vert (uint vtxID : SV_VertexID)
{
    v2f o;
    float2 quadPos = float2(vtxID&1, (vtxID>>1)&1) * 4.0 - 1.0;
	o.vertex = float4(quadPos, 1, 1);
    return o;
}

Texture2D _GaussianSplatRT;

half4 frag (v2f i) : SV_Target
{
    half4 col = _GaussianSplatRT.Load(int3(i.vertex.xy, 0));
    return float4(GammaToLinearSpace(col.rgb/col.a),col.a);
}
ENDCG
        }

        // Pass 1: Mobile-GS OIT composite
        // C = (1-T) * Σ(c·α·w)/Σ(α·w) + T * c_bg
        // where T = exp(Σ log(1-α)), and background comes from dst blend
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc
#include "UnityCG.cginc"

struct v2f
{
    float4 vertex : SV_POSITION;
};

v2f vert (uint vtxID : SV_VertexID)
{
    v2f o;
    float2 quadPos = float2(vtxID&1, (vtxID>>1)&1) * 4.0 - 1.0;
    o.vertex = float4(quadPos, 1, 1);
    return o;
}

Texture2D _GaussianSplatRT;
Texture2D _GaussianSplatRevealRT;

half4 frag (v2f i) : SV_Target
{
    int3 loc = int3(i.vertex.xy, 0);
    half4 accum = _GaussianSplatRT.Load(loc);
    half logT = _GaussianSplatRevealRT.Load(loc).r;

    float T = exp((float)logT);
    float invW = 1.0 / max((float)accum.a, 1e-5);
    float3 avg = accum.rgb * invW;
    float coverage = 1.0 - T;

    return half4(GammaToLinearSpace(avg), coverage);
}
ENDCG
        }
    }
}
