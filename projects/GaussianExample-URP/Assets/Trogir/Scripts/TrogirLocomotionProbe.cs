using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation;
using VRPlayer;

namespace Trogir
{
    // Walks the scene the way a player does: through the XR Device Simulator,
    // with the player prefab's own locomotion doing the moving.
    //
    // This is the other half of TrogirWalkProbe. That one switches the player's
    // scripts off and pushes the capsule itself, which tests the collision proxy
    // and nothing else. This one leaves every script running and presses keys, so
    // what is under test is the whole chain: keyboard -> XRDeviceSimulator ->
    // XRSimulatedController.primary2DAxis -> the Move action the prefab binds to
    // <XRController>{LeftHand}/{Primary2DAxis} -> VRPlayerControllerInput ->
    // PlayerController.TickLocomotion -> CharacterController.Move -> the proxy.
    //
    // Holding Left Shift is what puts the simulator in "manipulate left
    // controller" mode; WASD is then the left thumbstick. Those are the
    // simulator's own default bindings, the same ones a person uses.
    //
    // The head does not turn: PlayerController moves view-relative, so the probe
    // steers by picking which of the eight WASD directions points closest to the
    // next waypoint, and the view stays on the heading the player spawned with.
    public class TrogirLocomotionProbe : MonoBehaviour
    {
        [Tooltip("Route in world space, walked in order.")]
        public Vector3[] m_Waypoints;

        [Tooltip("Camera the screenshots come from. The XR rig's, if left empty.")]
        public Camera m_Camera;

        [Tooltip("Give up on a waypoint after this long and report it as blocked.")]
        public float m_WaypointTimeout = 25f;

        [Tooltip("Below this the player is treated as having fallen out of the scene.")]
        public float m_FallenBelowY = -5f;

        public string m_OutputDir = "Assets/Screenshots/TrogirLocomotion";
        public int m_ShotWidth = 1280;
        public int m_ShotHeight = 720;
        public bool m_RunOnStart;

        public bool Done { get; private set; }
        public bool Passed { get; private set; }
        public string Report { get; private set; } = "";

        readonly StringBuilder m_Log = new StringBuilder();
        int m_Failures;

        CharacterController m_Body;
        VRPlayerControllerInput m_Input;
        Transform m_Hmd;
        Behaviour[] m_SplatRenderers = System.Array.Empty<Behaviour>();
        Keyboard m_Keyboard;
        Key[] m_HeldKeys = System.Array.Empty<Key>();
        float m_PeakMoveAxis;
        InputSettings.BackgroundBehavior m_BackgroundBehaviorToRestore;
        bool m_BackgroundBehaviorChanged;
#if UNITY_EDITOR
        InputSettings.EditorInputBehaviorInPlayMode m_EditorBehaviorToRestore;
        bool m_EditorBehaviorChanged;
#endif

        // Batch mode has no keyboard or mouse, and the simulator reads the display
        // names of its own bindings' controls on Start - with no device to resolve
        // them against that throws before it finishes setting itself up. Adding the
        // devices here, before any scene loads, is also what lets the probe press
        // keys at all.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void EnsureInputDevices()
        {
            if (Keyboard.current == null)
                InputSystem.AddDevice<Keyboard>();
            if (Mouse.current == null)
                InputSystem.AddDevice<Mouse>();
        }

        void Start()
        {
            if (m_RunOnStart)
                StartCoroutine(Run());
        }

        public IEnumerator Run()
        {
            Directory.CreateDirectory(m_OutputDir);

            // Deterministic frames: PlayerController integrates gravity and speed
            // over Time.deltaTime, and a frame that draws 5.7M splats can cost a
            // second of wall clock, which would turn one step into a ten metre
            // lunge. captureDeltaTime pins it.
            Time.captureDeltaTime = 1f / 60f;

            m_SplatRenderers = FindObjectsByType<GaussianSplatting.Runtime.GaussianSplatRenderer>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            m_Body = FindFirstObjectByType<CharacterController>();
            m_Input = FindFirstObjectByType<VRPlayerControllerInput>();
            if (m_Body == null || m_Input == null)
            {
                Fail($"scene is missing CharacterController={m_Body != null} " +
                     $"VRPlayerControllerInput={m_Input != null}");
                Finish();
                yield break;
            }

            if (m_Camera == null)
                m_Camera = FindFirstObjectByType<Camera>();
            m_Hmd = m_Camera != null ? m_Camera.transform : m_Body.transform;

            yield return WaitForSimulatedController();
            if (m_Keyboard == null)
            {
                Finish();
                yield break;
            }

            KeepKeyboardAwake();

            Line($"start {Fmt(m_Body.transform.position)}, head yaw {m_Hmd.eulerAngles.y:0.0}, " +
                 $"{m_Waypoints.Length} waypoints");

            SetSplatsVisible(false);
            yield return Diagnose();
            yield return Shot("00_spawn");

            for (int i = 0; i < m_Waypoints.Length; ++i)
            {
                yield return WalkTo(m_Waypoints[i], i);
                yield return Shot($"{i + 1:00}_wp");
            }

            Press();
            Finish();
        }

        IEnumerator WaitForSimulatedController()
        {
            var simulator = FindFirstObjectByType<XRDeviceSimulator>(FindObjectsInactive.Include);
            if (simulator == null)
            {
                Fail("no XRDeviceSimulator in the scene - nothing to press keys at");
                yield break;
            }

            for (int i = 0; i < 300; ++i)
            {
                m_Keyboard = Keyboard.current;
                var left = GetLeftSimulatedController();
                if (m_Keyboard != null && left != null)
                {
                    Line($"simulator ready: keyboard '{m_Keyboard.name}', " +
                         $"left controller '{left.name}', axis2DTargets {simulator.axis2DTargets}");
                    yield break;
                }
                yield return null;
            }

            m_Keyboard = null;
            Fail("the simulator never produced a left XRSimulatedController");
        }

        static XRSimulatedController GetLeftSimulatedController()
        {
            foreach (var d in InputSystem.devices)
            {
                if (!(d is XRSimulatedController c))
                    continue;
                foreach (var usage in c.usages)
                {
                    if (usage == UnityEngine.InputSystem.CommonUsages.LeftHand)
                        return c;
                }
            }
            return null;
        }

        // A batch mode editor never has focus, and the input system's default
        // background behaviour is to reset and disable every device that is not
        // marked as working in the background. A disabled keyboard swallows
        // everything written to it, so the whole chain goes quiet and looks broken.
        // Re-enabling the device is usually enough; if the focus handling puts it
        // straight back to sleep, the behaviour itself is relaxed - and restored in
        // Finish, because that setting lives in a project asset, not in the scene.
        void KeepKeyboardAwake()
        {
            if (!m_Keyboard.enabled)
            {
                m_BackgroundBehaviorToRestore = InputSystem.settings.backgroundBehavior;
                InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                m_BackgroundBehaviorChanged = true;
                InputSystem.EnableDevice(m_Keyboard);
            }

#if UNITY_EDITOR
            // The second gate, and the one that is easy to miss because the device
            // reports itself as enabled either way: in play mode the editor routes
            // keyboard and pointer input only to a focused Game View. Batch mode has
            // no Game View to focus, so every key is dropped on the way to the
            // actions while the device itself looks perfectly healthy.
            if (InputSystem.settings.editorInputBehaviorInPlayMode !=
                InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView)
            {
                m_EditorBehaviorToRestore = InputSystem.settings.editorInputBehaviorInPlayMode;
                InputSystem.settings.editorInputBehaviorInPlayMode =
                    InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
                m_EditorBehaviorChanged = true;
            }
#endif

            Line($"keyboard: enabled={m_Keyboard.enabled}, backgroundBehavior=" +
                 $"{InputSystem.settings.backgroundBehavior}" +
#if UNITY_EDITOR
                 $", editorInputBehaviorInPlayMode={InputSystem.settings.editorInputBehaviorInPlayMode}"
#else
                 ""
#endif
                 );
        }

        // Both of those live in a project asset, not in the scene, so they go back
        // the way they were however the run ends.
        void RestoreBackgroundBehavior()
        {
            if (m_BackgroundBehaviorChanged)
            {
                InputSystem.settings.backgroundBehavior = m_BackgroundBehaviorToRestore;
                m_BackgroundBehaviorChanged = false;
            }
#if UNITY_EDITOR
            if (m_EditorBehaviorChanged)
            {
                InputSystem.settings.editorInputBehaviorInPlayMode = m_EditorBehaviorToRestore;
                m_EditorBehaviorChanged = false;
            }
#endif
        }

        // Hold Left Shift + W for a moment and report every link in the chain, so
        // a dead one names itself instead of just showing up as "did not move".
        IEnumerator Diagnose()
        {
            Press(Key.LeftShift, Key.W);
            for (int i = 0; i < 10; ++i)
                yield return null;

            var simulator = FindFirstObjectByType<XRDeviceSimulator>(FindObjectsInactive.Include);
            var left = GetLeftSimulatedController();

            string keys = m_Keyboard == null
                ? "no keyboard"
                : $"leftShift={m_Keyboard.leftShiftKey.isPressed} w={m_Keyboard.wKey.isPressed}";
            string stick = left == null ? "no controller" : left.primary2DAxis.ReadValue().ToString();
            string simState = simulator == null
                ? "no simulator"
                : $"enabled={simulator.isActiveAndEnabled} manipulatingLeftController=" +
                  $"{simulator.manipulatingLeftController} axis2DTargets={simulator.axis2DTargets}";

            var inputType = m_Input.GetType();
            var canMoveField = inputType.GetField("canMove",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var moveField = inputType.GetField("moveAction",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            object canMove = canMoveField?.GetValue(m_Input);

            string actionState = "moveAction not found";
            if (moveField?.GetValue(m_Input) is InputActionProperty prop)
            {
                var action = prop.action;
                actionState = action == null
                    ? "moveAction has no action"
                    : $"'{action.name}' enabled={action.enabled} controls={action.controls.Count} " +
                      $"value={action.ReadValue<Vector2>()}";
            }

            Line($"diag keys: {keys}, device enabled={m_Keyboard?.enabled}, " +
                 $"focused={Application.isFocused}, backgroundBehavior=" +
                 $"{InputSystem.settings.backgroundBehavior}");
            Line($"diag simulator: {simState}");
            Line($"diag left stick: {stick}");
            Line($"diag input: canMove={canMove} MoveAxis={m_Input.MoveAxis} enabled={m_Input.isActiveAndEnabled}");
            Line($"diag moveAction: {actionState}");

            Press();
            yield return null;
        }

        IEnumerator WalkTo(Vector3 target, int index)
        {
            int maxFrames = Mathf.CeilToInt(m_WaypointTimeout * 60f);
            float bestDist = Flat(target - m_Body.transform.position).magnitude;
            int lastProgressFrame = 0;
            float minY = float.MaxValue, maxY = float.MinValue;
            float walked = 0f;
            bool fell = false;
            int f = 0;

            for (; f < maxFrames; ++f)
            {
                Vector3 pos = m_Body.transform.position;
                Vector3 toTarget = Flat(target - pos);
                float dist = toTarget.magnitude;
                if (dist < 0.4f)
                    break;

                Press(KeysFor(toTarget.normalized));
                yield return null;

                walked += Flat(m_Body.transform.position - pos).magnitude;
                m_PeakMoveAxis = Mathf.Max(m_PeakMoveAxis, m_Input.MoveAxis.magnitude);

                float y = m_Body.transform.position.y;
                minY = Mathf.Min(minY, y);
                maxY = Mathf.Max(maxY, y);
                if (y < m_FallenBelowY)
                {
                    fell = true;
                    break;
                }

                if (dist < bestDist - 0.05f)
                {
                    bestDist = dist;
                    lastProgressFrame = f;
                }
            }

            Press();

            float remaining = Flat(target - m_Body.transform.position).magnitude;
            string where = Fmt(m_Body.transform.position);

            if (fell)
            {
                Fail($"wp{index} fell out of the scene at {where}");
                yield break;
            }

            if (remaining < 0.4f)
                Line($"wp{index} reached {Fmt(target)} in {f / 60f:0.0}s, floor y {minY:0.00}..{maxY:0.00}, " +
                     $"stick peak {m_PeakMoveAxis:0.00}");
            else if (walked < 0.2f)
                Fail($"wp{index} never moved - stuck at {where}, {remaining:0.0} m short, " +
                     $"MoveAxis {m_Input.MoveAxis}, IsMovePressed {m_Input.IsMovePressed}");
            else if (f - lastProgressFrame > 120)
                Line($"wp{index} BLOCKED {remaining:0.0} m short at {where} after walking " +
                     $"{walked:0.0} m - wall or step in the way");
            else
                Line($"wp{index} ran out of time {remaining:0.0} m short at {where}, walked {walked:0.0} m");
        }

        // Which of the eight WASD directions sends the player closest to where it
        // is going. PlayerController turns the stick into motion with the head's
        // yaw, so the stick is the target direction expressed in head space.
        Key[] KeysFor(Vector3 worldDir)
        {
            Vector3 local = Quaternion.Euler(0f, -m_Hmd.eulerAngles.y, 0f) * worldDir;
            bool w = local.z > 0.383f, s = local.z < -0.383f;
            bool d = local.x > 0.383f, a = local.x < -0.383f;

            var keys = new System.Collections.Generic.List<Key> { Key.LeftShift };
            if (w) keys.Add(Key.W);
            if (s) keys.Add(Key.S);
            if (d) keys.Add(Key.D);
            if (a) keys.Add(Key.A);
            return keys.ToArray();
        }

        // InputState.Change rather than QueueStateEvent: a queued event goes
        // through the focus-gated pipeline, and a batch mode editor is never
        // focused, so the keyboard is a disabled device and the events are thrown
        // away - which looks exactly like a locomotion that does not respond.
        // Change writes the state straight onto the device and still notifies the
        // action monitors, which is what the bindings are listening to.
        //
        // Held state persists until it is changed, so only a change is worth writing.
        void Press(params Key[] keys)
        {
            keys ??= System.Array.Empty<Key>();
            if (SameKeys(keys, m_HeldKeys))
                return;

            InputState.Change(m_Keyboard, new KeyboardState(keys));
            m_HeldKeys = keys;
        }

        static bool SameKeys(Key[] a, Key[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; ++i)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        IEnumerator Shot(string name)
        {
            // Let the rig settle first. The camera is not bolted to the capsule:
            // it rides VRIK's avatar head and only snaps back onto it when
            // locomotion ends, so a shot taken the instant the stick is released
            // is a shot of the player's own back from a couple of metres behind.
            for (int i = 0; i < 45; ++i)
                yield return null;

            SetSplatsVisible(true);
            yield return null;
            if (m_Camera == null)
                yield break;

            var rt = new RenderTexture(m_ShotWidth, m_ShotHeight, 24, RenderTextureFormat.ARGB32);
            RenderTexture prevTarget = m_Camera.targetTexture;
            RenderTexture prevActive = RenderTexture.active;

            m_Camera.targetTexture = rt;
            m_Camera.Render();

            RenderTexture.active = rt;
            var tex = new Texture2D(m_ShotWidth, m_ShotHeight, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, m_ShotWidth, m_ShotHeight), 0, 0);
            tex.Apply();

            m_Camera.targetTexture = prevTarget;
            RenderTexture.active = prevActive;

            string path = Path.Combine(m_OutputDir, $"loco_{name}.png");
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Line($"shot {path} from {Fmt(m_Camera.transform.position)}");

            Destroy(tex);
            rt.Release();
            Destroy(rt);
            SetSplatsVisible(false);
        }

        void SetSplatsVisible(bool on)
        {
            foreach (var r in m_SplatRenderers)
                if (r != null)
                    r.enabled = on;
        }

        void Finish()
        {
            SetSplatsVisible(true);
            Time.captureDeltaTime = 0f;
            RestoreBackgroundBehavior();

            if (m_PeakMoveAxis < 0.2f)
                Fail($"the Move action never went past the walk threshold (peak {m_PeakMoveAxis:0.00}) - " +
                     "the keys never reached the locomotion");

            Passed = m_Failures == 0;
            Done = true;
            Report = (Passed ? "[loco] PASS\n" : $"[loco] FAIL ({m_Failures})\n") + m_Log;
            Directory.CreateDirectory(m_OutputDir);
            File.WriteAllText(Path.Combine(m_OutputDir, "locomotion-report.txt"), Report);
            Debug.Log(Report);
        }

        void Line(string s) => m_Log.AppendLine("[loco] " + s);

        void Fail(string s)
        {
            ++m_Failures;
            m_Log.AppendLine("[loco] FAIL " + s);
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        static string Fmt(Vector3 v) => string.Format(
            CultureInfo.InvariantCulture, "({0:0.00}, {1:0.00}, {2:0.00})", v.x, v.y, v.z);
    }
}
