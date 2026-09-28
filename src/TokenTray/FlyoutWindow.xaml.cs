using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using TokenTray.Core;
using static TokenTray.Native;

namespace TokenTray;

internal partial class FlyoutWindow : Window
{
    readonly Func<IReadOnlyList<ProviderSnapshot>> data;
    readonly Func<IReadOnlyList<WidgetRenderer.Row>> widgetRows;
    readonly Settings settings;
    readonly Action settingsChanged, refresh, quit;
    Palette palette = new(Palette.UiDark);
    bool settingsPage;
    DateTime hiddenAt;
    RECT? anchor;
    bool suppressHide;

    internal FlyoutWindow(Func<IReadOnlyList<ProviderSnapshot>> data, Func<IReadOnlyList<WidgetRenderer.Row>> widgetRows, Settings settings, Action settingsChanged, Action refresh, Action quit)
    {
        this.data = data; this.widgetRows = widgetRows; this.settings = settings;
        this.settingsChanged = settingsChanged; this.refresh = refresh; this.quit = quit;
        InitializeComponent();
        RefreshButton.ToolTip = S.Refresh;
        SettingsButton.ToolTip = S.Settings;
        AutomationProperties.SetName(RefreshButton, S.Refresh);
        AutomationProperties.SetName(SettingsButton, S.Settings);
        Deactivated += (_, _) => { if (!suppressHide) HideFlyout(); };
        // SizeToContent settles after Show and on page switches; keep the flyout pinned to the taskbar.
        SizeChanged += (_, _) => { if (IsVisible) Place(anchor); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (settingsPage) ShowUsage(); else HideFlyout();
            e.Handled = true;
        };
    }

    /// <summary>True right after the flyout closed because the user clicked the widget that opens it.</summary>
    public bool JustHidden => (DateTime.UtcNow - hiddenAt).TotalMilliseconds < 350;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyTheme();
    }

    void ApplyTheme()
    {
        palette = new Palette(Palette.UiDark);
        Resources["TextBrush"] = Palette.Brush(palette.Text);
        Resources["MutedBrush"] = Palette.Brush(palette.TextMuted);
        Resources["HoverBrush"] = Palette.Brush(palette.HoverFill);
        Resources["SelectedBrush"] = Palette.Brush(palette.Track);
        Resources["AccentBrush"] = Palette.Brush(palette.Text);
        Resources["OnAccentBrush"] = Palette.Brush(palette.Surface);
        Foreground = Palette.Brush(palette.Text);
        Footer.BorderBrush = Palette.Brush(palette.Hairline);

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dark = palette.Dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        var round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        var backdrop = 3; // DWMSBT_TRANSIENTWINDOW: the acrylic Windows uses for flyouts
        if (SupportsBackdrop && DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0)
        {
            HwndSource.FromHwnd(hwnd)!.CompositionTarget!.BackgroundColor = Colors.Transparent;
            var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref m);
            // Acrylic alone takes on whatever is behind it; a tint of the theme's surface keeps text contrast steady.
            var tint = palette.Surface;
            Frame.Background = Palette.Brush(Color.FromArgb(palette.Dark ? (byte)0xB8 : (byte)0xA0, tint.R, tint.G, tint.B));
        }
        else
        {
            Frame.Background = Palette.Brush(palette.Surface);
            Frame.BorderBrush = Palette.Brush(palette.Hairline);
            Frame.BorderThickness = new Thickness(1);
        }
    }

    public void Toggle(RECT? anchor)
    {
        if (IsVisible) { HideFlyout(); return; }
        if (JustHidden) return;
        ShowAt(anchor);
    }

    public void ShowAt(RECT? anchor)
    {
        this.anchor = anchor;
        settingsPage = false;
        // Colors first: building with the previous palette left light text on a light panel.
        ApplyTheme();
        Build(animateBars: true);
        Opacity = 0;
        Show();
        UpdateLayout();
        Place(anchor);
        Opacity = 1;
        Activate();
        Focus();
        PlayOpen();
    }

    /// <summary>The usage page as an image, on a solid surface, for README screenshots.</summary>
    public BitmapSource RenderForMedia(double scale)
    {
        ApplyTheme();
        settingsPage = false;
        Build(animateBars: false);
        Frame.Background = Palette.Brush(palette.Surface);
        Frame.BorderBrush = Palette.Brush(palette.Hairline);
        Frame.BorderThickness = new Thickness(1);
        Frame.CornerRadius = new CornerRadius(10);
        Scroller.MaxHeight = double.PositiveInfinity;
        Frame.Measure(new Size(Width, double.PositiveInfinity));
        Frame.Arrange(new Rect(Frame.DesiredSize));
        return MediaRenderer.Capture(Frame, scale);
    }

    public void ShowSettingsAt(RECT? anchor)
    {
        if (!IsVisible) ShowAt(anchor);
        ShowSettings();
    }

    void Place(RECT? anchor)
    {
        var src = PresentationSource.FromVisual(this);
        var scale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        var work = SystemParameters.WorkArea;
        const double margin = 12;
        // Short screens scroll the page instead of pushing the flyout off-screen.
        Scroller.MaxHeight = Math.Max(240, work.Height - margin * 2 - Footer.ActualHeight - 2);
        var left = work.Right - ActualWidth - margin;
        if (anchor is { } a)
        {
            var center = (a.Left + a.Right) / 2.0 / scale;
            left = Math.Clamp(center - ActualWidth / 2, work.Left + margin, work.Right - ActualWidth - margin);
        }
        Left = left;
        // Taskbar on top: open downward, otherwise above.
        Top = work.Top > 0 && (anchor?.Top ?? 1) / scale < work.Top ? work.Top + margin : work.Bottom - ActualHeight - margin;
    }

    void PlayOpen()
    {
        if (!AnimationsEnabled) { Root.Opacity = 1; Slide.Y = 0; return; }
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    public void HideFlyout()
    {
        if (!IsVisible) return;
        hiddenAt = DateTime.UtcNow;
        Hide();
    }

    /// <summary>Rebuild with fresh data if open.</summary>
    public void Refresh()
    {
        if (!IsVisible) return;
        if (!settingsPage) Build(animateBars: false);
        // Style previews use live data; skip while the offset slider is being dragged.
        else if (!generalTab && Mouse.LeftButton != MouseButtonState.Pressed) ShowSettings();
    }

    void OnRefresh(object sender, RoutedEventArgs e)
    {
        refresh();
        if (!AnimationsEnabled) return;
        var spin = new RotateTransform();
        RefreshButton.RenderTransformOrigin = new Point(0.5, 0.5);
        RefreshButton.RenderTransform = spin;
        spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(600)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
    }

    void OnSettings(object sender, RoutedEventArgs e)
    {
        if (settingsPage) ShowUsage(); else ShowSettings();
    }

    void ShowUsage() { settingsPage = false; Build(animateBars: true); }

    // ───────────── Usage page ─────────────

    void Build(bool animateBars)
    {
        Page.Children.Clear();
        Footer.Visibility = Visibility.Visible;
        SettingsButton.Content = "";
        var now = DateTimeOffset.Now;
        var snaps = data();

        if (snaps.Count == 0)
        {
            Page.Children.Add(new TextBlock
            {
                Text = S.Tr ? "Hiçbir sağlayıcı açık değil. Ayarlar'dan Claude Code ya da Codex'i aç." : "No providers are on. Turn on Claude Code or Codex in Settings.",
                TextWrapping = TextWrapping.Wrap, Foreground = Palette.Brush(palette.TextMuted), Margin = new Thickness(0, 4, 0, 14),
            });
        }

        // Three or more tools side by side in two balanced columns, when the screen has room.
        var twoColumns = snaps.Count >= 3 && SystemParameters.WorkArea.Width >= 820;
        Width = twoColumns ? 720 : 360;
        if (twoColumns)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var columns = new[] { new StackPanel(), new StackPanel() };
            var heights = new double[2];
            foreach (var snap in snaps)
            {
                // Rough height: header and verdict, one row per window, a heading per extra pool.
                var estimate = 90 + 62 * snap.Windows.Count + 30 * snap.Windows.Select(w => w.Group).Distinct().Count(g => g != null);
                var c = heights[0] <= heights[1] ? 0 : 1;
                if (columns[c].Children.Count > 0)
                    columns[c].Children.Add(new Border { Height = 1, Background = Palette.Brush(palette.Hairline), Margin = new Thickness(0, 18, 0, 18) });
                columns[c].Children.Add(Section(snap, now, animateBars));
                heights[c] += estimate;
            }
            Grid.SetColumn(columns[0], 0);
            Grid.SetColumn(columns[1], 2);
            grid.Children.Add(columns[0]);
            grid.Children.Add(new Border { Width = 1, Background = Palette.Brush(palette.Hairline), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2), SnapsToDevicePixels = true, Tag = "divider" });
            Grid.SetColumn((UIElement)grid.Children[1], 1);
            grid.Children.Add(columns[1]);
            Page.Children.Add(grid);
        }
        else
        {
            for (var i = 0; i < snaps.Count; i++)
            {
                if (i > 0) Page.Children.Add(new Border { Height = 1, Background = Palette.Brush(palette.Hairline), Margin = new Thickness(0, 18, 0, 18) });
                Page.Children.Add(Section(snaps[i], now, animateBars));
            }
        }
        Page.Children.Add(new Border { Height = 12 });

        var latest = snaps.Where(s => s.Windows.Count > 0).Select(s => s.FetchedAt).DefaultIfEmpty().Max();
        UpdatedText.Text = latest == default ? "" : S.Updated(latest, now);
    }

    FrameworkElement Section(ProviderSnapshot snap, DateTimeOffset now, bool animateBars)
    {
        var panel = new StackPanel();
        var hue = palette.Hue(snap.Provider);

        // Name and plan
        var head = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 10) };
        if (snap.Plan is { } plan)
        {
            var pill = new Border
            {
                CornerRadius = new CornerRadius(5), Background = Palette.Brush(palette.Track),
                Padding = new Thickness(7, 2, 7, 3), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = plan, FontSize = 12, FontWeight = FontWeights.SemiBold },
            };
            DockPanel.SetDock(pill, Dock.Right);
            head.Children.Add(pill);
        }
        var name = new StackPanel { Orientation = Orientation.Horizontal };
        var mark = ProviderMark.Element(snap.Provider, hue, 14);
        mark.VerticalAlignment = VerticalAlignment.Center;
        mark.Margin = new Thickness(0, 1, 9, 0);
        name.Children.Add(mark);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = S.FullLabel(snap), FontWeight = FontWeights.SemiBold, FontSize = 15 });
        // Which account this is, so two sign-ins are never confused.
        if (settings.ShowEmail && snap.Email is { } email)
            titles.Children.Add(new TextBlock { Text = email, FontSize = 11.5, Foreground = Palette.Brush(palette.TextMuted) });
        name.Children.Add(titles);
        head.Children.Add(name);
        panel.Children.Add(head);

        if (snap.Windows.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = S.Status(snap), TextWrapping = TextWrapping.Wrap, LineHeight = 20,
                Foreground = Palette.Brush(snap.Status is SnapshotStatus.Loading or SnapshotStatus.RateLimited ? palette.TextMuted : palette.Text),
            });
            return panel;
        }

        // The verdict: one sentence that answers "will it last?"
        var verdict = Verdict.Of(snap, now);
        var line = S.Verdict(verdict, now);
        if (line.Length > 0)
            panel.Children.Add(new TextBlock
            {
                Text = line, FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 0, 0, 14),
                Foreground = Palette.Brush(verdict.Kind switch
                {
                    VerdictKind.Locked => palette.Red,
                    VerdictKind.RunsOut => palette.Amber,
                    _ => palette.Text,
                }),
            });

        // Every window, same weight. Main allowance first (session, weekly, model limits, period),
        // then each secondary pool under its own heading.
        var pools = snap.Windows.GroupBy(w => w.Group).OrderBy(g => g.Key is null ? 0 : 1).ThenBy(g => g.Key).ToList();
        foreach (var pool in pools)
        {
            if (pool.Key is { } groupName)
                panel.Children.Add(new TextBlock
                {
                    Text = groupName, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Palette.Brush(palette.TextMuted),
                    Margin = new Thickness(0, 16, 0, 8),
                });
            var ordered = pool.OrderBy(w => w.Kind).ThenBy(w => w.Label).ToList();
            for (var i = 0; i < ordered.Count; i++)
                panel.Children.Add(WindowRow(snap.Provider, ordered[i] with { Group = null }, now, animateBars, last: i == ordered.Count - 1));
        }

        if (snap.Stale && snap.Message != null)
            panel.Children.Add(new TextBlock
            {
                Text = S.Status(snap), FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Foreground = Palette.Brush(palette.Amber), Margin = new Thickness(0, 10, 0, 0),
            });

        return panel;
    }

    FrameworkElement WindowRow(ProviderId id, UsageWindow w, DateTimeOffset now, bool animateBars, bool last)
    {
        var r = Reading.Of(w, settings.ShowRemaining, now);
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, last ? 0 : 14) };

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labels = new StackPanel();
        labels.Children.Add(new TextBlock { Text = S.Window(w), FontWeight = FontWeights.SemiBold });
        labels.Children.Add(new TextBlock
        {
            Text = r.PastReset ? S.Refilling : w.ResetsAt is { } at ? S.ResetsAt(at, now) : S.NoReset,
            FontSize = 12, Foreground = Palette.Brush(palette.TextMuted), Margin = new Thickness(0, 1, 0, 0),
        });
        Grid.SetColumn(labels, 0);
        top.Children.Add(labels);

        var pct = new TextBlock
        {
            FontSize = 13, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 0, 1),
            Foreground = Palette.Brush(r.PastReset ? palette.TextMuted : palette.Text),
        };
        pct.Inlines.Add(new Run(S.Percent(r.Percent)) { FontWeight = FontWeights.SemiBold, FontSize = 16 });
        pct.Inlines.Add(new Run(" " + (settings.ShowRemaining ? S.Left : S.Used)) { Foreground = Palette.Brush(palette.TextMuted), FontSize = 12 });
        System.Windows.Documents.Typography.SetNumeralAlignment(pct, FontNumeralAlignment.Tabular);
        Grid.SetColumn(pct, 1);
        top.Children.Add(pct);
        row.Children.Add(top);

        var bar = new PaceBar
        {
            Height = 14, Margin = new Thickness(0, 5, 0, 0), Animate = animateBars,
            FillColor = r.PastReset ? palette.TextMuted : palette.Fill(id, w, r.Pace),
            TrackColor = palette.Track, NotchColor = palette.Tick, Notch = r.Notch,
            ToolTip = double.IsNaN(r.Notch) ? null : S.NotchHint(settings.ShowRemaining),
        };
        if (animateBars) bar.Loaded += (_, _) => bar.Value = r.Fraction; else bar.Value = r.Fraction;
        row.Children.Add(bar);
        return row;
    }

    // ───────────── Settings page ─────────────

    bool generalTab;

    void ShowSettings()
    {
        settingsPage = true;
        Width = 360;
        ApplyTheme();
        Page.Children.Clear();
        SettingsButton.Content = ""; // back arrow
        UpdatedText.Text = "";

        Page.Children.Add(new TextBlock { Text = S.Settings, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-2, 0, 0, 14) };
        foreach (var (label, general) in new[] { (S.TaskbarTab, false), (S.GeneralTab, true) })
        {
            var rb = new RadioButton { Content = label, GroupName = "tab", Style = (Style)Resources["Segment"], IsChecked = generalTab == general, Margin = new Thickness(0, 0, 4, 0) };
            rb.Checked += (_, _) => { generalTab = general; Dispatcher.BeginInvoke(ShowSettings); };
            tabs.Children.Add(rb);
        }
        Page.Children.Add(tabs);

        if (generalTab) GeneralSettings(); else TaskbarSettings();
    }

    void TaskbarSettings()
    {
        Page.Children.Add(Switch(S.EmbedLabel, S.EmbedHint, settings.EmbedInTaskbar, v => { settings.EmbedInTaskbar = v; Dispatcher.BeginInvoke(ShowSettings); }));
        if (!settings.EmbedInTaskbar) return;

        // Styles, each previewed with the real data on a taskbar-colored strip.
        Page.Children.Add(new TextBlock { Text = S.StyleLabel, Margin = new Thickness(0, 12, 0, 6) });
        var rows = widgetRows();
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var strip = palette.Dark ? Color.FromRgb(0x1C, 0x1C, 0x1C) : Color.FromRgb(0xEE, 0xEE, 0xEE);
        // Every style in two variants, name or icon, each previewed with the real data. One click picks both.
        foreach (var style in Enum.GetValues<WidgetStyle>())
        {
            var styleSelected = settings.WidgetStyle == style;
            var card = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            card.Children.Add(new TextBlock
            {
                Text = S.StyleName(style), FontSize = 12, Margin = new Thickness(2, 0, 0, 4),
                Foreground = Palette.Brush(styleSelected ? palette.Text : palette.TextMuted),
                FontWeight = styleSelected ? FontWeights.SemiBold : FontWeights.Normal,
            });
            foreach (var withName in new[] { true, false })
            {
                var selected = styleSelected && settings.WidgetName == withName;
                var bmp = WidgetRenderer.Render(rows, settings.Widget with { Style = style, Name = withName }, palette, dpi, 44, false, out var w);
                var label = new TextBlock
                {
                    Text = withName ? S.VariantName : S.VariantIcon, FontSize = 11.5, Width = 44, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Palette.Brush(selected ? palette.Text : palette.TextMuted),
                };
                DockPanel.SetDock(label, Dock.Left);
                // Shown at true size when it fits; scaled down (never up) when many tools are on.
                var preview = new Border
                {
                    CornerRadius = new CornerRadius(4), Background = Palette.Brush(strip), Height = 44, Padding = new Thickness(4, 0, 4, 0),
                    Child = new Viewbox
                    {
                        StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left,
                        Child = new Image { Source = bmp, Width = w, Height = 44, Stretch = Stretch.Fill },
                    },
                };
                var content = new DockPanel { LastChildFill = true };
                content.Children.Add(label);
                content.Children.Add(preview);
                var tile = new Button { Style = (Style)Resources["Tile"], Margin = new Thickness(0, 0, 0, 3), Tag = selected ? "on" : null, Content = content };
                AutomationProperties.SetName(tile, $"{S.StyleName(style)}, {(withName ? S.VariantName : S.VariantIcon)}");
                tile.Click += (_, _) => { settings.WidgetStyle = style; settings.WidgetName = withName; settingsChanged(); Dispatcher.BeginInvoke(ShowSettings); };
                card.Children.Add(tile);
            }
            Page.Children.Add(card);
        }

        // What each block shows
        Page.Children.Add(new TextBlock { Text = S.DetailsLabel, Margin = new Thickness(0, 10, 0, 6) });
        var chips = new WrapPanel();
        void Chip(string label, bool value, Action<bool> set)
        {
            var cb = new CheckBox { Content = label, IsChecked = value, Style = (Style)Resources["Chip"], Margin = new Thickness(0, 0, 6, 6) };
            AutomationProperties.SetName(cb, label);
            cb.Click += (_, _) => { set(cb.IsChecked == true); settingsChanged(); Dispatcher.BeginInvoke(ShowSettings); };
            chips.Children.Add(cb);
        }
        Chip(S.DetailSession, settings.WidgetSession, v => settings.WidgetSession = v);
        Chip(S.DetailWeekly, settings.WidgetWeekly, v => settings.WidgetWeekly = v);
        Chip(S.DetailTime, settings.WidgetTime, v => settings.WidgetTime = v);
        Page.Children.Add(chips);

        Page.Children.Add(new TextBlock { Text = S.OffsetLabel, Margin = new Thickness(0, 10, 0, 2) });
        var slider = new Slider { Minimum = 0, Maximum = 400, Value = settings.TaskbarOffset, SmallChange = 4, LargeChange = 40, IsSnapToTickEnabled = true, TickFrequency = 4, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(slider, S.OffsetLabel);
        slider.ValueChanged += (_, e) => { settings.TaskbarOffset = (int)e.NewValue; settingsChanged(); };
        Page.Children.Add(slider);
    }

    void GeneralSettings()
    {
        Page.Children.Add(new TextBlock { Text = S.ProvidersLabel, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        foreach (var id in Enum.GetValues<ProviderId>())
        {
            var found = Settings.IsInstalled(id);
            Page.Children.Add(Switch(S.Name(id), found ? S.FoundHere : S.NotFoundHere, settings.IsOn(id), v => settings.Set(id, v)));
        }

        // Extra sign-ins in other config folders
        Page.Children.Add(new TextBlock { Text = S.AccountsLabel, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 2) });
        Page.Children.Add(new TextBlock { Text = S.AccountsHint, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Brush(palette.TextMuted), Margin = new Thickness(0, 0, 0, 6) });
        foreach (var account in settings.Accounts.ToList())
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var remove = new Button { Content = "\uE711", Style = (Style)Resources["IconButton"], ToolTip = S.RemoveAccount };
            AutomationProperties.SetName(remove, $"{S.RemoveAccount}: {account.Name}");
            remove.Click += (_, _) => { settings.Accounts.Remove(account); settingsChanged(); Dispatcher.BeginInvoke(ShowSettings); };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = $"{account.Name}  ({S.ShortName(account.Provider)})" });
            text.Children.Add(new TextBlock { Text = account.Path, FontSize = 11.5, Foreground = Palette.Brush(palette.TextMuted), TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(text);
            Page.Children.Add(row);
        }
        var adds = new WrapPanel { Margin = new Thickness(-10, 0, 0, 0) };
        foreach (var id in new[] { ProviderId.Claude, ProviderId.Codex })
        {
            var add = new Button { Content = S.AddAccount(id), Style = (Style)Resources["TextButton"], Height = 32 };
            add.Click += (_, _) => AddAccount(id);
            adds.Children.Add(add);
        }
        Page.Children.Add(adds);
        Page.Children.Add(new Border { Height = 1, Background = Palette.Brush(palette.Hairline), Margin = new Thickness(0, 8, 0, 8) });

        Page.Children.Add(new TextBlock { Text = S.DisplayLabel, Margin = new Thickness(0, 4, 0, 6) });
        var mode = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-2, 0, 0, 8) };
        foreach (var (label, remaining) in new[] { (S.UsedOption, false), (S.LeftOption, true) })
        {
            var rb = new RadioButton { Content = label, GroupName = "mode", Style = (Style)Resources["Segment"], IsChecked = settings.ShowRemaining == remaining, Margin = new Thickness(0, 0, 4, 0) };
            rb.Checked += (_, _) => { settings.ShowRemaining = remaining; settingsChanged(); };
            mode.Children.Add(rb);
        }
        Page.Children.Add(mode);
        Page.Children.Add(new TextBlock { Text = S.ThemeLabel, Margin = new Thickness(0, 4, 0, 6) });
        var themes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-2, 0, 0, 2) };
        foreach (var t in Enum.GetValues<AppTheme>())
        {
            var rb = new RadioButton { Content = S.ThemeName(t), GroupName = "theme", Style = (Style)Resources["Segment"], IsChecked = settings.Theme == t, Margin = new Thickness(0, 0, 4, 0) };
            rb.Checked += (_, _) => { settings.Theme = t; settingsChanged(); Dispatcher.BeginInvoke(ShowSettings); };
            themes.Children.Add(rb);
        }
        Page.Children.Add(themes);
        Page.Children.Add(new TextBlock { Text = S.ThemeHint, FontSize = 12, Foreground = Palette.Brush(palette.TextMuted), Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
        Page.Children.Add(Switch(S.EmailLabel, S.EmailHint, settings.ShowEmail, v => settings.ShowEmail = v));
        Page.Children.Add(Switch(S.NotifyLabel, S.NotifyHint, settings.Notifications, v => settings.Notifications = v));
        Page.Children.Add(Switch(S.StartupLabel, null, Settings.StartWithWindows, v => Settings.StartWithWindows = v, persist: false));

        Page.Children.Add(new TextBlock { Text = S.IntervalLabel, Margin = new Thickness(0, 12, 0, 6) });
        var seg = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-2, 0, 0, 0) };
        foreach (var m in new[] { 2, 5, 10, 15 })
        {
            var rb = new RadioButton { Content = S.Minutes(m), GroupName = "interval", Style = (Style)Resources["Segment"], IsChecked = settings.IntervalSeconds == m * 60, Margin = new Thickness(0, 0, 4, 0) };
            rb.Checked += (_, _) => { settings.IntervalSeconds = m * 60; settingsChanged(); };
            seg.Children.Add(rb);
        }
        Page.Children.Add(seg);

        var quitButton = new Button { Content = S.Quit, Style = (Style)Resources["TextButton"], Height = 32, Margin = new Thickness(-10, 14, 0, 6), HorizontalAlignment = HorizontalAlignment.Left, Foreground = Palette.Brush(palette.Red) };
        quitButton.Click += (_, _) => quit();
        Page.Children.Add(quitButton);
    }

    void AddAccount(ProviderId id)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = S.PickFolder(id) };
        // Deactivation would hide the flyout while the dialog is up.
        suppressHide = true;
        try { if (dialog.ShowDialog(this) != true) return; }
        finally { suppressHide = false; }
        var path = dialog.FolderName;
        var file = id == ProviderId.Claude ? ".credentials.json" : "auth.json";
        if (!System.IO.File.Exists(System.IO.Path.Combine(path, file)))
        {
            MessageBox.Show(this, S.NoSignInInFolder(id, file), "TokenTray", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (settings.Accounts.Any(a => a.Provider == id && string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        var folder = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar)).TrimStart('.');
        settings.Accounts.Add(new ExtraAccount(id, path, folder.Length > 0 ? folder : S.ShortName(id)));
        settingsChanged();
        ShowSettings();
    }

    FrameworkElement Switch(string label, string? hint, bool value, Action<bool> set, bool persist = true)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = label });
        if (hint != null) content.Children.Add(new TextBlock { Text = hint, FontSize = 12, Foreground = Palette.Brush(palette.TextMuted), TextWrapping = TextWrapping.Wrap });
        var cb = new CheckBox { Content = content, IsChecked = value, Style = (Style)Resources["Switch"], Margin = new Thickness(0, 5, 0, 5) };
        AutomationProperties.SetName(cb, label);
        cb.Click += (_, _) =>
        {
            set(cb.IsChecked == true);
            if (persist) settingsChanged();
        };
        return cb;
    }
}

file static class AutomationProperties
{
    public static void SetName(DependencyObject d, string name) => System.Windows.Automation.AutomationProperties.SetName(d, name);
}
