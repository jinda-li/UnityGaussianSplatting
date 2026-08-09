using UnityEngine;
using GaussianSplatting.Runtime;

namespace GardenMR
{
    // Owns splat scale and exposes the floor pivot used by ScaleHandle.
    //
    // The floor is not always the splat root's own local origin — m_FloorLocal lets an
    // environment author a ground point anywhere inside the asset's local space. Scaling
    // pins that point in place by shifting the splat root's localPosition to compensate,
    // so the model stays visually planted regardless of where its reconstruction origin is.
    // m_FloorLocal = Vector3.zero reproduces the previous "origin is the floor" behaviour bit-for-bit.
    public class SplatHandleRig : MonoBehaviour
    {
        [Header("References")]
        public Transform m_Rig;
        public GaussianSplatRenderer m_SplatRenderer;
        public Transform m_HandleFrame;
        public Transform m_MoveHandle;
        public Transform m_ScaleHandle;

        [Tooltip("Ground point in the splat asset's local (unscaled) space. Zero = asset origin is the floor.")]
        public Vector3 m_FloorLocal = Vector3.zero;

        Transform m_SplatRoot;
        Vector3 m_ScaleSign = Vector3.one;

        public Vector3 PivotWorld => m_SplatRoot ? m_SplatRoot.TransformPoint(m_FloorLocal) : Vector3.zero;
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
        }

        public void SetFloorLocal(Vector3 floorLocal) => m_FloorLocal = floorLocal;

        public void InitializeFromScene()
        {
            if (!m_SplatRoot)
                return;
            if (m_Rig && m_SplatRoot.parent == m_Rig)
                m_SplatRoot.localPosition = -Vector3.Scale(m_SplatRoot.localScale, m_FloorLocal);
            if (m_HandleFrame)
                m_HandleFrame.localScale = Vector3.one;
        }

        // Splat root stays parented under the rig; pinning m_FloorLocal in place means the
        // root's localPosition must compensate whenever the scale changes, so the floor
        // point (not necessarily the asset origin) stays put in world space.
        public void ApplyScale(float magnitude)
        {
            if (!m_SplatRoot)
                return;
            CacheScaleSign();
            Vector3 scaleVec = Vector3.Scale(m_ScaleSign, Vector3.one * magnitude);
            m_SplatRoot.localScale = scaleVec;
            if (m_Rig && m_SplatRoot.parent == m_Rig)
                m_SplatRoot.localPosition = -Vector3.Scale(scaleVec, m_FloorLocal);
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
