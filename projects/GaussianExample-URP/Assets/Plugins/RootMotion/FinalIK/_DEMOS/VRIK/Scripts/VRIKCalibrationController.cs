using UnityEngine;
using System.Collections;
using RootMotion.FinalIK;

#if !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif


namespace RootMotion.Demos
{

    public class VRIKCalibrationController : MonoBehaviour
    {

        [Tooltip("Reference to the VRIK component on the avatar.")] public VRIK ik;
        [Tooltip("The settings for VRIK calibration.")] public VRIKCalibrator.Settings settings;
        [Tooltip("The HMD.")] public Transform headTracker;
        [Tooltip("(Optional) A tracker placed anywhere on the body of the player, preferrably close to the pelvis, on the belt area.")] public Transform bodyTracker;
        [Tooltip("(Optional) A tracker or hand controller device placed anywhere on or in the player's left hand.")] public Transform leftHandTracker;
        [Tooltip("(Optional) A tracker or hand controller device placed anywhere on or in the player's right hand.")] public Transform rightHandTracker;
        [Tooltip("(Optional) A tracker placed anywhere on the ankle or toes of the player's left leg.")] public Transform leftFootTracker;
        [Tooltip("(Optional) A tracker placed anywhere on the ankle or toes of the player's right leg.")] public Transform rightFootTracker;

        [Header("Data stored by Calibration")]
        public VRIKCalibrator.CalibrationData data = new VRIKCalibrator.CalibrationData();

        void LateUpdate()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
			bool cPressed = Input.GetKeyDown(KeyCode.C);
			bool dPressed = Input.GetKeyDown(KeyCode.D);
            bool sPressed = Input.GetKeyDown(KeyCode.S);
#else
			Keyboard kb = Keyboard.current;
			bool cPressed = kb != null && kb.cKey.wasPressedThisFrame;
			bool dPressed = kb != null && kb.dKey.wasPressedThisFrame;
            bool sPressed = kb != null && kb.sKey.wasPressedThisFrame;
#endif

            if (cPressed)
            {
                // Calibrate the character, store data of the calibration
                data = VRIKCalibrator.Calibrate(ik, settings, headTracker, bodyTracker, leftHandTracker, rightHandTracker, leftFootTracker, rightFootTracker);
            }

            /*
             * calling Calibrate with settings will return a VRIKCalibrator.CalibrationData, which can be used to calibrate that same character again exactly the same in another scene (just pass data instead of settings), 
             * without being dependent on the pose of the player at calibration time.
             * Calibration data still depends on bone orientations though, so the data is valid only for the character that it was calibrated to or characters with identical bone structures.
             * If you wish to use more than one character, it would be best to calibrate them all at once and store the CalibrationData for each one.
             * */
            if (dPressed)
            {
                if (data.scale == 0f)
                {
                    Debug.LogError("No Calibration Data to calibrate to, please calibrate with settings first.");
                }
                else
                {
                    // Use data from a previous calibration to calibrate that same character again.
                    VRIKCalibrator.Calibrate(ik, data, headTracker, bodyTracker, leftHandTracker, rightHandTracker, leftFootTracker, rightFootTracker);
                }
            }

            // Recalibrates avatar scale only. Can be called only if the avatar has been calibrated already.
            if (sPressed)
            {
                if (data.scale == 0f)
                {
                    Debug.LogError("Avatar needs to be calibrated before RecalibrateScale is called.");
                    return;
                }
                VRIKCalibrator.RecalibrateScale(ik, data, settings);
            }
        }
    }
}
