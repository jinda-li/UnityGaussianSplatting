using RootMotion.FinalIK;
using UnityEngine;
using VRPlayer;

public class PlayerController : MonoBehaviour
{
    private enum PlayerState
    {
        Idle,
        Locomotion
    }

    [Header("References")]
    [SerializeField] private VRPlayerControllerInput input;
    [SerializeField] private VRCameraRigController cameraRig;
    [SerializeField] private IKRetarget ikRetarget;
    [SerializeField] private Transform hmd;
    [SerializeField] private VRIK vrik;
    [SerializeField] private CharacterController playerBody;
    [SerializeField] private Transform ikRigRoot;
    [SerializeField] private Animator animator;

    [Header("Movement")]
    [SerializeField] private float moveSpeed = 2.5f;
    [SerializeField] private float gravity = -25f;

    private PlayerState _state = PlayerState.Idle;
    private float _verticalVelocity;
    private Vector3 _lastHmdPosition;
    private bool _hasLastHmdPosition;

    private void Awake()
    {
        if (animator == null && vrik != null)
            animator = vrik.GetComponent<Animator>();
    }

    private void OnEnable()
    {
        if (input != null)
        {
            input.EnableMovement();
            input.EnableCameraChange();
        }

        EnterIdle();
    }

    private void Update()
    {
        if (input == null)
            return;

        HandleSnapTurn();
        HandleStateTransitions();

        switch (_state)
        {
            case PlayerState.Idle:
                TickIdle();
                break;
            case PlayerState.Locomotion:
                TickLocomotion();
                break;
        }
    }

    private void HandleSnapTurn()
    {
        int direction = input.GetCameraSnapDirection();
        if (direction < 0)
            cameraRig?.SnapLeft();
        else if (direction > 0)
            cameraRig?.SnapRight();
    }

    private void HandleStateTransitions()
    {
        if (input.GetMovementStarted())
            EnterLocomotion();
        else if (input.GetMovementEnded() && !input.IsMovePressed)
            EnterIdle();
    }

    private void TickIdle()
    {
        if (playerBody != null)
        {
            playerBody.enabled = true;
            FollowHeadHorizontally();
        }

        // VRIK Animated Locomotion moves the root via solver root lerp, not animator root motion.
        if (animator != null)
            animator.applyRootMotion = false;
    }

    // Room-scale: drag the CharacterController along with the physical HMD movement using
    // incremental Move() (collision-safe), instead of hard-setting the root position (which
    // caused teleport-style jumps in the old AvatarHeadFollower approach).
    private void FollowHeadHorizontally()
    {
        if (hmd == null)
            return;

        if (!_hasLastHmdPosition)
        {
            _lastHmdPosition = hmd.position;
            _hasLastHmdPosition = true;
            return;
        }

        Vector3 delta = hmd.position - _lastHmdPosition;
        delta.y = 0f;
        _lastHmdPosition = hmd.position;

        if (delta.sqrMagnitude < 0.0001f)
            return;

        playerBody.Move(delta);
    }

    private void TickLocomotion()
    {
        if (playerBody == null || vrik == null)
            return;

        playerBody.enabled = true;

        if (animator != null)
            animator.applyRootMotion = false;

        bool grounded = playerBody.isGrounded;
        if (grounded && _verticalVelocity < 0f)
            _verticalVelocity = 0f;
        else
            _verticalVelocity += gravity * Time.deltaTime;

        Vector3 horizontal = GetViewRelativeMove(input.MoveAxis) * moveSpeed * Time.deltaTime;
        Vector3 vertical = Vector3.up * (_verticalVelocity * Time.deltaTime);

        Vector3 positionBefore = playerBody.transform.position;
        CollisionFlags flags = playerBody.Move(horizontal + vertical);
        Vector3 actualDelta = playerBody.transform.position - positionBefore;

        if ((flags & CollisionFlags.Below) != 0 && _verticalVelocity < 0f)
            _verticalVelocity = 0f;

        // IKRigRoot is a separate GameObject (not a child of PlayerBody), so during locomotion
        // it has to be dragged along by PlayerBody's actual (collision-resolved) delta each
        // frame — this keeps Head/Left Hand/Right Hand riding along with the body instead of
        // staying behind while IKRetarget is disabled.
        if (ikRigRoot != null)
            ikRigRoot.position += actualDelta;

        vrik.solver.AddPlatformMotion(actualDelta, Quaternion.identity, playerBody.transform.position);
    }

    private Vector3 GetViewRelativeMove(Vector2 stick)
    {
        float yaw = hmd != null ? hmd.eulerAngles.y : 0f;
        Quaternion viewYaw = Quaternion.Euler(0f, yaw, 0f);
        Vector3 inputLocal = new Vector3(stick.x, 0f, stick.y);
        return viewYaw * inputLocal;
    }

    private void EnterIdle()
    {
        _state = PlayerState.Idle;

        if (ikRetarget != null)
            ikRetarget.enabled = true;

        cameraRig?.SnapRigToAvatarHead();
        cameraRig?.SetLocomotionState(false);

        if (playerBody != null)
            playerBody.enabled = true;

        _hasLastHmdPosition = false;

        if (animator != null)
            animator.applyRootMotion = false;

        _verticalVelocity = 0f;
    }

    private void EnterLocomotion()
    {
        _state = PlayerState.Locomotion;

        if (ikRetarget != null)
            ikRetarget.enabled = false;

        cameraRig?.SetLocomotionState(true);

        if (playerBody != null)
            playerBody.enabled = true;

        if (animator != null)
            animator.applyRootMotion = false;
    }
}
