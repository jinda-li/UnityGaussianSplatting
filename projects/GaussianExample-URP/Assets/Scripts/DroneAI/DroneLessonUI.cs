using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Scene-global lesson/info panel driven by drone state changes. Listens to the static
// DroneAI.StateChanged event so runtime-spawned drones need no wiring. When a configured
// state is entered, freezes the game with Time.timeScale = 0 and places a world-space
// panel in front of the HMD; the OK button (XRI ray + UGUI, which runs on unscaled time
// and works while frozen) resumes the game. Entries default to showing once globally,
// like a tutorial.
public class DroneLessonUI : MonoBehaviour
{
    [Serializable]
    private class LessonEntry
    {
        public DroneAI.DroneState state = DroneAI.DroneState.Search;
        public string title;
        [TextArea(2, 6)] public string body;
        public bool enabled = true;
        public bool onlyOnce = true;
        [NonSerialized] public bool shown;
    }

    [Header("Entries (one per drone state that should pause and explain)")]
    [SerializeField] private LessonEntry[] entries;

    [Header("Panel (world-space canvas, inactive by default)")]
    [SerializeField] private GameObject panelRoot;
    [SerializeField] private TMP_Text titleText;
    [SerializeField] private TMP_Text bodyText;
    [SerializeField] private Button okButton;

    [Header("Placement")]
    [Tooltip("Meters in front of the HMD where the panel appears.")]
    [SerializeField, Min(0.1f)] private float panelDistance = 1.2f;

    private readonly Queue<LessonEntry> _queue = new Queue<LessonEntry>();
    private Transform _head;
    private bool _panelOpen;

    private void Awake()
    {
        if (panelRoot != null)
            panelRoot.SetActive(false);

        if (okButton != null)
            okButton.onClick.AddListener(Dismiss);
    }

    private void OnEnable()
    {
        DroneAI.StateChanged += OnDroneStateChanged;
    }

    private void OnDisable()
    {
        DroneAI.StateChanged -= OnDroneStateChanged;

        // Never leave the game frozen if this object goes away mid-lesson.
        if (_panelOpen)
            Time.timeScale = 1f;
    }

    private void OnDroneStateChanged(DroneAI drone, DroneAI.DroneState from, DroneAI.DroneState to)
    {
        if (entries == null)
            return;

        foreach (LessonEntry entry in entries)
        {
            if (entry.state != to || !entry.enabled)
                continue;
            if (entry.onlyOnce && entry.shown)
                continue;

            entry.shown = true;

            if (_panelOpen)
                _queue.Enqueue(entry);
            else
                Show(entry);
            return;
        }
    }

    private void Show(LessonEntry entry)
    {
        if (panelRoot == null)
        {
            Debug.LogError($"{nameof(DroneLessonUI)}: no panel assigned.", this);
            return;
        }

        if (_head == null)
        {
            Camera cam = Camera.main;
            if (cam != null)
                _head = cam.transform;
        }

        if (titleText != null)
            titleText.text = entry.title;
        if (bodyText != null)
            bodyText.text = entry.body;

        if (_head != null)
        {
            Vector3 forward = _head.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;

            panelRoot.transform.position = _head.position + forward * panelDistance;
            // Canvas +Z points away from the viewer so the UI faces the player.
            panelRoot.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
        }

        panelRoot.SetActive(true);
        _panelOpen = true;
        Time.timeScale = 0f;
    }

    public void Dismiss()
    {
        if (!_panelOpen)
            return;

        if (_queue.Count > 0)
        {
            Show(_queue.Dequeue());
            return;
        }

        panelRoot.SetActive(false);
        _panelOpen = false;
        Time.timeScale = 1f;
    }
}
