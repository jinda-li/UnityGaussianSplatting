using UnityEngine;
using GaussianSplatting.Runtime;

namespace EiffelMR
{
    // Drives TowerParticles.shader: how much of the tower is a drifting cloud
    // of its own splats and how much is the solid tower.
    //
    // Solidify == 0 is the bubble: sparse, tinted, shimmering points that bob
    // around the tower's shape. Solidify == 1 is the plain splat, pixel for
    // pixel what the stock shader would draw. ThrownTower ramps between them
    // along the throw, so the condensing and the growing are the same event.
    //
    // The parameters are globals rather than material properties because the
    // splat renderer builds its own Material from the shader asset at runtime,
    // so there is no material in the project to set them on.
    // ExecuteAlways so the cloud is visible in the scene view. These are shader
    // globals with no material asset behind them, so without this there is
    // nothing to look at until Play mode - and a look that can only be judged
    // in Play is a look nobody tunes.
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class TowerParticles : MonoBehaviour
    {
        [Tooltip("Renderer whose shader is TowerParticles.shader. Only used to " +
                 "warn when it is not, since every parameter is global.")]
        public GaussianSplatRenderer m_Renderer;

        [Header("Dispersed look")]
        [Tooltip("Point size in SCREEN PIXELS. Screen space on purpose: the same " +
                 "tower is 29 cm in the hand and 324 m around you, and no " +
                 "world-space size is legible across that range.")]
        [Range(1f, 10f)] public float m_ParticleSize = 2.6f;

        [Tooltip("Fraction of points drawn while fully dispersed. Under about a " +
                 "third it reads as sampled data rather than a solid object, and " +
                 "it is also what keeps the overdraw down on a Quest.")]
        [Range(0.02f, 1f)] public float m_ParticleFraction = 0.3f;

        [Tooltip("Drift amplitude in SPLAT-LOCAL units - for a 324 m tower, 3 is " +
                 "about 1% of its height. Local space, so the drift is the same " +
                 "fraction of the tower whether it is in the hand or at 1:1.")]
        [Min(0f)] public float m_Drift = 3.0f;

        [Min(0f)] public float m_DriftSpeed = 0.9f;

        [Tooltip("Slow upward creep, so the cloud is alive even when the tower " +
                 "and the player are both still.")]
        [Min(0f)] public float m_Swirl = 1.2f;

        [ColorUsage(false, true)] public Color m_Tint = new Color(0.10f, 0.22f, 0.42f);

        [Tooltip("Brightness shimmer while dispersed.")]
        [Range(0f, 1f)] public float m_Sparkle = 0.35f;

        [Tooltip("Width of each splat's own solidify window, as a fraction of " +
                 "the whole ramp. At 1 every point converges at once, which " +
                 "reads as the object being scaled rather than assembled.")]
        [Range(0.05f, 1f)] public float m_Stagger = 0.45f;

        [Header("State")]
        [Tooltip("0 = cloud of particles, 1 = the solid tower.")]
        [Range(0f, 1f)] public float m_Solidify;

        static class Props
        {
            public static readonly int Solidify = Shader.PropertyToID("_TowerSolidify");
            public static readonly int Stagger = Shader.PropertyToID("_TowerStagger");
            public static readonly int ParticleSize = Shader.PropertyToID("_TowerParticleSize");
            public static readonly int ParticleFraction = Shader.PropertyToID("_TowerParticleFraction");
            public static readonly int Drift = Shader.PropertyToID("_TowerDrift");
            public static readonly int DriftSpeed = Shader.PropertyToID("_TowerDriftSpeed");
            public static readonly int Swirl = Shader.PropertyToID("_TowerSwirl");
            public static readonly int Tint = Shader.PropertyToID("_TowerParticleTint");
            public static readonly int Sparkle = Shader.PropertyToID("_TowerSparkle");
        }

        /// 0 = drifting cloud, 1 = solid tower.
        public float Solidify
        {
            get => m_Solidify;
            set
            {
                m_Solidify = Mathf.Clamp01(value);
                Apply();
            }
        }

        void OnEnable() => Apply();
        void OnValidate() { if (isActiveAndEnabled) Apply(); }

        // Shader globals outlive this component - a scene change leaves the last
        // values set, and any other splat drawn with this shader would inherit
        // them. Put them back to "solid, no drift" on the way out.
        void OnDisable() => ResetGlobals();

        void Update()
        {
            // The drift is animated in the shader off _Time, but the globals
            // still have to be resent every frame: anything else in the scene
            // that sets them wins until the next Apply.
            Apply();
        }

        public void Apply()
        {
            Shader.SetGlobalFloat(Props.Solidify, m_Solidify);
            Shader.SetGlobalFloat(Props.Stagger, m_Stagger);
            Shader.SetGlobalFloat(Props.ParticleSize, m_ParticleSize);
            Shader.SetGlobalFloat(Props.ParticleFraction, m_ParticleFraction);
            Shader.SetGlobalFloat(Props.Drift, m_Drift);
            Shader.SetGlobalFloat(Props.DriftSpeed, m_DriftSpeed);
            Shader.SetGlobalFloat(Props.Swirl, m_Swirl);
            Shader.SetGlobalColor(Props.Tint, m_Tint);
            Shader.SetGlobalFloat(Props.Sparkle, m_Sparkle);
        }

        public static void ResetGlobals()
        {
            Shader.SetGlobalFloat(Props.Solidify, 1f);
            Shader.SetGlobalFloat(Props.Stagger, 1f);
            Shader.SetGlobalFloat(Props.ParticleSize, 1f);
            Shader.SetGlobalFloat(Props.ParticleFraction, 1f);
            Shader.SetGlobalFloat(Props.Drift, 0f);
            Shader.SetGlobalFloat(Props.DriftSpeed, 0f);
            Shader.SetGlobalFloat(Props.Swirl, 0f);
            Shader.SetGlobalColor(Props.Tint, Color.black);
            Shader.SetGlobalFloat(Props.Sparkle, 0f);
        }
    }
}
