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
        const string kPrefShowPreview = "com.r2b.SplatCollision.ShowPreview";
        const string kProxyName = "CollisionProxy";
        const string kOutputFolder = "Assets/GaussianAssets/Collision";

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
        [SerializeField] bool m_ShowPreview = true;
        [SerializeField] Vector2 m_LogScroll;

        SplatTransformCliInfo m_CachedCliInfo;
        bool m_CliCacheValid;
        SplatTransformBackgroundJob m_ActiveJob;
        bool m_JobIsPreview;
        string m_JobVoxelJsonPath;
        string m_JobAssetBase;

        Mesh m_PreviewMesh;
        Matrix4x4 m_PreviewMatrix = Matrix4x4.identity;
        Material m_PreviewMaterial;
        string m_StatusMessage;
        MessageType m_StatusType = MessageType.None;

        static readonly int kColorProp = Shader.PropertyToID("_Color");

        [MenuItem("R2B/Gaussian Splats/Generate Splat Collision")]
        [MenuItem("Tools/R2B/Gaussian Splats/Generate Splat Collision")]
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
            m_ShowPreview = EditorPrefs.GetBool(kPrefShowPreview, m_ShowPreview);

            if (m_TargetRenderer == null)
                m_TargetRenderer = FindSceneRenderer();

            if (!EditorPrefs.HasKey(kPrefRegionCenter + ".x") && m_TargetRenderer != null)
                ApplyDefaultRegionAndSeed();

            EditorApplication.delayCall += DelayedCliDetect;
            EditorApplication.update += OnEditorUpdate;
            UpdateScenePreviewHook();
        }

        void OnDisable()
        {
            EditorApplication.delayCall -= DelayedCliDetect;
            EditorApplication.update -= OnEditorUpdate;
            SceneView.duringSceneGui -= OnSceneGUI;
            DestroyPreviewMesh();
            DestroyPreviewMaterial();
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
                "Region & Seed are in Unity world space and converted to PLY coordinates for splat-transform. " +
                "Place Seed on the walkable splat island (e.g. select your VR player and use the buttons below). " +
                "The collision preview uses the same transform as GaussianSplatRenderer.",
                MessageType.None);

            DrawCliRow();
            EditorGUILayout.Space(4);
            DrawInputSection();
            EditorGUILayout.Space(4);
            DrawPipelineSection();
            EditorGUILayout.Space(6);

            bool busy = m_ActiveJob != null && m_ActiveJob.IsRunning;
            using (new EditorGUI.DisabledScope(busy))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Preview"))
                        StartJob(preview: true);
                    GUI.enabled = m_TargetRenderer != null;
                    if (GUILayout.Button("Generate"))
                        StartJob(preview: false);
                    GUI.enabled = true;
                }
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
                    if (GUILayout.Button("Clear Preview", GUILayout.Width(100)))
                        ClearPreview();
                    if (GUILayout.Button("Open Output") && AssetDatabase.IsValidFolder(kOutputFolder))
                        EditorUtility.RevealInFinder(Path.GetFullPath(kOutputFolder));
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

        void DrawInputSection()
        {
            EditorGUILayout.LabelField("Input", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();

            var newRenderer = (GaussianSplatRenderer)EditorGUILayout.ObjectField(
                "Renderer", m_TargetRenderer, typeof(GaussianSplatRenderer), true);
            if (newRenderer != m_TargetRenderer)
            {
                m_TargetRenderer = newRenderer;
                if (m_TargetRenderer != null)
                    TryGuessInputFile();
            }

            var pathRect = EditorGUILayout.GetControlRect();
            m_InputFile = m_FilePicker.PathFieldGUI(pathRect, new GUIContent("PLY / SPZ"), m_InputFile, "ply,spz", "SplatCollisionFile");

            if (m_TargetRenderer?.m_Asset != null)
            {
                Bounds assetWorld = GaussianSplatCoords.GetAssetWorldBounds(m_TargetRenderer);
                EditorGUILayout.LabelField(
                    $"Asset bounds (world): center {FormatVec(assetWorld.center)}, size {FormatVec(assetWorld.size)}",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.LabelField("Region (filter-box, world space)", EditorStyles.miniBoldLabel);
            m_RegionCenter = EditorGUILayout.Vector3Field("Center", m_RegionCenter);
            m_RegionHalfExtents = EditorGUILayout.Vector3Field("Half Extents", m_RegionHalfExtents);

            if (m_TargetRenderer != null)
            {
                GaussianSplatCoords.WorldBoxToFileAabb(
                    m_TargetRenderer.transform, m_RegionCenter, m_RegionHalfExtents,
                    out Vector3 fileMin, out Vector3 fileMax);
                Vector3 fileSeed = GaussianSplatCoords.WorldToFile(m_TargetRenderer.transform, m_SeedPosition);
                EditorGUILayout.LabelField(
                    $"PLY filter-box: min {FormatVec(fileMin)}, max {FormatVec(fileMax)}",
                    EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"PLY seed: {FormatVec(fileSeed)}", EditorStyles.miniLabel);

                if (GaussianSplatCoords.HasNonIdentityRotation(m_TargetRenderer.transform))
                {
                    EditorGUILayout.HelpBox(
                        $"Renderer rotation {m_TargetRenderer.transform.rotation.eulerAngles} — world boxes are converted to PLY axes for splat-transform.",
                        MessageType.None);
                }
            }

            EditorGUILayout.LabelField("Seed (--seed-pos, world space)", EditorStyles.miniBoldLabel);
            m_SeedPosition = EditorGUILayout.Vector3Field("Position", m_SeedPosition);

            m_ShowPreview = EditorGUILayout.Toggle("Scene Preview", m_ShowPreview);

            if (EditorGUI.EndChangeCheck())
            {
                SavePrefs();
                UpdateScenePreviewHook();
                SceneView.RepaintAll();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Guess PLY"))
                    TryGuessInputFile();
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

        void DrawPipelineSection()
        {
            EditorGUILayout.LabelField("Pipeline", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();

            m_ClusterResolution = EditorGUILayout.FloatField("Cluster Resolution", m_ClusterResolution);
            m_VoxelSize = EditorGUILayout.FloatField("Voxel Size", m_VoxelSize);
            m_VoxelOpacity = EditorGUILayout.Slider("Voxel Opacity", m_VoxelOpacity, 0.01f, 1f);

            m_FillMode = (CollisionFillMode)EditorGUILayout.EnumPopup("Fill", m_FillMode);
            using (new EditorGUI.DisabledScope(m_FillMode == CollisionFillMode.None))
                m_FillSize = EditorGUILayout.FloatField("Fill Size", m_FillSize);

            m_UseCarve = EditorGUILayout.Toggle("Carve", m_UseCarve);
            using (new EditorGUI.DisabledScope(!m_UseCarve))
            {
                m_CarveHeight = EditorGUILayout.FloatField("Carve Height", m_CarveHeight);
                m_CarveRadius = EditorGUILayout.FloatField("Carve Radius", m_CarveRadius);
            }

            m_CollisionFaces = EditorGUILayout.Toggle("Mesh: Faces (not Smooth)", m_CollisionFaces);

            if (EditorGUI.EndChangeCheck())
            {
                SavePrefs();
                SceneView.RepaintAll();
            }
        }

        void StartJob(bool preview)
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
                string suffix = preview ? "_preview" : "_collision";
                string voxelPath = Path.GetFullPath(Path.Combine(kOutputFolder, $"{assetBase}{suffix}.voxel.json"));

                var settings = BuildSettings(voxelPath);
                m_Log.AppendLine(
                    $"PLY filter-box min {FormatVec(settings.filterBoxMin)} max {FormatVec(settings.filterBoxMax)}");
                m_Log.AppendLine($"PLY seed {FormatVec(settings.seedPosition)}");
                if (!SplatTransformCli.TryStartCollisionPipeline(m_CachedCliInfo, settings, out m_ActiveJob, out string err))
                    throw new InvalidOperationException(err);

                m_JobIsPreview = preview;
                m_JobVoxelJsonPath = voxelPath;
                m_JobAssetBase = assetBase;
                m_Log.AppendLine(m_ActiveJob.CommandLine);
                m_StatusMessage = preview ? "Preview running…" : "Generate running…";
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
            GaussianSplatCoords.WorldBoxToFileAabb(
                t, m_RegionCenter, m_RegionHalfExtents,
                out Vector3 fileMin, out Vector3 fileMax);

            return new SplatTransformCollisionSettings
            {
                inputFile = m_InputFile,
                outputVoxelJson = voxelJsonPath,
                useFilterBox = true,
                filterBoxMin = fileMin,
                filterBoxMax = fileMax,
                useFilterCluster = true,
                clusterResolution = m_ClusterResolution,
                clusterOpacity = 0.999f,
                clusterMinContribution = 0.1f,
                seedPosition = GaussianSplatCoords.WorldToFile(t, m_SeedPosition),
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

            bool wasPreview = m_JobIsPreview;
            string voxelPath = m_JobVoxelJsonPath;
            string assetBase = m_JobAssetBase;
            CancelJob();

            try
            {
                if (code != 0)
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(output) ? $"Exit code {code}" : output);

                FinishJob(wasPreview, voxelPath, assetBase);
            }
            catch (Exception ex)
            {
                ClearPreview();
                m_StatusMessage = ex.Message;
                m_StatusType = MessageType.Error;
                Debug.LogException(ex);
            }

            Repaint();
        }

        void FinishJob(bool preview, string voxelPath, string assetBase)
        {
            string glbPath = SplatTransformCli.GetExpectedCollisionGlbPath(voxelPath);
            if (!File.Exists(glbPath))
                throw new InvalidOperationException($"Missing: {glbPath}");

            var mesh = GlbMeshLoader.LoadFirstMesh(glbPath);

            DestroyPreviewMesh();
            m_PreviewMesh = mesh;
            m_PreviewMatrix = m_TargetRenderer != null
                ? GaussianSplatCoords.FileToWorldMatrix(m_TargetRenderer.transform)
                : Matrix4x4.identity;
            UpdateScenePreviewHook();
            SceneView.RepaintAll();

            if (preview)
            {
                Bounds meshWorld = GaussianSplatCoords.GetMeshWorldBounds(mesh, m_TargetRenderer.transform);
                m_StatusMessage =
                    $"Preview: {Path.GetFileName(glbPath)} ({mesh.vertexCount:N0} verts, {mesh.triangles.Length / 3:N0} tris). " +
                    $"World center {FormatVec(meshWorld.center)}";
                m_StatusType = MessageType.Info;
                return;
            }

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
            m_StatusMessage = $"Saved {assetPath} ({mesh.triangles.Length / 3:N0} tris)";
            m_StatusType = MessageType.Info;
            Selection.activeGameObject = proxy;
        }

        void CancelJob()
        {
            m_ActiveJob?.Cancel();
            m_ActiveJob?.Dispose();
            m_ActiveJob = null;
        }

        void RefreshCliCache()
        {
            m_CachedCliInfo = SplatTransformCli.Detect();
            m_CliCacheValid = true;
        }

        void ClearPreview()
        {
            DestroyPreviewMesh();
            UpdateScenePreviewHook();
            m_StatusMessage = "Preview cleared.";
            m_StatusType = MessageType.Info;
            SceneView.RepaintAll();
        }

        void UpdateScenePreviewHook()
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

            if (m_ShowPreview && m_PreviewMesh != null && m_TargetRenderer != null)
            {
                Bounds meshWorld = GaussianSplatCoords.GetMeshWorldBounds(m_PreviewMesh, m_TargetRenderer.transform);
                Handles.color = new Color(1f, 0.2f, 0.9f, 0.95f);
                Handles.DrawWireCube(meshWorld.center, Vector3.Max(meshWorld.size, Vector3.one * 0.05f));

                var mat = GetPreviewMaterial();
                Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;
                mat.SetColor(kColorProp, new Color(0.2f, 0.75f, 1f, 0.35f));
                mat.SetPass(0);
                Graphics.DrawMeshNow(m_PreviewMesh, m_PreviewMatrix);

                bool wf = GL.wireframe;
                GL.wireframe = true;
                mat.SetColor(kColorProp, new Color(0.1f, 0.5f, 0.9f, 0.85f));
                mat.SetPass(0);
                Graphics.DrawMeshNow(m_PreviewMesh, m_PreviewMatrix);
                GL.wireframe = wf;
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
            EditorPrefs.SetBool(kPrefShowPreview, m_ShowPreview);
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

        void DestroyPreviewMesh()
        {
            if (m_PreviewMesh == null) return;
            DestroyImmediate(m_PreviewMesh);
            m_PreviewMesh = null;
        }

        void DestroyPreviewMaterial()
        {
            if (m_PreviewMaterial == null) return;
            DestroyImmediate(m_PreviewMaterial);
            m_PreviewMaterial = null;
        }

        Material GetPreviewMaterial()
        {
            if (m_PreviewMaterial != null) return m_PreviewMaterial;
            m_PreviewMaterial = new Material(Shader.Find("Hidden/Internal-Colored"))
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            m_PreviewMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m_PreviewMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m_PreviewMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            m_PreviewMaterial.SetInt("_ZWrite", 0);
            return m_PreviewMaterial;
        }
    }
}
