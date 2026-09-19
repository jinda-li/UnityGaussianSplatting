using UnityEngine;
using UnityEngine.Rendering;

namespace EiffelMR
{
    // The tower drawn as points sampled off its own surface.
    //
    // This is the mesh-world counterpart of TowerParticles. With a splat the
    // miniature's particles are the splat's own Gaussians; with a mesh they are
    // points scattered over the tower's triangles, in the tower's own space, so
    // every point that drifts in the bubble sits on a surface of the tower the
    // player ends up standing under. As Solidify goes to 1 they stop drifting,
    // settle onto that surface and fade while the solid mesh dissolves in under
    // them (TowerDissolve.shader) - the same tower, condensing.
    //
    // The mesh is built once in the editor (EiffelDesktopSceneBuilder): four
    // vertices per point, all at the point's centre, with the corner in UV0 and
    // a per-point random in UV1. TowerPoints.shader expands each quad to a fixed
    // size in screen pixels, which is what keeps the points legible at 29 cm and
    // at 324 m alike, and works without geometry shaders on a Quest.
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class TowerPointCloud : MonoBehaviour
    {
        [Tooltip("Point size in screen pixels.")]
        [Range(0.5f, 12f)] public float m_PointSize = 1.15f;

        [Tooltip("Fraction of points drawn while fully dispersed.")]
        [Range(0.02f, 1f)] public float m_DispersedFraction = 0.6f;

        [Tooltip("Drift amplitude in TOWER-LOCAL metres, so it is the same " +
                 "fraction of the tower at every scale. 3 is ~1% of its height.")]
        [Min(0f)] public float m_Drift = 2.5f;
        [Min(0f)] public float m_DriftSpeed = 0.7f;

        [Tooltip("How much the points twinkle, and how often one glints.")]
        [Range(0f, 1f)] public float m_Sparkle = 0.6f;

        [Tooltip("Width of each point's own settle window within the ramp.")]
        [Range(0.05f, 1f)] public float m_Stagger = 0.45f;

        [Range(0f, 1f)] public float m_Solidify;

        MaterialPropertyBlock m_Block;
        MeshRenderer m_Renderer;

        static readonly int k_Solidify = Shader.PropertyToID("_Solidify");
        static readonly int k_Stagger = Shader.PropertyToID("_Stagger");
        static readonly int k_Size = Shader.PropertyToID("_PointSize");
        static readonly int k_Fraction = Shader.PropertyToID("_Fraction");
        static readonly int k_Drift = Shader.PropertyToID("_Drift");
        static readonly int k_DriftSpeed = Shader.PropertyToID("_DriftSpeed");
        static readonly int k_Sparkle = Shader.PropertyToID("_Sparkle");

        public float Solidify
        {
            get => m_Solidify;
            set
            {
                m_Solidify = Mathf.Clamp01(value);
                Apply();
            }
        }

        void OnEnable() => Apply();
        void OnValidate() => Apply();

        public void Apply()
        {
            if (!m_Renderer)
                m_Renderer = GetComponent<MeshRenderer>();
            if (!m_Renderer)
                return;
            // Fully condensed, the solid tower is showing; the points would only
            // cost overdraw.
            m_Renderer.enabled = m_Solidify < 0.999f;
            m_Renderer.shadowCastingMode = ShadowCastingMode.Off;
            m_Block ??= new MaterialPropertyBlock();
            m_Renderer.GetPropertyBlock(m_Block);
            m_Block.SetFloat(k_Solidify, m_Solidify);
            m_Block.SetFloat(k_Stagger, m_Stagger);
            m_Block.SetFloat(k_Size, m_PointSize);
            m_Block.SetFloat(k_Fraction, m_DispersedFraction);
            m_Block.SetFloat(k_Drift, m_Drift);
            m_Block.SetFloat(k_DriftSpeed, m_DriftSpeed);
            m_Block.SetFloat(k_Sparkle, m_Sparkle);
            m_Renderer.SetPropertyBlock(m_Block);
        }

        /// Build the point mesh from a surface. Area-weighted, so a big pier
        /// face gets as many points per square metre as a thin brace.
        /// `albedo` is per submesh; points are pre-lit from `sunLocal` (a
        /// direction in the mesh's own space) so the cloud reads as a lit
        /// object rather than flat colour.
        public static Mesh BuildMesh(Mesh source, Color[] albedo, int count,
                                     Vector3 sunLocal, int seed = 7)
        {
            var verts = source.vertices;
            var rng = new System.Random(seed);
            int subs = source.subMeshCount;

            // cumulative area over all triangles of all submeshes
            var tris = new System.Collections.Generic.List<int>();
            var triSub = new System.Collections.Generic.List<int>();
            for (int s = 0; s < subs; s++)
            {
                var t = source.GetTriangles(s);
                tris.AddRange(t);
                for (int i = 0; i < t.Length / 3; i++)
                    triSub.Add(s);
            }
            int ntri = triSub.Count;
            var cdf = new double[ntri];
            double total = 0;
            for (int i = 0; i < ntri; i++)
            {
                Vector3 a = verts[tris[i * 3]], b = verts[tris[i * 3 + 1]],
                        c = verts[tris[i * 3 + 2]];
                total += Vector3.Cross(b - a, c - a).magnitude * 0.5;
                cdf[i] = total;
            }

            Vector3 sun = sunLocal.sqrMagnitude > 0 ? sunLocal.normalized : Vector3.up;
            var pos = new Vector3[count * 4];
            var col = new Color32[count * 4];
            var uv0 = new Vector2[count * 4];
            var uv1 = new Vector2[count * 4];
            var idx = new int[count * 6];
            Vector2[] corners = { new(-1, -1), new(1, -1), new(1, 1), new(-1, 1) };

            for (int p = 0; p < count; p++)
            {
                double r = rng.NextDouble() * total;
                int lo = 0, hi = ntri - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (cdf[mid] < r) lo = mid + 1; else hi = mid;
                }
                Vector3 a = verts[tris[lo * 3]], b = verts[tris[lo * 3 + 1]],
                        c = verts[tris[lo * 3 + 2]];
                float u = (float)rng.NextDouble(), v = (float)rng.NextDouble();
                if (u + v > 1f) { u = 1f - u; v = 1f - v; }
                Vector3 at = a + (b - a) * u + (c - a) * v;

                Vector3 n = Vector3.Cross(b - a, c - a).normalized;
                // A lattice is seen from both sides; light the side that faces
                // the sun either way round.
                float lambert = Mathf.Abs(Vector3.Dot(n, sun));
                Color alb = albedo != null && triSub[lo] < albedo.Length
                    ? albedo[triSub[lo]] : new Color(0.38f, 0.31f, 0.26f);
                float jitter = 0.88f + 0.24f * (float)rng.NextDouble();
                Color lit = alb * (0.42f + 0.75f * lambert) * jitter;
                lit.a = 1f;
                Color32 lit32 = lit;

                float rand = (float)rng.NextDouble();
                for (int k = 0; k < 4; k++)
                {
                    pos[p * 4 + k] = at;
                    col[p * 4 + k] = lit32;
                    uv0[p * 4 + k] = corners[k];
                    uv1[p * 4 + k] = new Vector2(rand, 0f);
                }
                int vi = p * 4, ii = p * 6;
                idx[ii] = vi; idx[ii + 1] = vi + 1; idx[ii + 2] = vi + 2;
                idx[ii + 3] = vi; idx[ii + 4] = vi + 2; idx[ii + 5] = vi + 3;
            }

            var mesh = new Mesh { name = "TowerPoints", indexFormat = IndexFormat.UInt32 };
            mesh.vertices = pos;
            mesh.colors32 = col;
            mesh.uv = uv0;
            mesh.uv2 = uv1;
            mesh.triangles = idx;
            // Every vertex of a quad sits at the point's centre, so the computed
            // bounds are right as they are; the drift is a few metres at most.
            mesh.RecalculateBounds();
            var bounds = mesh.bounds;
            bounds.Expand(8f);
            mesh.bounds = bounds;
            return mesh;
        }
    }
}
