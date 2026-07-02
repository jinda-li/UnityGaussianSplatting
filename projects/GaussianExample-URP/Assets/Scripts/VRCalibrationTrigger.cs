using RootMotion.Demos;
using UnityEngine;

public class VRCalibrationTrigger : MonoBehaviour
{
    [SerializeField] private VRIKCalibrationBasic calibration;
    [SerializeField] private KeyCode calibrateKey = KeyCode.C;

    private void Update()
    {
        if (Input.GetKeyDown(calibrateKey))
            ApplyCalibration();
    }

    [ContextMenu("Apply Calibration")]
    public void ApplyCalibration()
    {
        if (calibration == null || calibration.ik == null || calibration.data.scale == 0f)
        {
            Debug.LogError(
                $"{nameof(VRCalibrationTrigger)}: missing calibration reference or stored data. Calibrate with C in {nameof(VRIKCalibrationBasic)} first.",
                this);
            return;
        }

        // Only apply stored avatar scale. Do NOT call VRIKCalibrator.Calibrate(ik, data, ...)
        // here — that API reparents ik.solver.*.target under the XR anchors, which would
        // override the static PlayerBody IKRigRoot targets set up for IKRetarget.
        calibration.ik.references.root.localScale = Vector3.one * calibration.data.scale;
        calibration.ik.solver.FixTransforms();
    }
}
