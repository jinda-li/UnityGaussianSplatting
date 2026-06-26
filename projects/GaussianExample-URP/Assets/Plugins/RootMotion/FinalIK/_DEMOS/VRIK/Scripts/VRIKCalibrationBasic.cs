using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using RootMotion.FinalIK;

#if !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif


namespace RootMotion.Demos
{

    public class VRIKCalibrationBasic : MonoBehaviour
    {

        [Tooltip("The VRIK component.")] public VRIK ik;

        [Header("Head")]
        [Tooltip("HMD.")] public Transform centerEyeAnchor;
        [Tooltip("Position offset of the camera from the head bone (root space).")] public Vector3 headAnchorPositionOffset;
        [Tooltip("Rotation offset of the camera from the head bone (root space).")] public Vector3 headAnchorRotationOffset;

        [Header("Hands")]
        [Tooltip("Left Hand Controller")] public Transform leftHandAnchor;
        [Tooltip("Right Hand Controller")] public Transform rightHandAnchor;
        [Tooltip("Position offset of the hand controller from the hand bone (controller space).")] public Vector3 handAnchorPositionOffset;
        [Tooltip("Rotation offset of the hand controller from the hand bone (controller space).")] public Vector3 handAnchorRotationOffset;

        [Header("Scale")]
        [Tooltip("Multiplies the scale of the root.")] public float scaleMlp = 1f;

        [Header("Data stored by Calibration")]
        public VRIKCalibrator.CalibrationData data = new VRIKCalibrator.CalibrationData();

        private void LateUpdate()
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
                data = VRIKCalibrator.Calibrate(ik, centerEyeAnchor, leftHandAnchor, rightHandAnchor, headAnchorPositionOffset, headAnchorRotationOffset, handAnchorPositionOffset, handAnchorRotationOffset, scaleMlp);
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
                    Debug.LogError("No Calibration Data to calibrate to, please calibrate with 'C' first.");
                }
                else
                {
                    VRIKCalibrator.Calibrate(ik, data, centerEyeAnchor, null, leftHandAnchor, rightHandAnchor);
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
                VRIKCalibrator.RecalibrateScale(ik, data, scaleMlp);
            }
        }
    }
}
