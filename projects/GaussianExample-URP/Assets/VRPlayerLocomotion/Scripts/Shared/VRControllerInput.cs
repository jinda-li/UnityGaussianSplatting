using UnityEngine;
using UnityEngine.InputSystem;

namespace VRPlayer
{
    public class VRControllerInput : MonoBehaviour
    {
        [Header("Input Actions")]
        [SerializeField] private InputActionProperty moveAction;               // Left stick Vector2
        [SerializeField] private InputActionProperty rightStickAction;         // Right stick Vector2
        [SerializeField] private InputActionProperty aButtonAction;            // Button
        [SerializeField] private InputActionProperty bButtonAction;            // Button
        [SerializeField] private InputActionProperty leftMenuAction;           // Button
        [SerializeField] private InputActionProperty targetLockToggleAction;   // Right joystick down / stick click / assigned in Input System

        InputAction _healAction;

        [Header("Thresholds")]
        [SerializeField, Min(0f)] private float moveStartThreshold = 0.20f;
        [SerializeField, Min(0f)] private float moveEndThreshold = 0.15f;

        [Header("Right Stick Camera")]
        [SerializeField, Min(0f)] private float cameraSnapThreshold = 0.75f;
        [SerializeField, Min(0f)] private float cameraSnapResetThreshold = 0.50f;
        [SerializeField, Min(0f)] private float cameraForwardThreshold = 0.75f;
        [SerializeField, Min(0f)] private float cameraForwardResetThreshold = 0.50f;

        [Header("Input Gates")]
        [SerializeField] private bool canMove = false;
        [SerializeField] private bool canChangeCamera = false;

        public Vector2 MoveAxis { get; private set; }
        public Vector2 RightStickAxis { get; private set; }
        public bool IsMovePressed { get; private set; }

        private bool _aWasPressed;
        private bool _bWasPressed;
        private bool _leftMenuWasPressed;
        private bool _targetLockWasPressed;
        private bool _moveWasPressed;

        private bool _cameraSnapHeld;
        private bool _cameraForwardHeld;

        private float _aDownBuffered= -10f;
        private float _aReleasedBuffered= -10f;
        private float _bDownBuffered= -10f;
        private float _bReleasedBuffered= -10f;
        private float _leftMenuDownBuffered= -10f;
        private float _leftMenuReleasedBuffered= -10f;
        private float _targetLockToggleBuffered= -10f;
        private float _moveStartedBuffered= -10f;
        private float _moveEndedBuffered= -10f;
        private float _cameraForwardBuffered= -10f;

        private int _cameraSnapBuffered;

        private float _healBuffered = -10f;

        void Awake()
        {
              _healAction = InputSystem.actions.FindAction("Heal");
        }

        private void OnEnable()
        {
            EnableAction(moveAction);
            EnableAction(rightStickAction);
            EnableAction(aButtonAction);
            EnableAction(bButtonAction);
            EnableAction(leftMenuAction);
            EnableAction(targetLockToggleAction);
        }

        private void OnDisable()
        {
            DisableAction(moveAction);
            DisableAction(rightStickAction);
            DisableAction(aButtonAction);
            DisableAction(bButtonAction);
            DisableAction(leftMenuAction);
            DisableAction(targetLockToggleAction);
        }

        private void Update()
        {
            UpdateButtons();
            UpdateMovement();
            UpdateCameraInput();
        }

        public bool GetADown(float bufferTime) => GetBuffered(ref _aDownBuffered, bufferTime, false);
        public bool GetAReleased() => GetBuffered(ref _aReleasedBuffered);
        public bool GetBDown(float bufferTime) => GetBuffered(ref _bDownBuffered, bufferTime, false);
        public bool GetBReleased() => GetBuffered(ref _bReleasedBuffered);
        public bool GetLeftMenuDown() => GetBuffered(ref _leftMenuDownBuffered);
        public bool GetLeftMenuReleased() => GetBuffered(ref _leftMenuReleasedBuffered);
        public bool GetTargetLockToggleDown() => GetBuffered(ref _targetLockToggleBuffered);
        public bool GetMovementStarted() => GetBuffered(ref _moveStartedBuffered);
        public bool GetMovementEnded() => GetBuffered(ref _moveEndedBuffered);
        public bool GetCameraForwardDown() => GetBuffered(ref _cameraForwardBuffered);

        float ResetBufferedValue(ref float value) => value = -1;
        public float ResetBufferedADown() => ResetBufferedValue(ref _aDownBuffered);
        public float ResetBufferedBDown() => ResetBufferedValue(ref _bDownBuffered);

        public float ResetBufferedHealButtonPress() => ResetBufferedValue(ref _healBuffered);


        public bool GetHealPressed(float bufferTime) => GetBuffered(ref _healBuffered, bufferTime, false);



        public int GetCameraSnapDirection()
        {
            int value = _cameraSnapBuffered;
            _cameraSnapBuffered = 0;
            return value;
        }

        public void SetCanMove(bool value)
        {
            canMove = value;

            if (!canMove)
                ResetMovementState();
        }

        public void SetCanChangeCamera(bool value)
        {
            canChangeCamera = value;

            if (!canChangeCamera)
                ResetCameraState();
        }

        public void EnableMovement() => SetCanMove(true);
        public void DisableMovement() => SetCanMove(false);
        public void EnableCameraChange() => SetCanChangeCamera(true);
        public void DisableCameraChange() => SetCanChangeCamera(false);

        private bool GetBuffered(ref float value, float bufferTime = 0.0f, bool resetValue = true)
        {
            
            if (value < 0 || Time.time - value > bufferTime) //!value || )
                return false;

            if(resetValue)
                value = -1; // false;
            return true;
        }

        private static void EnableAction(InputActionProperty property)
        {
            InputAction action = property.action;
            if (action != null && !action.enabled)
                action.Enable();
        }

        private static void DisableAction(InputActionProperty property)
        {
            InputAction action = property.action;
            if (action != null && action.enabled)
                action.Disable();
        }

        private static bool ReadButton(InputActionProperty property)
        {
            InputAction action = property.action;
            if (action == null || !action.enabled)
                return false;

            return action.IsPressed();
        }

        private static Vector2 ReadVector2(InputActionProperty property)
        {
            InputAction action = property.action;
            if (action == null || !action.enabled)
                return Vector2.zero;

            return action.ReadValue<Vector2>();
        }

        private void UpdateButtons()
        {
            bool aPressed = ReadButton(aButtonAction);
            bool bPressed = ReadButton(bButtonAction);
            bool leftMenuPressed = ReadButton(leftMenuAction);
            bool targetLockPressed = ReadButton(targetLockToggleAction);

            if (aPressed && !_aWasPressed)
                _aDownBuffered = Time.time;
            else if (!aPressed && _aWasPressed)
                _aReleasedBuffered = Time.time;

            if (bPressed && !_bWasPressed)
                _bDownBuffered = Time.time;
            else if (!bPressed && _bWasPressed)
                _bReleasedBuffered = Time.time;

            if (leftMenuPressed && !_leftMenuWasPressed)
                _leftMenuDownBuffered = Time.time;
            else if (!leftMenuPressed && _leftMenuWasPressed)
                _leftMenuReleasedBuffered = Time.time;

            if (targetLockPressed && !_targetLockWasPressed)
                _targetLockToggleBuffered = Time.time;

            _aWasPressed = aPressed;
            _bWasPressed = bPressed;
            _leftMenuWasPressed = leftMenuPressed;
            _targetLockWasPressed = targetLockPressed;


            if(_healAction != null && _healAction.WasPressedThisFrame())
                _healBuffered = Time.time;
        }

        private void UpdateMovement()
        {
            if (!canMove)
            {
                ResetMovementState();
                return;
            }

            Vector2 axis = ReadVector2(moveAction);
            MoveAxis = axis;

            float magnitude = axis.magnitude;
            bool movePressedNow = _moveWasPressed
                ? magnitude >= moveEndThreshold
                : magnitude >= moveStartThreshold;

            if (movePressedNow && !_moveWasPressed)
                _moveStartedBuffered = Time.time;
            else if (!movePressedNow && _moveWasPressed)
                _moveEndedBuffered = Time.time;

            IsMovePressed = movePressedNow;
            _moveWasPressed = movePressedNow;
        }

        private void UpdateCameraInput()
        {
            if (!canChangeCamera)
            {
                ResetCameraState();
                return;
            }

            Vector2 axis = ReadVector2(rightStickAction);
            RightStickAxis = axis;

            UpdateCameraSnap(axis);
            UpdateCameraForward(axis);
        }

        private void UpdateCameraSnap(Vector2 axis)
        {
            float x = axis.x;
            bool wantsSnap = Mathf.Abs(x) >= cameraSnapThreshold;

            if (wantsSnap && !_cameraSnapHeld)
            {
                _cameraSnapBuffered = x < 0f ? -1 : 1;
                _cameraSnapHeld = true;
            }
            else if (_cameraSnapHeld && Mathf.Abs(x) <= cameraSnapResetThreshold)
            {
                _cameraSnapHeld = false;
            }
        }

        private void UpdateCameraForward(Vector2 axis)
        {
            float y = axis.y;
            bool wantsForward = y >= cameraForwardThreshold;

            if (wantsForward && !_cameraForwardHeld)
            {
                _cameraForwardBuffered = Time.time;
                _cameraForwardHeld = true;
            }
            else if (_cameraForwardHeld && y <= cameraForwardResetThreshold)
            {
                _cameraForwardHeld = false;
            }
        }

        private void ResetMovementState()
        {
            MoveAxis = Vector2.zero;
            IsMovePressed = false;
            _moveWasPressed = false;
            _moveStartedBuffered = -1;
            _moveEndedBuffered = -1;
        }

        private void ResetCameraState()
        {
            RightStickAxis = Vector2.zero;
            _cameraSnapHeld = false;
            _cameraForwardHeld = false;
            _cameraSnapBuffered = 0;
            _cameraForwardBuffered = -1;
        }
    }
}