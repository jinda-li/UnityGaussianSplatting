using UnityEngine;

namespace EiffelMR
{
    // The blue circle on the floor that the throw has to land in.
    //
    // It is a target, so it has to read as one before the player throws: it sits
    // on the floor in front of them, glows, and brightens as the miniature gets
    // near it. The hit test is a plain horizontal distance check rather than a
    // trigger volume, because a thrown object at Quest frame rates can cross a
    // flat trigger between two physics steps and miss it entirely.
    public class LandingRing : MonoBehaviour
    {
        [Tooltip("Radius the throw has to land inside, in metres.")]
        public float m_Radius = 0.55f;

        [Tooltip("Floor height in world space. The ring sits here and a throw is " +
                 "judged when the miniature crosses it.")]
        public float m_FloorY;

        [Header("Look")]
        public Renderer m_Renderer;
        public Color m_IdleColor = new Color(0.25f, 0.6f, 1f, 0.55f);
        public Color m_ArmedColor = new Color(0.5f, 0.9f, 1f, 0.95f);

        [Tooltip("Horizontal distance at which the ring starts brightening.")]
        public float m_ProximityRange = 1.6f;

        [Tooltip("Pulses per second while waiting for a throw.")]
        public float m_PulseRate = 0.8f;

        MaterialPropertyBlock m_Block;
        static readonly int k_ColorId = Shader.PropertyToID("_BaseColor");
        static readonly int k_EmissionId = Shader.PropertyToID("_EmissionColor");
        Transform m_Watched;

        void Awake()
        {
            m_Block = new MaterialPropertyBlock();
            if (m_FloorY == 0f)
                m_FloorY = transform.position.y;
        }

        public void Watch(Transform target)
        {
            m_Watched = target;
        }

        void Update()
        {
            if (!m_Renderer)
                return;

            float near = 0f;
            if (m_Watched)
            {
                float d = HorizontalDistance(m_Watched.position);
                near = 1f - Mathf.Clamp01(d / Mathf.Max(m_ProximityRange, 0.01f));
            }

            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * Mathf.PI * 2f * m_PulseRate);
            Color colour = Color.Lerp(m_IdleColor, m_ArmedColor, Mathf.Max(near, pulse * 0.35f));

            m_Renderer.GetPropertyBlock(m_Block);
            m_Block.SetColor(k_ColorId, colour);
            m_Block.SetColor(k_EmissionId, colour * (1.2f + near * 2.2f));
            m_Renderer.SetPropertyBlock(m_Block);
        }

        public float HorizontalDistance(Vector3 worldPoint)
        {
            Vector3 centre = transform.position;
            float dx = worldPoint.x - centre.x;
            float dz = worldPoint.z - centre.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        public bool Contains(Vector3 worldPoint)
        {
            return HorizontalDistance(worldPoint) <= m_Radius;
        }

        /// Where a ballistic path starting here will cross the floor. Used to
        /// judge the throw the moment it leaves the hand, so the miniature can
        /// start growing on the way rather than after it lands.
        public bool PredictLanding(Vector3 position, Vector3 velocity, float gravity,
                                   out Vector3 impact, out float flightTime)
        {
            impact = position;
            flightTime = 0f;

            float dy = position.y - m_FloorY;
            if (gravity <= 0f)
                return false;

            // dy + vy t - g t^2 / 2 = 0
            float a = -0.5f * gravity;
            float b = velocity.y;
            float c = dy;
            float disc = b * b - 4f * a * c;
            if (disc < 0f)
                return false;

            float root = Mathf.Sqrt(disc);
            float t1 = (-b + root) / (2f * a);
            float t2 = (-b - root) / (2f * a);
            flightTime = Mathf.Max(t1, t2);
            if (flightTime <= 0f)
                return false;

            impact = position + velocity * flightTime;
            impact.y = m_FloorY;
            return true;
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.3f, 0.7f, 1f, 0.8f);
            Vector3 centre = transform.position;
            const int steps = 48;
            Vector3 prev = centre + new Vector3(m_Radius, 0f, 0f);
            for (int i = 1; i <= steps; i++)
            {
                float a = i / (float)steps * Mathf.PI * 2f;
                Vector3 p = centre + new Vector3(Mathf.Cos(a) * m_Radius, 0f, Mathf.Sin(a) * m_Radius);
                Gizmos.DrawLine(prev, p);
                prev = p;
            }
        }
    }
}
