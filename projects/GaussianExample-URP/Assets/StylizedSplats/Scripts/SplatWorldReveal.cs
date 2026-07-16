using UnityEngine;

namespace StylizedSplats
{
    // Cinematic "world materialization" reveal for gaussian splats: an expanding
    // spherical wavefront from m_Center/m_CenterTransform that fades, ripples and
    // color-grades each splat into existence. Pure shader-global driver (like
    // StylizedSplatsController) - no package access, no buffers. Lives outside the
    // _StylizedEnable gate in StylizedSplats.shader, so it works whether stylized
    // mode is on or off. Absent or disabled, _RevealEnable defaults to 0 and
    // rendering is untouched.
    public class SplatWorldReveal : MonoBehaviour
    {
        [Header("Playback")]
        public bool m_PlayOnStart = true;
        [Tooltip("Seconds from radius 0 to Max Radius")]
        public float m_Duration = 8f;
        public float m_StartDelay = 0.5f;
        [Tooltip("Must exceed the distance from Center to the farthest splat + Edge Width, or splats will pop when the effect ends")]
        public float m_MaxRadius = 30f;
        public AnimationCurve m_RadiusCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);
        [Tooltip("Optional transform used as wave origin; falls back to Center")]
        public Transform m_CenterTransform;
        public Vector3 m_Center;

        [Header("Wavefront")]
        public float m_EdgeWidth = 2.5f;
        [Range(0f, 2f)] public float m_SizeOvershoot = 0.6f;
        [Range(0f, 4f)] public float m_FlashIntensity = 1.2f;

        [Header("Water Ripple")]
        public float m_RippleAmplitude = 0.08f;
        public float m_RippleFrequency = 2.5f;
        public float m_RippleSpeed = 4f;
        public float m_BobAmplitude = 0.04f;

        [Header("Colors")]
        [ColorUsage(false, true)] public Color m_UnrealTint = new Color(0.10f, 0.20f, 0.60f);
        [Range(0f, 1f)] public float m_UnrealSaturation = 0.25f;
        [Tooltip("HDR values >1 will bloom if the pipeline has HDR + Bloom enabled")]
        [ColorUsage(false, true)] public Color m_EdgeColor = new Color(0.35f, 1.8f, 2.0f);
        [ColorUsage(false, true)] public Color m_FireflyColor = new Color(0.6f, 2.2f, 2.4f);

        [Header("Vanguard Fireflies")]
        public float m_VanguardDistance = 4f;
        [Range(0f, 0.2f)] public float m_FireflyFraction = 0.03f;
        public float m_FireflySizePixels = 1.5f;
        public float m_FireflyTwinkleSpeed = 7f;

        float m_Elapsed;
        bool m_Playing;
        bool m_Finished;
        bool m_Reversed;

        Vector3 CenterWS => m_CenterTransform != null ? m_CenterTransform.position : m_Center;

        void OnEnable()
        {
            // Push the hidden state immediately so frame 0 never flashes the
            // full scene: OnEnable runs before any camera renders this frame.
            if (m_PlayOnStart)
            {
                m_Elapsed = -m_StartDelay;
                m_Playing = true;
                m_Finished = false;
                m_Reversed = false;
            }
            PushGlobals(CurrentRadius());
        }

        void OnDisable()
        {
            // Never leave the scene hidden via a stale global.
            Shader.SetGlobalFloat(Props.RevealEnable, 0f);
        }

        void Update()
        {
            if (m_Playing)
            {
                m_Elapsed += Time.deltaTime;
                if (m_Elapsed >= m_Duration)
                {
                    m_Playing = false;
                    m_Finished = true;
                }
            }
            PushGlobals(CurrentRadius());
        }

        float HiddenRadius => -(m_VanguardDistance + m_EdgeWidth + 1f);

        float CurrentRadius()
        {
            if (m_Elapsed < 0f)
                return HiddenRadius; // start-delay: nothing visible yet

            if (m_Finished)
                return m_Reversed ? HiddenRadius : m_MaxRadius;

            float t = Mathf.Clamp01(m_Elapsed / Mathf.Max(m_Duration, 1e-4f));
            if (m_Reversed)
                t = 1f - t;
            return m_RadiusCurve.Evaluate(t) * m_MaxRadius;
        }

        void PushGlobals(float radius)
        {
            // Forward-finished settles into the exact original rendering via the
            // shader's own p>=1 branch, so the effect can switch off entirely
            // (zero-cost early-out). Reverse-finished must stay enabled, since a
            // negative radius is what keeps everything hidden.
            bool enable = m_Playing || (m_Finished && m_Reversed);
            Shader.SetGlobalFloat(Props.RevealEnable, enable && isActiveAndEnabled ? 1f : 0f);
            Shader.SetGlobalVector(Props.RevealCenter, CenterWS);
            Shader.SetGlobalFloat(Props.RevealRadius, radius);
            Shader.SetGlobalFloat(Props.RevealEdgeWidth, Mathf.Max(m_EdgeWidth, 1e-3f));
            Shader.SetGlobalFloat(Props.RevealSizeOvershoot, m_SizeOvershoot);
            Shader.SetGlobalFloat(Props.RevealFlashIntensity, m_FlashIntensity);
            Shader.SetGlobalFloat(Props.RevealRippleAmplitude, m_RippleAmplitude);
            Shader.SetGlobalFloat(Props.RevealRippleFrequency, m_RippleFrequency);
            Shader.SetGlobalFloat(Props.RevealRippleSpeed, m_RippleSpeed);
            Shader.SetGlobalFloat(Props.RevealBobAmplitude, m_BobAmplitude);
            Shader.SetGlobalVector(Props.RevealUnrealTint, m_UnrealTint);
            Shader.SetGlobalFloat(Props.RevealUnrealSaturation, m_UnrealSaturation);
            Shader.SetGlobalVector(Props.RevealEdgeColor, m_EdgeColor);
            Shader.SetGlobalFloat(Props.RevealVanguardDistance, m_VanguardDistance);
            Shader.SetGlobalFloat(Props.RevealFireflyFraction, m_FireflyFraction);
            Shader.SetGlobalFloat(Props.RevealFireflySize, m_FireflySizePixels);
            Shader.SetGlobalFloat(Props.RevealFireflyTwinkleSpeed, m_FireflyTwinkleSpeed);
            Shader.SetGlobalVector(Props.RevealFireflyColor, m_FireflyColor);
        }

        /// Plays the reveal forward from hidden to fully materialized.
        [ContextMenu("Play")]
        public void Play()
        {
            m_Elapsed = -m_StartDelay;
            m_Playing = true;
            m_Finished = false;
            m_Reversed = false;
            PushGlobals(CurrentRadius());
        }

        public void Replay() => Play();

        /// Plays the reveal backward: the world dissolves from its edges inward
        /// and back into vanguard fireflies, ending fully hidden.
        [ContextMenu("Play Reverse")]
        public void PlayReverse()
        {
            m_Elapsed = 0f;
            m_Playing = true;
            m_Finished = false;
            m_Reversed = true;
            PushGlobals(CurrentRadius());
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(CenterWS, Application.isPlaying ? Mathf.Max(CurrentRadius(), 0f) : m_MaxRadius);
        }

        static class Props
        {
            public const string RevealEnable = "_RevealEnable";
            public const string RevealCenter = "_RevealCenter";
            public const string RevealRadius = "_RevealRadius";
            public const string RevealEdgeWidth = "_RevealEdgeWidth";
            public const string RevealSizeOvershoot = "_RevealSizeOvershoot";
            public const string RevealFlashIntensity = "_RevealFlashIntensity";
            public const string RevealRippleAmplitude = "_RevealRippleAmplitude";
            public const string RevealRippleFrequency = "_RevealRippleFrequency";
            public const string RevealRippleSpeed = "_RevealRippleSpeed";
            public const string RevealBobAmplitude = "_RevealBobAmplitude";
            public const string RevealUnrealTint = "_RevealUnrealTint";
            public const string RevealUnrealSaturation = "_RevealUnrealSaturation";
            public const string RevealEdgeColor = "_RevealEdgeColor";
            public const string RevealVanguardDistance = "_RevealVanguardDistance";
            public const string RevealFireflyFraction = "_RevealFireflyFraction";
            public const string RevealFireflySize = "_RevealFireflySize";
            public const string RevealFireflyTwinkleSpeed = "_RevealFireflyTwinkleSpeed";
            public const string RevealFireflyColor = "_RevealFireflyColor";
        }
    }
}
