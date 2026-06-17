using UnityEngine;

namespace VRPlayer
{
    /// <summary>
    /// Runtime per-controller activity state for VR.
    ///
    /// A controller is considered more active when:
    /// - it is raised closer to HMD height
    /// - it is far enough forward of the HMD
    /// - or it is behind the HMD
    /// - or optional external input is active
    ///
    /// This script does not drive IK or animation directly.
    /// Other systems can read LeftWeight / RightWeight.
    /// </summary>
    public sealed class VRControllerActivityState : MonoBehaviour
    {
        [Header("Tracked Transforms")]
        [SerializeField] private Transform hmd;
        [SerializeField] private Transform leftController;
        [SerializeField] private Transform rightController;

        [Header("Height Check (relative to HMD)")]
        [Tooltip("Controller becomes fully height-active when it is this far below the HMD or less.")]
        [SerializeField] private float activeHeightBelowHmd = 0.30f;

        [Tooltip("How far below the active threshold the height score starts blending in.")]
        [SerializeField] private float heightBlendRange = 0.20f;

        [Header("Forward Check (relative to HMD)")]
        [Tooltip("Controller becomes forward-active when it moves at least this far forward of the HMD.")]
        [SerializeField] private float activeForwardFromHmd = 0.18f;

        [Tooltip("If controller is behind the HMD, count it as fully active.")]
        [SerializeField] private bool behindHmdIsActive = true;

        [Header("External Input")]
        [SerializeField] private bool useInputSignal = true;

        [Range(0f, 1f)]
        [SerializeField] private float leftExternalInputSignal = 0f;

        [Range(0f, 1f)]
        [SerializeField] private float rightExternalInputSignal = 0f;

        [Header("Feature Weights")]
        [SerializeField] private float heightContribution = 1f;
        [SerializeField] private float forwardContribution = 1f;
        [SerializeField] private float inputContribution = 1f;

        [Header("Smoothing")]
        [SerializeField] private float riseSpeed = 10f;
        [SerializeField] private float fallSpeed = 3f;
        [SerializeField] private float zeroSnapThreshold = 0.02f;
        [SerializeField] private float oneSnapThreshold = 0.98f;

        [Header("Debug Logging")]
        [SerializeField] private bool enableDebugLogging = true;
        [SerializeField] private float debugLogInterval = 0.25f;

        public float LeftWeight => _leftWeight;
        public float RightWeight => _rightWeight;

        public float LeftRawScore => _leftRawScore;
        public float RightRawScore => _rightRawScore;

        public float CombinedWeight => Mathf.Max(_leftWeight, _rightWeight);

        private float _leftWeight;
        private float _rightWeight;

        private float _leftRawScore;
        private float _rightRawScore;

        private float _debugLogTimer;

        private ControllerDebugInfo _leftDebugInfo;
        private ControllerDebugInfo _rightDebugInfo;

        private void Reset()
        {
            AutoAssignFromChildren();
        }

        private void Awake()
        {
            if (hmd == null || leftController == null || rightController == null)
            {
                AutoAssignFromChildren();
            }
        }

        private void Update()
        {
            if (hmd == null || leftController == null || rightController == null)
            {
                _leftRawScore = 0f;
                _rightRawScore = 0f;
                _leftWeight = 0f;
                _rightWeight = 0f;
                return;
            }

            _leftRawScore = ComputeControllerScore(leftController, useInputSignal ? leftExternalInputSignal : 0f, ref _leftDebugInfo);
            _rightRawScore = ComputeControllerScore(rightController, useInputSignal ? rightExternalInputSignal : 0f, ref _rightDebugInfo);

            _leftWeight = SmoothToward(_leftWeight, _leftRawScore, Time.deltaTime);
            _rightWeight = SmoothToward(_rightWeight, _rightRawScore, Time.deltaTime);

            LogDebugInfo();
        }

        public void SetExternalInputSignals(float leftSignal01, float rightSignal01)
        {
            leftExternalInputSignal = Mathf.Clamp01(leftSignal01);
            rightExternalInputSignal = Mathf.Clamp01(rightSignal01);
        }

        private float ComputeControllerScore(Transform controller, float inputSignal, ref ControllerDebugInfo debugInfo)
        {
            Vector3 hmdToController = controller.position - hmd.position;

            float verticalOffset = hmdToController.y;
            float belowHmd = -verticalOffset;

            Vector3 hmdForwardFlat = Vector3.ProjectOnPlane(hmd.forward, Vector3.up);
            if (hmdForwardFlat.sqrMagnitude < 0.0001f)
            {
                hmdForwardFlat = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            }
            hmdForwardFlat.Normalize();

            float forwardFromHmd = Vector3.Dot(hmdToController, hmdForwardFlat);
            bool isBehindHmd = forwardFromHmd < 0f;

            float heightScore = ComputeHeightScore(belowHmd);
            float forwardScore = ComputeForwardScore(forwardFromHmd, isBehindHmd);
            float externalScore = inputSignal;

            float weightedHeight = heightScore * heightContribution;
            float weightedForward = forwardScore * forwardContribution;
            float weightedInput = externalScore * inputContribution;

            float finalScore = Mathf.Clamp01(Mathf.Max(weightedHeight, weightedForward, weightedInput));

            debugInfo = new ControllerDebugInfo
            {
                belowHmd = belowHmd,
                activeHeightBelowHmd = activeHeightBelowHmd,
                heightScore = heightScore,
                weightedHeightScore = weightedHeight,

                forwardFromHmd = forwardFromHmd,
                activeForwardFromHmd = activeForwardFromHmd,
                isBehindHmd = isBehindHmd,
                forwardScore = forwardScore,
                weightedForwardScore = weightedForward,

                inputSignal = inputSignal,
                weightedInputScore = weightedInput,

                finalRawScore = finalScore
            };

            return finalScore;
        }

        private float ComputeHeightScore(float belowHmd)
        {
            float start = activeHeightBelowHmd + heightBlendRange;
            float end = activeHeightBelowHmd;

            if (Mathf.Approximately(start, end))
                return belowHmd <= end ? 1f : 0f;

            return Mathf.Clamp01(Mathf.InverseLerp(start, end, belowHmd));
        }

        private float ComputeForwardScore(float forwardFromHmd, bool isBehindHmd)
        {
            if (behindHmdIsActive && isBehindHmd)
                return 1f;

            return forwardFromHmd >= activeForwardFromHmd ? 1f : 0f;
        }

        private float SmoothToward(float current, float target, float deltaTime)
        {
            float speed = target > current ? riseSpeed : fallSpeed;
            float result = Mathf.MoveTowards(current, target, speed * deltaTime);

            if (result < zeroSnapThreshold)
                result = 0f;
            else if (result > oneSnapThreshold)
                result = 1f;

            return result;
        }

        private void LogDebugInfo()
        {
            if (!enableDebugLogging)
                return;

            _debugLogTimer -= Time.deltaTime;
            if (_debugLogTimer > 0f)
                return;

            _debugLogTimer = Mathf.Max(0.01f, debugLogInterval);

            Debug.Log(
                "[VRControllerActivityState] " +
                $"L weight={_leftWeight:F2}, raw={_leftRawScore:F2}, " +
                $"height: belowHmd={_leftDebugInfo.belowHmd:F2} vs threshold={_leftDebugInfo.activeHeightBelowHmd:F2}, score={_leftDebugInfo.heightScore:F2}, " +
                $"forward: value={_leftDebugInfo.forwardFromHmd:F2} vs threshold={_leftDebugInfo.activeForwardFromHmd:F2}, behind={_leftDebugInfo.isBehindHmd}, score={_leftDebugInfo.forwardScore:F2}, " +
                $"input={_leftDebugInfo.inputSignal:F2} | " +

                $"R weight={_rightWeight:F2}, raw={_rightRawScore:F2}, " +
                $"height: belowHmd={_rightDebugInfo.belowHmd:F2} vs threshold={_rightDebugInfo.activeHeightBelowHmd:F2}, score={_rightDebugInfo.heightScore:F2}, " +
                $"forward: value={_rightDebugInfo.forwardFromHmd:F2} vs threshold={_rightDebugInfo.activeForwardFromHmd:F2}, behind={_rightDebugInfo.isBehindHmd}, score={_rightDebugInfo.forwardScore:F2}, " +
                $"input={_rightDebugInfo.inputSignal:F2}"
            );
        }

        private void AutoAssignFromChildren()
        {
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
            {
                string n = t.name.ToLowerInvariant();

                if (hmd == null && (n.Contains("head") || n.Contains("hmd") || n.Contains("camera")))
                    hmd = t;

                if (leftController == null && (n.Contains("left") && (n.Contains("hand") || n.Contains("controller"))))
                    leftController = t;

                if (rightController == null && (n.Contains("right") && (n.Contains("hand") || n.Contains("controller"))))
                    rightController = t;
            }
        }

        private struct ControllerDebugInfo
        {
            public float belowHmd;
            public float activeHeightBelowHmd;
            public float heightScore;
            public float weightedHeightScore;

            public float forwardFromHmd;
            public float activeForwardFromHmd;
            public bool isBehindHmd;
            public float forwardScore;
            public float weightedForwardScore;

            public float inputSignal;
            public float weightedInputScore;

            public float finalRawScore;
        }
    }
}