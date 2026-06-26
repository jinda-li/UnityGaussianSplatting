using UnityEngine;
using System.Collections;
using RootMotion.FinalIK;

#if !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

namespace RootMotion.Demos {

	/// <summary>
	/// Just for testing out the Recoil script.
	/// </summary>
	public class RecoilTest : MonoBehaviour {

		public float magnitude = 1f;

		private Recoil recoil;

		void Start() {
			recoil = GetComponent<Recoil>();
		}

		void Update()
		{
#if ENABLE_LEGACY_INPUT_MANAGER
    bool fired = Input.GetKeyDown(KeyCode.R) || Input.GetMouseButtonDown(0);
#else
			Keyboard kb = Keyboard.current;
			Mouse mouse = Mouse.current;
			bool fired = (kb != null && kb.rKey.wasPressedThisFrame) || (mouse != null && mouse.leftButton.wasPressedThisFrame);
#endif
			if (fired) recoil.Fire(magnitude);
		}
		
		void OnGUI() {
			GUILayout.Label("Press R or LMB for procedural recoil.");
		}

	}
}
