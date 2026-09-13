using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;

namespace MaxLoud
{
    /// <summary>
    /// Registers the locally built MaxLoud APO COM class and AudioEngine metadata for development use.
    /// It also manages the reversible DisableProtectedAudioDG development setting required by an unsigned APO build.
    /// Production deployment can replace this path with a signed componentized APO driver package.
    /// </summary>
    internal sealed class ApoDeploymentManager
    {
        private const string ApoFriendlyName =
            "MaxLoud Endpoint Effect";

        private const string AudioProcessingObjectInterface =
            "{FD7F2B29-24D0-4B5C-B177-592C39F9CA10}";

        // Match Microsoft's default system-APO registration constraints. The MaxLoud processor
        // already supports distinct input/output buffers, so advertising APO_FLAG_INPLACE is unnecessary.
        private const int ApoFlags =
            0x0000000e;

        private const string WindowsAudioSettingsPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio";

        private const string DeploymentStatePath =
            @"SOFTWARE\MaxLoud\Deployment";

        private const string DisableProtectedAudioDgValueName =
            "DisableProtectedAudioDG";

        private const string BackupCapturedValueName =
            "ProtectedAudioBackupCaptured";

        private const string OriginalPresentValueName =
            "ProtectedAudioOriginalPresent";

        private const string OriginalValueName =
            "ProtectedAudioOriginalValue";

        /// <summary>
        /// Gets the deterministic APO DLL path copied next to MaxLoud.exe by the solution build.
        /// </summary>
        public string SourceApoPath =>
            Path.Combine(
                AppContext.BaseDirectory,
                "apo",
                "MaxLoudApo.dll");

        /// <summary>
        /// Gets the DLL currently referenced by COM registration. When no registration exists,
        /// returns the content-addressed target path for the current build.
        /// </summary>
        public string InstalledApoPath
        {
            get
            {
                var registeredPath = GetRegisteredApoPath();
                return string.IsNullOrWhiteSpace(registeredPath)
                    ? GetTargetApoPath()
                    : registeredPath;
            }
        }

        /// <summary>
        /// Gets the Program Files directory containing development APO binaries.
        /// </summary>
        private string InstallDirectory =>
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles),
                "MaxLoud");

        /// <summary>
        /// Returns whether the current MaxLoud process owns an elevated Administrator token.
        /// </summary>
        public bool IsProcessElevated()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        /// <summary>
        /// Returns true when the installed APO DLL is byte-for-byte identical to the DLL shipped with the current MaxLoud build.
        /// </summary>
        public bool IsInstalledBinaryCurrent()
        {
            if (!File.Exists(SourceApoPath) ||
                !File.Exists(InstalledApoPath))
            {
                return false;
            }

            var sourceInfo = new FileInfo(SourceApoPath);
            var installedInfo = new FileInfo(InstalledApoPath);

            if (sourceInfo.Length != installedInfo.Length)
            {
                return false;
            }

            using (var sha256 = SHA256.Create())
            using (var source = File.OpenRead(SourceApoPath))
            using (var installed = File.OpenRead(InstalledApoPath))
            {
                var sourceHash = sha256.ComputeHash(source);
                var installedHash = sha256.ComputeHash(installed);

                for (var index = 0; index < sourceHash.Length; index++)
                {
                    if (sourceHash[index] != installedHash[index])
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Returns true when both the COM class and AudioEngine APO registration exist.
        /// </summary>
        public bool IsRegistered()
        {
            using (var comKey =
                Registry.LocalMachine.OpenSubKey(
                    GetComClsidPath(),
                    false))
            using (var audioEngineKey =
                Registry.LocalMachine.OpenSubKey(
                    GetAudioEnginePath(),
                    false))
            {
                return
                    comKey != null &&
                    audioEngineKey != null;
            }
        }

        /// <summary>
        /// Returns whether audiodg protected-process mode is disabled for unsigned APO development.
        /// </summary>
        public bool IsDevelopmentAudioHostEnabled()
        {
            using (var audioKey =
                Registry.LocalMachine.OpenSubKey(
                    WindowsAudioSettingsPath,
                    false))
            {
                if (audioKey == null)
                {
                    return false;
                }

                var value = audioKey.GetValue(
                    DisableProtectedAudioDgValueName);

                return
                    value != null &&
                    Convert.ToInt32(value) != 0;
            }
        }

        /// <summary>
        /// Saves the original DisableProtectedAudioDG registry state once and enables the development audio host mode.
        /// A new audiodg.exe instance is required before the setting affects APO loading.
        /// </summary>
        public void EnableDevelopmentAudioHost()
        {
            DiagnosticLogger.Info("Enabling development audio host (DisableProtectedAudioDG=1).");
            BackupProtectedAudioSetting();

            using (var audioKey =
                Registry.LocalMachine.CreateSubKey(
                    WindowsAudioSettingsPath,
                    true))
            {
                audioKey.SetValue(
                    DisableProtectedAudioDgValueName,
                    1,
                    RegistryValueKind.DWord);
            }
        }

        /// <summary>
        /// Restores the exact DisableProtectedAudioDG state captured before MaxLoud enabled its development audio host mode.
        /// </summary>
        public void RestoreProtectedAudioSetting()
        {
            DiagnosticLogger.Info("Restoring protected-audio setting from MaxLoud deployment backup.");

            using (var stateKey =
                Registry.LocalMachine.OpenSubKey(
                    DeploymentStatePath,
                    true))
            {
                if (stateKey == null ||
                    Convert.ToInt32(
                        stateKey.GetValue(
                            BackupCapturedValueName,
                            0)) == 0)
                {
                    return;
                }

                var originalPresent =
                    Convert.ToInt32(
                        stateKey.GetValue(
                            OriginalPresentValueName,
                            0)) != 0;

                var originalValue =
                    Convert.ToInt32(
                        stateKey.GetValue(
                            OriginalValueName,
                            0));

                using (var audioKey =
                    Registry.LocalMachine.CreateSubKey(
                        WindowsAudioSettingsPath,
                        true))
                {
                    if (originalPresent)
                    {
                        audioKey.SetValue(
                            DisableProtectedAudioDgValueName,
                            originalValue,
                            RegistryValueKind.DWord);
                    }
                    else
                    {
                        audioKey.DeleteValue(
                            DisableProtectedAudioDgValueName,
                            false);
                    }
                }

                stateKey.DeleteValue(
                    BackupCapturedValueName,
                    false);

                stateKey.DeleteValue(
                    OriginalPresentValueName,
                    false);

                stateKey.DeleteValue(
                    OriginalValueName,
                    false);
            }
        }

        /// <summary>
        /// Restarts the Windows Audio service so audiodg.exe is recreated with the current protected-audio setting
        /// and the endpoint audio graph is rebuilt from the latest APO registration.
        /// </summary>
        public void RestartWindowsAudioService()
        {
            DiagnosticLogger.Info(
                "RestartWindowsAudioService BEGIN. audiodg before: " +
                DescribeAudiodgProcesses());

            RunNetCommand(
                "stop audiosrv /y");

            RunNetCommand(
                "start audiosrv");

            System.Threading.Thread.Sleep(300);

            DiagnosticLogger.Info(
                "RestartWindowsAudioService END. audiodg after: " +
                DescribeAudiodgProcesses());
        }

        /// <summary>
        /// Copies the built native APO to Program Files and registers its COM and AudioEngine metadata.
        /// This method does not attach the APO to any endpoint.
        /// </summary>
        public void RegisterLocalBuild()
        {
            if (!File.Exists(SourceApoPath))
            {
                throw new FileNotFoundException(
                    "MaxLoudApo.dll отсутствует в каталоге сборки приложения.",
                    SourceApoPath);
            }

            var previousRegisteredPath = GetRegisteredApoPath();
            var targetApoPath = GetTargetApoPath();

            DiagnosticLogger.Info(
                "RegisterLocalBuild BEGIN. source=" + SourceApoPath +
                ", previousRegistered=" + (previousRegisteredPath ?? "<none>") +
                ", target=" + targetApoPath);

            Directory.CreateDirectory(InstallDirectory);

            if (!FilesAreIdentical(SourceApoPath, targetApoPath))
            {
                File.Copy(
                    SourceApoPath,
                    targetApoPath,
                    true);
            }
            else
            {
                DiagnosticLogger.Info(
                    "Target APO binary already matches source; copy skipped: " + targetApoPath);
            }

            using (var clsidKey =
                Registry.LocalMachine.CreateSubKey(
                    GetComClsidPath(),
                    true))
            {
                clsidKey.SetValue(
                    null,
                    ApoFriendlyName,
                    RegistryValueKind.String);

                using (var serverKey =
                    clsidKey.CreateSubKey(
                        "InprocServer32",
                        true))
                {
                    serverKey.SetValue(
                        null,
                        targetApoPath,
                        RegistryValueKind.String);

                    serverKey.SetValue(
                        "ThreadingModel",
                        "Both",
                        RegistryValueKind.String);
                }
            }

            using (var apoKey =
                Registry.LocalMachine.CreateSubKey(
                    GetAudioEnginePath(),
                    true))
            {
                apoKey.SetValue(
                    "FriendlyName",
                    ApoFriendlyName,
                    RegistryValueKind.String);

                apoKey.SetValue(
                    "Copyright",
                    "MaxLoud",
                    RegistryValueKind.String);

                apoKey.SetValue(
                    "MajorVersion",
                    1,
                    RegistryValueKind.DWord);

                apoKey.SetValue(
                    "MinorVersion",
                    0,
                    RegistryValueKind.DWord);

                apoKey.SetValue(
                    "Flags",
                    ApoFlags,
                    RegistryValueKind.DWord);

                apoKey.SetValue(
                    "MinInputConnections",
                    1,
                    RegistryValueKind.DWord);

                apoKey.SetValue(
                    "MaxInputConnections",
                    1,
                    RegistryValueKind.DWord);

                apoKey.SetValue(
                    "MinOutputConnections",
                    1,
                    RegistryValueKind.DWord);

                apoKey.SetValue(
                    "MaxOutputConnections",
                    1,
                    RegistryValueKind.DWord);

                apoKey.SetValue(
                    "MaxInstances",
                    unchecked((int)0xffffffff),
                    RegistryValueKind.DWord);

                apoKey.SetValue(
                    "NumAPOInterfaces",
                    1,
                    RegistryValueKind.DWord);

                apoKey.SetValue(
                    "APOInterface0",
                    AudioProcessingObjectInterface,
                    RegistryValueKind.String);
            }

            DiagnosticLogger.Info(
                "RegisterLocalBuild END. registered=" + IsRegistered() +
                ", registeredPath=" + InstalledApoPath +
                ", binaryCurrent=" + IsInstalledBinaryCurrent());

            CleanupStaleApoBinaries(targetApoPath);
        }

        /// <summary>
        /// Removes the local COM and AudioEngine registrations and restores the pre-MaxLoud protected-audio setting.
        /// Endpoint attachment must be removed before calling this method.
        /// </summary>
        public void UnregisterLocalBuild()
        {
            var registeredPath = GetRegisteredApoPath();

            DiagnosticLogger.Info(
                "UnregisterLocalBuild BEGIN. registeredPath=" +
                (registeredPath ?? "<none>"));

            Registry.LocalMachine.DeleteSubKeyTree(
                GetAudioEnginePath(),
                false);

            Registry.LocalMachine.DeleteSubKeyTree(
                GetComClsidPath(),
                false);

            RestoreProtectedAudioSetting();

            CleanupStaleApoBinaries(null);

            DiagnosticLogger.Info("UnregisterLocalBuild END. registered=" + IsRegistered());
        }

        /// <summary>
        /// Returns the current COM InprocServer32 path for MaxLoud, or null when not registered.
        /// </summary>
        private static string GetRegisteredApoPath()
        {
            using (var serverKey =
                Registry.LocalMachine.OpenSubKey(
                    GetComClsidPath() + @"\InprocServer32",
                    false))
            {
                var value = serverKey?.GetValue(null) as string;
                return string.IsNullOrWhiteSpace(value)
                    ? null
                    : Environment.ExpandEnvironmentVariables(value);
            }
        }

        /// <summary>
        /// Returns a content-addressed installation filename so an APO update never has to overwrite
        /// a DLL that audiodg.exe may still have memory-mapped.
        /// </summary>
        private string GetTargetApoPath()
        {
            var hash = GetFileSha256Hex(SourceApoPath);
            var suffix = hash.Length >= 16
                ? hash.Substring(0, 16)
                : hash;

            return Path.Combine(
                InstallDirectory,
                "MaxLoudApo." + suffix + ".dll");
        }

        /// <summary>
        /// Removes obsolete MaxLoud APO binaries that are no longer registered. Locked files are left
        /// for a later cleanup attempt instead of failing installation or removal.
        /// </summary>
        private void CleanupStaleApoBinaries(string keepPath)
        {
            if (!Directory.Exists(InstallDirectory))
            {
                return;
            }

            foreach (var path in Directory.GetFiles(
                InstallDirectory,
                "MaxLoudApo*.dll"))
            {
                if (!string.IsNullOrWhiteSpace(keepPath) &&
                    string.Equals(
                        Path.GetFullPath(path),
                        Path.GetFullPath(keepPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    File.Delete(path);
                    DiagnosticLogger.Info("Deleted stale APO binary: " + path);
                }
                catch (Exception exception)
                {
                    DiagnosticLogger.Info(
                        "Stale APO binary remains locked and will be retried later: " +
                        path + Environment.NewLine + exception.Message);
                }
            }
        }

        /// <summary>
        /// Returns true when both files exist and have equal SHA-256 content.
        /// </summary>
        private static bool FilesAreIdentical(
            string firstPath,
            string secondPath)
        {
            if (!File.Exists(firstPath) ||
                !File.Exists(secondPath))
            {
                return false;
            }

            var firstInfo = new FileInfo(firstPath);
            var secondInfo = new FileInfo(secondPath);

            if (firstInfo.Length != secondInfo.Length)
            {
                return false;
            }

            return string.Equals(
                GetFileSha256Hex(firstPath),
                GetFileSha256Hex(secondPath),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Calculates an uppercase SHA-256 string for a file.
        /// </summary>
        private static string GetFileSha256Hex(string path)
        {
            if (!File.Exists(path))
            {
                return "MISSING";
            }

            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                return string.Concat(
                    sha256.ComputeHash(stream)
                        .Select(value => value.ToString("X2")));
            }
        }

        /// <summary>
        /// Captures whether DisableProtectedAudioDG existed and its exact DWORD value before MaxLoud writes it.
        /// </summary>
        private static void BackupProtectedAudioSetting()
        {
            using (var stateKey =
                Registry.LocalMachine.CreateSubKey(
                    DeploymentStatePath,
                    true))
            {
                if (Convert.ToInt32(
                    stateKey.GetValue(
                        BackupCapturedValueName,
                        0)) != 0)
                {
                    return;
                }

                using (var audioKey =
                    Registry.LocalMachine.OpenSubKey(
                        WindowsAudioSettingsPath,
                        false))
                {
                    var original =
                        audioKey?.GetValue(
                            DisableProtectedAudioDgValueName);

                    stateKey.SetValue(
                        OriginalPresentValueName,
                        original == null ? 0 : 1,
                        RegistryValueKind.DWord);

                    stateKey.SetValue(
                        OriginalValueName,
                        original == null
                            ? 0
                            : Convert.ToInt32(original),
                        RegistryValueKind.DWord);
                }

                stateKey.SetValue(
                    BackupCapturedValueName,
                    1,
                    RegistryValueKind.DWord);
            }
        }

        /// <summary>
        /// Runs one elevated net.exe service-control command and reports its complete output on failure.
        /// </summary>
        private static void RunNetCommand(string arguments)
        {
            DiagnosticLogger.Info("net.exe " + arguments + " BEGIN");

            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(
                        Environment.SystemDirectory,
                        "net.exe"),
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                process.Start();

                var standardOutput =
                    process.StandardOutput.ReadToEnd();

                var standardError =
                    process.StandardError.ReadToEnd();

                process.WaitForExit();

                DiagnosticLogger.Info(
                    "net.exe " + arguments +
                    " exit=" + process.ExitCode +
                    (string.IsNullOrWhiteSpace(standardOutput)
                        ? string.Empty
                        : Environment.NewLine + standardOutput.Trim()) +
                    (string.IsNullOrWhiteSpace(standardError)
                        ? string.Empty
                        : Environment.NewLine + "STDERR: " + standardError.Trim()));

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        "net.exe " + arguments +
                        " завершился с кодом " + process.ExitCode + "." +
                        Environment.NewLine +
                        standardOutput +
                        Environment.NewLine +
                        standardError);
                }
            }
        }

        /// <summary>
        /// Returns audiodg.exe process IDs for graph-restart diagnostics.
        /// </summary>
        private static string DescribeAudiodgProcesses()
        {
            try
            {
                var processes = Process.GetProcessesByName("audiodg");
                var description = processes.Length == 0
                    ? "<none>"
                    : string.Join(", ", processes.Select(process => process.Id.ToString()));

                foreach (var process in processes)
                {
                    process.Dispose();
                }

                return description;
            }
            catch (Exception exception)
            {
                return "<ERROR " + exception.Message + ">";
            }
        }

        /// <summary>
        /// Returns the HKLM Software\Classes COM registration path for the MaxLoud APO CLSID.
        /// </summary>
        private static string GetComClsidPath()
        {
            return
                @"SOFTWARE\Classes\CLSID\" +
                ApoChainManager.MaxLoudApoClsid
                    .ToString("B")
                    .ToUpperInvariant();
        }

        /// <summary>
        /// Returns the HKLM Software\Classes AudioEngine registration path for the MaxLoud APO CLSID.
        /// </summary>
        private static string GetAudioEnginePath()
        {
            return
                @"SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\" +
                ApoChainManager.MaxLoudApoClsid
                    .ToString("B")
                    .ToUpperInvariant();
        }
    }
}
