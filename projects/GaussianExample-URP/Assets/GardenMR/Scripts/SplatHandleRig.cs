using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using GaussianSplatting.Runtime;

namespace GardenMR
{
    // Keeps Move/Scale handles aligned with the 3DGS tabletop: handles follow splat
    // position + rotation, stay constant world size, and scale their offset from the
    // floor pivot proportionally when only the splat magnitude changes.
    public class SplatHandleRig : MonoBehaviour
    {
        [Header("References")]
        public Transform m_Rig;
        public GaussianSplatRenderer m_SplatRenderer;
        [Tooltip("PlaceModeHandles — unscaled parent of Move/Scale handles.")]
        public Transform m_HandleFrame;
        public Transform m_MoveHandle;
        public Transform m_ScaleHandle;
        [Tooltip("Optional — layout pauses while the rig is grabbed for move.")]
        public XRGrabInteractable m_RigGrab;

        [Header("Handle layout")]
        [Tooltip("Lift handles slightly above the floor pivot in frame-local space.")]
        public float m_HandleLift = 0.02f;

        Transform m_SplatRoot;
        Vector3 m_ScaleSign = Vector3.one;
        Vector3 m_LocalFloorCenter;
        Vector3 m_MoveBaseOffset;
        Vector3 m_ScaleBaseOffset;
        bool m_FloorCenterReady;
        bool m_OffsetsReady;

        public Vector3 PivotWorld => FloorCenterWorld();
        public float CurrentScale => m_SplatRoot ? Mathf.Abs(m_SplatRoot.localScale.x) : 1f;
        public Vector3 ScaleSign => m_ScaleSign;

        void Awake()
        {
            if (m_SplatRenderer)
                m_SplatRoot = m_SplatRenderer.transform;
            if (!m_HandleFrame)
                m_HandleFrame = transform;
            if (!m_RigGrab && m_Rig)
                m_RigGrab = m_Rig.GetComponent<XRGrabInteractable>();
            CacheScaleSign();
        }

        void LateUpdate()
        {
            if (!m_SplatRoot || !m_HandleFrame)
                return;
            if (m_RigGrab && m_RigGrab.isSelected)
                return;
            SyncFrameToSplat();
            if (m_OffsetsReady)
                ApplyHandleOffsets(CurrentScale);
        }

        public void InitializeFromScene()
        {
            if (!m_SplatRoot)
                return;
            EnsureFloorCenter();
            SyncFrameToSplat();

            float scale = Mathf.Max(CurrentScale, 1e-6f);
            if (m_MoveHandle)
                m_MoveBaseOffset = StripY(m_MoveHandle.localPosition) / scale;
            if (m_ScaleHandle)
                m_ScaleBaseOffset = StripY(m_ScaleHandle.localPosition) / scale;
            m_OffsetsReady = m_MoveHandle && m_ScaleHandle;
            ApplyHandleOffsets(scale);
        }

        public void ApplyScale(float magnitude)
        {
            if (!m_SplatRoot)
                return;
            EnsureFloorCenter();
            CacheScaleSign();

            Vector3 worldFloor = m_SplatRoot.TransformPoint(m_LocalFloorCenter);
            m_SplatRoot.localScale = Vector3.Scale(m_ScaleSign, Vector3.one * magnitude);
            Vector3 newWorldFloor = m_SplatRoot.TransformPoint(m_LocalFloorCenter);
            m_SplatRoot.position += worldFloor - newWorldFloor;

            ApplyHandleOffsets(magnitude);
            SyncFrameToSplat();
        }

        public void SnapToScale(float magnitude) => ApplyScale(magnitude);

        void CacheScaleSign()
        {
            if (!m_SplatRoot)
                return;
            m_ScaleSign = new Vector3(
                Mathf.Sign(m_SplatRoot.localScale.x),
                Mathf.Sign(m_SplatRoot.localScale.y),
                Mathf.Sign(m_SplatRoot.localScale.z));
        }

        void EnsureFloorCenter()
        {
            if (m_FloorCenterReady || !m_SplatRoot || !m_SplatRenderer || m_SplatRenderer.asset == null)
                return;

            var asset = m_SplatRenderer.asset;
            Vector3 c = (asset.boundsMin + asset.boundsMax) * 0.5f;
            Vector3 floorLocal = new Vector3(c.x, asset.boundsMin.y, c.z);
            Vector3 ceilingLocal = new Vector3(c.x, asset.boundsMax.y, c.z);
            Vector3 wFloor = m_SplatRoot.TransformPoint(floorLocal);
            Vector3 wCeil = m_SplatRoot.TransformPoint(ceilingLocal);
            if (wCeil.y < wFloor.y)
                floorLocal = ceilingLocal;
            m_LocalFloorCenter = floorLocal;
            m_FloorCenterReady = true;
        }

        Vector3 FloorCenterWorld() =>
            m_SplatRoot ? m_SplatRoot.TransformPoint(m_LocalFloorCenter) : Vector3.zero;

        void SyncFrameToSplat()
        {
            if (!m_SplatRoot || !m_HandleFrame)
                return;
            m_HandleFrame.SetPositionAndRotation(FloorCenterWorld(), m_SplatRoot.rotation);
            m_HandleFrame.localScale = Vector3.one;
        }

        void ApplyHandleOffsets(float magnitude)
        {
            if (!m_OffsetsReady)
                return;
            if (m_MoveHandle)
                m_MoveHandle.localPosition = m_MoveBaseOffset * magnitude + Vector3.up * m_HandleLift;
            if (m_ScaleHandle)
                m_ScaleHandle.localPosition = m_ScaleBaseOffset * magnitude + Vector3.up * m_HandleLift;
        }

        static Vector3 StripY(Vector3 v) => new Vector3(v.x, 0f, v.z);
    }
}
