// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace R2B.Editor.GaussianCollision
{
    public enum SplatTransformCliStatus
    {
        Ready,
        NotFound,
        NodeMissing,
        NpmMissing
    }

    public enum CollisionFillMode
    {
        None,
        FloorFill,
        ExternalFill
    }

    public struct SplatTransformCliInfo
    {
        public SplatTransformCliStatus status;
        public string executable;
        public string argumentsPrefix;
        public string version;
        public string message;
    }

    public enum SogToPlyOutputMode
    {
        MergedSinglePly,
        OnePlyPerTile
    }

    public struct SplatTransformConvertSettings
    {
        public IReadOnlyList<string> inputFiles;
        public string outputFile;
        public bool overwrite;
        public bool filterNan;
        public bool useDecimate;
        public string decimateAmount;
    }

    public struct SogOutputEstimate
    {
        public bool valid;
        public int sourceSplats;
        public int outputSplats;
        public long estimatedBytes;
        public int bytesPerSplat;
        public bool decimateApplied;
        public string decimateLabel;
        public bool exceedsUnityPlyLimit;
        public bool exceedsUnitySplatLimit;
    }

    public struct SplatTransformCollisionSettings
    {
        public string inputFile;
        public string outputVoxelJson;
        public bool useFilterBox;
        public Vector3 filterBoxMin;
        public Vector3 filterBoxMax;
        public bool useFilterCluster;
        public float clusterResolution;
        public float clusterOpacity;
        public float clusterMinContribution;
        public Vector3 seedPosition;
        public float voxelSize;
        public float voxelOpacity;
        public CollisionFillMode fillMode;
        public float fillSize;
        public bool useCarve;
        public float carveHeight;
        public float carveRadius;
        public bool generateCollisionMesh;
        public bool collisionFaces;
    }

    public static class SplatTransformCli
    {
        public const string kPrefCliPath = "com.r2b.SplatTransform.CliPath";
        public const string kPrefUseNpx = "com.r2b.SplatTransform.UseNpx";

        // Windows CreateProcess limit is 8191 chars; npx + quoting adds overhead.
        public const int kMaxInputsPerCommand = 8;
        public const int kMaxCommandLineLength = 4000;
        public const long kUnityPlyMaxBytes = 2L * 1024 * 1024 * 1024;
        public const int kUnityMaxSplats = 8_600_000;

        static readonly Regex s_RootCountRegex = new(
            "\"count\"\\s*:\\s*(\\d+)",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
        static readonly Regex s_ShBandsRegex = new(
            "\"bands\"\\s*:\\s*(\\d+)",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static SplatTransformCliInfo Detect()
        {
            string customPath = EditorPrefs.GetString(kPrefCliPath, string.Empty);
            if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
            {
                var custom = ProbeExecutable(customPath, string.Empty);
                if (custom.status == SplatTransformCliStatus.Ready)
                    return custom;
            }

            foreach (var candidate in GetDefaultCandidates())
            {
                var info = ProbeExecutable(candidate.executable, candidate.argumentsPrefix);
                if (info.status == SplatTransformCliStatus.Ready)
                    return info;
            }

            if (!TryFindOnPath("node.exe", out _))
            {
                return new SplatTransformCliInfo
                {
                    status = SplatTransformCliStatus.NodeMissing,
                    message = "Node.js not found. Install from https://nodejs.org/"
                };
            }

            if (!TryFindOnPath("npm.cmd", out _) && !TryFindOnPath("npm", out _))
            {
                return new SplatTransformCliInfo
                {
                    status = SplatTransformCliStatus.NpmMissing,
                    message = "npm not found. Reinstall Node.js."
                };
            }

            if (EditorPrefs.GetBool(kPrefUseNpx, true))
            {
                var npx = ProbeExecutable("npx", "--yes @playcanvas/splat-transform");
                if (npx.status == SplatTransformCliStatus.Ready)
                    return npx;
            }

            return new SplatTransformCliInfo
            {
                status = SplatTransformCliStatus.NotFound,
                message = "splat-transform not found. Install CLI or browse to executable."
            };
        }

        public static bool TryStartConvertPipeline(
            SplatTransformCliInfo cli,
            SplatTransformConvertSettings settings,
            out SplatTransformBackgroundJob job,
            out string error)
        {
            job = null;
            error = null;

            if (cli.status != SplatTransformCliStatus.Ready)
            {
                error = cli.message;
                return false;
            }

            if (settings.inputFiles == null || settings.inputFiles.Count == 0)
            {
                error = "No SOG input files selected.";
                return false;
            }

            foreach (string input in settings.inputFiles)
            {
                if (string.IsNullOrWhiteSpace(input) || !File.Exists(input))
                {
                    error = $"Input file not found: {input}";
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(settings.outputFile))
            {
                error = "Output path is empty.";
                return false;
            }

            string outputDir = Path.GetDirectoryName(settings.outputFile);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);

            string args = BuildConvertArguments(settings);
            string workingDir = Path.GetDirectoryName(settings.inputFiles[0]);
            return SplatTransformBackgroundJob.TryStart(
                cli.executable,
                $"{cli.argumentsPrefix}{args}".Trim(),
                workingDir,
                out job,
                out error);
        }

        public static List<string> DiscoverSogMetaFiles(string path)
        {
            var results = new List<string>();
            if (string.IsNullOrWhiteSpace(path))
                return results;

            path = Path.GetFullPath(path);
            if (File.Exists(path))
            {
                if (IsSogMetaJson(path))
                    results.Add(path);
                return results;
            }

            if (!Directory.Exists(path))
                return results;

            string rootMeta = Path.Combine(path, "meta.json");
            if (File.Exists(rootMeta))
                results.Add(rootMeta);

            foreach (string dir in Directory.GetDirectories(path))
            {
                string tileMeta = Path.Combine(dir, "meta.json");
                if (File.Exists(tileMeta))
                    results.Add(tileMeta);
            }

            results.Sort(StringComparer.OrdinalIgnoreCase);
            return results;
        }

        public static bool IsSogMetaJson(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   path.EndsWith("meta.json", StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(path);
        }

        public static bool IsStreamedSogBundle(string folder)
        {
            return !string.IsNullOrWhiteSpace(folder) &&
                   Directory.Exists(folder) &&
                   File.Exists(Path.Combine(folder, "lod-meta.json"));
        }

        public static SogOutputEstimate EstimateOutput(
            IReadOnlyList<string> metaFiles,
            SogToPlyOutputMode outputMode,
            bool useDecimate,
            string decimateAmount)
        {
            var estimate = new SogOutputEstimate();
            if (metaFiles == null || metaFiles.Count == 0)
                return estimate;

            int totalSplats = 0;
            int bytesPerSplat = 0;
            foreach (string meta in metaFiles)
            {
                if (!TryReadSogTileCount(meta, out int count))
                    return estimate;
                totalSplats += count;
                if (bytesPerSplat == 0)
                    bytesPerSplat = EstimateBytesPerSplat(meta);
            }

            if (totalSplats <= 0 || bytesPerSplat <= 0)
                return estimate;

            estimate.valid = true;
            estimate.sourceSplats = totalSplats;
            estimate.bytesPerSplat = bytesPerSplat;

            int outputSplats = totalSplats;
            estimate.decimateApplied = useDecimate;
            if (useDecimate)
            {
                if (TryParseDecimateAmount(decimateAmount, totalSplats, out _, out string label))
                    estimate.decimateLabel = label;
                else
                    estimate.decimateLabel = "invalid amount";
            }

            long totalBytes = 0;
            if (outputMode == SogToPlyOutputMode.OnePlyPerTile)
            {
                outputSplats = 0;
                foreach (string meta in metaFiles)
                {
                    if (!TryReadSogTileCount(meta, out int count))
                        continue;

                    int tileOut = useDecimate && estimate.decimateLabel != "invalid amount"
                        ? ApplyDecimateCount(count, decimateAmount)
                        : count;
                    outputSplats += tileOut;
                    totalBytes += (long)tileOut * bytesPerSplat + 1024;
                }
            }
            else
            {
                outputSplats = useDecimate && estimate.decimateLabel != "invalid amount"
                    ? ApplyDecimateCount(totalSplats, decimateAmount)
                    : totalSplats;
                totalBytes = (long)outputSplats * bytesPerSplat + 1024;
            }

            estimate.outputSplats = Math.Max(0, outputSplats);
            estimate.estimatedBytes = totalBytes;

            estimate.exceedsUnityPlyLimit = estimate.estimatedBytes > kUnityPlyMaxBytes;
            estimate.exceedsUnitySplatLimit = estimate.outputSplats > kUnityMaxSplats;
            return estimate;
        }

        public static bool TryParseDecimateAmount(
            string decimateAmount,
            int sourceSplats,
            out int outputSplats,
            out string label)
        {
            outputSplats = sourceSplats;
            label = null;
            if (string.IsNullOrWhiteSpace(decimateAmount))
                return false;

            string trimmed = decimateAmount.Trim();
            if (trimmed.EndsWith("%", StringComparison.Ordinal))
            {
                string number = trimmed[..^1].Trim();
                if (!double.TryParse(number, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double percent))
                    return false;

                outputSplats = ApplyDecimateCount(sourceSplats, decimateAmount);
                label = $"{percent:G}% → ~{FormatSplatCount(outputSplats)} splats";
                return true;
            }

            if (!int.TryParse(trimmed, out int target) || target <= 0)
                return false;

            outputSplats = Math.Min(target, sourceSplats);
            label = $"{target:N0} max → ~{FormatSplatCount(outputSplats)} splats";
            return true;
        }

        public static int ApplyDecimateCount(int sourceSplats, string decimateAmount)
        {
            if (sourceSplats <= 0 || string.IsNullOrWhiteSpace(decimateAmount))
                return sourceSplats;

            string trimmed = decimateAmount.Trim();
            if (trimmed.EndsWith("%", StringComparison.Ordinal))
            {
                string number = trimmed[..^1].Trim();
                if (!double.TryParse(number, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double percent))
                    return sourceSplats;

                return Math.Max(1, (int)Math.Round(sourceSplats * percent / 100.0));
            }

            if (int.TryParse(trimmed, out int target) && target > 0)
                return Math.Min(target, sourceSplats);

            return sourceSplats;
        }

        public static string FormatSplatCount(int count) => $"{count:N0}";

        public static string FormatByteSize(long bytes) => EditorUtility.FormatBytes(bytes);

        public static bool TryReadPlyVertexCount(string plyPath, out int vertexCount, out long fileSize)
        {
            vertexCount = 0;
            fileSize = 0;
            if (string.IsNullOrWhiteSpace(plyPath) || !File.Exists(plyPath))
                return false;

            var info = new FileInfo(plyPath);
            fileSize = info.Length;

            try
            {
                using var reader = new StreamReader(plyPath, Encoding.ASCII, false);
                for (int i = 0; i < 64 && !reader.EndOfStream; i++)
                {
                    string line = reader.ReadLine();
                    if (line == null)
                        break;
                    if (!line.StartsWith("element vertex ", StringComparison.Ordinal))
                        continue;

                    return int.TryParse(line.Substring(15).Trim(), out vertexCount) && vertexCount > 0;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        static bool TryReadSogTileCount(string metaPath, out int count)
        {
            count = 0;
            if (string.IsNullOrWhiteSpace(metaPath) || !File.Exists(metaPath))
                return false;

            try
            {
                string head = ReadMetaHead(metaPath, 512);
                Match match = s_RootCountRegex.Match(head);
                if (!match.Success)
                    return false;

                return int.TryParse(match.Groups[1].Value, out count) && count > 0;
            }
            catch
            {
                return false;
            }
        }

        static int EstimateBytesPerSplat(string metaPath)
        {
            int bands = 1;
            try
            {
                string head = ReadMetaHead(metaPath, 4096);
                Match match = s_ShBandsRegex.Match(head);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int parsed))
                    bands = parsed;
            }
            catch
            {
                // use default
            }

            return bands switch
            {
                <= 0 => 68,
                1 => 92,
                2 => 140,
                _ => 248
            };
        }

        static string ReadMetaHead(string metaPath, int maxChars)
        {
            using var reader = new StreamReader(metaPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            char[] buffer = new char[maxChars];
            int read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read);
        }

        public struct MergePipelinePlan
        {
            public List<SplatTransformConvertSettings> jobs;
            public string stagingDirectory;
            public bool usesStagedMerge;
        }

        public static MergePipelinePlan BuildMergedJobQueue(
            SplatTransformCliInfo cli,
            IReadOnlyList<string> metaFiles,
            string finalOutputPath,
            bool overwrite,
            bool filterNan,
            bool useDecimate,
            string decimateAmount)
        {
            finalOutputPath = Path.GetFullPath(finalOutputPath);
            var plan = new MergePipelinePlan
            {
                jobs = new List<SplatTransformConvertSettings>(),
                stagingDirectory = null,
                usesStagedMerge = false
            };

            if (metaFiles == null || metaFiles.Count == 0)
                return plan;

            if (metaFiles.Count == 1)
            {
                plan.jobs.Add(CreateConvertSettings(
                    metaFiles, finalOutputPath, overwrite, filterNan, useDecimate, decimateAmount));
                return plan;
            }

            plan.usesStagedMerge = true;
            plan.stagingDirectory = CreateStagingDirectory();
            Directory.CreateDirectory(plan.stagingDirectory);

            var currentFiles = new List<string>(metaFiles.Count);
            for (int i = 0; i < metaFiles.Count; i++)
            {
                string meta = metaFiles[i];
                string tilePly = Path.Combine(plan.stagingDirectory, $"t{i:D3}.ply");
                plan.jobs.Add(CreateConvertSettings(
                    new[] { meta }, tilePly, overwrite: true, filterNan, useDecimate: false, decimateAmount));
                currentFiles.Add(tilePly);
            }

            string mergedStagingPath = Path.Combine(plan.stagingDirectory, "merged.ply");
            int round = 0;
            int mergeBatchSize = GetSafeMergeBatchSize(
                cli, currentFiles, mergedStagingPath, overwrite, useDecimate: false, decimateAmount: null);

            // Merge tiles in batches. Never decimate here — splat-transform ignores -F on multi-input merges.
            while (currentFiles.Count > 1)
            {
                var nextFiles = new List<string>();
                int batchIndex = 0;
                foreach (var batch in Chunk(currentFiles, mergeBatchSize))
                {
                    if (batch.Count == 1)
                    {
                        nextFiles.Add(batch[0]);
                        continue;
                    }

                    string batchOutput = Path.Combine(plan.stagingDirectory, $"m{round}_{batchIndex:D2}.ply");
                    plan.jobs.Add(CreateConvertSettings(
                        batch,
                        batchOutput,
                        overwrite: true,
                        filterNan: false,
                        useDecimate: false,
                        decimateAmount: null));

                    nextFiles.Add(batchOutput);
                    batchIndex++;
                }

                currentFiles = nextFiles;
                round++;

                if (currentFiles.Count > 1)
                {
                    mergeBatchSize = GetSafeMergeBatchSize(
                        cli, currentFiles, mergedStagingPath, overwrite, useDecimate: false, decimateAmount: null);
                }
            }

            if (currentFiles.Count != 1)
                return plan;

            string mergedSource = currentFiles[0];
            if (useDecimate)
            {
                // Decimate must be a separate single-input pass (verified against splat-transform v2.7.1).
                plan.jobs.Add(CreateConvertSettings(
                    new[] { mergedSource },
                    finalOutputPath,
                    overwrite,
                    filterNan: false,
                    useDecimate: true,
                    decimateAmount));
            }
            else if (!PathsEqual(mergedSource, finalOutputPath))
            {
                plan.jobs.Add(CreateConvertSettings(
                    new[] { mergedSource },
                    finalOutputPath,
                    overwrite,
                    filterNan: false,
                    useDecimate: false,
                    decimateAmount: null));
            }

            return plan;
        }

        static string CreateStagingDirectory()
        {
            // Short path under TEMP keeps merge command lines under the Windows limit.
            string root = Path.GetTempPath();
            string dir = Path.Combine(root, "sg" + Guid.NewGuid().ToString("N")[..8]);
            return Path.GetFullPath(dir);
        }

        static int GetSafeMergeBatchSize(
            SplatTransformCliInfo cli,
            IReadOnlyList<string> sampleInputs,
            string sampleOutput,
            bool overwrite,
            bool useDecimate,
            string decimateAmount)
        {
            if (sampleInputs == null || sampleInputs.Count == 0)
                return 1;

            int maxBatch = Math.Min(kMaxInputsPerCommand, sampleInputs.Count);
            for (int batch = maxBatch; batch >= 2; batch--)
            {
                var testInputs = new string[batch];
                for (int i = 0; i < batch; i++)
                    testInputs[i] = sampleInputs[i];

                var test = CreateConvertSettings(
                    testInputs,
                    sampleOutput,
                    overwrite: true,
                    filterNan: false,
                    useDecimate,
                    decimateAmount);

                if (EstimateCommandLineLength(cli, test) <= kMaxCommandLineLength)
                    return batch;
            }

            return 2;
        }

        public static void CleanupStagingDirectory(string stagingDirectory)
        {
            if (string.IsNullOrWhiteSpace(stagingDirectory) || !Directory.Exists(stagingDirectory))
                return;

            try
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SplatTransformCli] Failed to delete staging folder:\n{stagingDirectory}\n{ex.Message}");
            }
        }

        public static int EstimateCommandLineLength(SplatTransformCliInfo cli, SplatTransformConvertSettings settings)
        {
            string args = BuildConvertArguments(settings);
            return $"{cli.executable} {cli.argumentsPrefix}{args}".Trim().Length;
        }

        public static bool NeedsStagedMerge(SplatTransformConvertSettings settings)
        {
            return settings.inputFiles != null && settings.inputFiles.Count > 1;
        }

        static SplatTransformConvertSettings CreateConvertSettings(
            IReadOnlyList<string> inputs,
            string outputFile,
            bool overwrite,
            bool filterNan,
            bool useDecimate,
            string decimateAmount)
        {
            return new SplatTransformConvertSettings
            {
                inputFiles = inputs,
                outputFile = Path.GetFullPath(outputFile),
                overwrite = overwrite,
                filterNan = filterNan,
                useDecimate = useDecimate,
                decimateAmount = decimateAmount
            };
        }

        static bool PathsEqual(string a, string b)
        {
            return string.Equals(
                Path.GetFullPath(a),
                Path.GetFullPath(b),
                StringComparison.OrdinalIgnoreCase);
        }

        static IEnumerable<IReadOnlyList<string>> Chunk(IReadOnlyList<string> items, int chunkSize)
        {
            if (chunkSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(chunkSize));

            for (int i = 0; i < items.Count; i += chunkSize)
            {
                int count = Math.Min(chunkSize, items.Count - i);
                var chunk = new string[count];
                for (int j = 0; j < count; j++)
                    chunk[j] = items[i + j];
                yield return chunk;
            }
        }

        public static bool TryStartCollisionPipeline(
            SplatTransformCliInfo cli,
            SplatTransformCollisionSettings settings,
            out SplatTransformBackgroundJob job,
            out string error)
        {
            job = null;
            error = null;

            if (cli.status != SplatTransformCliStatus.Ready)
            {
                error = cli.message;
                return false;
            }

            if (string.IsNullOrWhiteSpace(settings.inputFile) || !File.Exists(settings.inputFile))
            {
                error = $"Input file not found: {settings.inputFile}";
                return false;
            }

            string outputDir = Path.GetDirectoryName(settings.outputVoxelJson);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);

            string args = BuildCollisionArguments(settings);
            return SplatTransformBackgroundJob.TryStart(
                cli.executable,
                $"{cli.argumentsPrefix}{args}".Trim(),
                Path.GetDirectoryName(settings.inputFile),
                out job,
                out error);
        }

        public static bool TryStartInstallGlobal(out SplatTransformBackgroundJob job, out string error)
        {
            job = null;
            error = null;

            if (!TryFindOnPath("npm.cmd", out string npm) && !TryFindOnPath("npm", out npm))
            {
                error = TryFindOnPath("node.exe", out _)
                    ? "npm not found on PATH."
                    : "Node.js is not installed.";
                return false;
            }

            return SplatTransformBackgroundJob.TryStart(
                npm,
                "install -g @playcanvas/splat-transform",
                null,
                out job,
                out error);
        }

        public static string GetExpectedCollisionGlbPath(string voxelJsonPath)
        {
            string dir = Path.GetDirectoryName(voxelJsonPath) ?? string.Empty;
            string baseName = Path.GetFileNameWithoutExtension(voxelJsonPath);
            if (baseName.EndsWith(".voxel", StringComparison.OrdinalIgnoreCase))
                baseName = baseName[..^6];
            return Path.Combine(dir, baseName + ".collision.glb");
        }

        static string BuildConvertArguments(SplatTransformConvertSettings settings)
        {
            var sb = new StringBuilder();

            if (settings.overwrite)
                sb.Append("-w ");

            foreach (string input in settings.inputFiles)
                sb.Append('"').Append(input.Replace("\"", "\\\"")).Append("\" ");

            if (settings.filterNan)
                sb.Append("-N ");

            if (settings.useDecimate && !string.IsNullOrWhiteSpace(settings.decimateAmount))
            {
                sb.Append("-F ");
                sb.Append(settings.decimateAmount.Trim());
                sb.Append(' ');
            }

            sb.Append('"').Append(settings.outputFile.Replace("\"", "\\\"")).Append('"');
            return sb.ToString();
        }

        // Order matches PlayCanvas collision guide:
        // input -> filter-box -> filter-cluster -> seed-pos -> output.voxel.json -> voxel-params -> fill -> carve -> -K
        static string BuildCollisionArguments(SplatTransformCollisionSettings settings)
        {
            var sb = new StringBuilder();
            var culture = System.Globalization.CultureInfo.InvariantCulture;

            sb.Append("-w ");
            sb.Append('"').Append(settings.inputFile.Replace("\"", "\\\"")).Append('"');

            if (settings.useFilterBox)
            {
                sb.Append(" --filter-box ");
                sb.Append(FormatFilterBox(settings.filterBoxMin, settings.filterBoxMax));
            }

            if (settings.useFilterCluster)
            {
                sb.Append(" --filter-cluster ");
                sb.Append(settings.clusterResolution.ToString("0.###", culture));
                sb.Append(',');
                sb.Append(settings.clusterOpacity.ToString("0.###", culture));
                sb.Append(',');
                sb.Append(settings.clusterMinContribution.ToString("0.###", culture));
            }

            AppendSeedPosition(sb, settings.seedPosition);

            sb.Append(' ');
            sb.Append('"').Append(settings.outputVoxelJson.Replace("\"", "\\\"")).Append('"');

            sb.Append(" --voxel-params ");
            sb.Append(settings.voxelSize.ToString("0.###", culture));
            sb.Append(',');
            sb.Append(settings.voxelOpacity.ToString("0.###", culture));

            switch (settings.fillMode)
            {
                case CollisionFillMode.FloorFill:
                    sb.Append(" --voxel-floor-fill ");
                    sb.Append(settings.fillSize.ToString("0.###", culture));
                    break;
                case CollisionFillMode.ExternalFill:
                    sb.Append(" --voxel-external-fill ");
                    sb.Append(settings.fillSize.ToString("0.###", culture));
                    break;
            }

            if (settings.useCarve)
            {
                sb.Append(" --voxel-carve ");
                sb.Append(settings.carveHeight.ToString("0.###", culture));
                sb.Append(',');
                sb.Append(settings.carveRadius.ToString("0.###", culture));
            }

            if (settings.generateCollisionMesh)
                sb.Append(settings.collisionFaces ? " -K faces" : " -K smooth");

            return sb.ToString();
        }

        static void AppendSeedPosition(StringBuilder sb, Vector3 seedPosition)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            sb.Append(" --seed-pos ");
            sb.Append(seedPosition.x.ToString("0.###", culture));
            sb.Append(',');
            sb.Append(seedPosition.y.ToString("0.###", culture));
            sb.Append(',');
            sb.Append(seedPosition.z.ToString("0.###", culture));
        }

        static string FormatFilterBox(Vector3 min, Vector3 max)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            return string.Join(",",
                min.x.ToString("0.###", culture),
                min.y.ToString("0.###", culture),
                min.z.ToString("0.###", culture),
                max.x.ToString("0.###", culture),
                max.y.ToString("0.###", culture),
                max.z.ToString("0.###", culture));
        }

        static IEnumerable<(string executable, string argumentsPrefix)> GetDefaultCandidates()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                string npmCmd = Path.Combine(appData, "npm", "splat-transform.cmd");
                if (File.Exists(npmCmd))
                    yield return (npmCmd, string.Empty);
            }

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles))
            {
                string nodeCmd = Path.Combine(programFiles, "nodejs", "splat-transform.cmd");
                if (File.Exists(nodeCmd))
                    yield return (nodeCmd, string.Empty);

                string npxCmd = Path.Combine(programFiles, "nodejs", "npx.cmd");
                if (File.Exists(npxCmd) && EditorPrefs.GetBool(kPrefUseNpx, true))
                    yield return (npxCmd, "--yes @playcanvas/splat-transform ");
            }

            foreach (var name in new[] { "splat-transform.cmd", "splat-transform", "npx.cmd", "npx" })
            {
                if (!TryFindOnPath(name, out string path))
                    continue;

                if (name.StartsWith("npx", StringComparison.OrdinalIgnoreCase) &&
                    !EditorPrefs.GetBool(kPrefUseNpx, true))
                    continue;

                string prefix = name.StartsWith("npx", StringComparison.OrdinalIgnoreCase)
                    ? "--yes @playcanvas/splat-transform "
                    : string.Empty;
                yield return (path, prefix);
            }
        }

        static SplatTransformCliInfo ProbeExecutable(string executable, string argumentsPrefix)
        {
            if (RunProcess(executable, $"{argumentsPrefix}--version".Trim(), null, null, out string error) != 0)
            {
                return new SplatTransformCliInfo
                {
                    status = SplatTransformCliStatus.NotFound,
                    executable = executable,
                    argumentsPrefix = argumentsPrefix,
                    message = string.IsNullOrWhiteSpace(error) ? "splat-transform --version failed." : error
                };
            }

            return new SplatTransformCliInfo
            {
                status = SplatTransformCliStatus.Ready,
                executable = executable,
                argumentsPrefix = argumentsPrefix,
                version = error?.Trim(),
                message = "Ready"
            };
        }

        static int RunProcess(string executable, string arguments, string workingDirectory, Action<string> onLog, out string combinedError)
        {
            combinedError = string.Empty;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                if (!string.IsNullOrEmpty(workingDirectory))
                    psi.WorkingDirectory = workingDirectory;

                using var process = new Process { StartInfo = psi };
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();

                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    stdout.AppendLine(e.Data);
                    onLog?.Invoke(e.Data);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    stderr.AppendLine(e.Data);
                    onLog?.Invoke(e.Data);
                };

                if (!process.Start())
                {
                    combinedError = $"Failed to start: {executable}";
                    return -1;
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();

                string output = stdout.ToString().Trim();
                string err = stderr.ToString().Trim();
                combinedError = string.IsNullOrWhiteSpace(output) ? err : output;
                if (!string.IsNullOrWhiteSpace(err) && !string.IsNullOrWhiteSpace(output))
                    combinedError = output + Environment.NewLine + err;

                if (process.ExitCode != 0)
                    Debug.LogError($"[SplatTransformCli] {executable} {arguments}\n{combinedError}");

                return process.ExitCode;
            }
            catch (Exception ex)
            {
                combinedError = ex.Message;
                return -1;
            }
        }

        static bool TryFindOnPath(string fileName, out string fullPath)
        {
            foreach (var dir in GetPathDirectories())
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;

                string candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate))
                {
                    fullPath = candidate;
                    return true;
                }
            }

            fullPath = null;
            return false;
        }

        static IEnumerable<string> GetPathDirectories()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in new[]
                     {
                         EnvironmentVariableTarget.Process,
                         EnvironmentVariableTarget.User,
                         EnvironmentVariableTarget.Machine
                     })
            {
                string pathEnv = Environment.GetEnvironmentVariable("PATH", target);
                if (string.IsNullOrWhiteSpace(pathEnv))
                    continue;

                foreach (var dir in pathEnv.Split(Path.PathSeparator))
                {
                    if (seen.Add(dir))
                        yield return dir;
                }
            }
        }
    }

    public sealed class SplatTransformBackgroundJob : IDisposable
    {
        readonly Process m_Process;
        readonly object m_LogLock = new();
        readonly List<string> m_PendingLogLines = new();
        readonly StringBuilder m_Stdout = new();
        readonly StringBuilder m_Stderr = new();
        bool m_Disposed;

        public string CommandLine { get; }

        public bool IsRunning => !m_Disposed && m_Process != null && !m_Process.HasExited;

        SplatTransformBackgroundJob(Process process, string commandLine)
        {
            m_Process = process;
            CommandLine = commandLine;
        }

        public static bool TryStart(
            string executable,
            string arguments,
            string workingDirectory,
            out SplatTransformBackgroundJob job,
            out string error)
        {
            job = null;
            error = null;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                if (!string.IsNullOrEmpty(workingDirectory))
                    psi.WorkingDirectory = workingDirectory;

                ApplyNodeMemoryOptions(psi);

                var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                var backgroundJob = new SplatTransformBackgroundJob(process, $"{executable} {arguments}".Trim());

                process.OutputDataReceived += (_, e) => backgroundJob.HandleLogLine(e.Data, backgroundJob.m_Stdout);
                process.ErrorDataReceived += (_, e) => backgroundJob.HandleLogLine(e.Data, backgroundJob.m_Stderr);

                if (!process.Start())
                {
                    error = $"Failed to start: {executable}";
                    process.Dispose();
                    return false;
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                job = backgroundJob;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        static void ApplyNodeMemoryOptions(ProcessStartInfo psi)
        {
            const string kNodeMemoryFlag = "--max-old-space-size=8192";
            string existing = Environment.GetEnvironmentVariable("NODE_OPTIONS");
            if (string.IsNullOrWhiteSpace(existing))
                psi.EnvironmentVariables["NODE_OPTIONS"] = kNodeMemoryFlag;
            else if (!existing.Contains("max-old-space-size", StringComparison.OrdinalIgnoreCase))
                psi.EnvironmentVariables["NODE_OPTIONS"] = $"{existing} {kNodeMemoryFlag}";
            else
                psi.EnvironmentVariables["NODE_OPTIONS"] = existing;
        }

        void HandleLogLine(string line, StringBuilder sink)
        {
            if (line == null) return;
            sink.AppendLine(line);
            lock (m_LogLock) { m_PendingLogLines.Add(line); }
        }

        public void DrainLogs(Action<string> onLine)
        {
            if (onLine == null) return;
            lock (m_LogLock)
            {
                foreach (var line in m_PendingLogLines)
                    onLine(line);
                m_PendingLogLines.Clear();
            }
        }

        public bool TryGetResult(out int exitCode, out string combinedOutput)
        {
            exitCode = -1;
            combinedOutput = string.Empty;

            if (m_Disposed || m_Process == null)
                return true;

            if (!m_Process.HasExited)
                return false;

            m_Process.WaitForExit();
            exitCode = m_Process.ExitCode;
            string output = m_Stdout.ToString().Trim();
            string error = m_Stderr.ToString().Trim();
            combinedOutput = string.IsNullOrWhiteSpace(output) ? error : output;
            if (!string.IsNullOrWhiteSpace(error) && !string.IsNullOrWhiteSpace(output))
                combinedOutput = output + Environment.NewLine + error;
            return true;
        }

        public void Cancel()
        {
            if (m_Disposed || m_Process == null || m_Process.HasExited) return;
            try { m_Process.Kill(); }
            catch (Exception ex) { Debug.LogWarning($"[SplatTransformBackgroundJob] Cancel failed: {ex.Message}"); }
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            if (m_Process == null) return;

            if (!m_Process.HasExited)
            {
                try { m_Process.Kill(); }
                catch { /* ignored */ }
            }

            m_Process.Dispose();
        }
    }
}
