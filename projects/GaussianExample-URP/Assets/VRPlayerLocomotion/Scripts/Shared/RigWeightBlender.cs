using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace VRPlayer
{
    public class RigWeightBlender : MonoBehaviour
    {
        [SerializeField] private List<Rig> rigs = new();

        private float _targetWeight = 1f;
        private float _startWeight = 1f;
        private float _blendDuration;
        private float _blendTimer;
        private bool _blending;

        public float CurrentWeight
        {
            get
            {
                if (rigs.Count == 0 || rigs[0] == null)
                    return 0f;

                return rigs[0].weight;
            }
        }

        public void SetWeightInstant(float weight)
        {
            weight = Mathf.Clamp01(weight);

            _targetWeight = weight;
            _startWeight = weight;
            _blendDuration = 0f;
            _blendTimer = 0f;
            _blending = false;

            ApplyWeight(weight);
        }

        public void BlendTo(float weight, float duration)
        {
            weight = Mathf.Clamp01(weight);

            if (duration <= 0f)
            {
                SetWeightInstant(weight);
                return;
            }

            _startWeight = CurrentWeight;
            _targetWeight = weight;
            _blendDuration = duration;
            _blendTimer = 0f;
            _blending = true;
        }

        private void Update()
        {
            if (!_blending)
                return;

            _blendTimer += Time.deltaTime;
            float t = Mathf.Clamp01(_blendTimer / _blendDuration);
            float weight = Mathf.Lerp(_startWeight, _targetWeight, t);

            ApplyWeight(weight);

            if (t >= 1f)
                _blending = false;
        }

        private void ApplyWeight(float weight)
        {
            for (int i = 0; i < rigs.Count; i++)
            {
                if (rigs[i] != null)
                    rigs[i].weight = weight;
            }
        }
    }
}