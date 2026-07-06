// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Text;
using GaussianSplatting.Editor.Utils;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace R2B.Editor.GaussianCollision
{
    public class GaussianGroundCollisionWindow : EditorWindow
    {
        const string kPrefInputFile = "com.r2b.SplatCollision.InputFile";
        const string kPrefRegionCenter = "com.r2b.SplatCollision.RegionCenter";
        const string kPrefRegionHalf = "com.r2b.SplatCollision.RegionHalf";
        const string kPrefSeedPos = "com.r2b.SplatCollision.SeedPos";
        const string kPrefClusterRes = "com.r2b.SplatCollision.ClusterRes";
        const string kPrefVoxelSize = "com.r2b.SplatCollision.VoxelSize";
        const string kPrefVoxelOpacity = "com.r2b.SplatCollision.VoxelOpacity";
        const string kPrefFillMode = "com.r2b.SplatCollision.FillMode";
        const string kPrefFillSize = "com.r2b.SplatCollision.FillSize";
        const string kPrefUseCarve = "com.r2b.SplatCollision.UseCarve";
        const string kPrefCollisionFaces = "com.r2b.SplatCollision.CollisionFaces";
        const string kPrefDisableRendererDuringGenerate = "com.r2b.SplatCollision.DisableRendererDuringGenerate";
        const string kProxyName = "CollisionProxy";
        const string kOutputFolder = "Assets/GaussianAssets/Collision";
        const long kVoxelCountWarnThreshold = 4_000_000;

        readonly FilePickerControl m_FilePicker = new();
        readonly StringBuilder m_Log = new();

        [SerializeField] GaussianSplatRenderer m_TargetRenderer;
        [SerializeField] string m_InputFile;
        [SerializeField] Vector3 m_RegionCenter;
        [SerializeField] Vector3 m_RegionHalfExtents = new(15f, 3.3f, 15f);
        [SerializeField] Vector3 m_SeedPosition;
        [SerializeField] float m_ClusterResolution = 1f;
        [SerializeField] float m_VoxelSize = 0.1f;
        [SerializeField] float m_VoxelOpacity = 0.1f;
        [SerializeField] CollisionFillMode m_FillMode = CollisionFillMode.None;
        [SerializeField] float m_FillSize = 1.6f;
        [SerializeField] bool m_UseCarve;
        [SerializeField] float m_CarveHeight = 1.6f;
        [SerializeField] float m_CarveRadius = 0.2f;
        [SerializeField] bool m_CollisionFaces;
        [SerializeField] bool m_DisableRendererDuringGenerate = true;
        [SerializeField] Vector2 m_LogScroll;

        bool m_RendererDisabledForJob;

        bool m_FoldInput = true;
        bool m_FoldRegion = true;
        bool m_FoldVoxelize = true;
        bool m_FoldFillCarve = true;
        bool m_FoldMeshOutput = true;
        bool m_FoldEstimate = true;

        SplatTransformCliInfo m_CachedCliInfo;
        bool m_CliCacheValid;
        SplatTransformBackgroundJob m_ActiveJob;
        string m_JobVoxelJsonPath;
        string m_JobAssetBase;

        string m_StatusMessage;
        MessageType m_StatusType = MessageType.None;

        [MenuItem("R2B/Gaussian Splats/Generate Splat Collision")]
        public static void Init()
        {
            var window = GetWindow<GaussianGroundCollisionWindow>(false, "Splat Collision", true);
            window.minSize = new Vector2(380, 480);
            window.Show();
        }

        void OnEnable()
        {
            m_InputFile = EditorPrefs.GetString(kPrefInputFile, m_InputFile);
            m_RegionCenter = ReadVector3Pref(kPrefRegionCenter, m_RegionCenter);
            m_RegionHalfExtents = ReadVector3Pref(kPrefRegionHalf, m_RegionHalfExtents);
            m_SeedPosition = ReadVector3Pref(kPrefSeedPos, m_SeedPosition);
            m_ClusterResolution = EditorPrefs.GetFloat(kPrefClusterRes, m_ClusterResolution);
            m_VoxelSize = EditorPrefs.GetFloat(kPrefVoxelSize, m_VoxelSize);
            m_VoxelOpacity = EditorPrefs.GetFloat(kPrefVoxelOpacity, m_VoxelOpacity);
            m_FillMode = (CollisionFillMode)EditorPrefs.GetInt(kPrefFillMode, (int)m_FillMode);
            m_FillSize = EditorPrefs.GetFloat(kPrefFillSize, m_FillSize);
            m_UseCarve = EditorPrefs.GetBool(kPrefUseCarve, m_UseCarve);
            m_CollisionFaces = EditorPrefs.GetBool(kPrefCollisionFaces, m_CollisionFaces);
            m_DisableRendererDuringGenerate = EditorPrefs.GetBool(kPrefDisableRendererDuringGenerate, m_DisableRendererDuringGenerate);

            if (m_TargetRenderer == null)
                m_TargetRenderer = FindSceneRenderer();

            if (!EditorPrefs.HasKey(kPrefRegionCenter + ".x") && m_TargetRenderer != null)
                ApplyDefaultRegionAndSeed();

            EditorApplication.delayCall += DelayedCliDetect;
            EditorApplication.update += OnEditorUpdate;
            HookSceneGui();
        }

        void OnDisable()
        {
            EditorApplication.delayCall -= DelayedCliDetect;
            EditorApplication.update -= OnEditorUpdate;
            SceneView.duringSceneGui -= OnSceneGUI;
        }

        void OnDestroy() => CancelJob();

        void DelayedCliDetect()
        {
            if (this == null) return;
            RefreshCliCache();
            Repaint();
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Splat Collision (splat-transform)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Region & Seed are edited in Unity world space. They are converted to splat-transform engine " +
                "coordinates (PLY + 180° Z, per PlayCanvas voxel format v1.1) for the CLI.\n\n" +
                "There is no separate Preview step — running the CLI and loading the resulting mesh is heavy " +
                "(large point clouds can use a lot of memory/CPU). Use the ⑥ Estimate section below to judge " +
                "whether your settings are reasonable before running Generate. Generate runs the CLI once, " +
                "saves a mesh asset, and creates/updates a 'CollisionProxy' child (with MeshFilter + MeshCollider " +
                "already set up) under the Renderer — no manual scene setup needed.",
                MessageType.None);

            DrawCliRow();
            EditorGUILayout.Space(4);
            DrawInputSection();
            EditorGUILayout.Space(4);
            DrawRegionSection();
            EditorGUILayout.Space(4);
            DrawVoxelizeSection();
            EditorGUILayout.Space(4);
            DrawFillCarveSection();
            EditorGUILayout.Space(4);
            DrawMeshOutputSection();
            EditorGUILayout.Space(4);
            DrawEstimateSection();
            EditorGUILayout.Space(6);

            EditorGUI.BeginChangeCheck();
            m_DisableRendererDuringGenerate = EditorGUILayout.Toggle(
                Tip("Disable Renderer while generating", "Temporarily disables this GaussianSplatRenderer's rendering while Generate runs, so the external splat-transform CLI's GPU work doesn't have to share the GPU with Unity live-rendering this (often huge) point cloud. Reduces the risk of a GPU driver timeout/crash on large scenes. Re-enabled automatically when the job ends."),
                m_DisableRendererDuringGenerate);
            if (EditorGUI.EndChangeCheck())
                SavePrefs();

            bool busy = m_ActiveJob != null && m_ActiveJob.IsRunning;
            using (new EditorGUI.DisabledScope(busy || m_TargetRenderer == null))
            {
                if (GUILayout.Button("Generate"))
                    StartJob();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (busy)
                {
                    if (GUILayout.Button("Cancel", GUILayout.Width(80)))
                    {
                        CancelJob();
                        m_StatusMessage = "Cancelled.";
                        m_StatusType = MessageType.Warning;
                    }
                }
                else
                {
                    if (GUILayout.Button("Open Output") && AssetDatabase.IsValidFolder(kOutputFolder))
                        EditorUtility.RevealInFinder(Path.GetFullPath(kOutputFolder));
                    using (new EditorGUI.DisabledScope(m_TargetRenderer == null))
                    {
                        if (GUILayout.Button("Select Collision Proxy"))
                            SelectExistingProxy();
                    }
                }
            }

            if (busy)
                EditorGUILayout.HelpBox("Running splat-transform in background…", MessageType.Info);

            if (!string.IsNullOrEmpty(m_StatusMessage))
                EditorGUILayout.HelpBox(m_StatusMessage, m_StatusType);

            if (m_Log.Length > 0)
            {
                m_LogScroll = EditorGUILayout.BeginScrollView(m_LogScroll, GUILayout.Height(90));
                EditorGUILayout.TextArea(m_Log.ToString(), GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();
            }
        }

        void DrawCliRow()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                string status = !m_CliCacheValid
                    ? "CLI: unknown"
                    : m_CachedCliInfo.status == SplatTransformCliStatus.Ready
                        ? $"CLI: {m_CachedCliInfo.version}"
                        : m_CachedCliInfo.message;
                EditorGUILayout.LabelField(status, EditorStyles.miniLabel);
                if (GUILayout.Button("Detect", GUILayout.Width(56)))
                    RefreshCliCache();
                if (GUILayout.Button("Install", GUILayout.Width(56)))
                    StartInstall();
            }
        }

        static GUIContent Tip(string label, string tooltip) => new(label, tooltip);

        void DrawInputSection()
        {
            m_FoldInput = EditorGUILayout.Foldout(m_FoldInput, "① Input", true, EditorStyles.foldoutHeader);
            if (!m_FoldInput) return;

            EditorGUILayout.HelpBox(
                "Pick the splat renderer to build collision for, and the PLY/SPZ file that matches it.",
                MessageType.Info);

            EditorGUI.BeginChangeCheck();

            var newRenderer = (GaussianSplatRenderer)EditorGUILayout.ObjectField(
                Tip("Renderer", "The GaussianSplatRenderer this collision mesh will be generated for and attached under (as a 'CollisionProxy' child)."),
                m_TargetRenderer, typeof(GaussianSplatRenderer), true);
            if (newRenderer != m_TargetRenderer)
            {
                m_TargetRenderer = newRenderer;
                if (m_TargetRenderer != null)
                    TryGuessInputFile();
            }

            var pathRect = EditorGUILayout.GetControlRect();
            m_InputFile = m_FilePicker.PathFieldGUI(
                pathRect,
                Tip("PLY / SPZ", "Source point-cloud file the CLI reads splats from. Use 'Guess PLY' to auto-find one matching the Renderer's asset name."),
                m_InputFile, "ply,spz", "SplatCollisionFile");

            if (m_TargetRenderer?.m_Asset != null)
            {
                Bounds assetWorld = GaussianSplatCoords.GetAssetWorldBounds(m_TargetRenderer);
                EditorGUILayout.LabelField(
                    $"Asset bounds (world): center {FormatVec(assetWorld.center)}, size {FormatVec(assetWorld.size)}",
                    EditorStyles.miniLabel);
            }

            if (EditorGUI.EndChangeCheck())
                SavePrefs();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Guess PLY"))
                    TryGuessInputFile();
            }
        }

        void DrawRegionSection()
        {
            m_FoldRegion = EditorGUILayout.Foldout(m_FoldRegion, "② Region & Seed", true, EditorStyles.foldoutHeader);
            if (!m_FoldRegion) return;

            EditorGUILayout.HelpBox(
                "Region is the world-space box the CLI will scan for splats (--filter-box). Seed is a world-space " +
                "point marking 'inside the walkable area' — Floor Fill and Carve both use it. Both are edited in " +
                "Unity world space and drag handles are shown in the Scene view.",
                MessageType.Info);

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.LabelField("Region (filter-box, world space)", EditorStyles.miniBoldLabel);
            m_RegionCenter = EditorGUILayout.Vector3Field(
                Tip("Center", "Center of the world-space box the CLI will scan for splats."), m_RegionCenter);
            m_RegionHalfExtents = EditorGUILayout.Vector3Field(
                Tip("Half Extents", "Half-size of the region box along each axis, in world units."), m_RegionHalfExtents);

            if (m_TargetRenderer != null)
            {
                GaussianSplatCoords.WorldBoxToEngineAabb(
                    m_TargetRenderer.transform, m_RegionCenter, m_RegionHalfExtents,
                    out Vector3 engineMin, out Vector3 engineMax);
                Vector3 engineSeed = GaussianSplatCoords.WorldToEngine(m_TargetRenderer.transform, m_SeedPosition);
                EditorGUILayout.LabelField(
                    $"Engine filter-box: min {FormatVec(engineMin)}, max {FormatVec(engineMax)}",
                    EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"Engine seed: {FormatVec(engineSeed)}", EditorStyles.miniLabel);

                if (GaussianSplatCoords.HasNonIdentityRotation(m_TargetRenderer.transform))
                {
                    EditorGUILayout.HelpBox(
                        $"Renderer rotation {m_TargetRenderer.transform.rotation.eulerAngles} — world boxes are converted to splat-transform engine axes.",
                        MessageType.None);
                }
            }

            EditorGUILayout.LabelField("Seed (--seed-pos, world space)", EditorStyles.miniBoldLabel);
            m_SeedPosition = EditorGUILayout.Vector3Field(
                Tip("Position", "World-space point marking 'inside the walkable area'. Used as the fill origin for Floor Fill and the center of Carve."),
                m_SeedPosition);

            if (EditorGUI.EndChangeCheck())
            {
                SavePrefs();
                SceneView.RepaintAll();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Fit Region"))
                    FitRegionToSelectionOrAsset();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Region = Selected"))
                {
                    SetFromSelection(ref m_RegionCenter);
                    SavePrefs();
                    SceneView.RepaintAll();
                }
                if (GUILayout.Button("Seed = Selected"))
                {
                    SetFromSelection(ref m_SeedPosition);
                    SavePrefs();
                    SceneView.RepaintAll();
                }
                if (GUILayout.Button("Both = Selected"))
                    SetRegionAndSeedFromSelection();
            }
        }

        void DrawVoxelizeSection()
        {
            m_FoldVoxelize = EditorGUILayout.Foldout(m_FoldVoxelize, "③ Voxelize", true, EditorStyles.foldoutHeader);
            if (!m_FoldVoxelize) return;

            EditorGUILayout.HelpBox(
                "Converts splats in the region into a solid voxel grid. Voxel Size is the main smoothness/detail " +
                "trade-off. Cluster Resolution is a separate, earlier step (--filter-cluster): after Region filtering, " +
                "it GPU-voxelizes at this (coarser) resolution and keeps only the splat cluster connected to Seed, " +
                "discarding everything else. On huge scenes this connectivity check is the step most likely to " +
                "overload the GPU — see the presets below, sized per PlayCanvas SuperSplat's Indoor/Outdoor/Object scheme.",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(Tip("Indoor Preset", "Small enclosed space (room/building interior). Fine detail, uses External Fill to seal interior gaps.")))
                    ApplyScenePreset("Indoor", clusterRes: 1f, voxelSize: 0.05f, voxelOpacity: 0.1f, fill: CollisionFillMode.ExternalFill, fillSize: 1.6f);
                if (GUILayout.Button(Tip("Outdoor Preset", "Large open scene (tens to hundreds of meters). Coarser cluster/voxel size so the connectivity check doesn't overload the GPU; Floor Fill for ground.")))
                    ApplyScenePreset("Outdoor", clusterRes: 10f, voxelSize: 0.3f, voxelOpacity: 0.08f, fill: CollisionFillMode.FloorFill, fillSize: 3f);
                if (GUILayout.Button(Tip("Object Preset", "Single small object/prop scan. Very fine detail, uses External Fill to get a watertight shape.")))
                    ApplyScenePreset("Object", clusterRes: 0.3f, voxelSize: 0.02f, voxelOpacity: 0.15f, fill: CollisionFillMode.ExternalFill, fillSize: 0.5f);
            }
            EditorGUILayout.LabelField(
                "Presets are our own starting points (inspired by SuperSplat Studio's 3-preset scheme, exact values not published) — tune from there. " +
                "For very large captures (100m+) you may need to push Cluster Resolution even higher than the Outdoor preset.",
                EditorStyles.miniLabel);

            EditorGUI.BeginChangeCheck();

            m_ClusterResolution = EditorGUILayout.FloatField(
                Tip("Cluster Resolution", "--filter-cluster: after Region filtering, keeps only the splat cluster connected to Seed (GPU flood-fill at this coarse resolution, world units), discarding disconnected islands (e.g. other buildings not reachable from Seed). Larger = cheaper/coarser connectivity check (safer on huge scenes); smaller = finer but GPU-heavier. Default 1."),
                m_ClusterResolution);
            m_VoxelSize = EditorGUILayout.FloatField(
                Tip("Voxel Size", "Size of each collision voxel (world units). Smaller = more detail but more risk of fragmented/jagged geometry from sparse splat data; larger = smoother but coarser. Default 0.05–0.1."),
                m_VoxelSize);
            m_VoxelOpacity = EditorGUILayout.Slider(
                Tip("Voxel Opacity", "Minimum splat density/opacity required to mark a voxel as solid. Lower = fills more marginal/thin areas (less fragmented); higher = stricter, may leave holes. Default 0.1."),
                m_VoxelOpacity, 0.01f, 1f);

            if (EditorGUI.EndChangeCheck())
            {
                SavePrefs();
                SceneView.RepaintAll();
            }
        }

        void DrawFillCarveSection()
        {
            m_FoldFillCarve = EditorGUILayout.Foldout(m_FoldFillCarve, "④ Fill & Carve", true, EditorStyles.foldoutHeader);
            if (!m_FoldFillCarve) return;

            EditorGUILayout.HelpBox(
                "Fill closes gaps/holes so the ground is one continuous surface instead of scattered islands. " +
                "Carve cuts a cylindrical clearance (e.g. around the player) out of the finished mesh.",
                MessageType.Info);

            EditorGUI.BeginChangeCheck();

            m_FillMode = (CollisionFillMode)EditorGUILayout.EnumPopup(
                Tip("Fill", "Post-process pass that closes gaps in the voxel grid. None = raw voxel data only (small gaps become holes/floating islands). Floor Fill = fill upward from Seed (good for ground). External Fill = fill from outside inward."),
                m_FillMode);
            using (new EditorGUI.DisabledScope(m_FillMode == CollisionFillMode.None))
            {
                m_FillSize = EditorGUILayout.FloatField(
                    Tip("Fill Size", "How far the Fill pass reaches (world units) when closing gaps."), m_FillSize);
            }

            if (m_FillMode == CollisionFillMode.None)
            {
                EditorGUILayout.HelpBox(
                    "Fill is off — small gaps or noise in the source splats will show up as disconnected islands/holes " +
                    "in the ground mesh. Enable Floor Fill (exterior/outdoor scenes) or External Fill (interior scenes) " +
                    "— or use one of the presets in ③ Voxelize — to merge them into one continuous surface.",
                    MessageType.Warning);
            }

            m_UseCarve = EditorGUILayout.Toggle(
                Tip("Carve", "Cuts a cylindrical hole out of the finished mesh at the Seed position — use to guarantee clearance around the player/spawn point instead of relying on Region shape."),
                m_UseCarve);
            using (new EditorGUI.DisabledScope(!m_UseCarve))
            {
                m_CarveHeight = EditorGUILayout.FloatField(
                    Tip("Carve Height", "Height of the carved clearance cylinder, in world units."), m_CarveHeight);
                m_CarveRadius = EditorGUILayout.FloatField(
                    Tip("Carve Radius", "Radius of the carved clearance cylinder, in world units."), m_CarveRadius);
            }

            if (EditorGUI.EndChangeCheck())
            {
                SavePrefs();
                SceneView.RepaintAll();
            }
        }

        void DrawMeshOutputSection()
        {
            m_FoldMeshOutput = EditorGUILayout.Foldout(m_FoldMeshOutput, "⑤ Mesh Output", true, EditorStyles.foldoutHeader);
            if (!m_FoldMeshOutput) return;

            EditorGUI.BeginChangeCheck();

            m_CollisionFaces = EditorGUILayout.Toggle(
                Tip("Mesh: Faces (not Smooth)", "Off (default) = smooth marching-cubes-style mesh (-K smooth). On = blocky per-voxel-face mesh (-K faces), useful for chunky stylized collision but not a natural ground surface."),
                m_CollisionFaces);

            if (EditorGUI.EndChangeCheck())
            {
                SavePrefs();
                SceneView.RepaintAll();
            }
        }

        void DrawEstimateSection()
        {
            m_FoldEstimate = EditorGUILayout.Foldout(m_FoldEstimate, "⑥ Estimate (before Generate)", true, EditorStyles.foldoutHeader);
            if (!m_FoldEstimate) return;

            EditorGUILayout.HelpBox(
                "Cheap, local numbers only — no CLI run, no mesh load. Use this to judge whether Region/Voxel " +
                "Size are reasonable before spending time/memory on an actual Generate.",
                MessageType.Info);

            if (m_TargetRenderer == null)
            {
                EditorGUILayout.LabelField("Assign a Renderer above to see an estimate.", EditorStyles.miniLabel);
                return;
            }

            GaussianSplatCoords.WorldBoxToEngineAabb(
                m_TargetRenderer.transform, m_RegionCenter, m_RegionHalfExtents,
                out Vector3 engineMin, out Vector3 engineMax);
            Vector3 size = engineMax - engineMin;
            float voxelSize = Mathf.Max(m_VoxelSize, 0.001f);

            int nx = Mathf.Max(1, Mathf.CeilToInt(size.x / voxelSize));
            int ny = Mathf.Max(1, Mathf.CeilToInt(size.y / voxelSize));
            int nz = Mathf.Max(1, Mathf.CeilToInt(size.z / voxelSize));
            long totalVoxels = (long)nx * ny * nz;
            long floorTris = 2L * nx * nz;
            long solidShellTris = 2L * ((long)nx * ny + (long)ny * nz + (long)nx * nz);

            EditorGUILayout.LabelField(
                $"Voxel grid: {nx:N0} × {ny:N0} × {nz:N0} = {totalVoxels:N0} voxels", EditorStyles.miniLabel);
            EditorGUILayout.LabelField(
                $"Estimated tris: ~{floorTris:N0} for a flat ground surface, up to ~{solidShellTris:N0} if the region is fully solid",
                EditorStyles.miniLabel);

            if (totalVoxels > kVoxelCountWarnThreshold)
            {
                EditorGUILayout.HelpBox(
                    $"Voxel count is very high ({totalVoxels:N0}, warn threshold {kVoxelCountWarnThreshold:N0}) — " +
                    "Generate may be slow or memory-heavy. Consider increasing Voxel Size or shrinking the Region.",
                    MessageType.Warning);
            }

            if (SplatTransformCli.TryReadPlyVertexCount(m_InputFile, out int splatCount, out long fileBytes))
            {
                EditorGUILayout.LabelField(
                    $"Input file: {SplatTransformCli.FormatSplatCount(splatCount)} splats ({SplatTransformCli.FormatByteSize(fileBytes)})",
                    EditorStyles.miniLabel);
            }

            Vector3 fullAssetSize = GaussianSplatCoords.GetAssetFileBounds(m_TargetRenderer).size;
            float clusterRes = Mathf.Max(m_ClusterResolution, 0.001f);
            int cnx = Mathf.Max(1, Mathf.CeilToInt(fullAssetSize.x / clusterRes));
            int cny = Mathf.Max(1, Mathf.CeilToInt(fullAssetSize.y / clusterRes));
            int cnz = Mathf.Max(1, Mathf.CeilToInt(fullAssetSize.z / clusterRes));
            long clusterCells = (long)cnx * cny * cnz;

            EditorGUILayout.LabelField(
                $"Cluster grid (full asset {FormatVec(fullAssetSize)} ÷ Cluster Resolution): " +
                $"{cnx:N0} × {cny:N0} × {cnz:N0} = {clusterCells:N0} cells",
                EditorStyles.miniLabel);

            if (clusterCells > kVoxelCountWarnThreshold)
            {
                EditorGUILayout.HelpBox(
                    $"Filter-cluster grid is very large ({clusterCells:N0} cells). splat-transform sizes this grid " +
                    "from the FULL asset extent, not just your Region — a small Region does not shrink it. This is " +
                    "the step most likely to overload the GPU (WebGPU 'device lost'/hang) on big scenes. Increase " +
                    "Cluster Resolution (e.g. 2–5) to shrink this grid, especially for large outdoor scenes.",
                    MessageType.Warning);
            }
        }

        void ApplyScenePreset(string name, float clusterRes, float voxelSize, float voxelOpacity, CollisionFillMode fill, float fillSize)
        {
            float oldClusterRes = m_ClusterResolution;
            float oldVoxelSize = m_VoxelSize;
            float oldOpacity = m_VoxelOpacity;
            var oldFill = m_FillMode;
            float oldFillSize = m_FillSize;

            m_ClusterResolution = clusterRes;
            m_VoxelSize = voxelSize;
            m_VoxelOpacity = voxelOpacity;
            m_FillMode = fill;
            m_FillSize = fillSize;

            m_Log.AppendLine(
                $"Applied '{name}' preset: Cluster Resolution {oldClusterRes:0.###}→{m_ClusterResolution:0.###}, " +
                $"Voxel Size {oldVoxelSize:0.###}→{m_VoxelSize:0.###}, Voxel Opacity {oldOpacity:0.###}→{m_VoxelOpacity:0.###}, " +
                $"Fill {oldFill}→{m_FillMode}, Fill Size {oldFillSize:0.###}→{m_FillSize:0.###}");

            SavePrefs();
            SceneView.RepaintAll();
        }

        void StartJob()
        {
            if (m_ActiveJob != null && m_ActiveJob.IsRunning)
                return;

            m_Log.Clear();
            try
            {
                if (!m_CliCacheValid || m_CachedCliInfo.status != SplatTransformCliStatus.Ready)
                {
                    RefreshCliCache();
                    if (m_CachedCliInfo.status != SplatTransformCliStatus.Ready)
                        throw new InvalidOperationException(m_CachedCliInfo.message);
                }

                if (m_TargetRenderer == null)
                    throw new InvalidOperationException("Assign a GaussianSplatRenderer.");
                if (string.IsNullOrWhiteSpace(m_InputFile) || !File.Exists(m_InputFile))
                    throw new InvalidOperationException("Select a valid PLY/SPZ file.");

                EnsureOutputFolder();
                string assetBase = m_TargetRenderer.m_Asset != null
                    ? m_TargetRenderer.m_Asset.name
                    : m_TargetRenderer.gameObject.name;
                string voxelPath = Path.GetFullPath(Path.Combine(kOutputFolder, $"{assetBase}_collision.voxel.json"));

                var settings = BuildSettings(voxelPath);
                m_Log.AppendLine(
                    $"Engine filter-box min {FormatVec(settings.filterBoxMin)} max {FormatVec(settings.filterBoxMax)}");
                m_Log.AppendLine($"Engine seed {FormatVec(settings.seedPosition)}");
                if (!SplatTransformCli.TryStartCollisionPipeline(m_CachedCliInfo, settings, out m_ActiveJob, out string err))
                    throw new InvalidOperationException(err);

                if (m_DisableRendererDuringGenerate && m_TargetRenderer.enabled)
                {
                    m_TargetRenderer.enabled = false;
                    m_RendererDisabledForJob = true;
                }

                m_JobVoxelJsonPath = voxelPath;
                m_JobAssetBase = assetBase;
                m_Log.AppendLine(m_ActiveJob.CommandLine);
                m_StatusMessage = "Generate running…";
                m_StatusType = MessageType.Info;
            }
            catch (Exception ex)
            {
                CancelJob();
                m_StatusMessage = ex.Message;
                m_StatusType = MessageType.Error;
                Debug.LogException(ex);
            }

            Repaint();
        }

        void StartInstall()
        {
            if (m_ActiveJob != null && m_ActiveJob.IsRunning) return;
            m_Log.Clear();
            if (!SplatTransformCli.TryStartInstallGlobal(out m_ActiveJob, out string err))
            {
                m_StatusMessage = err;
                m_StatusType = MessageType.Error;
                return;
            }

            m_Log.AppendLine(m_ActiveJob.CommandLine);
            m_StatusMessage = "Installing…";
            m_StatusType = MessageType.Info;
        }

        SplatTransformCollisionSettings BuildSettings(string voxelJsonPath)
        {
            Transform t = m_TargetRenderer.transform;
            GaussianSplatCoords.WorldBoxToEngineAabb(
                t, m_RegionCenter, m_RegionHalfExtents,
                out Vector3 engineMin, out Vector3 engineMax);

            return new SplatTransformCollisionSettings
            {
                inputFile = m_InputFile,
                outputVoxelJson = voxelJsonPath,
                useFilterBox = true,
                filterBoxMin = engineMin,
                filterBoxMax = engineMax,
                useFilterCluster = true,
                clusterResolution = m_ClusterResolution,
                clusterOpacity = 0.999f,
                clusterMinContribution = 0.1f,
                seedPosition = GaussianSplatCoords.WorldToEngine(t, m_SeedPosition),
                voxelSize = m_VoxelSize,
                voxelOpacity = m_VoxelOpacity,
                fillMode = m_FillMode,
                fillSize = m_FillSize,
                useCarve = m_UseCarve,
                carveHeight = m_CarveHeight,
                carveRadius = m_CarveRadius,
                generateCollisionMesh = true,
                collisionFaces = m_CollisionFaces
            };
        }

        void OnEditorUpdate()
        {
            if (m_ActiveJob == null) return;

            m_ActiveJob.DrainLogs(line => m_Log.AppendLine(line));

            if (m_ActiveJob.IsRunning)
            {
                Repaint();
                return;
            }

            if (!m_ActiveJob.TryGetResult(out int code, out string output))
                return;

            string voxelPath = m_JobVoxelJsonPath;
            string assetBase = m_JobAssetBase;
            CancelJob();

            try
            {
                if (code != 0)
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(output) ? $"Exit code {code}" : output);

                FinishJob(voxelPath, assetBase);
            }
            catch (Exception ex)
            {
                m_StatusMessage = ex.Message;
                m_StatusType = MessageType.Error;
                Debug.LogException(ex);
            }

            Repaint();
        }

        void FinishJob(string voxelPath, string assetBase)
        {
            string glbPath = SplatTransformCli.GetExpectedCollisionGlbPath(voxelPath);
            if (!File.Exists(glbPath))
                throw new InvalidOperationException($"Missing: {glbPath}");

            var mesh = GlbMeshLoader.LoadFirstMesh(glbPath);
            if (mesh.vertexCount == 0 || mesh.triangles.Length == 0)
            {
                DestroyImmediate(mesh);
                throw new InvalidOperationException(
                    "Generated mesh is empty — Region/Seed/Voxel Opacity likely excluded all splats. " +
                    "Widen the Region, check Seed is inside the splat cloud, or lower Voxel Opacity.");
            }
            GaussianSplatCoords.TransformMeshEngineToFile(mesh);

            if (m_TargetRenderer == null)
                throw new InvalidOperationException("Renderer lost.");

            string assetPath = AssetDatabase.GenerateUniqueAssetPath($"{kOutputFolder}/{assetBase}_collision.asset");
            AssetDatabase.CreateAsset(mesh, assetPath);
            AssetDatabase.SaveAssets();

            var proxy = GetOrCreateProxy(m_TargetRenderer);
            var mf = proxy.GetComponent<MeshFilter>() ?? proxy.AddComponent<MeshFilter>();
            var mc = proxy.GetComponent<MeshCollider>() ?? proxy.AddComponent<MeshCollider>();
            mf.sharedMesh = mesh;
            mc.sharedMesh = mesh;
            mc.convex = false;

            EditorUtility.SetDirty(proxy);
            m_StatusMessage =
                $"Saved {assetPath} ({mesh.triangles.Length / 3:N0} tris) → created/updated '{kProxyName}' " +
                $"(MeshFilter+MeshCollider) under '{m_TargetRenderer.name}'";
            m_StatusType = MessageType.Info;
            Selection.activeGameObject = proxy;
            EditorGUIUtility.PingObject(proxy);
        }

        void SelectExistingProxy()
        {
            if (m_TargetRenderer == null) return;

            Transform existing = m_TargetRenderer.transform.Find(kProxyName);
            if (existing == null)
            {
                m_StatusMessage = $"No '{kProxyName}' yet — run Generate first.";
                m_StatusType = MessageType.Warning;
                return;
            }

            Selection.activeGameObject = existing.gameObject;
            EditorGUIUtility.PingObject(existing.gameObject);
        }

        void CancelJob()
        {
            m_ActiveJob?.Cancel();
            m_ActiveJob?.Dispose();
            m_ActiveJob = null;

            if (m_RendererDisabledForJob)
            {
                if (m_TargetRenderer != null)
                    m_TargetRenderer.enabled = true;
                m_RendererDisabledForJob = false;
            }
        }

        void RefreshCliCache()
        {
            m_CachedCliInfo = SplatTransformCli.Detect();
            m_CliCacheValid = true;
        }

        void HookSceneGui()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            SceneView.duringSceneGui += OnSceneGUI;
        }

        void OnSceneGUI(SceneView view)
        {
            Matrix4x4 prevMatrix = Handles.matrix;
            Handles.matrix = Matrix4x4.identity;

            if (m_TargetRenderer != null)
            {
                Bounds assetWorld = GaussianSplatCoords.GetAssetWorldBounds(m_TargetRenderer);
                Handles.color = new Color(0.2f, 1f, 0.35f, 0.85f);
                Handles.DrawWireCube(assetWorld.center, Vector3.Max(assetWorld.size, Vector3.one * 0.1f));
            }

            Handles.color = new Color(1f, 0.85f, 0.2f, 0.95f);
            Handles.DrawWireCube(m_RegionCenter, Vector3.Max(m_RegionHalfExtents * 2f, Vector3.one * 0.1f));

            EditorGUI.BeginChangeCheck();
            Handles.color = Color.cyan;
            Vector3 newSeed = Handles.PositionHandle(m_SeedPosition, Quaternion.identity);
            Handles.SphereHandleCap(0, m_SeedPosition, Quaternion.identity, 0.25f, EventType.Repaint);

            Handles.color = new Color(1f, 0.85f, 0.2f, 0.95f);
            Vector3 newRegion = Handles.PositionHandle(m_RegionCenter, Quaternion.identity);
            if (EditorGUI.EndChangeCheck())
            {
                m_RegionCenter = newRegion;
                m_SeedPosition = newSeed;
                SavePrefs();
                Repaint();
            }

            Handles.matrix = prevMatrix;
        }

        static GameObject GetOrCreateProxy(GaussianSplatRenderer renderer)
        {
            Transform existing = renderer.transform.Find(kProxyName);
            if (existing != null)
                return existing.gameObject;

            var go = new GameObject(kProxyName);
            Undo.RegisterCreatedObjectUndo(go, "Create Collision Proxy");
            go.transform.SetParent(renderer.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            return go;
        }

        static void EnsureOutputFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/GaussianAssets"))
                AssetDatabase.CreateFolder("Assets", "GaussianAssets");
            if (!AssetDatabase.IsValidFolder(kOutputFolder))
                AssetDatabase.CreateFolder("Assets/GaussianAssets", "Collision");
        }

        void TryGuessInputFile()
        {
            if (m_TargetRenderer?.m_Asset == null) return;
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string name = m_TargetRenderer.m_Asset.name;
            foreach (var path in new[] { $"{name}.ply", $"{name}.spz", "point_cloud.ply" })
            {
                string full = Path.Combine(root, path);
                if (!File.Exists(full)) continue;
                m_InputFile = full;
                SavePrefs();
                return;
            }
        }

        static void SetFromSelection(ref Vector3 target)
        {
            if (Selection.activeTransform == null) return;
            target = Selection.activeTransform.position;
        }

        void SetRegionAndSeedFromSelection()
        {
            if (Selection.activeTransform == null) return;
            m_RegionCenter = Selection.activeTransform.position;
            m_SeedPosition = m_RegionCenter;
            SavePrefs();
            SceneView.RepaintAll();
        }

        void FitRegionToSelectionOrAsset()
        {
            if (m_TargetRenderer == null)
                return;

            if (Selection.activeTransform != null)
            {
                m_RegionCenter = Selection.activeTransform.position;
                m_SeedPosition = m_RegionCenter;
            }
            else
            {
                Bounds assetWorld = GaussianSplatCoords.GetAssetWorldBounds(m_TargetRenderer);
                m_RegionCenter = assetWorld.center;
                m_SeedPosition = m_RegionCenter;
            }

            m_RegionHalfExtents = new Vector3(15f, 3.3f, 15f);
            SavePrefs();
            SceneView.RepaintAll();
        }

        void ApplyDefaultRegionAndSeed()
        {
            if (m_TargetRenderer == null)
                return;

            if (Selection.activeTransform != null)
            {
                m_RegionCenter = Selection.activeTransform.position;
                m_SeedPosition = m_RegionCenter;
                return;
            }

            Transform t = m_TargetRenderer.transform;
            Bounds assetWorld = GaussianSplatCoords.GetAssetWorldBounds(m_TargetRenderer);
            m_RegionCenter = assetWorld.center;
            m_RegionCenter.y = t.position.y;
            m_SeedPosition = t.position;
            m_RegionHalfExtents = new Vector3(15f, 3.3f, 15f);
        }

        static string FormatVec(Vector3 v) => $"({v.x:0.##}, {v.y:0.##}, {v.z:0.##})";

        void SavePrefs()
        {
            EditorPrefs.SetString(kPrefInputFile, m_InputFile ?? string.Empty);
            WriteVector3Pref(kPrefRegionCenter, m_RegionCenter);
            WriteVector3Pref(kPrefRegionHalf, m_RegionHalfExtents);
            WriteVector3Pref(kPrefSeedPos, m_SeedPosition);
            EditorPrefs.SetFloat(kPrefClusterRes, m_ClusterResolution);
            EditorPrefs.SetFloat(kPrefVoxelSize, m_VoxelSize);
            EditorPrefs.SetFloat(kPrefVoxelOpacity, m_VoxelOpacity);
            EditorPrefs.SetInt(kPrefFillMode, (int)m_FillMode);
            EditorPrefs.SetFloat(kPrefFillSize, m_FillSize);
            EditorPrefs.SetBool(kPrefUseCarve, m_UseCarve);
            EditorPrefs.SetBool(kPrefCollisionFaces, m_CollisionFaces);
            EditorPrefs.SetBool(kPrefDisableRendererDuringGenerate, m_DisableRendererDuringGenerate);
        }

        static GaussianSplatRenderer FindSceneRenderer()
        {
            if (Selection.activeGameObject != null &&
                Selection.activeGameObject.TryGetComponent(out GaussianSplatRenderer r))
                return r;
            return FindFirstObjectByType<GaussianSplatRenderer>();
        }

        static Vector3 ReadVector3Pref(string key, Vector3 fallback)
        {
            if (!EditorPrefs.HasKey(key + ".x")) return fallback;
            return new Vector3(
                EditorPrefs.GetFloat(key + ".x"),
                EditorPrefs.GetFloat(key + ".y"),
                EditorPrefs.GetFloat(key + ".z"));
        }

        static void WriteVector3Pref(string key, Vector3 v)
        {
            EditorPrefs.SetFloat(key + ".x", v.x);
            EditorPrefs.SetFloat(key + ".y", v.y);
            EditorPrefs.SetFloat(key + ".z", v.z);
        }

    }
}
