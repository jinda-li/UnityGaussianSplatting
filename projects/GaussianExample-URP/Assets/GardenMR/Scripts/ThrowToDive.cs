using System.Collections;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace GardenMR
{
    // "I put the Eiffel Tower in my hand": hold the miniature, throw it, and the world it
    // lands on grows around you until you are standing at the foot of the real thing.
    //
    // This is only a new way to *trigger* the existing dive. TabletopDiveController already
    // does the hard part - anchored logarithmic growth, the tunnelling vignette, and the hard
    // cutout/passthrough/skybox flip hidden at the narrowest aperture. All that changes here is
    // that the trigger is a release instead of a ray plus a trigger pull, and that the dive
    // starts from wherever the miniature came to rest rather than from the tabletop.
    //
    // Where the player ends up is decided by the spawn point, not by this script: Dive() pins
    // the spawn's splat-local position under the player's feet, so authoring the spawn at, say,
    // (66, -66, 1.6) in the Eiffel asset puts them on the esplanade looking up rather than
    // inside the tower's own footprint.
    [RequireComponent(typeof(XRGrabInteractable))]
    public class ThrowToDive : MonoBehaviour
    {
        [Header("References")]
        public TabletopDiveController m_Controller;

        [Tooltip("Transform that actually flies - normally the rig the grab interactable moves.")]
        public Transform m_Rig;

        [Tooltip("Spawn point whose splat-local position becomes the player's standing spot at 1:1.")]
        public SplatSpawnPoint m_LandingSpawn;

        [Header("Throw")]
        [Tooltip("Release speed below this counts as putting the miniature down, not throwing it.")]
        public float m_MinThrowSpeed = 1.2f;

        [Tooltip("Frames of motion averaged into the release velocity. One frame of finite " +
                 "difference is far too noisy to throw with.")]
        public int m_VelocitySamples = 5;

        public float m_Gravity = 9.81f;

        [Tooltip("Height the miniature lands on, in tracking-space metres above the XR Origin.")]
        public float m_FloorHeight;

        [Tooltip("Safety net in case the arc never reaches the floor.")]
        public float m_MaxFlightTime = 2.5f;

        [Tooltip("Spin while airborne, degrees per second, scaled by throw speed.")]
        public float m_TumbleRate = 45f;

        [Header("Dive")]
        [Tooltip("Dive length while throwing. The tabletop dive grows the world about 50x; this " +
                 "one grows it more than a thousand times, and packing that into the same second " +
                 "reads as a lurch. Leave at 0 to use the controller's own duration.")]
        public float m_DiveDuration = 2.2f;

        [Tooltip("Pause on the ground before the world starts growing, so the landing registers.")]
        public float m_LandingHold = 0.15f;

        XRGrabInteractable m_Interactable;
        Vector3[] m_Samples;
        float[] m_SampleTimes;
        int m_SampleCount;
        Coroutine m_Flight;
        bool m_Held;

        void Awake()
        {
            m_Interactable = GetComponent<XRGrabInteractable>();
            if (!m_Rig)
                m_Rig = transform;
            if (!m_Controller)
                m_Controller = Object.FindFirstObjectByType<TabletopDiveController>();
            m_Samples = new Vector3[Mathf.Max(2, m_VelocitySamples)];
            m_SampleTimes = new float[m_Samples.Length];
        }

        void OnEnable()
        {
            m_Interactable.selectEntered.AddListener(OnGrabbed);
            m_Interactable.selectExited.AddListener(OnReleased);
        }

        void OnDisable()
        {
            m_Interactable.selectEntered.RemoveListener(OnGrabbed);
            m_Interactable.selectExited.RemoveListener(OnReleased);
        }

        void OnGrabbed(SelectEnterEventArgs args)
        {
            m_Held = true;
            m_SampleCount = 0;
            if (m_Flight != null)
            {
                // Catching the miniature out of the air cancels the throw, so undo what the
                // flight had already changed rather than leaving the controller locked.
                StopCoroutine(m_Flight);
                m_Flight = null;
                if (m_Controller)
                    m_Controller.EndExclusiveTransition();
            }
        }

        void LateUpdate()
        {
            if (!m_Held || !m_Rig)
                return;
            int i = m_SampleCount % m_Samples.Length;
            m_Samples[i] = m_Rig.position;
            m_SampleTimes[i] = Time.time;
            m_SampleCount++;
        }

        void OnReleased(SelectExitEventArgs args)
        {
            m_Held = false;
            Vector3 velocity = ReleaseVelocity();
            if (velocity.magnitude < m_MinThrowSpeed || !m_Controller || !m_Rig)
                return;
            if (m_Controller.CurrentState != TabletopDiveController.State.Place)
                return;
            m_Flight = StartCoroutine(FlightRoutine(velocity));
        }

        // Oldest-to-newest secant over the ring buffer. Averaging the whole window rather than
        // differencing the last two frames keeps a hand tremor at the moment of release from
        // dominating the direction of the throw.
        Vector3 ReleaseVelocity()
        {
            int n = Mathf.Min(m_SampleCount, m_Samples.Length);
            if (n < 2)
                return Vector3.zero;
            int newest = (m_SampleCount - 1) % m_Samples.Length;
            int oldest = (m_SampleCount - n) % m_Samples.Length;
            float dt = m_SampleTimes[newest] - m_SampleTimes[oldest];
            if (dt <= 1e-4f)
                return Vector3.zero;
            return (m_Samples[newest] - m_Samples[oldest]) / dt;
        }

        IEnumerator FlightRoutine(Vector3 velocity)
        {
            m_Controller.SetRigGrabEnabled(false);
            // The state is still Place while the miniature is in the air, so without this a
            // Summon or an environment switch could fire mid-flight. Released again just
            // before Dive(), which refuses to start while the lock is held.
            m_Controller.BeginExclusiveTransition();

            // The rig carries a Rigidbody for the grab interactable. The arc below is driven
            // by transform, so pin the body kinematic for the flight rather than let physics
            // pull against it - and put it back afterwards, since the interactable restores
            // its own kinematic state on the next grab.
            var body = m_Rig.GetComponent<Rigidbody>();
            bool wasKinematic = body && body.isKinematic;
            if (body)
                body.isKinematic = true;

            Vector3 pos = m_Rig.position;
            Vector3 spin = Random.onUnitSphere * (m_TumbleRate * Mathf.Min(velocity.magnitude, 4f));
            float floor = m_FloorHeight;
            if (m_Controller.m_XrOrigin)
                floor += m_Controller.m_XrOrigin.position.y;

            float t = 0f;
            while (t < m_MaxFlightTime && pos.y > floor)
            {
                float dt = Time.deltaTime;
                velocity += Vector3.down * (m_Gravity * dt);
                pos += velocity * dt;
                m_Rig.position = pos;
                m_Rig.Rotate(spin * dt, Space.World);
                t += dt;
                yield return null;
            }

            // Settle upright on the floor: the dive's own maths assumes the miniature is not
            // lying on its side when the world starts to grow out of it.
            pos.y = floor;
            m_Rig.position = pos;
            Vector3 euler = m_Rig.rotation.eulerAngles;
            m_Rig.rotation = Quaternion.Euler(0f, euler.y, 0f);

            if (body)
                body.isKinematic = wasKinematic;

            if (m_LandingHold > 0f)
                yield return new WaitForSeconds(m_LandingHold);

            if (m_DiveDuration > 0f)
                m_Controller.m_DiveDuration = m_DiveDuration;
            m_Controller.EndExclusiveTransition();
            m_Controller.Dive(m_LandingSpawn);
            m_Flight = null;
        }
    }
}
