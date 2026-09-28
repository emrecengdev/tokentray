using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TokenTray;

/// <summary>
/// A bar whose fill is the allowance and whose notch marks the same share of time.
/// In "remaining" mode the notch is the time left; fill behind the notch means you're
/// spending faster than the window refills.
/// </summary>
internal sealed class PaceBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(PaceBar), new(0.0, OnValueChanged));
    static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        "Shown", typeof(double), typeof(PaceBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Notch { get; set; } = double.NaN;
    public Color FillColor { get; set; }
    public Color TrackColor { get; set; }
    public Color NotchColor { get; set; }
    public bool Animate { get; set; } = true;

    static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (PaceBar)d;
        var to = Math.Clamp((double)e.NewValue, 0, 1);
        if (!bar.Animate || !Native.AnimationsEnabled)
        {
            bar.BeginAnimation(ShownProperty, null);
            bar.SetValue(ShownProperty, to);
            return;
        }
        bar.BeginAnimation(ShownProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(520))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    protected override Size MeasureOverride(Size available) => new(double.IsInfinity(available.Width) ? 120 : available.Width, 6);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = Math.Min(ActualHeight, 6);
        var y = (ActualHeight - h) / 2; var r = h / 2;
        dc.DrawRoundedRectangle(Theme(TrackColor), null, new Rect(0, y, w, h), r, r);

        var shown = (double)GetValue(ShownProperty);
        if (shown > 0.001)
        {
            var fw = Math.Max(h, w * shown);
            dc.DrawRoundedRectangle(Theme(FillColor), null, new Rect(0, y, fw, h), r, r);
        }

        if (!double.IsNaN(Notch))
        {
            // Snap to device pixels so the notch stays crisp.
            var x = Math.Round(Math.Clamp(Notch, 0, 1) * w) - 1;
            dc.DrawRoundedRectangle(Theme(NotchColor), null, new Rect(Math.Clamp(x, 0, w - 2), y - 3, 2, h + 6), 1, 1);
        }
    }

    static SolidColorBrush Theme(Color c) => Palette.Brush(c);
}
