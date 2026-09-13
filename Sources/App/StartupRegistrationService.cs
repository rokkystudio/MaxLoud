using System;
using System.Diagnostics;
using System.IO;

namespace MaxLoud
{
    /// <summary>
    /// Registers MaxLoud for the current user's logon through Task Scheduler.
    /// A scheduled task is used instead of HKCU Run because MaxLoud requires an elevated token.
    /// </summary>
    internal static class StartupRegistrationService
    {
        private const string TaskName = "RokkyStudio.MaxLoud";

        /// <summary>
        /// Creates, refreshes or removes the current-user logon task.
        /// </summary>
        public static void SetEnabled(bool enabled)
        {
            if (enabled)
            {
                CreateOrUpdateTask();
                return;
            }

            DeleteTask();
        }

        private static void CreateOrUpdateTask()
        {
            var executablePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                throw new InvalidOperationException(
                    "Не удалось определить путь к MaxLoud.exe для автозапуска.");
            }

            var arguments =
                "/Create /F /SC ONLOGON /RL HIGHEST /IT " +
                "/TN " + QuoteArgument(TaskName) + " " +
                "/TR " + QuoteArgument(QuoteExecutableForTask(executablePath));

            RunSchtasks(arguments, true);
            DiagnosticLogger.Info(
                "Windows startup task enabled: " + TaskName +
                ", executable=" + executablePath);
        }

        private static void DeleteTask()
        {
            if (!TaskExists())
            {
                DiagnosticLogger.Info(
                    "Windows startup task already absent: " + TaskName);
                return;
            }

            RunSchtasks(
                "/Delete /F /TN " + QuoteArgument(TaskName),
                true);

            DiagnosticLogger.Info(
                "Windows startup task disabled: " + TaskName);
        }

        private static bool TaskExists()
        {
            return RunSchtasks(
                "/Query /TN " + QuoteArgument(TaskName),
                false) == 0;
        }

        private static int RunSchtasks(string arguments, bool throwOnFailure)
        {
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(
                        Environment.SystemDirectory,
                        "schtasks.exe"),
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                process.Start();
                var standardOutput = process.StandardOutput.ReadToEnd();
                var standardError = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (throwOnFailure && process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        "Не удалось изменить автозапуск MaxLoud. schtasks.exe завершился с кодом " +
                        process.ExitCode + "." + Environment.NewLine +
                        standardOutput + Environment.NewLine + standardError);
                }

                return process.ExitCode;
            }
        }

        private static string QuoteExecutableForTask(string executablePath)
        {
            return "\"" + executablePath.Replace("\"", "\\\"") + "\"";
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }
    }
}
