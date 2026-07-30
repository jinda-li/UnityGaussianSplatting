using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Interaction.Toolkit;
using GaussianSplatting.Runtime;
using VRPlayer;

namespace GardenMR
{
    // Drives the whole GardenMR flow:
    //   Place (MR tabletop, player can drag/rotate/scale the miniature)
    //     -> Diving (world grows from tabletop scale to 1:1, anchored at the chosen spawn point)
    //     -> Immersive (full scale, player walks around with normal locomotion)
    //     -> Returning (mirror of Diving)
    //     -> back to Place
    //
    // Deliberately does not touch package/, StylizedSplatsController, SplatWorldReveal or
    // VRSplatBrush — it only flips existing switches (GaussianCutout.enabled, ARCameraManager,
    // Camera.clearFlags, an existing locomotion component's .enabled) and animates transforms.
    public class TabletopDiveController : MonoBehaviour
    {
        public enum State { Place, Diving, Immersive, Returning }

        [Header("Core references")]
        public Transform m_Rig;                          // GardenMRRig: what the player drags/rotates
        public GaussianSplatRenderer m_SplatRenderer;     // scale target; m_SplatRenderer.transform == splat root
        public GaussianCutout m_Cutout;                   // MR-only bird's-eye cutaway
        public SplatHandleRig m_HandleRig;
        public SplatScaleHandle m_ScaleHandle;

        [Header("Place-mode UI (active only outside Immersive)")]
        public GameObject m_PlaceModeHandles;             // Move + Scale handle group
        public GameObject m_SpawnPointsGroup;

        [Tooltip("Invisible walk-on collision mesh; only needed once the player is inside at full scale.")]
        public GameObject m_CollisionProxy;

        [Header("Player")]
        public Transform m_PlayerFeet;                    // root of the locomotion rig (has the CharacterController)
        public Behaviour m_LocomotionComponent;            // disabled outside Immersive so MR browsing can't shove the player

        [Tooltip("Avatar body (CharacterController + mesh). Hidden in Place: there is no character in bird's-eye mode, " +
                 "and with the collision proxy off there is no ground for it to stand on.")]
        public GameObject m_AvatarRoot;

        [Tooltip("Applies gravity every frame independently of the locomotion state machine, so it must be " +
                 "disabled too or the avatar free-falls while the collision proxy is off.")]
        public Behaviour m_RootMotionController;

        public bool m_AutoPlaceOnStart = true;
        public float m_DefaultTableScale = 0.0375f;
        public float m_PlaceForwardDistance = 0.8f;
        public float m_PlaceHeight = 0.85f;

        [Header("MR passthrough")]
        public ARCameraManager m_ArCameraManager;
        public ARCameraBackground m_ArCameraBackground;
        public Camera m_XrCamera;

        [Tooltip("Post-processing is forced off while passthrough is on (it writes opaque alpha and hides the " +
                 "real room). Uncheck to keep it off in immersive mode too.")]
        public bool m_PostProcessingInImmersive = true;

        UnityEngine.Rendering.Universal.UniversalAdditionalCameraData m_CameraData;

        [Header("Dive animation")]
        public float m_DiveDuration = 1.0f;
        public float m_ApertureOpen = 1f;
        public float m_ApertureClosedMin = 0.05f;
        public float m_Feathering = 0.35f;
        public Renderer m_VignetteRenderer;               // TunnelingVignette instance's mesh renderer

        [Header("Editor / debug dive (M1)")]
        [Tooltip("Spawn used by the keyboard Dive shortcut. Falls back to the first child SplatSpawnPoint.")]
        public SplatSpawnPoint m_DefaultSpawnPoint;
        [Tooltip("Dive from Place mode without XR (default: keyboard D).")]
        public InputAction m_DiveAction = new InputAction("DiveToSpawn", InputActionType.Button, "<Keyboard>/d");

        [Header("Return to tabletop")]
        [Tooltip("Keyboard R plus both controller menu buttons by default.")]
        public InputAction m_ReturnAction = new InputAction("ReturnToTabletop", InputActionType.Button, "<Keyboard>/r");

        [Header("Audio")]
        [Tooltip("Plays transition.mp3 as a one-shot when Dive or Return starts.")]
        public AudioSource m_TransitionAudio;
        [Tooltip("Looping garden ambience on the splat root; starts in Immersive, stops in Place.")]
        public AudioSource m_GardenAmbience;

        static readonly int k_ApertureSize = Shader.PropertyToID("_ApertureSize");
        static readonly int k_Feathering = Shader.PropertyToID("_FeatheringEffect");

        public State CurrentState { get; private set; } = State.Place;

        Transform m_SplatRoot;
        Vector3 m_ScaleSign = Vector3.one;
        Coroutine m_Routine;
        Material m_VignetteMaterialInstance;

        void Awake()
        {
            if (m_SplatRenderer)
            {
                m_SplatRoot = m_SplatRenderer.transform;
                m_ScaleSign = new Vector3(
                    Mathf.Sign(m_SplatRoot.localScale.x),
                    Mathf.Sign(m_SplatRoot.localScale.y),
                    Mathf.Sign(m_SplatRoot.localScale.z));
            }

            if (m_VignetteRenderer)
                m_VignetteMaterialInstance = m_VignetteRenderer.material; // instance, safe to mutate

            EnsureCameraData();

            EnsureVrReturnBindings();
            EnsureXRInteractionManager();
            if (!m_HandleRig && m_PlaceModeHandles)
                m_HandleRig = m_PlaceModeHandles.GetComponent<SplatHandleRig>();
        }

        void EnsureCameraData()
        {
            if (!m_CameraData && m_XrCamera)
                m_CameraData = m_XrCamera.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
        }

        static void EnsureXRInteractionManager()
        {
            if (Object.FindFirstObjectByType<XRInteractionManager>() != null)
                return;
            var go = new GameObject("XR Interaction Manager");
            go.AddComponent<XRInteractionManager>();
        }

        void OnEnable()
        {
            if (m_DiveAction != null)
            {
                m_DiveAction.performed += OnDiveActionPerformed;
                m_DiveAction.Enable();
            }
            if (m_ReturnAction != null)
            {
                m_ReturnAction.performed += OnReturnActionPerformed;
                m_ReturnAction.Enable();
            }
        }

        void OnDisable()
        {
            if (m_DiveAction != null)
            {
                m_DiveAction.performed -= OnDiveActionPerformed;
                m_DiveAction.Disable();
            }
            if (m_ReturnAction != null)
            {
                m_ReturnAction.performed -= OnReturnActionPerformed;
                m_ReturnAction.Disable();
            }
        }

        void OnDiveActionPerformed(InputAction.CallbackContext ctx) => DiveFromShortcut();
        void OnReturnActionPerformed(InputAction.CallbackContext ctx) => Return();

        void DiveFromShortcut()
        {
            var point = m_DefaultSpawnPoint;
            if (!point && m_SpawnPointsGroup)
                point = m_SpawnPointsGroup.GetComponentInChildren<SplatSpawnPoint>(true);
            if (point)
                Dive(point);
        }

        void EnsureVrReturnBindings()
        {
            if (m_ReturnAction == null)
                return;
            foreach (var binding in m_ReturnAction.bindings)
            {
                if (!string.IsNullOrEmpty(binding.path) && binding.path.Contains("XRController"))
                    return;
            }
            m_ReturnAction.AddBinding("<XRController>{LeftHand}/menuButton");
            m_ReturnAction.AddBinding("<XRController>{RightHand}/menuButton");
        }

        void Start()
        {
            if (m_AutoPlaceOnStart)
                PlaceRigInFrontOfPlayer();

            if (m_HandleRig)
                m_HandleRig.InitializeFromScene();
            SetMagnitude(m_DefaultTableScale);
            EnterPlace();
        }

        void LateUpdate()
        {
            if (CurrentState == State.Place)
                EnforcePassthroughCamera();
        }

        // XR / URP / ARCameraBackground can clobber clearFlags or re-enable post FX after Start.
        // Meta Link only composites passthrough where the eye buffer alpha is 0.
        void EnforcePassthroughCamera()
        {
            EnsureCameraData();
            if (!m_XrCamera)
                return;
            bool needsFix = m_XrCamera.clearFlags != CameraClearFlags.SolidColor
                || m_XrCamera.backgroundColor.a > 0.001f
                || (m_CameraData && m_CameraData.renderPostProcessing)
                || (m_ArCameraManager && !m_ArCameraManager.enabled)
                || (m_ArCameraBackground && m_ArCameraBackground.enabled);
            if (needsFix)
                SetPassthrough(true);
        }

        // ---- setup ----

        void PlaceRigInFrontOfPlayer()
        {
            if (!m_Rig || !m_PlayerFeet)
                return;
            Vector3 fwd = m_PlayerFeet.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f)
                fwd = Vector3.forward;
            fwd.Normalize();

            Vector3 pos = m_PlayerFeet.position + fwd * m_PlaceForwardDistance;
            pos.y = FootY(m_PlayerFeet) + m_PlaceHeight;
            m_Rig.position = pos;
            m_Rig.rotation = Quaternion.LookRotation(-fwd, Vector3.up); // face the player
        }

        static float FootY(Transform playerRoot)
        {
            var cc = playerRoot.GetComponentInChildren<CharacterController>();
            if (cc)
            {
                float y = cc.transform.position.y + cc.center.y - cc.height * 0.5f;
                // Some VR rigs re-fit the avatar's child transform to a tracked HMD
                // height; without a live headset (e.g. desktop Editor testing) that
                // calibration can read a bogus sentinel and fling the child transform
                // far away. Guard against trusting a wildly-off result.
                if (Mathf.Abs(y - playerRoot.position.y) < 2f)
                    return y;
            }
            return playerRoot.position.y;
        }

        void SetMagnitude(float magnitude)
        {
            if (m_HandleRig)
                m_HandleRig.ApplyScale(magnitude);
            else if (m_SplatRoot)
                m_SplatRoot.localScale = Vector3.Scale(m_ScaleSign, Vector3.one * magnitude);
        }

        float CurrentMagnitude => m_SplatRoot ? Mathf.Abs(m_SplatRoot.localScale.x) : 1f;

        // Spawn anchor in splat-root local space (unscaled). TransformPoint applies
        // Vector3.Scale(localScale, v), so the world offset must use the same op —
        // not a scalar multiply on each component (breaks with mirror scale e.g. -1,1,1).
        Vector3 AnchorWorldOffset(Vector3 localAnchor, float mag) =>
            Vector3.Scale(Vector3.Scale(m_ScaleSign, Vector3.one * mag), localAnchor);

        Vector3 SplatPositionForAnchor(Vector3 footTarget, Quaternion rot, Vector3 localAnchor, float mag) =>
            footTarget - rot * AnchorWorldOffset(localAnchor, mag);

        // ---- state entry ----

        void EnterPlace()
        {
            CurrentState = State.Place;
            SetActive(m_PlaceModeHandles, true);
            SetActive(m_SpawnPointsGroup, true);
            // Avatar and gravity go away before the ground does, otherwise the
            // CharacterController spends a frame falling through the removed proxy.
            SetAvatar(false);
            SetActive(m_CollisionProxy, false);
            SetCutout(true);
            SetPassthrough(true);
            SetVignette(m_ApertureOpen);
            SetGardenAmbience(false);
        }

        void EnterImmersive()
        {
            CurrentState = State.Immersive;
            SetActive(m_PlaceModeHandles, false);
            SetActive(m_SpawnPointsGroup, false);
            SetCutout(false);
            SetPassthrough(false);
            // Ground first, then re-enable the body: the CharacterController must have
            // something to land on the very first frame gravity runs again.
            SetActive(m_CollisionProxy, true);
            Physics.SyncTransforms();
            SetAvatar(true);
            SnapCameraToFirstPerson();
            SetVignette(m_ApertureOpen);
            SetGardenAmbience(true);
        }

        // Re-enabling the locomotion state machine does not re-run Idle.Enter() when it was
        // already idle before Place mode disabled it — so SnapRigToAvatarHead never fires and
        // the HMD stays at the bird's-eye height while the body spawns at floor level.
        void SnapCameraToFirstPerson()
        {
            if (m_LocomotionComponent is not VRPlayerLocomotionStateMachine sm)
                return;
            sm.CameraRig?.SnapRigToAvatarHead();
            sm.CameraRig?.SetLocomotionState(false);
            sm.CameraRig?.SetAvatarFirstPersonVisibility(true);
            sm.AvatarHeadFollower?.EnableFollow();
        }

        // The avatar is the CharacterController + mesh; the locomotion state machine and
        // the root-motion controller are separate components that keep running (and keep
        // applying gravity) unless disabled explicitly.
        void SetAvatar(bool on)
        {
            if (m_AvatarRoot)
            {
                if (on)
                    PlaceAvatarAtRigFeet();
                m_AvatarRoot.SetActive(on);
            }
            SetLocomotion(on);
            if (m_RootMotionController)
                m_RootMotionController.enabled = on;
        }

        // Re-entering Immersive after a Return can leave the body wherever it was when we
        // switched it off, which may be nowhere near where the player is now standing.
        void PlaceAvatarAtRigFeet()
        {
            if (!m_AvatarRoot || !m_PlayerFeet)
                return;
            var cc = m_AvatarRoot.GetComponent<CharacterController>();
            bool hadController = cc && cc.enabled;
            if (hadController)
                cc.enabled = false; // CharacterController overrides direct transform writes
            m_AvatarRoot.transform.position = new Vector3(
                m_PlayerFeet.position.x, FootY(m_PlayerFeet), m_PlayerFeet.position.z);
            if (hadController)
                cc.enabled = true;
        }

        void SetActive(GameObject go, bool active) { if (go) go.SetActive(active); }

        void SetCutout(bool enabled) { if (m_Cutout) m_Cutout.enabled = enabled; }

        void SetPassthrough(bool on)
        {
            EnsureCameraData();

            // Meta OpenXR passthrough is driven by ARCameraManager only.
            // ARCameraBackground has no effect on Quest and can fight clearFlags on Link.
            if (m_ArCameraManager) m_ArCameraManager.enabled = on;
            if (m_ArCameraBackground) m_ArCameraBackground.enabled = false;

            if (m_XrCamera)
            {
                if (on)
                {
                    m_XrCamera.clearFlags = CameraClearFlags.SolidColor;
                    m_XrCamera.backgroundColor = Color.clear; // alpha 0 → compositor shows passthrough
                }
                else
                {
                    m_XrCamera.clearFlags = CameraClearFlags.Skybox;
                }
            }

            // URP post-processing writes opaque alpha and hides passthrough on Link/Quest.
            if (m_CameraData)
                m_CameraData.renderPostProcessing = !on && m_PostProcessingInImmersive;
        }

        void SetLocomotion(bool on) { if (m_LocomotionComponent) m_LocomotionComponent.enabled = on; }

        void SetVignette(float aperture)
        {
            if (!m_VignetteMaterialInstance)
                return;
            m_VignetteMaterialInstance.SetFloat(k_ApertureSize, aperture);
            m_VignetteMaterialInstance.SetFloat(k_Feathering, m_Feathering);
        }

        void PlayTransition()
        {
            var clip = ResolveClip(m_TransitionAudio);
            if (!clip)
                return;
            m_TransitionAudio.PlayOneShot(clip);
        }

        void SetGardenAmbience(bool on)
        {
            if (!m_GardenAmbience || !ResolveClip(m_GardenAmbience))
                return;
            if (on)
            {
                if (!m_GardenAmbience.isPlaying)
                    m_GardenAmbience.Play();
            }
            else
            {
                m_GardenAmbience.Stop();
            }
        }

        static AudioClip ResolveClip(AudioSource source)
        {
            if (!source)
                return null;
            if (source.clip)
                return source.clip;
            return source.resource as AudioClip;
        }

        // ---- dive / return ----

        // Captured at Dive() so Return can restore the exact tabletop pose the player set
        // (world position on a real desk, scale, and the spawn used as the scale pivot).
        float m_PreDiveMagnitude;
        Vector3 m_PreDiveSplatWorldPos;
        Quaternion m_PreDiveSplatWorldRot;
        Vector3 m_PreDiveSpawnWorldPos;
        Vector3 m_DiveLocalAnchor; // spawn in splat-local (unscaled) space; fixed for Dive+Return

        public void Dive(SplatSpawnPoint point)
        {
            if (CurrentState != State.Place || !point || !m_SplatRoot)
                return;
            if (m_Routine != null)
                StopCoroutine(m_Routine);
            m_PreDiveMagnitude = CurrentMagnitude;
            m_PreDiveSplatWorldPos = m_SplatRoot.position;
            m_PreDiveSplatWorldRot = m_SplatRoot.rotation;
            m_PreDiveSpawnWorldPos = point.transform.position;
            m_Routine = StartCoroutine(DiveRoutine(m_PreDiveSpawnWorldPos));
        }

        public void Return()
        {
            if (CurrentState != State.Immersive)
                return;
            if (m_Routine != null)
                StopCoroutine(m_Routine);
            m_Routine = StartCoroutine(ReturnRoutine());
        }

        IEnumerator DiveRoutine(Vector3 spawnWorldPos)
        {
            CurrentState = State.Diving;
            PlayTransition();
            SetActive(m_PlaceModeHandles, false);
            SetActive(m_SpawnPointsGroup, false);

            float startMag = Mathf.Max(m_PreDiveMagnitude, 1e-4f);
            // Capture while still under the rig, then detach without changing world pose.
            m_DiveLocalAnchor = m_SplatRoot.InverseTransformPoint(spawnWorldPos);
            Quaternion rot = m_SplatRoot.rotation;
            m_SplatRoot.SetParent(null, true);

            // Frame 0 must keep the tabletop pose — pinning the spawn to the player's feet
            // immediately teleports the miniature off the desk (the "flash" users see).
            Vector3 anchorStart = spawnWorldPos;

            bool flipped = false;
            float t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime / Mathf.Max(0.01f, m_DiveDuration);
                float tc = Mathf.Clamp01(t);
                float ease = tc * tc * (3f - 2f * tc);

                float mag = Mathf.Exp(Mathf.Lerp(Mathf.Log(startMag), 0f, ease)); // log(1)=0
                Vector3 footTarget = new Vector3(m_PlayerFeet.position.x, FootY(m_PlayerFeet), m_PlayerFeet.position.z);
                // Spawn drifts from its desk position to under the player's feet while growing.
                Vector3 anchor = Vector3.Lerp(anchorStart, footTarget, ease);

                m_SplatRoot.localScale = Vector3.Scale(m_ScaleSign, Vector3.one * mag);
                m_SplatRoot.rotation = rot;
                m_SplatRoot.position = SplatPositionForAnchor(anchor, rot, m_DiveLocalAnchor, mag);

                float bell = 1f - Mathf.Abs(ease - 0.5f) * 2f;
                SetVignette(Mathf.Lerp(m_ApertureOpen, m_ApertureClosedMin, bell));

                if (!flipped && ease >= 0.5f)
                {
                    flipped = true;
                    SetCutout(false);
                    SetPassthrough(false);
                }

                yield return null;
            }

            Vector3 finalFoot = new Vector3(m_PlayerFeet.position.x, FootY(m_PlayerFeet), m_PlayerFeet.position.z);
            m_SplatRoot.localScale = m_ScaleSign;
            m_SplatRoot.rotation = rot;
            m_SplatRoot.position = SplatPositionForAnchor(finalFoot, rot, m_DiveLocalAnchor, 1f);
            SetVignette(m_ApertureOpen);
            EnterImmersive();
            m_Routine = null;
        }

        IEnumerator ReturnRoutine()
        {
            CurrentState = State.Returning;
            PlayTransition();
            SetGardenAmbience(false);
            SetAvatar(false);
            SetActive(m_CollisionProxy, false);

            float targetMag = m_PreDiveMagnitude > 0f ? m_PreDiveMagnitude : m_DefaultTableScale;
            if (m_ScaleHandle)
                targetMag = Mathf.Clamp(targetMag, m_ScaleHandle.m_MinScale, m_ScaleHandle.m_MaxScale);
            targetMag = Mathf.Max(targetMag, 1e-4f);

            // Shrink back toward the desk pose captured at Dive. During Immersive the splat
            // stays fixed in the room, so the live spawn world pos is TransformPoint(local) —
            // NOT the player's current feet (they may have walked away).
            Quaternion rot = m_PreDiveSplatWorldRot;
            Vector3 anchorStart = m_SplatRoot.TransformPoint(m_DiveLocalAnchor);
            Vector3 anchorEnd = m_PreDiveSpawnWorldPos;
            Vector3 endPos = m_PreDiveSplatWorldPos;
            Quaternion endRot = m_PreDiveSplatWorldRot;

            float t = 0f;
            bool flipped = false;
            while (t < 1f)
            {
                t += Time.deltaTime / Mathf.Max(0.01f, m_DiveDuration);
                float tc = Mathf.Clamp01(t);
                float ease = tc * tc * (3f - 2f * tc);

                float mag = Mathf.Exp(Mathf.Lerp(0f, Mathf.Log(targetMag), ease));
                Vector3 anchor = Vector3.Lerp(anchorStart, anchorEnd, ease);

                m_SplatRoot.localScale = Vector3.Scale(m_ScaleSign, Vector3.one * mag);
                m_SplatRoot.rotation = rot;
                m_SplatRoot.position = SplatPositionForAnchor(anchor, rot, m_DiveLocalAnchor, mag);

                float bell = 1f - Mathf.Abs(ease - 0.5f) * 2f;
                SetVignette(Mathf.Lerp(m_ApertureOpen, m_ApertureClosedMin, bell));

                if (!flipped && ease >= 0.5f)
                {
                    flipped = true;
                    SetCutout(true);
                    SetPassthrough(true);
                }

                yield return null;
            }

            // Exact restore of the desk pose, then re-parent under the (unmoved) Place rig.
            m_SplatRoot.SetParent(null, true);
            m_SplatRoot.SetPositionAndRotation(endPos, endRot);
            m_SplatRoot.localScale = Vector3.Scale(m_ScaleSign, Vector3.one * targetMag);
            if (m_Rig)
                m_SplatRoot.SetParent(m_Rig, true);

            if (m_HandleRig)
                m_HandleRig.SnapToScale(targetMag);

            SetVignette(m_ApertureOpen);
            EnterPlace();
            m_Routine = null;
        }
    }
}
