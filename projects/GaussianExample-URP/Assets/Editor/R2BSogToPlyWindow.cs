// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GaussianSplatting.Editor.Utils;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace R2B.Editor.GaussianCollision
{
    public class SogToPlyWindow : EditorWindow
    {
        const string kPrefInputPath = "com.r2b.SogToPly.InputPath";
        const string kPrefOutputPath = "com.r2b.SogToPly.OutputPath";
        const string kPrefOutputMode = "com.r2b.SogToPly.OutputMode";
        const string kPrefOverwrite = "com.r2b.SogToPly.Overwrite";
        const string kPrefFilterNan = "com.r2b.SogToPly.FilterNan";
        const string kPrefUseDecimate = "com.r2b.SogToPly.UseDecimate";
        const string kPrefDecimateAmount = "com.r2b.SogToPly.DecimateAmount";

        readonly FilePickerControl m_InputPicker = new();
        readonly FilePickerControl m_OutputPicker = new();
        readonly StringBuilder m_Log = new();

        [SerializeField] string m_InputPath;
        [SerializeField] string m_OutputPath;
        [SerializeField] SogToPlyOutputMode m_OutputMode = SogToPlyOutputMode.MergedSinglePly;
        [SerializeField] bool m_Overwrite = true;
        [SerializeField] bool m_FilterNan = true;
        [SerializeField] bool m_UseDecimate;
        [SerializeField] string m_DecimateAmount = "10%";
        [SerializeField] Vector2 m_LogScroll;

        List<string> m_DiscoveredMetaFiles = new();
        SplatTransformCliInfo m_CachedCliInfo;
        bool m_CliCacheValid;
        SplatTransformBackgroundJob m_ActiveJob;
        Queue<SplatTransformConvertSettings> m_QueuedJobs;
        bool m_IsInstallJob;
        string m_StagingDirectory;
        int m_QueueTotal;
        int m_QueueCompleted;
        string m_StatusMessage;
        MessageType m_StatusType = MessageType.None;

        [MenuItem("R2B/Gaussian Splats/Convert SOG to PLY")]
        public static void Init()
        {
            var window = GetWindow<SogToPlyWindow>(false, "SOG → PLY", true);
            window.minSize = new Vector2(380, 420);
            window.Show();
        }

        void OnEnable()
        {
            m_InputPath = EditorPrefs.GetString(kPrefInputPath, m_InputPath);
            m_OutputPath = EditorPrefs.GetString(kPrefOutputPath, m_OutputPath);
            m_OutputMode = (SogToPlyOutputMode)EditorPrefs.GetInt(kPrefOutputMode, (int)m_OutputMode);
            m_Overwrite = EditorPrefs.GetBool(kPrefOverwrite, m_Overwrite);
            m_FilterNan = EditorPrefs.GetBool(kPrefFilterNan, m_FilterNan);
            m_UseDecimate = EditorPrefs.GetBool(kPrefUseDecimate, m_UseDecimate);
            m_DecimateAmount = EditorPrefs.GetString(kPrefDecimateAmount, m_DecimateAmount);

            EditorApplication.delayCall += DelayedCliDetect;
            EditorApplication.update += OnEditorUpdate;
            RefreshDiscoveredFiles();
        }

        void OnDisable()
        {
            EditorApplication.delayCall -= DelayedCliDetect;
            EditorApplication.update -= OnEditorUpdate;
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
            DrawStepHeader(1, "CLI");
            DrawCliStep();
            DrawStepHeader(2, "Input");
            DrawInputStep();
            DrawStepHeader(3, "Output");
            DrawOutputStep();
            DrawStepHeader(4, "Options");
            DrawOptionsStep();
            DrawStepHeader(5, "Convert");
            DrawConvertStep();
        }

        static void DrawStepHeader(int step, string title)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField($"{step}. {title}", EditorStyles.boldLabel);
        }

        void DrawCliStep()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                string status = !m_CliCacheValid
                    ? "Not checked"
                    : m_CachedCliInfo.status == SplatTransformCliStatus.Ready
                        ? m_CachedCliInfo.version
                        : m_CachedCliInfo.message;
                EditorGUILayout.LabelField(status, EditorStyles.miniLabel, GUILayout.ExpandWidth(true));
                if (GUILayout.Button("Detect", GUILayout.Width(56)))
                    RefreshCliCache();
                if (GUILayout.Button("Install", GUILayout.Width(56)))
                    StartInstall();
            }

            if (m_CliCacheValid && m_CachedCliInfo.status == SplatTransformCliStatus.NodeMissing)
                EditorGUILayout.LabelField("Requires Node.js — nodejs.org", EditorStyles.miniLabel);
        }

        void DrawInputStep()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Download VR Ready export from", EditorStyles.miniLabel);
                if (GUILayout.Button("superspl.at", EditorStyles.linkLabel, GUILayout.Width(72)))
                    Application.OpenURL("https://superspl.at");
            }
            EditorGUILayout.LabelField("Pick a .sog file or unzipped SOG folder (parent of lod-meta.json).", EditorStyles.miniLabel);

            EditorGUI.BeginChangeCheck();
            var inputRect = EditorGUILayout.GetControlRect();
            m_InputPath = m_InputPicker.FolderOrFilePathFieldGUI(
                inputRect,
                new GUIContent("Input"),
                m_InputPath,
                "sog",
                "SogToPlyInput");

            if (EditorGUI.EndChangeCheck())
            {
                SavePrefs();
                RefreshDiscoveredFiles();
                if (string.IsNullOrWhiteSpace(m_OutputPath))
                    SuggestOutputPath();
            }

            if (m_DiscoveredMetaFiles.Count > 0)
            {
                if (m_DiscoveredMetaFiles.Count == 1 && SplatTransformCli.IsBundledSogFile(m_DiscoveredMetaFiles[0]))
                {
                    EditorGUILayout.LabelField("bundled SOG", EditorStyles.miniLabel);
                }
                else
                {
                    string note = SplatTransformCli.IsStreamedSogBundle(ResolveInputDirectory())
                        ? "VR Ready bundle"
                        : "SOG tiles";
                    EditorGUILayout.LabelField($"{m_DiscoveredMetaFiles.Count} tiles · {note}", EditorStyles.miniLabel);
                }
            }
            else if (!string.IsNullOrWhiteSpace(m_InputPath))
            {
                EditorGUILayout.LabelField("No SOG input found. Pick a .sog file or folder with meta.json.", EditorStyles.miniLabel);
            }
        }

        void DrawOutputStep()
        {
            EditorGUI.BeginChangeCheck();

            var prevMode = m_OutputMode;
            m_OutputMode = (SogToPlyOutputMode)EditorGUILayout.EnumPopup("Mode", m_OutputMode);
            EditorGUILayout.LabelField(
                m_OutputMode == SogToPlyOutputMode.MergedSinglePly
                    ? "One PLY for the whole scene."
                    : "One PLY per tile subfolder.",
                EditorStyles.miniLabel);

            string outputLabel = m_OutputMode == SogToPlyOutputMode.MergedSinglePly ? "PLY file" : "Folder";
            string outputExtension = m_OutputMode == SogToPlyOutputMode.MergedSinglePly ? "ply" : null;
            string outputKey = m_OutputMode == SogToPlyOutputMode.MergedSinglePly
                ? "SogToPlyOutputFile"
                : "SogToPlyOutputFolder";

            var outputRect = EditorGUILayout.GetControlRect();
            m_OutputPath = m_OutputPicker.PathFieldGUI(
                outputRect,
                new GUIContent(outputLabel),
                m_OutputPath,
                outputExtension,
                outputKey,
                saveDialog: m_OutputMode == SogToPlyOutputMode.MergedSinglePly);

            if (EditorGUI.EndChangeCheck())
            {
                SavePrefs();
                if (prevMode != m_OutputMode && ResolveInputDirectory() != null)
                    SuggestOutputPath();
            }
        }

        void DrawOptionsStep()
        {
            EditorGUI.BeginChangeCheck();

            m_Overwrite = EditorGUILayout.Toggle("Overwrite", m_Overwrite);
            EditorGUILayout.LabelField("Replace output if it already exists.", EditorStyles.miniLabel);

            m_FilterNan = EditorGUILayout.Toggle("Filter NaN", m_FilterNan);
            EditorGUILayout.LabelField("Drop invalid splat data.", EditorStyles.miniLabel);

            m_UseDecimate = EditorGUILayout.Toggle("Decimate", m_UseDecimate);
            EditorGUILayout.LabelField("Reduce splat count. Useful for VR.", EditorStyles.miniLabel);
            using (new EditorGUI.DisabledScope(!m_UseDecimate))
                m_DecimateAmount = EditorGUILayout.TextField("Amount", m_DecimateAmount);
            if (m_UseDecimate)
                EditorGUILayout.LabelField("e.g. 10% or 500000", EditorStyles.miniLabel);

            DrawOutputEstimate();

            if (EditorGUI.EndChangeCheck())
                SavePrefs();
        }

        void DrawOutputEstimate()
        {
            if (m_DiscoveredMetaFiles.Count == 0)
                return;

            var estimate = SplatTransformCli.EstimateOutput(
                m_DiscoveredMetaFiles,
                m_OutputMode,
                m_UseDecimate,
                m_DecimateAmount);

            if (!estimate.valid)
                return;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Preview", EditorStyles.miniBoldLabel);

            string sourceLine =
                $"Source: {SplatTransformCli.FormatSplatCount(estimate.sourceSplats)} splats · " +
                $"~{SplatTransformCli.FormatByteSize((long)estimate.sourceSplats * estimate.bytesPerSplat)}";
            EditorGUILayout.LabelField(sourceLine, EditorStyles.miniLabel);

            string outputLine =
                $"Output: {SplatTransformCli.FormatSplatCount(estimate.outputSplats)} splats · " +
                $"~{SplatTransformCli.FormatByteSize(estimate.estimatedBytes)}";
            if (m_UseDecimate && !string.IsNullOrEmpty(estimate.decimateLabel))
                outputLine += $" · {estimate.decimateLabel}";
            EditorGUILayout.LabelField(outputLine, EditorStyles.miniLabel);

            if (m_OutputMode == SogToPlyOutputMode.OnePlyPerTile)
            {
                int avg = estimate.outputSplats / Math.Max(1, m_DiscoveredMetaFiles.Count);
                EditorGUILayout.LabelField(
                    $"{m_DiscoveredMetaFiles.Count} PLY files · ~{SplatTransformCli.FormatSplatCount(avg)} splats each",
                    EditorStyles.miniLabel);
            }

            if (m_UseDecimate && estimate.decimateLabel == "invalid amount")
            {
                EditorGUILayout.HelpBox("Decimate amount is invalid. Use e.g. 10% or 500000.", MessageType.Warning);
            }
            else if (estimate.exceedsUnityPlyLimit || estimate.exceedsUnitySplatLimit)
            {
                EditorGUILayout.HelpBox(
                    "Estimated output exceeds Unity PLY import limit (2 GB / ~8.6M splats). " +
                    "Enable Decimate or convert fewer tiles.",
                    MessageType.Warning);
            }
            else if (!m_UseDecimate && estimate.sourceSplats > 1_000_000)
            {
                EditorGUILayout.HelpBox(
                    "Large scene — consider Decimate (e.g. 5% or 500000) before importing to Unity.",
                    MessageType.Info);
            }
        }

        void DrawConvertStep()
        {
            bool busy = IsBusy();
            using (new EditorGUI.DisabledScope(busy || !CanConvert()))
            {
                if (GUILayout.Button("Convert", GUILayout.Height(28)))
                    StartConversion();
            }

            if (busy)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string progress = m_QueueTotal > 1
                        ? $"Step {m_QueueCompleted + 1} / {m_QueueTotal}"
                        : "Running…";
                    EditorGUILayout.LabelField(progress, EditorStyles.miniLabel);
                    if (GUILayout.Button("Cancel", EditorStyles.miniButton, GUILayout.Width(56)))
                    {
                        CancelJob();
                        m_StatusMessage = "Cancelled.";
                        m_StatusType = MessageType.Warning;
                    }
                }
            }

            if (!string.IsNullOrEmpty(m_StatusMessage))
                EditorGUILayout.HelpBox(m_StatusMessage, m_StatusType);

            if (m_Log.Length > 0 && (busy || m_StatusType == MessageType.Error))
            {
                m_LogScroll = EditorGUILayout.BeginScrollView(m_LogScroll, GUILayout.Height(80));
                EditorGUILayout.TextArea(m_Log.ToString(), GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();
            }
        }

        bool CanConvert()
        {
            return m_CliCacheValid &&
                   m_CachedCliInfo.status == SplatTransformCliStatus.Ready &&
                   m_DiscoveredMetaFiles.Count > 0 &&
                   !string.IsNullOrWhiteSpace(m_OutputPath);
        }

        void SuggestOutputPath()
        {
            if (SplatTransformCli.IsBundledSogFile(m_InputPath))
            {
                string fullPath = Path.GetFullPath(m_InputPath);
                string sogName = Path.GetFileNameWithoutExtension(fullPath);
                string dir = Path.GetDirectoryName(fullPath);
                m_OutputPath = m_OutputMode == SogToPlyOutputMode.MergedSinglePly
                    ? Path.ChangeExtension(fullPath, ".ply")
                    : Path.Combine(dir, $"{sogName}_ply_tiles");
                SavePrefs();
                return;
            }

            string inputDir = ResolveInputDirectory();
            if (string.IsNullOrWhiteSpace(inputDir))
                return;

            string baseName = Path.GetFileName(inputDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "sog_export";

            m_OutputPath = m_OutputMode == SogToPlyOutputMode.MergedSinglePly
                ? Path.Combine(inputDir, $"{baseName}.ply")
                : Path.Combine(inputDir, $"{baseName}_ply_tiles");

            SavePrefs();
        }

        void RefreshDiscoveredFiles()
        {
            m_DiscoveredMetaFiles = SplatTransformCli.DiscoverSogMetaFiles(m_InputPath);
            Repaint();
        }

        void RefreshCliCache()
        {
            m_CachedCliInfo = SplatTransformCli.Detect();
            m_CliCacheValid = true;
        }

        void StartInstall()
        {
            if (IsBusy()) return;
            m_Log.Clear();
            if (!SplatTransformCli.TryStartInstallGlobal(out m_ActiveJob, out string err))
            {
                m_StatusMessage = err;
                m_StatusType = MessageType.Error;
                return;
            }

            m_QueuedJobs = null;
            m_IsInstallJob = true;
            m_StatusMessage = "Installing…";
            m_StatusType = MessageType.Info;
        }

        void StartConversion()
        {
            if (IsBusy()) return;

            m_Log.Clear();
            m_QueueCompleted = 0;
            m_QueueTotal = 0;
            m_QueuedJobs = null;
            m_StagingDirectory = null;
            m_IsInstallJob = false;
            m_StatusMessage = null;
            m_StatusType = MessageType.None;

            try
            {
                if (!m_CliCacheValid || m_CachedCliInfo.status != SplatTransformCliStatus.Ready)
                {
                    RefreshCliCache();
                    if (m_CachedCliInfo.status != SplatTransformCliStatus.Ready)
                        throw new InvalidOperationException(m_CachedCliInfo.message);
                }

                RefreshDiscoveredFiles();
                if (m_DiscoveredMetaFiles.Count == 0)
                    throw new InvalidOperationException("No SOG input found. Pick a .sog file or folder with meta.json.");

                if (string.IsNullOrWhiteSpace(m_OutputPath))
                    throw new InvalidOperationException("Set an output path.");

                var jobs = BuildJobQueue(out string stagingDirectory, out bool usesStagedMerge);
                if (jobs.Count == 0)
                    throw new InvalidOperationException("Nothing to convert.");

                m_StagingDirectory = stagingDirectory;
                if (usesStagedMerge)
                    m_Log.AppendLine($"Staged merge: {m_DiscoveredMetaFiles.Count} tiles, {jobs.Count} steps.");

                m_QueuedJobs = new Queue<SplatTransformConvertSettings>(jobs);
                m_QueueTotal = jobs.Count;
                if (!StartNextQueuedJob())
                    throw new InvalidOperationException("Failed to start conversion.");
            }
            catch (Exception ex)
            {
                CleanupStagingDirectory();
                CancelJob();
                m_StatusMessage = ex.Message;
                m_StatusType = MessageType.Error;
                Debug.LogException(ex);
            }

            Repaint();
        }

        List<SplatTransformConvertSettings> BuildJobQueue(out string stagingDirectory, out bool usesStagedMerge)
        {
            stagingDirectory = null;
            usesStagedMerge = false;

            if (m_OutputMode == SogToPlyOutputMode.MergedSinglePly)
            {
                var plan = SplatTransformCli.BuildMergedJobQueue(
                    m_CachedCliInfo,
                    m_DiscoveredMetaFiles,
                    m_OutputPath,
                    m_Overwrite,
                    m_FilterNan,
                    m_UseDecimate,
                    m_DecimateAmount);
                stagingDirectory = plan.stagingDirectory;
                usesStagedMerge = plan.usesStagedMerge;
                return plan.jobs;
            }

            var jobs = new List<SplatTransformConvertSettings>();
            string outputDir = GetOutputDirectory();
            Directory.CreateDirectory(outputDir);

            foreach (string meta in m_DiscoveredMetaFiles)
            {
                string tileName = Path.GetFileName(Path.GetDirectoryName(meta));
                if (string.IsNullOrWhiteSpace(tileName))
                    tileName = Path.GetFileNameWithoutExtension(meta);
                string outputFile = Path.Combine(outputDir, $"{tileName}.ply");
                jobs.Add(CreateSettings(new[] { meta }, outputFile));
            }

            return jobs;
        }

        SplatTransformConvertSettings CreateSettings(IReadOnlyList<string> inputs, string outputFile)
        {
            return new SplatTransformConvertSettings
            {
                inputFiles = inputs,
                outputFile = Path.GetFullPath(outputFile),
                overwrite = m_Overwrite,
                filterNan = m_FilterNan,
                useDecimate = m_UseDecimate,
                decimateAmount = m_DecimateAmount
            };
        }

        bool StartNextQueuedJob()
        {
            if (m_QueuedJobs == null || m_QueuedJobs.Count == 0)
                return false;

            var settings = m_QueuedJobs.Dequeue();
            if (!SplatTransformCli.TryStartConvertPipeline(m_CachedCliInfo, settings, out m_ActiveJob, out string err))
            {
                m_StatusMessage = err;
                m_StatusType = MessageType.Error;
                return false;
            }

            m_StatusMessage = null;
            return true;
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

            bool wasInstall = m_IsInstallJob;
            m_ActiveJob.Dispose();
            m_ActiveJob = null;
            m_IsInstallJob = false;

            if (wasInstall)
            {
                if (code != 0)
                {
                    m_StatusMessage = string.IsNullOrWhiteSpace(output) ? $"Install failed ({code})" : output;
                    m_StatusType = MessageType.Error;
                }
                else
                {
                    RefreshCliCache();
                    m_StatusMessage = "CLI ready.";
                    m_StatusType = MessageType.Info;
                }

                Repaint();
                return;
            }

            if (code != 0)
            {
                CleanupStagingDirectory();
                CancelJob();
                m_StatusMessage = string.IsNullOrWhiteSpace(output) ? $"Exit code {code}" : output;
                m_StatusType = MessageType.Error;
                Repaint();
                return;
            }

            m_QueueCompleted++;
            if (m_QueuedJobs != null && m_QueuedJobs.Count > 0)
            {
                if (!StartNextQueuedJob())
                {
                    CleanupStagingDirectory();
                    CancelJob();
                }
                Repaint();
                return;
            }

            CleanupStagingDirectory();
            m_QueuedJobs = null;

            if (m_OutputMode == SogToPlyOutputMode.MergedSinglePly &&
                SplatTransformCli.TryReadPlyVertexCount(m_OutputPath, out int actualSplats, out long actualBytes))
            {
                var estimate = SplatTransformCli.EstimateOutput(
                    m_DiscoveredMetaFiles, m_OutputMode, m_UseDecimate, m_DecimateAmount);

                if (actualBytes > SplatTransformCli.kUnityPlyMaxBytes)
                {
                    m_StatusMessage =
                        $"PLY is {SplatTransformCli.FormatByteSize(actualBytes)} ({actualSplats:N0} splats) — over Unity 2GB limit. " +
                        "Delete the file and re-convert with Decimate enabled.";
                    m_StatusType = MessageType.Error;
                }
                else if (m_UseDecimate && estimate.valid &&
                         actualSplats > estimate.outputSplats * 2)
                {
                    m_StatusMessage =
                        $"Done but splat count is {actualSplats:N0} (expected ~{estimate.outputSplats:N0}). " +
                        $"Size: {SplatTransformCli.FormatByteSize(actualBytes)}.";
                    m_StatusType = MessageType.Warning;
                }
                else
                {
                    m_StatusMessage =
                        $"Done → {SplatTransformCli.FormatSplatCount(actualSplats)} splats · " +
                        $"{SplatTransformCli.FormatByteSize(actualBytes)}";
                    m_StatusType = MessageType.Info;
                }
            }
            else
            {
                m_StatusMessage = $"Done → {m_OutputPath}";
                m_StatusType = MessageType.Info;
            }
            Repaint();
        }

        void CancelJob()
        {
            m_ActiveJob?.Cancel();
            m_ActiveJob?.Dispose();
            m_ActiveJob = null;
            m_QueuedJobs = null;
            m_IsInstallJob = false;
            m_QueueTotal = 0;
            m_QueueCompleted = 0;
            CleanupStagingDirectory();
        }

        void CleanupStagingDirectory()
        {
            if (string.IsNullOrWhiteSpace(m_StagingDirectory))
                return;

            SplatTransformCli.CleanupStagingDirectory(m_StagingDirectory);
            m_StagingDirectory = null;
        }

        bool IsBusy() => m_ActiveJob != null && m_ActiveJob.IsRunning;

        string ResolveInputDirectory()
        {
            if (string.IsNullOrWhiteSpace(m_InputPath))
                return null;

            if (Directory.Exists(m_InputPath))
                return Path.GetFullPath(m_InputPath);

            if (File.Exists(m_InputPath))
                return Path.GetDirectoryName(Path.GetFullPath(m_InputPath));

            return null;
        }

        string GetOutputDirectory()
        {
            if (string.IsNullOrWhiteSpace(m_OutputPath))
                return null;

            if (m_OutputMode == SogToPlyOutputMode.OnePlyPerTile)
                return Path.GetFullPath(m_OutputPath);

            return Path.GetDirectoryName(Path.GetFullPath(m_OutputPath));
        }

        void SavePrefs()
        {
            EditorPrefs.SetString(kPrefInputPath, m_InputPath ?? string.Empty);
            EditorPrefs.SetString(kPrefOutputPath, m_OutputPath ?? string.Empty);
            EditorPrefs.SetInt(kPrefOutputMode, (int)m_OutputMode);
            EditorPrefs.SetBool(kPrefOverwrite, m_Overwrite);
            EditorPrefs.SetBool(kPrefFilterNan, m_FilterNan);
            EditorPrefs.SetBool(kPrefUseDecimate, m_UseDecimate);
            EditorPrefs.SetString(kPrefDecimateAmount, m_DecimateAmount ?? string.Empty);
        }
    }
}
