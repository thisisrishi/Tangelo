using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tangelo.App;

/// Win32 interop used by the app shell.
static partial class Native
{
    // MARK: Low-level hooks

    public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    public const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_MOUSEMOVE = 0x0200;
    public const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;

    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(IntPtr hhk);

    [LibraryImport("user32.dll")]
    public static partial IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr GetModuleHandleW(string? lpModuleName);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vKey);

    public const int VK_LBUTTON = 0x01, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT p);

    // MARK: Windows

    const int GWL_EXSTYLE = -20;
    const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_TOPMOST = 0x00000008;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    /// Keeps a window from taking focus or appearing in Alt-Tab (wheel, HUD, floats).
    public static void MakeNonActivating(Window w, bool toolWindow = true)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        var ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
        if (toolWindow) ex |= WS_EX_TOOLWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)ex);
    }

    /// Hidden from Alt-Tab and the taskbar but still focusable (Floats need keyboard input).
    public static void MakeToolWindow(Window w)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)((long)GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW));
    }

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetTopWindow(IntPtr hWnd);

    public const uint GW_HWNDNEXT = 2;

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// Windows 11 rounded corners + Mica backdrop where available.
    public static void ApplyModernChrome(Window w, bool dark, bool mica = true)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        int useDark = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int));     // DWMWA_USE_IMMERSIVE_DARK_MODE
        int round = 2;
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));       // DWMWA_WINDOW_CORNER_PREFERENCE = round
        if (mica) { int backdrop = 2; DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int)); } // DWMWA_SYSTEMBACKDROP_TYPE = Mica
    }

    /// Converts physical screen pixels (hooks, GetCursorPos) to WPF device-independent units.
    public static Point ToDips(Visual? relativeTo, int x, int y)
    {
        var source = relativeTo != null ? PresentationSource.FromVisual(relativeTo) : null;
        double scale = source?.CompositionTarget?.TransformToDevice.M11 ?? DpiScale;
        return new Point(x / scale, y / scale);
    }

    static double? dpiScale;
    public static double DpiScale
    {
        get
        {
            if (dpiScale is { } d) return d;
            using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
            return (dpiScale = g.DpiX / 96.0).Value;
        }
    }

    public static Point CursorDips()
    {
        GetCursorPos(out var p);
        return ToDips(null, p.X, p.Y);
    }

    // MARK: Shell thumbnails (IShellItemImageFactory)

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr hObject);

    /// Explorer's own thumbnail (or icon) for any file, as a frozen BitmapSource.
    public static BitmapSource? Thumbnail(string path, int size)
    {
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            // SIIGBF_BIGGERSIZEOK (1) | SIIGBF_THUMBNAILONLY fallback handled by retry with icon.
            if (factory.GetImage(new SIZE { cx = size, cy = size }, 0x1, out var hbm) != 0) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                // HBITMAPs from the shell are bottom-up premultiplied BGRA; WPF handles orientation.
                src.Freeze();
                return src;
            }
            finally { DeleteObject(hbm); }
        }
        catch { return null; }
    }

    // MARK: Share sheet

    [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }

    static readonly Guid DataTransferManagerIid = new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    /// Opens the Windows share sheet (Nearby Share, Mail, apps) for files.
    public static void Share(Window owner, IReadOnlyList<string> paths)
    {
        var hwnd = new WindowInteropHelper(owner).EnsureHandle();
        var interop = Windows.ApplicationModel.DataTransfer.DataTransferManager.As<IDataTransferManagerInterop>();
        var iid = DataTransferManagerIid;
        var manager = WinRT.MarshalInterface<Windows.ApplicationModel.DataTransfer.DataTransferManager>.FromAbi(interop.GetForWindow(hwnd, ref iid));
        manager.DataRequested += async (_, e) =>
        {
            var deferral = e.Request.GetDeferral();
            e.Request.Data.Properties.Title = paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} files";
            var items = new List<Windows.Storage.IStorageItem>();
            foreach (var p in paths)
                items.Add(Directory.Exists(p) ? await Windows.Storage.StorageFolder.GetFolderFromPathAsync(p) : await Windows.Storage.StorageFile.GetFileFromPathAsync(p));
            e.Request.Data.SetStorageItems(items);
            deferral.Complete();
        };
        interop.ShowShareUIForWindow(hwnd);
    }
}
