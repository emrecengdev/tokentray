using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static TokenTray.Native;

namespace TokenTray;

/// <summary>
/// A layered child window inside the primary taskbar, just left of the tray icons.
/// Explorer owns the parent, so if it restarts our window goes with it and is recreated.
/// </summary>
internal sealed class TaskbarWidget : IDisposable
{
    const string ClassName = "TokenTrayTaskbarWidget";
    static WndProc? procKeepAlive;
    static bool registered;

    IntPtr hwnd;
    bool hover, visible = true, embedded;
    readonly DispatcherTimer watch;
    Func<IReadOnlyList<WidgetRenderer.Row>>? source;
    RECT lastTaskbar, lastTray;
    int lastWidthPx;

    public Func<int> Offset { get; set; } = () => 0;
    /// <summary>Screen rectangles the widget must not cover (taskbar buttons).</summary>
    public Func<RECT[]>? Occupied { get; set; }
    /// <summary>True while taskbar buttons leave no room for the widget.</summary>
    public bool Crowded { get; private set; }
    public event Action<bool>? HoverChanged;
    public Func<WidgetOptions> Options { get; set; } = () => new(WidgetStyle.Rings, true, true, true, true);
    public event Action? Clicked;
    public event Action? RightClicked;

    /// <summary>Screen rectangle of the widget, for anchoring the flyout.</summary>
    public RECT? ScreenBounds => hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var r) && visible ? r : null;

    public TaskbarWidget()
    {
        watch = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Track(), Dispatcher.CurrentDispatcher);
    }

    public void Show(Func<IReadOnlyList<WidgetRenderer.Row>> rows)
    {
        source = rows;
        visible = true;
        EnsureWindow();
        Redraw();
        watch.Start();
    }

    public void Hide()
    {
        visible = false;
        watch.Stop();
        if (hwnd != IntPtr.Zero) ShowWindow(hwnd, SW_HIDE);
    }

    void EnsureWindow()
    {
        if (hwnd != IntPtr.Zero && IsWindow(hwnd)) return;
        hwnd = IntPtr.Zero; embedded = false;

        if (!registered)
        {
            procKeepAlive = Proc;
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(procKeepAlive),
                hInstance = GetModuleHandle(null),
                hCursor = LoadCursor(IntPtr.Zero, 32512),
                lpszClassName = ClassName,
            };
            if (RegisterClassEx(ref wc) == 0) Trace($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
            registered = true;
        }
        hwnd = CreateWindowEx(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, ClassName, "TokenTray", WS_POPUP,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero) Trace($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
    }

    static void Trace(string message) => App.Log(new InvalidOperationException(message));

    static (IntPtr taskbar, IntPtr tray) FindTaskbar()
    {
        var tb = FindWindow("Shell_TrayWnd", null);
        return (tb, tb == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(tb, IntPtr.Zero, "TrayNotifyWnd", null));
    }

    void Embed(IntPtr taskbar)
    {
        var style = (long)GetWindowLongPtr(hwnd, GWL_STYLE);
        SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)((style & ~(long)unchecked((uint)WS_POPUP)) | WS_CHILD | WS_CLIPSIBLINGS));
        embedded = SetParent(hwnd, taskbar) != IntPtr.Zero || GetParent(hwnd) == taskbar;
        if (embedded)
        {
            // Windows 11 can leave a freshly reparented layered window under the taskbar's
            // composition visual; toggling the layered bit rebinds it.
            var ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(ex & ~WS_EX_LAYERED));
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)ex);
        }
        else
        {
            // Could not join the taskbar: float above it instead.
            SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)(style | WS_POPUP));
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)((long)GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_TOPMOST));
        }
    }

    void Track()
    {
        if (!visible) return;
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) { EnsureWindow(); Redraw(); return; }
        var (tb, tray) = FindTaskbar();
        if (tb == IntPtr.Zero) return;
        if (embedded && GetParent(hwnd) != tb) { DestroyWindow(hwnd); hwnd = IntPtr.Zero; EnsureWindow(); Redraw(); return; }
        GetWindowRect(tb, out var tbr);
        var trr = default(RECT);
        if (tray != IntPtr.Zero) GetWindowRect(tray, out trr);
        if (!tbr.Equals(lastTaskbar) || !trr.Equals(lastTray)) Redraw();
    }

    /// <summary>Re-render with the latest data. Cheap enough to call every second.</summary>
    public void Redraw()
    {
        if (!visible || source is null) return;
        EnsureWindow();
        var (tb, tray) = FindTaskbar();
        if (tb == IntPtr.Zero) { ShowWindow(hwnd, SW_HIDE); return; }
        if (!embedded || GetParent(hwnd) != tb) Embed(tb);

        GetWindowRect(tb, out var tbr);
        RECT trr = default;
        if (tray != IntPtr.Zero) GetWindowRect(tray, out trr);
        lastTaskbar = tbr; lastTray = trr;

        // Only horizontal taskbars have room for the widget; the tray icon still works otherwise.
        if (tbr.Width < tbr.Height * 2) { ShowWindow(hwnd, SW_HIDE); return; }

        var scale = Math.Max(1, GetDpiForWindow(tb)) / 96.0;
        var heightDip = tbr.Height / scale;
        var rows = source();
        if (rows.Count == 0) { ShowWindow(hwnd, SW_HIDE); return; }

        var right = tray != IntPtr.Zero ? trr.Left : tbr.Right - (int)(200 * scale);
        var palette = new Palette(Palette.TaskbarDark);

        // Taskbar buttons come first: if the chosen look would cover them, fall back to a compact
        // one, and if even that doesn't fit, step aside entirely (the tray icon stays).
        var user = Options();
        var candidates = new[] { user, user with { Style = WidgetStyle.Minimal, Name = false, Time = false } };
        System.Windows.Media.Imaging.BitmapSource? bmp = null;
        var x = 0;
        foreach (var option in candidates)
        {
            var candidate = WidgetRenderer.Render(rows, option, palette, scale, heightDip, hover, out _);
            var cx = right - candidate.PixelWidth - (int)((6 + Offset()) * scale);
            var rect = new RECT { Left = cx, Top = tbr.Top, Right = cx + candidate.PixelWidth, Bottom = tbr.Bottom };
            if (Occupied is not { } occ || !TaskbarOccupancy.Overlaps(rect, occ(), (int)(4 * scale))) { bmp = candidate; x = cx; break; }
        }
        if (bmp is null) { Crowded = true; ShowWindow(hwnd, SW_HIDE); return; }
        Crowded = false;
        var y = tbr.Top;

        var pos = new POINT { X = x, Y = y };
        if (embedded) MapWindowPoints(IntPtr.Zero, tb, ref pos, 1);
        SetWindowPos(hwnd, embedded ? IntPtr.Zero : new IntPtr(-1), pos.X, pos.Y, bmp.PixelWidth, bmp.PixelHeight,
            SWP_NOACTIVATE | SWP_SHOWWINDOW | (embedded ? SWP_NOZORDER : 0));
        Present(bmp);
        lastWidthPx = bmp.PixelWidth;
    }

    void Present(BitmapSource bmp)
    {
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var dib = CreateDIBSection(mem, ref bi, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            if (dib == IntPtr.Zero) return;
            bmp.CopyPixels(System.Windows.Int32Rect.Empty, bits, w * h * 4, w * 4);
            var old = SelectObject(mem, dib);
            var size = new SIZE { cx = w, cy = h };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
            UpdateLayeredWindow(hwnd, screen, IntPtr.Zero, ref size, mem, ref src, 0, ref blend, ULW_ALPHA);
            SelectObject(mem, old);
        }
        finally
        {
            if (dib != IntPtr.Zero) DeleteObject(dib);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    IntPtr Proc(IntPtr h, int msg, IntPtr wp, IntPtr lp)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE: return MA_NOACTIVATE;
            case WM_NCHITTEST: return HTCLIENT;
            case WM_MOUSEMOVE:
                if (!hover)
                {
                    hover = true;
                    var tme = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TME_LEAVE, hwndTrack = h };
                    TrackMouseEvent(ref tme);
                    Redraw();
                    HoverChanged?.Invoke(true);
                }
                return IntPtr.Zero;
            case WM_MOUSELEAVE:
                hover = false; Redraw(); HoverChanged?.Invoke(false); return IntPtr.Zero;
            case WM_LBUTTONUP: HoverChanged?.Invoke(false); Clicked?.Invoke(); return IntPtr.Zero;
            case WM_RBUTTONUP: RightClicked?.Invoke(); return IntPtr.Zero;
        }
        return DefWindowProc(h, msg, wp, lp);
    }

    public void Dispose()
    {
        watch.Stop();
        if (hwnd != IntPtr.Zero && IsWindow(hwnd)) DestroyWindow(hwnd);
        hwnd = IntPtr.Zero;
    }
}
