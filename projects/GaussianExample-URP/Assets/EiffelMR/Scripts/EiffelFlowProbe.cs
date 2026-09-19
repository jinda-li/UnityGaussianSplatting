using System.Collections;
using System.Text;
using UnityEngine;

namespace EiffelMR
{
    // Drives the whole interaction once, in Play mode, with no headset.
    //
    // The demo's four steps - bubble, poke, throw, arrive - are exactly the
    // steps that normally need a Quest, a room and two hands to reach, which
    // means the part most worth testing is the part hardest to test. This walks
    // the same code paths the player does: it calls TowerBubble.Pop the way a
    // finger does, and ThrownTower.Throw with a velocity aimed at the ring the
    // way a hand does. What it does NOT test is hand tracking, the poke
    // collision itself, or anything about how it looks in passthrough.
    //
    // Add it to the scene, tick m_RunOnStart, press Play, read the [flow] lines.
    public class EiffelFlowProbe : MonoBehaviour
    {
        public EiffelBubbleSession m_Session;
        public TowerBubble m_Bubble;
        public ThrownTower m_Tower;
        public LandingRing m_Ring;
        public HexSkyReveal m_Reveal;
        public EiffelWorld m_World;

        [Tooltip("Run the sequence as soon as Play starts.")]
        public bool m_RunOnStart = true;

        [Tooltip("Seconds to hold at each step before checking.")]
        public float m_Settle = 0.6f;

        [Tooltip("Time of flight the aimed throw is solved for.")]
        public float m_FlightTime = 0.9f;

        [Tooltip("Seconds to stay in the arrived world before the reset, so " +
                 "there is time to look (or to take a screenshot).")]
        public float m_HoldImmersive;

        /// Fired once the world has arrived, before the hold.
        public event System.Action ImmersiveReached;

        public bool Passed { get; private set; }
        public string Report { get; private set; } = "";

        readonly StringBuilder m_Log = new StringBuilder();
        int m_Failures;

        void Awake()
        {
            if (!m_Session) m_Session = FindFirstObjectByType<EiffelBubbleSession>();
            if (m_Session)
            {
                if (!m_Bubble) m_Bubble = m_Session.m_Bubble;
                if (!m_Tower) m_Tower = m_Session.m_Tower;
                if (!m_Ring) m_Ring = m_Session.m_Ring;
                if (!m_Reveal) m_Reveal = m_Session.m_Reveal;
                if (!m_World) m_World = m_Session.m_World;
            }
        }

        void Start()
        {
            if (m_RunOnStart)
                StartCoroutine(Run());
        }

        // Two of the checks are about the splat growing, and until an Eiffel
        // splat is trained there is no splat to grow. Reporting those as
        // failures would mean the probe cries wolf on every run until the
        // asset exists, and a test that is expected to fail stops being read.
        // They are skipped instead - and the moment a renderer is assigned
        // they become real checks again with no edit here.
        // Deliberately the RENDERER, not the handle rig. The builder assigns
        // the rig whether or not there is a splat in it, so keying off the rig
        // reported "there is a splat" for an empty one and turned the skip back
        // into a failure - measuring the presence of the thing that holds the
        // splat rather than the splat.
        bool HasSplat => m_World && m_World.CanGrow;

        void Skip(string what, string why)
        {
            string line = "[flow] SKIP " + what + " | " + why;
            m_Log.AppendLine(line);
            Debug.Log(line);
        }

        void Check(string what, bool ok, string detail = "")
        {
            if (!ok)
                m_Failures++;
            string line = string.Format("[flow] {0} {1}{2}", ok ? "PASS" : "FAIL",
                                        what,
                                        string.IsNullOrEmpty(detail) ? "" : " | " + detail);
            m_Log.AppendLine(line);
            if (ok)
                Debug.Log(line);
            else
                Debug.LogWarning(line);
        }

        public IEnumerator Run()
        {
            m_Failures = 0;
            m_Log.Length = 0;

            if (!m_Session || !m_Bubble || !m_Tower || !m_Ring)
            {
                Check("scene wired", false, "session/bubble/tower/ring missing");
                Finish();
                yield break;
            }

            // 1. Idle -> Bubble
            Check("starts idle",
                  m_Session.Current == EiffelBubbleSession.State.Idle,
                  "state=" + m_Session.Current);
            m_Session.SpawnBubble();
            yield return new WaitForSeconds(m_Settle);
            Check("button spawns a bubble",
                  m_Session.Current == EiffelBubbleSession.State.Bubble,
                  "state=" + m_Session.Current);
            Check("bubble is intact", !m_Bubble.IsPopped);

            // 2. Poke -> pop. The shell arms itself a moment after spawning so a
            // finger already inside it cannot pop it on the first frame, so wait
            // that out rather than racing it.
            yield return new WaitForSeconds(Mathf.Max(m_Bubble.m_ArmDelay, 0f) + 0.1f);
            m_Bubble.Pop();
            yield return new WaitForSeconds(Mathf.Max(m_Bubble.m_PopDuration, 0f) + m_Settle);
            Check("poke pops the bubble", m_Bubble.IsPopped);
            var film = m_Bubble.m_Shell ? m_Bubble.m_Shell : m_Bubble.GetComponent<Renderer>();
            Check("the film is gone after the pop", film && !film.enabled,
                  film ? "shell enabled=" + film.enabled : "no shell renderer");
            Check("miniature comes loose",
                  m_Session.Current == EiffelBubbleSession.State.Loose,
                  "state=" + m_Session.Current);

            var body = m_Tower.GetComponent<Rigidbody>();
            Check("miniature is a physics object",
                  body != null && !body.isKinematic,
                  body == null ? "no Rigidbody" : "kinematic=" + body.isKinematic);

            // 3. A throw that misses is left to physics.
            Vector3 wide = m_Tower.AimAtRing(m_FlightTime)
                           + m_Ring.transform.right * 6f;
            bool tookOverMiss = m_Tower.Throw(wide);
            Check("a wide throw is left to physics", !tookOverMiss);

            // 4. A throw that will land inside the ring is taken over, grows the
            // miniature along the arc, and hands it to the dive on landing.
            float before = ScaleNow();
            bool landed = false;
            m_Tower.Landed += _ => landed = true;

            Vector3 aimed = m_Tower.AimAtRing(m_FlightTime);
            bool tookOver = m_Tower.Throw(aimed);
            Check("an aimed throw is taken over", tookOver,
                  "v=" + aimed.ToString("F2"));

            if (tookOver)
            {
                yield return new WaitForSeconds(m_FlightTime * 0.5f);
                float mid = ScaleNow();
                if (HasSplat)
                    Check("it grows on the way", mid > before * 1.05f,
                          string.Format("{0:G4} -> {1:G4}", before, mid));
                else
                    Skip("it grows on the way", "no splat assigned, nothing to scale");

                float deadline = Time.time + m_FlightTime + 2f;
                while (!landed && Time.time < deadline)
                    yield return null;
                Check("it lands", landed);

                Check("it lands inside the ring",
                      m_Ring.Contains(m_Tower.transform.position),
                      "d=" + m_Ring.HorizontalDistance(m_Tower.transform.position)
                                  .ToString("F2") + "m r=" + m_Ring.m_Radius);

                if (m_Reveal)
                {
                    yield return new WaitForSeconds(0.2f);
                    Check("the sky starts filling in", m_Reveal.Reveal > 0f,
                          "reveal=" + m_Reveal.Reveal.ToString("F3"));
                }

                if (m_World && !HasSplat)
                {
                    Skip("the world grows around the player",
                         "no splat assigned, Dive() has no world to grow");
                    Check("it ends immersive",
                          m_Session.Current == EiffelBubbleSession.State.Immersive,
                          "state=" + m_Session.Current);
                }
                else if (m_World)
                {
                    float diveDeadline = Time.time + m_Tower.m_DiveDuration + 3f;
                    while (m_World.InPlace && Time.time < diveDeadline)
                        yield return null;
                    Check("the world grows around the player", !m_World.InPlace);

                    while (m_World.IsBusy && Time.time < diveDeadline + 3f)
                        yield return null;
                    Check("the world arrives at 1:1",
                          m_World.IsImmersive && Mathf.Abs(m_World.MeasuredScale - 1f) < 0.02f,
                          "scale=" + m_World.MeasuredScale.ToString("G4"));
                    Check("it ends immersive",
                          m_Session.Current == EiffelBubbleSession.State.Immersive,
                          "state=" + m_Session.Current);
                    ImmersiveReached?.Invoke();
                    if (m_HoldImmersive > 0f)
                        yield return new WaitForSeconds(m_HoldImmersive);
                }
            }

            // 5. The button always gets you back to a bubble.
            m_Session.ResetToBubble();
            float resetDeadline = Time.time + 8f;
            while (m_Session.Current != EiffelBubbleSession.State.Bubble
                   && Time.time < resetDeadline)
                yield return null;
            Check("the button returns to a fresh bubble",
                  m_Session.Current == EiffelBubbleSession.State.Bubble,
                  "state=" + m_Session.Current);
            Check("the returned bubble is intact", !m_Bubble.IsPopped);
            if (HasSplat)
                Check("the world is a miniature again",
                      m_World.InPlace
                      && Mathf.Abs(m_World.MeasuredScale - m_World.TableScale)
                         < m_World.TableScale * 0.05f,
                      "scale=" + m_World.MeasuredScale.ToString("G4"));

            Finish();
        }

        float ScaleNow()
        {
            if (m_World && m_World.CanGrow)
                return m_World.MeasuredScale;
            // Placeholder case: the box is its own model.
            return m_Tower ? m_Tower.transform.localScale.x : 0f;
        }

        void Finish()
        {
            Passed = m_Failures == 0;
            Report = m_Log.ToString();
            Debug.Log(string.Format("[flow] {0} - {1} check(s) failed",
                                    Passed ? "ALL PASS" : "FAILURES", m_Failures));
        }
    }
}
