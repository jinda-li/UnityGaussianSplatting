using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace GardenMR
{
    // Grabbing ScaleHandle changes splat magnitude around the floor pivot. Handle
    // positions update via SplatHandleRig; handle colliders stay constant world size.
    public class SplatScaleHandle : MonoBehaviour
    {
        public XRBaseInteractable m_Interactable;
        public SplatHandleRig m_HandleRig;

        [Min(0.001f)] public float m_MinScale = 0.025f;
        [Min(0.001f)] public float m_MaxScale = 0.06f;

        [Tooltip("World distance-to-scale sensitivity: meters of radial pull per unit scale.")]
        public float m_Sensitivity = 0.08f;

        Transform m_GrabInteractor;
        float m_GrabStartDistance;
        float m_GrabStartScale;

        void Awake()
        {
            if (!m_Interactable)
                m_Interactable = GetComponent<XRBaseInteractable>();
            if (!m_HandleRig)
                m_HandleRig = GetComponentInParent<SplatHandleRig>();
            EnsureCollider();
        }

        void EnsureCollider()
        {
            var col = GetComponent<Collider>();
            if (!col)
            {
                var sphere = gameObject.AddComponent<SphereCollider>();
                sphere.radius = 0.5f;
                col = sphere;
            }
            if (m_Interactable && !m_Interactable.colliders.Contains(col))
                m_Interactable.colliders.Add(col);
        }

        void OnEnable()
        {
            if (!m_Interactable)
                return;
            m_Interactable.selectEntered.AddListener(OnSelectEntered);
            m_Interactable.selectExited.AddListener(OnSelectExited);
        }

        void OnDisable()
        {
            if (!m_Interactable)
                return;
            m_Interactable.selectEntered.RemoveListener(OnSelectEntered);
            m_Interactable.selectExited.RemoveListener(OnSelectExited);
        }

        void OnSelectEntered(SelectEnterEventArgs args)
        {
            if (!m_HandleRig)
                return;
            m_GrabInteractor = args.interactorObject.transform;
            m_GrabStartDistance = Vector3.Distance(m_GrabInteractor.position, m_HandleRig.PivotWorld);
            m_GrabStartScale = m_HandleRig.CurrentScale;
        }

        void OnSelectExited(SelectExitEventArgs args) => m_GrabInteractor = null;

        void LateUpdate()
        {
            if (!m_GrabInteractor || !m_HandleRig)
                return;

            float distance = Vector3.Distance(m_GrabInteractor.position, m_HandleRig.PivotWorld);
            float delta = (distance - m_GrabStartDistance) * m_Sensitivity;
            float newScale = Mathf.Clamp(m_GrabStartScale + delta, m_MinScale, m_MaxScale);
            m_HandleRig.ApplyScale(newScale);
        }

        public void SnapToScale(float magnitude)
        {
            if (m_HandleRig)
                m_HandleRig.SnapToScale(Mathf.Clamp(magnitude, m_MinScale, m_MaxScale));
        }
    }
}
