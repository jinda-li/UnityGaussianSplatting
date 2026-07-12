using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace StylizedSplats
{
    // Standalone brush controller: point a controller and hold the trigger to
    // paint. The player stands inside the splat volume, so instead of a raycast
    // hit point every splat within brushRadius of the ray segment gets painted
    // (see StylizedSplatPaint.compute). Depth along the ray IS bounded, though:
    // a physics raycast against the scene (the CollisionProxy mesh - a concave
    // surface mesh, so rays from inside the garden still hit walls/ground
    // facing the player) clamps the paint distance to the first hit plus one
    // brushRadius of soak-through, so spray no longer bleeds through hedges.
    //
    // Feedback while spraying, per hand:
    //  - a looping 3D spray sound faded in/out with the trigger pull
    //  - a cone mist ParticleSystem aimed along the spray ray
    //  - a light haptic buzz scaled by trigger pull
    // The AudioSource/ParticleSystem live on a runtime-created "SprayFX" child
    // of each hand's ray origin; the particle material is built at runtime from
    // sprayParticleShader (wired in the scene so the URP particle shader ships
    // in builds; falls back to Shader.Find in the editor).
    //
    // Drop this on its own GameObject (it does NOT need to move with the
    // controller - it's a logic holder that reads the live controller ray
    // origins every frame; the GameObject itself can sit at the origin).
    //
    // Trigger input uses InputActionProperty fields configured the same way as
    // this project's VRPlayerControllerInput (standalone inline actions bound
    // to <XRController>{Hand}/{Trigger}, "Use Reference" OFF) - NOT a reference
    // into the shared "XRI Default Input Actions" asset. Referencing that
    // asset's action map did not fire on a real headset, because the
    // ControllerInputActionManager on each controller swaps/disables those maps
    // for locomotion/teleport modes. Each controller here carries its own
    // ControllerInputActionManager, confirming that risk - owning the action
    // sidesteps it. If a field is left unwired, Awake falls back to a
    // code-built default so painting still works out of the box.
    //
    // Hand/ray discovery mirrors InfoOrbController's approach (the raw
    // controller transform's forward is not a reliable pointing axis; the
    // NearFarInteractor's calibrated ray origin is).
    public class VRSplatBrush : MonoBehaviour
    {
        private class Hand
        {
            public Transform rayOrigin;
            public XRNode node;
            public HapticImpulsePlayer haptics;
            public AudioSource audio;
            public ParticleSystem particles;
        }

        [SerializeField] private StylizedSplatsController controller;

        [Header("Input (inline actions bound to Trigger [Left/Right Hand XR Controller])")]
        [SerializeField] private InputActionProperty leftTriggerAction;
        [SerializeField] private InputActionProperty rightTriggerAction;
        [SerializeField, Range(0f, 1f)] private float triggerDeadzone = 0.1f;

        [Header("Brush")]
        [SerializeField, Min(0f)] private float brushRadius = 0.15f;
        [SerializeField, Min(0f)] private float paintRatePerSecond = 2f;
        [SerializeField, Min(0f)] private float maxDistance = 5f;

        [Header("Occlusion")]
        [Tooltip("Layers the spray ray tests against (the CollisionProxy mesh); paint stops one brushRadius past the first hit")]
        [SerializeField] private LayerMask occlusionMask = ~0;

        [Header("Spray Feedback")]
        [Tooltip("Looping spray sound; one 3D AudioSource per hand, volume follows trigger pull")]
        [SerializeField] private AudioClip sprayClip;
        [SerializeField, Range(0f, 1f)] private float sprayVolume = 0.8f;
        [SerializeField, Min(0.01f)] private float audioFadeIn = 0.08f;
        [SerializeField, Min(0.01f)] private float audioFadeOut = 0.2f;
        [Tooltip("URP Particles/Unlit - referenced here so the shader is included in builds")]
        [SerializeField] private Shader sprayParticleShader;
        [SerializeField, Min(0f)] private float particleRate = 90f;
        [SerializeField, Range(0f, 1f)] private float hapticIntensity = 0.3f;

        private Hand[] _hands;
        private InputAction _leftTrigger;
        private InputAction _rightTrigger;
        private Material _sprayMaterial;

        private void Awake()
        {
            AcquireHands();

            _leftTrigger = ResolveTrigger(leftTriggerAction, "VRSplatBrush Left Trigger", "<XRController>{LeftHand}/{Trigger}");
            _rightTrigger = ResolveTrigger(rightTriggerAction, "VRSplatBrush Right Trigger", "<XRController>{RightHand}/{Trigger}");
        }

        // Prefer the Inspector-configured action; fall back to a code-built one
        // bound to the standard trigger axis if the field was left unwired.
        private static InputAction ResolveTrigger(InputActionProperty property, string name, string defaultPath)
        {
            InputAction action = property.action;
            if (action != null && action.bindings.Count > 0)
                return action;
            return new InputAction(name, InputActionType.Value, defaultPath, expectedControlType: "Axis");
        }

        private void AcquireHands()
        {
            HapticImpulsePlayer[] hapticPlayers = FindObjectsByType<HapticImpulsePlayer>(FindObjectsSortMode.None);
            _hands = new Hand[hapticPlayers.Length];
            for (int i = 0; i < hapticPlayers.Length; i++)
            {
                HapticImpulsePlayer haptics = hapticPlayers[i];
                NearFarInteractor interactor = haptics.GetComponentInChildren<NearFarInteractor>();
                Transform rayOrigin = interactor != null
                    ? ((IXRRayProvider)interactor).GetOrCreateRayOrigin()
                    : haptics.transform;

                var hand = new Hand { rayOrigin = rayOrigin, node = GuessNode(haptics.transform), haptics = haptics };
                CreateSprayFx(hand);
                _hands[i] = hand;
            }
        }

        private void OnEnable()
        {
            _leftTrigger?.Enable();
            _rightTrigger?.Enable();
        }

        private void OnDisable()
        {
            _leftTrigger?.Disable();
            _rightTrigger?.Disable();

            if (_hands == null)
                return;
            foreach (Hand hand in _hands)
            {
                if (hand.audio != null)
                {
                    hand.audio.volume = 0f;
                    hand.audio.Stop();
                }
                if (hand.particles != null)
                {
                    ParticleSystem.EmissionModule emission = hand.particles.emission;
                    emission.rateOverTimeMultiplier = 0f;
                }
            }
        }

        private void Update()
        {
            if (controller == null)
                return;

            // XR rig may finish spawning after Awake; re-acquire if we came up empty.
            if (_hands == null || _hands.Length == 0)
                AcquireHands();

            foreach (Hand hand in _hands)
            {
                InputAction action = hand.node == XRNode.LeftHand ? _leftTrigger : _rightTrigger;
                if (action == null || hand.rayOrigin == null)
                    continue;

                float pull = action.ReadValue<float>();
                bool spraying = pull >= triggerDeadzone;

                if (spraying)
                {
                    Vector3 origin = hand.rayOrigin.position;
                    Vector3 dir = hand.rayOrigin.forward;

                    // Stop the paint volume at the first surface so spray does
                    // not bleed through walls; +brushRadius lets the surface's
                    // own splat layer soak through its full thickness.
                    float paintDistance = maxDistance;
                    if (Physics.Raycast(origin, dir, out RaycastHit hit, maxDistance, occlusionMask, QueryTriggerInteraction.Ignore))
                        paintDistance = hit.distance + brushRadius;

                    controller.PaintRay(origin, dir, brushRadius, paintDistance, pull * paintRatePerSecond * Time.deltaTime);
                    hand.haptics?.SendHapticImpulse(hapticIntensity * pull, 0.05f);
                }

                UpdateSprayFx(hand, spraying ? pull : 0f);
            }
        }

        // Fade the loop volume toward the trigger pull and scale mist emission;
        // the AudioSource/ParticleSystem sit on a child of the ray origin so
        // both follow the hand and aim along the spray automatically.
        private void UpdateSprayFx(Hand hand, float pull)
        {
            if (hand.audio != null)
            {
                float target = pull * sprayVolume;
                float fade = target > hand.audio.volume ? audioFadeIn : audioFadeOut;
                hand.audio.volume = Mathf.MoveTowards(hand.audio.volume, target, sprayVolume / fade * Time.deltaTime);
                hand.audio.pitch = Mathf.Lerp(0.95f, 1.08f, pull);

                if (hand.audio.volume > 0f)
                {
                    if (!hand.audio.isPlaying)
                        hand.audio.Play();
                }
                else if (hand.audio.isPlaying && target <= 0f)
                {
                    hand.audio.Stop();
                }
            }

            if (hand.particles != null)
            {
                ParticleSystem.EmissionModule emission = hand.particles.emission;
                emission.rateOverTimeMultiplier = pull * particleRate;
            }
        }

        private void CreateSprayFx(Hand hand)
        {
            if (hand.rayOrigin == null)
                return;

            var go = new GameObject("SprayFX");
            go.transform.SetParent(hand.rayOrigin, false);

            AudioSource audio = go.AddComponent<AudioSource>();
            audio.clip = sprayClip;
            audio.loop = true;
            audio.playOnAwake = false;
            audio.volume = 0f;
            audio.spatialBlend = 1f;
            audio.dopplerLevel = 0f;
            audio.minDistance = 0.3f;
            audio.maxDistance = 12f;
            hand.audio = audio;

            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.startSpeed = new ParticleSystem.MinMaxCurve(3.5f, 5.5f);
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.35f, 0.6f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.05f);
            // white with a faint cool tint - reads as "reveal magic", not a
            // specific paint color (the brush restores original splat colors)
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 1f, 1f, 0.5f), new Color(0.8f, 0.93f, 1f, 0.5f));
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 400;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTime = 0f;

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 6f;
            shape.radius = 0.01f;

            ParticleSystem.ColorOverLifetimeModule colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.7f, 0.4f), new GradientAlphaKey(0f, 1f) });
            colorOverLifetime.color = gradient;

            ParticleSystem.SizeOverLifetimeModule sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(2.2f, AnimationCurve.Linear(0f, 0.45f, 1f, 1f));

            var psRenderer = go.GetComponent<ParticleSystemRenderer>();
            psRenderer.sharedMaterial = GetSprayMaterial();
            psRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            psRenderer.receiveShadows = false;

            ps.Play();
            hand.particles = ps;
        }

        private Material GetSprayMaterial()
        {
            if (_sprayMaterial != null)
                return _sprayMaterial;

            Shader shader = sprayParticleShader != null ? sprayParticleShader : Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null)
                return null;

            var mat = new Material(shader) { name = "SprayParticle (runtime)" };
            mat.SetFloat("_Surface", 1f); // transparent
            mat.SetFloat("_Blend", 0f);   // alpha blend
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 0f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)RenderQueue.Transparent;
            mat.SetTexture("_BaseMap", CreateSoftDotTexture());
            _sprayMaterial = mat;
            return mat;
        }

        // Soft radial-falloff dot so the mist has no hard sprite edges.
        private static Texture2D CreateSoftDotTexture()
        {
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "SpraySoftDot", wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f;
                    float dy = (y + 0.5f) / size * 2f - 1f;
                    float t = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    float a = t * t * (3f - 2f * t);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return tex;
        }

        private static XRNode GuessNode(Transform t)
        {
            for (Transform cur = t; cur != null; cur = cur.parent)
            {
                string n = cur.name.ToLowerInvariant();
                if (n.Contains("left"))
                    return XRNode.LeftHand;
                if (n.Contains("right"))
                    return XRNode.RightHand;
            }
            return XRNode.RightHand;
        }
    }
}
