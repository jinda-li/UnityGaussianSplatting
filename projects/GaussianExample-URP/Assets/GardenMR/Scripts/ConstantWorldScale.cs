using UnityEngine;

namespace GardenMR
{
    // Keeps spawn-point orbs at a fixed world diameter while parented under the scaled
    // splat root. Move/Scale handles live on unscaled PlaceModeHandles instead — do not
    // attach this to them (SplatHandleRig keeps handle size constant via fixed localScale).
    public class ConstantWorldScale : MonoBehaviour
    {
        [Tooltip("Desired size in world meters, independent of parent scale.")]
        [Min(0.001f)] public float m_WorldSize = 0.03f;

        [Tooltip("Scale to compensate for. Leave empty to use the direct parent.")]
        public Transform m_Reference;

        void LateUpdate()
        {
            var reference = m_Reference ? m_Reference : transform.parent;
            if (!reference)
                return;

            var s = reference.lossyScale;
            float k = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
            if (k < 1e-6f)
                return;

            transform.localScale = Vector3.one * (m_WorldSize / k);
        }
    }
}
