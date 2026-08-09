using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace GardenMR
{
    // Left menuButton opens/closes a world-space panel of environment thumbnail cards.
    // The panel spawns fixed in front of the head (yaw-only, doesn't keep tracking
    // afterward) so it doesn't swim while the player looks around, and is pushed clear of
    // the tabletop rig's ground-plane footprint so it isn't depth-clipped by the splat's
    // ground Plane (splats don't write depth themselves, but authored MeshRenderers do).
    public class EnvironmentMenu : MonoBehaviour
    {
        [Header("Data")]
        public EnvironmentCatalog m_Catalog;

        [Header("References")]
        public GameObject m_PanelRoot;              // toggled on/off; holds everything below
        public Transform m_CardContainer;           // cards are instantiated as children of this
        public EnvironmentMenuCard m_CardTemplate;   // deactivated template, cloned once per entry
        public Transform m_HeadTransform;            // XR main camera
        public Button m_SummonButton;
        public Button m_CloseButton;

        [Header("Placement")]
        public float m_OpenDistance = 1.0f;
        public float m_OpenHeightOffset = 0f;
        [Tooltip("If the computed position falls within this radius of the rig (on the ground plane), " +
                 "push the panel out to m_OpenDistance + this radius instead.")]
        public float m_FootprintRadius = 0.56f;
        public Transform m_RigTransform;

        [Header("Input")]
        [Tooltip("Left hand only — right hand menuButton is reserved for Return so the two don't conflict.")]
        public InputAction m_ToggleAction = new InputAction("ToggleEnvironmentMenu", InputActionType.Button, "<XRController>{LeftHand}/menuButton");

        readonly List<EnvironmentMenuCard> m_Cards = new();
        TabletopDiveController m_DiveController;

        void Awake()
        {
            if (m_CardTemplate)
                m_CardTemplate.gameObject.SetActive(false);
            if (m_PanelRoot)
                m_PanelRoot.SetActive(false);
            if (m_SummonButton)
                m_SummonButton.onClick.AddListener(OnSummonClicked);
            if (m_CloseButton)
                m_CloseButton.onClick.AddListener(Close);
            BuildCards();
        }

        void OnEnable()
        {
            if (m_ToggleAction != null)
            {
                m_ToggleAction.performed += OnTogglePerformed;
                m_ToggleAction.Enable();
            }
        }

        void OnDisable()
        {
            if (m_ToggleAction != null)
            {
                m_ToggleAction.performed -= OnTogglePerformed;
                m_ToggleAction.Disable();
            }
        }

        void OnTogglePerformed(InputAction.CallbackContext ctx)
        {
            if (!m_PanelRoot)
                return;
            if (m_PanelRoot.activeSelf)
                Close();
            else
                Open();
        }

        void BuildCards()
        {
            if (!m_Catalog || !m_CardTemplate || !m_CardContainer)
                return;
            foreach (var card in m_Cards)
                if (card)
                    Destroy(card.gameObject);
            m_Cards.Clear();

            string currentScene = SceneManager.GetActiveScene().name;
            foreach (var entry in m_Catalog.m_Environments)
            {
                var card = Instantiate(m_CardTemplate, m_CardContainer);
                card.gameObject.SetActive(true);
                bool isCurrent = entry.m_SceneName == currentScene;
                string sceneName = entry.m_SceneName;
                card.Configure(entry, isCurrent, () => OnCardClicked(sceneName));
                m_Cards.Add(card);
            }
        }

        void OnCardClicked(string sceneName)
        {
            if (SceneTransition.Instance == null || SceneTransition.Instance.IsBusy)
                return;
            Close();
            SceneTransition.Instance.LoadEnvironment(sceneName);
        }

        void OnSummonClicked()
        {
            if (!m_DiveController)
                m_DiveController = FindFirstObjectByType<TabletopDiveController>();
            m_DiveController?.RequestSummon();
        }

        public void Open()
        {
            if (!m_PanelRoot)
                return;
            PositionPanel();
            m_PanelRoot.SetActive(true);
        }

        public void Close()
        {
            if (m_PanelRoot)
                m_PanelRoot.SetActive(false);
        }

        void PositionPanel()
        {
            if (!m_HeadTransform)
                return;

            Vector3 forward = m_HeadTransform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f)
                forward = Vector3.ProjectOnPlane(m_HeadTransform.up, Vector3.up);
            forward.Normalize();

            Vector3 pos = m_HeadTransform.position + forward * m_OpenDistance;
            pos.y = m_HeadTransform.position.y + m_OpenHeightOffset;

            if (m_RigTransform)
            {
                Vector3 toPos = pos - m_RigTransform.position;
                toPos.y = 0f;
                if (toPos.magnitude < m_FootprintRadius)
                {
                    pos = m_HeadTransform.position + forward * (m_OpenDistance + m_FootprintRadius);
                    pos.y = m_HeadTransform.position.y + m_OpenHeightOffset;
                }
            }

            transform.position = pos;
            // Faces the player. Unity's default UI canvas renders visible from -Z looking
            // toward +Z, so this may need a 180 degree flip once seen in the Editor.
            transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
        }
    }
}
