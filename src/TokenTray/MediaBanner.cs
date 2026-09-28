using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TokenTray.Core;

namespace TokenTray;

/// <summary>README banner and the pace-notch explainer, drawn in both themes.</summary>
internal static class MediaBanner
{
    const double Scale = 2;
    static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    static readonly FontFamily TextFace = new("Segoe UI Variable Text, Segoe UI");

    static FormattedText Text(string s, FontFamily f, FontWeight w, double size, Color c) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(f, FontStyles.Normal, w, FontStretches.Normal), size, Palette.Brush(c), Scale);

    /// <summary>
    /// 1280×400: the name and what it does on the left; on the right a slice of real taskbar
    /// with the hover card above it, cropped at the edge like a zoomed-in screenshot.
    /// </summary>
    public static BitmapSource Banner(bool dark, IReadOnlyList<WidgetRenderer.Row> rows, BitmapSource hover, string tagline, string tools)
    {
        const double W = 1280, H = 400;
        var p = new Palette(dark);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Palette.Brush(dark ? Color.FromRgb(0x0F, 0x11, 0x15) : Color.FromRgb(0xF4, 0xF3, 0xF1)), null, new Rect(0, 0, W, H));
            // Two soft lights in the tools' own hues, as if the desktop glowed through.
            Glow(dc, new Point(W * 0.78, H * 0.35), 420, p.Hue(ProviderId.Codex), dark ? 0x38 : 0x30);
            Glow(dc, new Point(W * 0.18, H * 1.05), 380, p.Hue(ProviderId.Claude), dark ? 0x2A : 0x26);

            // Left: icon, name, one line of purpose, the tools as marks.
            var icon = AppIcon();
            if (icon != null) dc.DrawImage(icon, new Rect(72, 88, 56, 56));
            var name = Text("TokenTray", Display, FontWeights.SemiBold, 64, p.Text);
            dc.DrawText(name, new Point(142, 72));
            var tag = Text(tagline, TextFace, FontWeights.Normal, 21, p.Text);
            tag.MaxTextWidth = 520;
            dc.DrawText(tag, new Point(74, 172));
            var x = 74.0; var y = 172 + tag.Height + 22;
            foreach (var id in Enum.GetValues<ProviderId>())
            {
                var label = Text(S.ShortName(id), TextFace, FontWeights.Normal, 15, p.TextMuted);
                ProviderMark.Draw(dc, id, new Point(x + 8, y + label.Height / 2), 14, Palette.Brush(p.Hue(id)));
                dc.DrawText(label, new Point(x + 22, y));
                x += 22 + label.Width + 20;
                if (x > 560) { x = 74; y += label.Height + 10; }
            }

            // Right: the taskbar slice, bleeding off the right edge, with the hover card above.
            var widget = WidgetRenderer.Render(rows, new WidgetOptions(WidgetStyle.Rings, true, true, true, true), p, Scale * 1.3, 48, false, out var ww);
            const double zoom = 1.3, barH = 48 * zoom, clockW = 96 * zoom;
            var barTop = H - barH;
            var wx = W - clockW - ww * zoom;
            // The slice starts a little before the widget so its faded edge lands on empty bar, not on text.
            var barLeft = wx - 90;
            dc.DrawRectangle(Palette.Brush(dark ? Color.FromRgb(0x1C, 0x1C, 0x1C) : Color.FromRgb(0xEA, 0xEA, 0xEA)), null, new Rect(barLeft, barTop, W - barLeft, barH));
            dc.DrawRectangle(Palette.Brush(dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x20, 0, 0, 0)), null, new Rect(barLeft, barTop, W - barLeft, 1));
            dc.DrawImage(widget, new Rect(wx, barTop, ww * zoom, barH));
            var clock1 = Text("14:32", TextFace, FontWeights.Normal, 12 * zoom, p.Text);
            var clock2 = Text("29/09/2026", TextFace, FontWeights.Normal, 12 * zoom, p.Text);
            dc.DrawText(clock1, new Point(W - 16 - clock1.Width, barTop + 8));
            dc.DrawText(clock2, new Point(W - 16 - clock2.Width, barTop + 8 + clock1.Height));
            // Soft left edge so the slice reads as a crop, not a box.
            dc.DrawRectangle(new LinearGradientBrush(
                dark ? Color.FromRgb(0x0F, 0x11, 0x15) : Color.FromRgb(0xF4, 0xF3, 0xF1),
                dark ? Color.FromArgb(0, 0x0F, 0x11, 0x15) : Color.FromArgb(0, 0xF4, 0xF3, 0xF1), 0), null, new Rect(barLeft - 1, barTop, 90, barH));

            var cw = hover.PixelWidth / Scale; var ch = hover.PixelHeight / Scale;
            var cardScale = Math.Min(1.0, (barTop - 20) / ch);
            dc.DrawImage(hover, new Rect(W - clockW - cw * cardScale - 10, barTop - ch * cardScale + 14, cw * cardScale, ch * cardScale));
        }
        return Bake(dv, W, H);
    }

    /// <summary>Three bars that teach the notch: under pace, on pace, running hot.</summary>
    public static BitmapSource PaceGuide(bool dark, (string title, string body)[] rows, string caption)
    {
        const double W = 900, rowH = 76, barW = 380, barH = 10;
        var H = 40 + rowH * rows.Length + 56;
        var p = new Palette(dark);
        var fills = new[] { (0.30, 0.62, p.Hue(ProviderId.Claude)), (0.58, 0.60, p.Hue(ProviderId.Codex)), (0.82, 0.44, p.Amber) };
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRoundedRectangle(Palette.Brush(dark ? Color.FromRgb(0x16, 0x17, 0x1A) : Color.FromRgb(0xFA, 0xF9, 0xF7)),
                new Pen(Palette.Brush(p.Hairline), 1), new Rect(0.5, 0.5, W - 1, H - 1), 14, 14);
            for (var i = 0; i < rows.Length; i++)
            {
                var y = 36 + rowH * i;
                var t = Text(rows[i].title, TextFace, FontWeights.SemiBold, 17, p.Text);
                var b = Text(rows[i].body, TextFace, FontWeights.Normal, 14, p.TextMuted);
                b.MaxTextWidth = 380;
                dc.DrawText(t, new Point(40, y));
                dc.DrawText(b, new Point(40, y + t.Height + 2));

                var (fill, notch, color) = fills[i];
                var bx = W - 40 - barW; var by = y + 16;
                dc.DrawRoundedRectangle(Palette.Brush(p.Track), null, new Rect(bx, by, barW, barH), barH / 2, barH / 2);
                dc.DrawRoundedRectangle(Palette.Brush(color), null, new Rect(bx, by, barW * fill, barH), barH / 2, barH / 2);
                dc.DrawRoundedRectangle(Palette.Brush(p.Tick), null, new Rect(bx + barW * notch - 1.5, by - 6, 3, barH + 12), 1.5, 1.5);
            }
            var c = Text(caption, TextFace, FontWeights.Normal, 14, p.TextMuted);
            c.MaxTextWidth = W - 80;
            dc.DrawText(c, new Point(40, H - 40 - c.Height / 2 + 6));
        }
        return Bake(dv, W, H);
    }

    static void Glow(DrawingContext dc, Point c, double r, Color hue, int alpha)
    {
        var brush = new RadialGradientBrush(Color.FromArgb((byte)alpha, hue.R, hue.G, hue.B), Color.FromArgb(0, hue.R, hue.G, hue.B));
        dc.DrawEllipse(brush, null, c, r, r * 0.8);
    }

    static BitmapSource? AppIcon()
    {
        try
        {
            var decoder = new IconBitmapDecoder(new Uri("pack://application:,,,/Assets/TokenTray.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or System.Runtime.InteropServices.COMException) { return null; }
    }

    static BitmapSource Bake(Visual v, double w, double h)
    {
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(w * Scale), (int)Math.Ceiling(h * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bmp.Render(v);
        bmp.Freeze();
        return bmp;
    }
}

