using UnityEngine;

// Spawns a DroneAI at this transform after a configurable delay. Uses scaled time on
// purpose: while a lesson panel has the game paused, the countdown pauses too.
public class DroneSpawner : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private DroneAI dronePrefab;
    [Tooltip("Flyby waypoints - one is picked at random per spawn and injected into the drone.")]
    [SerializeField] private Transform[] flybyTargets;
    [Tooltip("Player head (main camera). Optional - the drone falls back to Camera.main.")]
    [SerializeField] private Transform playerHead;

    [Header("Timing")]
    [SerializeField, Min(0f)] private float spawnDelay = 20f;
    [SerializeField] private bool spawnOnStart = true;

    private float _timer;
    private bool _pending;

    private void Start()
    {
        if (spawnOnStart)
        {
            _pending = true;
            _timer = spawnDelay;
        }
    }

    private void Update()
    {
        if (!_pending)
            return;

        _timer -= Time.deltaTime;
        if (_timer <= 0f)
        {
            _pending = false;
            SpawnNow();
        }
    }

    [ContextMenu("Spawn Now")]
    public void SpawnNow()
    {
        if (dronePrefab == null)
        {
            Debug.LogError($"{nameof(DroneSpawner)}: no drone prefab assigned.", this);
            return;
        }

        Transform target = null;
        if (flybyTargets != null && flybyTargets.Length > 0)
            target = flybyTargets[Random.Range(0, flybyTargets.Length)];

        DroneAI drone = Instantiate(dronePrefab, transform.position, transform.rotation);
        drone.Initialize(target, playerHead);
        Debug.Log($"{nameof(DroneSpawner)}: spawned '{drone.name}' toward '{(target != null ? target.name : "nothing")}'.", this);
    }
}
