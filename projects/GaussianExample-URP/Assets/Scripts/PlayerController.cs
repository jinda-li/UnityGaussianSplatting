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
    [SerializeField] private VRPlayer.AvatarFirstPersonVisibility avatarVisibility;

    [Header("Movement")]
    [SerializeField] private float moveSpeed = 2.5f;
    [SerializeField] private float gravity = -25f;

    [Header("Calibration")]
    [SerializeField] private KeyCode calibrateKey = KeyCode.C;

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
        if (Input.GetKeyDown(calibrateKey))
            ApplyCalibration();

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

        // Root motion has to stay on even in first-person: turning your head without
        // moving is normal in room-scale VR, and VRIK's turn-on-spot animation is the
        // only thing that can actually rotate the root to catch up with it (the
        // solver's own root-lerp only ever corrects position, never rotation — see
        // TickLocomotion). With this off, VRIK_Turn gets stuck at whatever mismatch
        // exists between body and head facing and never resolves, which is what was
        // causing perpetual foot-shuffling while standing still.
        if (animator != null)
            animator.applyRootMotion = true;
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

        // VRIK Animated Locomotion needs Mecanim root motion to actually turn the
        // model (its turn-on-spot blend tree only rotates the root via animator
        // root motion; the solver's own root-lerp only ever corrects position,
        // never rotation). Without this the character keeps replaying the turn
        // animation without ever resolving it, i.e. spins in place.
        if (animator != null)
            animator.applyRootMotion = true;

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
        // staying behind while IKRetarget is disabled. Dragging the head target is itself the
        // "move" signal VRIK Animated Locomotion walks toward; the avatar's real root position
        // is owned by VRIK's own root-lerp + root motion, not by PlayerBody.
        //
        // Do NOT call vrik.solver.AddPlatformMotion here: that only nudges the solver's
        // internal last-root-position bookkeeping, it does not move references.root (which
        // isn't parented under PlayerBody). Calling it with a delta that was never actually
        // applied to the root desyncs that bookkeeping from the real root transform and
        // corrupts the next frame's offset/turn calculations — a second source of the
        // rotation/offset glitches on top of the applyRootMotion issue above.
        if (ikRigRoot != null)
            ikRigRoot.position += actualDelta;
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
        // Redundant if cameraRig already drives avatarVisibility; keeps Forest setups that
        // reference visibility only on PlayerController working.
        avatarVisibility?.SetFirstPerson(true);

        if (playerBody != null)
            playerBody.enabled = true;

        _hasLastHmdPosition = false;

        if (animator != null)
            animator.applyRootMotion = true;

        _verticalVelocity = 0f;
    }

    private void EnterLocomotion()
    {
        _state = PlayerState.Locomotion;

        if (ikRetarget != null)
            ikRetarget.enabled = false;

        cameraRig?.SetLocomotionState(true);
        avatarVisibility?.SetFirstPerson(false);

        if (playerBody != null)
            playerBody.enabled = true;

        if (animator != null)
            animator.applyRootMotion = true;
    }

    // Replaces VRIKCalibrationBasic / VRCalibrationTrigger: those either reparent
    // solver targets under the XR anchors (VRIKCalibrator.Calibrate, breaks IKRetarget's
    // static IKRigRoot targets) or only ever touched scale, never rotation. Only usable
    // in Idle (first-person): during Locomotion the head target is a frozen/dragged
    // proxy, not a live HMD reading, so there is nothing meaningful to calibrate against.
    [ContextMenu("Apply Calibration")]
    public void ApplyCalibration()
    {
        if (_state != PlayerState.Idle)
        {
            Debug.LogWarning($"{nameof(PlayerController)}: calibration only works in first-person (Idle) state.", this);
            return;
        }

        if (vrik == null || hmd == null)
        {
            Debug.LogError($"{nameof(PlayerController)}: missing vrik or hmd reference for calibration.", this);
            return;
        }

        Transform root = vrik.references.root;
        Transform headBone = vrik.references.head;
        Transform headTarget = vrik.solver.spine.headTarget;

        if (root == null || headBone == null || headTarget == null)
        {
            Debug.LogError($"{nameof(PlayerController)}: VRIK root/head bone/head target not assigned.", this);
            return;
        }

        // Re-face the body to the camera. This rig's mesh visually faces root's local
        // -Z, not +Z — confirmed by live testing: with root.forward pointed straight at
        // the camera, the character stood facing directly away from it. VRIK's own
        // continuous turn-tracking already compensates for this via
        // vrik.solver.spine.rootHeadingOffset (set to 180 on this character in the
        // Inspector); apply that same offset here so a one-time calibration snap agrees
        // with it instead of fighting it on the next frame.
        //
        // (Comparing the head *bone's* current forward to the camera — the previous
        // approach here — doesn't work: VRIK continuously re-solves the head bone to
        // already match the camera regardless of root's facing, so that comparison is
        // always ~zero and never actually corrects root.)
        Vector3 cameraForward = FlattenY(hmd.forward);
        if (cameraForward.sqrMagnitude > 0.0001f)
        {
            Quaternion headingFix = Quaternion.Euler(0f, vrik.solver.spine.rootHeadingOffset, 0f);
            root.rotation = Quaternion.LookRotation(cameraForward.normalized) * headingFix;
        }

        // Re-derive scale from the current head target height vs the head bone's height.
        float rootToHeadTarget = headTarget.position.y - root.position.y;
        float rootToHeadBone = headBone.position.y - root.position.y;
        if (Mathf.Abs(rootToHeadBone) > 0.0001f)
        {
            float sizeF = rootToHeadTarget / rootToHeadBone;
            if (sizeF > 0f)
                root.localScale = Vector3.one * (root.localScale.y * sizeF);
        }

        vrik.solver.FixTransforms();

        // Re-sync the camera rig to the freshly calibrated avatar so the view doesn't stay
        // offset from the body that just rotated/rescaled under it.
        cameraRig?.SnapRigToAvatarHead();
    }

    private static Vector3 FlattenY(Vector3 v)
    {
        v.y = 0f;
        return v;
    }
}
