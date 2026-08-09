using System.Collections.Generic;
using System.Reflection;
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace GardenSplat
{
    // Drives the "Splat Materialize" shader/paint feature for the
    // GardenAmericaSplat scene, without modifying the gaussian-splatting package
    // or the old StylizedSplats system. Assign SplatMaterialize.shader to the
    // target GaussianSplatRenderer's "Splats Shader" field; this component
    // pushes shader globals, runs the burst paint kernel, and reports coverage.
    //
    // The world starts as dormant points. Color arrives as growing spherical
    // bursts (PaintBurst), typically from a thrown comet's impact, and the whole
    // garden can be finished off with one big slow burst (PaintFinale).
    [ExecuteAlways]
    public class SplatMaterializeController : MonoBehaviour
    {
        public GaussianSplatRenderer m_Renderer;
        public ComputeShader m_PaintCompute;

        [Header("Dormant Look")]
        [Tooltip("Additive RGB offset applied over each dormant point's original splat color; black leaves it unchanged")]
        [ColorUsage(false, true)] public Color m_DormantColorOffset = Color.black;
        [Tooltip("Dormant point size in screen pixels, authored at the renderer's scene scale; scales " +
                 "automatically with m_Renderer's current transform scale (e.g. TabletopDiveController's " +
                 "bird's-eye shrink) so it stays proportionate to the miniature instead of full-size specks")]
        [Range(1f, 12f)] public float m_DormantPointSize = 3f;
        [Tooltip("Floor on the scaled-down point size, in screen pixels. At extreme shrink (bird's-eye " +
                 "tabletop mode) m_DormantPointSize * scaleRatio can fall under 1px and vanish; this keeps " +
                 "dormant points visibly readable no matter how small the world gets")]
        [Min(0.1f)] public float m_MinDormantPointSize = 1.5f;
        [Tooltip("Fraction of still-dormant points that render, so the world starts sparse (and cheaper). Painted points always show")]
        [Range(0.02f, 1f)] public float m_DormantVisibleFraction = 0.35f;
        [Tooltip("Metres of gentle idle bob while dormant, authored at the renderer's scene scale; scales " +
                 "automatically with m_Renderer's current transform scale like m_DormantPointSize above. " +
                 "Damps to zero as a splat is painted")]
        [Min(0f)] public float m_DormantDrift = 0.03f;
        [Min(0f)] public float m_DormantDriftSpeed = 1.2f;

        [Header("Stylized Painted Look (matches StylizedSplats)")]
        [Tooltip("Brush stroke alpha texture, e.g. StylizedSplats/Textures/BrushStroke_BoxSoft.png")]
        public Texture2D m_BrushTexture;
        [Tooltip("World-space splat size below which strokes are fully stylized")]
        public float m_StyleSizeMin = 0.01f;
        [Tooltip("World-space splat size above which splats stay fully gaussian")]
        public float m_StyleSizeMax = 0.05f;
        [Range(0f, 1f)] public float m_AlphaCut = 0.188f;
        public float m_AlphaGamma = 2.65f;
        public bool m_RandomFlip;
        [Tooltip("Toggles Random Flip on a timer, churning strokes like an animated oil painting")]
        public bool m_AnimateRandomFlip = true;
        [Min(0.01f)] public float m_AnimateFlipInterval = 0.3f;
        [Range(0f, 1f)] public float m_FlipJitter = 1f;

        [Header("Bloom (one-shot when a splat is born)")]
        [Tooltip("Paint progress at which a splat is considered born and blooms")]
        [Range(0.05f, 1f)] public float m_BloomAt = 0.4f;
        [Min(0.01f)] public float m_PopDuration = 0.55f;
        [Range(0f, 3f)] public float m_PopSize = 0.9f;
        [Range(0f, 2f)] public float m_PopSaturation = 0.8f;
        [Min(0f)] public float m_PopFlash = 0.6f;
        [ColorUsage(false, true)] public Color m_PopColor = new Color(1f, 0.95f, 0.75f);

        [Header("Burst")]
        [Tooltip("How fast a burst's wavefront expands, m/s")]
        [Min(0.1f)] public float m_BurstSpeed = 6f;
        [Tooltip("Soft shell width behind the wavefront")]
        [Min(0.01f)] public float m_BurstEdge = 0.6f;
        [Tooltip("How hard a burst paints per second once a splat is inside it. High = snappy reveal")]
        [Min(0.1f)] public float m_BurstFillRate = 6f;
        [Tooltip("Extra seconds a burst keeps painting after reaching its full radius, so the trailing edge fully fills in")]
        [Min(0f)] public float m_BurstHold = 0.25f;

        [Header("Coverage")]
        [Min(0f)] public float m_CoverageInterval = 0.25f;

        [Header("Editor Preview")]
        [Tooltip("Editor-only: show every splat fully materialized")]
        public bool m_PreviewPainted;

        /// Fraction of the cloud painted, 0..1. Updated asynchronously.
        public float PaintedFraction { get; private set; }

        struct Burst
        {
            public Vector3 center;
            public float startTime;
            public float maxRadius;
        }

        readonly List<Burst> m_Bursts = new();

        bool m_FlipAnimState;

        GraphicsBuffer m_PaintProgress;
        int m_PaintProgressCount;

        GraphicsBuffer m_PaintPartialSums;
        float m_NextCoverageSample;
        bool m_CoverageReadbackPending;
        bool m_CoverageDirty;

        static FieldInfo s_FieldGpuPosData;
        static FieldInfo s_FieldGpuChunks;
        static FieldInfo s_FieldGpuChunksValid;

        const float k_NeverPainted = -1e9f;

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

        bool RendererReady => m_Renderer != null && m_Renderer.HasValidAsset && m_Renderer.HasValidRenderSetup;

        static float PaintClock => Time.realtimeSinceStartup;
        static int GroupCount(int splatCount) => (splatCount + 1023) / 1024;

        void OnEnable()
        {
            EnsureReflectionCached();
            // Baseline scale to measure the live scale-ratio against, captured once here rather
            // than on Awake so a domain reload / re-enable while the world is already shrunk
            // (e.g. re-entering Play mode mid-Place) doesn't wrongly adopt the shrunk pose as
            // "authored". OnEnable still runs before TabletopDiveController's Start() shrinks the
            // renderer for the first time, so in practice this is the same instant either way.
            m_AuthoredScale = m_Renderer ? Mathf.Max(Mathf.Abs(m_Renderer.transform.localScale.x), 1e-4f) : 1f;
        }

        void OnDisable()
        {
            m_PaintProgress?.Dispose();
            m_PaintProgress = null;
            m_PaintProgressCount = 0;
            m_PaintPartialSums?.Dispose();
            m_PaintPartialSums = null;
            m_CoverageReadbackPending = false;
            m_Bursts.Clear();
            ResetGlobals();
        }

        // Renderer's current scale relative to the authored (OnEnable-time) scale — 1 at the
        // scene-authored pose, <1 while TabletopDiveController has the world shrunk toward
        // tabletop size. Reading m_Renderer.transform directly (rather than subscribing to a
        // scale-change event) means any current or future path that changes the renderer's
        // transform scale is picked up automatically, with zero coupling to how the scale changed.
        float CurrentScaleRatio => m_Renderer ? Mathf.Abs(m_Renderer.transform.localScale.x) / m_AuthoredScale : 1f;
        float m_AuthoredScale = 1f;

        // Shader.SetGlobal* is process-global and outlives this component (a scene switch
        // doesn't reset it). Static so a scene with no SplatMaterializeController at all
        // (e.g. SplatGlobalsReset on scene load) can still scrub every key this class owns.
        public static void ResetGlobals()
        {
            Shader.SetGlobalColor(Props.DormantColorOffset, Color.black);
            Shader.SetGlobalFloat(Props.DormantPointSize, 0f);
            Shader.SetGlobalFloat(Props.DormantVisibleFraction, 0f);
            Shader.SetGlobalFloat(Props.DormantDrift, 0f);
            Shader.SetGlobalFloat(Props.DormantDriftSpeed, 0f);
            Shader.SetGlobalFloat(Props.PaintTime, 0f);
            Shader.SetGlobalFloat(Props.PopDuration, 0f);
            Shader.SetGlobalFloat(Props.PopSize, 0f);
            Shader.SetGlobalFloat(Props.PopSaturation, 0f);
            Shader.SetGlobalFloat(Props.PopFlash, 0f);
            Shader.SetGlobalColor(Props.PopColor, Color.black);
            Shader.SetGlobalFloat(Props.MaterializePreview, 0f);
            Shader.SetGlobalFloat(Props.StyleSizeMin, 0f);
            Shader.SetGlobalFloat(Props.StyleSizeMax, 0f);
            Shader.SetGlobalFloat(Props.StyleAlphaCut, 0f);
            Shader.SetGlobalFloat(Props.StyleAlphaGamma, 1f);
            Shader.SetGlobalFloat(Props.StyleRandomFlip, 0f);
            Shader.SetGlobalFloat(Props.StyleFlipJitter, 0f);
            Shader.SetGlobalInt(Props.SplatPaintValid, 0);
        }

        void Update()
        {
            EnsurePaintBuffer();
            UpdateFlipAnimation();
            DispatchBursts();
            UpdateCoverage();
            PushGlobals();
        }

        // Flipping Random Flip on/off re-rolls each splat's stroke orientation,
        // reading as an animated oil painting. Phase comes straight from the
        // clock so it needs no priming.
        void UpdateFlipAnimation()
        {
            if (!m_AnimateRandomFlip)
            {
                m_FlipAnimState = false;
                return;
            }

#if UNITY_EDITOR
            if (!Application.isPlaying)
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif

            float interval = Mathf.Max(m_AnimateFlipInterval, 0.01f);
            m_FlipAnimState = Mathf.Repeat(Time.realtimeSinceStartup, interval * 2f) >= interval;
        }

        bool EffectiveRandomFlip => m_AnimateRandomFlip ? m_FlipAnimState : m_RandomFlip;

        void EnsurePaintBuffer()
        {
            if (!RendererReady)
                return;

            int count = m_Renderer.splatCount;
            if (m_PaintProgress != null && m_PaintProgressCount == count)
                return;

            m_PaintProgress?.Dispose();
            m_PaintProgress = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float) * 2) { name = "SplatMaterializePaintProgress" };
            m_PaintProgressCount = count;

            m_PaintPartialSums?.Dispose();
            m_PaintPartialSums = new GraphicsBuffer(GraphicsBuffer.Target.Structured, GroupCount(count), sizeof(float)) { name = "SplatMaterializePartialSums" };
            m_CoverageReadbackPending = false;

            ClearPaint();
        }

        /// Registers an expanding spherical paint burst centred at worldCenter.
        /// The wavefront grows at m_BurstSpeed up to maxRadius, so color spreads
        /// outward as a visible wave. Call once per comet impact / finale.
        public void PaintBurst(Vector3 worldCenter, float maxRadius)
        {
            if (maxRadius <= 0f)
                return;
            m_Bursts.Add(new Burst { center = worldCenter, startTime = PaintClock, maxRadius = maxRadius });
        }

        /// One big, slow burst from worldCenter sized to finish the whole cloud -
        /// the climactic full-restore. Wire this to a coverage milestone.
        public void PaintFinale(Vector3 worldCenter, float radius)
        {
            m_Bursts.Add(new Burst { center = worldCenter, startTime = PaintClock, maxRadius = Mathf.Max(radius, 1f) });
        }

        // Each active burst dispatches once per frame with its current radius,
        // so splats are swept in from the centre outward and each blooms as the
        // wavefront reaches it. A burst lives until its wavefront has covered
        // maxRadius plus m_BurstHold seconds of soak.
        void DispatchBursts()
        {
            if (m_Bursts.Count == 0)
                return;
            if (!RendererReady || m_PaintCompute == null || m_PaintProgress == null)
            {
                m_Bursts.Clear();
                return;
            }

            GraphicsBuffer posBuffer = GpuPosData;
            GraphicsBuffer chunksBuffer = GpuChunks;
            if (posBuffer == null || chunksBuffer == null)
                return;

            var asset = m_Renderer.asset;
            uint format = (uint)asset.posFormat | ((uint)asset.scaleFormat << 8) | ((uint)asset.shFormat << 16);
            int kernel = m_PaintCompute.FindKernel("CSPaintBurst");
            float now = PaintClock;
            float strength = m_BurstFillRate * Time.deltaTime;

            for (int i = m_Bursts.Count - 1; i >= 0; i--)
            {
                Burst b = m_Bursts[i];
                float elapsed = now - b.startTime;
                float radius = elapsed * m_BurstSpeed;
                float fullAt = b.maxRadius / Mathf.Max(m_BurstSpeed, 0.1f);
                if (elapsed > fullAt + m_BurstHold)
                {
                    m_Bursts.RemoveAt(i);
                    continue;
                }
                radius = Mathf.Min(radius, b.maxRadius);

                using CommandBuffer cmb = new CommandBuffer { name = "SplatMaterializeBurst" };
                cmb.SetComputeBufferParam(m_PaintCompute, kernel, "_SplatPos", posBuffer);
                cmb.SetComputeBufferParam(m_PaintCompute, kernel, "_SplatChunks", chunksBuffer);
                cmb.SetComputeIntParam(m_PaintCompute, "_SplatFormat", (int)format);
                cmb.SetComputeIntParam(m_PaintCompute, "_SplatChunkCount", GpuChunksValid ? chunksBuffer.count : 0);
                cmb.SetComputeBufferParam(m_PaintCompute, kernel, "_PaintProgress", m_PaintProgress);
                cmb.SetComputeIntParam(m_PaintCompute, "_PaintSplatCount", m_PaintProgressCount);
                cmb.SetComputeMatrixParam(m_PaintCompute, "_PaintObjectToWorld", m_Renderer.transform.localToWorldMatrix);
                cmb.SetComputeVectorParam(m_PaintCompute, "_PaintCenter", b.center);
                cmb.SetComputeFloatParam(m_PaintCompute, "_PaintRadius", radius);
                cmb.SetComputeFloatParam(m_PaintCompute, "_PaintEdge", m_BurstEdge);
                cmb.SetComputeFloatParam(m_PaintCompute, "_PaintStrength", strength);
                cmb.SetComputeFloatParam(m_PaintCompute, "_PaintTime", now);
                cmb.SetComputeFloatParam(m_PaintCompute, "_PaintBloomAt", m_BloomAt);

                cmb.DispatchCompute(m_PaintCompute, kernel, GroupCount(m_PaintProgressCount), 1, 1);
                Graphics.ExecuteCommandBuffer(cmb);
            }

            m_CoverageDirty = true;
        }

        void UpdateCoverage()
        {
            if (m_PaintProgress == null || m_PaintPartialSums == null || m_PaintCompute == null)
                return;
            if (!m_CoverageDirty || m_CoverageReadbackPending || Time.realtimeSinceStartup < m_NextCoverageSample)
                return;

            m_NextCoverageSample = Time.realtimeSinceStartup + m_CoverageInterval;
            m_CoverageDirty = false;

            int kernel = m_PaintCompute.FindKernel("CSReducePaint");
            m_PaintCompute.SetBuffer(kernel, "_PaintProgress", m_PaintProgress);
            m_PaintCompute.SetBuffer(kernel, "_PaintPartialSums", m_PaintPartialSums);
            m_PaintCompute.SetInt("_PaintSplatCount", m_PaintProgressCount);
            m_PaintCompute.Dispatch(kernel, GroupCount(m_PaintProgressCount), 1, 1);

            m_CoverageReadbackPending = true;
            AsyncGPUReadback.Request(m_PaintPartialSums, OnCoverageReadback);
        }

        void OnCoverageReadback(AsyncGPUReadbackRequest request)
        {
            m_CoverageReadbackPending = false;
            if (request.hasError || m_PaintProgressCount == 0)
                return;

            var partials = request.GetData<float>();
            float total = 0f;
            for (int i = 0; i < partials.Length; i++)
                total += partials[i];
            PaintedFraction = Mathf.Clamp01(total / m_PaintProgressCount);
        }

        void PushGlobals()
        {
            float scaleRatio = CurrentScaleRatio;
            Shader.SetGlobalColor(Props.DormantColorOffset, m_DormantColorOffset);
            Shader.SetGlobalFloat(Props.DormantPointSize, Mathf.Max(m_DormantPointSize * scaleRatio, m_MinDormantPointSize));
            Shader.SetGlobalFloat(Props.DormantVisibleFraction, m_DormantVisibleFraction);
            Shader.SetGlobalFloat(Props.DormantDrift, m_DormantDrift * scaleRatio);
            Shader.SetGlobalFloat(Props.DormantDriftSpeed, m_DormantDriftSpeed);
            Shader.SetGlobalFloat(Props.PaintTime, PaintClock);
            Shader.SetGlobalFloat(Props.PopDuration, m_PopDuration);
            Shader.SetGlobalFloat(Props.PopSize, m_PopSize);
            Shader.SetGlobalFloat(Props.PopSaturation, m_PopSaturation);
            Shader.SetGlobalFloat(Props.PopFlash, m_PopFlash);
            Shader.SetGlobalColor(Props.PopColor, m_PopColor);
            Shader.SetGlobalFloat(Props.MaterializePreview, m_PreviewPainted ? 1f : 0f);

            Shader.SetGlobalFloat(Props.StyleSizeMin, m_StyleSizeMin);
            Shader.SetGlobalFloat(Props.StyleSizeMax, Mathf.Max(m_StyleSizeMax, m_StyleSizeMin + 1e-4f));
            Shader.SetGlobalFloat(Props.StyleAlphaCut, m_AlphaCut);
            Shader.SetGlobalFloat(Props.StyleAlphaGamma, m_AlphaGamma);
            Shader.SetGlobalFloat(Props.StyleRandomFlip, EffectiveRandomFlip ? 1f : 0f);
            Shader.SetGlobalFloat(Props.StyleFlipJitter, m_FlipJitter);
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

        /// Resets the whole cloud back to dormant points.
        public void ClearPaint()
        {
            m_Bursts.Clear();
            if (m_PaintProgress == null)
                return;

            var cleared = new Vector2[m_PaintProgressCount];
            for (int i = 0; i < cleared.Length; i++)
                cleared[i] = new Vector2(0f, k_NeverPainted);
            m_PaintProgress.SetData(cleared);
            PaintedFraction = 0f;
        }

        static class Props
        {
            public const string DormantColorOffset = "_DormantColorOffset";
            public const string DormantPointSize = "_DormantPointSize";
            public const string DormantVisibleFraction = "_DormantVisibleFraction";
            public const string DormantDrift = "_DormantDrift";
            public const string DormantDriftSpeed = "_DormantDriftSpeed";
            public const string PaintTime = "_PaintTime";
            public const string PopDuration = "_PopDuration";
            public const string PopSize = "_PopSize";
            public const string PopSaturation = "_PopSaturation";
            public const string PopFlash = "_PopFlash";
            public const string PopColor = "_PopColor";
            public const string MaterializePreview = "_MaterializePreview";
            public const string StyleSizeMin = "_StyleSizeMin";
            public const string StyleSizeMax = "_StyleSizeMax";
            public const string StyleAlphaCut = "_StyleAlphaCut";
            public const string StyleAlphaGamma = "_StyleAlphaGamma";
            public const string StyleRandomFlip = "_StyleRandomFlip";
            public const string StyleFlipJitter = "_StyleFlipJitter";
            public const string StylizedBrushTex = "_StylizedBrushTex";
            public const string SplatPaintProgress = "_SplatPaintProgress";
            public const string SplatPaintValid = "_SplatPaintValid";
        }
    }
}
