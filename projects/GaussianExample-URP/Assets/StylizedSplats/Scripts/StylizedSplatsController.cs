using System.Reflection;
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace StylizedSplats
{
    // Drives the "Stylized Splats" shader/paint feature without modifying the
    // org.nesnausk.gaussian-splatting package. Assign StylizedSplats.shader to
    // the target GaussianSplatRenderer's "Splats Shader" field (Resources
    // foldout) in the Inspector; this component only pushes shader globals and
    // runs the paint compute kernel. m_GpuPosData/m_GpuChunks are private on
    // GaussianSplatRenderer, so this reads them via reflection once per asset.
    [ExecuteAlways]
    public class StylizedSplatsController : MonoBehaviour
    {
        public GaussianSplatRenderer m_Renderer;
        public ComputeShader m_PaintCompute;
        public Texture2D m_BrushTexture;

        public bool m_StylizedEnable = true;

        [Tooltip("World-space splat size below which strokes are fully stylized")]
        public float m_StyleSizeMin = 0.02f;
        [Tooltip("World-space splat size above which splats stay fully gaussian")]
        public float m_StyleSizeMax = 0.15f;

        [Range(0f, 1f)] public float m_AlphaCut = 0.15f;
        public float m_AlphaGamma = 1f;
        public bool m_RandomFlip;
        [Tooltip("Toggles Random Flip on/off on a timer, which makes the strokes churn like an animated oil painting")]
        public bool m_AnimateRandomFlip;
        [Tooltip("Seconds between Random Flip toggles")]
        [Min(0.01f)] public float m_AnimateFlipInterval = 0.3f;
        [Tooltip("How far each stroke rotates while the flip is on. 0 = plain mirror, 1 = up to +/-90 degrees")]
        [Range(0f, 1f)] public float m_FlipJitter;
        [Range(0f, 1f)] public float m_BaseSaturation = 0.12f;
        [Tooltip("Lifts the unpainted base toward white (0 = plain gray, 1 = pure white). Keep below ~0.8 so shading stays readable")]
        [Range(0f, 1f)] public float m_BaseLift = 0.6f;

        [Header("Editor Preview")]
        [Tooltip("Editor-only: skip Base Saturation/Base Lift and show every splat as if fully painted, to preview the post-spray stylized look")]
        public bool m_PreviewPainted;

        [Header("Size Cull")]
        [Tooltip("Hide splats whose world-space size exceeds Size Cull Max. Independent of Stylized Enable")]
        public bool m_SizeCullEnable;
        [Tooltip("World-space splat size above which splats are hidden entirely")]
        public float m_SizeCullMax = 1f;

        GraphicsBuffer m_PaintProgress;
        int m_PaintProgressCount;

        bool m_FlipAnimState;

        static FieldInfo s_FieldGpuPosData;
        static FieldInfo s_FieldGpuChunks;
        static FieldInfo s_FieldGpuChunksValid;

        static void EnsureReflectionCached()
        {
            if (s_FieldGpuPosData != null)
                return;
            var t = typeof(GaussianSplatRenderer);
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            s_FieldGpuPosData = t.GetField("m_GpuPosData", flags);
            s_FieldGpuChunks = t.GetField("m_GpuChunks", flags);
            s_FieldGpuChunksValid = t.GetField("m_GpuChunksValid", flags);
        }

        GraphicsBuffer GpuPosData => s_FieldGpuPosData?.GetValue(m_Renderer) as GraphicsBuffer;
        GraphicsBuffer GpuChunks => s_FieldGpuChunks?.GetValue(m_Renderer) as GraphicsBuffer;
        bool GpuChunksValid => s_FieldGpuChunksValid != null && (bool)s_FieldGpuChunksValid.GetValue(m_Renderer);

        void OnEnable()
        {
            EnsureReflectionCached();
        }

        void OnDisable()
        {
            m_PaintProgress?.Dispose();
            m_PaintProgress = null;
            m_PaintProgressCount = 0;
            Shader.SetGlobalInt(Props.SplatPaintValid, 0);
        }

        void Update()
        {
            EnsurePaintBuffer();
            UpdateFlipAnimation();
            PushGlobals();
        }

        // Flipping Random Flip on and off repeatedly re-rolls the per-splat stroke
        // orientation, which reads as an animated oil painting. The phase is derived
        // straight from the clock rather than accumulated, so it keeps running with
        // no state to prime - entering play mode picks it up on the first frame.
        void UpdateFlipAnimation()
        {
            if (!m_AnimateRandomFlip)
            {
                m_FlipAnimState = false;
                return;
            }

#if UNITY_EDITOR
            // Edit mode only pumps Update on demand; keep asking for the next tick.
            if (!Application.isPlaying)
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif

            float interval = Mathf.Max(m_AnimateFlipInterval, 0.01f);
            m_FlipAnimState = Mathf.Repeat(Time.realtimeSinceStartup, interval * 2f) >= interval;
        }

        bool EffectiveRandomFlip => m_AnimateRandomFlip ? m_FlipAnimState : m_RandomFlip;

        bool RendererReady => m_Renderer != null && m_Renderer.HasValidAsset && m_Renderer.HasValidRenderSetup;

        void EnsurePaintBuffer()
        {
            if (!RendererReady)
                return;

            int count = m_Renderer.splatCount;
            if (m_PaintProgress != null && m_PaintProgressCount == count)
                return;

            m_PaintProgress?.Dispose();
            m_PaintProgress = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float)) { name = "StylizedSplatPaintProgress" };
            m_PaintProgress.SetData(new float[count]);
            m_PaintProgressCount = count;
        }

        void PushGlobals()
        {
            Shader.SetGlobalFloat(Props.StylizedEnable, m_StylizedEnable ? 1f : 0f);
            Shader.SetGlobalFloat(Props.StyleSizeMin, m_StyleSizeMin);
            Shader.SetGlobalFloat(Props.StyleSizeMax, Mathf.Max(m_StyleSizeMax, m_StyleSizeMin + 1e-4f));
            Shader.SetGlobalFloat(Props.StyleAlphaCut, m_AlphaCut);
            Shader.SetGlobalFloat(Props.StyleAlphaGamma, m_AlphaGamma);
            Shader.SetGlobalFloat(Props.StyleRandomFlip, EffectiveRandomFlip ? 1f : 0f);
            Shader.SetGlobalFloat(Props.StyleFlipJitter, m_FlipJitter);
            Shader.SetGlobalFloat(Props.BaseSaturation, m_BaseSaturation);
            Shader.SetGlobalFloat(Props.BaseLift, m_BaseLift);
            Shader.SetGlobalFloat(Props.PreviewPainted, m_PreviewPainted ? 1f : 0f);
            Shader.SetGlobalFloat(Props.SizeCullEnable, m_SizeCullEnable ? 1f : 0f);
            Shader.SetGlobalFloat(Props.SizeCullMax, m_SizeCullMax);
            if (m_BrushTexture != null)
                Shader.SetGlobalTexture(Props.StylizedBrushTex, m_BrushTexture);

            if (m_PaintProgress != null)
            {
                Shader.SetGlobalBuffer(Props.SplatPaintProgress, m_PaintProgress);
                Shader.SetGlobalInt(Props.SplatPaintValid, 1);
            }
            else
            {
                Shader.SetGlobalInt(Props.SplatPaintValid, 0);
            }
        }

        /// Accumulates paint progress for splats within radius of the ray segment
        /// [rayOrigin, rayOrigin + rayDir*maxDistance]. No raycast/collider needed:
        /// the player stands inside the splat volume, so a single proxy collider
        /// can't reliably catch the ray (its origin is inside the collider, and
        /// Unity raycasts never register an exit-only hit). Depth along the ray
        /// is intentionally ignored - only perpendicular distance matters.
        /// Call every frame while the brush is active.
        public void PaintRay(Vector3 rayOrigin, Vector3 rayDir, float radius, float maxDistance, float strength)
        {
            if (!RendererReady || m_PaintCompute == null)
                return;

            EnsurePaintBuffer();
            if (m_PaintProgress == null)
                return;

            GraphicsBuffer posBuffer = GpuPosData;
            GraphicsBuffer chunksBuffer = GpuChunks;
            if (posBuffer == null || chunksBuffer == null)
                return;

            var asset = m_Renderer.asset;
            uint format = (uint)asset.posFormat | ((uint)asset.scaleFormat << 8) | ((uint)asset.shFormat << 16);

            int kernel = m_PaintCompute.FindKernel("CSPaintSplats");
            using CommandBuffer cmb = new CommandBuffer { name = "StylizedSplatPaint" };
            cmb.SetComputeBufferParam(m_PaintCompute, kernel, "_SplatPos", posBuffer);
            cmb.SetComputeBufferParam(m_PaintCompute, kernel, "_SplatChunks", chunksBuffer);
            cmb.SetComputeIntParam(m_PaintCompute, "_SplatFormat", (int)format);
            cmb.SetComputeIntParam(m_PaintCompute, "_SplatChunkCount", GpuChunksValid ? chunksBuffer.count : 0);
            cmb.SetComputeBufferParam(m_PaintCompute, kernel, "_PaintProgress", m_PaintProgress);
            cmb.SetComputeIntParam(m_PaintCompute, "_PaintSplatCount", m_Renderer.splatCount);
            cmb.SetComputeMatrixParam(m_PaintCompute, "_PaintObjectToWorld", m_Renderer.transform.localToWorldMatrix);
            cmb.SetComputeVectorParam(m_PaintCompute, "_PaintRayOrigin", rayOrigin);
            cmb.SetComputeVectorParam(m_PaintCompute, "_PaintRayDir", rayDir.normalized);
            cmb.SetComputeFloatParam(m_PaintCompute, "_PaintMaxDistance", maxDistance);
            cmb.SetComputeFloatParam(m_PaintCompute, "_PaintRadius", radius);
            cmb.SetComputeFloatParam(m_PaintCompute, "_PaintStrength", strength);

            int groups = (m_Renderer.splatCount + 1023) / 1024;
            cmb.DispatchCompute(m_PaintCompute, kernel, groups, 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);
        }

        /// Resets all splats back to the desaturated base look.
        public void ClearPaint()
        {
            if (m_PaintProgress == null)
                return;
            m_PaintProgress.SetData(new float[m_PaintProgressCount]);
        }

        static class Props
        {
            public const string StylizedEnable = "_StylizedEnable";
            public const string StyleSizeMin = "_StyleSizeMin";
            public const string StyleSizeMax = "_StyleSizeMax";
            public const string StyleAlphaCut = "_StyleAlphaCut";
            public const string StyleAlphaGamma = "_StyleAlphaGamma";
            public const string StyleRandomFlip = "_StyleRandomFlip";
            public const string StyleFlipJitter = "_StyleFlipJitter";
            public const string BaseSaturation = "_BaseSaturation";
            public const string BaseLift = "_BaseLift";
            public const string PreviewPainted = "_StylizedPreviewPainted";
            public const string SizeCullEnable = "_SizeCullEnable";
            public const string SizeCullMax = "_SizeCullMax";
            public const string StylizedBrushTex = "_StylizedBrushTex";
            public const string SplatPaintProgress = "_SplatPaintProgress";
            public const string SplatPaintValid = "_SplatPaintValid";
        }
    }
}
