using UnityEngine;

namespace VRPlayer
{
    public class VRPlayerRootMotionController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Animator animator;
        [SerializeField] private VRPlayerControllerInput input;
        [SerializeField] private Transform hmd;
        [SerializeField] private Transform forwardReference;
        [SerializeField] private Transform bodyRoot;
        [SerializeField] private Transform motionRoot;
        [SerializeField] private CharacterController characterController;

        [Header("Animator Parameters")]
        [SerializeField] private string forwardParam = "ForwardSpeed";
        [SerializeField] private string lateralParam = "LateralSpeed";
        [SerializeField] private string turningParam = "TurningSpeed";
        [SerializeField] private string strafeParam = "Strafe";

        [Header("Action Triggers")]
        [SerializeField] private string dodgeRollTrigger = "DodgeRoll";

        [Header("Action State Names")]
        [SerializeField] private string dodgeRollStateName = "Dive Roll";
        [SerializeField] private int actionLayer = 0;

        [Header("Locomotion")]
        [SerializeField] private float deadZone = 0.15f;
        [SerializeField] private float responsiveness = 10f;
        [SerializeField] private float idleThreshold = 0.05f;

        [Header("Facing")]
        [SerializeField] private float yawRotateSpeed = 12f;

        [Header("Directional Dodge Roll")]
        [SerializeField] private float directionalRollInputThreshold = 0.20f;

        [Header("Vertical Movement")]
        [SerializeField] private float gravity = -25f;

        [Header("Teleport")]
        [SerializeField] private int updateLockFramesAfterTeleport = 1;
        [SerializeField] private int gravitySkipFramesAfterTeleport = 2;

        public bool LocomotionEnabled { get; private set; } = true;
        public bool FacingEnabled { get; private set; } = true;
        public bool IsMoving { get; private set; }

        private int _forwardHash;
        private int _lateralHash;
        private int _turningHash;
        private int _strafeHash;
        private int _dodgeRollTriggerHash;

        private float _currentForward;
        private float _currentLateral;
        private float _verticalVelocity;

        private Vector3 _pendingRootMotionDelta;
        private Quaternion _pendingRootMotionRotation = Quaternion.identity;

        private int _updateLockFrames;
        private int _skipGravityFrames;
        private bool _isTeleporting;

        private bool _hasStoredFacing;
        private Quaternion _storedFacingRotation = Quaternion.identity;

        private void Awake()
        {
            if (animator == null)
                Debug.LogError($"{nameof(VRPlayerRootMotionController)} on {name} is missing an Animator reference.", this);

            if (motionRoot == null && characterController != null)
                motionRoot = characterController.transform;

            if (bodyRoot == null)
            {
                if (motionRoot != null)
                    bodyRoot = motionRoot;
                else
                    bodyRoot = transform;
            }

            if (hmd == null && Camera.main != null)
                hmd = Camera.main.transform;

            _forwardHash = Animator.StringToHash(forwardParam);
            _lateralHash = Animator.StringToHash(lateralParam);
            _turningHash = Animator.StringToHash(turningParam);
            _strafeHash = Animator.StringToHash(strafeParam);
            _dodgeRollTriggerHash = Animator.StringToHash(dodgeRollTrigger);

            if (animator != null)
                animator.applyRootMotion = true;
        }

        private void Update()
        {
            if (animator == null)
                return;

            if (_isTeleporting)
                return;

            if (_updateLockFrames > 0)
            {
                _updateLockFrames--;
                return;
            }

            if (FacingEnabled)
                UpdateFacing();

            UpdateLocomotion();
            ApplyMotion();
        }

        public void SetMovementReferences(Transform newHmd, Transform newForward)
        {
            hmd = newHmd;
            forwardReference = newForward;
        }

        public void SetLocomotionEnabled(bool enabled)
        {
            LocomotionEnabled = enabled;

            if (!enabled)
                ResetLocomotionParameters();
        }

        public void SetFacingEnabled(bool enabled)
        {
            FacingEnabled = enabled;
        }

        public bool WantsToMove()
        {
            return input != null && input.IsMovePressed;
        }

        public void TriggerDodgeRoll()
        {
            if (animator == null)
                return;

            animator.SetTrigger(_dodgeRollTriggerHash);
        }

        public bool IsInDodgeRollState()
        {
            return IsInAnimatorState(dodgeRollStateName);
        }

        public void StoreCurrentFacing()
        {
            if (bodyRoot == null)
                return;

            _storedFacingRotation = bodyRoot.rotation;
            _hasStoredFacing = true;
        }

        public void RestoreStoredFacing()
        {
            if (!_hasStoredFacing || bodyRoot == null)
                return;

            bodyRoot.rotation = _storedFacingRotation;
            _hasStoredFacing = false;
        }

        public void AlignFacingToMovementOrCurrent()
        {
            if (bodyRoot == null)
                return;

            Vector2 moveAxis = input != null ? input.MoveAxis : Vector2.zero;
            Vector3 desiredDirection = GetHmdRelativeWorldMove(moveAxis);
            desiredDirection.y = 0f;

            if (desiredDirection.sqrMagnitude < directionalRollInputThreshold * directionalRollInputThreshold)
                desiredDirection = bodyRoot.forward;

            desiredDirection.y = 0f;

            if (desiredDirection.sqrMagnitude < 0.0001f)
                return;

            bodyRoot.rotation = Quaternion.LookRotation(desiredDirection.normalized, Vector3.up);
        }

        public void AlignFacingToWorldDirectionOrCurrent(Vector3 worldDirection)
        {
            if (bodyRoot == null)
                return;

            worldDirection.y = 0f;

            if (worldDirection.sqrMagnitude < directionalRollInputThreshold * directionalRollInputThreshold)
                worldDirection = bodyRoot.forward;

            worldDirection.y = 0f;

            if (worldDirection.sqrMagnitude < 0.0001f)
                return;

            bodyRoot.rotation = Quaternion.LookRotation(worldDirection.normalized, Vector3.up);
        }

        private bool IsInAnimatorState(string stateName)
        {
            if (animator == null || string.IsNullOrWhiteSpace(stateName))
                return false;

            AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(actionLayer);
            return state.IsName(stateName);
        }

        public void CaptureRootMotion(Vector3 deltaPosition, Quaternion deltaRotation)
        {
            if (_isTeleporting || _updateLockFrames > 0)
                return;

            _pendingRootMotionDelta += deltaPosition;
            _pendingRootMotionRotation = deltaRotation * _pendingRootMotionRotation;
        }

        public void BeginTeleport()
        {
            _isTeleporting = true;
            _updateLockFrames = 0;
            _skipGravityFrames = 0;

            _pendingRootMotionDelta = Vector3.zero;
            _pendingRootMotionRotation = Quaternion.identity;
            _verticalVelocity = 0f;

            ResetLocomotionParameters();

            if (animator != null)
                animator.applyRootMotion = false;

            if (characterController != null && characterController.enabled)
                characterController.enabled = false;
        }

        public void EndTeleport()
        {
            Physics.SyncTransforms();

            if (characterController != null && !characterController.enabled)
                characterController.enabled = true;

            _pendingRootMotionDelta = Vector3.zero;
            _pendingRootMotionRotation = Quaternion.identity;
            _verticalVelocity = 0f;

            _skipGravityFrames = Mathf.Max(0, gravitySkipFramesAfterTeleport);
            _updateLockFrames = Mathf.Max(0, updateLockFramesAfterTeleport);

            if (animator != null)
                animator.applyRootMotion = true;

            _isTeleporting = false;
        }

        private void UpdateFacing()
        {
            if (bodyRoot == null || forwardReference == null)
                return;

            Vector3 forward = forwardReference.forward;
            forward.y = 0f;

            if (forward.sqrMagnitude < 0.0001f)
                return;

            Quaternion targetRotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
            bodyRoot.rotation = Quaternion.Slerp(
                bodyRoot.rotation,
                targetRotation,
                yawRotateSpeed * Time.deltaTime
            );
        }

        private void UpdateLocomotion()
        {
            if (!LocomotionEnabled)
            {
                IsMoving = false;
                animator.SetBool(_strafeHash, false);
                return;
            }

            Vector2 stick = input != null ? input.MoveAxis : Vector2.zero;

            if (stick.magnitude < deadZone)
                stick = Vector2.zero;

            Vector3 worldMove = GetHmdRelativeWorldMove(stick);
            Vector3 localMove = bodyRoot != null
                ? bodyRoot.InverseTransformDirection(worldMove)
                : transform.InverseTransformDirection(worldMove);

            float targetForward = localMove.z;
            float targetLateral = localMove.x;

            _currentForward = Mathf.MoveTowards(_currentForward, targetForward, responsiveness * Time.deltaTime);
            _currentLateral = Mathf.MoveTowards(_currentLateral, targetLateral, responsiveness * Time.deltaTime);

            animator.SetFloat(_forwardHash, _currentForward);
            animator.SetFloat(_lateralHash, _currentLateral);
            animator.SetFloat(_turningHash, 0f);

            IsMoving =
                Mathf.Abs(_currentForward) > idleThreshold ||
                Mathf.Abs(_currentLateral) > idleThreshold;

            animator.SetBool(_strafeHash, IsMoving);
        }

        private void ApplyMotion()
        {
            if (motionRoot == null && characterController != null)
                motionRoot = characterController.transform;

            Transform target = motionRoot != null ? motionRoot : transform;
            if (target == null)
                return;

            if (_pendingRootMotionRotation != Quaternion.identity)
                target.rotation = _pendingRootMotionRotation * target.rotation;

            Vector3 horizontalDelta = _pendingRootMotionDelta;
            horizontalDelta.y = 0f;

            if (_skipGravityFrames > 0)
            {
                _skipGravityFrames--;

                if (characterController != null)
                    characterController.Move(horizontalDelta);
                else
                    target.position += horizontalDelta;

                _pendingRootMotionDelta = Vector3.zero;
                _pendingRootMotionRotation = Quaternion.identity;
                _verticalVelocity = 0f;
                return;
            }

            bool isGrounded = characterController != null
                ? characterController.isGrounded
                : true;

            if (isGrounded && _verticalVelocity < 0f)
                _verticalVelocity = 0f;
            else
                _verticalVelocity += gravity * Time.deltaTime;

            Vector3 verticalDelta = Vector3.up * (_verticalVelocity * Time.deltaTime);
            Vector3 finalDelta = horizontalDelta + verticalDelta;

            if (characterController != null)
            {
                CollisionFlags flags = characterController.Move(finalDelta);

                if ((flags & CollisionFlags.Below) != 0 && _verticalVelocity < 0f)
                    _verticalVelocity = 0f;
            }
            else
            {
                target.position += finalDelta;
            }

            _pendingRootMotionDelta = Vector3.zero;
            _pendingRootMotionRotation = Quaternion.identity;
        }

        private void ResetLocomotionParameters()
        {
            _currentForward = 0f;
            _currentLateral = 0f;
            IsMoving = false;

            if (animator == null)
                return;

            animator.SetFloat(_forwardHash, 0f);
            animator.SetFloat(_lateralHash, 0f);
            animator.SetFloat(_turningHash, 0f);
            animator.SetBool(_strafeHash, false);
        }

        private Vector3 GetHmdRelativeWorldMove(Vector2 stick)
        {
            if (hmd == null)
                return new Vector3(stick.x, 0f, stick.y);

            Vector3 forward = hmd.forward;
            Vector3 right = hmd.right;

            forward.y = 0f;
            right.y = 0f;

            if (forward.sqrMagnitude < 0.0001f)
                forward = Vector3.forward;

            if (right.sqrMagnitude < 0.0001f)
                right = Vector3.right;

            forward.Normalize();
            right.Normalize();

            Vector3 move = forward * stick.y + right * stick.x;

            if (move.sqrMagnitude > 1f)
                move.Normalize();

            return move;
        }

        private void OnAnimatorMove()
        {
            if (animator == null || !animator.applyRootMotion)
                return;

            CaptureRootMotion(animator.deltaPosition, animator.deltaRotation);
        }

        public void RestartDodgeRoll()
        {
            if (animator == null)
                return;

            _pendingRootMotionDelta = Vector3.zero;
            _pendingRootMotionRotation = Quaternion.identity;

            animator.Play(dodgeRollStateName, actionLayer, 0f);
            animator.Update(0f);
        }

        public void PrepareForLocomotion()
        {
            _pendingRootMotionDelta = Vector3.zero;
            _pendingRootMotionRotation = Quaternion.identity;
            _verticalVelocity = 0f;
            Physics.SyncTransforms();
        }

        public bool CanChainDodgeRoll(float chainStartNormalizedTime = 0.7f)
        {
            if (animator == null)
                return false;

            AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(actionLayer);

            if (!state.IsName(dodgeRollStateName))
                return false;

            return state.normalizedTime >= chainStartNormalizedTime;
        }
    }
}
