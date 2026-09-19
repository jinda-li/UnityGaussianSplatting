using System.Collections;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using GardenMR;

namespace EiffelMR
{
    // The miniature is the scene. There is one tower, and the throw magnifies it.
    //
    // This is the whole design and it is worth being explicit about, because the
    // obvious implementation is wrong: a small model of a tower that you throw,
    // which is then replaced by a big photoreal tower, is two objects and a cut.
    // The player sees the swap - the shading changes, the silhouette shifts, and
    // the thing they were holding turns out not to have been the thing they
    // arrived in.
    //
    // So what sits in the bubble is the splat itself, at TabletopDiveController's
    // table scale (0.0009 for a 324 m tower, which is 29 cm in the hand), with a
    // GaussianCutout hiding everything that is not the tower. Throwing it grows
    // that same splat along the arc, and the landing hands the same splat to
    // Dive(), which carries on growing it to 1:1 and pins the spawn point under
    // the player's feet. Nothing is ever swapped. The cutout opening up on
    // landing is what makes the rest of the park appear around the tower that
    // was already in their hand.
    //
    // Physics owns the rig between the bubble popping and the throw, so the
    // miniature can be dropped, caught out of the air and picked up off the
    // floor. A scripted arc only takes over for a throw that is going to land in
    // the ring, because that is the only case whose path has to be exact.
    // A throw that misses is left alone: it bounces, it rolls, you go and get it.
    [RequireComponent(typeof(XRGrabInteractable))]
    [RequireComponent(typeof(Rigidbody))]
    public class ThrownTower : MonoBehaviour
    {
        [Header("References")]
        public LandingRing m_Ring;
        public HexSkyReveal m_Reveal;

        [Tooltip("What this is a miniature of: the trained splat (SplatDiveWorld) " +
                 "or the exported meshes (MeshWorld). Scaled along the throw, " +
                 "condensed from points to solid, and grown into on landing.")]
        public EiffelWorld m_World;

        [Header("Throw")]
        [Tooltip("Release speed below this is putting it down, not throwing it.")]
        public float m_MinThrowSpeed = 1.1f;

        [Tooltip("Frames of motion averaged into the release velocity. One frame " +
                 "of finite difference is far too noisy to throw with.")]
        public int m_VelocitySamples = 5;

        public float m_Gravity = 9.81f;
        public float m_MaxFlightTime = 3f;

        [Tooltip("Spin while airborne, degrees per second, scaled by throw speed.")]
        public float m_TumbleRate = 40f;

        [Header("Growth along the arc")]
        [Tooltip("How much bigger the splat gets between leaving the hand and " +
                 "touching down, as a multiple of the table scale. Dive() takes " +
                 "it the rest of the way - from 0.0009 to 1 is over a thousand " +
                 "times, and doing all of that after the landing reads as the " +
                 "world lurching rather than as the tower growing.")]
        public float m_FlightGrowth = 9f;

        [Tooltip("Growth along the flight. Late growth reads better than linear: " +
                 "the tower stays small long enough to follow, then swells into " +
                 "the ring.")]
        public AnimationCurve m_GrowthCurve = new AnimationCurve(
            new Keyframe(0f, 0f, 0f, 0.4f), new Keyframe(1f, 1f, 2.2f, 0f));

        [Tooltip("Fraction of the flight over which the cloud becomes the solid " +
                 "tower. Below 1 it finishes before the landing.")]
        [Range(0.2f, 1f)] public float m_SolidifyBy = 0.75f;

        [Header("Dive")]
        public float m_DiveDuration = 2.2f;

        [Tooltip("Pause on the ground before the world starts growing, so the " +
                 "landing registers as a landing.")]
        public float m_LandingHold = 0.12f;

        public event System.Action<Vector3> Landed;
        public event System.Action Missed;

        XRGrabInteractable m_Interactable;
        Rigidbody m_Body;
        Vector3[] m_Samples;
        float[] m_SampleTimes;
        int m_SampleCount;
        bool m_Held;
        Coroutine m_Flight;

        void Awake()
        {
            m_Interactable = GetComponent<XRGrabInteractable>();
            m_Body = GetComponent<Rigidbody>();
            m_Samples = new Vector3[Mathf.Max(2, m_VelocitySamples)];
            m_SampleTimes = new float[m_Samples.Length];
            if (!m_World)
                m_World = FindFirstObjectByType<EiffelWorld>();
        }

        float TableScale => m_World ? m_World.TableScale : 0.0009f;

        /// Move the miniature while it is under script control (in the bubble,
        /// held on a desktop, in flight).
        ///
        /// Through the Rigidbody as well as the Transform, not the Transform
        /// alone. The body interpolates, and an interpolated body writes its
        /// own pose back over the Transform every frame - so a Transform-only
        /// move is undone on the next frame. That is how the miniature ended
        /// up at the room origin instead of in front of the player, and how a
        /// throw could look like it never left the hand while every check on
        /// transform.position still passed.
        public void Teleport(Vector3 position, Quaternion rotation)
        {
            if (!m_Body)
                m_Body = GetComponent<Rigidbody>();
            if (m_Body)
            {
                m_Body.position = position;
                m_Body.rotation = rotation;
            }
            transform.SetPositionAndRotation(position, rotation);
        }

        void ApplyScale(float magnitude)
        {
            if (m_World)
                m_World.SetMiniatureScale(magnitude);
        }

        void SetSolidify(float amount)
        {
            if (m_World)
                m_World.SetSolidify(amount);
        }

        /// Put the world back to the size it is inside the bubble.
        public void ResetToMiniature()
        {
            if (m_Flight != null)
            {
                StopCoroutine(m_Flight);
                m_Flight = null;
                if (m_World)
                    m_World.EndFlight();
            }
            if (m_World)
                m_World.ResetToTable();
            if (m_Body)
            {
                m_Body.linearVelocity = Vector3.zero;
                m_Body.angularVelocity = Vector3.zero;
            }
        }

        void OnEnable()
        {
            m_Interactable.selectEntered.AddListener(OnGrabbed);
            m_Interactable.selectExited.AddListener(OnReleased);
            if (m_Ring)
                m_Ring.Watch(transform);
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
                // Catching it out of the air cancels the throw. Put the size
                // back too, or the player is left holding a half-grown tower
                // and the dive controller stays locked.
                StopCoroutine(m_Flight);
                m_Flight = null;
                if (m_World)
                    m_World.CancelFlight();
            }
        }

        void LateUpdate()
        {
            if (!m_Held)
                return;
            int i = m_SampleCount % m_Samples.Length;
            m_Samples[i] = transform.position;
            m_SampleTimes[i] = Time.time;
            m_SampleCount++;
        }

        void OnReleased(SelectExitEventArgs args)
        {
            m_Held = false;
            Throw(ReleaseVelocity());
        }

        /// Release the miniature at this velocity. Returns true if the throw
        /// was taken over by the scripted arc - that is, if it is going to land
        /// in the ring. Everything else is left to physics on purpose.
        ///
        /// Public so the flow can be driven without a headset: without this the
        /// only way into the throw is an XRGrabInteractable event, and the one
        /// part of this demo most worth testing is the part that needs a room,
        /// two hands and a Quest to reach.
        public bool Throw(Vector3 velocity)
        {
            if (velocity.magnitude < m_MinThrowSpeed || !m_Ring)
                return false;
            if (m_World && !m_World.InPlace)
                return false;

            if (!m_Ring.PredictLanding(transform.position, velocity, m_Gravity,
                                       out Vector3 impact, out float flight))
                return false;

            if (!m_Ring.Contains(impact))
            {
                Missed?.Invoke();
                return false;
            }

            m_Flight = StartCoroutine(FlightRoutine(velocity, flight));
            return true;
        }

        /// The velocity that would drop the miniature into the centre of the
        /// ring from where it is now, given a time of flight. The test harness
        /// throws with this; a player aims by hand.
        public Vector3 AimAtRing(float flightTime = 0.9f)
        {
            if (!m_Ring)
                return Vector3.zero;
            Vector3 from = transform.position;
            Vector3 to = m_Ring.transform.position;
            to.y = m_Ring.m_FloorY;
            Vector3 flat = new Vector3(to.x - from.x, 0f, to.z - from.z);
            float t = Mathf.Max(flightTime, 0.05f);
            // y = y0 + v*t - g*t^2/2, solved for v.
            float vy = ((to.y - from.y) + 0.5f * m_Gravity * t * t) / t;
            return flat / t + Vector3.up * vy;
        }

        // Oldest-to-newest secant over the ring buffer. Averaging the whole
        // window rather than differencing the last two frames keeps a hand
        // tremor at the moment of release from deciding where the throw goes.
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

        IEnumerator FlightRoutine(Vector3 velocity, float flightTime)
        {
            if (m_World)
                m_World.BeginFlight();

            m_Body.isKinematic = true;

            float start = TableScale;
            float end = start * Mathf.Max(m_FlightGrowth, 1f);
            Vector3 pos = transform.position;
            Vector3 spin = Random.onUnitSphere * (m_TumbleRate * Mathf.Min(velocity.magnitude, 4f));
            float total = Mathf.Max(flightTime, 0.05f);

            float t = 0f;
            while (t < Mathf.Min(total, m_MaxFlightTime))
            {
                float dt = Time.deltaTime;
                velocity += Vector3.down * (m_Gravity * dt);
                pos += velocity * dt;
                t += dt;

                Teleport(pos, Quaternion.Euler(spin * dt) * transform.rotation);
                // Exponential, not linear: scale is perceived logarithmically,
                // and lerping it makes the tower appear to stall near the end.
                float k = m_GrowthCurve.Evaluate(Mathf.Clamp01(t / total));
                ApplyScale(Mathf.Exp(Mathf.Lerp(Mathf.Log(start), Mathf.Log(end), k)));
                // Condense while it grows. Ahead of the size curve on purpose:
                // the tower wants to be solid by the time it touches down, and
                // the last of the cloud catching up mid-air reads as the thing
                // pulling itself together rather than as an effect ending.
                SetSolidify(Mathf.Clamp01(t / (total * m_SolidifyBy)));
                yield return null;
            }

            // Settle upright: Dive()'s own maths assumes the miniature is not
            // lying on its side when the world starts to grow out of it.
            pos.y = m_Ring.m_FloorY;
            Teleport(pos, Quaternion.Euler(0f, transform.rotation.eulerAngles.y, 0f));
            ApplyScale(end);
            SetSolidify(1f);
            // Stays kinematic from here. The world is about to grow around this
            // by three orders of magnitude, and a dynamic body with a collider
            // that large tunnels through the floor on the first physics step.
            m_Body.isKinematic = true;

            Landed?.Invoke(pos);
            if (m_Reveal)
            {
                m_Reveal.m_Duration = m_DiveDuration;
                m_Reveal.Play(pos);
            }

            if (m_LandingHold > 0f)
                yield return new WaitForSeconds(m_LandingHold);

            if (m_World)
            {
                m_World.EndFlight();
                // Both worlds start their growth from the scale the arc left
                // the miniature at, so the growth in flight carries straight on
                // into the growth of the world - one continuous magnification
                // rather than a jump back to table scale.
                m_World.Arrive(pos, m_DiveDuration);
            }
            m_Flight = null;
        }
    }
}
