using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

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
        [Tooltip("Fraction of the reveal spent fading the swarm up from nothing, so the fireflies drift in at the center instead of flashing on. Fireflies also fade across the vanguard band itself: dim at its outer rim, full at the wavefront")]
        [Range(0f, 1f)] public float m_FireflyFadeIn = 0.25f;

        [Header("Audio")]
        [Tooltip("Played from the start of the reveal in BOTH directions. Leave the source empty to auto-create a 2D AudioSource here")]
        public AudioClip m_RevealClip;
        public AudioSource m_AudioSource;
        [Range(0f, 1f)] public float m_RevealVolume = 1f;

        [Header("Skybox Fade")]
        [Tooltip("Fades RenderSettings.skybox from pure black up to its authored colors as the reveal progresses, and back down on reverse")]
        public bool m_FadeSkybox = true;
        [Tooltip("Maps reveal progress (0 = hidden, 1 = materialized) to skybox brightness")]
        public AnimationCurve m_SkyboxCurve = AnimationCurve.Linear(0, 0, 1, 1);
        [Tooltip("Also scales ambient/reflection intensity, so scene lighting darkens together with the sky")]
        public bool m_FadeAmbientLight = true;

        [Header("Debug Toggle Input")]
        [Tooltip("Press to play forward when hidden, reverse when revealed. Leave the action unwired to use the built-in bindings: left controller menu button + keyboard R")]
        public InputActionProperty m_ToggleAction;

        float m_Elapsed;
        bool m_Playing;
        bool m_Finished;
        bool m_Reversed;
        // False until the first playback, so a scene with m_PlayOnStart off is
        // left fully lit instead of being blacked out by the skybox fade.
        bool m_HasPlayed;
        // Set when a forward play is still sitting in its start delay: the
        // sound should land with the first fireflies, not with the button press.
        bool m_AudioPending;

        InputAction m_Toggle;

        // Skybox fade state: we never touch the authored material, only a
        // runtime clone swapped into RenderSettings.skybox and restored on
        // disable.
        Material m_SkyboxSource;
        Material m_SkyboxInstance;
        int[] m_SkyboxColorIds;
        Color[] m_SkyboxColors;
        float m_SkyboxBrightness = -1f;
        float m_AmbientIntensity;
        float m_ReflectionIntensity;
        Color m_AmbientLight;
        Color m_AmbientSky;
        Color m_AmbientEquator;
        Color m_AmbientGround;

        Vector3 CenterWS => m_CenterTransform != null ? m_CenterTransform.position : m_Center;

        void Awake()
        {
            m_Toggle = ResolveToggle(m_ToggleAction);
        }

        // Prefer the Inspector-configured action; fall back to a code-built one
        // so the debug toggle works out of the box. Paths that the current
        // device layouts cannot resolve are ignored by the Input System, so
        // listing several controller spellings alongside the keyboard key is
        // safe.
        static InputAction ResolveToggle(InputActionProperty property)
        {
            InputAction action = property.action;
            if (action != null && action.bindings.Count > 0)
                return action;

            var fallback = new InputAction("SplatWorldReveal Toggle", InputActionType.Button);
            fallback.AddBinding("<XRController>{LeftHand}/{MenuButton}");
            fallback.AddBinding("<XRController>{LeftHand}/menu");
            fallback.AddBinding("<XRController>{LeftHand}/menuButton");
            fallback.AddBinding("<Keyboard>/r");
            return fallback;
        }

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
                m_HasPlayed = true;
                m_AudioPending = true;
            }
            m_Toggle?.Enable();
            BindSkybox();
            PushGlobals(CurrentRadius());
            PushSkybox(NormalizedProgress());
        }

        void OnDisable()
        {
            // Never leave the scene hidden via a stale global.
            Shader.SetGlobalFloat(Props.RevealEnable, 0f);
            m_Toggle?.Disable();
            UnbindSkybox();
        }

        void Update()
        {
            if (m_Toggle != null && m_Toggle.WasPressedThisFrame())
                Toggle();

            if (m_Playing)
            {
                m_Elapsed += Time.deltaTime;
                if (m_Elapsed >= m_Duration)
                {
                    m_Playing = false;
                    m_Finished = true;
                }
            }

            if (m_AudioPending && m_Elapsed >= 0f)
                PlayRevealAudio();

            PushGlobals(CurrentRadius());
            PushSkybox(NormalizedProgress());
        }

        float HiddenRadius => -(m_VanguardDistance + m_EdgeWidth + 1f);

        float CurrentRadius()
        {
            if (m_Elapsed < 0f)
                return HiddenRadius; // start-delay: nothing visible yet

            if (m_Finished)
                return m_Reversed ? HiddenRadius : m_MaxRadius;

            return m_RadiusCurve.Evaluate(NormalizedProgress()) * m_MaxRadius;
        }

        /// 0 = fully hidden, 1 = fully materialized, regardless of play direction.
        public float NormalizedProgress()
        {
            if (!m_HasPlayed)
                return 1f;
            if (m_Elapsed < 0f)
                return m_Reversed ? 1f : 0f;
            if (m_Finished)
                return m_Reversed ? 0f : 1f;

            float t = Mathf.Clamp01(m_Elapsed / Mathf.Max(m_Duration, 1e-4f));
            return m_Reversed ? 1f - t : t;
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
            Shader.SetGlobalFloat(Props.RevealFireflyIntensity, FireflyIntensity());
            Shader.SetGlobalVector(Props.RevealFireflyColor, m_FireflyColor);
        }

        // Ramps the whole swarm up over the first m_FireflyFadeIn of the
        // reveal, so the vanguard drifts in around the center instead of
        // popping on the frame the wavefront starts moving. Symmetric by
        // construction: a reverse run walks progress back down through the
        // same ramp.
        float FireflyIntensity()
        {
            float fade = Mathf.Clamp01(m_FireflyFadeIn);
            if (fade <= 1e-4f)
                return 1f;
            return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(NormalizedProgress() / fade));
        }

        // One clip for both directions, restarted rather than layered so a
        // mid-flight toggle does not stack two copies.
        void PlayRevealAudio()
        {
            m_AudioPending = false;
            if (m_RevealClip == null)
                return;

            if (m_AudioSource == null)
            {
                m_AudioSource = gameObject.AddComponent<AudioSource>();
                m_AudioSource.playOnAwake = false;
                m_AudioSource.spatialBlend = 0f; // a world-scale event, not a point source
            }
            m_AudioSource.Stop();
            m_AudioSource.clip = m_RevealClip;
            m_AudioSource.volume = m_RevealVolume;
            m_AudioSource.Play();
        }

        // Clones RenderSettings.skybox so the authored material is never
        // written to, and caches every Color property of its shader - which
        // keeps this generic across skybox shaders (gradient, procedural,
        // cubemap tint...) instead of hard-coding property names.
        void BindSkybox()
        {
            if (!m_FadeSkybox || m_SkyboxInstance != null || !Application.isPlaying)
                return;

            Material source = RenderSettings.skybox;
            if (source == null)
                return;

            m_SkyboxSource = source;
            m_SkyboxInstance = new Material(source) { hideFlags = HideFlags.HideAndDontSave };

            Shader shader = source.shader;
            var ids = new List<int>();
            var colors = new List<Color>();
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Color)
                    continue;
                int id = shader.GetPropertyNameId(i);
                ids.Add(id);
                colors.Add(source.GetColor(id));
            }
            m_SkyboxColorIds = ids.ToArray();
            m_SkyboxColors = colors.ToArray();

            m_AmbientIntensity = RenderSettings.ambientIntensity;
            m_ReflectionIntensity = RenderSettings.reflectionIntensity;
            m_AmbientLight = RenderSettings.ambientLight;
            m_AmbientSky = RenderSettings.ambientSkyColor;
            m_AmbientEquator = RenderSettings.ambientEquatorColor;
            m_AmbientGround = RenderSettings.ambientGroundColor;

            m_SkyboxBrightness = -1f;
            RenderSettings.skybox = m_SkyboxInstance;
        }

        void UnbindSkybox()
        {
            if (m_SkyboxInstance == null)
                return;

            if (RenderSettings.skybox == m_SkyboxInstance)
                RenderSettings.skybox = m_SkyboxSource;
            RenderSettings.ambientIntensity = m_AmbientIntensity;
            RenderSettings.reflectionIntensity = m_ReflectionIntensity;
            RenderSettings.ambientLight = m_AmbientLight;
            RenderSettings.ambientSkyColor = m_AmbientSky;
            RenderSettings.ambientEquatorColor = m_AmbientEquator;
            RenderSettings.ambientGroundColor = m_AmbientGround;

            Destroy(m_SkyboxInstance);
            m_SkyboxInstance = null;
            m_SkyboxSource = null;
            m_SkyboxColorIds = null;
            m_SkyboxColors = null;
        }

        void PushSkybox(float progress)
        {
            if (m_SkyboxInstance == null)
                return;

            float b = Mathf.Max(m_SkyboxCurve.Evaluate(progress), 0f);
            if (Mathf.Approximately(b, m_SkyboxBrightness))
                return;
            m_SkyboxBrightness = b;

            for (int i = 0; i < m_SkyboxColorIds.Length; i++)
                m_SkyboxInstance.SetColor(m_SkyboxColorIds[i], Dim(m_SkyboxColors[i], b));

            if (!m_FadeAmbientLight)
                return;

            // Covers every ambient mode: intensity drives Skybox mode, the
            // colors drive Flat/Trilight.
            RenderSettings.ambientIntensity = m_AmbientIntensity * b;
            RenderSettings.reflectionIntensity = m_ReflectionIntensity * b;
            RenderSettings.ambientLight = Dim(m_AmbientLight, b);
            RenderSettings.ambientSkyColor = Dim(m_AmbientSky, b);
            RenderSettings.ambientEquatorColor = Dim(m_AmbientEquator, b);
            RenderSettings.ambientGroundColor = Dim(m_AmbientGround, b);
        }

        // Alpha is left alone: for several skybox shaders it carries a
        // contribution weight rather than opacity.
        static Color Dim(Color c, float b) => new Color(c.r * b, c.g * b, c.b * b, c.a);

        /// Plays the reveal forward from hidden to fully materialized.
        [ContextMenu("Play")]
        public void Play()
        {
            m_Elapsed = -m_StartDelay;
            m_Playing = true;
            m_Finished = false;
            m_Reversed = false;
            m_HasPlayed = true;
            m_AudioPending = true;
            PushGlobals(CurrentRadius());
            PushSkybox(NormalizedProgress());
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
            m_HasPlayed = true;
            PlayRevealAudio();
            PushGlobals(CurrentRadius());
            PushSkybox(NormalizedProgress());
        }

        /// Reverses direction from wherever the effect currently sits - the
        /// debug entry point for the menu button / R key. Unlike Play() it
        /// keeps the current progress instead of snapping to an end state, so
        /// repeated presses read as one continuous animation, and it skips the
        /// start delay.
        [ContextMenu("Toggle")]
        public void Toggle()
        {
            float p = m_HasPlayed ? NormalizedProgress() : 1f;
            bool revealing = m_Playing ? !m_Reversed : p > 0.5f;

            m_Reversed = revealing;
            m_Elapsed = (m_Reversed ? 1f - p : p) * m_Duration;
            m_Playing = true;
            m_Finished = false;
            m_HasPlayed = true;
            PlayRevealAudio();
            PushGlobals(CurrentRadius());
            PushSkybox(NormalizedProgress());
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
            public const string RevealFireflyIntensity = "_RevealFireflyIntensity";
            public const string RevealFireflyColor = "_RevealFireflyColor";
        }
    }
}
