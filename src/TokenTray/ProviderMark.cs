using System.Windows;
using System.Windows.Media;
using TokenTray.Core;

namespace TokenTray;

/// <summary>
/// Small geometric marks, one per provider. They hint at each tool without copying
/// its logo: a spark, a prompt, a four-point star, a slashed ring, a pointer, brackets.
/// </summary>
internal static class ProviderMark
{
    public static void Draw(DrawingContext dc, ProviderId id, Point c, double d, Brush hue)
    {
        var round = new Pen(hue, d * 0.15) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        switch (id)
        {
            case ProviderId.Claude:
                for (var i = 0; i < 4; i++)
                {
                    var a = i * Math.PI / 4;
                    var dx = Math.Cos(a) * d * 0.42; var dy = Math.Sin(a) * d * 0.42;
                    dc.DrawLine(round, new Point(c.X - dx, c.Y - dy), new Point(c.X + dx, c.Y + dy));
                }
                break;

            case ProviderId.Codex:
            {
                var s = d * 0.4;
                dc.DrawGeometry(null, round, Poly(false, new(c.X - s, c.Y - s * 0.75), new(c.X - s * 0.1, c.Y), new(c.X - s, c.Y + s * 0.75)));
                dc.DrawLine(round, new Point(c.X + s * 0.25, c.Y + s * 0.75), new Point(c.X + s, c.Y + s * 0.75));
                break;
            }

            case ProviderId.Antigravity:
            {
                // Four-point star with concave sides.
                var r = d * 0.48; var k = r * 0.18;
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(c.X, c.Y - r), true, true);
                    ctx.QuadraticBezierTo(new Point(c.X + k, c.Y - k), new Point(c.X + r, c.Y), true, true);
                    ctx.QuadraticBezierTo(new Point(c.X + k, c.Y + k), new Point(c.X, c.Y + r), true, true);
                    ctx.QuadraticBezierTo(new Point(c.X - k, c.Y + k), new Point(c.X - r, c.Y), true, true);
                    ctx.QuadraticBezierTo(new Point(c.X - k, c.Y - k), new Point(c.X, c.Y - r), true, true);
                }
                g.Freeze();
                dc.DrawGeometry(hue, null, g);
                break;
            }

            case ProviderId.Grok:
            {
                var r = d * 0.36;
                dc.DrawEllipse(null, round, c, r, r);
                dc.DrawLine(round, new Point(c.X - r * 1.15, c.Y + r * 1.15), new Point(c.X + r * 1.15, c.Y - r * 1.15));
                break;
            }

            case ProviderId.Cursor:
            {
                var s = d * 0.46;
                dc.DrawGeometry(hue, null, Poly(true,
                    new(c.X - s * 0.7, c.Y - s), new(c.X + s * 0.75, c.Y + s * 0.1), new(c.X + s * 0.05, c.Y + s * 0.3), new(c.X - s * 0.3, c.Y + s)));
                break;
            }

            default:
            {
                var s = d * 0.42; var w = s * 0.45;
                dc.DrawGeometry(null, round, Poly(false, new(c.X - s + w, c.Y - s), new(c.X - s, c.Y - s), new(c.X - s, c.Y + s), new(c.X - s + w, c.Y + s)));
                dc.DrawGeometry(null, round, Poly(false, new(c.X + s - w, c.Y - s), new(c.X + s, c.Y - s), new(c.X + s, c.Y + s), new(c.X + s - w, c.Y + s)));
                break;
            }
        }
    }

    static Geometry Poly(bool closed, params Point[] pts)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(pts[0], closed, closed);
            for (var i = 1; i < pts.Length; i++) ctx.LineTo(pts[i], true, true);
        }
        g.Freeze();
        return g;
    }

    /// <summary>The mark as a WPF element, for the flyout.</summary>
    public static FrameworkElement Element(ProviderId id, Color hue, double size) => new MarkElement(id, Palette.Brush(hue)) { Width = size, Height = size };

    sealed class MarkElement(ProviderId id, Brush hue) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc) => Draw(dc, id, new Point(ActualWidth / 2, ActualHeight / 2), Math.Min(ActualWidth, ActualHeight), hue);
    }
}
