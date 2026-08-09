using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Filtering;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace GardenMR
{
    // Spawn marker inside the miniature. Ray hover highlights; controller trigger
    // (Activate) starts Dive. Grip (Select) is rejected so it never steals grabs.
    // Requires NearFarInteractor.allowHoveredActivate so Activate works without Select.
    [RequireComponent(typeof(ConstantWorldScale))]
    public class SplatSpawnPoint : MonoBehaviour
    {
        public TabletopDiveController m_Controller;

        [Tooltip("Interactable that reports hover/activate. Defaults to one on this object.")]
        public XRBaseInteractable m_Interactable;

        [Header("Hover feedback")]
        public Renderer m_Visual;
        public Color m_IdleColor = new Color(0.6f, 0.8f, 1f, 0.55f);
        public Color m_HoverColor = new Color(1f, 0.95f, 0.6f, 0.9f);

        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
        XRSelectFilterDelegate m_RejectSelectFilter;
        MaterialPropertyBlock m_Mpb;

        void Awake()
        {
            if (!m_Interactable)
                m_Interactable = GetComponent<XRBaseInteractable>();
            if (!m_Controller)
            {
                m_Controller = Object.FindFirstObjectByType<TabletopDiveController>();
                if (!m_Controller)
                    Debug.LogWarning($"{nameof(SplatSpawnPoint)}: no {nameof(TabletopDiveController)} found; Dive will do nothing.", this);
            }
            RegisterCollider();
            // Non-trigger so XR ray / sphere cast can hit it.
            var col = GetComponent<Collider>();
            if (col)
                col.isTrigger = false;
            ApplyColor(m_IdleColor);

            // Block grip/select so MoveHandle grabs and other selects are never stolen.
            if (m_Interactable)
            {
                m_RejectSelectFilter = new XRSelectFilterDelegate((_, __) => false);
                m_Interactable.selectFilters.Add(m_RejectSelectFilter);
            }
        }

        void RegisterCollider()
        {
            if (!m_Interactable)
                return;
            var col = GetComponent<Collider>();
            if (!col)
            {
                var sphere = gameObject.AddComponent<SphereCollider>();
                sphere.radius = 0.5f;
                col = sphere;
            }
            if (!m_Interactable.colliders.Contains(col))
                m_Interactable.colliders.Add(col);
        }

        void OnEnable()
        {
            if (!m_Interactable)
                return;
            m_Interactable.hoverEntered.AddListener(OnHoverEntered);
            m_Interactable.hoverExited.AddListener(OnHoverExited);
            m_Interactable.activated.AddListener(OnActivated);
        }

        void OnDisable()
        {
            if (!m_Interactable)
                return;
            m_Interactable.hoverEntered.RemoveListener(OnHoverEntered);
            m_Interactable.hoverExited.RemoveListener(OnHoverExited);
            m_Interactable.activated.RemoveListener(OnActivated);
        }

        void OnDestroy()
        {
            if (m_Interactable && m_RejectSelectFilter != null)
                m_Interactable.selectFilters.Remove(m_RejectSelectFilter);
        }

        void OnHoverEntered(HoverEnterEventArgs args) => ApplyColor(m_HoverColor);
        void OnHoverExited(HoverExitEventArgs args) => ApplyColor(m_IdleColor);

        void OnActivated(ActivateEventArgs args)
        {
            // Activate and UI Press share the same trigger; a menu button press behind this
            // orb must not also fire a Dive. NearFarInteractor keeps registering 3D hits even
            // while a ray is over UI, so this check is the only thing preventing that.
            if (args.interactorObject is NearFarInteractor nf && nf.TryGetCurrentUIRaycastResult(out _))
                return;
            if (m_Controller && m_Controller.IsBusy)
                return;
            if (m_Controller)
                m_Controller.Dive(this);
        }

        void ApplyColor(Color c)
        {
            if (!m_Visual || !m_Visual.sharedMaterial || !m_Visual.sharedMaterial.HasProperty(k_BaseColor))
                return;
            m_Mpb ??= new MaterialPropertyBlock();
            m_Visual.GetPropertyBlock(m_Mpb);
            m_Mpb.SetColor(k_BaseColor, c);
            m_Visual.SetPropertyBlock(m_Mpb);
        }
    }
}
