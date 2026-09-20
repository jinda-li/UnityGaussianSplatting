using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Trogir
{
    // Walks the player through the splat on its own, so the collision mesh can be
    // checked without a headset.
    //
    // A splat scene has no geometry of its own: what the player stands on is a
    // voxelised proxy mesh, and the two agree only if the proxy was generated in
    // the frame the renderer is actually drawn in. That is the part worth
    // testing, and it is invisible in the Scene view because the proxy has no
    // material. So this drives the CharacterController down a route of waypoints
    // under gravity and records, per sample, the floor height under the capsule
    // and whether a wall stopped it. Falling through the floor shows up as a
    // fallen flag; a proxy that is offset from the splat shows up as a floor
    // height that does not match the eye height in the screenshot.
    //
    // What it does NOT test: the locomotion state machine, hand IK, or anything
    // driven by controller input. It moves the capsule directly.
    public class TrogirWalkProbe : MonoBehaviour
    {
        [Tooltip("The capsule that is moved. Found on the player prefab if left empty.")]
        public CharacterController m_Body;

        [Tooltip("Camera the screenshots are taken from. Found under the player if left empty.")]
        public Camera m_Camera;

        [Tooltip("Route in world space. The probe walks from one to the next.")]
        public Vector3[] m_Waypoints;

        public float m_Speed = 1.4f;
        public float m_Gravity = -9.81f;

        [Tooltip("Give up on a waypoint after this long and report it as blocked.")]
        public float m_WaypointTimeout = 20f;

        [Tooltip("Below this the player is treated as having fallen out of the scene.")]
        public float m_FallenBelowY = -5f;

        [Tooltip("Folder the PNGs and the report are written to.")]
        public string m_OutputDir = "Assets/Screenshots/Trogir";

        public int m_ShotWidth = 1280;
        public int m_ShotHeight = 720;

        public bool m_RunOnStart = true;

        [Tooltip("Switch off the player's own scripts so nothing else writes the " +
                 "transform while the probe walks it.")]
        public bool m_TakeOverPlayer = true;

        public bool Done { get; private set; }
        public bool Passed { get; private set; }
        public string Report { get; private set; } = "";

        readonly StringBuilder m_Log = new StringBuilder();
        int m_Failures;
        Behaviour[] m_SplatRenderers = System.Array.Empty<Behaviour>();

        void Awake()
        {
            if (!m_Body) m_Body = FindFirstObjectByType<CharacterController>();
            if (!m_Camera) m_Camera = Camera.main;
            if (!m_Camera) m_Camera = FindFirstObjectByType<Camera>();
            m_SplatRenderers = FindObjectsByType<GaussianSplatting.Runtime.GaussianSplatRenderer>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        }

        void Start()
        {
            if (m_TakeOverPlayer)
                TakeOverPlayer();
            if (m_RunOnStart)
                StartCoroutine(Run());
        }

        // The player prefab drives its own transform - VRIK, the rig controller,
        // root motion off the animator. With a headset attached that is the point;
        // here it means the capsule is put back every frame and the probe's Move
        // looks like a collision that never resolves. So everything on the player
        // except the capsule is switched off, and the probe is the only thing
        // moving it. What this test is about is the proxy mesh, not the rig.
        void TakeOverPlayer()
        {
            if (m_Body == null)
                return;

            Transform root = m_Body.transform;
            while (root.parent != null)
                root = root.parent;

            int stopped = 0;
            foreach (var b in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (b == this || b == null || !b.enabled)
                    continue;
                b.enabled = false;
                ++stopped;
            }
            foreach (var a in root.GetComponentsInChildren<Animator>(true))
                a.enabled = false;

            Line($"took over '{root.name}': {stopped} scripts stopped");
        }

        public IEnumerator Run()
        {
            Directory.CreateDirectory(m_OutputDir);

            if (m_Body == null)
            {
                Fail("no CharacterController in the scene - nothing to walk");
                Finish();
                yield break;
            }

            Line($"start {Fmt(m_Body.transform.position)}, {m_Waypoints.Length} waypoints");

            // Settle first: the player is spawned above the floor on purpose, so
            // the drop tells us whether there is a floor under the spawn at all.
            float spawnY = m_Body.transform.position.y;
            yield return Settle();
            float restY = m_Body.transform.position.y;
            Line($"spawn dropped {spawnY - restY:0.00} m to y={restY:0.00}");
            if (!Grounded(out float floorY, out string floorWhat))
                Fail($"nothing under the spawn - the proxy does not cover {Fmt(m_Body.transform.position)}");
            else
                Line($"floor under spawn: y={floorY:0.00} ({floorWhat}), eye y={EyeY():0.00}");

            PointCamera(m_Waypoints.Length > 0
                ? Flat(m_Waypoints[0] - m_Body.transform.position)
                : m_Body.transform.forward);
            yield return Shot("00_spawn");

            for (int i = 0; i < m_Waypoints.Length; ++i)
            {
                Vector3 target = m_Waypoints[i];
                yield return WalkTo(target, i);
                yield return Shot($"{i + 1:00}_wp");
            }

            Finish();
        }

        IEnumerator WalkTo(Vector3 target, int index)
        {
            // A fixed step rather than Time.deltaTime: a frame here can cost a
            // second of wall clock, and feeding that straight into gravity makes
            // every Move a hundred-metre plunge that the floor cancels, which
            // reads as "the player cannot walk" when the route was fine.
            const float step = 0.02f;
            int maxSteps = Mathf.CeilToInt(m_WaypointTimeout / step);

            float bestDist = Flat(target - m_Body.transform.position).magnitude;
            int lastProgressStep = 0;
            float vertical = 0f;
            float minY = float.MaxValue, maxY = float.MinValue;
            bool fell = false;
            CollisionFlags lastFlags = CollisionFlags.None;
            float moved = 0f;
            int i = 0;

            for (; i < maxSteps; ++i)
            {
                Vector3 pos = m_Body.transform.position;
                Vector3 toTarget = Flat(target - pos);
                float dist = toTarget.magnitude;
                if (dist < 0.35f)
                    break;

                Vector3 dir = toTarget.normalized;
                m_Body.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);

                if (m_Body.isGrounded && vertical < 0f)
                    vertical = -2f;
                vertical += m_Gravity * step;

                lastFlags = m_Body.Move((dir * m_Speed + Vector3.up * vertical) * step);
                moved += Flat(m_Body.transform.position - pos).magnitude;

                float y = m_Body.transform.position.y;
                minY = Mathf.Min(minY, y);
                maxY = Mathf.Max(maxY, y);
                if (y < m_FallenBelowY)
                {
                    fell = true;
                    break;
                }

                if (dist < bestDist - 0.05f)
                {
                    bestDist = dist;
                    lastProgressStep = i;
                }

                // Run several simulation steps per rendered frame: the walk is
                // simulated, so it does not need one frame each.
                if ((i % 25) == 24)
                    yield return null;
            }

            PointCamera(Flat(target - m_Body.transform.position));

            float remaining = Flat(target - m_Body.transform.position).magnitude;
            string where = Fmt(m_Body.transform.position);

            if (fell)
            {
                Fail($"wp{index} fell out of the scene at {where} (below y={m_FallenBelowY})");
                yield break;
            }

            if (remaining < 0.35f)
            {
                Line($"wp{index} reached {Fmt(target)} in {i * step:0.0}s, floor y {minY:0.00}..{maxY:0.00}");
                yield break;
            }

            // Moving almost nowhere is the interesting failure: it means the
            // capsule is jammed, not that it walked into a wall.
            if (moved < 0.2f)
                Fail($"wp{index} never moved - stuck at {where}, {remaining:0.0} m short, " +
                     $"last collision flags {lastFlags}, {Clearance()}");
            else if (i - lastProgressStep > (int)(2f / step))
                Line($"wp{index} BLOCKED {remaining:0.0} m short at {where} after walking " +
                     $"{moved:0.0} m - wall or step in the way ({lastFlags})");
            else
                Line($"wp{index} ran out of steps {remaining:0.0} m short at {where}, walked {moved:0.0} m");
        }

        void PointCamera(Vector3 flatDir)
        {
            if (m_Camera == null || m_Body == null)
                return;
            if (flatDir.sqrMagnitude < 1e-4f)
                flatDir = m_Body.transform.forward;
            Vector3 look = flatDir.normalized;
            // Nudged forward out of the avatar's head, which is still in the scene
            // and otherwise fills a corner of every shot.
            m_Camera.transform.position =
                m_Body.transform.position + Vector3.up * EyeHeight() + look * 0.3f;
            m_Camera.transform.rotation = Quaternion.LookRotation(look, Vector3.up);
        }

        // What is actually touching the capsule, for when it will not move.
        string Clearance()
        {
            var sb = new StringBuilder("clearance");
            Vector3 origin = m_Body.transform.position + Vector3.up * (m_Body.height * 0.5f);
            for (int a = 0; a < 8; ++a)
            {
                float rad = a * Mathf.PI * 0.25f;
                var dir = new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad));
                bool hit = Physics.Raycast(origin, dir, out RaycastHit h, 4f);
                sb.Append($" {a * 45}deg:{(hit ? h.distance.ToString("0.00") : ">4")}");
            }
            return sb.ToString();
        }

        IEnumerator Settle()
        {
            const float step = 0.02f;
            float vertical = 0f;
            for (int i = 0; i < 200; ++i)
            {
                if (m_Body.isGrounded)
                    break;
                vertical += m_Gravity * step;
                m_Body.Move(Vector3.up * vertical * step);
                if ((i % 25) == 24)
                    yield return null;
            }
            yield return null;
        }

        bool Grounded(out float floorY, out string what)
        {
            Vector3 origin = m_Body.transform.position + Vector3.up * 0.5f;
            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 50f))
            {
                floorY = hit.point.y;
                what = hit.collider != null ? hit.collider.gameObject.name : "?";
                return true;
            }
            floorY = float.NaN;
            what = "nothing";
            return false;
        }

        // The capsule is 1.36 m tall because it is sized for a seated-to-standing
        // VR play area, not for a 1.7 m person. Framing the screenshots at the top
        // of the capsule would put the horizon at a child's height, so the shots
        // use a standing eye height instead.
        public float m_EyeHeight = 1.6f;

        float EyeHeight() => m_EyeHeight;
        float EyeY() => m_Body.transform.position.y + EyeHeight();

        IEnumerator Shot(string name)
        {
            // Not WaitForEndOfFrame: it never resumes under -batchmode, and the
            // camera is rendered explicitly below anyway.
            yield return null;
            if (m_Camera == null)
                yield break;

            SetSplatsVisible(true);
            var rt = new RenderTexture(m_ShotWidth, m_ShotHeight, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 1
            };
            RenderTexture prevTarget = m_Camera.targetTexture;
            RenderTexture prevActive = RenderTexture.active;

            m_Camera.targetTexture = rt;
            m_Camera.Render();

            RenderTexture.active = rt;
            var tex = new Texture2D(m_ShotWidth, m_ShotHeight, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, m_ShotWidth, m_ShotHeight), 0, 0);
            tex.Apply();

            m_Camera.targetTexture = prevTarget;
            RenderTexture.active = prevActive;

            string path = Path.Combine(m_OutputDir, $"trogir_{name}.png");
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Line($"shot {path} from {Fmt(m_Camera.transform.position)}");

            Destroy(tex);
            rt.Release();
            Destroy(rt);
            SetSplatsVisible(false);
        }

        void SetSplatsVisible(bool on)
        {
            foreach (var r in m_SplatRenderers)
                if (r != null)
                    r.enabled = on;
        }

        void Finish()
        {
            SetSplatsVisible(true);
            Passed = m_Failures == 0;
            Done = true;
            Report = (Passed ? "[trogir] PASS\n" : $"[trogir] FAIL ({m_Failures})\n") + m_Log;
            Directory.CreateDirectory(m_OutputDir);
            File.WriteAllText(Path.Combine(m_OutputDir, "walk-report.txt"), Report);
            Debug.Log(Report);
        }

        void Line(string s)
        {
            m_Log.AppendLine("[trogir] " + s);
        }

        void Fail(string s)
        {
            ++m_Failures;
            m_Log.AppendLine("[trogir] FAIL " + s);
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        static string Fmt(Vector3 v) => string.Format(
            CultureInfo.InvariantCulture, "({0:0.00}, {1:0.00}, {2:0.00})", v.x, v.y, v.z);
    }
}
