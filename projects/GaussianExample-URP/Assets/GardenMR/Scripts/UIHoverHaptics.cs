using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.UI;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;

namespace GardenMR
{
    // Small haptic tick whenever the ray starts hovering a UI element. Sits on the same
    // GameObject as the NearFarInteractor (controller).
    [RequireComponent(typeof(NearFarInteractor))]
    public class UIHoverHaptics : MonoBehaviour
    {
        public HapticImpulsePlayer m_Haptics;
        [Range(0f, 1f)] public float m_Amplitude = 0.15f;
        public float m_Duration = 0.03f;

        NearFarInteractor m_Interactor;

        void Awake()
        {
            m_Interactor = GetComponent<NearFarInteractor>();
            if (!m_Haptics)
                m_Haptics = GetComponent<HapticImpulsePlayer>();
        }

        void OnEnable()
        {
            if (m_Interactor != null)
                m_Interactor.uiHoverEntered.AddListener(OnUIHoverEntered);
        }

        void OnDisable()
        {
            if (m_Interactor != null)
                m_Interactor.uiHoverEntered.RemoveListener(OnUIHoverEntered);
        }

        void OnUIHoverEntered(UIHoverEventArgs args)
        {
            if (m_Haptics)
                m_Haptics.SendHapticImpulse(m_Amplitude, m_Duration);
        }
    }
}
