using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace GardenMR
{
    // Hover/press feedback for MoveHandle and ScaleHandle: color + emission (via
    // MaterialPropertyBlock, so every handle can share the single M_HandleChrome material)
    // plus a scale pulse on a visual-only child transform.
    //
    // INVARIANT: for MoveHandle, m_Source is NOT an interactable on MoveHandle itself — it is
    // GardenMRRig's own XRGrabInteractable. MoveHandle has no interactable of its own; the
    // rig's grab interactable's collider list contains only MoveHandle's collider, so "the
    // rig is hovered" is logically equivalent to "MoveHandle is hovered." This is intentional,
    // not an approximation. Anyone adding a second collider to that XRGrabInteractable breaks
    // this equivalence and must repoint this component instead of silently changing collider
    // semantics.
    public class HandleVisualState : MonoBehaviour
    {
        public XRBaseInteractable m_Source;

        [Tooltip("Visual-only child to scale. Must NOT be the collider owner, or the collider " +
                 "breathes in and out with hover and ray/sphere-cast hit testing gets flaky.")]
        public Transform m_ScaleTarget;

        public Renderer[] m_Renderers;

        [Header("Color")]
        public Color m_IdleColor = new Color(200f / 255f, 205f / 255f, 212f / 255f);
        public Color m_HoverColor = Color.white;
        public Color m_PressedColor = new Color(0f, 100f / 255f, 224f / 255f);

        [Header("Emission (rim light)")]
        [ColorUsage(false, true)] public Color m_IdleEmission = Color.black;
        [ColorUsage(false, true)] public Color m_HoverEmission = new Color(0.09f, 0.24f, 0.6f);
        [ColorUsage(false, true)] public Color m_PressedEmission = new Color(0.12f, 0.66f, 1.2f);

        [Header("Scale")]
        public float m_IdleScale = 1f;
        public float m_HoverScale = 1.05f;
        public float m_PressedScale = 0.96f;
        [Tooltip("Seconds, ~80-100 ms per the Horizon OS reference feel.")]
        public float m_SmoothTime = 0.09f;

        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int k_EmissionColor = Shader.PropertyToID("_EmissionColor");

        // Counted, not a bool: two interactors (e.g. both controllers) can hover the same
        // handle at once, and each fires its own independent enter/exit pair.
        int m_HoverCount;
        bool m_Selected;

        float m_TargetScale;
        float m_CurrentScale;
        float m_ScaleVelocity;
        MaterialPropertyBlock m_Mpb;

        void Awake()
        {
            m_CurrentScale = m_TargetScale = m_IdleScale;
            ApplyScaleImmediate();
            ApplyColor(m_IdleColor, m_IdleEmission);
        }

        void OnEnable()
        {
            if (!m_Source)
                return;
            m_Source.hoverEntered.AddListener(OnHoverEntered);
            m_Source.hoverExited.AddListener(OnHoverExited);
            m_Source.selectEntered.AddListener(OnSelectEntered);
            m_Source.selectExited.AddListener(OnSelectExited);
        }

        void OnDisable()
        {
            if (m_Source)
            {
                m_Source.hoverEntered.RemoveListener(OnHoverEntered);
                m_Source.hoverExited.RemoveListener(OnHoverExited);
                m_Source.selectEntered.RemoveListener(OnSelectEntered);
                m_Source.selectExited.RemoveListener(OnSelectExited);
            }
            m_HoverCount = 0;
            m_Selected = false;
            Refresh();
        }

        void Update()
        {
            if (Mathf.Approximately(m_CurrentScale, m_TargetScale))
                return;
            m_CurrentScale = Mathf.SmoothDamp(m_CurrentScale, m_TargetScale, ref m_ScaleVelocity, m_SmoothTime);
            ApplyScaleImmediate();
        }

        void OnHoverEntered(HoverEnterEventArgs args) { m_HoverCount++; Refresh(); }
        void OnHoverExited(HoverExitEventArgs args) { m_HoverCount = Mathf.Max(0, m_HoverCount - 1); Refresh(); }
        void OnSelectEntered(SelectEnterEventArgs args) { m_Selected = true; Refresh(); }
        void OnSelectExited(SelectExitEventArgs args) { m_Selected = m_Source && m_Source.isSelected; Refresh(); }

        void Refresh()
        {
            if (m_Selected)
            {
                ApplyColor(m_PressedColor, m_PressedEmission);
                m_TargetScale = m_PressedScale;
            }
            else if (m_HoverCount > 0)
            {
                ApplyColor(m_HoverColor, m_HoverEmission);
                m_TargetScale = m_HoverScale;
            }
            else
            {
                ApplyColor(m_IdleColor, m_IdleEmission);
                m_TargetScale = m_IdleScale;
            }
        }

        void ApplyScaleImmediate()
        {
            if (m_ScaleTarget)
                m_ScaleTarget.localScale = Vector3.one * m_CurrentScale;
        }

        void ApplyColor(Color baseColor, Color emission)
        {
            if (m_Renderers == null)
                return;
            m_Mpb ??= new MaterialPropertyBlock();
            foreach (var r in m_Renderers)
            {
                if (!r)
                    continue;
                r.GetPropertyBlock(m_Mpb);
                m_Mpb.SetColor(k_BaseColor, baseColor);
                m_Mpb.SetColor(k_EmissionColor, emission);
                r.SetPropertyBlock(m_Mpb);
            }
        }
    }
}
