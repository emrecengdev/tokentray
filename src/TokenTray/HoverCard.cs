using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using TokenTray.Core;
using static TokenTray.Native;

namespace TokenTray;

/// <summary>
/// A glance card above the taskbar widget on hover: every window with its name, number and
/// exact reset time, so nothing in the compact widget has to be guessed. Never takes focus
/// and lets clicks through.
/// </summary>
internal sealed class HoverCard : Window
{
    readonly StackPanel body = new();
    readonly Border frame;

    public HoverCard()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 12.5;
        UseLayoutRounding = true;
        frame = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 11, 14, 12), Margin = new Thickness(12),
            BorderThickness = new Thickness(1), Child = body, MinWidth = 220,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.25 },
        };
        Content = frame;
        Grid.SetIsSharedSizeScope(body, true);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT));
    }

    public System.Windows.Media.Imaging.BitmapSource RenderForMedia(IReadOnlyList<ProviderSnapshot> snaps, bool remaining, double scale)
    {
        Fill(snaps, remaining);
        frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        frame.Arrange(new Rect(frame.DesiredSize));
        return MediaRenderer.Capture(frame, scale);
    }

    public void ShowFor(IReadOnlyList<ProviderSnapshot> snaps, bool remaining, RECT anchor)
    {
        Fill(snaps, remaining);
        Opacity = 0;
        Show();
        UpdateLayout();
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        var work = SystemParameters.WorkArea;
        Left = Math.Clamp((anchor.Left + anchor.Right) / 2.0 / scale - ActualWidth / 2, work.Left, work.Right - ActualWidth);
        // Above the widget, or below it when the taskbar sits at the top of the screen.
        var above = anchor.Top / scale - ActualHeight + 6;
        Top = above >= work.Top ? above : Math.Min(anchor.Bottom / scale - 6, work.Bottom - ActualHeight);
        if (AnimationsEnabled) BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        else Opacity = 1;
    }

    void Fill(IReadOnlyList<ProviderSnapshot> snaps, bool remaining)
    {
        var p = new Palette(Palette.UiDark);
        frame.Background = Palette.Brush(p.Surface);
        frame.BorderBrush = Palette.Brush(p.Hairline);
        Foreground = Palette.Brush(p.Text);
        body.Children.Clear();
        var now = DateTimeOffset.Now;

        for (var i = 0; i < snaps.Count; i++)
        {
            var s = snaps[i];
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, i == 0 ? 0 : 10, 0, 4) };
            var mark = ProviderMark.Element(s.Provider, p.Hue(s.Provider), 12);
            mark.Margin = new Thickness(0, 0, 7, 0);
            head.Children.Add(mark);
            head.Children.Add(new TextBlock { Text = S.FullLabel(s), FontWeight = FontWeights.SemiBold });
            if (s.Plan is { } plan) head.Children.Add(new TextBlock { Text = "  " + plan, Foreground = Palette.Brush(p.TextMuted) });
            body.Children.Add(head);

            if (s.Windows.Count == 0)
            {
                body.Children.Add(new TextBlock { Text = S.Status(s), Foreground = Palette.Brush(p.TextMuted), TextWrapping = TextWrapping.Wrap, MaxWidth = 320 });
                continue;
            }

            var grid = new Grid();
            // Shared column widths line the numbers up across providers.
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "Name" });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "Value" });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var windows = s.Windows.Where(w => w.Group is null && (w.Kind != WindowKind.WeeklyModel || w.UsedPercent > 0))
                                   .OrderBy(w => w.Kind).ToList();
            foreach (var w in windows)
            {
                var r = Reading.Of(w, remaining, now);
                var row = grid.RowDefinitions.Count;
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
                Cell(grid, row, 0, new TextBlock { Text = S.Window(w), Margin = new Thickness(19, 0, 16, 0) });
                var pct = new TextBlock
                {
                    Text = $"{S.Percent(r.Percent)} {(remaining ? S.Left : S.Used)}", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 16, 0),
                    Foreground = Palette.Brush(r.PastReset ? p.TextMuted : p.Fill(s.Provider, w, r.Pace) is var c && c != p.Hue(s.Provider) ? (c == p.Red ? p.RedText : p.AmberText) : p.Text),
                };
                Cell(grid, row, 1, pct);
                Cell(grid, row, 2, new TextBlock
                {
                    Text = r.PastReset ? S.Refilling : w.ResetsAt is { } at ? S.ResetsAt(at, now) : "",
                    Foreground = Palette.Brush(p.TextMuted),
                });
            }
            body.Children.Add(grid);
            if (s.Stale && s.Message != null)
                body.Children.Add(new TextBlock { Text = S.Status(s), FontSize = 11.5, Foreground = Palette.Brush(p.AmberText), TextWrapping = TextWrapping.Wrap, MaxWidth = 360, Margin = new Thickness(19, 2, 0, 0) });
        }

    }

    static void Cell(Grid g, int row, int col, UIElement e) { Grid.SetRow(e, row); Grid.SetColumn(e, col); g.Children.Add(e); }
}
