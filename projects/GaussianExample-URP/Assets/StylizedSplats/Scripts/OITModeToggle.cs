using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.InputSystem;

namespace StylizedSplats
{
    // Quest Touch left-controller X (= PrimaryButton) toggles Mobile-GS OIT on
    // all GaussianSplatRenderers. Owns its InputAction (same pattern as
    // VRSplatBrush) so XRI ControllerInputActionManager map swaps cannot
    // silently disable the binding. Keyboard X is a desktop/editor fallback.
    public class OITModeToggle : MonoBehaviour
    {
        [Tooltip("If empty, toggles every active GaussianSplatRenderer in the scene")]
        [SerializeField] private GaussianSplatRenderer[] targets;

        [Header("Input")]
        [Tooltip("Quest left X / PrimaryButton. Leave unwired to use the built-in default binding.")]
        [SerializeField] private InputActionProperty leftXAction;
        [SerializeField] private Key keyboardFallback = Key.X;
        [SerializeField] private bool logToConsole = true;
        [SerializeField] private bool showHud = true;

        InputAction _leftX;
        float _hudUntil;
        string _hudText = "";

        void Awake()
        {
            _leftX = ResolveButton(
                leftXAction,
                "OITModeToggle Left X",
                "<XRController>{LeftHand}/{PrimaryButton}");
        }

        void OnEnable()
        {
            _leftX?.Enable();
            RefreshHudFromCurrentState();
        }

        void OnDisable()
        {
            _leftX?.Disable();
        }

        void Update()
        {
            bool pressed = _leftX != null && _leftX.WasPressedThisFrame();
            if (!pressed && keyboardFallback != Key.None && Keyboard.current != null)
                pressed = Keyboard.current[keyboardFallback].wasPressedThisFrame;

            if (pressed)
                ToggleOIT();
        }

        void ToggleOIT()
        {
            GaussianSplatRenderer[] list = ResolveTargets();
            if (list.Length == 0)
            {
                Debug.LogWarning("[OITModeToggle] No GaussianSplatRenderer found.");
                return;
            }

            // Flip relative to the first target so a multi-renderer scene stays in sync.
            bool next = !list[0].m_UseOIT;
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i] != null)
                    list[i].m_UseOIT = next;
            }

            _hudText = next ? "OIT ON" : "OIT OFF (sorted)";
            _hudUntil = Time.unscaledTime + 2.5f;

            if (logToConsole)
                Debug.Log($"[OITModeToggle] {_hudText}");
        }

        GaussianSplatRenderer[] ResolveTargets()
        {
            if (targets != null && targets.Length > 0)
            {
                int n = 0;
                for (int i = 0; i < targets.Length; i++)
                    if (targets[i] != null) n++;
                if (n == targets.Length)
                    return targets;

                var trimmed = new GaussianSplatRenderer[n];
                int w = 0;
                for (int i = 0; i < targets.Length; i++)
                    if (targets[i] != null)
                        trimmed[w++] = targets[i];
                if (n > 0)
                    return trimmed;
            }

            return FindObjectsByType<GaussianSplatRenderer>(FindObjectsSortMode.None);
        }

        void RefreshHudFromCurrentState()
        {
            GaussianSplatRenderer[] list = ResolveTargets();
            if (list.Length == 0)
                return;
            bool on = list[0] != null && list[0].m_UseOIT;
            _hudText = on ? "OIT ON" : "OIT OFF (sorted)";
            _hudUntil = Time.unscaledTime + 1.5f;
        }

        void OnGUI()
        {
            if (!showHud || Time.unscaledTime > _hudUntil || string.IsNullOrEmpty(_hudText))
                return;

            const float w = 280f, h = 48f;
            var rect = new Rect(16f, 16f, w, h);
            GUI.color = new Color(0f, 0f, 0f, 0.65f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;
            var style = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 22,
                fontStyle = FontStyle.Bold
            };
            GUI.Label(rect, _hudText, style);
        }

        static InputAction ResolveButton(InputActionProperty property, string name, string defaultPath)
        {
            InputAction action = property.action;
            if (action != null && action.bindings.Count > 0)
                return action;
            return new InputAction(name, InputActionType.Button, defaultPath);
        }
    }
}
