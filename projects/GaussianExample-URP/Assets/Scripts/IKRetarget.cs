using UnityEngine;

namespace VRPlayer
{
    public class IKRetarget : MonoBehaviour
    {
        [Header("Tracked Sources (XR Origin)")]
        [SerializeField] private Transform headSource;
        [SerializeField] private Transform leftHandSource;
        [SerializeField] private Transform rightHandSource;

        [Header("IK Rig Destinations (PlayerBody IKRigRoot)")]
        [SerializeField] private Transform headDestination;
        [SerializeField] private Transform leftHandDestination;
        [SerializeField] private Transform rightHandDestination;

        private void LateUpdate()
        {
            Copy(headSource, headDestination);
            Copy(leftHandSource, leftHandDestination);
            Copy(rightHandSource, rightHandDestination);
        }

        private static void Copy(Transform source, Transform destination)
        {
            if (source == null || destination == null)
                return;

            destination.SetPositionAndRotation(source.position, source.rotation);
        }
    }
}
