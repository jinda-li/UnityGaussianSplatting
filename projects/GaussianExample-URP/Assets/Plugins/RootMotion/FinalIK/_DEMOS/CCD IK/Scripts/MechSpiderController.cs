using UnityEngine;
using System.Collections;

namespace RootMotion.Demos {

	/// <summary>
	/// Controller for the Mech spider.
	/// </summary>
	public class MechSpiderController: MonoBehaviour {

		public MechSpider mechSpider; // The mech spider
		public Transform cameraTransform; // The camera
		public float speed = 6f; // Horizontal speed of the spider
		public float turnSpeed = 30f; // The speed of turning the spider to align with the camera

		public Vector3 inputVector {
			get {
				#if ENABLE_LEGACY_INPUT_MANAGER
				return new Vector3(Input.GetAxis("Horizontal"), 0, Input.GetAxis("Vertical"));
				#else
				Vector2 move = UnityEngine.InputSystem.InputSystem.actions["Move"].ReadValue<Vector2>();
				return new Vector3(move.x, 0f, move.y);
				#endif
			}
		}

		void Update() {
			// Read the input
			Vector3 cameraForward = cameraTransform.forward;
			Vector3 camNormal = transform.up;
			Vector3.OrthoNormalize(ref camNormal, ref cameraForward);

			// Moving the spider
			Quaternion cameraLookRotation = Quaternion.LookRotation(cameraForward, transform.up);
			transform.Translate(cameraLookRotation * inputVector.normalized * Time.deltaTime * speed * mechSpider.scale, Space.World);
			
			// Rotating the spider to camera forward
			transform.rotation = Quaternion.RotateTowards(transform.rotation, cameraLookRotation, Time.deltaTime * turnSpeed);
		}
	}

}