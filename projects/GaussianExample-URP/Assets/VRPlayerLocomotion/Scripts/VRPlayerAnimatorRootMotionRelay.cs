using UnityEngine;

namespace VRPlayer
{
    public class VRPlayerAnimatorRootMotionRelay : MonoBehaviour
    {
        [SerializeField] private VRPlayerRootMotionController rootMotionController;
        [SerializeField] private Animator animator;

        private void Awake()
        {
            if (animator == null)
                animator = GetComponent<Animator>();
        }

        private void OnAnimatorMove()
        {
            if (animator == null || rootMotionController == null)
                return;

            rootMotionController.CaptureRootMotion(
                animator.deltaPosition,
                animator.deltaRotation
            );
        }
    }
}
