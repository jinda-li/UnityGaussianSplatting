using UnityEngine;
using System.Collections;

#if !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

namespace RootMotion.Demos {

	/// <summary>
	/// Demo character controller for the Full Body FPS scene.
	/// </summary>
	public class FPSCharacter: MonoBehaviour {

		[Range(0f, 1f)] public float walkSpeed = 0.5f;

		private float sVel;
		private Animator animator;
		private FPSAiming FPSAiming;

		void Start() {
			animator = GetComponent<Animator>();
			FPSAiming = GetComponent<FPSAiming>();
		}

		void Update() {
			// Aiming down the sight of the gun when RMB is down
#if ENABLE_LEGACY_INPUT_MANAGER
			bool aimDown = Input.GetMouseButton(1);
#else
			Mouse mouse = Mouse.current;
			bool aimDown = mouse != null && mouse.rightButton.isPressed;
#endif
			FPSAiming.sightWeight = Mathf.SmoothDamp(FPSAiming.sightWeight, (aimDown ? 1f : 0f), ref sVel, 0.1f);
 
			// Set to full values to optimize IK
			if (FPSAiming.sightWeight < 0.001f) FPSAiming.sightWeight = 0f;
			if (FPSAiming.sightWeight > 0.999f) FPSAiming.sightWeight = 1f;
 
			animator.SetFloat("Speed", walkSpeed);
		}

		void OnGUI() {
			GUI.Label(new Rect(Screen.width - 210, 10, 200, 25), "Hold RMB to aim down the sight");
		}

	}
}
