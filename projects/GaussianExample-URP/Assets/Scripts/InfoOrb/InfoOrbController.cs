using TMPro;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace VRPlayer
{
    // Single prefab, drop anywhere: orb breathes at all times (shader-driven, see _Time in
    // the material). Only two states beyond that - closed and expanded. Expansion is driven
    // purely by hand pose each frame: either hand is close to the orb, or the controller's ray
    // passes near it within range. No button press, no XRI interactable/interactor event
    // wiring - hands are found once via the HapticImpulsePlayer that already lives on every
    // XRI controller GameObject (haptic output target), paired with that same controller's
    // NearFarInteractor ray origin (the same calibrated pointing ray XRI's own UI/interaction
    // raycasts use - the raw controller transform's forward is not a reliable pointing axis).
    // Particles/panel are only SetActive while the player is within lodRadius, since a garden
    // can have several of these active in a Gaussian Splatting scene at once.
    public class InfoOrbController : MonoBehaviour
    {
        private struct Hand
        {
            public HapticImpulsePlayer haptics;
            public Transform position;
            public Transform rayOrigin;
        }

        [Header("Line Pointer (optional - skipped if lineEnd is unset)")]
        [SerializeField] private LineRenderer linePointer;
        [SerializeField] private Transform lineStart;
        [SerializeField] private Transform lineEnd;

        [Header("Orb Visual")]
        [SerializeField] private Transform orbVisual;

        [Header("Near-range visuals (disabled until player is close)")]
        [SerializeField] private GameObject nearFxRoot;
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private Renderer panelBackgroundRenderer;
        [SerializeField] private TMP_Text titleText;
        [SerializeField] private TMP_Text bodyText;

        [Header("Distances")]
        [SerializeField, Min(0f)] private float lodRadius = 3f;
        [SerializeField, Min(0f)] private float handProximityRadius = 0.2f;
        [SerializeField, Min(0f)] private float rayMaxDistance = 4f;
        [SerializeField, Min(0f)] private float rayRadius = 0.2f;

        [Header("Reveal")]
        [SerializeField, Min(0.01f)] private float revealSpeed = 2.5f;

        [Header("Haptics")]
        [SerializeField] private float touchHapticAmplitude = 0.5f;
        [SerializeField] private float touchHapticDuration = 0.05f;

        private static readonly int ProgressId = Shader.PropertyToID("_Progress");

        private Transform _player;
        private Hand[] _hands;
        private ParticleSystem _nearFx;
        private MaterialPropertyBlock _mpb;
        private Vector3 _orbBaseScale;
        private bool _isExpanded;
        private bool _lodActive;
        private float _reveal;

        private void Awake()
        {
            _mpb = new MaterialPropertyBlock();
            _orbBaseScale = orbVisual != null ? orbVisual.localScale : Vector3.one;

            if (nearFxRoot != null)
                _nearFx = nearFxRoot.GetComponent<ParticleSystem>();

            HapticImpulsePlayer[] hapticPlayers = FindObjectsByType<HapticImpulsePlayer>(FindObjectsSortMode.None);
            _hands = new Hand[hapticPlayers.Length];
            for (int i = 0; i < hapticPlayers.Length; i++)
            {
                HapticImpulsePlayer haptics = hapticPlayers[i];
                NearFarInteractor interactor = haptics.GetComponentInChildren<NearFarInteractor>();
                Transform rayOrigin = interactor != null
                    ? ((IXRRayProvider)interactor).GetOrCreateRayOrigin()
                    : haptics.transform;

                _hands[i] = new Hand { haptics = haptics, position = haptics.transform, rayOrigin = rayOrigin };
            }

            ApplyLodState(false, force: true);
            SetReveal(0f);
        }

        private void Update()
        {
            if (_player == null)
            {
                Camera cam = Camera.main;
                if (cam == null)
                    return;
                _player = cam.transform;
            }

            float camDistance = Vector3.Distance(_player.position, transform.position);
            bool shouldBeActive = camDistance <= lodRadius;
            if (shouldBeActive != _lodActive)
                ApplyLodState(shouldBeActive);

            UpdateExpansion();
            UpdateReveal();
            UpdateLinePointer();
            UpdatePanelFacing();
        }

        private void UpdateExpansion()
        {
            if (!_lodActive || _hands == null || _hands.Length == 0)
            {
                SetExpanded(false);
                return;
            }

            bool wasExpanded = _isExpanded;
            HapticImpulsePlayer triggeringHand = null;

            foreach (Hand hand in _hands)
            {
                if (!HandTriggers(hand))
                    continue;

                triggeringHand = hand.haptics;
                break;
            }

            SetExpanded(triggeringHand != null);

            if (!wasExpanded && _isExpanded)
                triggeringHand.SendHapticImpulse(touchHapticAmplitude, touchHapticDuration);
        }

        // True if the hand itself is close enough to the orb, or if the controller's ray
        // (its calibrated pointing origin, not just the raw hand transform) passes near the
        // orb within rayRadius somewhere inside rayMaxDistance.
        private bool HandTriggers(Hand hand)
        {
            Vector3 toOrbFromHand = transform.position - hand.position.position;
            if (toOrbFromHand.sqrMagnitude <= handProximityRadius * handProximityRadius)
                return true;

            Vector3 toOrbFromRay = transform.position - hand.rayOrigin.position;
            float t = Vector3.Dot(toOrbFromRay, hand.rayOrigin.forward);
            if (t < 0f || t > rayMaxDistance)
                return false;

            Vector3 closestPointOnRay = hand.rayOrigin.position + hand.rayOrigin.forward * t;
            return (closestPointOnRay - transform.position).sqrMagnitude <= rayRadius * rayRadius;
        }

        private void SetExpanded(bool expanded)
        {
            if (_isExpanded == expanded)
                return;

            _isExpanded = expanded;

            if (_nearFx != null)
            {
                if (expanded)
                    _nearFx.Pause();
                else if (_lodActive)
                    _nearFx.Play();
            }
        }

        private void UpdateReveal()
        {
            float target = _isExpanded ? 1f : 0f;
            if (Mathf.Approximately(_reveal, target))
                return;

            _reveal = Mathf.MoveTowards(_reveal, target, revealSpeed * Time.deltaTime);
            SetReveal(_reveal);
        }

        private void UpdateLinePointer()
        {
            if (linePointer == null || lineEnd == null)
                return;

            bool shouldDraw = _isExpanded;
            if (linePointer.enabled != shouldDraw)
                linePointer.enabled = shouldDraw;

            if (shouldDraw)
            {
                Vector3 start = lineStart != null ? lineStart.position : transform.position;
                linePointer.SetPosition(0, start);
                linePointer.SetPosition(1, lineEnd.position);
            }
        }

        // Keeps the panel readable from wherever the player is standing. Yaw-only so it
        // doesn't tilt forward/back if the player looks up or crouches.
        private void UpdatePanelFacing()
        {
            if (panelRoot == null || !panelRoot.activeSelf || _player == null)
                return;

            Vector3 toCamera = _player.position - panelRoot.transform.position;
            toCamera.y = 0f;

            if (toCamera.sqrMagnitude < 0.0001f)
                return;

            panelRoot.transform.rotation = Quaternion.LookRotation(toCamera.normalized, Vector3.up);
        }

        private void ApplyLodState(bool active, bool force = false)
        {
            if (_lodActive == active && !force)
                return;

            _lodActive = active;

            if (nearFxRoot != null)
                nearFxRoot.SetActive(active);
            if (panelRoot != null)
                panelRoot.SetActive(active);

            // Walking out of range always closes the orb - re-approaching starts from closed.
            if (!active)
            {
                _isExpanded = false;
                _reveal = 0f;
                SetReveal(0f);
            }
        }

        private void SetReveal(float value)
        {
            if (panelBackgroundRenderer != null)
            {
                panelBackgroundRenderer.GetPropertyBlock(_mpb);
                _mpb.SetFloat(ProgressId, value);
                panelBackgroundRenderer.SetPropertyBlock(_mpb);
            }

            if (titleText != null)
                titleText.alpha = value;
            if (bodyText != null)
                bodyText.alpha = value;

            // Orb and panel are mutually exclusive: the orb shrinks away as the panel reveals
            // in, and grows back as the panel closes, so the two never compete visually.
            if (orbVisual != null)
                orbVisual.localScale = Vector3.Lerp(_orbBaseScale, Vector3.zero, value);
        }
    }
}
