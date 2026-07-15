using System;
using UnityEngine;
using UnityEngine.Events;

// Drone state machine. The root GameObject is the drone's center of mass and only ever
// yaws (body stays level, like a real multirotor) - all visual pitch/roll lives on the
// Graphic child via DroneMotionSim. Movement is transform-driven with a kinematic
// Rigidbody so the attack trigger still generates OnTriggerEnter against static
// colliders and the player's CharacterController.
public class DroneAI : MonoBehaviour
{
    public enum DroneState
    {
        None,
        Search,
        Identify,
        Attack,
        Leave,
        Exploded
    }

    [Header("References")]
    [Tooltip("Waypoint the drone flies toward while searching (injected by DroneSpawner, or set by hand).")]
    [SerializeField] private Transform flybyTarget;
    [Tooltip("Player head (main camera). Falls back to Camera.main when unset.")]
    [SerializeField] private Transform playerHead;
    [Tooltip("Trigger collider enabled only during the attack dive.")]
    [SerializeField] private Collider attackCollider;

    [Header("Search")]
    [SerializeField, Min(0f)] private float searchSpeed = 3f;
    [Tooltip("Distance to the flyby target that counts as arrived.")]
    [SerializeField, Min(0f)] private float arriveThreshold = 1f;
    [Tooltip("How long the drone hovers at the flyby target still searching before it leaves.")]
    [SerializeField, Min(0f)] private float searchHoverDuration = 5f;

    [Header("Detection")]
    [SerializeField, Min(0f)] private float detectRadius = 30f;
    [SerializeField, Min(0.02f)] private float detectInterval = 0.2f;
    [Tooltip("Line of sight must be held this long (seconds) before the player counts as spotted; broken sight resets the timer.")]
    [SerializeField, Min(0f)] private float detectConfirmTime = 2f;
    [Tooltip("Layers that can block line of sight to the player.")]
    [SerializeField] private LayerMask obstacleMask = ~0;

    [Header("Identify")]
    [SerializeField, Min(0f)] private float approachSpeed = 6f;
    [Tooltip("Hover distance from the player's head once identified.")]
    [SerializeField, Min(0f)] private float hoverDistance = 5f;
    [Tooltip("Dead zone around hoverDistance so the drone doesn't jitter back and forth.")]
    [SerializeField, Min(0f)] private float hoverDeadband = 0.5f;
    [Tooltip("Seconds of staring (after first reaching hover range) before the attack dive.")]
    [SerializeField, Min(0f)] private float identifyDuration = 3f;

    [Header("Attack")]
    [SerializeField, Min(0f)] private float diveSpeed = 15f;
    [Tooltip("If the dive passes the locked point by this many meters without hitting anything, the drone self-destructs midair.")]
    [SerializeField, Min(0f)] private float diveOvershoot = 10f;
    [Tooltip("Radius of the anti-tunneling sphere cast during the dive. Match the attack collider size.")]
    [SerializeField, Min(0.01f)] private float diveCastRadius = 0.3f;

    [Header("Leave")]
    [Tooltip("Seconds of flying straight ahead before the drone destroys itself.")]
    [SerializeField, Min(0f)] private float leaveLifetime = 10f;

    [Header("Turning")]
    [Tooltip("Yaw turn rate in degrees per second.")]
    [SerializeField, Min(0f)] private float turnSpeed = 120f;

    [Header("Events")]
    [SerializeField] private UnityEvent onPlayerHit;
    [SerializeField] private UnityEvent onExploded;

    // Static so DroneLessonUI can hear about drones that are spawned at runtime without
    // any per-instance wiring.
    public static event Action<DroneAI, DroneState, DroneState> StateChanged;

    public DroneState State { get; private set; } = DroneState.None;

    private float _detectTimer;
    private float _sightTimer;
    private float _hoverTimer;
    private bool _arrivedAtTarget;
    private bool _hoverReached;
    private float _identifyTimer;
    private Vector3 _diveDirection;
    private float _diveRemaining;
    private float _leaveTimer;

    public void Initialize(Transform target, Transform player)
    {
        flybyTarget = target;
        playerHead = player;
    }

    private void Awake()
    {
        if (attackCollider != null)
            attackCollider.enabled = false;
    }

    private void Start()
    {
        if (playerHead == null && Camera.main != null)
            playerHead = Camera.main.transform;

        SetState(DroneState.Search);
    }

    private void Update()
    {
        switch (State)
        {
            case DroneState.Search:
                TickSearch();
                break;
            case DroneState.Identify:
                TickIdentify();
                break;
            case DroneState.Attack:
                TickAttack();
                break;
            case DroneState.Leave:
                TickLeave();
                break;
        }
    }

    private void TickSearch()
    {
        if (TickDetection())
        {
            SetState(DroneState.Identify);
            return;
        }

        if (!_arrivedAtTarget)
        {
            if (flybyTarget == null)
            {
                // Nothing to fly toward - degenerate case, just leave straight ahead.
                SetState(DroneState.Leave);
                return;
            }

            Vector3 toTarget = flybyTarget.position - transform.position;
            if (toTarget.magnitude <= arriveThreshold)
            {
                _arrivedAtTarget = true;
                _hoverTimer = 0f;
                return;
            }

            MoveAndFace(toTarget.normalized, searchSpeed);
        }
        else
        {
            _hoverTimer += Time.deltaTime;
            if (_hoverTimer >= searchHoverDuration)
                SetState(DroneState.Leave);
        }
    }

    // Returns true once the player has been continuously visible for detectConfirmTime.
    private bool TickDetection()
    {
        if (playerHead == null)
            return false;

        _detectTimer -= Time.deltaTime;
        if (_detectTimer <= 0f)
        {
            _detectTimer = detectInterval;
            if (HasLineOfSight())
                _sightTimer += detectInterval;
            else
                _sightTimer = 0f;
        }

        return _sightTimer >= detectConfirmTime;
    }

    private bool HasLineOfSight()
    {
        Vector3 toPlayer = playerHead.position - transform.position;
        if (toPlayer.sqrMagnitude > detectRadius * detectRadius)
            return false;

        // The player's own CharacterController may envelop the head point - a hit that
        // belongs to the player still counts as clear line of sight.
        if (Physics.Linecast(transform.position, playerHead.position, out RaycastHit hit, obstacleMask, QueryTriggerInteraction.Ignore))
            return hit.collider.GetComponentInParent<PlayerController>() != null;

        return true;
    }

    private void TickIdentify()
    {
        if (playerHead == null)
        {
            SetState(DroneState.Leave);
            return;
        }

        Vector3 toPlayer = playerHead.position - transform.position;
        float distance = toPlayer.magnitude;
        Vector3 direction = distance > 0.001f ? toPlayer / distance : transform.forward;

        FaceYaw(direction);

        if (distance > hoverDistance + hoverDeadband)
        {
            float step = Mathf.Min(approachSpeed * Time.deltaTime, distance - hoverDistance);
            transform.position += direction * step;
        }
        else if (distance < hoverDistance - hoverDeadband)
        {
            transform.position -= direction * (approachSpeed * 0.5f * Time.deltaTime);
        }

        if (!_hoverReached && distance <= hoverDistance + hoverDeadband)
            _hoverReached = true;

        if (_hoverReached)
        {
            _identifyTimer += Time.deltaTime;
            if (_identifyTimer >= identifyDuration)
                SetState(DroneState.Attack);
        }
    }

    private void TickAttack()
    {
        float step = diveSpeed * Time.deltaTime;
        if (step <= 0f)
            return;

        // Sphere cast ahead of the movement so a fast dive can't tunnel through the
        // player or a thin cover collider between frames.
        if (Physics.SphereCast(transform.position, diveCastRadius, _diveDirection, out RaycastHit hit, step, obstacleMask, QueryTriggerInteraction.Ignore))
        {
            transform.position += _diveDirection * hit.distance;
            HandleImpact(hit.collider);
            return;
        }

        transform.position += _diveDirection * step;
        FaceYaw(_diveDirection);

        _diveRemaining -= step;
        if (_diveRemaining <= 0f)
        {
            Debug.Log($"{name}: dive overshot without hitting anything, self-destructing.", this);
            Explode();
        }
    }

    private void TickLeave()
    {
        transform.position += transform.forward * (searchSpeed * Time.deltaTime);

        _leaveTimer += Time.deltaTime;
        if (_leaveTimer >= leaveLifetime)
            Destroy(gameObject);
    }

    private void OnTriggerEnter(Collider other)
    {
        if (State != DroneState.Attack)
            return;
        if (other.transform.IsChildOf(transform))
            return;

        HandleImpact(other);
    }

    private void HandleImpact(Collider other)
    {
        if (State == DroneState.Exploded)
            return;

        bool hitPlayer = other.GetComponentInParent<PlayerController>() != null;
        Debug.Log($"{name}: dive impact on '{other.name}' (player: {hitPlayer}).", this);

        if (hitPlayer)
            onPlayerHit?.Invoke();

        Explode();
    }

    // Placeholder - VFX/audio/slow-motion hook up here later via onExploded or directly.
    public void Explode()
    {
        if (State == DroneState.Exploded)
            return;

        SetState(DroneState.Exploded);
        onExploded?.Invoke();
        Destroy(gameObject);
    }

    private void MoveAndFace(Vector3 direction, float speed)
    {
        transform.position += direction * (speed * Time.deltaTime);
        FaceYaw(direction);
    }

    // Root only ever yaws - the body stays level (see class comment).
    private void FaceYaw(Vector3 direction)
    {
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f)
            return;

        Quaternion target = Quaternion.LookRotation(direction.normalized, Vector3.up);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, target, turnSpeed * Time.deltaTime);
    }

    private void SetState(DroneState next)
    {
        if (State == next)
            return;

        DroneState previous = State;
        State = next;

        switch (next)
        {
            case DroneState.Identify:
                _hoverReached = false;
                _identifyTimer = 0f;
                break;
            case DroneState.Attack:
                Vector3 lockPoint = playerHead != null ? playerHead.position : transform.position + transform.forward;
                Vector3 toLock = lockPoint - transform.position;
                _diveDirection = toLock.sqrMagnitude > 0.0001f ? toLock.normalized : transform.forward;
                _diveRemaining = toLock.magnitude + diveOvershoot;
                if (attackCollider != null)
                    attackCollider.enabled = true;
                break;
            case DroneState.Leave:
                _leaveTimer = 0f;
                break;
        }

        Debug.Log($"{name}: {previous} -> {next}", this);
        StateChanged?.Invoke(this, previous, next);
    }
}
