using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace MaxLoud
{
    /// <summary>
    /// Writes MaxLoud application diagnostics and creates a combined diagnostic report with
    /// endpoint/APO state, registry values, recent app/APO log tails and Windows Audio event channels.
    /// </summary>
    internal static class DiagnosticLogger
    {
        private static readonly object Sync = new object();

        public static string AppLogDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MaxLoud",
                "logs");

        public static string ApoLogDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "MaxLoud",
                "logs");

        public static string AppLogPath =>
            Path.Combine(
                AppLogDirectory,
                "MaxLoud-" + DateTime.Now.ToString("yyyyMMdd") + ".log");

        /// <summary>
        /// Creates log directories and starts a new visible application-log session.
        /// </summary>
        public static void Initialize()
        {
            try
            {
                Directory.CreateDirectory(AppLogDirectory);
                Directory.CreateDirectory(ApoLogDirectory);
            }
            catch
            {
            }

            Info(
                "========== MaxLoud session start ==========" + Environment.NewLine +
                "Process: " + Process.GetCurrentProcess().Id +
                ", x64=" + Environment.Is64BitProcess +
                ", OS=" + Environment.OSVersion +
                ", BaseDirectory=" + AppContext.BaseDirectory);
        }

        /// <summary>
        /// Appends one informational diagnostic record.
        /// </summary>
        public static void Info(string message)
        {
            Write("INFO", message);
        }

        /// <summary>
        /// Appends an exception with full stack trace and operation context.
        /// </summary>
        public static void Error(string context, Exception exception)
        {
            Write(
                "ERROR",
                context + Environment.NewLine + exception);
        }

        /// <summary>
        /// Writes a complete selected-endpoint snapshot to the application log.
        /// </summary>
        public static void LogEndpointSnapshot(
            string label,
            AudioEndpointInfo endpoint,
            AudioEndpointManager endpointManager,
            ApoChainManager chainManager,
            ApoDeploymentManager deploymentManager)
        {
            try
            {
                Info(
                    "----- endpoint snapshot: " + label + " -----" +
                    Environment.NewLine +
                    BuildEndpointSnapshot(
                        endpoint,
                        endpointManager,
                        chainManager,
                        deploymentManager));
            }
            catch (Exception exception)
            {
                Error(
                    "Failed to create endpoint snapshot: " + label,
                    exception);
            }
        }

        /// <summary>
        /// Creates a timestamped combined diagnostic report and returns its complete text.
        /// </summary>
        public static string CreateDiagnosticReport(
            AudioEndpointInfo endpoint,
            AudioEndpointManager endpointManager,
            ApoChainManager chainManager,
            ApoDeploymentManager deploymentManager)
        {
            var builder = new StringBuilder();

            builder.AppendLine("MaxLoud diagnostic report");
            builder.AppendLine("Created: " + DateTime.Now.ToString("O"));
            builder.AppendLine(new string('=', 78));
            builder.AppendLine();

            builder.AppendLine("CURRENT ENDPOINT / DEPLOYMENT STATE");
            builder.AppendLine(new string('-', 78));
            builder.AppendLine(
                BuildEndpointSnapshot(
                    endpoint,
                    endpointManager,
                    chainManager,
                    deploymentManager));

            builder.AppendLine();
            builder.AppendLine("WINDOWS AUDIO PROCESS / SERVICE STATE");
            builder.AppendLine(new string('-', 78));
            builder.AppendLine(
                RunCapture(
                    Path.Combine(Environment.SystemDirectory, "sc.exe"),
                    "query AudioSrv"));
            builder.AppendLine(
                RunCapture(
                    Path.Combine(Environment.SystemDirectory, "sc.exe"),
                    "query AudioEndpointBuilder"));
            builder.AppendLine(
                RunCapture(
                    Path.Combine(Environment.SystemDirectory, "tasklist.exe"),
                    "/FI \"IMAGENAME eq audiodg.exe\" /V"));

            builder.AppendLine();
            builder.AppendLine("APPLICATION LOG TAIL");
            builder.AppendLine(new string('-', 78));
            builder.AppendLine(ReadLatestLogTail(AppLogDirectory, "MaxLoud-*.log", 400));

            builder.AppendLine();
            builder.AppendLine("APO / audiodg LOG TAIL");
            builder.AppendLine(new string('-', 78));
            builder.AppendLine(ReadLatestLogTail(ApoLogDirectory, "MaxLoudApo-*.log", 500));

            builder.AppendLine();
            builder.AppendLine("WINDOWS AUDIO EVENT CHANNELS");
            builder.AppendLine(new string('-', 78));
            AppendAudioEventLogs(builder);

            var report = builder.ToString();

            try
            {
                Directory.CreateDirectory(AppLogDirectory);
                var path = Path.Combine(
                    AppLogDirectory,
                    "diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");

                File.WriteAllText(
                    path,
                    report,
                    new UTF8Encoding(false));

                Info("Diagnostic report created: " + path);
            }
            catch (Exception exception)
            {
                Error("Failed to persist diagnostic report", exception);
            }

            return report;
        }

        /// <summary>
        /// Opens both application and APO log directories in Explorer.
        /// </summary>
        public static void OpenLogFolders()
        {
            Directory.CreateDirectory(AppLogDirectory);
            Directory.CreateDirectory(ApoLogDirectory);

            Process.Start(new ProcessStartInfo(
                "explorer.exe",
                "\"" + AppLogDirectory + "\"")
            {
                UseShellExecute = true
            });

            if (!string.Equals(
                AppLogDirectory,
                ApoLogDirectory,
                StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo(
                    "explorer.exe",
                    "\"" + ApoLogDirectory + "\"")
                {
                    UseShellExecute = true
                });
            }
        }

        /// <summary>
        /// Returns a detailed textual snapshot of the selected endpoint and MaxLoud deployment.
        /// </summary>
        private static string BuildEndpointSnapshot(
            AudioEndpointInfo endpoint,
            AudioEndpointManager endpointManager,
            ApoChainManager chainManager,
            ApoDeploymentManager deploymentManager)
        {
            var builder = new StringBuilder();

            builder.AppendLine("Time: " + DateTime.Now.ToString("O"));
            builder.AppendLine("Elevated: " + deploymentManager.IsProcessElevated());
            builder.AppendLine("Development audio host: " + deploymentManager.IsDevelopmentAudioHostEnabled());
            builder.AppendLine("APO registered: " + deploymentManager.IsRegistered());
            builder.AppendLine("Source APO: " + deploymentManager.SourceApoPath);
            builder.AppendLine("Source SHA256: " + GetSha256(deploymentManager.SourceApoPath));
            builder.AppendLine("Installed APO: " + deploymentManager.InstalledApoPath);
            builder.AppendLine("Installed SHA256: " + GetSha256(deploymentManager.InstalledApoPath));
            builder.AppendLine("Installed binary current: " + deploymentManager.IsInstalledBinaryCurrent());

            if (endpoint == null)
            {
                builder.AppendLine("Endpoint: <null>");
                return builder.ToString();
            }

            builder.AppendLine("Endpoint name: " + endpoint.Name);
            builder.AppendLine("Endpoint ID: " + endpoint.Id);
            builder.AppendLine("Endpoint GUID: " + endpoint.EndpointGuid.ToString("B"));
            builder.AppendLine("Endpoint default: " + endpoint.IsDefault);

            try
            {
                builder.AppendLine(
                    "Enable audio enhancements: " +
                    endpointManager.GetSystemEnhancementsEnabled(endpoint));
            }
            catch (Exception exception)
            {
                builder.AppendLine(
                    "Enable audio enhancements: ERROR " + exception.Message);
            }

            builder.AppendLine("FxProperties: " + EndpointFxRegistry.GetFxPropertiesPath(endpoint));

            try
            {
                using (var key = EndpointFxRegistry.OpenRead(endpoint))
                {
                    if (key == null)
                    {
                        builder.AppendLine("  <FxProperties missing>");
                    }
                    else
                    {
                        foreach (var valueName in key.GetValueNames().OrderBy(value => value))
                        {
                            object value;
                            RegistryValueKind kind;

                            try
                            {
                                value = key.GetValue(valueName);
                                kind = key.GetValueKind(valueName);
                            }
                            catch (Exception exception)
                            {
                                builder.AppendLine(
                                    "  " + valueName + " = <ERROR " + exception.Message + ">");
                                continue;
                            }

                            builder.AppendLine(
                                "  " + valueName +
                                " [" + kind + "] = " +
                                FormatRegistryValue(value));
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                builder.AppendLine("FxProperties read ERROR: " + exception);
            }

            builder.AppendLine("APO chain:");

            try
            {
                foreach (var node in chainManager.ReadChain(endpoint))
                {
                    builder.AppendLine(
                        "  " + node.Stage +
                        " position=" + node.Position +
                        " attached=" + node.Attached +
                        " detachable=" + node.CanDetach +
                        " maxloud=" + node.IsMaxLoud +
                        " clsid=" + node.Clsid.ToString("B") +
                        " name=\"" + node.Name + "\"" +
                        " dll=\"" + (node.DllPath ?? "") + "\"");
                }
            }
            catch (Exception exception)
            {
                builder.AppendLine("  APO chain ERROR: " + exception);
            }

            return builder.ToString();
        }

        private static void AppendAudioEventLogs(StringBuilder builder)
        {
            var channelsOutput = RunCapture(
                Path.Combine(Environment.SystemDirectory, "wevtutil.exe"),
                "el");

            var channels = channelsOutput
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(channel => channel.IndexOf("audio", StringComparison.OrdinalIgnoreCase) >= 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToArray();

            if (channels.Length == 0)
            {
                builder.AppendLine("No Windows event channels containing 'audio' were enumerated.");
                builder.AppendLine(channelsOutput);
                return;
            }

            foreach (var channel in channels)
            {
                builder.AppendLine();
                builder.AppendLine("### " + channel);
                builder.AppendLine(
                    RunCapture(
                        Path.Combine(Environment.SystemDirectory, "wevtutil.exe"),
                        "qe \"" + channel.Replace("\"", "") + "\" /c:30 /rd:true /f:xml"));
            }
        }

        private static string ReadLatestLogTail(
            string directory,
            string pattern,
            int maxLines)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return "<log directory does not exist>";
                }

                var file = Directory
                    .GetFiles(directory, pattern)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();

                if (file == null)
                {
                    return "<no matching log file>";
                }

                var queue = new Queue<string>(maxLines);

                foreach (var line in File.ReadLines(file))
                {
                    if (queue.Count == maxLines)
                    {
                        queue.Dequeue();
                    }

                    queue.Enqueue(line);
                }

                return "File: " + file + Environment.NewLine +
                    string.Join(Environment.NewLine, queue);
            }
            catch (Exception exception)
            {
                return "<failed to read log tail: " + exception + ">";
            }
        }

        private static string RunCapture(string fileName, string arguments)
        {
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    process.Start();
                    var stdout = process.StandardOutput.ReadToEnd();
                    var stderr = process.StandardError.ReadToEnd();

                    if (!process.WaitForExit(7000))
                    {
                        try
                        {
                            process.Kill(true);
                        }
                        catch
                        {
                        }

                        return fileName + " " + arguments + Environment.NewLine + "<timeout>";
                    }

                    return
                        fileName + " " + arguments + Environment.NewLine +
                        "ExitCode=" + process.ExitCode + Environment.NewLine +
                        stdout +
                        (string.IsNullOrWhiteSpace(stderr)
                            ? string.Empty
                            : Environment.NewLine + "STDERR:" + Environment.NewLine + stderr);
                }
            }
            catch (Exception exception)
            {
                return fileName + " " + arguments + Environment.NewLine + exception;
            }
        }

        private static string GetSha256(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return "<missing>";
                }

                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(path))
                {
                    return string.Concat(
                        sha.ComputeHash(stream)
                            .Select(value => value.ToString("X2")));
                }
            }
            catch (Exception exception)
            {
                return "<ERROR " + exception.Message + ">";
            }
        }

        private static string FormatRegistryValue(object value)
        {
            if (value == null)
            {
                return "<null>";
            }

            if (value is string[] strings)
            {
                return "[" + string.Join(", ", strings.Select(item => "\"" + item + "\"")) + "]";
            }

            if (value is byte[] bytes)
            {
                return "0x" + BitConverter.ToString(bytes).Replace("-", string.Empty);
            }

            return Convert.ToString(value);
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(AppLogDirectory);

                    File.AppendAllText(
                        AppLogPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz") +
                        " [" + level + "] [pid=" + Process.GetCurrentProcess().Id +
                        ", tid=" + Environment.CurrentManagedThreadId + "] " +
                        message + Environment.NewLine,
                        new UTF8Encoding(false));
                }
            }
            catch
            {
            }
        }
    }
}
