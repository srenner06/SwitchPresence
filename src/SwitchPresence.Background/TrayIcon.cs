using System.Runtime.InteropServices;
using SwitchPresence.Logging;

namespace SwitchPresence.Background;

/// <summary>
/// A tray icon built on raw Win32: WinForms NotifyIcon is not NativeAOT-friendly, and this
/// needs about as much code as wiring one up would.
///
/// The window is message-only - it exists purely to receive the tray callback and WM_COMMAND.
/// Everything that touches the icon runs on the thread pumping messages, which is why
/// UpdateStatus posts rather than calling Shell_NotifyIcon directly.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int WmDestroy = 0x0002;
    private const int WmCommand = 0x0111;
    private const int WmRightButtonUp = 0x0205;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmTrayCallback = 0x8000 + 1;   // WM_APP + 1
    private const int WmUpdateStatus = 0x8000 + 2;

    private const int NimAdd = 0x0;
    private const int NimModify = 0x1;
    private const int NimDelete = 0x2;
    private const int NifMessage = 0x1;
    private const int NifIcon = 0x2;
    private const int NifTip = 0x4;

    private const int MfString = 0x0000;
    private const int MfGrayed = 0x0001;
    private const int MfSeparator = 0x0800;
    private const int TpmRightButton = 0x0002;
    private const int TpmReturnCmd = 0x0100;

    private const int IdStatus = 1;
    private const int IdSetup = 2;
    private const int IdChangeAccount = 3;
    private const int IdQuit = 4;

    private const int IdiApplication = 32512;

    public event Action? QuitRequested;
    public event Action? SetupRequested;
    public event Action? ChangeAccountRequested;

    // The delegate must outlive the window; letting it be collected crashes the pump.
    private readonly WndProcDelegate _wndProc;
    private readonly string _title;

    private nint _hwnd;
    private nint _icon;
    private volatile string _status = "Starting...";

    public TrayIcon(string title)
    {
        _title = title;
        _wndProc = WindowProc;
    }

    /// <summary>Creates the window and icon, then pumps messages until Quit. Blocks.</summary>
    public void Run()
    {
        if (!TryCreateWindow())
            return;

        _icon = LoadAppIcon();
        Notify(NimAdd);

        while (GetMessageW(out var message, 0, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }

    /// <summary>Safe to call from the polling thread; the update happens on the pump thread.</summary>
    public void UpdateStatus(string? game)
    {
        _status = game is { Length: > 0 } ? game : "Nothing playing";

        if (_hwnd != 0)
            PostMessageW(_hwnd, WmUpdateStatus, 0, 0);
    }

    public void Stop()
    {
        if (_hwnd != 0)
            PostMessageW(_hwnd, WmDestroy, 0, 0);
    }

    public void Dispose()
    {
        if (_hwnd == 0)
            return;

        Notify(NimDelete);
        DestroyWindow(_hwnd);
        _hwnd = 0;
    }

    private bool TryCreateWindow()
    {
        try
        {
            var className = $"SwitchPresenceTray_{Environment.ProcessId}";
            var windowClass = new WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = GetModuleHandleW(null),
                lpszClassName = className,
            };

            if (RegisterClassExW(ref windowClass) == 0)
            {
                Log.Warn($"tray: RegisterClassEx failed ({Marshal.GetLastWin32Error()})");
                return false;
            }

            // HWND_MESSAGE (-3): no taskbar entry, no painting, just a message sink.
            _hwnd = CreateWindowExW(0, className, _title, 0, 0, 0, 0, 0, -3, 0, windowClass.hInstance, 0);

            if (_hwnd == 0)
            {
                Log.Warn($"tray: CreateWindowEx failed ({Marshal.GetLastWin32Error()})");
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Error($"tray: unavailable ({ex.Message})");
            return false;
        }
    }

    private nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmUpdateStatus:
                Notify(NimModify);
                return 0;

            case WmTrayCallback when lParam is WmRightButtonUp or WmLeftButtonUp:
                ShowMenu();
                return 0;

            case WmDestroy:
                Notify(NimDelete);
                PostQuitMessage(0);
                return 0;

            case WmCommand:
                OnMenuCommand((int)(wParam & 0xFFFF));
                return 0;

            default:
                return DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    private void OnMenuCommand(int id)
    {
        switch (id)
        {
            case IdSetup:
                SetupRequested?.Invoke();
                break;
            case IdChangeAccount:
                ChangeAccountRequested?.Invoke();
                break;
            case IdQuit:
                QuitRequested?.Invoke();
                break;
        }
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0)
            return;

        try
        {
            AppendMenuW(menu, MfString | MfGrayed, IdStatus, _status);
            AppendMenuW(menu, MfSeparator, 0, null);
            AppendMenuW(menu, MfString, IdChangeAccount, "Change account...");
            AppendMenuW(menu, MfString, IdSetup, "Open setup...");
            AppendMenuW(menu, MfSeparator, 0, null);
            AppendMenuW(menu, MfString, IdQuit, "Quit");

            GetCursorPos(out var point);

            // Required, or the menu refuses to close when the user clicks elsewhere.
            SetForegroundWindow(_hwnd);

            var choice = TrackPopupMenu(menu, TpmRightButton | TpmReturnCmd, point.X, point.Y, 0, _hwnd, 0);
            if (choice != 0)
                OnMenuCommand(choice);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void Notify(int action)
    {
        var data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmTrayCallback,
            hIcon = _icon,
            szTip = Clip($"switch-presence - {_status}", 127),
        };

        if (!Shell_NotifyIconW(action, ref data) && action != NimDelete)
            Log.Warn($"tray: Shell_NotifyIcon({action}) failed");
    }

    /// <summary>The exe's own icon if it has one, otherwise the default application icon.</summary>
    private static nint LoadAppIcon()
    {
        if (Environment.ProcessPath is { } path)
        {
            var extracted = ExtractIconW(GetModuleHandleW(null), path, 0);
            if (extracted != 0 && extracted != 1)
                return extracted;
        }

        return LoadIconW(0, IdiApplication);
    }

    private static string Clip(string value, int max)
    {
        return value.Length <= max ? value : value[..max];
    }

    // ---- Win32 ----

    private delegate nint WndProcDelegate(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public int uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public Point pt;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessageW(out Msg message, nint hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DispatchMessageW(ref Msg message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(nint menu, uint flags, nint id, string? item);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved,
        nint hwnd, nint rect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadIconW(nint instance, nint iconName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint ExtractIconW(nint instance, string exeFileName, uint iconIndex);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(int message, ref NotifyIconData data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);
}
