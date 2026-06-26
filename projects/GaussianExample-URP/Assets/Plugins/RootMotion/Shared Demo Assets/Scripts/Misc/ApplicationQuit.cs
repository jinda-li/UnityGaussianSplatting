using UnityEngine;
using System.Collections;

#if !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

namespace RootMotion.Demos {

	// Safely getting out of full screen desktop builds
	public class ApplicationQuit : MonoBehaviour {

		void Update () {
#if ENABLE_LEGACY_INPUT_MANAGER
			bool input = Input.GetKeyDown(KeyCode.Q) || Input.GetKeyDown(KeyCode.Escape);
#else
			Keyboard kb = Keyboard.current;
			bool input = kb != null && (kb.qKey.wasPressedThisFrame || kb.escapeKey.wasPressedThisFrame);
#endif

			if (input) Application.Quit();
		}
	}
}
