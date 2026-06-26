using UnityEngine;
using RootMotion.FinalIK;

#if !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif


namespace RootMotion.Demos
{
    // Moving the demo VR character controller.
    public class VRController : MonoBehaviour
    {

        [System.Serializable]
        public enum InputMode
        {
            Input = 0,
            WASDOnly = 1,
        }

        public InputMode inputMode;
        public VRIK ik;
        public Transform centerEyeAnchor;

        // Match these values to velocities in the locomotion animation blend tree for better looking results (avoids half-blends)
        public float walkSpeed = 1f;
        public float runSpeed = 3f;
        public float walkForwardSpeedMlp = 1f;
        public float runForwardSpeedMlp = 1f;

        private Vector3 smoothInput, smoothInputV;

        private void Update()
        {
            // Get input
            Vector3 input = GetInput();
            input *= ik.solver.scale;

            float fDot = Vector3.Dot(input, Vector3.forward);
            bool f = fDot > 0f;

            // Locomotion speed
            float s = walkSpeed;

            #if ENABLE_LEGACY_INPUT_MANAGER
            bool shiftPressed = Input.GetKey(KeyCode.LeftShift);
            #else
            Keyboard kb = Keyboard.current;
            bool shiftPressed = kb != null && kb.shiftKey.isPressed;
            #endif
            
            if (shiftPressed)
            {
                s = runSpeed;
                if (f) s *= runForwardSpeedMlp; // Walk faster/slower when moving forward
            } else
            {
                if (f) s *= walkForwardSpeedMlp; // Run faster/slower when moving forward
            }

            // Input smoothing
            smoothInput = Vector3.SmoothDamp(smoothInput, input * s, ref smoothInputV, 0.1f);

            // Rotate input to avatar space
            Vector3 forward = centerEyeAnchor.forward;
            forward.y = 0f;
            Quaternion avatarSpace = Quaternion.LookRotation(forward);

            // Apply
            transform.position += avatarSpace * smoothInput * Time.deltaTime;
        }

        // Returns keyboard/thumbstick input vector
        private Vector3 GetInput()
        {
            switch (inputMode)
            {
                case InputMode.Input:
                    Vector3 v = GetInputVectorRaw();
                    if (v.sqrMagnitude < 0.3f) return Vector3.zero;
                    return v.normalized;
                case InputMode.WASDOnly:
#if ENABLE_LEGACY_INPUT_MANAGER
			bool wPressed = Input.GetKey(KeyCode.W);
			bool aPressed = Input.GetKey(KeyCode.A);
            bool sPressed = Input.GetKey(KeyCode.S);
            bool dPressed = Input.GetKey(KeyCode.D);
#else
			Keyboard kb = Keyboard.current;
			bool wPressed = kb != null && kb.wKey.isPressed;
			bool aPressed = kb != null && kb.aKey.isPressed;
            bool sPressed = kb != null && kb.sKey.isPressed;
			bool dPressed = kb != null && kb.dKey.isPressed;
#endif

                    Vector3 input = Vector3.zero;
                    if (wPressed) input += Vector3.forward;
                    if (sPressed) input += Vector3.back;
                    if (aPressed) input += Vector3.left;
                    if (dPressed) input += Vector3.right;
                    return input.normalized;
                default: return Vector3.zero;
            }
        }

        private Vector3 GetInputVectorRaw() {
#if ENABLE_LEGACY_INPUT_MANAGER
			return new Vector3(
				Input.GetAxisRaw("Horizontal"),
				0f,
				Input.GetAxisRaw("Vertical")
			);
#else
			return ReadMovementAxes();
#endif
		}
 
#if !ENABLE_LEGACY_INPUT_MANAGER
		// Reads WASD / arrow keys from the new Input System.
		// smooth=true mimics GetAxis (clamped float), smooth=false mimics GetAxisRaw (-1/0/1).

		private Vector3 ReadMovementAxes() {
			Keyboard kb = Keyboard.current;
			if (kb == null) return Vector3.zero;

			float h = (kb.dKey.isPressed || kb.rightArrowKey.isPressed) ? 1f :
				          (kb.aKey.isPressed || kb.leftArrowKey.isPressed)  ? -1f : 0f;
			float v = (kb.wKey.isPressed || kb.upArrowKey.isPressed)    ? 1f :
				          (kb.sKey.isPressed || kb.downArrowKey.isPressed)  ? -1f : 0f;

			return new Vector3(h, 0f, v);
		}
#endif

    }
}
