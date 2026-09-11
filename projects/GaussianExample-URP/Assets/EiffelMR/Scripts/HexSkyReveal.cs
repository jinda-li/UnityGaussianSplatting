using System.Collections;
using UnityEngine;

namespace EiffelMR
{
    // Drives the hexagon-by-hexagon arrival of the world on the shell around the
    // player, and hands the passthrough over to it at the right moment.
    //
    // The order matters and is easy to get wrong. Passthrough has to stay
    // visible while the tiles are still sparse - that is the whole effect, the
    // player's own room showing through the gaps - and it can only be switched
    // off once enough tiles have landed to hide the seam. Cutting it at the
    // start gives a black void behind the tiles; cutting it at the end gives a
    // visible pop when the last tile lands on top of an already-complete sky.
    [RequireComponent(typeof(MeshRenderer))]
    public class HexSkyReveal : MonoBehaviour
    {
        [Tooltip("Seconds for the patchwork to close. Match the dive length or " +
                 "the world finishes growing before it finishes appearing.")]
        public float m_Duration = 2.2f;

        [Tooltip("Curve of the fill. Ease-out reads best: a scatter of tiles " +
                 "arrives immediately, then the gaps close.")]
        public AnimationCurve m_Curve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Tooltip("Fill fraction at which the passthrough background is switched " +
                 "off. Below about 0.6 the seam shows through the remaining gaps.")]
        [Range(0f, 1f)] public float m_PassthroughCutAt = 0.72f;

        [Header("Optional")]
        [Tooltip("Called with true when the tiles have covered enough to hide " +
                 "the room. Wire to whatever owns the passthrough background.")]
        public UnityEngine.Events.UnityEvent<bool> m_PassthroughVisible;

        MeshRenderer m_Renderer;
        MaterialPropertyBlock m_Block;
        static readonly int k_RevealId = Shader.PropertyToID("_Reveal");
        static readonly int k_OriginId = Shader.PropertyToID("_Origin");
        Coroutine m_Running;

        public float Reveal { get; private set; }

        // Resolved on demand rather than only in Awake.
        //
        // EiffelBubbleSession calls Clear() from its own OnEnable, and Unity
        // does not run every Awake in a scene before every OnEnable - it runs
        // Awake then OnEnable per object, in an order that is not defined. So
        // whether this component had been woken when the session first cleared
        // it came down to which object Unity happened to initialise first, and
        // when it lost that race the shell threw on m_Renderer and the sky
        // reveal was dead for the rest of the session.
        MeshRenderer Shell
        {
            get
            {
                if (!m_Renderer)
                    m_Renderer = GetComponent<MeshRenderer>();
                return m_Renderer;
            }
        }

        MaterialPropertyBlock Block =>
            m_Block ?? (m_Block = new MaterialPropertyBlock());

        void Awake()
        {
            SetReveal(0f);
            if (Shell)
                Shell.enabled = false;
        }

        /// Start filling in, spreading outwards from `origin` (a world position,
        /// normally where the miniature landed).
        public void Play(Vector3 origin)
        {
            if (m_Running != null)
                StopCoroutine(m_Running);
            m_Running = StartCoroutine(PlayRoutine(origin));
        }

        public void Clear()
        {
            if (m_Running != null)
                StopCoroutine(m_Running);
            m_Running = null;
            SetReveal(0f);
            if (Shell)
                Shell.enabled = false;
            m_PassthroughVisible?.Invoke(true);
        }

        IEnumerator PlayRoutine(Vector3 origin)
        {
            if (Shell)
                Shell.enabled = true;
            Vector3 dir = origin - transform.position;
            if (dir.sqrMagnitude < 1e-4f)
                dir = transform.forward;
            dir.Normalize();

            if (Shell)
            {
                Shell.GetPropertyBlock(Block);
                Block.SetVector(k_OriginId, new Vector4(dir.x, dir.y, dir.z, 0f));
                Shell.SetPropertyBlock(Block);
            }

            bool cut = false;
            float t = 0f;
            while (t < m_Duration)
            {
                t += Time.deltaTime;
                float fill = m_Curve.Evaluate(Mathf.Clamp01(t / m_Duration));
                SetReveal(fill);
                if (!cut && fill >= m_PassthroughCutAt)
                {
                    cut = true;
                    m_PassthroughVisible?.Invoke(false);
                }
                yield return null;
            }
            SetReveal(1f);
            if (!cut)
                m_PassthroughVisible?.Invoke(false);
            m_Running = null;
        }

        void SetReveal(float value)
        {
            Reveal = value;
            if (!Shell)
                return;
            Shell.GetPropertyBlock(Block);
            Block.SetFloat(k_RevealId, value);
            Shell.SetPropertyBlock(Block);
        }
    }
}
