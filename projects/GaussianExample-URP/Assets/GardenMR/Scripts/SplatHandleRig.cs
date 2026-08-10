using UnityEngine;
using GaussianSplatting.Runtime;

namespace GardenMR
{
    // Owns splat scale and keeps the splat's authored pivot pinned to the rig origin.
    //
    // A splat asset's reconstruction origin is wherever the capture happened, not the centre
    // of the room, so an environment author offsets the splat root's localPosition until the
    // point they want as the pivot sits on the rig origin. That offset is authored at the
    // asset's own (immersive) scale, so it only stays correct if it scales with magnitude:
    //
    //   rigLocal(c) = rot * Scale(scaleSign * mag, c) + localPosition
    //
    // Pinning asset-local point c to the rig origin gives localPosition = -mag * rot *
    // Scale(scaleSign, c) — strictly linear in mag, whatever the rotation or mirror sign.
    // So the authored pose captured at bind time only needs a proportional rescale, and the
    // splat keeps the same relationship to the rig (which GrabBase grabs) at every magnitude.
    public class SplatHandleRig : MonoBehaviour
    {
        [Header("References")]
        public Transform m_Rig;
        public GaussianSplatRenderer m_SplatRenderer;
        public Transform m_HandleFrame;
        public Transform m_ScaleHandle;

        [Tooltip("Fallback pivot in the splat asset's local (unscaled) space, used only when no rig is assigned.")]
        public Vector3 m_FloorLocal = Vector3.zero;

        Transform m_SplatRoot;
        Vector3 m_ScaleSign = Vector3.one;
        Vector3 m_BaseLocalPosition;
        float m_BaseMagnitude = 1f;

        // Scaling holds the authored pivot on the rig origin, so that is the world fixed point.
        public Vector3 PivotWorld => m_Rig ? m_Rig.position
            : (m_SplatRoot ? m_SplatRoot.TransformPoint(m_FloorLocal) : Vector3.zero);
        public float CurrentScale => m_SplatRoot ? Mathf.Abs(m_SplatRoot.localScale.x) : 1f;
        public Vector3 ScaleSign => m_ScaleSign;

        void Awake()
        {
            if (m_SplatRenderer)
                m_SplatRoot = m_SplatRenderer.transform;
            if (!m_HandleFrame)
                m_HandleFrame = transform;
            if (!m_Rig && m_SplatRoot && m_SplatRoot.parent)
                m_Rig = m_SplatRoot.parent;
            CacheScaleSign();
            CaptureBase();
        }

        // Repoints this rig at a freshly loaded environment's renderer/floor after a scene
        // switch. Must be called before any ApplyScale/InitializeFromScene call on the new asset.
        public void RebindSplat(GaussianSplatRenderer renderer, Vector3 floorLocal)
        {
            m_SplatRenderer = renderer;
            m_SplatRoot = renderer ? renderer.transform : null;
            m_FloorLocal = floorLocal;
            if (!m_Rig && m_SplatRoot && m_SplatRoot.parent)
                m_Rig = m_SplatRoot.parent;
            CacheScaleSign();
            CaptureBase();
        }

        public void SetFloorLocal(Vector3 floorLocal) => m_FloorLocal = floorLocal;

        // The authored pivot offset and the magnitude it was authored at. Every later
        // ApplyScale rescales from this pair, so the Inspector pose stays the source of truth.
        void CaptureBase()
        {
            if (!m_SplatRoot)
                return;
            m_BaseLocalPosition = m_SplatRoot.localPosition;
            m_BaseMagnitude = Mathf.Max(Mathf.Abs(m_SplatRoot.localScale.x), 1e-4f);
        }

        public void InitializeFromScene()
        {
            if (m_HandleFrame)
                m_HandleFrame.localScale = Vector3.one;
        }

        public void ApplyScale(float magnitude)
        {
            if (!m_SplatRoot)
                return;
            CacheScaleSign();
            m_SplatRoot.localScale = Vector3.Scale(m_ScaleSign, Vector3.one * magnitude);
            if (m_Rig && m_SplatRoot.parent == m_Rig)
                m_SplatRoot.localPosition = m_BaseLocalPosition * (magnitude / m_BaseMagnitude);
        }

        public void SnapToScale(float magnitude) => ApplyScale(magnitude);

        public void ApplyScaleKeepRig(float magnitude) => ApplyScale(magnitude);

        void CacheScaleSign()
        {
            if (!m_SplatRoot)
                return;
            m_ScaleSign = new Vector3(
                Mathf.Sign(m_SplatRoot.localScale.x),
                Mathf.Sign(m_SplatRoot.localScale.y),
                Mathf.Sign(m_SplatRoot.localScale.z));
        }
    }
}
