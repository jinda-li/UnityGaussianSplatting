using UnityEngine;
using UnityEngine.InputSystem;

namespace EiffelMR
{
    // Keyboard and mouse stand-ins for a headset, so the whole Eiffel flow can
    // be played on a desktop.
    //
    //   Space        the one button (spawn a bubble / back to a bubble)
    //   Left click   poke the bubble under the cursor
    //   E            pick the miniature up (it floats in front of you)
    //   T            throw it at the ring
    //   G            throw it wide, to see a miss handled by physics
    //   F            drop it
    //   Right drag   look around
    //
    // It drives the same public entry points a hand does - TowerBubble.Pop and
    // ThrownTower.Throw - so what works here works with a hand, less the hand
    // tracking itself.
    public class DesktopEiffelRig : MonoBehaviour
    {
        public EiffelBubbleSession m_Session;
        public Camera m_Camera;

        [Tooltip("Degrees per pixel of mouse movement while looking.")]
        public float m_LookSpeed = 0.15f;

        [Tooltip("Where a held miniature floats, in camera space.")]
        public Vector3 m_HoldOffset = new Vector3(0.18f, -0.28f, 0.55f);

        [Tooltip("Time of flight the throw at the ring is solved for.")]
        public float m_ThrowFlightTime = 0.9f;

        public bool m_ShowHelp = true;

        float m_Yaw, m_Pitch;
        bool m_Holding;

        void Awake()
        {
            if (!m_Camera)
                m_Camera = Camera.main;
            if (!m_Session)
                m_Session = FindFirstObjectByType<EiffelBubbleSession>();
            var e = transform.eulerAngles;
            m_Yaw = e.y;
            m_Pitch = e.x > 180f ? e.x - 360f : e.x;
        }

        void Update()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null)
                return;

            if (mouse.rightButton.isPressed)
            {
                Vector2 d = mouse.delta.ReadValue();
                m_Yaw += d.x * m_LookSpeed;
                m_Pitch = Mathf.Clamp(m_Pitch - d.y * m_LookSpeed, -85f, 85f);
                transform.rotation = Quaternion.Euler(m_Pitch, m_Yaw, 0f);
            }

            if (kb.spaceKey.wasPressedThisFrame && m_Session)
            {
                m_Holding = false;
                m_Session.Toggle();
            }

            if (mouse.leftButton.wasPressedThisFrame)
                TryPoke(mouse.position.ReadValue());

            var tower = m_Session ? m_Session.m_Tower : null;
            if (!tower)
                return;

            if (kb.eKey.wasPressedThisFrame && CanHandle())
                m_Holding = true;
            if (kb.fKey.wasPressedThisFrame && m_Holding)
                Release(tower, Vector3.zero, false);
            if (kb.tKey.wasPressedThisFrame && (m_Holding || CanHandle()))
                Release(tower, tower.AimAtRing(m_ThrowFlightTime), true);
            if (kb.gKey.wasPressedThisFrame && (m_Holding || CanHandle()))
                Release(tower, tower.AimAtRing(m_ThrowFlightTime)
                               + transform.right * 5f, true);
        }

        void LateUpdate()
        {
            if (!m_Holding || !m_Session || !m_Session.m_Tower || !m_Camera)
                return;
            var body = m_Session.m_Tower.GetComponent<Rigidbody>();
            if (body)
                body.isKinematic = true;
            Transform cam = m_Camera.transform;
            Vector3 flat = cam.forward;
            flat.y = 0f;
            m_Session.m_Tower.Teleport(
                cam.TransformPoint(m_HoldOffset),
                Quaternion.LookRotation(flat.sqrMagnitude > 1e-4f ? -flat : Vector3.back));
        }

        bool CanHandle() =>
            m_Session && m_Session.Current == EiffelBubbleSession.State.Loose;

        void Release(ThrownTower tower, Vector3 velocity, bool thrown)
        {
            m_Holding = false;
            var body = tower.GetComponent<Rigidbody>();
            if (thrown && tower.Throw(velocity))
                return;             // the scripted arc has it
            // A miss, or a drop: physics owns it.
            if (body)
            {
                body.isKinematic = false;
                body.linearVelocity = velocity;
            }
        }

        // A ray-sphere test against the bubble, not a collider hit: the shell is
        // a trigger so a finger can go into it, and the tower's own collider sits
        // inside it and would take the click.
        void TryPoke(Vector2 screen)
        {
            if (!m_Camera || !m_Session || !m_Session.m_Bubble)
                return;
            var bubble = m_Session.m_Bubble;
            if (bubble.IsPopped || !bubble.isActiveAndEnabled)
                return;
            Ray ray = m_Camera.ScreenPointToRay(screen);
            Vector3 c = bubble.transform.position;
            float r = bubble.transform.lossyScale.x * 0.5f;
            Vector3 toC = c - ray.origin;
            float along = Vector3.Dot(toC, ray.direction);
            if (along < 0f)
                return;
            if ((toC - ray.direction * along).sqrMagnitude <= r * r)
                bubble.Pop();
        }

        void OnGUI()
        {
            if (!m_ShowHelp)
                return;
            string state = m_Session ? m_Session.Current.ToString() : "-";
            GUI.Label(new Rect(12, 10, 640, 22),
                      "[" + state + "]  Space: bubble   Click: poke   E: pick up   " +
                      "T: throw at ring   G: throw wide   F: drop   RMB: look");
        }
    }
}
