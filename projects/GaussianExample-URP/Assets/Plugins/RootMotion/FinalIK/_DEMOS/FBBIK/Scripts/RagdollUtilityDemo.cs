using UnityEngine;
using System.Collections;
using RootMotion.FinalIK;

#if !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

namespace RootMotion.Demos {

	// Demonstrating the use of RagdollUtility.cs.
	public class RagdollUtilityDemo : MonoBehaviour {

		public RagdollUtility ragdollUtility;
		public Transform root;
		public Rigidbody pelvis;

		void OnGUI() {
			GUILayout.Label(" Press R to switch to ragdoll. " +
			                "\n Weigh in one of the FBBIK effectors to make kinematic changes to the ragdoll pose." +
			                "\n A to blend back to animation");
		}

		void Update() {
#if ENABLE_LEGACY_INPUT_MANAGER
			bool rPressed = Input.GetKeyDown(KeyCode.R);
			bool aPressed = Input.GetKeyDown(KeyCode.A);
#else
			Keyboard kb = Keyboard.current;
			bool rPressed = kb != null && kb.rKey.wasPressedThisFrame;
			bool aPressed = kb != null && kb.aKey.wasPressedThisFrame;
#endif

			if (rPressed) ragdollUtility.EnableRagdoll();
			if (aPressed) {
				// Move the root of the character to where the pelvis is without moving the ragdoll
				Vector3 toPelvis = pelvis.position - root.position;
				root.position += toPelvis;
				pelvis.transform.position -= toPelvis;

				ragdollUtility.DisableRagdoll();
			}
		}

	}
}
