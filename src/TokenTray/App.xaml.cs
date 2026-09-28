using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using TokenTray.Core;

namespace TokenTray;

public partial class App : Application
{
    Mutex? single;
    Settings settings = new();
    Poller? poller;
    TaskbarWidget? widget;
    TrayIcon? tray;
    FlyoutWindow? flyout;
    Notifier? notifier;
    DispatcherTimer? tick;
    TaskbarOccupancy? occupancy;
    HoverCard? card;
    DispatcherTimer? hoverDelay;
    readonly SnapshotCache cache = new(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TokenTray", "last.json"));

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 1 && e.Args[0] == "--render-media")
        {
            // README images from sample data. Language first: strings read the culture once.
            var lang = Array.IndexOf(e.Args, "--lang") is var li and >= 0 && li + 1 < e.Args.Length ? e.Args[li + 1] : "en";
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(lang);
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(lang);
            try
            {
                MediaRenderer.Run(e.Args[1], (snaps, _) => RowsFor(snaps, DateTimeOffset.Now),
                    (data, s) => { settings = s; return new FlyoutWindow(data, () => [], s, () => { }, () => { }, () => { }); });
            }
            catch (Exception ex) { Log(ex); }
            Shutdown();
            return;
        }
        single = new Mutex(true, @"Local\TokenTray.SingleInstance", out var first);
        if (!first) { Shutdown(); return; }

        DispatcherUnhandledException += (_, ex) => { Log(ex.Exception); ex.Handled = true; };
        settings = Settings.Load();
        Palette.UiOverride = settings.UiDark;

        tray = new TrayIcon();
        notifier = new Notifier(tray);
        flyout = new FlyoutWindow(Snapshots, WidgetRows, settings, OnSettingsChanged, () => poller?.RefreshNow(), Quit);
        occupancy = new TaskbarOccupancy();
        widget = new TaskbarWidget { Offset = () => settings.TaskbarOffset, Options = () => settings.Widget, Occupied = () => occupancy.Occupied };
        occupancy.Changed += () => Dispatcher.BeginInvoke(() => widget?.Redraw());

        // Hovering the widget shows the glance card after a short dwell.
        card = new HoverCard();
        hoverDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(380), DispatcherPriority.Normal, (_, _) =>
        {
            hoverDelay!.Stop();
            if (flyout?.IsVisible == true || widget.ScreenBounds is not { } bounds) return;
            card.ShowFor(Snapshots(), settings.ShowRemaining, bounds);
        }, Dispatcher);
        hoverDelay.Stop();
        widget.HoverChanged += over =>
        {
            if (over) hoverDelay.Start();
            else { hoverDelay.Stop(); card.Hide(); }
        };

        tray.Clicked += () => { card?.Hide(); flyout.Toggle(widget.ScreenBounds); };
        tray.RightClicked += ShowMenu;
        widget.Clicked += () => { card?.Hide(); flyout.Toggle(widget.ScreenBounds); };
        widget.RightClicked += ShowMenu;

        StartPolling();
        ApplyWidget();

        // Countdowns move every minute even without new data.
        tick = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => Render(), Dispatcher);
        tick.Start();

        SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(Render);
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(() => widget?.Redraw());
        SystemEvents.PowerModeChanged += (_, pe) => { if (pe.Mode == PowerModes.Resume) poller?.RefreshNow(); };

        if (e.Args.Contains("--open")) Dispatcher.BeginInvoke(() => flyout.ShowAt(widget.ScreenBounds), DispatcherPriority.ApplicationIdle);
        if (!settings.Welcomed) Dispatcher.BeginInvoke(Welcome, DispatcherPriority.ApplicationIdle);
        if (e.Args.Contains("--settings")) Dispatcher.BeginInvoke(() => flyout.ShowSettingsAt(widget.ScreenBounds), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// First run: open the flyout so people see what they got, and say where the tray icon went,
    /// since Windows 11 tucks new ones behind the ^ overflow.
    /// </summary>
    void Welcome()
    {
        settings.Welcomed = true;
        settings.Save();
        flyout?.ShowAt(widget?.ScreenBounds);
        tray?.Notify(S.WelcomeTitle, S.WelcomeBody(settings.EmbedInTaskbar));
    }

    void StartPolling()
    {
        poller?.Dispose();
        poller = new Poller(settings.BuildProviders(), TimeSpan.FromSeconds(Math.Max(120, settings.IntervalSeconds)));
        foreach (var snap in cache.Load()) poller.Seed(snap);
        foreach (var (key, until) in cache.LoadHolds()) poller.Hold(key, until);
        poller.Updated += snap => Dispatcher.BeginInvoke(() =>
        {
            if (settings.Notifications) notifier?.Observe(snap);
            if (snap.Status == SnapshotStatus.Ok && !snap.Stale) { cache.Save([snap]); cache.SaveHold(snap.Source, null); }
            if (snap.Status == SnapshotStatus.RateLimited) cache.SaveHold(snap.Source, snap.RetryAt);
            Render();
        });
        poller.Start();
        Render();
    }

    IReadOnlyList<ProviderSnapshot> Snapshots()
    {
        if (poller is null) return [];
        return poller.All
            .OrderBy(p => p.Id).ThenBy(p => p.Key)
            .Select(p => poller.Latest(p.Key) ?? new ProviderSnapshot(p.Id, SnapshotStatus.Loading, [], DateTimeOffset.Now,
                Key: p.Key == p.Id.ToString() ? null : p.Key,
                Name: settings.Accounts.FirstOrDefault(a => $"{a.Provider}:{a.Path}" == p.Key)?.Name))
            .ToList();
    }

    IReadOnlyList<WidgetRenderer.Row> WidgetRows() => RowsFor(Snapshots(), DateTimeOffset.Now);

    IReadOnlyList<WidgetRenderer.Row> RowsFor(IReadOnlyList<ProviderSnapshot> snapshots, DateTimeOffset now)
    {
        // Tools that need a sign-in take no taskbar space; the flyout says what to do about them.
        return snapshots.Where(s => s.Windows.Count > 0 || s.Status is SnapshotStatus.Loading or SnapshotStatus.RateLimited or SnapshotStatus.Offline)
            .Select(s =>
        {
            var session = s.Get(WindowKind.Session);
            var weekly = s.Long;
            if (session != null || weekly != null)
            {
                // The countdown that matters most is the one that comes first.
                var next = ProviderSnapshot.NextReset([session, weekly], now) is { } r ? r - now : (TimeSpan?)null;
                return new WidgetRenderer.Row(s.Provider,
                    session is null ? null : Reading.Of(session, settings.ShowRemaining, now),
                    weekly is null ? null : Reading.Of(weekly, settings.ShowRemaining, now),
                    null, next, Stale: IsOld(s, now), Name: s.Name);
            }
            var problem = s.Status switch
            {
                SnapshotStatus.Loading => S.Tr ? "okunuyor…" : "reading…",
                SnapshotStatus.NotSignedIn => S.Tr ? "giriş yok" : "not signed in",
                SnapshotStatus.TokenExpired => S.Tr ? "oturum doldu" : "sign-in expired",
                SnapshotStatus.Offline => S.Tr ? "bağlantı yok" : "offline",
                SnapshotStatus.RateLimited when s.RetryAt is { } at && at > now => S.Tr ? $"okuma {S.ShortDuration(at - now)} içinde" : $"reading in {S.ShortDuration(at - now)}",
                SnapshotStatus.RateLimited => S.Tr ? "okunuyor…" : "reading…",
                _ => S.Tr ? "okunamadı" : "unavailable",
            };
            var waiting = s.Status is SnapshotStatus.Loading or SnapshotStatus.RateLimited;
            return new WidgetRenderer.Row(s.Provider, null, null, problem, null, waiting, Name: s.Name);
        }).ToList();
    }

    /// <summary>A reading that failed to refresh, or is older than two polls, is shown as old.</summary>
    bool IsOld(ProviderSnapshot s, DateTimeOffset now) =>
        s.Windows.Count > 0 && s.Stale &&
        (s.Status != SnapshotStatus.Ok || now - s.FetchedAt > TimeSpan.FromSeconds(Math.Max(120, settings.IntervalSeconds) * 2 + 60));

    void Render()
    {
        var now = DateTimeOffset.Now;
        var snaps = Snapshots();

        // Tray icon follows whichever provider is closest to running out.
        var tightest = snaps
            .Where(s => s.Binding != null)
            .Select(s => (s, r: Reading.Of(s.Binding!, remaining: true, now)))
            .OrderBy(x => x.r.Percent)
            .FirstOrDefault();
        var tip = string.Join("\n", snaps.Select(s => s.Binding is { } b
            ? $"{S.Label(s)}: {S.Percent(settings.ShowRemaining ? b.RemainingPercent : b.UsedPercent)} {(settings.ShowRemaining ? S.Left : S.Used)} · {S.Window(b)}" +
              (b.ResetsAt is { } r && r > now ? $" · {S.ShortDuration(r - now)}" : "") +
              (IsOld(s, now) ? (S.Tr ? $" (son okuma {S.ShortDuration(now - s.FetchedAt)} önce)" : $" (read {S.ShortDuration(now - s.FetchedAt)} ago)") : "")
            : $"{S.Label(s)}: {S.Status(s)}"));
        if (tip.Length == 0) tip = "TokenTray";
        var shown = tightest.s is null ? (Reading?)null : Reading.Of(tightest.s.Binding!, settings.ShowRemaining, now);
        tray?.Update(tightest.s?.Provider, shown, snaps.Any(s => s.Windows.Count == 0 && s.Status != SnapshotStatus.Loading) ? "!" : null, tip);

        if (settings.EmbedInTaskbar) widget?.Redraw();
        flyout?.Refresh();
    }

    void ApplyWidget()
    {
        if (widget is null) return;
        if (settings.EmbedInTaskbar) widget.Show(WidgetRows); else widget.Hide();
    }

    void OnSettingsChanged()
    {
        var wanted = settings.BuildProviders().Select(p => p.Key).OrderBy(k => k).ToList();
        var providersChanged = poller is null || !wanted.SequenceEqual(poller.Sources.OrderBy(k => k));
        settings.Save();
        Palette.UiOverride = settings.UiDark;
        if (providersChanged) StartPolling();
        else if (poller != null) poller.Interval = TimeSpan.FromSeconds(Math.Max(120, settings.IntervalSeconds));
        ApplyWidget();
        Render();
    }

    void ShowMenu()
    {
        flyout?.HideFlyout();
        var p = new Palette(Palette.UiDark);
        var menu = new ContextMenu { Style = (Style)Resources["TrayMenu"] };
        menu.Resources["MenuSurface"] = Palette.Brush(p.Surface);
        menu.Resources["MenuHairline"] = Palette.Brush(p.Hairline);
        menu.Resources["MenuText"] = Palette.Brush(p.Text);
        menu.Resources["MenuHover"] = Palette.Brush(p.Track);
        void Add(string text, Action act)
        {
            var mi = new MenuItem { Header = text, Style = (Style)Resources["TrayMenuItem"] };
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
        }
        Add(S.Refresh, () => poller?.RefreshNow());
        Add(S.Settings, () => flyout?.ShowSettingsAt(widget?.ScreenBounds));
        menu.Items.Add(new Separator { Style = (Style)Resources["TrayMenuSeparator"] });
        Add(S.Quit, Quit);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
        // A popup without an active owner doesn't close on outside clicks; give it focus.
        if (PresentationSource.FromVisual(menu) is System.Windows.Interop.HwndSource src) Native.SetForegroundWindow(src.Handle);
    }

    void Quit()
    {
        tick?.Stop();
        occupancy?.Dispose();
        card?.Close();
        poller?.Dispose();
        widget?.Dispose();
        tray?.Dispose();
        flyout?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        widget?.Dispose();
        tray?.Dispose();
        single?.Dispose();
        base.OnExit(e);
    }

    internal static void Log(Exception ex)
    {
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TokenTray.log");
            System.IO.File.AppendAllText(path, $"{DateTimeOffset.Now:u} {ex}\n\n");
        }
        catch { }
    }
}
