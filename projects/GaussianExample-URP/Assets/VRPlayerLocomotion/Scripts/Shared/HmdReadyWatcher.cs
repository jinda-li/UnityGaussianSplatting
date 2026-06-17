using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR;

namespace VRPlayer
{
    public class HmdReadyWatcher : MonoBehaviour
    {
        [Header("Settings")]
        [SerializeField] private int requiredStableFrames = 5;
        [SerializeField] private bool fireOnlyOnce = true;

        [Header("Events")]
        [SerializeField] private UnityEvent onHmdReady;

        public bool IsReady { get; private set; }

        private InputDevice _hmd;
        private readonly List<InputDevice> _devices = new();

        private int _stableFrameCount;
        private bool _hasFired;

        private void OnEnable()
        {
            InputDevices.deviceConnected += OnDeviceChanged;
            InputDevices.deviceDisconnected += OnDeviceChanged;
            AcquireHmd();
        }

        private void OnDisable()
        {
            InputDevices.deviceConnected -= OnDeviceChanged;
            InputDevices.deviceDisconnected -= OnDeviceChanged;
        }

        private void Update()
        {
            if (!_hmd.isValid)
                AcquireHmd();

            bool readyThisFrame = IsHmdReadyThisFrame();

            if (readyThisFrame)
            {
                _stableFrameCount++;
            }
            else
            {
                _stableFrameCount = 0;
                IsReady = false;
            }

            if (_stableFrameCount >= requiredStableFrames)
            {
                IsReady = true;

                if (!_hasFired || !fireOnlyOnce)
                {
                    _hasFired = true;
                    onHmdReady?.Invoke();
                }
            }
        }

        private void OnDeviceChanged(InputDevice _)
        {
            AcquireHmd();
        }

        private void AcquireHmd()
        {
            _devices.Clear();
            InputDevices.GetDevicesAtXRNode(XRNode.Head, _devices);

            for (int i = 0; i < _devices.Count; i++)
            {
                if (_devices[i].isValid)
                {
                    _hmd = _devices[i];
                    return;
                }
            }

            _hmd = default;
        }

        private bool IsHmdReadyThisFrame()
        {
            if (!_hmd.isValid)
                return false;

            if (!_hmd.TryGetFeatureValue(CommonUsages.isTracked, out bool isTracked) || !isTracked)
                return false;

            bool hasPosition = _hmd.TryGetFeatureValue(CommonUsages.centerEyePosition, out Vector3 position);
            bool hasRotation = _hmd.TryGetFeatureValue(CommonUsages.centerEyeRotation, out Quaternion rotation);

            if (!hasPosition || !hasRotation)
                return false;

            // Optional sanity check to avoid obvious uninitialized values.
            // Rotation identity can still be valid in some setups, so keep this loose.
            if (position == Vector3.zero && rotation == Quaternion.identity)
                return false;

            return true;
        }

        public void ResetReadyState()
        {
            IsReady = false;
            _hasFired = false;
            _stableFrameCount = 0;
        }
    }
}