using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TokenTray.Core;
using Forms = System.Windows.Forms;

namespace TokenTray;

/// <summary>Notification-area icon: a ring that drains with the tightest allowance, number in the middle.</summary>
internal sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon icon;
    IntPtr hicon;
    string lastKey = "";

    public event Action? Clicked;
    public event Action? RightClicked;

    public TrayIcon()
    {
        icon = new Forms.NotifyIcon { Text = "TokenTray", Visible = true };
        icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) Clicked?.Invoke();
            else if (e.Button == Forms.MouseButtons.Right) RightClicked?.Invoke();
        };
        Update(null, null, null, "TokenTray");
    }

    public void Update(ProviderId? id, Reading? reading, string? problem, string tooltip)
    {
        icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        var dark = Palette.TaskbarDark;
        var key = $"{id}|{reading?.Percent:0}|{reading?.Notch:0.00}|{problem != null}|{dark}|{reading?.Pace.State}";
        if (key == lastKey) return;
        lastKey = key;

        var size = Forms.SystemInformation.SmallIconSize.Width;
        var bmp = Draw(size, new Palette(dark), id, reading, problem != null);
        var old = hicon;
        hicon = ToHicon(bmp);
        icon.Icon = System.Drawing.Icon.FromHandle(hicon);
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    public void Notify(string title, string body) => icon.ShowBalloonTip(8000, title, body, Forms.ToolTipIcon.None);

    static BitmapSource Draw(int px, Palette p, ProviderId? id, Reading? r, bool problem)
    {
        var dv = new DrawingVisual();
        double s = px;
        using (var dc = dv.RenderOpen())
        {
            var stroke = Math.Max(1.6, s * 0.13);
            var c = new Point(s / 2, s / 2);
            var rad = s / 2 - stroke / 2 - 0.25;
            dc.DrawEllipse(null, new Pen(Palette.Brush(p.Track), stroke), c, rad, rad);

            if (r is { } rd && id is { } pid)
            {
                var color = rd.PastReset ? p.TextMuted : p.Fill(pid, rd.Window, rd.Pace);
                var frac = Math.Clamp(rd.Fraction, 0, 1);
                if (frac >= 0.999) dc.DrawEllipse(null, new Pen(Palette.Brush(color), stroke), c, rad, rad);
                else if (frac > 0.01) dc.DrawGeometry(null, new Pen(Palette.Brush(color), stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Arc(c, rad, frac));

                var n = rd.Percent >= 99.5 ? "" : Math.Round(rd.Percent).ToString("0", CultureInfo.InvariantCulture);
                if (n.Length > 0)
                {
                    var tf = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.SemiCondensed);
                    var ft = new FormattedText(n, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, s * (n.Length > 1 ? 0.46 : 0.54), Palette.Brush(p.Text), 1.0);
                    dc.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
                }
                else
                {
                    dc.DrawEllipse(Palette.Brush(color), null, c, s * 0.12, s * 0.12);
                }
            }
            else if (problem)
            {
                dc.DrawEllipse(Palette.Brush(p.Amber), null, c, s * 0.14, s * 0.14);
            }
        }
        var bmp = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        return bmp;
    }

    /// <summary>Clockwise arc from 12 o'clock covering <paramref name="frac"/> of the circle.</summary>
    static Geometry Arc(Point c, double r, double frac)
    {
        var a = frac * 2 * Math.PI;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, frac > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    static IntPtr ToHicon(BitmapSource src)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        var pixels = new byte[w * h * 4];
        src.CopyPixels(pixels, w * 4, 0);
        // Un-premultiply for GDI+'s straight-alpha Format32bppArgb.
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = pixels[i + 3];
            if (a is 0 or 255) continue;
            pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / a);
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / a);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / a);
        }
        using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        bmp.UnlockBits(data);
        return bmp.GetHicon();
    }

    [DllImport("user32")] static extern bool DestroyIcon(IntPtr h);

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        if (hicon != IntPtr.Zero) DestroyIcon(hicon);
    }
}
