using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace GardenMR
{
    // Dive spawn marker inside the splat scene. Highlights when a controller enters the
    // trigger volume; pulling the trigger while inside starts Dive (not ray/grab select).
    [RequireComponent(typeof(ConstantWorldScale))]
    public class SplatSpawnPoint : MonoBehaviour
    {
        public TabletopDiveController m_Controller;

        [Header("Trigger volume")]
        [Tooltip("Collider used as the poke/trigger zone. Defaults to SphereCollider on this object.")]
        public Collider m_TriggerCollider;

        [Header("Hover feedback")]
        public Renderer m_Visual;
        public Color m_IdleColor = new Color(0.6f, 0.8f, 1f, 0.55f);
        public Color m_HoverColor = new Color(1f, 0.95f, 0.6f, 0.9f);

        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");

        readonly HashSet<XRBaseInputInteractor> m_InsideInteractors = new();
        bool m_WasSelectActive;

        void Awake()
        {
            EnsureTriggerVolume();
            ApplyColor(m_IdleColor);
        }

        void EnsureTriggerVolume()
        {
            if (!m_TriggerCollider)
                m_TriggerCollider = GetComponent<Collider>();
            if (!m_TriggerCollider)
            {
                var sphere = gameObject.AddComponent<SphereCollider>();
                sphere.radius = 0.5f;
                m_TriggerCollider = sphere;
            }
            m_TriggerCollider.isTrigger = true;

            // At least one side of a trigger pair needs a Rigidbody.
            var rb = GetComponent<Rigidbody>();
            if (!rb)
                rb = gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
        }

        void OnTriggerEnter(Collider other)
        {
            var interactor = other.GetComponentInParent<XRBaseInputInteractor>();
            if (interactor)
                m_InsideInteractors.Add(interactor);
            UpdateHoverVisual();
        }

        void OnTriggerExit(Collider other)
        {
            var interactor = other.GetComponentInParent<XRBaseInputInteractor>();
            if (interactor)
                m_InsideInteractors.Remove(interactor);
            UpdateHoverVisual();
        }

        void Update()
        {
            if (m_InsideInteractors.Count == 0)
            {
                m_WasSelectActive = false;
                return;
            }

            bool selectActive = false;
            foreach (var interactor in m_InsideInteractors)
            {
                if (!interactor)
                    continue;
                if (interactor.isSelectActive)
                {
                    selectActive = true;
                    break;
                }
            }

            if (selectActive && !m_WasSelectActive && m_Controller)
                m_Controller.Dive(this);
            m_WasSelectActive = selectActive;
        }

        void UpdateHoverVisual() =>
            ApplyColor(m_InsideInteractors.Count > 0 ? m_HoverColor : m_IdleColor);

        void ApplyColor(Color c)
        {
            if (m_Visual && m_Visual.sharedMaterial && m_Visual.sharedMaterial.HasProperty(k_BaseColor))
                m_Visual.material.SetColor(k_BaseColor, c);
        }
    }
}
