using TokenTray.Core;

namespace TokenTray.Tests;

public class ParsingTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-28T19:38:00Z");

    // Trimmed from a live /api/oauth/usage response.
    const string ClaudeJson = """
    {"five_hour":{"utilization":2.0,"resets_at":"2026-09-29T00:20:00.284000+00:00"},
     "seven_day":{"utilization":85.0,"resets_at":"2026-09-29T01:00:00.284022+00:00"},
     "seven_day_opus":null,"seven_day_sonnet":null,
     "limits":[{"kind":"session","percent":2},{"kind":"weekly_all","percent":85},
       {"kind":"weekly_scoped","percent":0,"resets_at":"2026-09-29T01:00:00+00:00","scope":{"model":{"id":null,"display_name":"Fable"},"surface":null}}]}
    """;

    [Fact]
    public void Claude_reads_session_weekly_and_scoped_windows()
    {
        var s = ClaudeProvider.Parse(ClaudeJson, Now);
        Assert.Equal(SnapshotStatus.Ok, s.Status);
        Assert.Equal(2, s.Get(WindowKind.Session)!.UsedPercent);
        Assert.Equal(85, s.Get(WindowKind.Weekly)!.UsedPercent);
        Assert.Equal(15, s.Get(WindowKind.Weekly)!.RemainingPercent);
        Assert.Contains(s.Windows, w => w.Label == "Weekly · Fable");
        Assert.Equal(WindowKind.Weekly, s.Binding!.Kind);
    }

    [Fact]
    public void Claude_missing_windows_is_an_error_not_zero_usage()
    {
        var s = ClaudeProvider.Parse("""{"five_hour":null,"seven_day":{"utilization":"x"}}""", Now);
        Assert.Equal(SnapshotStatus.Error, s.Status);
        Assert.Empty(s.Windows);
    }

    [Fact]
    public void Claude_null_reset_is_kept_as_unknown()
    {
        var s = ClaudeProvider.Parse("""{"seven_day":{"utilization":100.0,"resets_at":null}}""", Now);
        Assert.Null(s.Windows.Single().ResetsAt);
        Assert.Equal(PaceState.Exhausted, Pace.Of(s.Windows[0], Now).State);
    }

    [Fact]
    public void Codex_assigns_windows_by_length_not_slot()
    {
        var s = CodexProvider.Parse("""
        {"plan_type":"pro","rate_limit":{
          "primary_window":{"used_percent":21,"limit_window_seconds":604800,"reset_after_seconds":422810,"reset_at":1791047100},
          "secondary_window":{"used_percent":40,"limit_window_seconds":18000,"reset_at":1790620000}}}
        """, Now);
        Assert.Equal("Pro", s.Plan);
        Assert.Equal(21, s.Get(WindowKind.Weekly)!.UsedPercent);
        Assert.Equal(40, s.Get(WindowKind.Session)!.UsedPercent);
        Assert.Equal(WindowKind.Session, s.Windows[0].Kind);
    }

    [Fact]
    public void Codex_lone_weekly_window()
    {
        var s = CodexProvider.Parse("""{"rate_limit":{"primary_window":{"used_percent":21,"limit_window_seconds":604800,"reset_at":1791047100},"secondary_window":null}}""", Now);
        Assert.Single(s.Windows);
        Assert.Equal(WindowKind.Weekly, s.Windows[0].Kind);
    }

    [Fact]
    public void Codex_without_rate_limit_is_an_error()
    {
        Assert.Equal(SnapshotStatus.Error, CodexProvider.Parse("""{"rate_limit":null}""", Now).Status);
    }

    [Theory]
    [InlineData(85, 0.89, PaceState.OnPace)]   // 85% used, 89% through the week
    [InlineData(60, 0.30, PaceState.Hot)]
    [InlineData(10, 0.50, PaceState.Comfortable)]
    public void Pace_compares_usage_with_elapsed_time(double used, double elapsedFraction, PaceState expected)
    {
        var length = TimeSpan.FromDays(7);
        var start = Now - length * elapsedFraction;
        var w = new UsageWindow(WindowKind.Weekly, "Weekly", used, start + length, length);
        Assert.Equal(expected, Pace.Of(w, Now).State);
    }

    [Fact]
    public void Pace_projects_when_the_allowance_runs_out()
    {
        var length = TimeSpan.FromHours(5);
        var start = Now - TimeSpan.FromHours(1);
        var w = new UsageWindow(WindowKind.Session, "5-hour", 50, start + length, length);
        var p = Pace.Of(w, Now);
        Assert.Equal(PaceState.Hot, p.State);
        Assert.Equal(Now + TimeSpan.FromHours(1), p.ProjectedEmptyAt!.Value, TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(0, "now")]
    [InlineData(5 * 60, "5m")]
    [InlineData(3 * 3600 + 12 * 60, "3h 12m")]
    [InlineData(2 * 86400 + 4 * 3600, "2d 4h")]
    public void Durations_read_naturally(int seconds, string expected) =>
        Assert.Equal(expected, Format.Duration(TimeSpan.FromSeconds(seconds)));
}

public class PlanAndBindingTests
{
    [Theory]
    [InlineData("max", "default_claude_max_20x", "Max 20x")]
    [InlineData("max", "default_claude_max_5x", "Max 5x")]
    [InlineData("pro", "default_claude_ai", "Pro")]
    [InlineData(null, null, null)]
    public void Claude_plan_includes_the_max_tier(string? sub, string? tier, string? expected) =>
        Assert.Equal(expected, ClaudeProvider.PlanName(sub, tier));

    [Fact]
    public void A_lone_unused_model_limit_still_binds()
    {
        var s = ClaudeProvider.Parse("""{"seven_day_opus":{"utilization":0,"resets_at":null}}""", DateTimeOffset.Now);
        Assert.NotNull(s.Binding);
    }

    [Fact]
    public void Next_reset_picks_the_soonest_future_one()
    {
        var now = DateTimeOffset.Parse("2026-09-28T12:00:00Z");
        var soon = new UsageWindow(WindowKind.Weekly, "Weekly", 50, now.AddMinutes(10), TimeSpan.FromDays(7));
        var later = new UsageWindow(WindowKind.Session, "5-hour", 5, now.AddHours(4), TimeSpan.FromHours(5));
        var past = new UsageWindow(WindowKind.Session, "5-hour", 5, now.AddHours(-1), TimeSpan.FromHours(5));
        Assert.Equal(now.AddMinutes(10), ProviderSnapshot.NextReset([later, soon, past, null], now));
    }
}

public class VerdictTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-28T12:00:00Z");
    static UsageWindow W(WindowKind k, double used, double elapsedFraction, TimeSpan length) =>
        new(k, k.ToString(), used, Now - length * elapsedFraction + length, length);

    [Fact]
    public void Full_window_locks()
    {
        var s = new ProviderSnapshot(ProviderId.Claude, SnapshotStatus.Ok, [W(WindowKind.Session, 100, 0.5, TimeSpan.FromHours(5))], Now);
        Assert.Equal(VerdictKind.Locked, Verdict.Of(s, Now).Kind);
    }

    [Fact]
    public void Burning_fast_runs_out_before_reset()
    {
        var s = new ProviderSnapshot(ProviderId.Claude, SnapshotStatus.Ok, [W(WindowKind.Session, 60, 0.2, TimeSpan.FromHours(5))], Now);
        var v = Verdict.Of(s, Now);
        Assert.Equal(VerdictKind.RunsOut, v.Kind);
        Assert.True(v.At < v.Window!.ResetsAt);
    }

    [Fact]
    public void High_but_on_pace_is_tight_not_runs_out()
    {
        var s = new ProviderSnapshot(ProviderId.Claude, SnapshotStatus.Ok,
            [W(WindowKind.Session, 4, 0.1, TimeSpan.FromHours(5)), W(WindowKind.Weekly, 85, 0.97, TimeSpan.FromDays(7))], Now);
        Assert.Equal(VerdictKind.Tight, Verdict.Of(s, Now).Kind);
    }

    [Fact]
    public void Low_usage_is_relaxed()
    {
        var s = new ProviderSnapshot(ProviderId.Codex, SnapshotStatus.Ok, [W(WindowKind.Weekly, 21, 0.3, TimeSpan.FromDays(7))], Now);
        Assert.Equal(VerdictKind.Relaxed, Verdict.Of(s, Now).Kind);
    }
}

public class EarlyWindowTests
{
    [Fact]
    public void A_burst_minutes_into_a_session_does_not_predict_running_out()
    {
        var now = DateTimeOffset.Parse("2026-09-28T12:00:00Z");
        var length = TimeSpan.FromHours(5);
        var w = new UsageWindow(WindowKind.Session, "5-hour", 4, now - TimeSpan.FromMinutes(10) + length, length);
        Assert.Null(Pace.Of(w, now).ProjectedEmptyAt);
    }
}

public class CacheTests
{
    [Fact]
    public void Round_trips_and_comes_back_stale()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tt-{Guid.NewGuid():N}.json");
        try
        {
            var now = DateTimeOffset.Parse("2026-09-28T12:00:00Z");
            var snap = new ProviderSnapshot(ProviderId.Claude, SnapshotStatus.Ok,
                [new UsageWindow(WindowKind.Weekly, "Weekly", 85, now.AddHours(5), TimeSpan.FromDays(7))], now, "Max 20x");
            var cache = new SnapshotCache(path);
            cache.Save([snap, ProviderSnapshot.Failed(ProviderId.Codex, SnapshotStatus.Offline, "x")]);
            var back = Assert.Single(cache.Load());
            Assert.True(back.Stale);
            Assert.Equal("Max 20x", back.Plan);
            Assert.Equal(85, back.Windows[0].UsedPercent);
            Assert.Equal(now.AddHours(5), back.Windows[0].ResetsAt);
        }
        finally { File.Delete(path); }
    }
}

public class HoldTests
{
    sealed class Counting : IUsageProvider
    {
        public int Calls;
        public ProviderId Id => ProviderId.Claude;
        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new ProviderSnapshot(Id, SnapshotStatus.Ok, [], DateTimeOffset.Now));
        }
    }

    [Fact]
    public async Task A_held_provider_is_not_asked_until_the_hold_ends()
    {
        var p = new Counting();
        using var poller = new Poller([p], TimeSpan.FromMinutes(10));
        poller.Hold("Claude", DateTimeOffset.Now.AddMilliseconds(600));
        poller.Start();
        await Task.Delay(300);
        Assert.Equal(0, p.Calls);
        Assert.Equal(SnapshotStatus.RateLimited, poller.Latest("Claude")!.Status);
        await Task.Delay(700);
        Assert.Equal(1, p.Calls);
    }

    [Fact]
    public void Holds_survive_a_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tt-{Guid.NewGuid():N}");
        try
        {
            var until = DateTimeOffset.Now.AddMinutes(2);
            new SnapshotCache(Path.Combine(dir, "last.json")).SaveHold("Claude", until);
            var holds = new SnapshotCache(Path.Combine(dir, "last.json")).LoadHolds();
            Assert.Equal(until, holds["Claude"], TimeSpan.FromSeconds(1));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

public class ResetShortTests
{
    [Fact]
    public void Days_while_a_day_or_more_away_then_clock_time()
    {
        var now = new DateTimeOffset(2026, 9, 28, 23, 30, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 28)));
        Assert.Equal("2g", Format.ResetShort(now.AddDays(2).AddHours(3), now, "g"));
        Assert.Equal("1g", Format.ResetShort(now.AddHours(24), now, "g"));
        Assert.Equal(now.AddHours(4).AddMinutes(30).ToString("HH:mm"), Format.ResetShort(now.AddHours(4).AddMinutes(30), now, "g"));
        Assert.Equal("…", Format.ResetShort(now.AddMinutes(-1), now, "g"));
    }
}

public class MinuteRoundingTests
{
    [Fact]
    public void A_reset_a_hair_before_the_hour_reads_as_the_hour()
    {
        var at = new DateTimeOffset(2026, 9, 29, 0, 59, 59, 900, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 1, 0, 0, TimeSpan.Zero), Format.Minute(at).ToUniversalTime());
    }
}

public class RefreshRespectsRetryAfterTests
{
    sealed class Throttled : IUsageProvider
    {
        public int Calls;
        public ProviderId Id => ProviderId.Claude;
        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref Calls) == 1) throw new RateLimitedException(TimeSpan.FromMilliseconds(800));
            return Task.FromResult(new ProviderSnapshot(Id, SnapshotStatus.Ok, [], DateTimeOffset.Now));
        }
    }

    [Fact]
    public async Task Refresh_now_does_not_cut_a_retry_after_short()
    {
        var p = new Throttled();
        using var poller = new Poller([p], TimeSpan.FromMilliseconds(100));
        poller.Start();
        await Task.Delay(150);
        poller.RefreshNow();
        await Task.Delay(150);
        Assert.Equal(1, p.Calls);
    }
}

public class KeepLastTests
{
    sealed class Script(params ProviderSnapshot[] steps) : IUsageProvider
    {
        int i;
        public ProviderId Id => ProviderId.Claude;
        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct) => Task.FromResult(steps[Math.Min(i++, steps.Length - 1)]);
    }

    static ProviderSnapshot Good(string account) => new(ProviderId.Claude, SnapshotStatus.Ok,
        [new UsageWindow(WindowKind.Weekly, "Weekly", 40, DateTimeOffset.Now.AddDays(1), TimeSpan.FromDays(7))], DateTimeOffset.Now, Account: account);

    [Fact]
    public async Task Expired_sign_in_keeps_the_last_reading_as_stale()
    {
        using var poller = new Poller([new Script(Good("a@x"), ProviderSnapshot.Failed(ProviderId.Claude, SnapshotStatus.TokenExpired, "expired") with { Account = "a@x" })], TimeSpan.FromMilliseconds(50));
        poller.Start();
        await Task.Delay(300);
        var s = poller.Latest("Claude")!;
        Assert.Equal(SnapshotStatus.TokenExpired, s.Status);
        Assert.True(s.Stale);
        Assert.Single(s.Windows);
    }

    [Fact]
    public async Task Another_accounts_failure_does_not_inherit_old_numbers()
    {
        using var poller = new Poller([new Script(Good("a@x"), ProviderSnapshot.Failed(ProviderId.Claude, SnapshotStatus.TokenExpired, "expired") with { Account = "b@x" })], TimeSpan.FromMilliseconds(50));
        poller.Start();
        await Task.Delay(300);
        Assert.Empty(poller.Latest("Claude")!.Windows);
    }
}

public class ReviewRegressionTests
{
    sealed class SwitchingAccounts : IUsageProvider
    {
        int calls;
        public ProviderId Id => ProviderId.Codex;
        public string? LastAccount { get; private set; }
        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
        {
            if (++calls == 1)
            {
                LastAccount = "acct-a";
                return Task.FromResult(new ProviderSnapshot(Id, SnapshotStatus.Ok,
                    [new UsageWindow(WindowKind.Weekly, "Weekly", 40, DateTimeOffset.Now.AddDays(1), TimeSpan.FromDays(7))], DateTimeOffset.Now, Account: "acct-a"));
            }
            LastAccount = "acct-b";
            throw new HttpRequestException("offline");
        }
    }

    [Fact]
    public async Task A_network_error_after_switching_accounts_shows_no_old_numbers()
    {
        using var poller = new Poller([new SwitchingAccounts()], TimeSpan.FromMilliseconds(50));
        poller.Start();
        await Task.Delay(300);
        var s = poller.Latest("Codex")!;
        Assert.Equal(SnapshotStatus.Offline, s.Status);
        Assert.Empty(s.Windows);
    }

    [Fact]
    public void Without_a_measured_pace_high_usage_makes_no_promise()
    {
        var now = DateTimeOffset.Now;
        var monthly = new UsageWindow(WindowKind.Period, "This month", 95, now.AddDays(10), null);
        var s = new ProviderSnapshot(ProviderId.Cursor, SnapshotStatus.Ok, [monthly], now);
        Assert.Equal(VerdictKind.High, Verdict.Of(s, now).Kind);
    }

    [Fact]
    public void Codex_isolates_by_account_id_and_shows_email()
    {
        var s = CodexProvider.Parse("""{"email":"me@example.com","account_id":"acct-1","rate_limit":{"primary_window":{"used_percent":5,"limit_window_seconds":604800,"reset_at":1791000000}}}""", DateTimeOffset.Now);
        Assert.Equal("acct-1", s.Account);
        Assert.Equal("me@example.com", s.Email);
    }
}
