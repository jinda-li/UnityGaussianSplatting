using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using GardenMR;

namespace EiffelMR
{
    // One button, four states, and the rule that the button always gets you back.
    //
    //   Idle       nothing in the room but passthrough
    //   Bubble     the miniature floating in its shell, waiting to be poked
    //   Loose      the shell is gone; the miniature is a physics object the
    //              player can catch, examine, put down, pick up and throw
    //   Immersive  the throw landed in the ring and the world grew around them
    //
    // The button cycles Idle -> Bubble and, from anywhere else, back to Bubble.
    // That last part is the important one. This is a demo people will hand to
    // someone who has never worn a headset: whatever they have managed to do -
    // thrown the tower behind a sofa, ended up inside the world, popped the
    // bubble and lost the model under a table - one press has to put a bubble
    // back in front of them. There is no failure state that needs explaining.
    public class EiffelBubbleSession : MonoBehaviour
    {
        public enum State { Idle, Bubble, Loose, Immersive }

        [Header("Scene")]
        public TowerBubble m_Bubble;
        public ThrownTower m_Tower;
        public LandingRing m_Ring;
        public HexSkyReveal m_Reveal;
        public TabletopDiveController m_Dive;

        [Tooltip("Root holding the bubble, the miniature and the ring. Hidden in Idle.")]
        public GameObject m_PlayGroup;

        [Header("Placement")]
        [Tooltip("Camera the bubble is placed in front of. Falls back to Camera.main.")]
        public Transform m_Head;

        [Tooltip("Metres in front of the player the bubble appears.")]
        public float m_BubbleDistance = 0.55f;

        [Tooltip("Height above the floor, in metres. Chest height, so the player " +
                 "can poke it without reaching up or bending.")]
        public float m_BubbleHeight = 1.25f;

        [Tooltip("Metres in front of the player the landing ring is drawn.")]
        public float m_RingDistance = 2.0f;

        [Tooltip("Floor height in world space. On Quest with a room set up this " +
                 "is the XR Origin's own height.")]
        public float m_FloorY;

        [Header("Input")]
        [Tooltip("The one button. Defaults to the right controller's A/X.")]
        public InputAction m_ToggleAction = new InputAction(
            "EiffelToggle", InputActionType.Button,
            "<XRController>{RightHand}/primaryButton");

        [Tooltip("Ignore repeat presses for this long. A Quest face button " +
                 "bounces, and a double fire here would spawn and despawn in one " +
                 "press.")]
        public float m_Debounce = 0.35f;

        public State Current { get; private set; } = State.Idle;

        float m_LastToggle;

        void Awake()
        {
            if (!m_Head && Camera.main)
                m_Head = Camera.main.transform;
            if (!m_Dive)
                m_Dive = FindFirstObjectByType<TabletopDiveController>();
        }

        void OnEnable()
        {
            m_ToggleAction.performed += OnToggle;
            m_ToggleAction.Enable();

            if (m_Bubble)
                m_Bubble.Popped += OnBubblePopped;
            if (m_Tower)
                m_Tower.Landed += OnTowerLanded;

            EnterIdle();
        }

        void OnDisable()
        {
            m_ToggleAction.performed -= OnToggle;
            m_ToggleAction.Disable();

            if (m_Bubble)
                m_Bubble.Popped -= OnBubblePopped;
            if (m_Tower)
                m_Tower.Landed -= OnTowerLanded;
        }

        void OnToggle(InputAction.CallbackContext ctx)
        {
            if (Time.unscaledTime - m_LastToggle < m_Debounce)
                return;
            m_LastToggle = Time.unscaledTime;

            if (Current == State.Idle)
                SpawnBubble();
            else
                ResetToBubble();
        }

        void EnterIdle()
        {
            Current = State.Idle;
            if (m_PlayGroup)
                m_PlayGroup.SetActive(false);
            if (m_Reveal)
                m_Reveal.Clear();
        }

        public void SpawnBubble()
        {
            if (!m_PlayGroup || !m_Bubble || !m_Tower)
                return;

            PlaceInFront();
            m_PlayGroup.SetActive(true);

            m_Tower.ResetToMiniature();
            if (m_Tower.transform.parent == m_Bubble.transform)
            {
                // Placeholder case: the model belongs to the shell.
                m_Tower.transform.localPosition = Vector3.zero;
                m_Tower.transform.localRotation = Quaternion.identity;
            }
            else
            {
                // The real case: the miniature is the splat rig, which
                // TabletopDiveController owns and places. Ask it to bring the
                // rig to the player rather than moving it ourselves - it has
                // the reachability and floor-height logic already.
                if (m_Dive)
                    m_Dive.RequestSummon();
            }

            // Re-enabling the bubble runs its own OnEnable, which re-arms the
            // poke test and resets the shell.
            m_Bubble.gameObject.SetActive(false);
            m_Bubble.gameObject.SetActive(true);

            Current = State.Bubble;
        }

        /// Whatever the player has got themselves into, put a bubble back in
        /// front of them.
        public void ResetToBubble()
        {
            StopAllCoroutines();
            StartCoroutine(ResetRoutine());
        }

        IEnumerator ResetRoutine()
        {
            if (m_Dive && m_Dive.CurrentState != TabletopDiveController.State.Place)
            {
                m_Dive.Return();
                // Return() runs its own transition; wait it out rather than
                // spawning a bubble into the middle of the world shrinking.
                while (m_Dive.IsBusy)
                    yield return null;
            }

            if (m_Reveal)
                m_Reveal.Clear();
            SpawnBubble();
        }

        void PlaceInFront()
        {
            if (!m_Head)
                return;

            // Flatten the head's forward: a bubble placed along the true view
            // direction ends up on the floor or over the player's head whenever
            // they happen to be looking up or down when they press the button.
            Vector3 forward = m_Head.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f)
                forward = Vector3.forward;
            forward.Normalize();

            Vector3 origin = m_Head.position;
            // Only meaningful for the placeholder; when the shell is following
            // the splat rig it overwrites this on its first Update.
            if (m_Bubble && !m_Bubble.m_FollowTower)
            {
                m_Bubble.transform.position = new Vector3(
                    origin.x + forward.x * m_BubbleDistance,
                    m_FloorY + m_BubbleHeight,
                    origin.z + forward.z * m_BubbleDistance);
            }

            if (m_Ring)
            {
                m_Ring.transform.position = new Vector3(
                    origin.x + forward.x * m_RingDistance,
                    m_FloorY + 0.005f,
                    origin.z + forward.z * m_RingDistance);
                m_Ring.m_FloorY = m_FloorY;
            }
        }

        void OnBubblePopped(TowerBubble bubble)
        {
            Current = State.Loose;
        }

        void OnTowerLanded(Vector3 impact)
        {
            Current = State.Immersive;
        }
    }
}
