using UnityEngine;
using System.Collections;
using RootMotion.FinalIK;

#if !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

namespace RootMotion.Demos {

	/// <summary>
	/// Triggering Hit Reactions on mouse button.
	/// </summary>
	public class HitReactionTrigger: MonoBehaviour {

        public HitReaction hitReaction;
        public float hitForce = 1f;

        private string colliderName;

		void Update() {
#if ENABLE_LEGACY_INPUT_MANAGER
			bool clicked = Input.GetMouseButtonDown(0);
			Vector2 mousePos = Input.mousePosition;
#else
			Mouse mouse = Mouse.current;
			bool clicked = mouse != null && mouse.leftButton.wasPressedThisFrame;
			Vector2 mousePos = mouse != null ? mouse.position.ReadValue() : Vector2.zero;
#endif
 
			// On left mouse button...
			if (clicked) {
				Ray ray = Camera.main.ScreenPointToRay(mousePos);
 
				// Raycast to find a ragdoll collider
				RaycastHit hit = new RaycastHit();
				if (Physics.Raycast(ray, out hit, 100f)) {
 
					// Use the HitReaction
					hitReaction.Hit(hit.collider, ray.direction * hitForce, hit.point);
 
					// Just for GUI
					colliderName = hit.collider.name;
				}
			}
		}

		void OnGUI() {
			GUILayout.Label("LMB to shoot the Dummy, RMB to rotate the camera.");
			if (colliderName != string.Empty) GUILayout.Label("Last Bone Hit: " + colliderName);
		}
	}
}
