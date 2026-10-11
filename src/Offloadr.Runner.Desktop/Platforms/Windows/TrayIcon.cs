using System.Runtime.InteropServices;

namespace Offloadr.Runner.Desktop;

/// <summary>A notification-area menu item. A null <see cref="Text"/> is a separator.</summary>
internal sealed record TrayMenuItem(string? Text, Action? Invoke = null, bool Enabled = true, bool IsDefault = false)
{
    public static readonly TrayMenuItem Separator = new(Text: null);
}

/// <summary>
/// Notification-area icon on top of Shell_NotifyIcon. Messages arrive through a subclass of the
/// app window, which keeps receiving them while the window is hidden. Use from the UI thread.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = WmApp + 1;
    private const uint WmApp = 0x8000;
    private const uint WmNull = 0x0000;
    private const uint WmContextMenu = 0x007B;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NimSetFocus = 3;
    private const uint NimSetVersion = 4;
    private const uint NifMessage = 0x01;
    private const uint NifIcon = 0x02;
    private const uint NifTip = 0x04;
    private const uint NifInfo = 0x10;
    private const uint NifShowTip = 0x80;
    private const uint NotifyIconVersion4 = 4;
    private const uint NiifInfo = 0x01;
    private const uint NiifNoSound = 0x10;
    private const uint MfString = 0x0000;
    private const uint MfGrayed = 0x0001;
    private const uint MfSeparator = 0x0800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmNoNotify = 0x0080;
    private const uint TpmReturnCmd = 0x0100;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x10;
    private const int SmCxSmIcon = 49;
    private const int SmCySmIcon = 50;
    private static readonly UIntPtr SubclassId = 0x0FF1;

    private readonly IntPtr _window;
    private readonly IntPtr _icon;
    private readonly uint _taskbarCreated;
    private readonly Action _open;
    private readonly Func<IReadOnlyList<TrayMenuItem>> _menu;
    private readonly SubclassProc _subclass;
    private string _tooltip;
    private bool _disposed;
    private bool _subclassed;

    public TrayIcon(IntPtr window, string iconPath, string tooltip, Action open, Func<IReadOnlyList<TrayMenuItem>> menu)
    {
        _window = window;
        _tooltip = tooltip;
        _open = open;
        _menu = menu;
        _icon = LoadImageW(IntPtr.Zero, iconPath, ImageIcon, GetSystemMetrics(SmCxSmIcon), GetSystemMetrics(SmCySmIcon), LrLoadFromFile);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _subclass = WindowProc;

        // Without the subclass, clicks on the icon would never arrive: the icon stays away, so the
        // window is minimized rather than hidden into an icon that cannot open it again.
        _subclassed = SetWindowSubclass(_window, _subclass, SubclassId, UIntPtr.Zero);
        if (_subclassed)
        {
            Add();
        }
    }

    /// <summary>
    /// False while the notification area did not take the icon (Explorer not ready yet at sign-in,
    /// say); it is added again when the taskbar comes back. Nothing should hide into it meanwhile.
    /// </summary>
    public bool IsVisible { get; private set; }

    public void SetTooltip(string tooltip)
    {
        if (_disposed || tooltip == _tooltip)
        {
            return;
        }

        _tooltip = tooltip;
        var data = Data(NifTip | NifShowTip);
        Shell_NotifyIconW(NimModify, ref data);
    }

    public void ShowBalloon(string title, string text)
    {
        if (_disposed)
        {
            return;
        }

        var data = Data(NifInfo);
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(text, 255);
        data.dwInfoFlags = NiifInfo | NiifNoSound;
        Shell_NotifyIconW(NimModify, ref data);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var data = Data(0);
        Shell_NotifyIconW(NimDelete, ref data);
        if (_subclassed)
        {
            RemoveWindowSubclass(_window, _subclass, SubclassId);
        }
        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
        }
    }

    private void Add()
    {
        var data = Data(NifMessage | NifIcon | NifTip | NifShowTip);
        IsVisible = Shell_NotifyIconW(NimAdd, ref data);
        if (IsVisible)
        {
            data.uTimeoutOrVersion = NotifyIconVersion4;
            Shell_NotifyIconW(NimSetVersion, ref data);
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr refData)
    {
        if (message == CallbackMessage)
        {
            // NOTIFYICON_VERSION_4: the event is in the low word of lParam, the anchor point in wParam.
            var notification = (uint)(lParam.ToInt64() & 0xFFFF);
            switch (notification)
            {
                case NinSelect or NinKeySelect or WmLButtonDblClk:
                    _open();
                    break;
                case WmContextMenu:
                    ShowMenu(unchecked((short)(wParam.ToInt64() & 0xFFFF)), unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF)));
                    break;
            }

            return IntPtr.Zero;
        }

        if (message == _taskbarCreated && !_disposed)
        {
            // Explorer restarted and forgot every icon.
            Add();
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        var items = _menu();
        var menu = CreatePopupMenu();
        try
        {
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (item.Text is null)
                {
                    AppendMenuW(menu, MfSeparator, UIntPtr.Zero, null);
                    continue;
                }

                AppendMenuW(menu, MfString | (item.Enabled ? 0 : MfGrayed), (UIntPtr)(uint)(index + 1), item.Text);
                if (item.IsDefault)
                {
                    SetMenuDefaultItem(menu, (uint)(index + 1), 0);
                }
            }

            // Without this the menu does not close when the person clicks elsewhere.
            SetForegroundWindow(_window);
            var chosen = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd | TpmNoNotify, x, y, _window, IntPtr.Zero);
            PostMessageW(_window, WmNull, IntPtr.Zero, IntPtr.Zero);

            // Focus goes back to the notification area (keyboard users, Escape); an item that opens
            // the window takes it from there.
            var focus = Data(0);
            Shell_NotifyIconW(NimSetFocus, ref focus);
            if (chosen > 0 && chosen <= items.Count && items[chosen - 1] is { Enabled: true, Invoke: { } invoke })
            {
                invoke();
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private NotifyIconData Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = Truncate(_tooltip, 127),
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr refData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, UIntPtr refData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr instance, string name, uint type, int width, int height, uint load);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
