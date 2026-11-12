using TokenTray.Core;

namespace TokenTray.Tests;

public class MoreProviderTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-28T21:10:00Z");

    // Trimmed from a live retrieveUserQuotaSummary response.
    const string AntigravityJson = """
    {"groups":[
      {"buckets":[
        {"bucketId":"gemini-weekly","window":"weekly","resetTime":"2026-10-01T01:34:20Z","remainingFraction":0.85421365},
        {"bucketId":"gemini-5h","window":"5h","resetTime":"2026-09-28T22:38:36Z","remainingFraction":0.9916939}],
       "displayName":"Gemini Models","description":"Models within this group: Gemini Flash, Gemini Pro"},
      {"buckets":[
        {"bucketId":"3p-weekly","window":"weekly","resetTime":"2026-10-05T21:18:22Z","remainingFraction":1},
        {"bucketId":"3p-5h","window":"5h","resetTime":"2026-09-29T02:18:22Z","remainingFraction":1}],
       "displayName":"Claude and GPT models"}]}
    """;

    [Fact]
    public void Antigravity_gemini_is_the_main_pool_and_others_are_groups()
    {
        var s = AntigravityProvider.Parse(AntigravityJson, Now);
        Assert.Equal(SnapshotStatus.Ok, s.Status);
        Assert.Equal(14.6, s.Get(WindowKind.Weekly)!.UsedPercent, 1);
        Assert.Null(s.Get(WindowKind.Weekly)!.Group);
        Assert.Equal(0.8, s.Get(WindowKind.Session)!.UsedPercent, 1);
        Assert.Equal(2, s.Windows.Count(w => w.Group == "Claude and GPT"));
        Assert.Equal(WindowKind.Weekly, s.Binding!.Kind);
    }

    [Fact]
    public void Antigravity_without_buckets_is_an_error()
    {
        Assert.Equal(SnapshotStatus.Error, AntigravityProvider.Parse("""{"groups":[]}""", Now).Status);
    }

    // Trimmed from %APPDATA%\Grok Bot\sand-client-persistence. xAI bills one allowance across
    // its products, so the Grok Bot app's weekly reading is the subscription's reading.
    const string GrokBotReading = """
    {"schemaVersion":2,"value":{"kind":"present","selectedTeamId":null,
      "reading":{"usage":{"percentUsed":36.684723726977246,"nextResetMs":1794680537790,
        "isSandTrial":false,"hasNonZeroIncludedLimit":true,"isTeamSeat":false,"onDemand":null,
        "grokPlanLabel":"Grok Bot Plan","unifiedPlanName":"Premium","readForTeamId":null},
        "readAtMs":1794474311428},
      "expiresAtMs":1794560711976}}
    """;

    [Fact]
    public void Grok_bot_reading_becomes_one_weekly_window()
    {
        var s = GrokProvider.ParseBotCache(GrokBotReading, DateTimeOffset.Parse("2026-11-12T09:07:00Z"));
        Assert.Equal(SnapshotStatus.Ok, s.Status);
        var w = s.Windows.Single();
        Assert.Equal(WindowKind.Weekly, w.Kind);
        Assert.Equal(36.7, w.UsedPercent, 1);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1794680537790), w.ResetsAt);
        Assert.Equal("Premium", s.Plan);
        // The reading is dated when Grok Bot took it, not when we read its file.
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1794474311428), s.FetchedAt);
        Assert.False(s.Stale);
    }

    [Fact]
    public void Grok_bot_reading_past_its_deadline_keeps_the_numbers_but_says_why()
    {
        var s = GrokProvider.ParseBotCache(GrokBotReading, DateTimeOffset.Parse("2026-11-13T10:00:00Z"));
        Assert.True(s.Stale);
        Assert.Equal(SnapshotStatus.Offline, s.Status);
        Assert.Contains("isn't running", s.Message!);
        Assert.Single(s.Windows);
    }

    [Fact]
    public void Grok_bot_names_its_cache_files_by_base32()
    {
        Assert.Equal("onqw4zbomnwgszlooqxhg3djmnss4y3mnfsw45bnnvsxiyjomfrwg33vnz2c243mn52a",
            GrokProvider.Base32(System.Text.Encoding.UTF8.GetBytes("sand.client.slice.client-meta.account-slot")));
        Assert.Equal("auth0|user_01KYNZ3GF19J389Y13RQWEG4WA",
            GrokProvider.AccountOf("""{"schemaVersion":1,"value":"auth0|user_01KYNZ3GF19J389Y13RQWEG4WA"}"""));
    }

    [Fact]
    public void Grok_bot_reading_without_usage_says_what_is_missing()
    {
        Assert.Equal(SnapshotStatus.Error, GrokProvider.ParseBotCache("""{"value":{"reading":{}}}""", Now).Status);
        Assert.Equal(SnapshotStatus.Error, GrokProvider.ParseBotCache("not json", Now).Status);
        Assert.Equal(SnapshotStatus.NotSignedIn, GrokProvider.ParseBotCache("""{"value":{"kind":"signed-out"}}""", Now).Status);
    }

    [Fact]
    public async Task Grok_fresh_app_reading_wins_over_an_expired_cli_sign_in()
    {
        var dir = Directory.CreateTempSubdirectory("tt-grok-");
        try
        {
            const string account = "auth0|user_test";
            var readAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            WriteBlob(dir.FullName, "sand.client.slice.client-meta.account-slot",
                """{"schemaVersion":1,"value":"auth0|user_test"}""");
            WriteBlob(dir.FullName, $"sand.client.slice.account.{Uri.EscapeDataString(account)}.weekly-usage.cache",
                BotCache(12.5, readAt, readAt + 2 * 86400000, readAt + 43_200_000));

            // A CLI sign-in that expired years ago must not outrank a fresh reading.
            var auth = Path.Combine(dir.FullName, "auth.json");
            File.WriteAllText(auth, """{"https://auth.x.ai::c":{"key":"tok","expires_at":"2020-01-01T00:00:00Z"}}""");

            var s = await new GrokProvider(authPath: () => auth, dataDir: () => dir.FullName).FetchAsync(default);
            Assert.Equal(SnapshotStatus.Ok, s.Status);
            Assert.Equal(12.5, s.Windows.Single().UsedPercent);
            Assert.Equal("Premium", s.Plan);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task Grok_falls_back_to_the_cli_when_the_app_reading_expired()
    {
        var dir = Directory.CreateTempSubdirectory("tt-grok-");
        try
        {
            const string account = "auth0|user_test";
            var readAt = DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds();
            WriteBlob(dir.FullName, "sand.client.slice.client-meta.account-slot",
                """{"schemaVersion":1,"value":"auth0|user_test"}""");
            WriteBlob(dir.FullName, $"sand.client.slice.account.{Uri.EscapeDataString(account)}.weekly-usage.cache",
                BotCache(80, readAt, readAt + 5 * 86400000, readAt + 3_600_000));

            // With no CLI sign-in at all, the expired numbers still stand, marked old with the reason.
            var s = await new GrokProvider(authPath: () => Path.Combine(dir.FullName, "none.json"), dataDir: () => dir.FullName).FetchAsync(default);
            Assert.True(s.Stale);
            Assert.Contains("isn't running", s.Message!);
            Assert.Equal(80, s.Windows.Single().UsedPercent);
        }
        finally { dir.Delete(true); }
    }

    static void WriteBlob(string dir, string key, string json) =>
        File.WriteAllText(Path.Combine(dir, GrokProvider.Base32(System.Text.Encoding.UTF8.GetBytes(key)) + ".blob"), json);

    /// <summary>A Grok Bot cache blob, built by substitution so the JSON braces stay out of the literal.</summary>
    static string BotCache(double percent, long readAtMs, long resetMs, long expiresMs) =>
        """{"schemaVersion":2,"value":{"kind":"present","reading":{"usage":{"percentUsed":PERCENT,"nextResetMs":RESET,"grokPlanLabel":"Grok Bot Plan","unifiedPlanName":"Premium"},"readAtMs":READ},"expiresAtMs":EXPIRES}}"""
            .Replace("PERCENT", percent.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
            .Replace("RESET", resetMs.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("READ", readAtMs.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("EXPIRES", expiresMs.ToString(System.Globalization.CultureInfo.InvariantCulture));

    // The CLI's own billing response, kept so both sources stay covered.
    [Fact]
    public void Grok_cli_billing_builds_a_weekly_or_monthly_window()
    {
        var weekly = GrokProvider.Parse("""{"subscriptionTier":"SUBSCRIPTION_TIER_SUPER_GROK","config":{"creditUsagePercent":37.5,"currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY","end":"2026-10-02T00:00:00Z"}}}""", Now);
        Assert.Equal(WindowKind.Weekly, weekly.Windows.Single().Kind);
        Assert.Equal(37.5, weekly.Windows[0].UsedPercent);
        Assert.Equal("Super Grok", weekly.Plan);

        var monthly = GrokProvider.Parse("""{"config":{"creditUsagePercent":"12","currentPeriod":{"periodType":"MONTHLY","end":1791000000000}}}""", Now);
        Assert.Equal(WindowKind.Period, monthly.Windows.Single().Kind);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1791000000000), monthly.Windows[0].ResetsAt);
        Assert.Same(monthly.Windows[0], monthly.Long);
    }

    [Fact]
    public void Grok_cli_session_prefers_the_newest_xai_sign_in()
    {
        var s = GrokProvider.SelectSession("""
        {"https://auth.x.ai::old":{"key":"a","expires_at":"2026-01-01T00:00:00Z"},
         "https://auth.x.ai::new":{"key":"b","user_id":"u1","email":"me@x.ai","expires_at":"2026-12-01T00:00:00Z"},
         "https://idp.example.com::client":{"key":"corp","expires_at":"2027-01-01T00:00:00Z"},
         "https://auth.x.ai::":{"key":"blank-client"}}
        """);
        Assert.Equal("b", s!.Token);
        Assert.Equal("u1", s.UserId);
        Assert.Equal("me@x.ai", s.Email);
    }

    [Fact]
    public void Cursor_maps_included_and_api_usage()
    {
        var s = CursorProvider.Parse("""{"billingCycleEnd":"2026-10-25T19:27:24.000Z","membershipType":"pro","individualUsage":{"plan":{"autoPercentUsed":12.5,"apiPercentUsed":3.0,"totalPercentUsed":10.0}}}""", Now);
        Assert.Equal(10.0, s.Long!.UsedPercent);
        Assert.Null(s.Long.Group);
        Assert.Equal("API", s.Windows.Single(w => w.Group != null).Group);
        Assert.Equal("Pro", s.Plan);
    }

    [Fact]
    public void Cursor_cookie_from_jwt_or_existing_forms()
    {
        // payload {"sub":"auth0|user_123"}
        var jwt = "h.eyJzdWIiOiJhdXRoMHx1c2VyXzEyMyJ9.s";
        Assert.Equal($"user_123%3A%3A{jwt}", CursorProvider.SessionCookie(jwt));
        Assert.Equal("id%3A%3Atok", CursorProvider.SessionCookie("id::tok"));
        Assert.Equal("id%3A%3Atok", CursorProvider.SessionCookie("WorkosCursorSessionToken=id%3A%3Atok"));
        Assert.Null(CursorProvider.SessionCookie("a\r\nCookie: x"));
    }

    [Fact]
    public void OpenCode_meters_become_session_week_and_month()
    {
        var s = OpenCodeProvider.Parse("""
        {"access":{"startsAt":"2026-09-01T00:00:00.000Z","endsAt":"2026-10-01T00:00:00.000Z","meters":{
          "fiveHour":{"limitMicroCents":"2000000000","usedMicroCents":"250000000","resetsAt":"2026-09-28T23:00:00.000Z"},
          "week":{"limitMicroCents":"10000000000","usedMicroCents":"4500000000","resetsAt":"2026-10-01T00:00:00.000Z"},
          "month":{"limitMicroCents":"20000000000","usedMicroCents":"12000000000"}}}}
        """, Now);
        Assert.Equal(12.5, s.Get(WindowKind.Session)!.UsedPercent);
        Assert.Equal(45, s.Get(WindowKind.Weekly)!.UsedPercent);
        var month = s.Get(WindowKind.Period)!;
        Assert.Equal(60, month.UsedPercent);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T00:00:00Z"), month.ResetsAt);
    }

    [Fact]
    public void OpenCode_without_access_is_an_error()
    {
        Assert.Equal(SnapshotStatus.Error, OpenCodeProvider.Parse("""{"access":null}""", Now).Status);
        Assert.Equal(SnapshotStatus.Error, OpenCodeProvider.Parse("null", Now).Status);
    }

    [Fact]
    public void Jwt_claims_decode_without_padding() =>
        Assert.Equal("auth0|user_123", Jwt.Claim("h.eyJzdWIiOiJhdXRoMHx1c2VyXzEyMyJ9.s", "sub"));

    [Fact]
    public void Codex_response_carries_the_account()
    {
        var s = CodexProvider.Parse("""{"email":"me@example.com","plan_type":"plus","rate_limit":{"primary_window":{"used_percent":5,"limit_window_seconds":18000,"reset_at":1791000000}}}""", Now);
        Assert.Equal("me@example.com", s.Email);
        Assert.Equal("Plus", s.Plan);
        Assert.Equal(WindowKind.Session, s.Windows[0].Kind);
    }
}

public class ClaudeDesktopTests
{
    [Fact]
    public void Prefers_the_usage_capable_token_and_reads_account_and_plan()
    {
        const string plain = """
        {"acct:11111111-2222-3333-4444-555555555555|aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee:ffffffff-0000-1111-2222-333333333333:https://api.anthropic.com:user:profile":
            {"token":"profile-only","expiresAt":4102444800000},
         "acct:11111111-2222-3333-4444-555555555555|aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee:ffffffff-0000-1111-2222-333333333333:https://api.anthropic.com:user:inference user:profile":
            {"token":"both","expiresAt":1790000000000,"subscriptionType":"max","rateLimitTier":"default_claude_max_20x"}}
        """;
        var t = ClaudeDesktop.Select(plain)!;
        Assert.Equal("both", t.AccessToken);
        Assert.Equal("11111111-2222-3333-4444-555555555555", t.AccountId);
        Assert.Equal("Max 20x", t.Plan);
    }
}

public class ClaudeDesktopSelectionTests
{
    const string A = "11111111-2222-3333-4444-555555555555", B = "99999999-8888-7777-6666-555555555555";
    static string Entry(string account, string token, long expires) =>
        $"\"acct:{account}|aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee:ffffffff-0000-1111-2222-333333333333:https://api.anthropic.com:user:inference user:profile\":{{\"token\":\"{token}\",\"expiresAt\":{expires}}}";

    [Fact]
    public void Filters_by_account_before_choosing()
    {
        var plain = "{" + Entry(A, "a-token", 1790000000000) + "," + Entry(B, "b-token", 4102444800000) + "}";
        Assert.Equal("a-token", ClaudeDesktop.Select(plain, t => t.AccountId == A)!.AccessToken);
        Assert.Null(ClaudeDesktop.Select(plain, t => t.AccountId == "someone-else"));
    }

    [Fact]
    public void Bad_entries_are_skipped_not_fatal()
    {
        var plain = "{\"bad\":null,\"weird\":[1],"
            + Entry(A, "far-future", 253402300800000).Replace("far-future\"", "far-future\"") + ","
            + Entry(A, "good", 1790000000000) + "}";
        var t = ClaudeDesktop.Select(plain);
        Assert.NotNull(t);
    }
}

public class StartupHoldTests
{
    sealed class Never : IUsageProvider
    {
        public ProviderId Id => ProviderId.Claude;
        public Task<ProviderSnapshot> FetchAsync(CancellationToken ct) => throw new InvalidOperationException("must not be called during the hold");
    }

    [Fact]
    public async Task A_startup_hold_keeps_the_saved_reading()
    {
        using var poller = new Poller([new Never()], TimeSpan.FromMinutes(10));
        poller.Seed(new ProviderSnapshot(ProviderId.Claude, SnapshotStatus.Ok,
            [new UsageWindow(WindowKind.Weekly, "Weekly", 40, DateTimeOffset.Now.AddDays(1), TimeSpan.FromDays(7))], DateTimeOffset.Now, Stale: true, Account: "acct"));
        poller.Hold("Claude", DateTimeOffset.Now.AddMinutes(5));
        poller.Start();
        await Task.Delay(200);
        var s = poller.Latest("Claude")!;
        Assert.Equal(SnapshotStatus.RateLimited, s.Status);
        Assert.Single(s.Windows);
    }
}
