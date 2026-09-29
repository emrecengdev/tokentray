using System.Net.Http;

namespace TokenTray.Core;

/// <summary>
/// Polls each provider on its own loop — one request in flight per provider — and keeps
/// the last good reading when a later poll fails for a transient reason.
/// </summary>
public sealed class Poller : IDisposable
{
    readonly Dictionary<string, IUsageProvider> providers;
    readonly Dictionary<string, ProviderSnapshot> latest = [];
    readonly Dictionary<string, CancellationTokenSource> wake = [];
    readonly CancellationTokenSource stop = new();
    readonly object gate = new();
    readonly List<Task> loops = [];

    public TimeSpan Interval { get; set; }
    public event Action<ProviderSnapshot>? Updated;

    public Poller(IEnumerable<IUsageProvider> providers, TimeSpan interval)
    {
        this.providers = providers.ToDictionary(p => p.Key);
        Interval = interval;
    }

    public IReadOnlyCollection<string> Sources => providers.Keys;
    public IEnumerable<IUsageProvider> All => providers.Values;

    public ProviderSnapshot? Latest(string key) { lock (gate) return latest.GetValueOrDefault(key); }

    /// <summary>Show a remembered reading until the first poll answers.</summary>
    public void Seed(ProviderSnapshot snap)
    {
        lock (gate) if (providers.ContainsKey(snap.Source) && !latest.ContainsKey(snap.Source)) latest[snap.Source] = snap;
    }

    public void Start()
    {
        foreach (var p in providers.Values) loops.Add(Task.Run(() => Loop(p, stop.Token)));
    }

    /// <summary>Skip the current wait and poll everything now.</summary>
    public void RefreshNow()
    {
        lock (gate) foreach (var cts in wake.Values) cts.Cancel();
    }

    readonly Dictionary<string, DateTimeOffset> holds = [];

    /// <summary>Don't ask this provider again before <paramref name="until"/> — e.g. a Retry-After from a previous run.</summary>
    public void Hold(string key, DateTimeOffset until) { lock (gate) holds[key] = until; }

    async Task Loop(IUsageProvider provider, CancellationToken ct)
    {
        var failures = 0;
        DateTimeOffset hold;
        lock (gate) hold = holds.GetValueOrDefault(provider.Key);
        if (hold > DateTimeOffset.Now)
        {
            // Nothing has been read yet this run, so there's no account to compare: keep the saved
            // reading as it is, marked old, until the service lets us ask again.
            ProviderSnapshot wait;
            lock (gate)
            {
                var saved = latest.GetValueOrDefault(provider.Key);
                wait = saved is { Windows.Count: > 0 }
                    ? saved with { Status = SnapshotStatus.RateLimited, Stale = true, RetryAt = hold, Message = "The usage service asked us to slow down." }
                    : Fail(provider, SnapshotStatus.RateLimited, "The usage service asked us to slow down.") with { RetryAt = hold };
                latest[provider.Key] = wait;
            }
            Updated?.Invoke(wait);
            try { await Task.Delay(hold - DateTimeOffset.Now, ct); } catch (OperationCanceledException) { return; }
        }
        while (!ct.IsCancellationRequested)
        {
            TimeSpan delay;
            ProviderSnapshot snap;
            try
            {
                snap = await provider.FetchAsync(ct);
                failures = snap.Status == SnapshotStatus.Error ? failures + 1 : 0;
                delay = snap.Status == SnapshotStatus.Error ? Backoff(failures) : Interval;
                // An expired sign-in or a bad response doesn't erase what we last knew; it's shown as old.
                if (snap.Status is SnapshotStatus.Error or SnapshotStatus.TokenExpired) snap = KeepLast(snap);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (RateLimitedException rl)
            {
                failures++;
                // Retry-After is a floor, not a promise: repeated refusals back off further.
                var backoff = Backoff(failures);
                delay = rl.RetryAfter is { } ra && ra > backoff ? ra : backoff;
                snap = KeepLast(Fail(provider, SnapshotStatus.RateLimited,
                    $"The usage service asked us to slow down. Next try in {Format.Duration(delay)}.") with { RetryAt = DateTimeOffset.Now + delay });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                failures++;
                delay = Backoff(failures);
                snap = KeepLast(Fail(provider, SnapshotStatus.Offline,
                    "Can't reach the usage service. Showing the last reading."));
            }
            catch (Exception ex)
            {
                failures++;
                delay = Backoff(failures);
                snap = KeepLast(Fail(provider, SnapshotStatus.Error, $"Unexpected response: {ex.Message}"));
            }

            lock (gate) latest[provider.Key] = snap;
            Updated?.Invoke(snap);

            // Read again just after a window resets — but only when the service isn't asking us to wait.
            if (snap.Status == SnapshotStatus.Ok)
            {
                var now = DateTimeOffset.Now;
                var next = snap.Windows.Select(w => w.ResetsAt).Where(r => r > now).Min();
                if (next is { } r && r - now + TimeSpan.FromSeconds(20) < delay) delay = r - now + TimeSpan.FromSeconds(20);
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (gate) wake[provider.Key] = cts;
            try { await Task.Delay(delay, cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (OperationCanceledException) { return; }
            finally { lock (gate) wake.Remove(provider.Key); cts.Dispose(); }

            // "Refresh now" and waking from sleep may cut the wait short, but never a
            // service-imposed one: that deadline holds regardless.
            if (snap.Status == SnapshotStatus.RateLimited && snap.RetryAt is { } notBefore && notBefore > DateTimeOffset.Now)
            {
                try { await Task.Delay(notBefore - DateTimeOffset.Now, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    static ProviderSnapshot Fail(IUsageProvider p, SnapshotStatus status, string message) =>
        ProviderSnapshot.Failed(p.Id, status, message) with { Key = p.Key == p.Id.ToString() ? null : p.Key, Account = p.LastAccount };

    ProviderSnapshot KeepLast(ProviderSnapshot failed)
    {
        ProviderSnapshot? last;
        lock (gate) last = latest.GetValueOrDefault(failed.Source);
        if (last is null || last.Windows.Count == 0) return failed;
        // Only the same, identified account may inherit old numbers.
        if (last.Account is null || failed.Account != last.Account) return failed;
        return last with { Status = failed.Status, Message = failed.Message, Stale = true, RetryAt = failed.RetryAt };
    }

    TimeSpan Backoff(int failures) =>
        TimeSpan.FromSeconds(Math.Min(Interval.TotalSeconds * Math.Pow(2, Math.Max(0, failures - 1)), 30 * 60));

    public void Dispose()
    {
        stop.Cancel();
        try { Task.WaitAll([.. loops], TimeSpan.FromSeconds(2)); } catch { }
        stop.Dispose();
    }
}

public static class Format
{
    /// <summary>"2d 4h", "3h 12m", "8m", "now".</summary>
    public static string Duration(TimeSpan t)
    {
        if (t <= TimeSpan.Zero) return "now";
        if (t.TotalDays >= 1) return t.Hours > 0 ? $"{(int)t.TotalDays}d {t.Hours}h" : $"{(int)t.TotalDays}d";
        if (t.TotalHours >= 1) return t.Minutes > 0 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{(int)t.TotalHours}h";
        return $"{Math.Max(1, (int)Math.Ceiling(t.TotalMinutes))}m";
    }

    /// <summary>
    /// When a window refills, as briefly as possible: whole days while it's a day or more away ("2d"),
    /// the local clock time within the day ("04:00"), and minutes in the last hour ("40m"), when a
    /// countdown is more useful than a time of day.
    /// </summary>
    public static string ResetShort(DateTimeOffset at, DateTimeOffset now, string dayUnit, string minuteUnit = "m")
    {
        var left = at - now;
        if (left <= TimeSpan.Zero) return "…";
        if (left >= TimeSpan.FromDays(1)) return $"{(int)left.TotalDays}{dayUnit}";
        if (left < TimeSpan.FromHours(1))
            return left < TimeSpan.FromMinutes(1) ? $"<1{minuteUnit}" : $"{(int)Math.Ceiling(left.TotalMinutes)}{minuteUnit}";
        return Minute(at).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Local time rounded to the nearest minute. Services report resets a hair before or after
    /// the hour ("00:59:59.9"); truncating would print 03:59 for a 04:00 reset.
    /// </summary>
    public static DateTimeOffset Minute(DateTimeOffset at)
    {
        var local = at.ToLocalTime().AddSeconds(30);
        return new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, local.Offset);
    }

    /// <summary>Taskbar-short: "4h", "38m", "2d".</summary>
    public static string Short(TimeSpan t)
    {
        if (t <= TimeSpan.Zero) return "now";
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h";
        return $"{Math.Max(1, (int)Math.Ceiling(t.TotalMinutes))}m";
    }
}
