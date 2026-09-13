using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MaxLoud
{
    /// <summary>
    /// Hosts the MaxLoud notification-area icon through Shell_NotifyIcon without WinForms UI.
    /// Mouse notifications are delivered to the WPF window handle and surfaced as managed events.
    /// </summary>
    internal sealed class TrayIconService : IDisposable
    {
        private const uint NimAdd = 0x00000000;
        private const uint NimModify = 0x00000001;
        private const uint NimDelete = 0x00000002;
        private const uint NimSetVersion = 0x00000004;
        private const uint NotifyIconVersion4 = 4;
        private const uint MessageFilterAllow = 1;

        private const uint NifMessage = 0x00000001;
        private const uint NifIcon = 0x00000002;
        private const uint NifTip = 0x00000004;

        private const int WmApp = 0x8000;
        private const int CallbackMessage = WmApp + 0x41;
        private const int WmLeftButtonUp = 0x0202;
        private const int WmLeftButtonDoubleClick = 0x0203;
        private const int WmRightButtonUp = 0x0205;

        private readonly IntPtr _windowHandle;
        private readonly HwndSource _source;
        private readonly uint _taskbarCreatedMessage;
        private IntPtr _iconHandle;
        private string _tooltip = string.Empty;
        private bool _added;
        private bool _disposed;
        private bool _suppressNextLeftButtonUp;

        public event EventHandler LeftClick;
        public event EventHandler LeftDoubleClick;
        public event EventHandler RightClick;

        /// <summary>
        /// Creates a native notification icon owner bound to the supplied WPF window handle.
        /// The window itself may stay hidden in the tray.
        /// </summary>
        public TrayIconService(Window owner)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            _windowHandle = new WindowInteropHelper(owner).EnsureHandle();
            _source = HwndSource.FromHwnd(_windowHandle) ??
                throw new InvalidOperationException("Не удалось получить HwndSource для tray icon.");

            _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
            if (_taskbarCreatedMessage == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (!ChangeWindowMessageFilterEx(
                    _windowHandle,
                    _taskbarCreatedMessage,
                    MessageFilterAllow,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            _source.AddHook(WindowMessageHook);
        }

        /// <summary>
        /// Adds or updates the notification icon and tooltip. The caller owns the Icon lifetime.
        /// </summary>
        public void SetIcon(IntPtr iconHandle, string tooltip)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(TrayIconService));
            }

            if (iconHandle == IntPtr.Zero)
            {
                throw new ArgumentException(
                    "Tray icon handle must not be zero.",
                    nameof(iconHandle));
            }

            _iconHandle = iconHandle;
            _tooltip = NormalizeTooltip(tooltip);

            var data = CreateNotifyIconData(_iconHandle, _tooltip);
            if (_added && ShellNotifyIcon(NimModify, ref data))
            {
                return;
            }

            _added = false;
            data = CreateNotifyIconData(_iconHandle, _tooltip);
            if (!ShellNotifyIcon(NimAdd, ref data))
            {
                throw new InvalidOperationException(
                    "Shell_NotifyIcon не смог добавить значок MaxLoud.");
            }

            _added = true;
            SetNotifyIconVersion();
        }

        /// <summary>
        /// Removes the notification icon and disconnects its WPF window-message hook.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_added)
            {
                var data = CreateNotifyIconData(IntPtr.Zero, string.Empty);
                ShellNotifyIcon(NimDelete, ref data);
                _added = false;
            }

            _source.RemoveHook(WindowMessageHook);
            _iconHandle = IntPtr.Zero;
            _tooltip = string.Empty;
        }

        /// <summary>
        /// Converts native tray mouse messages to WPF-side events.
        /// </summary>
        private IntPtr WindowMessageHook(
            IntPtr hwnd,
            int message,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if ((uint)message == _taskbarCreatedMessage)
            {
                RestoreAfterExplorerRestart();
                return IntPtr.Zero;
            }

            if (message != CallbackMessage)
            {
                return IntPtr.Zero;
            }

            var notificationMessage =
                unchecked((int)(lParam.ToInt64() & 0xFFFF));

            switch (notificationMessage)
            {
                case WmLeftButtonUp:
                    if (_suppressNextLeftButtonUp)
                    {
                        _suppressNextLeftButtonUp = false;
                        handled = true;
                        break;
                    }

                    LeftClick?.Invoke(this, EventArgs.Empty);
                    handled = true;
                    break;

                case WmLeftButtonDoubleClick:
                    // Native tray double-click sequence ends with a second WM_LBUTTONUP.
                    // Suppress that trailing UP so it cannot be interpreted as a new single click.
                    _suppressNextLeftButtonUp = true;
                    LeftDoubleClick?.Invoke(this, EventArgs.Empty);
                    handled = true;
                    break;

                case WmRightButtonUp:
                    RightClick?.Invoke(this, EventArgs.Empty);
                    handled = true;
                    break;
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// Re-registers the current notification icon after Explorer recreates the taskbar.
        /// A failed immediate add is left for the regular status tick to retry through SetIcon.
        /// </summary>
        private void RestoreAfterExplorerRestart()
        {
            if (_disposed || _iconHandle == IntPtr.Zero)
            {
                return;
            }

            _added = false;
            var data = CreateNotifyIconData(_iconHandle, _tooltip);
            if (!ShellNotifyIcon(NimAdd, ref data))
            {
                return;
            }

            _added = true;
            SetNotifyIconVersion();
        }

        /// <summary>
        /// Selects the current shell notification behavior after each successful NIM_ADD.
        /// </summary>
        private void SetNotifyIconVersion()
        {
            var data = CreateNotifyIconData(_iconHandle, _tooltip);
            data.TimeoutOrVersion = NotifyIconVersion4;
            ShellNotifyIcon(NimSetVersion, ref data);
        }

        /// <summary>
        /// Creates one NOTIFYICONDATA payload for add, modify or delete operations.
        /// </summary>
        private NotifyIconData CreateNotifyIconData(IntPtr iconHandle, string tooltip)
        {
            return new NotifyIconData
            {
                Size = (uint)Marshal.SizeOf<NotifyIconData>(),
                WindowHandle = _windowHandle,
                IconId = 1,
                Flags = NifMessage | NifIcon | NifTip,
                CallbackMessage = CallbackMessage,
                IconHandle = iconHandle,
                ToolTip = NormalizeTooltip(tooltip),
                Info = string.Empty,
                InfoTitle = string.Empty
            };
        }

        /// <summary>
        /// Keeps the tray tooltip within the fixed Windows NOTIFYICONDATA field.
        /// </summary>
        private static string NormalizeTooltip(string tooltip)
        {
            var value = tooltip ?? string.Empty;
            return value.Length <= 127
                ? value
                : value.Substring(0, 127);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NotifyIconData
        {
            public uint Size;
            public IntPtr WindowHandle;
            public uint IconId;
            public uint Flags;
            public uint CallbackMessage;
            public IntPtr IconHandle;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string ToolTip;

            public uint State;
            public uint StateMask;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string Info;

            public uint TimeoutOrVersion;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string InfoTitle;

            public uint InfoFlags;
            public Guid ItemGuid;
            public IntPtr BalloonIconHandle;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint RegisterWindowMessage(string message);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ChangeWindowMessageFilterEx(
            IntPtr window,
            uint message,
            uint action,
            IntPtr changeFilterStruct);

        [DllImport(
            "shell32.dll",
            EntryPoint = "Shell_NotifyIconW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShellNotifyIcon(
            uint message,
            ref NotifyIconData data);
    }
}
