using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace EiffelMR
{
    // The blue bubble the miniature arrives in, and the poke that breaks it.
    //
    // Two ways in, because the two input modes on a Quest 3 behave differently
    // and the interaction has to work whichever the player picked up last:
    //
    //  - XRI's own poke path, if an XRPokeInteractor is driving a hand or a
    //    controller. This is the accurate one: XRI tracks the poke direction and
    //    depth and rejects a finger that merely brushes past.
    //  - A direct distance test against a list of tips. Hand tracking on Quest
    //    can be running without a poke interactor set up on the rig, and a
    //    bubble you cannot pop is a dead end for the player, so this is the
    //    safety net rather than the main path.
    //
    // The shell is deliberately larger than the tower inside it. A bubble sized
    // to the model leaves nowhere to aim: the player's finger reaches the tower
    // before it reaches the surface, and the pop reads as grabbing rather than
    // as bursting.
    [RequireComponent(typeof(SphereCollider))]
    public class TowerBubble : MonoBehaviour
    {
        [Header("Contents")]
        [Tooltip("The miniature - normally the splat rig, held at table scale. " +
                 "Released as a physics object when the bubble pops.")]
        public Rigidbody m_Tower;

        [Tooltip("Keep the shell centred on the miniature instead of parenting " +
                 "it. The splat rig is placed and owned by TabletopDiveController " +
                 "and must not be re-parented under a decorative shell; the " +
                 "bubble follows it instead.")]
        public bool m_FollowTower = true;

        [Tooltip("Metres above the miniature's origin to centre the shell. A " +
                 "tower stands on its own origin, so a shell centred there holds " +
                 "the bottom half and cuts off the top.")]
        public float m_CentreOffset = 0.14f;

        [Header("Shell")]
        public Renderer m_Shell;

        [Tooltip("Seconds the shell takes to swell and vanish once poked.")]
        public float m_PopDuration = 0.22f;

        [Tooltip("Particle burst for the film breaking up. Optional.")]
        public ParticleSystem m_PopParticles;

        public AudioSource m_PopAudio;

        [Header("Idle motion")]
        [Tooltip("Metres of vertical bob. The bubble has to look buoyant or it " +
                 "reads as a solid ball on an invisible stand.")]
        public float m_BobHeight = 0.025f;

        public float m_BobPeriod = 3.4f;
        public float m_SpinRate = 8f;

        [Header("Poke")]
        [Tooltip("Extra transforms treated as finger tips - hand-tracking joints, " +
                 "controller tips. XRPokeInteractors in the scene are found " +
                 "automatically and do not need listing here.")]
        public List<Transform> m_ExtraPokeTips = new List<Transform>();

        [Tooltip("How far inside the shell a tip has to reach to count as a poke. " +
                 "Touching the surface exactly is not something hand tracking can " +
                 "resolve, so the test is against a slightly smaller sphere.")]
        public float m_PokeDepth = 0.025f;

        [Tooltip("Ignore pokes for this long after spawning, so a hand that " +
                 "happens to be where the bubble appears does not pop it instantly.")]
        public float m_ArmDelay = 0.45f;

        public event System.Action<TowerBubble> Popped;

        public bool IsPopped { get; private set; }

        SphereCollider m_Collider;
        Vector3 m_RestPosition;
        float m_SpawnTime;
        MaterialPropertyBlock m_Block;
        static readonly int k_PopId = Shader.PropertyToID("_Pop");
        readonly List<Transform> m_Tips = new List<Transform>();
        float m_TipRefreshTime;

        void Awake()
        {
            m_Collider = GetComponent<SphereCollider>();
            m_Collider.isTrigger = true;
            m_Block = new MaterialPropertyBlock();
        }

        void OnEnable()
        {
            IsPopped = false;
            m_RestPosition = transform.localPosition;
            m_SpawnTime = Time.time;
            if (m_Shell)
            {
                m_Shell.enabled = true;
                SetPop(0f);
            }
            if (m_Tower)
                m_Tower.isKinematic = true;
        }

        void Update()
        {
            if (IsPopped)
                return;

            float t = Time.time - m_SpawnTime;
            float bob = Mathf.Sin(t * Mathf.PI * 2f / Mathf.Max(m_BobPeriod, 0.01f));
            if (m_FollowTower && m_Tower)
            {
                // The shell tracks the miniature rather than carrying it, so the
                // bob is a wobble of the film around a tower that stays put -
                // which is also what stops a bobbing bubble from dragging the
                // splat rig up and down with it.
                transform.position = m_Tower.transform.position
                                     + Vector3.up * (m_CentreOffset + bob * m_BobHeight);
            }
            else
            {
                transform.localPosition = m_RestPosition + Vector3.up * (bob * m_BobHeight);
            }
            transform.Rotate(Vector3.up, m_SpinRate * Time.deltaTime, Space.Self);

            if (t >= m_ArmDelay && TipInside())
                Pop();
        }

        bool TipInside()
        {
            RefreshTips();
            float radius = m_Collider.radius * MaxAbs(transform.lossyScale) - m_PokeDepth;
            if (radius <= 0f)
                return false;

            float sqr = radius * radius;
            Vector3 centre = transform.TransformPoint(m_Collider.center);
            foreach (var tip in m_Tips)
            {
                if (tip && (tip.position - centre).sqrMagnitude <= sqr)
                    return true;
            }
            return false;
        }

        void RefreshTips()
        {
            // Interactors come and go as the player switches between hands and
            // controllers, so the list is rebuilt periodically rather than
            // cached once - but not every frame, because the scene query is not
            // free and a fifth of a second is far below the reaction time this
            // interaction needs.
            if (Time.time - m_TipRefreshTime < 0.2f && m_Tips.Count > 0)
                return;
            m_TipRefreshTime = Time.time;

            m_Tips.Clear();
            foreach (var tip in m_ExtraPokeTips)
            {
                if (tip)
                    m_Tips.Add(tip);
            }

            var pokes = FindObjectsByType<XRPokeInteractor>(FindObjectsInactive.Exclude,
                                                            FindObjectsSortMode.None);
            foreach (var poke in pokes)
            {
                if (!poke || !poke.isActiveAndEnabled)
                    continue;
                // GetAttachTransform is the supported way in: XRPokeInteractor
                // resolves its own poke point through it, and reading a public
                // field instead would miss rigs that override it per-hand.
                var tip = poke.GetAttachTransform(null);
                m_Tips.Add(tip ? tip : poke.transform);
            }
        }

        static float MaxAbs(Vector3 v)
        {
            return Mathf.Max(Mathf.Abs(v.x), Mathf.Max(Mathf.Abs(v.y), Mathf.Abs(v.z)));
        }

        // Also reachable from XRI: hook this to an XRSimpleInteractable's
        // selectEntered when the rig has poke filters set up properly.
        public void Pop()
        {
            if (IsPopped)
                return;
            IsPopped = true;
            m_Collider.enabled = false;
            StartCoroutine(PopRoutine());
        }

        IEnumerator PopRoutine()
        {
            if (m_PopParticles)
                m_PopParticles.Play();
            if (m_PopAudio)
                m_PopAudio.Play();

            // Let go of the tower on the first frame of the pop, not at the end
            // of it: the film breaking and the model starting to fall have to be
            // the same event or the tower looks like it was waiting for a cue.
            if (m_Tower)
            {
                // Only unparent something that is actually ours. The splat rig
                // is not a child of the shell and re-parenting it would move it
                // out from under the dive controller.
                if (m_Tower.transform.parent == transform)
                    m_Tower.transform.SetParent(null, true);
                m_Tower.isKinematic = false;
                m_Tower.useGravity = true;
                m_Tower.linearVelocity = Vector3.down * 0.15f;
                m_Tower.angularVelocity = Random.insideUnitSphere * 0.6f;
            }
            Popped?.Invoke(this);

            float t = 0f;
            while (t < m_PopDuration)
            {
                t += Time.deltaTime;
                SetPop(Mathf.Clamp01(t / m_PopDuration));
                yield return null;
            }
            if (m_Shell)
                m_Shell.enabled = false;
        }

        void SetPop(float value)
        {
            if (!m_Shell)
                return;
            m_Shell.GetPropertyBlock(m_Block);
            m_Block.SetFloat(k_PopId, value);
            m_Shell.SetPropertyBlock(m_Block);
        }
    }
}
