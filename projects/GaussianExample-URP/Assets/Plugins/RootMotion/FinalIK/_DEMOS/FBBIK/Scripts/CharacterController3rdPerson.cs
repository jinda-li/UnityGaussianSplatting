using UnityEngine;
using System.Collections;

#if !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

namespace RootMotion.Demos {

	/// <summary>
	/// Basic Mecanim character controller for 3rd person view.
	/// </summary>
	public class CharacterController3rdPerson: MonoBehaviour {

		public CameraController cam; // The camera

		private AnimatorController3rdPerson animatorController; // The Animator controller

		void Start() {
			animatorController = GetComponent<AnimatorController3rdPerson>();

			cam.enabled = false;
		}

		void LateUpdate() {
			// Update the camera first so we always have its final translation in the frame
			cam.UpdateInput();
			cam.UpdateTransform();

			// Read the input
			Vector3 input = GetInputVector();

			// Should the character be moving? 
			// inputVectorRaw is required here for not starting a transition to idle on that one frame where inputVector is Vector3.zero when reversing directions.
			bool isMoving = input != Vector3.zero || GetInputVectorRaw() != Vector3.zero;

			// Character look at vector.
			Vector3 lookDirection = cam.transform.forward;

			// Aiming target
			Vector3 aimTarget = cam.transform.position + (lookDirection * 10f);

			// Move the character.
			animatorController.Move(input, isMoving, lookDirection, aimTarget);
		}

		// Reads the Input to get the movement direction.
		private Vector3 GetInputVector() {
#if ENABLE_LEGACY_INPUT_MANAGER
			Vector3 d = new Vector3(
				Input.GetAxis("Horizontal"),
				0f,
				Input.GetAxis("Vertical")
			);
#else
			Vector3 d = ReadMovementAxes(smooth: true);
#endif
			d.z += Mathf.Abs(d.x) * 0.05f;
			d.x -= Mathf.Abs(d.z) * 0.05f;
			return d;
		}
 
		private Vector3 GetInputVectorRaw() {
#if ENABLE_LEGACY_INPUT_MANAGER
			return new Vector3(
				Input.GetAxisRaw("Horizontal"),
				0f,
				Input.GetAxisRaw("Vertical")
			);
#else
			return ReadMovementAxes(smooth: false);
#endif
		}
 
#if !ENABLE_LEGACY_INPUT_MANAGER
		// Reads WASD / arrow keys from the new Input System.
		// smooth=true mimics GetAxis (clamped float), smooth=false mimics GetAxisRaw (-1/0/1).
		private Vector3 smoothInput, smoothInputV;

		private Vector3 ReadMovementAxes(bool smooth) {
			Keyboard kb = Keyboard.current;
			if (kb == null) return Vector3.zero;

			float h = (kb.dKey.isPressed || kb.rightArrowKey.isPressed) ? 1f :
				          (kb.aKey.isPressed || kb.leftArrowKey.isPressed)  ? -1f : 0f;
			float v = (kb.wKey.isPressed || kb.upArrowKey.isPressed)    ? 1f :
				          (kb.sKey.isPressed || kb.downArrowKey.isPressed)  ? -1f : 0f;

			Vector3 raw = new Vector3(h, 0f, v);
			if (!smooth) return raw;


			//smoothInput = Vector3.MoveTowards(smoothInput, raw, Time.deltaTime);
			smoothInput = Vector3.SmoothDamp(smoothInput, raw, ref smoothInputV, 0.2f);
			return smoothInput;
		}
#endif
	}
}