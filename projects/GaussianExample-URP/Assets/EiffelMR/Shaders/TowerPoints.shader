// The mesh-world tower as glowing points (see TowerPointCloud.cs).
//
// Each point is four vertices at the same position; UV0 is the corner, UV1 two
// per-point randoms. The quad is expanded in clip space to a size in screen
// pixels, so the cloud is legible at 29 cm and at 324 m alike.
//
// Dispersed (_Solidify 0) the tower is warm light: gilded motes whose colour
// runs from bronze at the piers to pale champagne at the spire, twinkling on
// their own clocks, breathing and slowly swirling about the tower's axis, with
// a few embers lifting off and drifting up out of the lattice.
//
// Each mote is a solid core over an additive halo (premultiplied alpha: the
// core occludes, the halo only adds). Purely additive motes were tried first
// and could not work: the lower tower has far more surface than the spire, so
// its points pile up dozens deep per pixel and any brightness that makes one
// mote visible turns the piers into a white blob - measured, 70% of the
// tower's pixels were clipped whatever the glow was set to. A solid core
// saturates to its own gold instead of to white; the halo gives the glow the
// project cannot get from bloom (it renders without HDR, for the Quest), and
// the halo alone is scaled by the tower's size on screen so a distant cloud
// does not haze over.
//
// Condensing (_Solidify -> 1) every point stops, lands on the exact surface
// point it was sampled from and loses its light, while TowerDissolve brings
// the solid tower in beneath it. Additive points with no light are invisible,
// so the handover needs no separate fade.
Shader "EiffelMR/TowerPoints"
{
    Properties
    {
        _PointSize ("Point size (px)", Float) = 1.15
        _Fraction ("Dispersed fraction", Range(0,1)) = 0.6
        _Drift ("Drift (object units)", Float) = 2.5
        _DriftSpeed ("Drift speed", Float) = 0.7
        _Sparkle ("Twinkle", Range(0,1)) = 0.6
        _Stagger ("Stagger", Range(0.05,1)) = 0.45
        _Solidify ("Solidify", Range(0,1)) = 0

        _ColorBase ("Colour at the piers", Color) = (1.0, 0.66, 0.28, 1)
        _ColorTop ("Colour at the spire", Color) = (1.0, 0.85, 0.52, 1)
        _Glow ("Glow", Range(0, 2)) = 1.0
        _BronzeGain ("Bronze brightness", Range(0, 4)) = 0.8
        _WaveSpeed ("Light run speed", Float) = 0.16
        _WaveSharp ("Light band sharpness", Float) = 7
        _WaveGain ("Light band strength", Range(0, 2)) = 1.3
        _FreeFraction ("Free floaters", Range(0, 0.5)) = 0.05
        _FreeDrift ("Free floater drift (x _Drift)", Float) = 2.5
        _AnchorDrift ("Anchored shimmer (x _Drift)", Float) = 0.05
        _BackShade ("Far side darkening", Range(0, 1)) = 0.45
        _Swirl ("Swirl (degrees)", Range(0, 30)) = 7
        _EmberFraction ("Ember fraction", Range(0, 0.4)) = 0.07
        _EmberRise ("Ember rise (object units)", Float) = 60
        _EmberSpeed ("Ember speed", Float) = 0.12
        _UpOS ("Tower up, object space", Vector) = (0, 0, 1, 0)
        _Height ("Tower height, object units", Float) = 324
        _DensityRef ("Object units per metre of view distance at full halo", Float) = 440
        _Halo ("Halo", Range(0, 1)) = 0.16
        _CoreOpacity ("Core opacity", Range(0, 1)) = 0.85
    }
    SubShader
    {
        // After the bubble. The bubble refracts the opaque texture, which does
        // not contain these points, so drawn before it they were overwritten by
        // a picture of the room with no tower in it - which is why the first
        // cloud read as grey sand behind frosted glass.
        Tags { "RenderType"="Transparent" "Queue"="Transparent+20" "RenderPipeline"="UniversalPipeline" "IgnoreProjector"="True" }
        Pass
        {
            Name "Points"
            Tags { "LightMode"="UniversalForward" }
            ZWrite Off
            Cull Off
            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _PointSize;
                float _Fraction;
                float _Drift;
                float _DriftSpeed;
                float _Sparkle;
                float _Stagger;
                float _Solidify;
                half4 _ColorBase;
                half4 _ColorTop;
                float _Glow;
                float _Swirl;
                float _EmberFraction;
                float _EmberRise;
                float _EmberSpeed;
                float4 _UpOS;
                float _Height;
                float _DensityRef;
                float _Halo;
                float _CoreOpacity;
                float _FreeFraction;
                float _FreeDrift;
                float _AnchorDrift;
                float _BackShade;
                float _BronzeGain;
                float _WaveSpeed;
                float _WaveSharp;
                float _WaveGain;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;
                float2 corner : TEXCOORD0;
                float2 rand : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float2 corner : TEXCOORD0;
                float2 look : TEXCOORD1;   // x = halo gain, y = core opacity
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float Hash(float x) { return frac(sin(x * 127.1) * 43758.5453); }

            float3 RotateAbout(float3 p, float3 axis, float a)
            {
                float s = sin(a), c = cos(a);
                return p * c + cross(axis, p) * s + axis * dot(axis, p) * (1.0 - c);
            }

            Varyings vert (Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float h = v.rand.x;
                float h2 = Hash(h * 91.7 + 3.1);
                float h3 = Hash(h * 13.3 + 7.7);

                // Each point settles in its own window, so the tower condenses
                // point by point instead of every mote freezing at once.
                float w = max(_Stagger, 1e-3);
                float lit = saturate((_Solidify - h * (1.0 - w)) / w);
                lit = lit * lit * (3.0 - 2.0 * lit);
                float loose = 1.0 - lit;

                float3 up = normalize(_UpOS.xyz);
                float3 p = v.positionOS.xyz;
                float height01 = saturate(dot(p, up) / max(_Height, 1e-3));
                float t = _Time.y;

                // --- floating -----------------------------------------------
                // Two kinds of point. Most are anchored to the steel and only
                // shimmer in place - a lattice is mostly holes, and a cloud in
                // which every point wanders fills them in until the tower reads
                // as a solid statue of sand. A minority float free: they drift
                // several metres and turn slowly about the tower's axis, which
                // is what makes the whole thing read as suspended in the air.
                bool isFree = h2 >= _EmberFraction && h2 < _EmberFraction + _FreeFraction;
                float3 axisPt = up * dot(p, up);
                if (isFree)
                {
                    float swirl = radians(_Swirl) * sin(t * 0.35 + h * 6.28318)
                                  * (0.35 + 0.65 * height01);
                    p = axisPt + RotateAbout(p - axisPt, up, swirl * loose);
                }
                float ph = h * 6.28318;
                float3 drift = float3(sin(t * _DriftSpeed + ph),
                                      sin(t * _DriftSpeed * 1.13 + ph * 2.0),
                                      cos(t * _DriftSpeed * 0.87 + ph * 0.5));
                p += drift * (_Drift * loose * (isFree ? _FreeDrift : _AnchorDrift));

                // Embers: a few points lift off, rise and fade, then start again.
                bool ember = h2 < _EmberFraction;
                float emberFade = 1.0;
                if (ember)
                {
                    float cyc = frac(t * _EmberSpeed * (0.6 + h3) + h);
                    float3 radial = p - axisPt;
                    float radialLen = max(length(radial), 1e-3);
                    p += (up * (cyc * _EmberRise) + radial / radialLen * (cyc * _EmberRise * 0.35)) * loose;
                    emberFade = smoothstep(0.0, 0.15, cyc) * (1.0 - smoothstep(0.55, 1.0, cyc));
                }

                // --- visibility ---------------------------------------------
                // Sparse while dispersed; the rest arrive as the cloud condenses.
                float visible = saturate(step(h, _Fraction) + (ember || isFree ? 1.0 : 0.0) + lit);

                // --- colour -------------------------------------------------
                // The tower itself is dark bronze, lit: the points carry the
                // mesh's own albedo shaded by the sun at sampling time, so the
                // four faces, the platforms and the arches come out as light
                // and shade. Glowing every point gold was tried first and read
                // as a filled silhouette - at 29 cm the lattice is sub-pixel,
                // so the only way to show the structure is the shading of it.
                half3 gold = lerp(_ColorBase.rgb, _ColorTop.rgb, height01);
                half3 bronze = v.color.rgb * _BronzeGain * lerp(half3(1,1,1), gold, 0.35);

                // Light that runs up the tower: a soft band, restarting at the
                // piers, lighting the points it passes gold.
                float wave = frac(t * _WaveSpeed);
                float band = exp(-pow((height01 - wave * 1.25 + 0.1) * _WaveSharp, 2.0));
                // Twinkle on the point's own clock, plus the occasional glint.
                float tw = 0.62 + 0.38 * sin(t * (1.5 + 3.0 * h3) + h2 * 40.0);
                float glint = pow(saturate(sin(t * (0.7 + h2) + h * 57.0)), 24.0) * 3.0 * _Sparkle;
                float lightUp = saturate(band * _WaveGain * lerp(1.0, tw, _Sparkle) + glint
                                         + (ember || isFree ? 0.85 : 0.0));
                half3 lightCol = gold * _Glow * (1.0 + glint * 0.5);
                half3 warm = lerp(bronze * lerp(1.0, tw, _Sparkle * 0.4), lightCol, lightUp);
                half3 col = warm * loose * emberFade * visible;

                // --- size ---------------------------------------------------
                float size = _PointSize * lerp(0.6, 1.35, h3 * h3) * (1.0 + 0.6 * glint)
                             * (ember ? 1.4 : (isFree ? 1.2 : 1.0)) * (0.35 + 0.65 * loose);

                // Volume: the half of the cloud facing away from the viewer is
                // darker, so the tower has a near side and a far side instead of
                // reading as a flat cut-out.
                float3 radialWS = TransformObjectToWorldDir(p - axisPt);
                float3 toCam = GetWorldSpaceViewDir(TransformObjectToWorld(p));
                float facing = dot(normalize(radialWS + 1e-5), normalize(toCam));
                col *= lerp(1.0 - _BackShade, 1.0, saturate(facing * 0.5 + 0.5));

                float4 cs = TransformObjectToHClip(p);

                // The halo is additive, so how many overlap on one pixel
                // matters: points are a fixed size in pixels, and the overlap
                // goes with the square of how small the tower is on screen.
                // Scale the halo by the tower's projected size squared (object
                // scale over view distance), normalised to the close-up.
                float objScale = length(unity_ObjectToWorld._m00_m10_m20);
                float proj = objScale * _DensityRef / max(cs.w, 1e-4);
                float haloGain = _Halo * clamp(proj * proj, 0.05, 1.0) * lightUp;
                // Cores fade with the light as the point lands on the surface.
                float lightAmt = saturate(loose * emberFade * visible);
                o.color = half4(col, 1);
                o.look = float2(haloGain, _CoreOpacity * lightAmt);

                cs.xy += v.corner * size * 2.0 / _ScreenParams.xy * cs.w;
                o.positionCS = cs;
                o.corner = v.corner;
                if (dot(col, col) <= 1e-6)
                    o.positionCS = float4(0, 0, -2, 1);   // outside the clip volume
                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                float r2 = dot(i.corner, i.corner);
                half core = exp(-7.0 * r2);
                half halo = exp(-3.0 * r2) * i.look.x;
                half a = core * i.look.y;
                clip(core + halo - 0.004);
                // premultiplied: core occludes what is behind it, halo adds
                return half4(i.color.rgb * (core + halo), a);
            }
            ENDHLSL
        }
    }
}
