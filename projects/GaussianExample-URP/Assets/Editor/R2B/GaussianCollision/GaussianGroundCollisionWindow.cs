// SPDX-License-Identifier: MIT

using System;
using System.IO;
using GaussianSplatting.Editor.Utils;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace R2B.Editor.GaussianCollision
{
    public class GaussianGroundCollisionWindow : EditorWindow
    {
        const string kPrefInputFile = "com.r2b.GroundCollision.InputFile";
        const string kPrefCellSize = "com.r2b.GroundCollision.CellSize";
        const string kPrefOpacity = "com.r2b.GroundCollision.Opacity";
        const string kPrefPercentile = "com.r2b.GroundCollision.Percentile";
        const string kPrefSmoothing = "com.r2b.GroundCollision.Smoothing";
        const string kPrefHoleFill = "com.r2b.GroundCollision.HoleFill";
        const string kPrefBakeTransform = "com.r2b.GroundCollision.BakeTransform";
        const string kPrefPreview = "com.r2b.GroundCollision.Preview";
        const string kPrefTightBounds = "com.r2b.GroundCollision.TightBounds";
        const string kProxyName = "GroundCollisionProxy";

        readonly FilePickerControl m_FilePicker = new();

        [SerializeField] GaussianSplatRenderer m_TargetRenderer;
        [SerializeField] string m_InputFile;
        [SerializeField] float m_CellSize = 0.2f;
        [SerializeField] float m_OpacityThreshold = 0.4f;
        [Range(0f, 1f)]
        [SerializeField] float m_HeightPercentile = 0.85f;
        [SerializeField] int m_SmoothingIterations = 2;
        [SerializeField] int m_HoleFillIterations = 8;
        [SerializeField] bool m_BakeTransform;
        [SerializeField] bool m_ShowPreview = true;
        [SerializeField] bool m_UseTightBounds = true;

        HeightfieldData m_PreviewHeightfield;
        Mesh m_PreviewMesh;
        Matrix4x4 m_PreviewMatrix = Matrix4x4.identity;
        string m_StatusMessage;
        MessageType m_StatusType = MessageType.None;

        [MenuItem("R2B/Gaussian Splats/Generate Ground Collision")]
        public static void Init()
        {
            var window = GetWindow<GaussianGroundCollisionWindow>(false, "Ground Collision", true);
            window.minSize = new Vector2(360, 460);
            window.Show();
        }

        void OnEnable()
        {
            m_InputFile = EditorPrefs.GetString(kPrefInputFile, m_InputFile);
            m_CellSize = EditorPrefs.GetFloat(kPrefCellSize, m_CellSize);
            m_OpacityThreshold = EditorPrefs.GetFloat(kPrefOpacity, m_OpacityThreshold);
            m_HeightPercentile = EditorPrefs.GetFloat(kPrefPercentile, m_HeightPercentile);
            m_SmoothingIterations = EditorPrefs.GetInt(kPrefSmoothing, m_SmoothingIterations);
            m_HoleFillIterations = EditorPrefs.GetInt(kPrefHoleFill, m_HoleFillIterations);
            m_BakeTransform = EditorPrefs.GetBool(kPrefBakeTransform, m_BakeTransform);
            m_ShowPreview = EditorPrefs.GetBool(kPrefPreview, m_ShowPreview);
            m_UseTightBounds = EditorPrefs.GetBool(kPrefTightBounds, m_UseTightBounds);

            if (m_TargetRenderer == null)
                m_TargetRenderer = FindSceneRenderer();

            SceneView.duringSceneGui -= OnSceneGUI;
            SceneView.duringSceneGui += OnSceneGUI;
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            DestroyPreviewMesh();
        }

        static GaussianSplatRenderer FindSceneRenderer()
        {
            if (Selection.activeGameObject != null &&
                Selection.activeGameObject.TryGetComponent(out GaussianSplatRenderer selected))
                return selected;

            return FindFirstObjectByType<GaussianSplatRenderer>();
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Gaussian Splat Ground Collision", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Preview uses subsampled splats and a smaller grid for speed. Generate uses the full file.",
                MessageType.Info);
            EditorGUILayout.Space(4);

            EditorGUI.BeginChangeCheck();
            m_TargetRenderer = (GaussianSplatRenderer)EditorGUILayout.ObjectField(
                "Target Renderer", m_TargetRenderer, typeof(GaussianSplatRenderer), true);

            var pathRect = EditorGUILayout.GetControlRect();
            m_InputFile = m_FilePicker.PathFieldGUI(pathRect, new GUIContent("Input PLY/SPZ File"), m_InputFile, "ply,spz", "GroundCollisionFile");
            m_CellSize = EditorGUILayout.FloatField("Cell Size (m)", m_CellSize);
            m_OpacityThreshold = EditorGUILayout.Slider("Opacity Threshold", m_OpacityThreshold, 0f, 1f);
            m_HeightPercentile = EditorGUILayout.Slider("Height Percentile", m_HeightPercentile, 0.5f, 1f);
            m_SmoothingIterations = EditorGUILayout.IntSlider("Smoothing Iterations", m_SmoothingIterations, 0, 8);
            m_HoleFillIterations = EditorGUILayout.IntSlider("Hole Fill Iterations", m_HoleFillIterations, 0, 16);
            m_UseTightBounds = EditorGUILayout.Toggle("Tight Region (XZ)", m_UseTightBounds);
            m_BakeTransform = EditorGUILayout.Toggle("Bake Transform", m_BakeTransform);
            m_ShowPreview = EditorGUILayout.Toggle("Scene Preview", m_ShowPreview);

            if (EditorGUI.EndChangeCheck())
                SavePrefs();

            if (m_TargetRenderer != null && GUILayout.Button("Guess PLY From Asset Name"))
                TryGuessInputFile();

            EditorGUILayout.Space(8);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Preview Heightfield"))
                    RunPreview();

                GUI.enabled = m_TargetRenderer != null;
                if (GUILayout.Button("Generate Ground Collision"))
                    GenerateCollision();
                GUI.enabled = true;
            }

            if (!string.IsNullOrEmpty(m_StatusMessage))
                EditorGUILayout.HelpBox(m_StatusMessage, m_StatusType);

            if (m_PreviewHeightfield != null)
            {
                int validCells = 0;
                foreach (var v in m_PreviewHeightfield.valid)
                {
                    if (v)
                        validCells++;
                }

                EditorGUILayout.HelpBox(
                    $"Preview grid: {m_PreviewHeightfield.width} x {m_PreviewHeightfield.height}, " +
                    $"cell {m_PreviewHeightfield.cellSize:F3}m, valid cells {validCells:N0}",
                    MessageType.Info);
            }
        }

        void SavePrefs()
        {
            EditorPrefs.SetString(kPrefInputFile, m_InputFile ?? string.Empty);
            EditorPrefs.SetFloat(kPrefCellSize, m_CellSize);
            EditorPrefs.SetFloat(kPrefOpacity, m_OpacityThreshold);
            EditorPrefs.SetFloat(kPrefPercentile, m_HeightPercentile);
            EditorPrefs.SetInt(kPrefSmoothing, m_SmoothingIterations);
            EditorPrefs.SetInt(kPrefHoleFill, m_HoleFillIterations);
            EditorPrefs.SetBool(kPrefBakeTransform, m_BakeTransform);
            EditorPrefs.SetBool(kPrefPreview, m_ShowPreview);
            EditorPrefs.SetBool(kPrefTightBounds, m_UseTightBounds);
        }

        void TryGuessInputFile()
        {
            if (m_TargetRenderer == null || m_TargetRenderer.m_Asset == null)
                return;

            string assetName = m_TargetRenderer.m_Asset.name;
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string[] candidates =
            {
                Path.Combine(projectRoot, $"{assetName}.ply"),
                Path.Combine(projectRoot, $"{assetName}.spz"),
                Path.Combine(projectRoot, "point_cloud.ply"),
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    m_InputFile = candidate;
                    SavePrefs();
                    m_StatusMessage = $"Using {candidate}";
                    m_StatusType = MessageType.Info;
                    return;
                }
            }

            m_StatusMessage = "Could not find a matching PLY/SPZ next to the project root. Please pick the file manually.";
            m_StatusType = MessageType.Warning;
        }

        GroundExtractionSettings BuildSettings(bool forPreview)
        {
            return new GroundExtractionSettings
            {
                cellSize = m_CellSize,
                opacityThreshold = m_OpacityThreshold,
                heightPercentile = m_HeightPercentile,
                applyTransform = m_BakeTransform,
                transform = m_TargetRenderer != null
                    ? m_TargetRenderer.transform.localToWorldMatrix
                    : Matrix4x4.identity,
                maxGridDimension = forPreview
                    ? GroundExtractionSettings.kDefaultMaxGridDimension
                    : GroundExtractionSettings.kGenerateMaxGridDimension,
                splatStride = forPreview ? 20 : 1,
                useTightBounds = m_UseTightBounds,
                tightBoundsPercentile = 0.02f
            };
        }

        void RunPreview()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(m_InputFile) || !File.Exists(m_InputFile))
                    throw new InvalidOperationException("Select a valid PLY/SPZ file.");

                EditorUtility.DisplayProgressBar("Ground Collision", "Reading and extracting heightfield (preview)...", 0.3f);
                var raw = SplatGroundExtractor.ExtractFromFile(m_InputFile, BuildSettings(forPreview: true));

                EditorUtility.DisplayProgressBar("Ground Collision", "Processing heightfield...", 0.7f);
                int holeFill = Mathf.Min(m_HoleFillIterations, 4);
                int smooth = Mathf.Min(m_SmoothingIterations, 2);
                m_PreviewHeightfield = HeightfieldMeshBuilder.Process(raw, holeFill, smooth);

                DestroyPreviewMesh();
                m_PreviewMesh = HeightfieldMeshBuilder.BuildPreviewMesh(m_PreviewHeightfield);
                m_PreviewMatrix = GetPreviewMatrix();

                m_StatusMessage = "Preview updated (subsampled for speed).";
                m_StatusType = MessageType.Info;
                SceneView.RepaintAll();
            }
            catch (Exception ex)
            {
                ClearPreview();
                m_StatusMessage = ex.Message;
                m_StatusType = MessageType.Error;
                Debug.LogException(ex);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        void GenerateCollision()
        {
            try
            {
                if (m_TargetRenderer == null)
                    throw new InvalidOperationException("Assign a GaussianSplatRenderer from the scene.");
                if (string.IsNullOrWhiteSpace(m_InputFile) || !File.Exists(m_InputFile))
                    throw new InvalidOperationException("Select a valid PLY/SPZ file.");

                EditorUtility.DisplayProgressBar("Ground Collision", "Extracting heightfield (full)...", 0.15f);
                var raw = SplatGroundExtractor.ExtractFromFile(m_InputFile, BuildSettings(forPreview: false));
                EditorUtility.DisplayProgressBar("Ground Collision", "Processing heightfield...", 0.45f);
                var processed = HeightfieldMeshBuilder.Process(raw, m_HoleFillIterations, m_SmoothingIterations);
                EditorUtility.DisplayProgressBar("Ground Collision", "Building mesh...", 0.7f);
                var mesh = HeightfieldMeshBuilder.BuildMesh(processed);

                string outputFolder = "Assets/GaussianAssets/Collision";
                if (!AssetDatabase.IsValidFolder("Assets/GaussianAssets"))
                    AssetDatabase.CreateFolder("Assets", "GaussianAssets");
                if (!AssetDatabase.IsValidFolder(outputFolder))
                    AssetDatabase.CreateFolder("Assets/GaussianAssets", "Collision");

                string assetBase = m_TargetRenderer.m_Asset != null
                    ? m_TargetRenderer.m_Asset.name
                    : m_TargetRenderer.gameObject.name;
                string meshPath = AssetDatabase.GenerateUniqueAssetPath($"{outputFolder}/{assetBase}_ground.asset");

                AssetDatabase.CreateAsset(mesh, meshPath);
                AssetDatabase.SaveAssets();

                var proxy = GetOrCreateProxy(m_TargetRenderer, m_BakeTransform);
                var meshFilter = proxy.GetComponent<MeshFilter>() ?? proxy.AddComponent<MeshFilter>();
                var meshCollider = proxy.GetComponent<MeshCollider>() ?? proxy.AddComponent<MeshCollider>();

                meshFilter.sharedMesh = mesh;
                meshCollider.sharedMesh = mesh;
                meshCollider.convex = false;

                EditorUtility.SetDirty(proxy);
                EditorUtility.SetDirty(m_TargetRenderer.gameObject);

                DestroyPreviewMesh();
                m_PreviewHeightfield = processed;
                m_PreviewMesh = HeightfieldMeshBuilder.BuildPreviewMesh(processed);
                m_PreviewMatrix = GetPreviewMatrix();

                m_StatusMessage = $"Created ground collision at {meshPath} ({mesh.vertexCount:N0} verts, {mesh.triangles.Length / 3:N0} tris).";
                m_StatusType = MessageType.Info;
                Selection.activeGameObject = proxy;
                SceneView.RepaintAll();
            }
            catch (Exception ex)
            {
                m_StatusMessage = ex.Message;
                m_StatusType = MessageType.Error;
                Debug.LogException(ex);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        Matrix4x4 GetPreviewMatrix()
        {
            if (m_BakeTransform || m_TargetRenderer == null)
                return Matrix4x4.identity;
            return m_TargetRenderer.transform.localToWorldMatrix;
        }

        void ClearPreview()
        {
            m_PreviewHeightfield = null;
            DestroyPreviewMesh();
        }

        void DestroyPreviewMesh()
        {
            if (m_PreviewMesh != null)
            {
                DestroyImmediate(m_PreviewMesh);
                m_PreviewMesh = null;
            }
        }

        static GameObject GetOrCreateProxy(GaussianSplatRenderer renderer, bool bakedToWorld)
        {
            Transform parent = bakedToWorld ? null : renderer.transform;
            Transform existingTransform = bakedToWorld
                ? null
                : renderer.transform.Find(kProxyName);
            if (existingTransform != null)
                return existingTransform.gameObject;

            if (bakedToWorld)
            {
                var worldExisting = GameObject.Find(kProxyName);
                if (worldExisting != null)
                    return worldExisting;
            }

            var go = new GameObject(kProxyName);
            Undo.RegisterCreatedObjectUndo(go, "Create Ground Collision Proxy");
            if (parent != null)
            {
                go.transform.SetParent(parent, false);
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
            }
            return go;
        }

        void OnSceneGUI(SceneView view)
        {
            if (!m_ShowPreview || m_PreviewMesh == null)
                return;

            Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;
            var fill = new Color(0.2f, 0.9f, 0.3f, 0.15f);
            var wire = new Color(0.2f, 0.9f, 0.3f, 0.85f);

            var prevColor = Handles.color;
            Handles.color = fill;
            Handles.DrawMesh(m_PreviewMesh, m_PreviewMatrix);
            Handles.color = wire;
            Handles.DrawWireMesh(m_PreviewMesh, m_PreviewMatrix);
            Handles.color = prevColor;
        }
    }
}
