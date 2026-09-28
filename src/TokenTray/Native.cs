using System.Runtime.InteropServices;

namespace TokenTray;

internal static partial class Native
{
    public const int WS_CHILD = 0x40000000, WS_POPUP = unchecked((int)0x80000000), WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000;
    public const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20;
    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const uint SWP_NOACTIVATE = 0x10, SWP_NOZORDER = 0x4, SWP_SHOWWINDOW = 0x40, SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2;
    public const int WM_MOUSEMOVE = 0x200, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205, WM_MOUSELEAVE = 0x2A3, WM_NCHITTEST = 0x84,
        WM_DPICHANGED = 0x2E0, WM_DESTROY = 0x2, WM_SETTINGCHANGE = 0x1A, WM_MOUSEACTIVATE = 0x21;
    public const int MA_NOACTIVATE = 3, HTCLIENT = 1;
    public const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;
    public const int ULW_ALPHA = 2;
    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    public const uint TME_LEAVE = 2;

    public delegate IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; public readonly int Width => Right - Left; public readonly int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] public struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
    [StructLayout(LayoutKind.Sequential)] public struct MARGINS { public int Left, Right, Top, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public IntPtr lParam; }

    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(int exStyle, string className, string title, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32")] public static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32")] public static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32")] public static extern IntPtr GetParent(IntPtr hWnd);
    [DllImport("user32")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int idx);
    [DllImport("user32", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int idx, IntPtr v);
    [DllImport("user32")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32")] public static extern int MapWindowPoints(IntPtr from, IntPtr to, ref POINT pt, int count);
    [DllImport("user32")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32")] public static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int flags);
    [DllImport("user32")] public static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int flags);
    [DllImport("user32")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("user32")] public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
    [DllImport("user32")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
    [DllImport("user32")] public static extern IntPtr LoadCursor(IntPtr inst, IntPtr id);
    [DllImport("user32")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32")] public static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);
    [DllImport("user32")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("shell32")] public static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);

    [DllImport("gdi32")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);

    [DllImport("dwmapi")] public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);
    [DllImport("dwmapi")] public static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS m);

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_SYSTEMBACKDROP_TYPE = 38, DWMWA_BORDER_COLOR = 34;
    public const int SPI_GETCLIENTAREAANIMATION = 0x1042;

    public static bool AnimationsEnabled =>
        !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out var on, 0) || on;

    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;
    public static bool SupportsBackdrop => Environment.OSVersion.Version.Build >= 22621;
}
