using UnityEngine;
using UnityEngine.Events;

namespace VRPlayer
{
    public class VRCameraRigController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Transform xrOrigin;
        [SerializeField] private Transform hmd;
        [SerializeField] private Transform avatarHead;
        [SerializeField] private Transform avatarRoot;
        [SerializeField] private VRPlayerControllerInput input;

        [Header("Snap Turn")]
        [SerializeField] private float snapAngleDeg = 35f;

        [Header("Orbit (Locomotion On)")]
        [SerializeField] private float orbitRadius = 2.5f;

        [Header("Orbit Collision")]
        [SerializeField] private bool preventOrbitClipping = true;
        [SerializeField] private float orbitCollisionBuffer = 0.10f;
        [SerializeField] private LayerMask orbitCollisionMask = ~0;
        [SerializeField] private QueryTriggerInteraction orbitTriggerInteraction = QueryTriggerInteraction.Ignore;

        [Header("Catch Up")]
        [SerializeField] private float catchUpInterval = 0.25f;
        [SerializeField] private bool catchUpOnlyWhileLocomoting = true;

        [Header("Locomotion Start Nudge")]
        [SerializeField] private float locomotionStartDeadzone = 0.2f;
        [SerializeField] private float forwardConeHalfAngleDeg = 30f;
        [SerializeField] private float backwardConeHalfAngleDeg = 35f;
        [SerializeField] private float strafeNudgeBackMeters = 0.25f;
        [SerializeField] private float strafeNudgeSideMeters = 0.25f;
        [SerializeField] private float backwardSnapBackMeters = 1.0f;

        [Header("Target Lock")]
        [SerializeField] private bool targetLockEnabled;
        [SerializeField] private GameObject targetLockGameObject;
        [SerializeField] private Transform targetLockTransform;

        [SerializeField] private bool isLocomoting;

        [Header("Events")]
        [SerializeField] private UnityEvent onLocomotionStarted;
        [SerializeField] private UnityEvent onLocomotionEnded;

        private float _viewYawDeg;
        private float _catchUpTimer;

        public float GetBackwardsSnap() => backwardSnapBackMeters;
        public Vector3 GetAvatarRootPosition() => avatarRoot.position;

        private void OnEnable()
        {
            if (hmd != null)
                _viewYawDeg = YawOnly(hmd.rotation).eulerAngles.y;

            _catchUpTimer = 0f;
            ApplyTargetLockVisuals();
        }

        private void Update()
        {
            // if (input != null && input.GetTargetLockToggleDown())
            //     ToggleTargetLock();
        }

        
        private void LateUpdate()
        {
            if (xrOrigin == null || hmd == null || avatarHead == null)
                return;

            if (catchUpOnlyWhileLocomoting && !isLocomoting)
                return;

            _catchUpTimer -= Time.deltaTime;
            var dist = Vector3.Distance(hmd.position, avatarHead.position);
            var sign = Mathf.Sign(Vector3.Dot(hmd.forward, (avatarHead.position - hmd.position).normalized));
            dist *= sign;
            float CatchUpToOrbitDistance = 1f;
            bool jumpBackwards = dist < CatchUpToOrbitDistance;

            if (jumpBackwards|| _catchUpTimer <= 0f)
            {
                CatchUpToOrbitRadius(jumpBackwards);
                _catchUpTimer = catchUpInterval;
            }
        }

        /*public void SnapRigToAvatarHead()
        {
            if (xrOrigin == null || hmd == null || avatarHead == null)
                return;

            Quaternion hmdYaw = YawOnly(hmd.rotation);
            Quaternion headYaw = YawOnly(avatarHead.rotation);
            Quaternion yawDelta = headYaw * Quaternion.Inverse(hmdYaw);

            RotateAroundPivot(xrOrigin, hmd.position, yawDelta);

            Vector3 targetHmdPosition = avatarHead.position;
            xrOrigin.position += targetHmdPosition - hmd.position;

            _viewYawDeg = YawOnly(hmd.rotation).eulerAngles.y;
        }*/

        // Added target-locked logic
        public void SnapRigToAvatarHead()
        {
            if (xrOrigin == null || hmd == null || avatarHead == null)
                return;

            if (targetLockEnabled && TryGetTargetLockDirectionFromAvatar(out Vector3 toTarget))
            {
                Vector3 desiredHmdPosition = avatarHead.position;

                /*if (isLocomoting)
                    desiredHmdPosition -= toTarget * orbitRadius;*/

                desiredHmdPosition.y = avatarHead.position.y;

                Quaternion desiredYaw = Quaternion.LookRotation(toTarget, Vector3.up);
                ApplyDesiredHmdPose(desiredHmdPosition, desiredYaw);
                return;
            }

            Quaternion hmdYaw = YawOnly(hmd.rotation);
            Quaternion headYaw = YawOnly(avatarHead.rotation);
            Quaternion yawDelta = headYaw * Quaternion.Inverse(hmdYaw);

            RotateAroundPivot(xrOrigin, hmd.position, yawDelta);

            Vector3 targetHmdPosition = avatarHead.position;
            xrOrigin.position += targetHmdPosition - hmd.position;

            _viewYawDeg = YawOnly(hmd.rotation).eulerAngles.y;
        }

        public void SetLocomotionState(bool locomoting)
        {
            bool started = locomoting && !isLocomoting;
            bool ended = !locomoting && isLocomoting;

            isLocomoting = locomoting;

            if (hmd != null)
                _viewYawDeg = YawOnly(hmd.rotation).eulerAngles.y;

            if (started)
            {
                onLocomotionStarted?.Invoke();
                TryNudgeCameraOnLocomotionStart();
            }

            if (ended)
            {
                onLocomotionEnded?.Invoke();
            }
        }

        public void SnapLeft()
        {
            ApplySnap(-1f);
        }

        public void SnapRight()
        {
            ApplySnap(+1f);
        }

        public void MoveToCurrentOrbitPosition()
        {
            if (xrOrigin == null || hmd == null || avatarHead == null)
                return;

            Vector3 desiredHmdPosition = GetDesiredOrbitHmdPositionFromViewYaw();
            Vector3 deltaPosition = desiredHmdPosition - hmd.position;
            xrOrigin.position += deltaPosition;
        }

        public void ToggleTargetLock()
        {
            bool newState = !targetLockEnabled;

            if (newState && GetTargetLockTransform() == null)
            {
                targetLockEnabled = false;
                ApplyTargetLockVisuals();
                return;
            }

            targetLockEnabled = newState;
            ApplyTargetLockVisuals();
        }

         public void ToggleTargetLock(Transform newTarget, bool enabled)
        {
            targetLockEnabled = !enabled;
            targetLockTransform = newTarget;
            ToggleTargetLock();
        }

        private void ApplySnap(float direction)
        {
            if (xrOrigin == null || hmd == null)
                return;

            _viewYawDeg += direction * snapAngleDeg;

            if (isLocomoting)
                SnapOrbitUsingViewYaw();
            else
                SnapTurnUsingViewYaw();
        }

        private void SnapTurnUsingViewYaw()
        {
            if (avatarHead == null)
                return;

            float currentYaw = YawOnly(hmd.rotation).eulerAngles.y;
            float deltaYaw = Mathf.DeltaAngle(currentYaw, _viewYawDeg);

            Quaternion rotation = Quaternion.Euler(0f, deltaYaw, 0f);
            RotateAroundPivot(xrOrigin, hmd.position, rotation);

            Vector3 desiredHmdPosition = hmd.position;
            desiredHmdPosition.y = avatarHead.position.y;

            Vector3 deltaPosition = desiredHmdPosition - hmd.position;
            xrOrigin.position += deltaPosition;
        }

        private void SnapOrbitUsingViewYaw()
        {
            if (avatarHead == null)
                return;

            if (targetLockEnabled && TryGetTargetLockDirectionFromAvatar(out Vector3 toTarget))
            {
                Vector3 lockedDesiredHmdPosition = avatarHead.position - toTarget * orbitRadius;
                lockedDesiredHmdPosition.y = avatarHead.position.y;

                Quaternion lockedDesiredYaw = Quaternion.LookRotation(toTarget, Vector3.up);
                ApplyDesiredHmdPose(lockedDesiredHmdPosition, lockedDesiredYaw);
                return;
            }

            Vector3 desiredHmdPosition = GetDesiredOrbitHmdPositionFromViewYaw();
            Quaternion desiredYaw = GetDesiredYawLookingAtAvatar(desiredHmdPosition);

            ApplyDesiredHmdPose(desiredHmdPosition, desiredYaw);
        }

        private void CatchUpToOrbitRadius(bool jumpBackwards = false)
        {
            if (avatarHead == null)
                return;

            if (targetLockEnabled && TryGetTargetLockDirectionFromAvatar(out Vector3 toTarget))
            {
                Vector3 lockedDesiredHmdPosition = avatarHead.position - toTarget * orbitRadius;
                lockedDesiredHmdPosition.y = avatarHead.position.y;

                Quaternion lockedDesiredYaw = Quaternion.LookRotation(toTarget, Vector3.up);
                ApplyDesiredHmdPose(lockedDesiredHmdPosition, lockedDesiredYaw);
                return;
            }

            Vector3 fallbackDesiredHmdPosition = GetDesiredOrbitHmdPositionFromViewYaw();
            Vector3 deltaPosition = fallbackDesiredHmdPosition - hmd.position;
            Vector3 jumpDistance = jumpBackwards ? deltaPosition.normalized * backwardSnapBackMeters : deltaPosition;
            xrOrigin.position += jumpDistance;// deltaPosition;
        }

        private Vector3 GetDesiredOrbitHmdPositionFromViewYaw()
        {
            Vector3 orbitCenter = avatarHead.position;

            Quaternion yawRotation = Quaternion.Euler(0f, _viewYawDeg, 0f);
            Vector3 lookDirection = yawRotation * Vector3.forward;
            lookDirection.y = 0f;

            if (lookDirection.sqrMagnitude < 0.0001f)
                lookDirection = Vector3.forward;
            else
                lookDirection.Normalize();

            float finalRadius = orbitRadius;

            if (preventOrbitClipping)
            {
                float clampedRadius;
                if (TryGetClampedOrbitRadius(orbitCenter, lookDirection, orbitRadius, out clampedRadius))
                    finalRadius = clampedRadius;
            }

            Vector3 desiredHmdPosition = orbitCenter - lookDirection * finalRadius;
            desiredHmdPosition.y = avatarHead.position.y;

            return desiredHmdPosition;
        }

        private Quaternion GetDesiredYawLookingAtAvatar(Vector3 desiredHmdPosition)
        {
            Vector3 toAvatar = avatarHead.position - desiredHmdPosition;
            toAvatar.y = 0f;

            if (toAvatar.sqrMagnitude < 0.0001f)
                toAvatar = Vector3.forward;

            return Quaternion.LookRotation(toAvatar.normalized, Vector3.up);
        }

        private bool TryGetClampedOrbitRadius(
            Vector3 orbitCenter,
            Vector3 lookDirection,
            float desiredRadius,
            out float clampedRadius)
        {
            clampedRadius = desiredRadius;

            if (desiredRadius <= 0f)
                return false;

            Vector3 targetPosition = orbitCenter - lookDirection * desiredRadius;
            Vector3 castVector = targetPosition - orbitCenter;
            float castDistance = castVector.magnitude;

            if (castDistance <= 0.0001f)
                return false;

            Vector3 castDirection = castVector / castDistance;

            if (!Physics.Raycast(
                    orbitCenter,
                    castDirection,
                    out RaycastHit hit,
                    castDistance,
                    orbitCollisionMask,
                    orbitTriggerInteraction))
            {
                return false;
            }

            clampedRadius = Mathf.Max(0f, hit.distance - orbitCollisionBuffer);
            return true;
        }

        public void nudgeTest() => TryNudgeCameraOnLocomotionStart();

        private void TryNudgeCameraOnLocomotionStart()
        {
            if (xrOrigin == null || hmd == null || avatarRoot == null || avatarHead == null || input == null)
                return;

            Vector2 axis = input.MoveAxis;

            if (axis.magnitude < locomotionStartDeadzone)
                return;

            Quaternion hmdYaw = YawOnly(hmd.rotation);

            Vector3 hmdForward = hmdYaw * Vector3.forward;
            Vector3 hmdRight = hmdYaw * Vector3.right;
            Vector3 hmdBack = -hmdForward;

            hmdForward.y = 0f;
            hmdRight.y = 0f;
            hmdBack.y = 0f;

            hmdForward.Normalize();
            hmdRight.Normalize();
            hmdBack.Normalize();

            Vector3 inputLocal = new Vector3(axis.x, 0f, axis.y);
            Vector3 moveDirection = hmdYaw * inputLocal;
            moveDirection.y = 0f;

            if (moveDirection.sqrMagnitude < 0.0001f)
                return;

            moveDirection.Normalize();

            float angleToForward = Vector3.Angle(hmdForward, moveDirection);
            if (angleToForward <= forwardConeHalfAngleDeg)
                return;

            float angleToBackward = Vector3.Angle(hmdBack, moveDirection);

            Vector3 desiredHmdPosition = hmd.position;
            Quaternion desiredYaw = hmdYaw;

            if (angleToBackward <= backwardConeHalfAngleDeg)
            {
                desiredHmdPosition += hmdBack * backwardSnapBackMeters;
            }
            else
            {
                float sideSign = Mathf.Sign(axis.x);
                desiredHmdPosition += hmdBack * strafeNudgeBackMeters;
                desiredHmdPosition += hmdRight * (sideSign * strafeNudgeSideMeters);
            }

            desiredHmdPosition.y = avatarHead.position.y;
            ApplyDesiredHmdPose(desiredHmdPosition, desiredYaw);
        }

        private void ApplyDesiredHmdPose(Vector3 desiredHmdPosition, Quaternion desiredHmdYaw)
        {
            float currentYaw = YawOnly(hmd.rotation).eulerAngles.y;
            float targetYaw = YawOnly(desiredHmdYaw).eulerAngles.y;
            float deltaYaw = Mathf.DeltaAngle(currentYaw, targetYaw);

            Quaternion yawRotation = Quaternion.Euler(0f, deltaYaw, 0f);
            RotateAroundPivot(xrOrigin, hmd.position, yawRotation);

            Vector3 deltaPosition = desiredHmdPosition - hmd.position;
            xrOrigin.position += deltaPosition;

            _viewYawDeg = YawOnly(hmd.rotation).eulerAngles.y;
        }

        private Transform GetTargetLockTransform()
        {
            if (targetLockTransform != null)
                return targetLockTransform;

            if (targetLockGameObject != null)
                return targetLockGameObject.transform;

            return null;
        }

        private bool TryGetTargetLockDirectionFromAvatar(out Vector3 directionToTarget)
        {
            directionToTarget = Vector3.zero;

            if (!targetLockEnabled || avatarHead == null)
                return false;

            Transform target = GetTargetLockTransform();
            if (target == null)
                return false;

            directionToTarget = target.position - avatarHead.position;
            directionToTarget.y = 0f;

            if (directionToTarget.sqrMagnitude < 0.0001f)
                return false;

            directionToTarget.Normalize();
            return true;
        }

        private void ApplyTargetLockVisuals()
        {
            if (targetLockGameObject != null)
                targetLockGameObject.SetActive(targetLockEnabled);
        }

        private static void RotateAroundPivot(Transform target, Vector3 pivot, Quaternion rotation)
        {
            Vector3 offset = target.position - pivot;
            offset = rotation * offset;
            target.position = pivot + offset;
            target.rotation = rotation * target.rotation;
        }

        private static Quaternion YawOnly(Quaternion rotation)
        {
            Vector3 forward = rotation * Vector3.forward;
            forward.y = 0f;

            if (forward.sqrMagnitude < 0.0001f)
                return Quaternion.identity;

            return Quaternion.LookRotation(forward.normalized, Vector3.up);
        }
    }
}