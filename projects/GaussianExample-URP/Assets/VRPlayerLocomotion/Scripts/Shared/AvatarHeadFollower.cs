using UnityEngine;

namespace VRPlayer
{
    public class AvatarHeadFollower : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Transform hmd;
        [SerializeField] private Transform eyeAnchor;
        [SerializeField] private Transform avatarRoot;

        [Header("State")]
        [SerializeField] private bool followEnabled = true;

        public void SetFollowEnabled(bool enabled)
        {
            followEnabled = enabled;
        }

        public void EnableFollow()
        {
            followEnabled = true;
        }

        public void DisableFollow()
        {
            followEnabled = false;
        }

        private void LateUpdate()
        {
            if (!followEnabled || hmd == null || eyeAnchor == null || avatarRoot == null)
                return;

            Quaternion targetRootRotation = Quaternion.Euler(0f, hmd.eulerAngles.y, 0f);
            avatarRoot.rotation = targetRootRotation;

            Vector3 eyeWorldOffset = eyeAnchor.position - avatarRoot.position;
            Vector3 targetRootPosition = hmd.position - eyeWorldOffset;

            avatarRoot.position = new Vector3(
                targetRootPosition.x,
                avatarRoot.position.y,
                targetRootPosition.z
            );
        }
    }
}