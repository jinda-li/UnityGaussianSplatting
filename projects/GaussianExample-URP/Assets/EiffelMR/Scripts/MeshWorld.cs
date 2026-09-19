using System.Collections;
using UnityEngine;

namespace EiffelMR
{
    // The Blender scene, exported as meshes, standing in for the trained splat.
    //
    // m_WorldRoot holds the whole park at 1:1 in its own space - tower base at
    // the origin, metres - and is parented under the miniature while the tower
    // is something to hold. At that scale only the tower shows, and only as a
    // point cloud sampled off its own surface (TowerPointCloud); the rest of the
    // park is switched off, which does the job the GaussianCutout does for the
    // splat.
    //
    // Arriving detaches the root and grows it log-linearly to 1:1, pinning the
    // hero viewpoint under the player's feet and turning it so the view the
    // scene was tuned for is the one in front of them. Leaving runs the same
    // path backwards onto the miniature.
    public class MeshWorld : EiffelWorld
    {
        [Header("Scene")]
        [Tooltip("Everything, at 1:1 in local space. Parented under the " +
                 "miniature while it is a miniature.")]
        public Transform m_WorldRoot;

        [Tooltip("The object the player holds and throws (ThrownTower).")]
        public Transform m_Miniature;

        [Tooltip("The park around the tower. Off while it is a miniature.")]
        public GameObject m_Environment;

        public TowerPointCloud m_Points;

        [Tooltip("Renderers of the solid tower, drawn with TowerDissolve.")]
        public Renderer[] m_TowerRenderers;

        [Tooltip("Hero viewpoint on the ground, inside the world root.")]
        public Transform m_Hero;

        [Tooltip("Marker looking from the hero viewpoint at the tower.")]
        public Transform m_HeroLook;

        [Tooltip("Marker in the direction of the sun, inside the world root.")]
        public Transform m_SunMarker;

        [Header("Player")]
        public Transform m_Head;
        public float m_FloorY;

        [Header("Scale")]
        [Tooltip("0.0009 x 324 m = 29 cm.")]
        public float m_TableScale = 0.0009f;
        public float m_ImmersiveScale = 1f;
        public float m_LeaveDuration = 1.6f;

        [Header("Look")]
        public Light m_Sun;
        [Tooltip("Shown while in the room: in MR this is passthrough, on a " +
                 "desktop a stand-in room.")]
        public GameObject m_Room;
        public Camera m_Camera;
        public Color m_RoomBackground = new Color(0.16f, 0.17f, 0.19f);
        public Material m_Skybox;

        static readonly int k_Dissolve = Shader.PropertyToID("_Dissolve");

        enum Mode { Place, Arriving, Immersive, Leaving }
        Mode m_Mode = Mode.Place;
        MaterialPropertyBlock m_Block;
        float m_Solidify;

        public override float TableScale => m_TableScale;
        public override bool IsBusy => m_Mode == Mode.Arriving || m_Mode == Mode.Leaving;
        public override bool InPlace => m_Mode == Mode.Place;
        public override bool IsImmersive => m_Mode == Mode.Immersive;
        public override bool CanGrow => m_WorldRoot;
        public override float MeasuredScale => m_WorldRoot ? m_WorldRoot.lossyScale.x : 0f;

        void Awake()
        {
            if (!m_Head && Camera.main)
                m_Head = Camera.main.transform;
            if (!m_Camera && Camera.main)
                m_Camera = Camera.main;
            ResetToTable();
        }

        void LateUpdate()
        {
            // The light follows the world: while it is being turned to face the
            // player, a fixed light would swing the tower's shadows round with it.
            if (m_Sun && m_SunMarker && m_WorldRoot && m_Mode != Mode.Place)
                m_Sun.transform.rotation = Quaternion.LookRotation(
                    m_WorldRoot.position - m_SunMarker.position);
        }

        public override void SetMiniatureScale(float magnitude)
        {
            if (m_WorldRoot)
                m_WorldRoot.localScale = Vector3.one * Mathf.Max(magnitude, 1e-6f);
        }

        public override void SetSolidify(float amount)
        {
            m_Solidify = Mathf.Clamp01(amount);
            if (m_Points)
                m_Points.Solidify = m_Solidify;
            m_Block ??= new MaterialPropertyBlock();
            if (m_TowerRenderers == null)
                return;
            foreach (var r in m_TowerRenderers)
            {
                if (!r)
                    continue;
                // Nothing to draw at 0, and 515 k faces is not free.
                r.enabled = m_Solidify > 0.001f;
                r.GetPropertyBlock(m_Block);
                m_Block.SetFloat(k_Dissolve, m_Solidify);
                r.SetPropertyBlock(m_Block);
            }
        }

        public override void Arrive(Vector3 landing, float duration)
        {
            if (m_Mode != Mode.Place || !m_WorldRoot)
                return;
            StopAllCoroutines();
            StartCoroutine(ArriveRoutine(Mathf.Max(duration, 0.05f)));
        }

        public override void Leave()
        {
            if (m_Mode != Mode.Immersive || !m_WorldRoot)
                return;
            StopAllCoroutines();
            StartCoroutine(LeaveRoutine(Mathf.Max(m_LeaveDuration, 0.05f)));
        }

        public override void ResetToTable()
        {
            StopAllCoroutines();
            m_Mode = Mode.Place;
            if (m_WorldRoot && m_Miniature)
            {
                m_WorldRoot.SetParent(m_Miniature, false);
                m_WorldRoot.localPosition = Vector3.zero;
                m_WorldRoot.localRotation = Quaternion.identity;
            }
            SetMiniatureScale(m_TableScale);
            SetSolidify(0f);
            SetImmersiveLook(false);
        }

        // Where the hero viewpoint is in the root's own space.
        Vector3 AnchorLocal =>
            m_Hero ? m_WorldRoot.InverseTransformPoint(m_Hero.position) : Vector3.zero;

        Vector3 Feet
        {
            get
            {
                Vector3 h = m_Head ? m_Head.position : Vector3.zero;
                return new Vector3(h.x, m_FloorY, h.z);
            }
        }

        // The rotation that puts the hero view in front of the player.
        Quaternion FacingRotation(Quaternion current)
        {
            if (!m_HeroLook || !m_Head)
                return current;
            Vector3 heroFwd = m_WorldRoot.InverseTransformDirection(m_HeroLook.forward);
            heroFwd.y = 0f;
            Vector3 playerFwd = m_Head.forward;
            playerFwd.y = 0f;
            if (heroFwd.sqrMagnitude < 1e-6f || playerFwd.sqrMagnitude < 1e-6f)
                return current;
            float yaw = Vector3.SignedAngle(heroFwd.normalized, playerFwd.normalized,
                                            Vector3.up);
            return Quaternion.Euler(0f, yaw, 0f);
        }

        IEnumerator ArriveRoutine(float duration)
        {
            m_Mode = Mode.Arriving;
            Vector3 anchorLocal = AnchorLocal;
            m_WorldRoot.SetParent(null, true);
            if (m_Environment)
                m_Environment.SetActive(true);

            float startMag = Mathf.Max(m_WorldRoot.lossyScale.x, 1e-6f);
            Quaternion startRot = m_WorldRoot.rotation;
            // Frame 0 keeps the pose it landed in; pinning the anchor to the
            // player's feet immediately would teleport the tower off the ring.
            Vector3 anchorStart = m_WorldRoot.TransformPoint(anchorLocal);

            bool flipped = false;
            float t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime / duration;
                float e = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                float mag = Mathf.Exp(Mathf.Lerp(Mathf.Log(startMag),
                                                 Mathf.Log(m_ImmersiveScale), e));
                Quaternion rot = Quaternion.Slerp(startRot, FacingRotation(startRot), e);
                Vector3 anchor = Vector3.Lerp(anchorStart, Feet, e);
                Place(anchor, anchorLocal, rot, mag);
                if (!flipped && e >= 0.5f)
                {
                    flipped = true;
                    SetImmersiveLook(true);
                }
                yield return null;
            }
            Place(Feet, anchorLocal, FacingRotation(startRot), m_ImmersiveScale);
            SetImmersiveLook(true);
            m_Mode = Mode.Immersive;
        }

        IEnumerator LeaveRoutine(float duration)
        {
            m_Mode = Mode.Leaving;
            Vector3 anchorLocal = AnchorLocal;
            float startMag = Mathf.Max(m_WorldRoot.lossyScale.x, 1e-6f);
            Quaternion startRot = m_WorldRoot.rotation;
            Vector3 anchorStart = m_WorldRoot.TransformPoint(anchorLocal);

            Quaternion endRot = m_Miniature ? m_Miniature.rotation : startRot;
            Vector3 endPos = m_Miniature ? m_Miniature.position : Vector3.zero;
            Vector3 anchorEnd = endPos + endRot * (anchorLocal * m_TableScale);

            bool flipped = false;
            float t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime / duration;
                float e = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                float mag = Mathf.Exp(Mathf.Lerp(Mathf.Log(startMag),
                                                 Mathf.Log(m_TableScale), e));
                Place(Vector3.Lerp(anchorStart, anchorEnd, e), anchorLocal,
                      Quaternion.Slerp(startRot, endRot, e), mag);
                if (!flipped && e >= 0.5f)
                {
                    flipped = true;
                    SetImmersiveLook(false);
                }
                yield return null;
            }
            ResetToTable();
        }

        void Place(Vector3 anchorWorld, Vector3 anchorLocal, Quaternion rot, float mag)
        {
            m_WorldRoot.rotation = rot;
            m_WorldRoot.localScale = Vector3.one * mag;
            m_WorldRoot.position = anchorWorld - rot * (anchorLocal * mag);
        }

        void SetImmersiveLook(bool immersive)
        {
            if (m_Environment)
                m_Environment.SetActive(immersive || m_Mode == Mode.Arriving
                                        || m_Mode == Mode.Leaving);
            if (m_Room)
                m_Room.SetActive(!immersive);
            if (m_Camera)
            {
                m_Camera.clearFlags = immersive && m_Skybox
                    ? CameraClearFlags.Skybox
                    : CameraClearFlags.SolidColor;
                m_Camera.backgroundColor = m_RoomBackground;
            }
            if (immersive && m_Skybox)
                RenderSettings.skybox = m_Skybox;
        }
    }
}
