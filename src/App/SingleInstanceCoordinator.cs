using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace MaxLoud
{
    /// <summary>
    /// Owns the named MaxLoud single-instance semaphore and a named activation event used by
    /// later launches to ask the primary WPF process to restore and foreground its settings window.
    /// </summary>
    internal sealed class SingleInstanceCoordinator : IDisposable
    {
        private const string SemaphoreName = @"Local\RokkyStudio.MaxLoud.SingleInstance.x64";
        private const string ActivationEventName = @"Local\RokkyStudio.MaxLoud.Activate.x64";

        private readonly Semaphore _semaphore;
        private readonly EventWaitHandle _activationEvent;
        private readonly EventWaitHandle _shutdownEvent;
        private Thread _activationThread;
        private bool _ownsSemaphore;
        private bool _disposed;

        private SingleInstanceCoordinator(
            Semaphore semaphore,
            EventWaitHandle activationEvent)
        {
            _semaphore = semaphore;
            _activationEvent = activationEvent;
            _shutdownEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
            _ownsSemaphore = true;
        }

        /// <summary>
        /// Attempts to acquire the single-instance semaphore. When another MaxLoud instance owns it,
        /// signals that process to foreground its existing settings window and returns false.
        /// </summary>
        public static bool TryAcquirePrimary(out SingleInstanceCoordinator coordinator)
        {
            coordinator = null;

            var semaphore = new Semaphore(
                1,
                1,
                SemaphoreName);

            if (!semaphore.WaitOne(0))
            {
                semaphore.Dispose();
                SignalPrimaryInstance();
                return false;
            }

            var activationEvent = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                ActivationEventName);

            coordinator = new SingleInstanceCoordinator(
                semaphore,
                activationEvent);

            return true;
        }

        /// <summary>
        /// Starts one background waiter which invokes the supplied callback whenever another process
        /// attempts to launch MaxLoud.
        /// </summary>
        public void StartActivationListener(Action activatePrimaryWindow)
        {
            if (activatePrimaryWindow == null)
            {
                throw new ArgumentNullException(nameof(activatePrimaryWindow));
            }

            if (_activationThread != null)
            {
                return;
            }

            _activationThread = new Thread(
                new ThreadStart(
                    delegate
                    {
                    var handles = new WaitHandle[]
                    {
                        _activationEvent,
                        _shutdownEvent
                    };

                    while (true)
                    {
                        var signaled = WaitHandle.WaitAny(handles);
                        if (signaled == 1)
                        {
                            return;
                        }

                        try
                        {
                            activatePrimaryWindow();
                        }
                        catch (Exception exception)
                        {
                            DiagnosticLogger.Error(
                                "Failed to activate primary MaxLoud window",
                                exception);
                        }
                    }
                }))
            {
                IsBackground = true,
                Name = "MaxLoud single-instance activation listener"
            };

            _activationThread.Start();
        }

        /// <summary>
        /// Releases the activation waiter and named single-instance semaphore.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _shutdownEvent.Set();

            if (_activationThread != null &&
                _activationThread.IsAlive)
            {
                _activationThread.Join(2000);
            }

            _activationThread = null;
            _activationEvent.Dispose();
            _shutdownEvent.Dispose();

            if (_ownsSemaphore)
            {
                try
                {
                    _semaphore.Release();
                }
                catch (SemaphoreFullException)
                {
                }

                _ownsSemaphore = false;
            }

            _semaphore.Dispose();
        }

        /// <summary>
        /// Signals the primary process and grants it foreground permission when Windows allows it.
        /// The activation-event open is retried briefly to cover a second launch racing primary startup.
        /// </summary>
        private static void SignalPrimaryInstance()
        {
            AllowPrimaryProcessToTakeForeground();

            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    using (var activationEvent =
                        EventWaitHandle.OpenExisting(ActivationEventName))
                    {
                        activationEvent.Set();
                        return;
                    }
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    Thread.Sleep(50);
                }
                catch (UnauthorizedAccessException)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Lets the already-running MaxLoud process call SetForegroundWindow in response to this launch.
        /// </summary>
        private static void AllowPrimaryProcessToTakeForeground()
        {
            var currentProcess = Process.GetCurrentProcess();

            foreach (var process in Process.GetProcessesByName(currentProcess.ProcessName))
            {
                try
                {
                    if (process.Id == currentProcess.Id)
                    {
                        continue;
                    }

                    AllowSetForegroundWindow(
                        unchecked((uint)process.Id));
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllowSetForegroundWindow(uint processId);
    }
}
