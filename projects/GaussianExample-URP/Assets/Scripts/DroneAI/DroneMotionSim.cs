using UnityEngine;

// Purely visual drone attitude on the Graphic child: derives the root's velocity and yaw
// rate from its transform each frame and tilts the body the way a multirotor leans into
// its motion (pitch forward with speed, roll into sideways drift and turns). The root
// itself stays level - swapping the placeholder cube for a real drone model later only
// means replacing the mesh under this component.
public class DroneMotionSim : MonoBehaviour
{
    [Header("Tilt")]
    [SerializeField, Min(0f)] private float maxTiltAngle = 25f;
    [Tooltip("Degrees of forward pitch per m/s of forward speed.")]
    [SerializeField, Min(0f)] private float pitchPerSpeed = 4f;
    [Tooltip("Degrees of roll per m/s of sideways speed.")]
    [SerializeField, Min(0f)] private float rollPerSpeed = 4f;
    [Tooltip("Degrees of banking roll per deg/s of yaw rate.")]
    [SerializeField, Min(0f)] private float rollPerYawRate = 0.1f;
    [Tooltip("How quickly the tilt follows the target attitude (higher = snappier).")]
    [SerializeField, Min(0.01f)] private float tiltSmoothing = 6f;

    private Transform _root;
    private Vector3 _lastPosition;
    private float _lastYaw;

    private void Start()
    {
        _root = transform.parent != null ? transform.parent : transform;
        _lastPosition = _root.position;
        _lastYaw = _root.eulerAngles.y;
    }

    // LateUpdate so the root's movement for this frame (DroneAI runs in Update) is
    // already applied.
    private void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f)
            return;

        Vector3 velocity = (_root.position - _lastPosition) / dt;
        _lastPosition = _root.position;

        float yaw = _root.eulerAngles.y;
        float yawRate = Mathf.DeltaAngle(_lastYaw, yaw) / dt;
        _lastYaw = yaw;

        Vector3 localVelocity = _root.InverseTransformDirection(velocity);

        float pitch = Mathf.Clamp(localVelocity.z * pitchPerSpeed, -maxTiltAngle, maxTiltAngle);
        float roll = Mathf.Clamp(-localVelocity.x * rollPerSpeed - yawRate * rollPerYawRate, -maxTiltAngle, maxTiltAngle);

        Quaternion target = Quaternion.Euler(pitch, 0f, roll);
        float blend = 1f - Mathf.Exp(-tiltSmoothing * dt);
        transform.localRotation = Quaternion.Slerp(transform.localRotation, target, blend);
    }
}
