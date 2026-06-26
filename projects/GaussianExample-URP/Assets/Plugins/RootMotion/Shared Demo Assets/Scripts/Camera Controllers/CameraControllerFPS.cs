using UnityEngine;
using System.Collections;

#if !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

namespace RootMotion {

	/// <summary>
	/// The very simple FPS camera.
	/// </summary>
	public class CameraControllerFPS: MonoBehaviour {

		public float rotationSensitivity = 3f;
		public float yMinLimit = -89f;
		public float yMaxLimit = 89f;

		private float x, y;

		void Awake () {
			Vector3 angles = transform.eulerAngles;
			x = angles.y;
			y = angles.x;
		}

		public void LateUpdate() {
			Cursor.lockState = CursorLockMode.Locked;
 
#if ENABLE_LEGACY_INPUT_MANAGER
			x += Input.GetAxis("Mouse X") * rotationSensitivity;
			y = ClampAngle(y - Input.GetAxis("Mouse Y") * rotationSensitivity, yMinLimit, yMaxLimit);
#else
			Mouse mouse = Mouse.current;
			if (mouse != null) {
				Vector2 delta = mouse.delta.ReadValue();
				x += delta.x * rotationSensitivity * 0.1f;
				y = ClampAngle(y - delta.y * rotationSensitivity * 0.1f, yMinLimit, yMaxLimit);
			}
#endif
 
			// Rotation
			transform.rotation = Quaternion.AngleAxis(x, Vector3.up) * Quaternion.AngleAxis(y, Vector3.right);
		}

		// Clamping Euler angles
		private float ClampAngle (float angle, float min, float max) {
			if (angle < -360) angle += 360;
			if (angle > 360) angle -= 360;
			return Mathf.Clamp (angle, min, max);
		}

	}
}
