using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TokenTray.Core;

namespace TokenTray;

/// <summary>
/// `TokenTray.exe --render-media <dir>`: draws README images and GIF frames with the app's own
/// renderers and made-up sample data. No network, no real accounts, nothing personal.
/// </summary>
internal static class MediaRenderer
{
    const double Scale = 2;

    /// <summary>Sample readings, relative to now. The numbers are invented for screenshots.</summary>
    public static List<ProviderSnapshot> Demo(DateTimeOffset now, double sessionUsed = 42)
    {
        var h5 = TimeSpan.FromHours(5); var d7 = TimeSpan.FromDays(7);
        // Resets land on whole hours, like the real services, so screenshots read naturally.
        DateTimeOffset At(double hours)
        {
            var t = now.AddHours(hours);
            return new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Offset).AddHours(t.Minute >= 30 ? 1 : 0);
        }
        return
        [
            new(ProviderId.Claude, SnapshotStatus.Ok,
            [
                new(WindowKind.Session, "5-hour", sessionUsed, At(2.17), h5),
                new(WindowKind.Weekly, "Weekly", 71, At(53), d7),
                new(WindowKind.WeeklyModel, "Weekly · Opus", 18, At(53), d7),
            ], now, "Max 20x", Email: "you@example.com"),
            new(ProviderId.Codex, SnapshotStatus.Ok,
            [
                new(WindowKind.Session, "5-hour", 64, At(1.58), h5),
                new(WindowKind.Weekly, "Weekly", 38, At(99), d7),
            ], now, "Plus", Email: "you@example.com"),
            new(ProviderId.Antigravity, SnapshotStatus.Ok,
            [
                new(WindowKind.Session, "5-hour", 12, At(3.67), h5),
                new(WindowKind.Weekly, "Weekly", 55, At(74), d7),
                new(WindowKind.Session, "5-hour", 5, At(4.33), h5, "Claude and GPT"),
                new(WindowKind.Weekly, "Weekly", 20, At(120), d7, "Claude and GPT"),
            ], now, "Google AI Pro", Email: "you@example.com"),
            new(ProviderId.Cursor, SnapshotStatus.Ok,
            [
                new(WindowKind.Period, "This month", 83, At(216), null),
                new(WindowKind.Period, "This month", 12, At(216), null, "API"),
            ], now, "Pro"),
        ];
    }

    public static void Run(string dir, Func<IReadOnlyList<ProviderSnapshot>, WidgetOptions, IReadOnlyList<WidgetRenderer.Row>> rowsOf,
        Func<Func<IReadOnlyList<ProviderSnapshot>>, Settings, FlyoutWindow> makeFlyout)
    {
        Directory.CreateDirectory(dir);
        var frames = Path.Combine(dir, "frames");
        if (Directory.Exists(frames)) Directory.Delete(frames, true);
        Directory.CreateDirectory(frames);
        var now = DateTimeOffset.Now;
        var three = Demo(now).Take(3).ToList();
        var rings = new WidgetOptions(WidgetStyle.Rings, true, true, true, true);

        // Taskbar strips
        foreach (var dark in new[] { true, false })
            Save(Strip(rowsOf(three, rings), rings, dark, 0), Path.Combine(dir, $"widget-{(dark ? "dark" : "light")}.png"));

        // Every style, both variants
        var sheet = new List<(string label, BitmapSource strip)>();
        foreach (var style in Enum.GetValues<WidgetStyle>())
            foreach (var name in new[] { true, false })
            {
                var o = rings with { Style = style, Name = name };
                sheet.Add(($"{S.StyleName(style)} · {(name ? S.VariantName : S.VariantIcon)}", Strip(rowsOf(three, o), o, true, 560)));
            }
        Save(Sheet(sheet), Path.Combine(dir, "styles.png"));

        // Flyout, light and dark, with four tools in two columns
        var settings = new Settings { ShowRemaining = false, ShowEmail = true };
        foreach (var dark in new[] { true, false })
        {
            Palette.UiOverride = dark;
            var flyout = makeFlyout(() => Demo(now), settings);
            Save(flyout.RenderForMedia(Scale), Path.Combine(dir, $"panel-{(dark ? "dark" : "light")}.png"));
            flyout.Close();
        }

        // Provider marks for tables, in each theme's hue.
        foreach (var dark in new[] { true, false })
            foreach (var id in Enum.GetValues<ProviderId>())
            {
                var mv = new DrawingVisual();
                using (var dc = mv.RenderOpen())
                    ProviderMark.Draw(dc, id, new Point(10, 10), 16, Palette.Brush(new Palette(dark).Hue(id)));
                var bmp = new RenderTargetBitmap(40, 40, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
                bmp.Render(mv);
                Save(bmp, Path.Combine(dir, $"mark-{id.ToString().ToLowerInvariant()}-{(dark ? "dark" : "light")}.png"));
            }

        // Hover card, both themes; then the banner and the pace guide built around it.
        foreach (var dark in new[] { true, false })
        {
            Palette.UiOverride = dark;
            var card = new HoverCard();
            var hover = card.RenderForMedia(three, false, Scale);
            card.Close();
            var theme = dark ? "dark" : "light";
            Save(hover, Path.Combine(dir, $"hover-{theme}.png"));
            Save(MediaBanner.Banner(dark, rowsOf(three, rings), hover, S.BannerTagline, ""), Path.Combine(dir, $"banner-{theme}.png"));
            Save(MediaBanner.PaceGuide(dark, S.PaceGuideRows, S.PaceGuideCaption), Path.Combine(dir, $"pace-{theme}.png"));
        }

        // GIF frames: a Claude session filling up, running out, refilling; then a tour of the styles.
        var n = 0;
        IEnumerable<(List<ProviderSnapshot> snaps, WidgetOptions o)> Script()
        {
            for (var i = 0; i <= 44; i++) yield return (Demo(now, 30 + i * 70.0 / 44).Take(3).ToList(), rings);
            for (var i = 0; i < 14; i++) yield return (Demo(now, 100).Take(3).ToList(), rings);
            for (var i = 0; i < 10; i++) yield return (Demo(now, 0).Take(3).ToList(), rings);
            foreach (var style in new[] { WidgetStyle.Dots, WidgetStyle.Cells, WidgetStyle.Bars, WidgetStyle.Minimal, WidgetStyle.Rings })
                for (var i = 0; i < 16; i++) yield return (Demo(now, 30).Take(3).ToList(), rings with { Style = style });
        }
        var script = Script().ToList();
        // Every frame the same size, so the GIF doesn't jump as styles change width.
        var widest = script.Max(f => { WidgetRenderer.Render(rowsOf(f.snaps, f.o), f.o, new Palette(true), 1, 48, false, out var w); return w; });
        foreach (var (snaps, o) in script)
            Save(Strip(rowsOf(snaps, o), o, true, widest + 92 + 36), Path.Combine(frames, $"f{n++:D4}.png"));
    }

    /// <summary>A slice of taskbar: the widget, then a tray area with a clock.</summary>
    static BitmapSource Strip(IReadOnlyList<WidgetRenderer.Row> rows, WidgetOptions o, bool dark, double minWidth)
    {
        var p = new Palette(dark);
        var widget = WidgetRenderer.Render(rows, o, p, Scale, 48, false, out var w);
        const double clock = 92;
        var total = Math.Max(minWidth, w + clock + 24);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = dark ? Color.FromRgb(0x1C, 0x1C, 0x1C) : Color.FromRgb(0xEE, 0xEE, 0xEE);
            dc.DrawRectangle(Palette.Brush(bg), null, new Rect(0, 0, total, 48));
            dc.DrawRectangle(Palette.Brush(dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x20, 0, 0, 0)), null, new Rect(0, 0, total, 1));
            dc.DrawImage(widget, new Rect(total - clock - 12 - w, 0, w, 48));
            var tf = new Typeface("Segoe UI Variable Text, Segoe UI");
            var t1 = new FormattedText("14:32", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 12, Palette.Brush(p.Text), Scale);
            var t2 = new FormattedText("29/09/2026", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 12, Palette.Brush(p.Text), Scale);
            dc.DrawText(t1, new Point(total - 16 - t1.Width, 7));
            dc.DrawText(t2, new Point(total - 16 - t2.Width, 25));
        }
        return Bake(dv, total, 48);
    }

    static BitmapSource Sheet(List<(string label, BitmapSource strip)> rows)
    {
        var width = rows.Max(r => r.strip.Width) + 200;
        const double rowH = 64;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Palette.Brush(Color.FromRgb(0x12, 0x12, 0x12)), null, new Rect(0, 0, width, rowH * rows.Count + 16));
            var tf = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            for (var i = 0; i < rows.Count; i++)
            {
                var y = 8 + rowH * i;
                var label = new FormattedText(rows[i].label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 13, Brushes.Gainsboro, Scale);
                dc.DrawText(label, new Point(20, y + 24 - label.Height / 2 + 4));
                dc.DrawImage(rows[i].strip, new Rect(width - rows[i].strip.Width - 8, y + 4, rows[i].strip.Width, 48));
            }
        }
        return Bake(dv, width, rowH * rows.Count + 16);
    }

    static BitmapSource Bake(Visual v, double w, double h)
    {
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(w * Scale), (int)Math.Ceiling(h * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bmp.Render(v);
        bmp.Freeze();
        return bmp;
    }

    public static void Save(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>Renders a laid-out element at media scale.</summary>
    public static BitmapSource Capture(FrameworkElement e, double scale)
    {
        e.UpdateLayout();
        var w = e.ActualWidth; var h = e.ActualHeight;
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(e), null, new Rect(0, 0, w, h));
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }
}
