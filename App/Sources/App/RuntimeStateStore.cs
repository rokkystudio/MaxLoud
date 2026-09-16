using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MaxLoud
{
    /// <summary>
    /// Publishes the current MaxLoud DSP state through a fixed-layout memory-mapped file in ProgramData.
    /// The native APO reads the same mapped bytes without performing file I/O on its real-time thread.
    /// </summary>
    internal sealed class RuntimeStateStore : IDisposable
    {
        private const uint Magic = 0x53444c4d;
        private const uint FormatVersion = 1;
        private const long StateSize = 64;

        private const uint FlagMaxLoudEnabled = 1 << 0;
        private const uint FlagCompressorEnabled = 1 << 1;
        private const uint FlagEqualizerEnabled = 1 << 2;
        private const uint FlagLimiterEnabled = 1 << 3;

        private readonly FileStream _fileStream;
        private readonly MemoryMappedFile _mappedFile;
        private readonly MemoryMappedViewAccessor _view;

        /// <summary>
        /// Opens %PROGRAMDATA%\MaxLoud\state.bin, grants the Windows Audio host account read access
        /// and keeps one shared mapping alive for the tray process lifetime.
        /// </summary>
        public RuntimeStateStore()
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "MaxLoud");

            var logDirectory = Path.Combine(
                directory,
                "logs");

            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(logDirectory);

            var path = Path.Combine(directory, "state.bin");
            _fileStream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite);

            if (_fileStream.Length != StateSize)
            {
                _fileStream.SetLength(StateSize);
            }

            EnsureAudioHostAccess(
                directory,
                path,
                logDirectory);

            _mappedFile = MemoryMappedFile.CreateFromFile(
                _fileStream,
                null,
                StateSize,
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None,
                false);

            _view = _mappedFile.CreateViewAccessor(
                0,
                StateSize,
                MemoryMappedFileAccess.ReadWrite);
        }

        /// <summary>
        /// Publishes one coherent DSP snapshot using an odd/even sequence number around the payload update.
        /// </summary>
        public void Write(AppSettings settings)
        {
            var sequence = _view.ReadUInt32(8);
            if ((sequence & 1) != 0)
            {
                sequence++;
            }

            var writeSequence = sequence + 1;
            var committedSequence = sequence + 2;

            _view.Write(8, writeSequence);
            _view.Write(0, Magic);
            _view.Write(4, FormatVersion);
            _view.Write(12, BuildFlags(settings));

            _view.Write(16, settings.ThresholdDb);
            _view.Write(20, settings.Ratio);
            _view.Write(24, settings.AttackMs);
            _view.Write(28, settings.ReleaseMs);
            _view.Write(32, settings.InputGainDb);
            _view.Write(36, settings.LimiterCeilingDb);

            _view.Write(40, settings.Eq80Db);
            _view.Write(44, settings.Eq250Db);
            _view.Write(48, settings.Eq1000Db);
            _view.Write(52, settings.Eq3000Db);
            _view.Write(56, settings.Eq8000Db);
            _view.Write(60, settings.VolumeLeveling);

            _view.Flush();
            _view.Write(8, committedSequence);
            _view.Flush();
        }

        /// <summary>
        /// Releases the shared view, mapping and backing file.
        /// </summary>
        public void Dispose()
        {
            _view.Dispose();
            _mappedFile.Dispose();
            _fileStream.Dispose();
        }

        /// <summary>
        /// Grants the Local Service account used by audiodg.exe the minimum rights required by MaxLoud:
        /// read-only access to runtime state and create/append access inside the APO diagnostic-log directory.
        /// Existing SYSTEM/Administrator/user ACL entries are preserved.
        /// </summary>
        private static void EnsureAudioHostAccess(
            string directory,
            string statePath,
            string logDirectory)
        {
            try
            {
                var localService = new SecurityIdentifier(
                    WellKnownSidType.LocalServiceSid,
                    null);

                var rootInfo = new DirectoryInfo(directory);
                var rootSecurity = rootInfo.GetAccessControl();
                rootSecurity.AddAccessRule(
                    new FileSystemAccessRule(
                        localService,
                        FileSystemRights.ReadAndExecute | FileSystemRights.ListDirectory,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None,
                        AccessControlType.Allow));
                rootInfo.SetAccessControl(rootSecurity);

                var stateInfo = new FileInfo(statePath);
                var stateSecurity = stateInfo.GetAccessControl();
                stateSecurity.AddAccessRule(
                    new FileSystemAccessRule(
                        localService,
                        FileSystemRights.Read,
                        AccessControlType.Allow));
                stateInfo.SetAccessControl(stateSecurity);

                var logsInfo = new DirectoryInfo(logDirectory);
                var logsSecurity = logsInfo.GetAccessControl();
                logsSecurity.AddAccessRule(
                    new FileSystemAccessRule(
                        localService,
                        FileSystemRights.Modify,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None,
                        AccessControlType.Allow));
                logsInfo.SetAccessControl(logsSecurity);

                DiagnosticLogger.Info(
                    "Runtime state ACL prepared for NT AUTHORITY\\LOCAL SERVICE: state=Read, APO logs=Modify.");
            }
            catch (Exception exception)
            {
                DiagnosticLogger.Error(
                    "Failed to grant Local Service access to MaxLoud runtime state/logs",
                    exception);
            }
        }

        /// <summary>
        /// Converts tray-level enable switches to the bit field consumed by MaxLoudApo.dll.
        /// </summary>
        private static uint BuildFlags(AppSettings settings)
        {
            var flags = 0u;

            if (settings.MaxLoudEnabled)
            {
                flags |= FlagMaxLoudEnabled;
            }

            if (settings.CompressorEnabled)
            {
                flags |= FlagCompressorEnabled;
            }

            if (settings.EqualizerEnabled)
            {
                flags |= FlagEqualizerEnabled;
            }

            if (settings.LimiterEnabled)
            {
                flags |= FlagLimiterEnabled;
            }

            return flags;
        }
    }
}
