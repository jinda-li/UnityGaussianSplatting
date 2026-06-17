using UnityEngine;

namespace VRPlayer
{
    public class VRPlayerLocomotionStateMachine : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private VRPlayerControllerInput input;
        [SerializeField] private VRCameraRigController cameraRig;
        [SerializeField] private VRPlayerRootMotionController rootMotionController;
        [SerializeField] private VRControllerActivityState controllerActivity;
        [SerializeField] private AvatarHeadFollower avatarHeadFollower;

        [Header("Rig Blenders")]
        [SerializeField] private RigWeightBlender leftArmRigBlender;
        [SerializeField] private RigWeightBlender rightArmRigBlender;

        [Header("Idle Rig Weights")]
        [SerializeField] private float idleLeftArmWeight = 1f;
        [SerializeField] private float idleRightArmWeight = 1f;

        [Header("Locomotion Rig Weights")]
        [SerializeField] private float locomotionLeftArmMaxWeight = 1f;
        [SerializeField] private float locomotionRightArmMaxWeight = 1f;

        [Header("Action Rig Weights")]
        [SerializeField] private float actionLeftArmWeight = 0f;
        [SerializeField] private float actionRightArmWeight = 0f;

        [Header("Rig Blend Times")]
        [SerializeField] private float idleRigBlendTime = 0.15f;
        [SerializeField] private float locomotionRigBlendTime = 0.10f;
        [SerializeField] private float actionRigBlendTime = 0.15f;

        [Header("Settings")]
        [SerializeField] private float locomotionRigWeightEpsilon = 0.01f;
        [SerializeField] private bool startInLocomotion = false;
        [SerializeField] private bool allowSnapTurnDuringActions = false;

        [SerializeField] private float inputBufferTime = 0.2f;
        public float rollRecastTime = 0.6f;

        private LocomotionState _currentState;

        private LocomotionIdleState _idleState;
        private LocomotionMoveState _locomotionState;
        private LocomotionDodgeRollState _dodgeRollState;

        private float _lastBlendedLeftArmWeight = -999f;
        private float _lastBlendedRightArmWeight = -999f;

        private bool _hasExternalDodgeRollDirection;
        private Vector3 _externalDodgeRollDirection;

        public VRPlayerControllerInput Input => input;
        public VRCameraRigController CameraRig => cameraRig;
        public VRPlayerRootMotionController RootMotionController => rootMotionController;
        public VRControllerActivityState ControllerActivity => controllerActivity;
        public AvatarHeadFollower AvatarHeadFollower => avatarHeadFollower;

        public float IdleLeftArmWeight => idleLeftArmWeight;
        public float IdleRightArmWeight => idleRightArmWeight;
        public float LocomotionLeftArmMaxWeight => locomotionLeftArmMaxWeight;
        public float LocomotionRightArmMaxWeight => locomotionRightArmMaxWeight;
        public float ActionLeftArmWeight => actionLeftArmWeight;
        public float ActionRightArmWeight => actionRightArmWeight;
        public float IdleRigBlendTime => idleRigBlendTime;
        public float LocomotionRigBlendTime => locomotionRigBlendTime;
        public float ActionRigBlendTime => actionRigBlendTime;
        public bool AllowSnapTurnDuringActions => allowSnapTurnDuringActions;

        public bool IsInIdleState() => _currentState == _idleState;
        public bool IsInLocomotionState() => _currentState == _locomotionState;
        public bool IsInDodgeRollState() => _currentState == _dodgeRollState;

        private void Awake()
        {
            _idleState = new LocomotionIdleState(this);
            _locomotionState = new LocomotionMoveState(this);
            _dodgeRollState = new LocomotionDodgeRollState(this);
        }

        private void OnEnable()
        {
            ChangeState(startInLocomotion ? _locomotionState : _idleState);
        }

        private void Update()
        {
            _dodgeRollState.TimeBeforeRestarting = rollRecastTime;

            if (_currentState == null || input == null)
                return;

            HandleBufferedInput();
            _currentState.Tick();
        }

        private float _timeInStateChanged;

        public bool ChangeState(LocomotionState nextState, bool forceStateChange = false)
        {
            if (nextState == null)
                return false;

            if (!forceStateChange)
            {
                if (_currentState == nextState &&
                    (_currentState.TimeBeforeRestarting < 0 ||
                     Time.time - _timeInStateChanged < _currentState.TimeBeforeRestarting))
                {
                    return false;
                }
            }

            _currentState?.Exit();
            _currentState = nextState;
            _currentState.Enter();

            _timeInStateChanged = Time.time;
            return true;
        }

        public void GoToIdle() => ChangeState(_idleState);
        public void GoToLocomotion() => ChangeState(_locomotionState);
        public bool GoToDodgeRoll() => ChangeState(_dodgeRollState);

        public bool TriggerDodgeRoll(Vector3 worldDirection)
        {
            worldDirection.y = 0f;

            if (worldDirection.sqrMagnitude > 0.0001f)
            {
                _externalDodgeRollDirection = worldDirection.normalized;
                _hasExternalDodgeRollDirection = true;
            }
            else
            {
                _externalDodgeRollDirection = Vector3.zero;
                _hasExternalDodgeRollDirection = false;
            }

            return GoToDodgeRoll();
        }

        public bool TryConsumeExternalDodgeRollDirection(out Vector3 worldDirection)
        {
            if (_hasExternalDodgeRollDirection)
            {
                worldDirection = _externalDodgeRollDirection;
                _externalDodgeRollDirection = Vector3.zero;
                _hasExternalDodgeRollDirection = false;
                return true;
            }

            worldDirection = Vector3.zero;
            return false;
        }

        private void HandleBufferedInput()
        {
            if (input.GetMovementStarted())
                _currentState.OnMovementStarted();

            if (input.GetMovementEnded())
                _currentState.OnMovementEnded();

            if (input.GetBDown(inputBufferTime))
            {
                if (_currentState.OnDodgeRollPressed())
                    input.ResetBufferedBDown();
            }

            int snapDirection = input.GetCameraSnapDirection();
            if (snapDirection != 0)
                _currentState.OnCameraSnap(snapDirection);
        }

        public void BlendLeftArm(float weight, float time)
        {
            weight = Mathf.Clamp01(weight);

            if (leftArmRigBlender == null)
                return;

            if (Mathf.Abs(_lastBlendedLeftArmWeight - weight) < locomotionRigWeightEpsilon)
                return;

            _lastBlendedLeftArmWeight = weight;
            leftArmRigBlender.BlendTo(weight, time);
        }

        public void BlendRightArm(float weight, float time)
        {
            weight = Mathf.Clamp01(weight);

            if (rightArmRigBlender == null)
                return;

            if (Mathf.Abs(_lastBlendedRightArmWeight - weight) < locomotionRigWeightEpsilon)
                return;

            _lastBlendedRightArmWeight = weight;
            rightArmRigBlender.BlendTo(weight, time);
        }

        public void BlendRigs(float leftArmWeight, float rightArmWeight, float time)
        {
            BlendLeftArm(leftArmWeight, time);
            BlendRightArm(rightArmWeight, time);
        }

        public void UpdateLocomotionRigWeights()
        {
            float leftActivity = controllerActivity != null ? controllerActivity.LeftWeight : 0f;
            float rightActivity = controllerActivity != null ? controllerActivity.RightWeight : 0f;

            float leftTarget = leftActivity * locomotionLeftArmMaxWeight;
            float rightTarget = rightActivity * locomotionRightArmMaxWeight;

            BlendRigs(leftTarget, rightTarget, locomotionRigBlendTime);
        }

        public void ResetRigBlendCache()
        {
            _lastBlendedLeftArmWeight = -999f;
            _lastBlendedRightArmWeight = -999f;
        }
    }

    public abstract class LocomotionState
    {
        protected readonly VRPlayerLocomotionStateMachine machine;

        protected LocomotionState(VRPlayerLocomotionStateMachine machine)
        {
            this.machine = machine;
        }

        public float TimeBeforeRestarting { get; set; } = -1f;

        public virtual void Enter() { }
        public virtual void Tick() { }
        public virtual void Exit() { }

        public virtual void OnMovementStarted() { }
        public virtual void OnMovementEnded() { }
        public virtual bool OnDodgeRollPressed() => false;

        public virtual void OnCameraSnap(int direction)
        {
            if (direction < 0)
                machine.CameraRig?.SnapLeft();
            else if (direction > 0)
                machine.CameraRig?.SnapRight();
        }

        protected void BlendRigs(float leftArmWeight, float rightArmWeight, float time)
        {
            machine.BlendRigs(leftArmWeight, rightArmWeight, time);
        }

        protected void SetIdlePresentation()
        {
            machine.CameraRig?.SnapRigToAvatarHead();
            machine.CameraRig?.SetLocomotionState(false);
            machine.AvatarHeadFollower?.EnableFollow();
            machine.RootMotionController?.SetLocomotionEnabled(true);
            machine.RootMotionController?.SetFacingEnabled(true);
        }

        protected void SetLocomotionPresentation()
        {
            machine.AvatarHeadFollower?.DisableFollow();
            machine.CameraRig?.SetLocomotionState(true);
            machine.RootMotionController?.SetLocomotionEnabled(true);
            machine.RootMotionController?.SetFacingEnabled(true);
        }

        protected void SetActionPresentation()
        {
            machine.AvatarHeadFollower?.DisableFollow();
            machine.CameraRig?.SetLocomotionState(false);
            machine.RootMotionController?.SetLocomotionEnabled(false);
            machine.RootMotionController?.SetFacingEnabled(false);
        }

        protected void SnapRigBackToAvatar()
        {
            machine.CameraRig?.SnapRigToAvatarHead();
        }

        protected bool WantsToMove()
        {
            return machine.RootMotionController != null && machine.RootMotionController.WantsToMove();
        }

        protected void GoToIdleOrLocomotion()
        {
            if (WantsToMove())
                machine.GoToLocomotion();
            else
                machine.GoToIdle();
        }
    }

    public sealed class LocomotionIdleState : LocomotionState
    {
        public LocomotionIdleState(VRPlayerLocomotionStateMachine machine) : base(machine)
        {
            TimeBeforeRestarting = -1f;
        }

        public override void Enter()
        {
            machine.ResetRigBlendCache();
            SetIdlePresentation();

            if (machine.Input != null)
                machine.Input.EnableMovement();

            BlendRigs(
                machine.IdleLeftArmWeight,
                machine.IdleRightArmWeight,
                machine.IdleRigBlendTime
            );
        }

        public override void Tick()
        {
            if (WantsToMove())
                machine.GoToLocomotion();
        }

        public override void OnMovementStarted()
        {
            machine.GoToLocomotion();
        }

        public override bool OnDodgeRollPressed()
        {
            machine.GoToDodgeRoll();
            return true;
        }
    }

    public sealed class LocomotionMoveState : LocomotionState
    {
        public LocomotionMoveState(VRPlayerLocomotionStateMachine machine) : base(machine) { }

        public override void Enter()
        {
            machine.ResetRigBlendCache();
            SetLocomotionPresentation();
            machine.RootMotionController?.PrepareForLocomotion();
            machine.UpdateLocomotionRigWeights();
        }

        public override void Tick()
        {
            machine.UpdateLocomotionRigWeights();

            if (machine.RootMotionController != null &&
                !machine.RootMotionController.WantsToMove() &&
                !machine.RootMotionController.IsMoving)
            {
                machine.GoToIdle();
            }
        }

        public override bool OnDodgeRollPressed()
        {
            machine.GoToDodgeRoll();
            return true;
        }
    }

    public sealed class LocomotionDodgeRollState : LocomotionState
    {
        private bool _enteredAnimatorState;
        private bool _restoredFacing;

        public LocomotionDodgeRollState(VRPlayerLocomotionStateMachine machine) : base(machine) { }

        public override void Enter()
        {
            machine.ResetRigBlendCache();
            machine.RootMotionController?.StoreCurrentFacing();

            if (machine.TryConsumeExternalDodgeRollDirection(out Vector3 externalDirection))
                machine.RootMotionController?.AlignFacingToWorldDirectionOrCurrent(externalDirection);
            else
                machine.RootMotionController?.AlignFacingToMovementOrCurrent();

            SetActionPresentation();
            BlendRigs(
                machine.ActionLeftArmWeight,
                machine.ActionRightArmWeight,
                machine.ActionRigBlendTime
            );

            _enteredAnimatorState = false;
            _restoredFacing = false;

            machine.RootMotionController?.TriggerDodgeRoll();
        }

        public override void Tick()
        {
            if (machine.RootMotionController == null)
                return;

            if (machine.RootMotionController.IsInDodgeRollState())
            {
                _enteredAnimatorState = true;
                return;
            }

            if (!_enteredAnimatorState)
                return;

            if (!_restoredFacing)
            {
                machine.RootMotionController.RestoreStoredFacing();
                _restoredFacing = true;
            }

            SnapRigBackToAvatar();
            GoToIdleOrLocomotion();
        }

        public override void Exit()
        {
            if (!_restoredFacing)
                machine.RootMotionController?.RestoreStoredFacing();
        }

        public override void OnCameraSnap(int direction)
        {
            if (!machine.AllowSnapTurnDuringActions)
                return;

            base.OnCameraSnap(direction);
        }

        public override bool OnDodgeRollPressed()
        {
            if (!machine.RootMotionController.CanChainDodgeRoll(0.7f))
                return false;

            machine.RootMotionController.RestartDodgeRoll();
            Enter();
            return true;
        }
    }
}
