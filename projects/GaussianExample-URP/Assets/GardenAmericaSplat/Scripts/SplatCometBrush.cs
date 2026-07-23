using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace GardenSplat
{
    // The GardenAmericaSplat verb: charge, then throw a color comet.
    //
    // Hold the trigger to charge (0..1 over chargeTime). Charge scales the comet
    // and, on release, how big a patch of the garden it wakes. Let go and a comet
    // is launched along the controller ray; it flies a ballistic arc, and the
    // frame it hits the world (a manual SphereCast against occlusionMask - the
    // CollisionProxy mesh) it fires an expanding spherical paint burst on
    // SplatMaterializeController at the impact point. That burst is what turns
    // dormant points back into the living, colored world.
    //
    // Hand/ray discovery mirrors VRSplatBrush/InfoOrbController: each hand's
    // HapticImpulsePlayer -> NearFarInteractor -> calibrated ray origin. Trigger
    // input uses inline InputActionProperty fields (bound to
    // <XRController>{Hand}/{Trigger}, "Use Reference" OFF), same rationale as
    // VRSplatBrush - the shared XRI maps get swapped out by the per-controller
    // ControllerInputActionManager and don't fire on a headset.
    public class SplatCometBrush : MonoBehaviour
    {
        private class Hand
        {
            public Transform rayOrigin;
            public XRNode node;
            public HapticImpulsePlayer haptics;
            public bool wasHeld;
            public float charge;
            public ParticleSystem chargeFx;
        }

        private class Comet
        {
            public GameObject go;
            public Vector3 pos;
            public Vector3 vel;
            public float radius;
            public float age;
            public HapticImpulsePlayer haptics;
        }

        [SerializeField] private SplatMaterializeController controller;
        [Tooltip("Optional. Receives comet impacts so the world can answer back with local chimes/particles and milestone events")]
        [SerializeField] private SplatPaintBloom bloom;

        [Header("Input (inline actions bound to Trigger [Left/Right Hand XR Controller])")]
        [SerializeField] private InputActionProperty leftTriggerAction;
        [SerializeField] private InputActionProperty rightTriggerAction;
        [SerializeField, Range(0f, 1f)] private float triggerDeadzone = 0.1f;

        [Header("Charge")]
        [Tooltip("Seconds of holding the trigger to reach full charge")]
        [SerializeField, Min(0.05f)] private float chargeTime = 0.9f;
        [Tooltip("Optional particle effect parented to the hand. Leave unassigned for a code-built cluster that gathers in front of the controller")]
        [SerializeField] private GameObject chargeFxPrefab;
        [SerializeField, Range(0f, 1f)] private float chargeHaptic = 0.15f;
        [Tooltip("Metres in front of the controller where the charge cluster gathers")]
        [SerializeField, Min(0f)] private float chargeForwardOffset = 0.1f;
        [Tooltip("Size of each charge-cluster particle")]
        [SerializeField, Min(0.002f)] private float chargeParticleSize = 0.012f;
        [Tooltip("Radius of the gathered charge cluster")]
        [SerializeField, Min(0.005f)] private float chargeClusterRadius = 0.025f;
        [SerializeField, Min(1f)] private float chargeEmitRate = 70f;

        [Header("Throw")]
        [SerializeField, Min(0f)] private float launchSpeed = 9f;
        [Tooltip("Extra launch speed added at full charge")]
        [SerializeField, Min(0f)] private float launchSpeedChargeBonus = 4f;
        [Tooltip("Gravity multiplier on the comet's arc")]
        [SerializeField] private float gravityScale = 0.6f;
        [Tooltip("Physical radius of the comet for the impact SphereCast")]
        [SerializeField, Min(0.01f)] private float cometRadius = 0.12f;
        [Tooltip("Comet self-destructs after this long if it never hits anything")]
        [SerializeField, Min(0.2f)] private float cometMaxLifetime = 4f;

        [Header("Impact Burst")]
        [Tooltip("Patch radius a minimum-charge comet wakes")]
        [SerializeField, Min(0.1f)] private float minBurstRadius = 1.5f;
        [Tooltip("Patch radius a fully-charged comet wakes")]
        [SerializeField, Min(0.1f)] private float maxBurstRadius = 4.5f;
        [SerializeField, Range(0f, 1f)] private float impactHaptic = 0.6f;

        [Header("Comet Visual")]
        [Tooltip("Optional prefab (ParticleSystem/trail) instantiated as the flying comet; falls back to a code-built default")]
        [SerializeField] private GameObject cometPrefab;
        [Tooltip("Size of each comet particle")]
        [SerializeField, Min(0.005f)] private float cometParticleSize = 0.035f;
        [Tooltip("Radius of the comet's particle ball")]
        [SerializeField, Min(0.005f)] private float cometClusterRadius = 0.03f;
        [Tooltip("URP Particles/Unlit - referenced so the shader ships in builds")]
        [SerializeField] private Shader cometParticleShader;
        [Tooltip("Use the custom cometColors gradient below instead of the built-in warm->cool one")]
        [SerializeField] private bool customCometColors;
        [SerializeField] private Gradient cometColors;
        [Tooltip("Optional prefab spawned at the impact point (color splash / pollen)")]
        [SerializeField] private GameObject impactPrefab;
        [SerializeField, Min(0.1f)] private float impactLifetime = 3f;

        [Header("Occlusion")]
        [Tooltip("Layers the comet collides with (the CollisionProxy mesh)")]
        [SerializeField] private LayerMask occlusionMask = ~0;

        private Hand[] _hands;
        private InputAction _leftTrigger;
        private InputAction _rightTrigger;
        private readonly List<Comet> _comets = new();
        private Material _cometMaterial;

        private void Awake()
        {
            AcquireHands();
            _leftTrigger = ResolveTrigger(leftTriggerAction, "SplatCometBrush Left Trigger", "<XRController>{LeftHand}/{Trigger}");
            _rightTrigger = ResolveTrigger(rightTriggerAction, "SplatCometBrush Right Trigger", "<XRController>{RightHand}/{Trigger}");
        }

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
                CreateChargeFx(hand);
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
        }

        private void Update()
        {
            if (controller == null)
                return;

            if (_hands == null || _hands.Length == 0)
                AcquireHands();

            UpdateHands();
            UpdateComets();
        }

        private void UpdateHands()
        {
            foreach (Hand hand in _hands)
            {
                InputAction action = hand.node == XRNode.LeftHand ? _leftTrigger : _rightTrigger;
                if (action == null || hand.rayOrigin == null)
                    continue;

                float pull = action.ReadValue<float>();
                bool held = pull >= triggerDeadzone;

                if (held)
                {
                    hand.charge = Mathf.Clamp01(hand.charge + Time.deltaTime / chargeTime);
                    if (chargeHaptic > 0f)
                        hand.haptics?.SendHapticImpulse(chargeHaptic * hand.charge, 0.03f);
                }
                else if (hand.wasHeld)
                {
                    // released this frame -> throw
                    Throw(hand);
                    hand.charge = 0f;
                }

                UpdateChargeFx(hand, held ? hand.charge : 0f);
                hand.wasHeld = held;
            }
        }

        private void Throw(Hand hand)
        {
            Vector3 origin = hand.rayOrigin.position;
            Vector3 dir = hand.rayOrigin.forward;
            float charge = Mathf.Clamp01(hand.charge);

            var comet = new Comet
            {
                pos = origin,
                vel = dir * (launchSpeed + launchSpeedChargeBonus * charge),
                radius = Mathf.Lerp(minBurstRadius, maxBurstRadius, charge),
                haptics = hand.haptics,
                go = CreateCometVisual(origin, charge)
            };
            _comets.Add(comet);

            hand.haptics?.SendHapticImpulse(0.4f, 0.06f);
        }

        // Ballistic integration + a SphereCast over the segment travelled this
        // frame, so a fast comet cannot tunnel through a thin proxy wall.
        private void UpdateComets()
        {
            for (int i = _comets.Count - 1; i >= 0; i--)
            {
                Comet c = _comets[i];
                c.age += Time.deltaTime;
                c.vel += Physics.gravity * (gravityScale * Time.deltaTime);

                Vector3 step = c.vel * Time.deltaTime;
                float dist = step.magnitude;

                bool impact = false;
                Vector3 impactPoint = c.pos;
                if (dist > 1e-5f &&
                    Physics.SphereCast(c.pos, cometRadius, step / dist, out RaycastHit hit, dist, occlusionMask, QueryTriggerInteraction.Ignore))
                {
                    impact = true;
                    impactPoint = hit.point;
                    c.pos = hit.point;
                }
                else
                {
                    c.pos += step;
                }

                if (c.go != null)
                    c.go.transform.position = c.pos;

                if (impact)
                {
                    Impact(c, impactPoint);
                    DestroyComet(c);
                    _comets.RemoveAt(i);
                }
                else if (c.age >= cometMaxLifetime)
                {
                    DestroyComet(c);
                    _comets.RemoveAt(i);
                }
                else
                {
                    _comets[i] = c;
                }
            }
        }

        private void Impact(Comet c, Vector3 point)
        {
            controller.PaintBurst(point, c.radius);
            bloom?.ReportPaint(point, c.radius);
            c.haptics?.SendHapticImpulse(impactHaptic, 0.09f);

            if (impactPrefab != null)
            {
                GameObject fx = Instantiate(impactPrefab, point, Random.rotationUniform);
                Destroy(fx, impactLifetime);
            }
        }

        private void DestroyComet(Comet c)
        {
            if (c.go == null)
                return;
            // Let a trailing ParticleSystem finish rather than snapping off.
            var ps = c.go.GetComponentInChildren<ParticleSystem>();
            if (ps != null)
            {
                ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                c.go.transform.SetParent(null, true);
                Destroy(c.go, ps.main.startLifetime.constantMax + 0.5f);
            }
            else
            {
                Destroy(c.go);
            }
        }

        private GameObject CreateCometVisual(Vector3 pos, float charge)
        {
            if (cometPrefab != null)
            {
                GameObject go = Instantiate(cometPrefab, pos, Quaternion.identity);
                float s = Mathf.Lerp(0.7f, 1.3f, charge);
                go.transform.localScale *= s;
                return go;
            }

            var comet = new GameObject("Comet");
            comet.transform.position = pos;

            ParticleSystem ps = comet.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.startSpeed = 0f;
            main.startLifetime = 0.45f;
            main.startSize = cometParticleSize * Mathf.Lerp(0.85f, 1.2f, charge);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 300;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTime = 120f;

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = cometClusterRadius;

            ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(CometGradient());

            ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0f));

            var renderer = comet.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = GetCometMaterial();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            ps.Play();
            return comet;
        }

        private Gradient CometGradient()
        {
            if (customCometColors && cometColors != null && cometColors.colorKeys.Length > 0)
                return cometColors;
            var g = new Gradient();
            g.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.85f, 0.3f), 0f),
                    new GradientColorKey(new Color(1f, 0.4f, 0.6f), 0.5f),
                    new GradientColorKey(new Color(0.5f, 0.7f, 1f), 1f),
                },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
            return g;
        }

        private Material GetCometMaterial()
        {
            if (_cometMaterial != null)
                return _cometMaterial;

            Shader shader = cometParticleShader != null ? cometParticleShader : Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null)
                return null;

            var mat = new Material(shader) { name = "CometParticle (runtime)" };
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 1f); // additive reads as glowing energy
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            mat.SetFloat("_ZWrite", 0f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            _cometMaterial = mat;
            return mat;
        }

        private void CreateChargeFx(Hand hand)
        {
            if (hand.rayOrigin == null)
                return;
            GameObject go = chargeFxPrefab != null ? Instantiate(chargeFxPrefab) : BuildDefaultChargeFx();
            go.name = "ChargeFX";
            go.transform.SetParent(hand.rayOrigin, false);
            // gather a bit in front of the controller, along its ray
            go.transform.localPosition = Vector3.forward * chargeForwardOffset;
            hand.chargeFx = go.GetComponentInChildren<ParticleSystem>();
            if (hand.chargeFx != null)
            {
                var em = hand.chargeFx.emission;
                em.rateOverTimeMultiplier = 0f;
                hand.chargeFx.Play();
            }
        }

        // A small, tight cluster of particles that hangs in front of the
        // controller and fills in as the trigger is charged. Local sim space so
        // it stays glued to the hand.
        private GameObject BuildDefaultChargeFx()
        {
            var go = new GameObject("ChargeFX");

            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.startSpeed = 0f;
            main.startLifetime = 0.4f;
            main.startSize = chargeParticleSize;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.maxParticles = 150;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTime = 0f; // driven by charge in UpdateChargeFx

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = chargeClusterRadius;

            ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(CometGradient());

            ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.3f, 1f, 1f));

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = GetCometMaterial();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            ps.Play();
            return go;
        }

        private void UpdateChargeFx(Hand hand, float charge)
        {
            if (hand.chargeFx == null)
                return;
            var em = hand.chargeFx.emission;
            em.rateOverTimeMultiplier = charge * chargeEmitRate;
            // the cluster grows a little as it fills, then flies off on release
            float s = Mathf.Lerp(0.6f, 1f, charge);
            hand.chargeFx.transform.localScale = new Vector3(s, s, s);
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
