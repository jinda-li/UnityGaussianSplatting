// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
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
