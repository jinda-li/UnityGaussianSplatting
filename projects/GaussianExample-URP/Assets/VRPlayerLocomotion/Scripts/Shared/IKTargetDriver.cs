using UnityEngine;

namespace VRPlayer
{
    public class IKTargetDriver : MonoBehaviour
    {
        [Header("Tracked Rig")]
        [SerializeField] private Transform xrOrigin;
        [SerializeField] private Transform trackedHmd;
        [SerializeField] private Transform trackedLeftHand;
        [SerializeField] private Transform trackedRightHand;

        [Header("IK Targets Rig")]
        [SerializeField] private Transform ikTargetsRoot;
        [SerializeField] private Transform ikHeadTarget;
        [SerializeField] private Transform ikLeftHandTarget;
        [SerializeField] private Transform ikRightHandTarget;

        [Header("Avatar References")]
        [SerializeField] private Transform avatarHead;

        [Header("Settings")]
        [SerializeField] private bool updateInLateUpdate = true;
        [SerializeField] private bool copyHandRotations = true;
        [SerializeField] private bool copyHeadRotation = true;

        private void Update()
        {
            if (!updateInLateUpdate)
                UpdateTargets();
        }

        private void LateUpdate()
        {
            if (updateInLateUpdate)
                UpdateTargets();
        }

        public void UpdateTargets()
        {
            if (!HasRequiredReferences())
                return;

            ApplySnappedRootPose();
            CopyTrackedPoseToTargetsInXROriginSpace();
        }

        private bool HasRequiredReferences()
        {
            return xrOrigin != null &&
                   trackedHmd != null &&
                   trackedLeftHand != null &&
                   trackedRightHand != null &&
                   ikTargetsRoot != null &&
                   ikHeadTarget != null &&
                   ikLeftHandTarget != null &&
                   ikRightHandTarget != null &&
                   avatarHead != null;
        }

        private void ApplySnappedRootPose()
        {
            Quaternion hmdYaw = YawOnly(trackedHmd.rotation);
            Quaternion avatarYaw = YawOnly(avatarHead.rotation);
            Quaternion yawDelta = avatarYaw * Quaternion.Inverse(hmdYaw);

            Quaternion targetRootRotation = yawDelta * xrOrigin.rotation;

            Vector3 rootOffsetFromHmd = xrOrigin.position - trackedHmd.position;
            Vector3 rotatedRootOffset = yawDelta * rootOffsetFromHmd;
            Vector3 targetRootPosition = avatarHead.position + rotatedRootOffset;

            ikTargetsRoot.SetPositionAndRotation(targetRootPosition, targetRootRotation);
        }

        private void CopyTrackedPoseToTargetsInXROriginSpace()
        {
            CopyTrackedTransformToTarget(trackedHmd, ikHeadTarget, copyHeadRotation);
            CopyTrackedTransformToTarget(trackedLeftHand, ikLeftHandTarget, copyHandRotations);
            CopyTrackedTransformToTarget(trackedRightHand, ikRightHandTarget, copyHandRotations);
        }

        private void CopyTrackedTransformToTarget(Transform tracked, Transform target, bool copyRotation)
        {
            target.localPosition = xrOrigin.InverseTransformPoint(tracked.position);

            if (copyRotation)
            {
                target.localRotation = Quaternion.Inverse(xrOrigin.rotation) * tracked.rotation;
            }
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