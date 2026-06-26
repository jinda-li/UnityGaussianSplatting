using UnityEngine;
using System.Collections;

#if !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

namespace RootMotion.Demos {

	/// <summary>
	/// User input for a third person character controller.
	/// </summary>
	public class UserControlThirdPerson : MonoBehaviour {

		// Input state
		public struct State {
			public Vector3 move;
			public Vector3 lookPos;
			public bool crouch;
			public bool jump;
			public int actionIndex;
		}

		public bool walkByDefault;        // toggle for walking state
		public bool canCrouch = true;
		public bool canJump = true;

		public State state = new State();			// The current state of the user input

		protected Transform cam;                    // A reference to the main camera in the scenes transform

		protected virtual void Start () {
			// get the transform of the main camera
			cam = Camera.main.transform;
		}

		protected virtual void Update () {
#if ENABLE_LEGACY_INPUT_MANAGER
			state.crouch = canCrouch && Input.GetKey(KeyCode.C);
			state.jump   = canJump   && Input.GetButton("Jump");
 
			float h = Input.GetAxisRaw("Horizontal");
			float v = Input.GetAxisRaw("Vertical");
 
			bool walkToggle = Input.GetKey(KeyCode.LeftShift);
#else
			Keyboard kb = Keyboard.current;
 
			state.crouch = canCrouch && kb != null && kb.cKey.isPressed;
			state.jump   = canJump   && kb != null && kb.spaceKey.isPressed;
 
			float h = 0f, v = 0f;
			if (kb != null) {
				h = (kb.dKey.isPressed || kb.rightArrowKey.isPressed ? 1f : 0f)
				  - (kb.aKey.isPressed || kb.leftArrowKey.isPressed  ? 1f : 0f);
				v = (kb.wKey.isPressed || kb.upArrowKey.isPressed    ? 1f : 0f)
				  - (kb.sKey.isPressed || kb.downArrowKey.isPressed  ? 1f : 0f);
			}
 
			bool walkToggle = kb != null && kb.leftShiftKey.isPressed;
#endif
 
			// calculate move direction
			Vector3 move = cam.rotation * new Vector3(h, 0f, v).normalized;
 
			// Flatten move vector to the character.up plane
			if (move != Vector3.zero) {
				Vector3 normal = transform.up;
				Vector3.OrthoNormalize(ref normal, ref move);
				state.move = move;
			} else state.move = Vector3.zero;
 
			// We select appropriate speed based on whether we're walking by default, and whether the walk/run toggle button is pressed:
			float walkMultiplier = walkByDefault ? (walkToggle ? 1f : 0.5f) : (walkToggle ? 0.5f : 1f);
 
			state.move *= walkMultiplier;
 
			// calculate the head look target position
			state.lookPos = transform.position + cam.forward * 100f;
		}
	}
}

