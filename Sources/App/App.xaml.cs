using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;

namespace MaxLoud
{
    /// <summary>
    /// Starts the WPF/XAML MaxLoud application, enforces a single interactive instance and keeps
    /// the tray-owned main window alive explicitly.
    /// </summary>
    public partial class App : Application
    {
        private MainWindow _mainWindow;
        private SingleInstanceCoordinator _singleInstanceCoordinator;
        private bool _activationRequestedBeforeWindowReady;

        /// <summary>
        /// Creates the hidden tray window. A normal second launch does not create another tray instance;
        /// it activates the settings window of the already-running process instead.
        /// Maintenance command-line modes intentionally run independently of the interactive semaphore.
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (!EnsureElevated(e.Args))
            {
                return;
            }

            DiagnosticLogger.Initialize();
            DiagnosticLogger.Info("Application startup arguments: " + string.Join(" ", e.Args));

            var maintenanceMode =
                HasArgument(e.Args, "--update-apo") ||
                HasArgument(e.Args, "--recover-audio");

            if (!maintenanceMode)
            {
                if (!SingleInstanceCoordinator.TryAcquirePrimary(
                    out _singleInstanceCoordinator))
                {
                    DiagnosticLogger.Info(
                        "Another MaxLoud instance owns the single-instance semaphore. Activation requested.");
                    Shutdown(0);
                    return;
                }

                _singleInstanceCoordinator.StartActivationListener(
                    RequestPrimaryWindowActivation);
            }

            try
            {
                var startupSettings = new SettingsStore().Load();
                UiThemeService.Apply(Current, startupSettings.Theme);
                LocalizationService.Apply(Current, startupSettings.Language);

                _mainWindow = new MainWindow();
                MainWindow = _mainWindow;

                if (HasArgument(e.Args, "--update-apo"))
                {
                    var updated = _mainWindow.InstallOrUpdateSelectedEndpointApo();
                    DiagnosticLogger.Info("--update-apo result=" + updated);
                    Shutdown(updated ? 0 : 2);
                    return;
                }

                if (HasArgument(e.Args, "--recover-audio"))
                {
                    _mainWindow.RecoverSelectedEndpointAudio();
                    MessageBox.Show(
                        "MaxLoud EFX отключён от выбранного устройства. Windows Audio graph пересоздан.",
                        "MaxLoud — восстановление звука",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    Shutdown(0);
                    return;
                }

                _mainWindow.StartTrayApplication();

                if (_activationRequestedBeforeWindowReady)
                {
                    _activationRequestedBeforeWindowReady = false;
                    _mainWindow.ShowAndActivateFromSecondaryInstance();
                }
            }
            catch (Exception exception)
            {
                DiagnosticLogger.Error("Unhandled application startup failure", exception);

                MessageBox.Show(
                    exception.ToString(),
                    "MaxLoud",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                Shutdown(-1);
            }
        }

        /// <summary>
        /// Releases native tray, Core Audio and single-instance resources when WPF terminates.
        /// </summary>
        protected override void OnExit(ExitEventArgs e)
        {
            DiagnosticLogger.Info("Application exit code: " + e.ApplicationExitCode);

            _mainWindow?.DisposeApplicationResources();
            _mainWindow = null;

            _singleInstanceCoordinator?.Dispose();
            _singleInstanceCoordinator = null;

            base.OnExit(e);
        }

        /// <summary>
        /// Ensures the interactive process has an Administrator token. Rider and Explorer may launch
        /// the asInvoker app normally; MaxLoud then restarts itself through the Windows runas verb.
        /// </summary>
        private bool EnsureElevated(string[] arguments)
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                if (principal.IsInRole(WindowsBuiltInRole.Administrator))
                {
                    return true;
                }
            }

            var executablePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                MessageBox.Show(
                    "Не удалось определить путь к MaxLoud.exe для повышения привилегий.",
                    "MaxLoud",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(-1);
                return false;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    WorkingDirectory = AppContext.BaseDirectory,
                    UseShellExecute = true,
                    Verb = "runas"
                };

                foreach (var argument in arguments ?? Array.Empty<string>())
                {
                    startInfo.ArgumentList.Add(argument);
                }

                Process.Start(startInfo);
                Shutdown(0);
                return false;
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                MessageBox.Show(
                    "MaxLoud требует права администратора для управления Windows Audio. Запуск отменён.",
                    "MaxLoud",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Shutdown(1223);
                return false;
            }
        }

        /// <summary>
        /// Marshals a second-launch activation request onto the WPF dispatcher.
        /// </summary>
        private void RequestPrimaryWindowActivation()
        {
            Dispatcher.BeginInvoke(
                new Action(
                    delegate
                    {
                        if (_mainWindow == null)
                        {
                            _activationRequestedBeforeWindowReady = true;
                            return;
                        }

                        DiagnosticLogger.Info(
                            "Secondary launch requested settings-window activation.");

                        _mainWindow.ShowAndActivateFromSecondaryInstance();
                    }),
                DispatcherPriority.Send);
        }

        /// <summary>
        /// Returns whether one command-line switch is present using case-insensitive comparison.
        /// </summary>
        private static bool HasArgument(
            string[] arguments,
            string expected)
        {
            return Array.Exists(
                arguments,
                argument => string.Equals(
                    argument,
                    expected,
                    StringComparison.OrdinalIgnoreCase));
        }
    }
}
