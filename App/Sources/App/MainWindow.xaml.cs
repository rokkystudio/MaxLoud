using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using FlagsPack;
using NeoUI;
using Forms = System.Windows.Forms;

namespace MaxLoud
{
    /// <summary>
    /// WPF/XAML control surface for the selected Windows render endpoint, APO chain and MaxLoud DSP.
    /// The same window owns the native tray icon and remains hidden when MaxLoud is running in the tray.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly SettingsStore _settingsStore;
        private readonly RuntimeStateStore _runtimeStateStore;
        private readonly AudioEndpointManager _endpointManager;
        private readonly ApoChainManager _apoChainManager;
        private readonly ApoDeploymentManager _apoDeploymentManager;

        private readonly IntPtr _activeTrayIcon;
        private readonly IntPtr _inactiveTrayIcon;

        private readonly DispatcherTimer _singleClickTimer;
        private readonly DispatcherTimer _statusTimer;
        private readonly DispatcherTimer _startupTimer;

        private AppSettings _settings;
        private AudioEndpointInfo[] _endpoints = Array.Empty<AudioEndpointInfo>();
        private AudioEndpointInfo _selectedEndpoint;
        private TrayIconService _trayIconService;
        private Forms.ContextMenuStrip _trayContextMenu;
        private Forms.ToolStripMenuItem _trayDspMenuItem;
        private Forms.ToolStripMenuItem _trayOpenMenuItem;
        private Forms.ToolStripMenuItem _trayExitMenuItem;

        private bool _updatingEndpointControls = true;
        private bool _updatingDspControls = true;
        private bool _updatingStartupControl = true;
        private bool _integrationPromptShown;
        private bool _closeApplication;
        private bool _resourcesDisposed;
        private bool _trayStarted;
        private bool? _lastObservedEnhancementsState;
        private bool? _lastObservedMaxLoudAttached;
        private bool _systemEnhancementsOperationInProgress;

        public ObservableCollection<ApoNodeViewModel> ApoNodes { get; } =
            new ObservableCollection<ApoNodeViewModel>();

        /// <summary>
        /// Creates the hidden WPF settings window and initializes all non-tray application state.
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
            TitleBar.VersionText = ApplicationInfo.VersionText;
            DataContext = this;

            _settingsStore = new SettingsStore();
            _settings = _settingsStore.Load();
            _settings.Theme = UiThemeService.NormalizeTheme(_settings.Theme);
            _settings.Language = LocalizationService.NormalizeLanguage(_settings.Language);
            _settingsStore.Save(_settings);
            UiThemeService.Apply(Application.Current, _settings.Theme);
            LocalizationService.Apply(Application.Current, _settings.Language);
            UpdateHeaderButtons();
            InitializeStartupRegistration();

            _runtimeStateStore = new RuntimeStateStore();
            _endpointManager = new AudioEndpointManager();
            _apoChainManager = new ApoChainManager();
            _apoDeploymentManager = new ApoDeploymentManager();

            DiagnosticLogger.Info("MainWindow initialized. Loading endpoint and runtime state.");
            EnsureSelectedEndpoint();
            _runtimeStateStore.Write(_settings);

            _activeTrayIcon = TrayIconFactory.Create(true);
            _inactiveTrayIcon = TrayIconFactory.Create(false);
            SetWindowIcon();

            _trayContextMenu = CreateTrayContextMenu();

            _singleClickTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(
                    Math.Max(200, (int)GetDoubleClickTime() + 50))
            };
            _singleClickTimer.Tick += SingleClickTimer_OnTick;

            _statusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _statusTimer.Tick += StatusTimer_OnTick;

            _startupTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _startupTimer.Tick += StartupTimer_OnTick;

            RefreshEndpoints(_settings.RenderEndpointId);
            LoadDspControls(_settings);

            _updatingEndpointControls = false;
            _updatingDspControls = false;

            UpdateDspMasterAppearance();
            LoadSelectedEndpointState();

            DiagnosticLogger.LogEndpointSnapshot(
                "initial application state",
                GetSelectedEndpoint(),
                _endpointManager,
                _apoChainManager,
                _apoDeploymentManager);
        }

        /// <summary>
        /// Installs or updates the current MaxLoud APO build, attaches it to the selected endpoint EFX stage
        /// and returns whether the registered binary and endpoint attachment match the requested state.
        /// </summary>
        public bool InstallOrUpdateSelectedEndpointApo()
        {
            RegisterAndAttachMaxLoud(true);

            var endpoint = GetSelectedEndpoint();
            return
                endpoint != null &&
                _apoDeploymentManager.IsInstalledBinaryCurrent() &&
                _apoChainManager.IsMaxLoudAttached(endpoint);
        }

        /// <summary>
        /// Emergency recovery path used when a development MaxLoud APO breaks the selected endpoint graph.
        /// Only the MaxLoud EFX CLSID is detached; vendor/Windows APO entries are preserved exactly.
        /// </summary>
        public void RecoverSelectedEndpointAudio()
        {
            var endpoint = GetSelectedEndpoint();
            if (endpoint == null)
            {
                throw new InvalidOperationException(
                    "Не найден выбранный render endpoint для восстановления звука.");
            }

            DiagnosticLogger.LogEndpointSnapshot(
                "before emergency recovery",
                endpoint,
                _endpointManager,
                _apoChainManager,
                _apoDeploymentManager);

            if (_apoChainManager.IsMaxLoudAttached(endpoint))
            {
                _apoChainManager.SetMaxLoudAttached(
                    endpoint,
                    false);
            }

            _settings.MaxLoudEnabled = false;
            SaveAndPublishSettings();
            _apoDeploymentManager.RestoreProtectedAudioSetting();
            _apoDeploymentManager.RestartWindowsAudioService();
            LoadSelectedEndpointState();

            DiagnosticLogger.LogEndpointSnapshot(
                "after emergency recovery",
                endpoint,
                _endpointManager,
                _apoChainManager,
                _apoDeploymentManager);
        }

        /// <summary>
        /// Creates the native tray icon after the WPF window has been initialized.
        /// The main window itself stays hidden unless integration needs attention.
        /// </summary>
        public void StartTrayApplication()
        {
            if (_trayStarted)
            {
                return;
            }

            _trayStarted = true;
            DiagnosticLogger.Info("Starting tray application.");
            _trayIconService = new TrayIconService(this);
            _trayIconService.LeftClick += TrayIcon_OnLeftClick;
            _trayIconService.LeftDoubleClick += TrayIcon_OnLeftDoubleClick;
            _trayIconService.RightClick += TrayIcon_OnRightClick;

            _trayIconService.SetIcon(
                _inactiveTrayIcon,
                "MaxLoud");

            _statusTimer.Start();
            _startupTimer.Start();
            UpdateTrayState();
        }

        /// <summary>
        /// Releases the tray icon, native icons, timers, Core Audio enumerator and runtime mapping once.
        /// </summary>
        public void DisposeApplicationResources()
        {
            if (_resourcesDisposed)
            {
                return;
            }

            _resourcesDisposed = true;

            _singleClickTimer.Stop();
            _statusTimer.Stop();
            _startupTimer.Stop();

            if (_trayContextMenu != null)
            {
                _trayContextMenu.Close();
                _trayContextMenu.Dispose();
                _trayContextMenu = null;
                _trayDspMenuItem = null;
                _trayOpenMenuItem = null;
                _trayExitMenuItem = null;
            }

            if (_trayIconService != null)
            {
                _trayIconService.LeftClick -= TrayIcon_OnLeftClick;
                _trayIconService.LeftDoubleClick -= TrayIcon_OnLeftDoubleClick;
                _trayIconService.RightClick -= TrayIcon_OnRightClick;
                _trayIconService.Dispose();
                _trayIconService = null;
            }

            TrayIconFactory.Destroy(_activeTrayIcon);
            TrayIconFactory.Destroy(_inactiveTrayIcon);
            _endpointManager.Dispose();
            _runtimeStateStore.Dispose();
        }

        /// <summary>
        /// Re-enumerates active render endpoints while preserving the requested exact MMDevice ID when possible.
        /// </summary>
        private void RefreshEndpoints(string endpointId)
        {
            _updatingEndpointControls = true;

            try
            {
                _endpoints =
                    _endpointManager.GetActiveRenderEndpoints().ToArray();

                var selected = _endpoints.FirstOrDefault(
                    endpoint => string.Equals(
                        endpoint.Id,
                        endpointId,
                        StringComparison.OrdinalIgnoreCase));

                selected ??= _endpoints.FirstOrDefault(
                    endpoint => endpoint.IsDefault);

                selected ??= _endpoints.FirstOrDefault();
                _selectedEndpoint = selected;
                UpdateEndpointButton();
            }
            finally
            {
                _updatingEndpointControls = false;
            }

            LoadSelectedEndpointState();
        }

        /// <summary>
        /// Loads persisted MaxLoud DSP values into the XAML controls without publishing intermediate slider events.
        /// </summary>
        private void LoadDspControls(AppSettings settings)
        {
            _updatingDspControls = true;

            try
            {
                MaxLoudEnabledCheckBox.IsChecked = settings.MaxLoudEnabled;
                CompressorCheckBox.IsChecked = settings.CompressorEnabled;
                EqualizerCheckBox.IsChecked = settings.EqualizerEnabled;
                LimiterCheckBox.IsChecked = settings.LimiterEnabled;

                ThresholdSlider.Value = settings.ThresholdDb;
                RatioSlider.Value = settings.Ratio;
                AttackSlider.Value = settings.AttackMs;
                ReleaseSlider.Value = settings.ReleaseMs;
                VolumeLevelingSlider.Value = settings.VolumeLeveling;
                InputGainSlider.Value = settings.InputGainDb;
                LimiterSlider.Value = settings.LimiterCeilingDb;

                Eq80Slider.Value = settings.Eq80Db;
                Eq250Slider.Value = settings.Eq250Db;
                Eq1000Slider.Value = settings.Eq1000Db;
                Eq3000Slider.Value = settings.Eq3000Db;
                Eq8000Slider.Value = settings.Eq8000Db;
            }
            finally
            {
                _updatingDspControls = false;
            }

            UpdateDspMasterAppearance();
        }

        /// <summary>
        /// Shows and activates the settings window, refreshing the selected endpoint before presenting it.
        /// </summary>
        private void ShowSettingsWindow(bool promptForIntegration)
        {
            EnsureSelectedEndpoint();
            RefreshEndpoints(_settings.RenderEndpointId);
            LoadDspControls(_settings);

            if (!IsVisible)
            {
                Show();
            }

            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            ForceSettingsWindowToForeground();

            if (promptForIntegration && !_integrationPromptShown)
            {
                Dispatcher.BeginInvoke(
                    new Action(PromptForIntegrationIfNeeded),
                    DispatcherPriority.Background);
            }
        }

        /// <summary>
        /// Restores and foregrounds the settings window when another MaxLoud.exe launch is rejected
        /// by the named single-instance semaphore.
        /// </summary>
        internal void ShowAndActivateFromSecondaryInstance()
        {
            ShowSettingsWindow(false);
        }

        /// <summary>
        /// Brings the custom WPF window in front of other applications even when it was hidden,
        /// minimized or buried in the current desktop's z-order.
        /// </summary>
        private void ForceSettingsWindowToForeground()
        {
            var handle = new WindowInteropHelper(this).EnsureHandle();

            ShowWindow(handle, ShowWindowRestore);
            SetForegroundWindow(handle);

            Topmost = true;
            Activate();
            Focus();
            BringIntoView();

            Dispatcher.BeginInvoke(
                new Action(delegate { Topmost = false; }),
                DispatcherPriority.ApplicationIdle);
        }

        /// <summary>
        /// Hides the custom XAML window while keeping the application and tray icon alive.
        /// </summary>
        private void HideToTray()
        {
            Hide();
        }

        /// <summary>
        /// Delays the tray single-click action long enough to distinguish it from a double click.
        /// </summary>
        private void TrayIcon_OnLeftClick(object sender, EventArgs e)
        {
            _singleClickTimer.Stop();
            _singleClickTimer.Start();
        }

        /// <summary>
        /// Cancels the pending single click and opens MaxLoud on tray double click.
        /// </summary>
        private void TrayIcon_OnLeftDoubleClick(object sender, EventArgs e)
        {
            _singleClickTimer.Stop();
            ShowSettingsWindow(true);
        }

        /// <summary>
        /// Opens the WinForms ContextMenuStrip used by Network Diagram at the current mouse position.
        /// The notification-area menu is intentionally isolated from WPF theme resources.
        /// </summary>
        private void TrayIcon_OnRightClick(object sender, EventArgs e)
        {
            UpdateTrayState();
            NeoTrayMenuStyle.Show(
                _trayContextMenu,
                new WindowInteropHelper(this).Handle,
                Forms.Cursor.Position);
        }

        /// <summary>
        /// Создаёт tray menu через общий NeoUI builder и сохраняет ссылки на динамические пункты.
        /// </summary>
        private Forms.ContextMenuStrip CreateTrayContextMenu()
        {
            var builder = new NeoTrayMenuBuilder();

            _trayDspMenuItem = builder.AddItem(string.Empty, delegate { ToggleMaxLoud(); });
            _trayOpenMenuItem = builder.AddItem(string.Empty, delegate { ShowSettingsWindow(true); });

            builder.AddSeparator();

            _trayExitMenuItem = builder.AddItem(string.Empty, delegate { CloseApplication(); });

            return builder.Build();
        }

        /// <summary>
        /// Executes the deferred tray single-click action exactly once.
        /// </summary>
        private void SingleClickTimer_OnTick(object sender, EventArgs e)
        {
            _singleClickTimer.Stop();
            ToggleMaxLoud();
        }

        /// <summary>
        /// Refreshes the effective tray state while MaxLoud is running.
        /// </summary>
        private void StatusTimer_OnTick(object sender, EventArgs e)
        {
            ObserveExternalEndpointChanges();
            UpdateTrayState();
        }

        /// <summary>
        /// Detects endpoint changes made outside MaxLoud and synchronizes the UI/diagnostics.
        /// Windows owns the graph transition for changes committed through the endpoint property store;
        /// MaxLoud does not restart AudioSrv merely because the external state changed.
        /// </summary>
        private void ObserveExternalEndpointChanges()
        {
            if (_systemEnhancementsOperationInProgress)
            {
                return;
            }

            try
            {
                var endpoint = GetSelectedEndpoint();
                if (endpoint == null)
                {
                    return;
                }

                var enhancementsEnabled =
                    _endpointManager.GetSystemEnhancementsEnabled(endpoint);

                var maxLoudAttached =
                    _apoChainManager.IsMaxLoudAttached(endpoint);

                if (!_lastObservedEnhancementsState.HasValue ||
                    !_lastObservedMaxLoudAttached.HasValue)
                {
                    SynchronizeObservedEndpointState(endpoint);

                    DiagnosticLogger.Info(
                        "Initial observed endpoint state: enhancements=" + enhancementsEnabled +
                        ", MaxLoudAttached=" + maxLoudAttached);
                    return;
                }

                var previousEnhancementsState =
                    _lastObservedEnhancementsState.Value;

                var enhancementsChanged =
                    previousEnhancementsState != enhancementsEnabled;

                if (!enhancementsChanged &&
                    _lastObservedMaxLoudAttached.Value == maxLoudAttached)
                {
                    return;
                }

                DiagnosticLogger.Info(
                    "External endpoint state change observed: enhancements " +
                    previousEnhancementsState + " -> " + enhancementsEnabled +
                    ", MaxLoudAttached " +
                    _lastObservedMaxLoudAttached.Value + " -> " + maxLoudAttached);

                SynchronizeObservedEndpointState(endpoint);

                DiagnosticLogger.LogEndpointSnapshot(
                    "externally observed endpoint state transition",
                    endpoint,
                    _endpointManager,
                    _apoChainManager,
                    _apoDeploymentManager);

                if (!enhancementsChanged)
                {
                    return;
                }

                DiagnosticLogger.Info(
                    "External Enable audio enhancements transition detected. Windows owns the graph transition; no AudioSrv restart requested by MaxLoud.");

                LoadSelectedEndpointState();
                SynchronizeObservedEndpointState(endpoint);
                UpdateTrayState();
            }
            catch (Exception exception)
            {
                DiagnosticLogger.Error(
                    "Failed while observing external endpoint state",
                    exception);
            }
            finally
            {
                if (_systemEnhancementsOperationInProgress)
                {
                    _systemEnhancementsOperationInProgress = false;
                    SetSystemEnhancementsOperationUi(false, null);
                }
            }
        }

        /// <summary>
        /// Synchronizes the observer baseline after MaxLoud or the observer itself has deliberately rebuilt
        /// the endpoint graph, preventing an internal operation from being mistaken for a new external change.
        /// </summary>
        private void SynchronizeObservedEndpointState(AudioEndpointInfo endpoint)
        {
            _lastObservedEnhancementsState =
                _endpointManager.GetSystemEnhancementsEnabled(endpoint);

            _lastObservedMaxLoudAttached =
                _apoChainManager.IsMaxLoudAttached(endpoint);
        }

        /// <summary>
        /// Opens the settings window shortly after launch when integration is missing or the installed DLL is outdated.
        /// </summary>
        private void StartupTimer_OnTick(object sender, EventArgs e)
        {
            _startupTimer.Stop();

            try
            {
                var endpoint = GetSelectedEndpoint();

                if (endpoint != null && NeedsIntegration(endpoint))
                {
                    ShowSettingsWindow(true);
                }
            }
            catch
            {
                ShowSettingsWindow(true);
            }
        }

        /// <summary>
        /// Returns true when registration, EFX attachment or installed APO binary requires user attention.
        /// </summary>
        private bool NeedsIntegration(AudioEndpointInfo endpoint)
        {
            if (!_apoDeploymentManager.IsRegistered())
            {
                return true;
            }

            if (!_apoChainManager.IsMaxLoudAttached(endpoint))
            {
                return true;
            }

            return !_apoDeploymentManager.IsInstalledBinaryCurrent();
        }

        /// <summary>
        /// Toggles only the MaxLoud DSP master bypass and leaves Windows/Realtek APO state untouched.
        /// </summary>
        private void ToggleMaxLoud()
        {
            _settings.MaxLoudEnabled = !_settings.MaxLoudEnabled;
            SaveAndPublishSettings();

            _updatingDspControls = true;
            MaxLoudEnabledCheckBox.IsChecked = _settings.MaxLoudEnabled;
            _updatingDspControls = false;

            UpdateDspMasterAppearance();
            UpdateTrayState();

            if (IsVisible)
            {
                LoadSelectedEndpointState();
            }
        }

        /// <summary>
        /// Publishes the complete current XAML DSP control state immediately for live tuning.
        /// </summary>
        private void PublishDspControls()
        {
            if (_updatingDspControls)
            {
                return;
            }

            var endpoint = GetSelectedEndpoint();

            _settings = new AppSettings
            {
                RenderEndpointId = endpoint?.Id ?? _settings.RenderEndpointId,
                Theme = _settings.Theme,
                Language = _settings.Language,
                RunOnStartup = _settings.RunOnStartup,
                MaxLoudEnabled = MaxLoudEnabledCheckBox.IsChecked == true,
                CompressorEnabled = CompressorCheckBox.IsChecked == true,
                EqualizerEnabled = EqualizerCheckBox.IsChecked == true,
                LimiterEnabled = LimiterCheckBox.IsChecked == true,
                ThresholdDb = (float)ThresholdSlider.Value,
                Ratio = (float)RatioSlider.Value,
                AttackMs = (float)AttackSlider.Value,
                ReleaseMs = (float)ReleaseSlider.Value,
                VolumeLeveling = (float)VolumeLevelingSlider.Value,
                InputGainDb = (float)InputGainSlider.Value,
                LimiterCeilingDb = (float)LimiterSlider.Value,
                Eq80Db = (float)Eq80Slider.Value,
                Eq250Db = (float)Eq250Slider.Value,
                Eq1000Db = (float)Eq1000Slider.Value,
                Eq3000Db = (float)Eq3000Slider.Value,
                Eq8000Db = (float)Eq8000Slider.Value
            };

            SaveAndPublishSettings();
            UpdateDspMasterAppearance();
            UpdateTrayState();
        }

        /// <summary>
        /// Saves JSON settings and publishes one coherent runtime-state snapshot for MaxLoudApo.dll.
        /// </summary>
        private void SaveAndPublishSettings()
        {
            _settingsStore.Save(_settings);
            _runtimeStateStore.Write(_settings);
        }

        /// <summary>
        /// Makes the current master bypass state explicit and prevents inactive DSP controls from
        /// looking usable while the MaxLoud processing master is bypassed.
        /// </summary>
        private void UpdateDspMasterAppearance()
        {
            var enabled = MaxLoudEnabledCheckBox.IsChecked == true;

            DspControlsGrid.IsEnabled = enabled;
            DspControlsGrid.Opacity = enabled ? 1.0 : 0.45;

            var masterText =
                LocalizationService.Text("LocDspMaster", _settings.Language);

            var stateText = LocalizationService.Text(
                enabled ? "LocOn" : "LocOff",
                _settings.Language);

            if (enabled)
            {
                MaxLoudEnabledCheckBox.Content = masterText + " — " + stateText;
                DspMasterStatusTextBlock.Text =
                    LocalizationService.Text("LocDspActive", _settings.Language);

                DspMasterStatusTextBlock.Foreground =
                    (Brush)FindResource("MutedTextBrush");
            }
            else
            {
                MaxLoudEnabledCheckBox.Content = masterText + " — " + stateText;
                DspMasterStatusTextBlock.Text =
                    LocalizationService.Text("LocDspBypassed", _settings.Language);

                DspMasterStatusTextBlock.Foreground =
                    (Brush)FindResource("DangerBrush");
            }
        }

        /// <summary>
        /// Reloads the real endpoint system-effects switch, grouped APO chain and deployment status.
        /// </summary>
        private void LoadSelectedEndpointState()
        {
            if (_endpointManager == null ||
                _apoChainManager == null ||
                _apoDeploymentManager == null)
            {
                return;
            }

            var endpoint = GetSelectedEndpoint();
            _updatingEndpointControls = true;

            try
            {
                ApoNodes.Clear();

                if (endpoint == null)
                {
                    SystemEnhancementsCheckBox.IsChecked = false;
                    IntegrationStatusTextBlock.Text =
                        "MaxLoud APO: render endpoint не выбран.";
                    StatusTextBlock.Text = "Render endpoint is unavailable.";
                    return;
                }

                SystemEnhancementsCheckBox.IsChecked =
                    _endpointManager.GetSystemEnhancementsEnabled(endpoint);

                var chain = _apoChainManager.ReadChain(endpoint);

                foreach (var stage in new[]
                {
                    ApoStage.Sfx,
                    ApoStage.Mfx,
                    ApoStage.Efx
                })
                {
                    foreach (var node in chain.Where(item => item.Stage == stage))
                    {
                        ApoNodes.Add(new ApoNodeViewModel(node));
                    }
                }

                var maxLoudNode = chain.FirstOrDefault(node => node.IsMaxLoud);
                var maxLoudAttached = maxLoudNode != null && maxLoudNode.Attached;
                var registered = _apoDeploymentManager.IsRegistered();
                var elevated = _apoDeploymentManager.IsProcessElevated();
                var binaryCurrent = registered && _apoDeploymentManager.IsInstalledBinaryCurrent();

                IntegrationStatusTextBlock.Text =
                    (registered && maxLoudAttached
                        ? binaryCurrent
                            ? "MaxLoud APO: интегрирован в выбранный endpoint."
                            : "MaxLoud APO: подключён, но доступно обновление DLL."
                        : registered
                            ? "MaxLoud APO: зарегистрирован, но EFX не подключён к выбранному endpoint."
                            : "MaxLoud APO: не интегрирован. Нажмите «Установить / обновить MaxLoud APO».") +
                    "  Administrator: " + (elevated ? "YES" : "NO");

                var audioHostState =
                    _apoDeploymentManager.IsDevelopmentAudioHostEnabled()
                        ? "Audio host: DEVELOPMENT (DisableProtectedAudioDG=1)"
                        : "Audio host: protected";

                StatusTextBlock.Text =
                    endpoint.Name + Environment.NewLine +
                    "MaxLoud DSP master: " +
                    (MaxLoudEnabledCheckBox.IsChecked == true ? "ON" : "OFF") +
                    Environment.NewLine +
                    "Administrator token: " +
                    (elevated ? "YES" : "NO") +
                    Environment.NewLine +
                    "System enhancements: " +
                    (SystemEnhancementsCheckBox.IsChecked == true ? "ON" : "OFF") +
                    Environment.NewLine +
                    "MaxLoud APO registration: " +
                    (registered ? "installed" : "not installed") +
                    Environment.NewLine +
                    "MaxLoud EFX: " +
                    (maxLoudAttached ? "attached" : "not attached") +
                    Environment.NewLine +
                    "Installed APO binary: " +
                    (registered
                        ? binaryCurrent ? "current" : "update available"
                        : "not installed") +
                    Environment.NewLine +
                    audioHostState;
            }
            catch (Exception exception)
            {
                IntegrationStatusTextBlock.Text =
                    "Ошибка чтения endpoint/APO: " + exception.Message;

                StatusTextBlock.Text = exception.ToString();
            }
            finally
            {
                _updatingEndpointControls = false;
            }
        }

        /// <summary>
        /// Offers installation or binary update once during the current process lifetime when it is required.
        /// </summary>
        private void PromptForIntegrationIfNeeded()
        {
            if (_integrationPromptShown)
            {
                return;
            }

            _integrationPromptShown = true;
            var endpoint = GetSelectedEndpoint();

            if (endpoint == null || !NeedsIntegration(endpoint))
            {
                return;
            }

            if (!_apoDeploymentManager.IsProcessElevated())
            {
                ShowNeoMessage(
                    "Для интеграции MaxLoud APO нужны права администратора.\r\n" +
                    "Перезапустите MaxLoud через Run as administrator.",
                    "MaxLoud — требуются права администратора",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var registered = _apoDeploymentManager.IsRegistered();
            var attached = registered && _apoChainManager.IsMaxLoudAttached(endpoint);
            var binaryCurrent = registered && _apoDeploymentManager.IsInstalledBinaryCurrent();

            var prompt =
                registered && attached && !binaryCurrent
                    ? "Установленная MaxLoudApo.dll отличается от DLL текущей сборки.\r\n\r\n" +
                      "Обновить MaxLoud APO для:\r\n" + endpoint.Name + "?\r\n\r\n" +
                      "Audio graph будет временно остановлен, старая DLL выгружена, затем новая DLL будет установлена и EFX подключён обратно."
                    : "MaxLoud APO ещё не интегрирован в:\r\n" +
                      endpoint.Name + "\r\n\r\n" +
                      "Установить MaxLoudApo.dll и добавить его в EFX после существующего APO производителя?\r\n" +
                      "Существующие SFX/MFX/EFX производителя будут сохранены.";

            if (!_apoDeploymentManager.IsDevelopmentAudioHostEnabled())
            {
                prompt +=
                    "\r\n\r\nТекущая DLL является неподписанной dev-сборкой, поэтому MaxLoud временно включит " +
                    "development audio host и восстановит исходную настройку при удалении APO.";
            }

            var answer = ShowNeoMessage(
                prompt,
                "MaxLoud — интеграция APO",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer == MessageBoxResult.Yes)
            {
                RegisterAndAttachMaxLoud(true);
            }
        }

        /// <summary>
        /// Registers or updates MaxLoudApo.dll, attaches it to EFX and recreates the Windows Audio graph.
        /// </summary>
        private void RegisterAndAttachMaxLoud(bool developmentConsentAlreadyGiven)
        {
            var endpoint = GetSelectedEndpoint();
            if (endpoint == null)
            {
                return;
            }

            try
            {
                DiagnosticLogger.LogEndpointSnapshot(
                    "before MaxLoud APO install/update",
                    endpoint,
                    _endpointManager,
                    _apoChainManager,
                    _apoDeploymentManager);

                if (!_apoDeploymentManager.IsProcessElevated())
                {
                    throw new InvalidOperationException(
                        "MaxLoud запущен без elevated Administrator token.");
                }

                if (!_apoDeploymentManager.IsDevelopmentAudioHostEnabled())
                {
                    if (!developmentConsentAlreadyGiven)
                    {
                        var answer = ShowNeoMessage(
                            "Текущая MaxLoudApo.dll является локальной неподписанной dev-сборкой. " +
                            "Windows Audio не загрузит её в защищённый audiodg.exe.\r\n\r\n" +
                            "MaxLoud сохранит исходное значение DisableProtectedAudioDG, включит development audio host " +
                            "и восстановит прежнее значение при удалении local APO.\r\n\r\n" +
                            "Продолжить?",
                            "MaxLoud — development APO",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning);

                        if (answer != MessageBoxResult.Yes)
                        {
                            return;
                        }
                    }

                    _apoDeploymentManager.EnableDevelopmentAudioHost();
                }

                if (_apoChainManager.IsMaxLoudAttached(endpoint))
                {
                    _apoChainManager.SetMaxLoudAttached(endpoint, false);
                    _apoDeploymentManager.RestartWindowsAudioService();
                }

                _apoDeploymentManager.RegisterLocalBuild();
                _apoChainManager.SetMaxLoudAttached(endpoint, true);
                _apoDeploymentManager.RestartWindowsAudioService();
                LoadSelectedEndpointState();
                SynchronizeObservedEndpointState(endpoint);
                UpdateTrayState();

                DiagnosticLogger.LogEndpointSnapshot(
                    "after MaxLoud APO install/update",
                    endpoint,
                    _endpointManager,
                    _apoChainManager,
                    _apoDeploymentManager);
            }
            catch (Exception exception)
            {
                ShowOperationError(
                    "MaxLoud — APO installation",
                    exception);
            }
        }

        /// <summary>
        /// Detaches MaxLoud from the selected endpoint while leaving the local APO registration installed.
        /// </summary>
        private void DetachMaxLoud()
        {
            var endpoint = GetSelectedEndpoint();
            if (endpoint == null)
            {
                return;
            }

            try
            {
                DiagnosticLogger.LogEndpointSnapshot(
                    "before MaxLoud EFX detach",
                    endpoint,
                    _endpointManager,
                    _apoChainManager,
                    _apoDeploymentManager);

                _apoChainManager.SetMaxLoudAttached(endpoint, false);
                _apoDeploymentManager.RestartWindowsAudioService();
                LoadSelectedEndpointState();
                UpdateTrayState();

                DiagnosticLogger.LogEndpointSnapshot(
                    "after MaxLoud EFX detach",
                    endpoint,
                    _endpointManager,
                    _apoChainManager,
                    _apoDeploymentManager);
            }
            catch (Exception exception)
            {
                ShowOperationError(
                    "MaxLoud — APO chain",
                    exception);
            }
        }

        /// <summary>
        /// Detaches MaxLoud, unloads the graph, removes the local APO and restores protected-audio state.
        /// </summary>
        private void UnregisterMaxLoud()
        {
            var endpoint = GetSelectedEndpoint();
            if (endpoint == null)
            {
                return;
            }

            try
            {
                DiagnosticLogger.LogEndpointSnapshot(
                    "before MaxLoud APO removal",
                    endpoint,
                    _endpointManager,
                    _apoChainManager,
                    _apoDeploymentManager);

                if (_apoChainManager.IsMaxLoudAttached(endpoint))
                {
                    _apoChainManager.SetMaxLoudAttached(endpoint, false);
                    _apoDeploymentManager.RestartWindowsAudioService();
                }

                _apoDeploymentManager.UnregisterLocalBuild();
                _apoDeploymentManager.RestartWindowsAudioService();
                LoadSelectedEndpointState();
                UpdateTrayState();

                DiagnosticLogger.LogEndpointSnapshot(
                    "after MaxLoud APO removal",
                    endpoint,
                    _endpointManager,
                    _apoChainManager,
                    _apoDeploymentManager);
            }
            catch (Exception exception)
            {
                ShowOperationError(
                    "MaxLoud — APO removal",
                    exception);
            }
        }

        /// <summary>
        /// Restarts Windows Audio so endpoint graphs are recreated from the current registry and APO state.
        /// </summary>
        private void RestartWindowsAudio()
        {
            try
            {
                var endpoint = GetSelectedEndpoint();
                DiagnosticLogger.LogEndpointSnapshot(
                    "before manual Windows Audio restart",
                    endpoint,
                    _endpointManager,
                    _apoChainManager,
                    _apoDeploymentManager);

                _apoDeploymentManager.RestartWindowsAudioService();
                LoadSelectedEndpointState();
                UpdateTrayState();

                DiagnosticLogger.LogEndpointSnapshot(
                    "after manual Windows Audio restart",
                    endpoint,
                    _endpointManager,
                    _apoChainManager,
                    _apoDeploymentManager);
            }
            catch (Exception exception)
            {
                ShowOperationError(
                    "MaxLoud — Windows Audio",
                    exception);
            }
        }

        /// <summary>
        /// Writes one CompositeFX attach/detach operation and recreates the audio graph.
        /// </summary>
        private void ApplyApoNodeState(ApoNodeInfo node, bool attached)
        {
            var endpoint = GetSelectedEndpoint();
            if (endpoint == null)
            {
                return;
            }

            try
            {
                DiagnosticLogger.Info(
                    "APO node toggle requested: endpoint=" + endpoint.Name +
                    ", stage=" + node.Stage +
                    ", clsid=" + node.Clsid.ToString("B") +
                    ", attached=" + attached);

                DiagnosticLogger.LogEndpointSnapshot(
                    "before APO node toggle",
                    endpoint,
                    _endpointManager,
                    _apoChainManager,
                    _apoDeploymentManager);

                _apoChainManager.SetAttached(endpoint, node, attached);
                _apoDeploymentManager.RestartWindowsAudioService();
                LoadSelectedEndpointState();
                UpdateTrayState();

                DiagnosticLogger.LogEndpointSnapshot(
                    "after APO node toggle",
                    endpoint,
                    _endpointManager,
                    _apoChainManager,
                    _apoDeploymentManager);
            }
            catch (Exception exception)
            {
                ShowOperationError(
                    "MaxLoud — APO chain",
                    exception);
            }
        }

        /// <summary>
        /// Selects the multimedia default render endpoint when the stored device no longer exists.
        /// </summary>
        private void EnsureSelectedEndpoint()
        {
            var endpoints = _endpointManager.GetActiveRenderEndpoints();

            if (endpoints.Any(
                endpoint => string.Equals(
                    endpoint.Id,
                    _settings.RenderEndpointId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var selected =
                endpoints.FirstOrDefault(endpoint => endpoint.IsDefault) ??
                endpoints.FirstOrDefault();

            _settings.RenderEndpointId = selected?.Id;
            _settingsStore.Save(_settings);
        }

        /// <summary>
        /// Returns the endpoint currently selected by exact MMDevice ID.
        /// </summary>
        private AudioEndpointInfo GetSelectedEndpoint()
        {
            if (_selectedEndpoint != null)
            {
                return _selectedEndpoint;
            }

            return _endpointManager
                .GetActiveRenderEndpoints()
                .FirstOrDefault(
                    endpoint => string.Equals(
                        endpoint.Id,
                        _settings.RenderEndpointId,
                        StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Updates tray brightness, tooltip and WPF tray-menu check state from effective processing availability.
        /// </summary>
        private void UpdateTrayState()
        {
            var effective = false;
            var stateText = LocalizationService.Text(
                "LocTrayStateDspOff",
                _settings.Language);

            if (_settings.MaxLoudEnabled)
            {
                try
                {
                    var endpoint = GetSelectedEndpoint();

                    if (endpoint == null)
                    {
                        stateText = LocalizationService.Text(
                            "LocTrayStateDeviceUnavailable",
                            _settings.Language);
                    }
                    else if (!_apoDeploymentManager.IsRegistered())
                    {
                        stateText = LocalizationService.Text(
                            "LocTrayStateApoNotInstalled",
                            _settings.Language);
                    }
                    else if (!_apoChainManager.IsMaxLoudAttached(endpoint))
                    {
                        stateText = LocalizationService.Text(
                            "LocTrayStateEfxDetached",
                            _settings.Language);
                    }
                    else if (!_endpointManager.GetSystemEnhancementsEnabled(endpoint))
                    {
                        stateText = LocalizationService.Text(
                            "LocTrayStateEnhancementsOff",
                            _settings.Language);
                    }
                    else
                    {
                        effective = true;
                        stateText = LocalizationService.Text(
                            "LocTrayStateDspOn",
                            _settings.Language);
                    }
                }
                catch (Exception exception)
                {
                    stateText = string.Format(
                        LocalizationService.Text("LocTrayStateError", _settings.Language),
                        exception.Message);
                }
            }

            if (_trayDspMenuItem != null)
            {
                _trayDspMenuItem.Text =
                    LocalizationService.Text("LocTrayDsp", _settings.Language);
                _trayDspMenuItem.Checked = _settings.MaxLoudEnabled;
            }

            if (_trayOpenMenuItem != null)
            {
                _trayOpenMenuItem.Text = LocalizationService.Text("LocTrayOpen", _settings.Language);
            }

            if (_trayExitMenuItem != null)
            {
                _trayExitMenuItem.Text = LocalizationService.Text("LocTrayExit", _settings.Language);
            }

            if (_trayIconService != null)
            {
                try
                {
                    _trayIconService.SetIcon(
                        effective ? _activeTrayIcon : _inactiveTrayIcon,
                        "MaxLoud — " + stateText);
                }
                catch
                {
                    // Explorer can recreate the notification area transiently; the next status tick retries.
                }
            }
        }

        /// <summary>
        /// Converts the generated manometer HICON to the WPF window Icon property.
        /// </summary>
        private void SetWindowIcon()
        {
            Icon = Imaging.CreateBitmapSourceFromHIcon(
                _activeTrayIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
        }

        /// <summary>
        /// Shows a modal NeoUI message window using the active MaxLoud language and theme.
        /// </summary>
        private MessageBoxResult ShowNeoMessage(
            string message,
            string title,
            MessageBoxButton buttons,
            MessageBoxImage image)
        {
            return NeoMessageBox.ShowMessage(
                this,
                message,
                title,
                buttons,
                image,
                LocalizationService.Text("LocDialogOk", _settings.Language),
                LocalizationService.Text("LocDialogYes", _settings.Language),
                LocalizationService.Text("LocDialogNo", _settings.Language),
                LocalizationService.Text("LocDialogCancel", _settings.Language));
        }

        /// <summary>
        /// Displays one operation failure and immediately reloads endpoint state so the XAML controls stay truthful.
        /// </summary>
        private void ShowOperationError(string title, Exception exception)
        {
            DiagnosticLogger.Error(title, exception);

            ShowNeoMessage(
                exception.ToString(),
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            LoadSelectedEndpointState();
            UpdateTrayState();
        }

        /// <summary>
        /// Opens the modern Windows Sound settings page.
        /// </summary>
        private static void OpenWindowsSoundSettings()
        {
            Process.Start(new ProcessStartInfo("ms-settings:sound")
            {
                UseShellExecute = true
            });
        }

        /// <summary>
        /// Opens the classic Sound Control Panel on Playback.
        /// </summary>
        private static void OpenSoundControlPanel()
        {
            Process.Start(new ProcessStartInfo(
                "control.exe",
                "mmsys.cpl,,0")
            {
                UseShellExecute = true
            });
        }

        /// <summary>
        /// Opens the classic Windows per-application Volume Mixer.
        /// </summary>
        private static void OpenWindowsVolumeMixer()
        {
            Process.Start(new ProcessStartInfo("SndVol.exe")
            {
                UseShellExecute = true
            });
        }

        private void EndpointButton_OnClick(object sender, RoutedEventArgs e)
        {
            NeoContextMenu.Show(CreateEndpointMenu(), EndpointButton);
        }

        private ContextMenu CreateEndpointMenu()
        {
            var menu = new ContextMenu();

            foreach (var endpoint in _endpoints)
            {
                var item = new MenuItem
                {
                    Header = CreateEndpointMenuHeader(endpoint),
                    IsCheckable = true,
                    IsChecked = string.Equals(
                        endpoint.Id,
                        _selectedEndpoint?.Id,
                        StringComparison.OrdinalIgnoreCase),
                    Tag = endpoint
                };

                item.Click += delegate
                {
                    SelectEndpoint((AudioEndpointInfo)item.Tag);
                };

                menu.Items.Add(item);
            }

            return menu;
        }

        private FrameworkElement CreateEndpointMenuHeader(AudioEndpointInfo endpoint)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal
            };

            panel.Children.Add(CreateEndpointIcon(16.0));
            panel.Children.Add(new TextBlock
            {
                Text = GetEndpointDisplayName(endpoint),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 520
            });

            return panel;
        }

        private static Viewbox CreateEndpointIcon(double size)
        {
            var canvas = new Canvas
            {
                Width = 16,
                Height = 16
            };

            var fill = new Path
            {
                Data = Geometry.Parse("M 1,6 L 5,6 L 9,2 L 9,14 L 5,10 L 1,10 Z")
            };
            fill.SetResourceReference(Shape.FillProperty, "MutedTextBrush");
            canvas.Children.Add(fill);

            var waves = new Path
            {
                Data = Geometry.Parse("M 11,5 C 13,6 13,10 11,11 M 12.5,3.5 C 16,5.5 16,10.5 12.5,12.5"),
                StrokeThickness = 1.2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            waves.SetResourceReference(Shape.StrokeProperty, "MutedTextBrush");
            canvas.Children.Add(waves);

            return new Viewbox
            {
                Width = size,
                Height = size,
                Child = canvas,
                Stretch = Stretch.Uniform
            };
        }

        private void SelectEndpoint(AudioEndpointInfo endpoint)
        {
            if (endpoint == null)
            {
                return;
            }

            _selectedEndpoint = endpoint;
            _settings.RenderEndpointId = endpoint.Id;
            UpdateEndpointButton();
            SaveAndPublishSettings();
            LoadSelectedEndpointState();
            UpdateTrayState();
        }

        private void UpdateEndpointButton()
        {
            if (EndpointButtonText == null)
            {
                return;
            }

            EndpointButtonText.Text =
                _selectedEndpoint == null
                    ? string.Empty
                    : GetEndpointDisplayName(_selectedEndpoint);
        }

        private string GetEndpointDisplayName(AudioEndpointInfo endpoint)
        {
            if (endpoint == null)
            {
                return string.Empty;
            }

            if (!endpoint.IsDefault)
            {
                return endpoint.Name;
            }

            return endpoint.Name + "  [" +
                LocalizationService.Text("LocDefaultSuffix", _settings.Language) + "]";
        }

        /// <summary>
        /// Открывает меню языка относительно общей кнопки NeoUI.
        /// </summary>
        private void LanguageButton_OnClick(object sender, RoutedEventArgs e)
        {
            TitleBar.OpenLanguageMenu(CreateLanguageMenu());
        }

        /// <summary>
        /// Создаёт меню выбора языка через общий NeoUI builder и общий каталог флагов.
        /// </summary>
        private ContextMenu CreateLanguageMenu()
        {
            NeoLanguageMenuBuilder builder = new NeoLanguageMenuBuilder();
            string selectedLanguage = LocalizationService.NormalizeLanguage(_settings.Language);

            builder.AddLanguage(
                LocalizationService.AutomaticLanguage,
                LocalizationService.SystemLanguageDisplayText(_settings.Language),
                GetLanguageFlagCountryCode(LocalizationService.AutomaticLanguage),
                string.Equals(
                    selectedLanguage,
                    LocalizationService.AutomaticLanguage,
                    StringComparison.Ordinal),
                ApplyLanguage);

            builder.AddSeparator();

            builder.AddLanguage(
                LocalizationService.EnglishLanguage,
                "English",
                "US",
                string.Equals(
                    selectedLanguage,
                    LocalizationService.EnglishLanguage,
                    StringComparison.Ordinal),
                ApplyLanguage);

            builder.AddLanguage(
                LocalizationService.RussianLanguage,
                "Русский",
                "RU",
                string.Equals(
                    selectedLanguage,
                    LocalizationService.RussianLanguage,
                    StringComparison.Ordinal),
                ApplyLanguage);

            ContextMenu menu = builder.Build();
            menu.MinWidth = 190;
            return menu;
        }

        private void ApplyLanguage(string language)
        {
            _settings.Language = LocalizationService.NormalizeLanguage(language);
            _settingsStore.Save(_settings);
            LocalizationService.Apply(Application.Current, _settings.Language);
            UpdateHeaderButtons();
            UpdateEndpointButton();
            UpdateDspMasterAppearance();
            UpdateTrayState();
        }

        private void ThemeButton_OnClick(object sender, RoutedEventArgs e)
        {
            _settings.Theme = UiThemeService.ToggleTheme(_settings.Theme);
            _settingsStore.Save(_settings);
            UiThemeService.Apply(Application.Current, _settings.Theme);
            UpdateHeaderButtons();
        }

        /// <summary>
        /// Обновляет флаг языка и иконку темы из общего каталога NeoUI.
        /// </summary>
        private void UpdateHeaderButtons()
        {
            if (TitleBar == null || _settings == null)
            {
                return;
            }

            TitleBar.LanguageIconSource =
                CountryFlags.Load(GetLanguageFlagCountryCode(_settings.Language));

            string themePath = UiThemeService.IsDarkTheme(_settings.Theme)
                ? NeoAssetPaths.ThemeDark
                : NeoAssetPaths.ThemeLight;
            TitleBar.ThemeIconSource = NeoAssetService.LoadImage(themePath);
        }

        private string GetLanguageFlagCountryCode(string language)
        {
            return string.Equals(
                LocalizationService.ResolveLanguage(language),
                LocalizationService.RussianLanguage,
                StringComparison.Ordinal)
                ? "RU"
                : "US";
        }

        private void RefreshEndpointsButton_OnClick(object sender, RoutedEventArgs e)
        {
            RefreshEndpoints(GetSelectedEndpoint()?.Id ?? _settings.RenderEndpointId);
        }

        /// <summary>
        /// Applies the system-effects switch only for a real user activation of the WPF checkbox.
        /// Programmatic IsChecked synchronization must never write the endpoint property or restart audio.
        /// </summary>
        private async void SystemEnhancementsCheckBox_OnClick(object sender, RoutedEventArgs e)
        {
            if (_updatingEndpointControls ||
                _systemEnhancementsOperationInProgress)
            {
                return;
            }

            var endpoint = GetSelectedEndpoint();
            if (endpoint == null)
            {
                return;
            }

            await ApplySystemEnhancementsStateAsync(
                endpoint,
                SystemEnhancementsCheckBox.IsChecked == true);
        }

        /// <summary>
        /// Writes the requested Windows system-effects state through Core Audio's endpoint property store.
        /// The checkbox changes immediately; Windows performs the graph transition after Commit().
        /// </summary>
        private async Task ApplySystemEnhancementsStateAsync(
            AudioEndpointInfo endpoint,
            bool requestedState)
        {
            _systemEnhancementsOperationInProgress = true;
            SetSystemEnhancementsOperationUi(
                true,
                LocalizationService.Text(
                    requestedState ? "LocEnabling" : "LocDisabling",
                    _settings.Language));

            await Dispatcher.Yield(DispatcherPriority.Render);

            try
            {
                var previousState = _endpointManager.GetSystemEnhancementsEnabled(endpoint);

                DiagnosticLogger.Info(
                    "Enable audio enhancements user action: endpoint=" + endpoint.Name +
                    ", previous=" + previousState +
                    ", requested=" + requestedState);

                DiagnosticLogger.LogEndpointSnapshot(
                    "before Enable audio enhancements user action",
                    endpoint,
                    _endpointManager,
                    _apoChainManager,
                    _apoDeploymentManager);

                _endpointManager.SetSystemEnhancementsEnabled(
                    endpoint,
                    requestedState);

                DiagnosticLogger.Info(
                    "Enable audio enhancements endpoint property committed through Core Audio property store.");

                await Task.Delay(250);
                LoadSelectedEndpointState();
                SynchronizeObservedEndpointState(endpoint);
                UpdateTrayState();

                DiagnosticLogger.LogEndpointSnapshot(
                    "after Enable audio enhancements user action",
                    endpoint,
                    _endpointManager,
                    _apoChainManager,
                    _apoDeploymentManager);
            }
            catch (Exception exception)
            {
                LoadSelectedEndpointState();
                SynchronizeObservedEndpointState(endpoint);

                ShowOperationError(
                    "MaxLoud — system effects",
                    exception);
            }
            finally
            {
                _systemEnhancementsOperationInProgress = false;
                SetSystemEnhancementsOperationUi(false, null);
            }
        }

        /// <summary>
        /// Shows a short non-blocking status next to the Windows system-effects checkbox while AudioSrv is rebuilding.
        /// </summary>
        private void SetSystemEnhancementsOperationUi(bool inProgress, string status)
        {
            SystemEnhancementsCheckBox.IsEnabled = !inProgress;
            SystemEnhancementsOperationTextBlock.Text = status ?? string.Empty;
            SystemEnhancementsOperationTextBlock.Visibility =
                inProgress
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        /// <summary>
        /// Reflects the saved startup preference in the UI and refreshes the elevated logon task path.
        /// </summary>
        private void InitializeStartupRegistration()
        {
            _updatingStartupControl = true;
            RunOnStartupCheckBox.IsChecked = _settings.RunOnStartup;
            _updatingStartupControl = false;

            if (!_settings.RunOnStartup)
            {
                return;
            }

            try
            {
                StartupRegistrationService.SetEnabled(true);
            }
            catch (Exception exception)
            {
                DiagnosticLogger.Error(
                    "Failed to configure Windows startup task",
                    exception);

                _settings.RunOnStartup = false;
                _settingsStore.Save(_settings);

                _updatingStartupControl = true;
                RunOnStartupCheckBox.IsChecked = false;
                _updatingStartupControl = false;
            }
        }

        /// <summary>
        /// Applies the Windows-startup checkbox immediately and keeps settings truthful if Task Scheduler rejects it.
        /// </summary>
        private void RunOnStartupCheckBox_OnChanged(object sender, RoutedEventArgs e)
        {
            if (_updatingStartupControl)
            {
                return;
            }

            var requestedState = RunOnStartupCheckBox.IsChecked == true;

            try
            {
                StartupRegistrationService.SetEnabled(requestedState);
                _settings.RunOnStartup = requestedState;
                _settingsStore.Save(_settings);
            }
            catch (Exception exception)
            {
                DiagnosticLogger.Error(
                    "Failed to change Windows startup task",
                    exception);

                _updatingStartupControl = true;
                RunOnStartupCheckBox.IsChecked = _settings.RunOnStartup;
                _updatingStartupControl = false;

                ShowNeoMessage(
                    exception.Message,
                    "MaxLoud — Windows startup",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ApoNodeCheckBox_OnClick(object sender, RoutedEventArgs e)
        {
            if (!(sender is CheckBox checkBox) ||
                !(checkBox.DataContext is ApoNodeViewModel item) ||
                !item.CanDetach)
            {
                return;
            }

            ApplyApoNodeState(
                item.Node,
                checkBox.IsChecked == true);
        }

        private void DspControl_OnChanged(object sender, RoutedEventArgs e)
        {
            if (_updatingDspControls)
            {
                return;
            }

            PublishDspControls();

            if (ReferenceEquals(sender, MaxLoudEnabledCheckBox))
            {
                LoadSelectedEndpointState();
            }
        }

        private void DspSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingDspControls)
            {
                return;
            }

            PublishDspControls();
        }

        private void InstallApoButton_OnClick(object sender, RoutedEventArgs e)
        {
            RegisterAndAttachMaxLoud(false);
        }

        private void DetachApoButton_OnClick(object sender, RoutedEventArgs e)
        {
            DetachMaxLoud();
        }

        private void RemoveApoButton_OnClick(object sender, RoutedEventArgs e)
        {
            UnregisterMaxLoud();
        }

        private void RestartAudioButton_OnClick(object sender, RoutedEventArgs e)
        {
            RestartWindowsAudio();
        }

        private void WindowsSoundSettingsButton_OnClick(object sender, RoutedEventArgs e)
        {
            OpenWindowsSoundSettings();
        }

        private void SoundControlPanelButton_OnClick(object sender, RoutedEventArgs e)
        {
            OpenSoundControlPanel();
        }

        private void VolumeMixerButton_OnClick(object sender, RoutedEventArgs e)
        {
            OpenWindowsVolumeMixer();
        }

        private void OpenLogsButton_OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                DiagnosticLogger.OpenLogFolders();
            }
            catch (Exception exception)
            {
                ShowOperationError("MaxLoud — logs", exception);
            }
        }

        private void CopyDiagnosticsButton_OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var diagnostics = DiagnosticLogger.CreateDiagnosticReport(
                    GetSelectedEndpoint(),
                    _endpointManager,
                    _apoChainManager,
                    _apoDeploymentManager);

                Clipboard.SetText(diagnostics);

                ShowNeoMessage(
                    "Диагностика собрана, сохранена в папке logs и скопирована в буфер обмена.",
                    "MaxLoud — diagnostics",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception exception)
            {
                ShowOperationError("MaxLoud — diagnostics", exception);
            }
        }

        private void CloseButton_OnClick(object sender, RoutedEventArgs e)
        {
            HideToTray();
        }

        private void Window_OnClosing(object sender, CancelEventArgs e)
        {
            if (_closeApplication)
            {
                return;
            }

            e.Cancel = true;
            HideToTray();
        }

        private void Window_OnClosed(object sender, EventArgs e)
        {
            DisposeApplicationResources();
        }

        /// <summary>
        /// Performs the explicit tray Exit action and terminates the WPF application.
        /// </summary>
        private void CloseApplication()
        {
            if (_closeApplication)
            {
                return;
            }

            _closeApplication = true;
            Close();
            Application.Current.Shutdown();
        }

        private const int ShowWindowRestore = 9;

        [DllImport("user32.dll")]
        private static extern uint GetDoubleClickTime();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr windowHandle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr windowHandle, int command);
    }
}
