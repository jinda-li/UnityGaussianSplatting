using UnityEngine;
using UnityEngine.EventSystems;

namespace GardenMR
{
    // Idle/hover/pressed scale feedback for a world-space UI button. Driven by standard
    // EventSystem pointer callbacks, which XRUIInputModule dispatches the same as mouse/touch,
    // so this works unmodified for the NearFarInteractor ray.
    public class UIButtonMotion : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
    {
        public float m_IdleScale = 1f;
        public float m_HoverScale = 1.04f;
        public float m_PressedScale = 0.97f;
        public float m_SmoothTime = 0.09f;

        float m_TargetScale;
        float m_CurrentScale;
        float m_Velocity;
        bool m_Hovering;
        bool m_Pressed;

        void Awake()
        {
            m_CurrentScale = m_TargetScale = m_IdleScale;
            transform.localScale = Vector3.one * m_CurrentScale;
        }

        void OnDisable()
        {
            m_Hovering = false;
            m_Pressed = false;
        }

        void Update()
        {
            if (Mathf.Approximately(m_CurrentScale, m_TargetScale))
                return;
            m_CurrentScale = Mathf.SmoothDamp(m_CurrentScale, m_TargetScale, ref m_Velocity, m_SmoothTime);
            transform.localScale = Vector3.one * m_CurrentScale;
        }

        void RefreshTarget()
        {
            m_TargetScale = m_Pressed ? m_PressedScale : (m_Hovering ? m_HoverScale : m_IdleScale);
        }

        public void OnPointerEnter(PointerEventData eventData) { m_Hovering = true; RefreshTarget(); }
        public void OnPointerExit(PointerEventData eventData) { m_Hovering = false; m_Pressed = false; RefreshTarget(); }
        public void OnPointerDown(PointerEventData eventData) { m_Pressed = true; RefreshTarget(); }
        public void OnPointerUp(PointerEventData eventData) { m_Pressed = false; RefreshTarget(); }
    }
}
