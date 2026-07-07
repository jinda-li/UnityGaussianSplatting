using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace StylizedSplats
{
    // Standalone brush controller: point a controller and hold the trigger to
    // paint. No raycast/collider target - the player stands inside the splat
    // volume, so a single proxy collider can't reliably catch the ray (a ray
    // whose origin is inside a collider never registers an exit-only hit in
    // Unity). Instead every splat within brushRadius of the ray segment out to
    // maxDistance gets painted (see StylizedSplatPaint.compute); depth along
    // the ray is intentionally ignored.
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
        private struct Hand
        {
            public Transform rayOrigin;
            public XRNode node;
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

        private Hand[] _hands;
        private InputAction _leftTrigger;
        private InputAction _rightTrigger;

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

                _hands[i] = new Hand { rayOrigin = rayOrigin, node = GuessNode(haptics.transform) };
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

            // XR rig may finish spawning after Awake; re-acquire if we came up empty.
            if (_hands == null || _hands.Length == 0)
                AcquireHands();

            foreach (Hand hand in _hands)
            {
                InputAction action = hand.node == XRNode.LeftHand ? _leftTrigger : _rightTrigger;
                if (action == null || hand.rayOrigin == null)
                    continue;

                float pull = action.ReadValue<float>();
                if (pull < triggerDeadzone)
                    continue;

                controller.PaintRay(hand.rayOrigin.position, hand.rayOrigin.forward, brushRadius, maxDistance, pull * paintRatePerSecond * Time.deltaTime);
            }
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
