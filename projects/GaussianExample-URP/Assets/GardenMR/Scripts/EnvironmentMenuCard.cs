using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GardenMR
{
    // One thumbnail card inside EnvironmentMenu. Built at runtime from m_CardTemplate,
    // one clone per EnvironmentCatalog.Entry. RawImage (not Image+Sprite) so the catalog's
    // Texture2D thumbnails can be assigned directly with no sprite-import step.
    // No thumbnail art yet, so m_Label shows entry.m_DisplayId as English text instead.
    public class EnvironmentMenuCard : MonoBehaviour
    {
        public Button m_Button;
        public RawImage m_Thumbnail;
        public TMP_Text m_Label;
        [Tooltip("Enabled when this card represents the scene currently loaded.")]
        public GameObject m_LoadedRing;

        [Tooltip("Enabled when this card is the pending selection (not yet summoned).")]
        public GameObject m_SelectedRing;

        public void Configure(EnvironmentCatalog.Entry entry, bool isCurrent, System.Action onClick)
        {
            if (m_Thumbnail)
                m_Thumbnail.texture = entry.m_Thumbnail;
            if (m_Label)
                m_Label.text = entry.m_DisplayId;
            if (m_LoadedRing)
                m_LoadedRing.SetActive(isCurrent);
            SetSelected(false);
            if (!m_Button)
                return;
            // The currently loaded scene's card is not a valid switch target.
            m_Button.interactable = !isCurrent;
            m_Button.onClick.RemoveAllListeners();
            if (!isCurrent)
                m_Button.onClick.AddListener(() => onClick?.Invoke());
        }

        public void SetSelected(bool selected)
        {
            if (m_SelectedRing)
                m_SelectedRing.SetActive(selected);
        }
    }
}
