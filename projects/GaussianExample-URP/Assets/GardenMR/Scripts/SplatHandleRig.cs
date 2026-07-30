using UnityEngine;
using GaussianSplatting.Runtime;

namespace GardenMR
{
    // Owns splat scale and exposes the floor pivot used by ScaleHandle.
    //
    // The splat root's own local origin IS the floor: the scene-authored ground `Plane`
    // sits at local (0,0,0) under GaussianSplats. Scaling in place around that origin
    // keeps the model planted with no extra math, so the pivot is simply the splat
    // root's world position — no GaussianSplatAsset bounds involved (those are raw
    // reconstruction-space bounds, not aligned with the authored floor, and previously
    // produced a pivot many meters away from the model).
    public class SplatHandleRig : MonoBehaviour
    {
        [Header("References")]
        public Transform m_Rig;
        public GaussianSplatRenderer m_SplatRenderer;
        public Transform m_HandleFrame;
        public Transform m_MoveHandle;
        public Transform m_ScaleHandle;

        Transform m_SplatRoot;
        Vector3 m_ScaleSign = Vector3.one;

        public Vector3 PivotWorld => m_SplatRoot ? m_SplatRoot.position : Vector3.zero;
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

        public void InitializeFromScene()
        {
            if (!m_SplatRoot)
                return;
            if (m_Rig && m_SplatRoot.parent == m_Rig)
                m_SplatRoot.localPosition = Vector3.zero;
            if (m_HandleFrame)
                m_HandleFrame.localScale = Vector3.one;
        }

        // Splat root stays parented at local (0,0,0) under the rig, so changing its
        // localScale never moves its own origin (the floor) in world space — the rig
        // itself never needs to be repositioned to "pin" anything.
        public void ApplyScale(float magnitude)
        {
            if (!m_SplatRoot)
                return;
            CacheScaleSign();
            m_SplatRoot.localScale = Vector3.Scale(m_ScaleSign, Vector3.one * magnitude);
            if (m_Rig && m_SplatRoot.parent == m_Rig)
                m_SplatRoot.localPosition = Vector3.zero;
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
