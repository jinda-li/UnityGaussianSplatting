using UnityEngine;

namespace VRPlayer
{
    /// <summary>
    /// Hides head/face meshes (and optionally collapses the head bone) while in first-person VR,
    /// so the HMD does not render the inside of the avatar. Restores them for third-person.
    /// </summary>
    public class AvatarFirstPersonVisibility : MonoBehaviour
    {
        [Header("Hide in First Person")]
        [Tooltip("Head/face meshes that clip the HMD in first-person (Eye, Hair, Mouth, etc.).")]
        [SerializeField] private GameObject[] hideInFirstPerson;

        [Header("Head Bone Scale (optional)")]
        [Tooltip("If set, scaled near-zero in first-person so skinned body meshes (e.g. Body3) do not show face interior.")]
        [SerializeField] private Transform headBone;
        [SerializeField] private float firstPersonHeadScale = 0.0001f;

        [Header("Startup")]
        [SerializeField] private bool startInFirstPerson = true;

        private Vector3 _defaultHeadScale = Vector3.one;
        private bool _hasDefaultHeadScale;
        private bool _isFirstPerson = true;

        private void Awake()
        {
            CacheHeadScale();
        }

        private void OnEnable()
        {
            SetFirstPerson(startInFirstPerson);
        }

        /// <summary>True = first-person (hide head). False = third-person (show full avatar).</summary>
        public void SetFirstPerson(bool firstPerson)
        {
            _isFirstPerson = firstPerson;

            if (hideInFirstPerson != null)
            {
                for (int i = 0; i < hideInFirstPerson.Length; i++)
                {
                    GameObject go = hideInFirstPerson[i];
                    if (go != null)
                        go.SetActive(!firstPerson);
                }
            }

            ApplyHeadScale(firstPerson);
        }

        public void HideForFirstPerson() => SetFirstPerson(true);

        public void ShowForThirdPerson() => SetFirstPerson(false);

        public bool IsFirstPerson => _isFirstPerson;

        private void CacheHeadScale()
        {
            if (headBone == null || _hasDefaultHeadScale)
                return;

            _defaultHeadScale = headBone.localScale;
            _hasDefaultHeadScale = true;
        }

        private void ApplyHeadScale(bool firstPerson)
        {
            if (headBone == null)
                return;

            CacheHeadScale();

            if (firstPerson)
            {
                float s = Mathf.Max(firstPersonHeadScale, 0.00001f);
                headBone.localScale = new Vector3(s, s, s);
            }
            else if (_hasDefaultHeadScale)
            {
                headBone.localScale = _defaultHeadScale;
            }
        }

#if UNITY_EDITOR
        [ContextMenu("Auto-Fill Survival Character Head Parts")]
        private void AutoFillSurvivalCharacterHeadParts()
        {
            Transform root = transform;
            var list = new System.Collections.Generic.List<GameObject>();
            string[] names = { "Eye", "Eyebrows", "Eyeleash", "Hair", "Mouth1" };
            for (int i = 0; i < names.Length; i++)
            {
                Transform found = FindDeepChild(root, names[i]);
                if (found != null)
                    list.Add(found.gameObject);
            }

            hideInFirstPerson = list.ToArray();

            if (headBone == null)
            {
                var animator = GetComponentInChildren<Animator>();
                if (animator != null && animator.isHuman)
                    headBone = animator.GetBoneTransform(HumanBodyBones.Head);
            }

            UnityEditor.EditorUtility.SetDirty(this);
        }

        private static Transform FindDeepChild(Transform parent, string name)
        {
            if (parent.name == name)
                return parent;

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform found = FindDeepChild(parent.GetChild(i), name);
                if (found != null)
                    return found;
            }

            return null;
        }
#endif
    }
}
