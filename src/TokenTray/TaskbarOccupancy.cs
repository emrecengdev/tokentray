using System.Windows.Automation;
using static TokenTray.Native;

namespace TokenTray;

/// <summary>
/// Where the taskbar's own buttons are, so the widget can step aside instead of covering them.
/// UI Automation calls into Explorer can be slow, so they run on a dedicated MTA thread and the
/// UI thread only ever reads the last sample.
/// </summary>
internal sealed class TaskbarOccupancy : IDisposable
{
    readonly Thread thread;
    readonly CancellationTokenSource stop = new();
    volatile RECT[] occupied = [];
    public event Action? Changed;

    public TaskbarOccupancy()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "TokenTray taskbar sampling" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    /// <summary>Screen rectangles of taskbar buttons outside the tray area.</summary>
    public RECT[] Occupied => occupied;

    public static bool Overlaps(RECT a, IEnumerable<RECT> others, int margin) =>
        others.Any(b => a.Left - margin < b.Right && b.Left < a.Right + margin && a.Top < b.Bottom && b.Top < a.Bottom);

    void Run()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var next = Sample();
                if (!Same(next, occupied)) { occupied = next; Changed?.Invoke(); }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException or TimeoutException) { }
            stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2));
        }
    }

    static RECT[] Sample()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return [];
        var trayHwnd = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        RECT tray = default;
        if (trayHwnd != IntPtr.Zero) GetWindowRect(trayHwnd, out tray);

        var root = AutomationElement.FromHandle(taskbar);
        var buttons = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        var list = new List<RECT>();
        foreach (AutomationElement b in buttons)
        {
            System.Windows.Rect r;
            try { r = b.Current.BoundingRectangle; } catch (ElementNotAvailableException) { continue; }
            if (r.IsEmpty || r.Width < 4 || r.Height < 4) continue;
            var rect = new RECT { Left = (int)r.Left, Top = (int)r.Top, Right = (int)r.Right, Bottom = (int)r.Bottom };
            // Tray icons, the clock and our own widget live in the tray area; they don't count.
            if (tray.Width > 0 && rect.Left >= tray.Left - 1) continue;
            list.Add(rect);
        }
        return [.. list];
    }

    static bool Same(RECT[] a, RECT[] b) => a.Length == b.Length && a.Zip(b).All(p => p.First.Equals(p.Second));

    public void Dispose() => stop.Cancel();
}
