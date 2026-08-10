using UnityEngine;

namespace GardenMR
{
    // Floating name tag above a spawn point: billboards to the camera and keeps a fixed
    // world size even though the spawn point sits under the scaled splat root.
    //
    // The tag itself is authored as prefab children (Label = TextMeshPro, Stem = LineRenderer)
    // so the text and its pill are editable and visible in the editor. This component only
    // drives placement at runtime; the authored localPosition/localScale of the Label are
    // taken as the target offset and size in world meters. Edit the text on the TextMeshPro
    // component - the pill background is its own <mark=...> tag, so no extra material is needed.
    [DisallowMultipleComponent]
    public class SpawnPointLabel : MonoBehaviour
    {
        [Header("Children (authored in the prefab)")]
        [Tooltip("Tag root. Its authored localPosition/localScale define offset and size in meters.")]
        public Transform m_Label;

        [Tooltip("Optional stem drawn from the orb up to the tag.")]
        public LineRenderer m_Stem;

        [Header("Readability")]
        [Tooltip("Grow the tag with distance so it keeps a constant apparent size.")]
        public bool m_ConstantScreenSize = true;

        [Tooltip("Distance at which the tag is exactly its authored size.")]
        [Min(0.05f)] public float m_ReferenceDistance = 0.6f;

        [Tooltip("Distance range the constant-screen-size scaling is clamped to.")]
        public Vector2 m_DistanceClamp = new Vector2(0.3f, 3f);

        [Tooltip("Hide the tag past this camera distance. 0 disables the check.")]
        [Min(0f)] public float m_MaxVisibleDistance = 0f;

        [Header("Stem")]
        [Tooltip("Gap between the top of the stem and the tag, as a fraction of tag height.")]
        [Range(0f, 1f)] public float m_StemGap = 0.35f;

        Vector3 m_BaseOffset;
        Vector3 m_BaseScale;
        float m_BaseStemWidth;
        Transform m_Camera;

        void Awake()
        {
            if (!m_Label)
            {
                Debug.LogWarning($"{nameof(SpawnPointLabel)}: {nameof(m_Label)} is unset; nothing to place.", this);
                enabled = false;
                return;
            }

            // Authored under an unscaled prefab root, so these already are world meters.
            m_BaseOffset = m_Label.localPosition;
            m_BaseScale = m_Label.localScale;
            if (m_Stem)
                m_BaseStemWidth = m_Stem.widthMultiplier;
        }

        void LateUpdate()
        {
            if (!m_Camera)
            {
                var cam = Camera.main;
                if (!cam)
                    return;
                m_Camera = cam.transform;
            }

            Vector3 anchor = transform.position;
            float camDistance = Vector3.Distance(m_Camera.position, anchor);

            bool visible = m_MaxVisibleDistance <= 0f || camDistance <= m_MaxVisibleDistance;
            if (m_Label.gameObject.activeSelf != visible)
                m_Label.gameObject.SetActive(visible);
            if (m_Stem && m_Stem.enabled != visible)
                m_Stem.enabled = visible;
            if (!visible)
                return;

            // Undo the parent's scale so offset and size stay in world meters.
            var ls = transform.lossyScale;
            float parentScale = Mathf.Max(Mathf.Abs(ls.x), Mathf.Max(Mathf.Abs(ls.y), Mathf.Abs(ls.z)));
            if (parentScale < 1e-6f)
                return;

            float sizeFactor = 1f;
            if (m_ConstantScreenSize)
            {
                float d = Mathf.Clamp(camDistance, m_DistanceClamp.x, m_DistanceClamp.y);
                sizeFactor = d / m_ReferenceDistance;
            }

            float height = m_BaseOffset.y * sizeFactor;
            Vector3 tagPosition = anchor + Vector3.up * height;

            m_Label.position = tagPosition;
            m_Label.localScale = m_BaseScale * (sizeFactor / parentScale);

            // Full billboard, but no roll: the tag stays level with the horizon.
            Vector3 forward = tagPosition - m_Camera.position;
            if (forward.sqrMagnitude > 1e-8f)
                m_Label.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);

            if (m_Stem && m_Stem.enabled)
            {
                m_Stem.useWorldSpace = true;
                m_Stem.positionCount = 2;
                m_Stem.SetPosition(0, anchor);
                m_Stem.SetPosition(1, anchor + Vector3.up * (height * (1f - m_StemGap)));
                m_Stem.widthMultiplier = m_BaseStemWidth * sizeFactor;
            }
        }
    }
}
