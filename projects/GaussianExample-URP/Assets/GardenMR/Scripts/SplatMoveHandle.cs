using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace GardenMR
{
    // Moves the complete tabletop rig directly from controller motion. The MoveHandle
    // owns the interactable, while GardenMRRig remains the transform being manipulated.
    public class SplatMoveHandle : MonoBehaviour
    {
        public XRBaseInteractable m_Interactable;
        public Transform m_Rig;
        public bool m_AllowYawRotation = true;

        Transform m_Interactor;
        Vector3 m_StartInteractorPosition;
        Vector3 m_StartInteractorForward;
        Vector3 m_StartRigPosition;
        Quaternion m_StartRigRotation;

        public bool IsMoving => m_Interactor;

        void Awake()
        {
            if (!m_Interactable)
                m_Interactable = GetComponent<XRBaseInteractable>();
            EnsureCollider();
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
            m_Interactor = null;
        }

        void Update()
        {
            if (!m_Interactor || !m_Rig)
                return;

            Quaternion yaw = Quaternion.identity;
            if (m_AllowYawRotation)
            {
                Vector3 currentForward = HorizontalForward(m_Interactor);
                if (currentForward.sqrMagnitude > 0f && m_StartInteractorForward.sqrMagnitude > 0f)
                {
                    float angle = Vector3.SignedAngle(
                        m_StartInteractorForward, currentForward, Vector3.up);
                    yaw = Quaternion.AngleAxis(angle, Vector3.up);
                }
            }

            m_Rig.SetPositionAndRotation(
                m_StartRigPosition + m_Interactor.position - m_StartInteractorPosition,
                yaw * m_StartRigRotation);
        }

        void OnSelectEntered(SelectEnterEventArgs args)
        {
            if (!m_Rig)
                return;

            m_Interactor = args.interactorObject.transform;
            m_StartInteractorPosition = m_Interactor.position;
            m_StartInteractorForward = HorizontalForward(m_Interactor);
            m_StartRigPosition = m_Rig.position;
            m_StartRigRotation = m_Rig.rotation;
        }

        void OnSelectExited(SelectExitEventArgs args)
        {
            if (args.interactorObject.transform == m_Interactor)
                m_Interactor = null;
        }

        void EnsureCollider()
        {
            if (!m_Interactable)
                return;

            var collider = GetComponent<Collider>();
            if (!collider)
            {
                var sphere = gameObject.AddComponent<SphereCollider>();
                sphere.radius = 0.5f;
                collider = sphere;
            }

            if (!m_Interactable.colliders.Contains(collider))
                m_Interactable.colliders.Add(collider);
        }

        static Vector3 HorizontalForward(Transform source)
        {
            Vector3 forward = Vector3.ProjectOnPlane(source.forward, Vector3.up);
            return forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.zero;
        }
    }
}
