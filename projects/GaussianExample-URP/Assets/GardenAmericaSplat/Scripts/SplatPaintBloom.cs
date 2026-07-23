using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace GardenSplat
{
    // Makes the world answer back when the player wakes it with color comets.
    // Three layers:
    //
    //  - local: the world is diced into cells of cellSize metres. Each comet
    //    impact charges the cell it landed in, and the first time a cell passes
    //    cellThreshold it wakes once - a 3D chime plus an optional particle
    //    burst (fireflies, pollen, birds) at the cell centre. Cells fire once
    //    and stay awake, so there is always somewhere new that rewards a throw.
    //
    //  - milestones: SplatMaterializeController.PaintedFraction is watched
    //    against a list of fractions; crossing one raises onMilestone with the
    //    fraction. That is where scene payoffs get wired (skybox fade, a music
    //    layer, ambience).
    //
    //  - finale: once coverage passes finaleAt, one big slow PaintBurst is fired
    //    from finaleCenter to sweep the rest of the garden to full color - the
    //    climactic full-restore, triggered by the player's own throws.
    //
    // Drop this on its own GameObject and point SplatCometBrush's "Bloom" field
    // at it; it is optional - the comet brush works without one.
    public class SplatPaintBloom : MonoBehaviour
    {
        // UnityEvent<float> only draws in the Inspector through a concrete
        // subclass; the raw generic serializes but shows as an empty field.
        [System.Serializable]
        public class MilestoneEvent : UnityEvent<float> { }

        [SerializeField] private SplatMaterializeController controller;

        [Header("Local Response")]
        [Tooltip("Edge length of a response cell, in metres")]
        [SerializeField, Min(0.1f)] private float cellSize = 2f;
        [Tooltip("Accumulated impact radius that wakes a cell")]
        [SerializeField, Min(0.01f)] private float cellThreshold = 1.5f;
        [Tooltip("Minimum seconds between two cells waking, so a flurry of hits stays a rhythm not a pile-up")]
        [SerializeField, Min(0f)] private float minSecondsBetweenBlooms = 0.15f;

        [Header("Local Feedback")]
        [SerializeField] private AudioClip[] bloomClips;
        [SerializeField, Range(0f, 1f)] private float bloomVolume = 0.7f;
        [SerializeField] private Vector2 bloomPitchRange = new Vector2(0.92f, 1.12f);
        [SerializeField, Min(1)] private int audioVoices = 6;
        [Tooltip("Optional one-shot particle effect at the cell centre (fireflies, pollen). Destroyed after burstLifetime")]
        [SerializeField] private GameObject burstPrefab;
        [SerializeField, Min(0.1f)] private float burstLifetime = 4f;

        [Header("Global Milestones")]
        [Tooltip("Painted fractions that raise onMilestone, ascending. Each fires once")]
        [SerializeField] private float[] milestones = { 0.25f, 0.5f, 0.75f };
        [SerializeField] private MilestoneEvent onMilestone;

        [Header("Finale")]
        [Tooltip("Coverage at which the climactic full-restore burst fires. 0 disables it")]
        [SerializeField, Range(0f, 1f)] private float finaleAt = 0.7f;
        [Tooltip("Where the finale wave originates. Defaults to this transform if unset")]
        [SerializeField] private Transform finaleCenter;
        [Tooltip("Radius of the finale burst - make it large enough to cover the whole garden")]
        [SerializeField, Min(1f)] private float finaleRadius = 40f;

        /// Cells woken so far this run - useful as a "places discovered" score.
        public int BloomedCells => _bloomed.Count;

        private readonly Dictionary<Vector3Int, float> _charge = new();
        private readonly HashSet<Vector3Int> _bloomed = new();
        private AudioSource[] _voices;
        private int _nextVoice;
        private float _lastBloomTime = float.NegativeInfinity;
        private int _nextMilestone;
        private bool _finaleFired;

        private void Awake()
        {
            _voices = new AudioSource[Mathf.Max(audioVoices, 1)];
            for (int i = 0; i < _voices.Length; i++)
            {
                var go = new GameObject($"BloomVoice{i}");
                go.transform.SetParent(transform, false);
                AudioSource source = go.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.spatialBlend = 1f;
                source.dopplerLevel = 0f;
                source.minDistance = 1f;
                source.maxDistance = 25f;
                _voices[i] = source;
            }
        }

        private void Update()
        {
            if (controller == null)
                return;

            float frac = controller.PaintedFraction;

            if (milestones != null)
            {
                while (_nextMilestone < milestones.Length && frac >= milestones[_nextMilestone])
                {
                    onMilestone?.Invoke(milestones[_nextMilestone]);
                    _nextMilestone++;
                }
            }

            if (!_finaleFired && finaleAt > 0f && frac >= finaleAt)
            {
                _finaleFired = true;
                Vector3 center = finaleCenter != null ? finaleCenter.position : transform.position;
                controller.PaintFinale(center, finaleRadius);
            }
        }

        /// Feeds a comet impact at worldPoint into its cell. amount is the burst
        /// radius, so a big charged comet charges a cell faster than a small one.
        public void ReportPaint(Vector3 worldPoint, float amount)
        {
            if (amount <= 0f)
                return;

            var cell = new Vector3Int(
                Mathf.FloorToInt(worldPoint.x / cellSize),
                Mathf.FloorToInt(worldPoint.y / cellSize),
                Mathf.FloorToInt(worldPoint.z / cellSize));

            if (_bloomed.Contains(cell))
                return;

            _charge.TryGetValue(cell, out float charge);
            charge += amount;

            if (charge < cellThreshold)
            {
                _charge[cell] = charge;
                return;
            }

            // Hold at full charge rather than blooming, so many cells crossing
            // at once still fire one at a time, each with its own audible beat.
            if (Time.time - _lastBloomTime < minSecondsBetweenBlooms)
            {
                _charge[cell] = cellThreshold;
                return;
            }

            _charge.Remove(cell);
            _bloomed.Add(cell);
            _lastBloomTime = Time.time;
            Bloom(((Vector3)cell + Vector3.one * 0.5f) * cellSize);
        }

        /// Forgets every woken cell and milestone so the world can respond again,
        /// e.g. after SplatMaterializeController.ClearPaint().
        public void ResetBlooms()
        {
            _charge.Clear();
            _bloomed.Clear();
            _nextMilestone = 0;
            _finaleFired = false;
        }

        private void Bloom(Vector3 position)
        {
            if (bloomClips != null && bloomClips.Length > 0 && _voices != null)
            {
                AudioClip clip = bloomClips[Random.Range(0, bloomClips.Length)];
                if (clip != null)
                {
                    AudioSource voice = _voices[_nextVoice];
                    _nextVoice = (_nextVoice + 1) % _voices.Length;
                    voice.transform.position = position;
                    voice.clip = clip;
                    voice.volume = bloomVolume;
                    voice.pitch = Random.Range(bloomPitchRange.x, bloomPitchRange.y);
                    voice.Play();
                }
            }

            if (burstPrefab != null)
            {
                GameObject burst = Instantiate(burstPrefab, position, Random.rotationUniform);
                Destroy(burst, burstLifetime);
            }
        }
    }
}
