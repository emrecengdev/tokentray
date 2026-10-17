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

    // Gemini 4 Argon: its own weekly-only quota on AI Pro, separate from the Gemini pool.
    const string AntigravityArgonGroupJson = """
    {"groups":[
      {"buckets":[
        {"bucketId":"argon-weekly","window":"weekly","resetTime":"2026-10-03T09:00:00Z","remainingFraction":0.4}],
       "displayName":"Gemini 4 Argon","description":"Models within this group: Gemini 4 Argon"},
      {"buckets":[
        {"bucketId":"gemini-weekly","window":"weekly","resetTime":"2026-10-01T01:34:20Z","remainingFraction":0.85421365},
        {"bucketId":"gemini-5h","window":"5h","resetTime":"2026-09-28T22:38:36Z","remainingFraction":0.9916939}],
       "displayName":"Gemini Models","description":"Models within this group: Gemini Flash, Gemini Pro"},
      {"buckets":[
        {"bucketId":"3p-weekly","window":"weekly","resetTime":"2026-10-05T21:18:22Z","remainingFraction":1}],
       "displayName":"Claude and GPT models"}]}
    """;

    [Fact]
    public void Antigravity_argon_group_is_its_own_pool_even_when_listed_first()
    {
        var s = AntigravityProvider.Parse(AntigravityArgonGroupJson, Now);
        var argon = s.Windows.Single(w => w.Group == "Argon");
        Assert.Equal(WindowKind.Weekly, argon.Kind);
        Assert.Equal(60, argon.UsedPercent, 1);
        Assert.Equal(14.6, s.Get(WindowKind.Weekly)!.UsedPercent, 1);
        Assert.Null(s.Get(WindowKind.Weekly)!.Group);
        Assert.Null(s.Binding!.Group);
        Assert.Equal(14.6, s.Binding.UsedPercent, 1);
    }

    [Fact]
    public void Antigravity_argon_buckets_inside_the_gemini_group_split_out()
    {
        const string json = """
        {"groups":[
          {"buckets":[
            {"bucketId":"gemini-weekly","window":"weekly","resetTime":"2026-10-01T01:34:20Z","remainingFraction":0.9},
            {"bucketId":"gemini-5h","window":"5h","resetTime":"2026-09-28T22:38:36Z","remainingFraction":0.95},
            {"bucketId":"gemini-argon-weekly","window":"weekly","resetTime":"2026-10-03T09:00:00Z","remainingFraction":0.1},
            {"bucketId":"gemini-argon-5h","window":"5h","resetTime":"2026-09-28T23:00:00Z","remainingFraction":0.5}],
           "displayName":"Gemini Models","description":"Models within this group: Gemini Flash, Gemini Pro, Gemini 4 Argon"}]}
        """;
        var s = AntigravityProvider.Parse(json, Now);
        Assert.Equal(10, s.Get(WindowKind.Weekly)!.UsedPercent, 1);
        Assert.Equal(5, s.Get(WindowKind.Session)!.UsedPercent, 1);
        Assert.Equal(90, s.Windows.Single(w => w.Group == "Argon" && w.Kind == WindowKind.Weekly).UsedPercent, 1);
        Assert.Equal(50, s.Windows.Single(w => w.Group == "Argon" && w.Kind == WindowKind.Session).UsedPercent, 1);
        Assert.Null(s.Binding!.Group);
    }

    [Fact]
    public void Antigravity_window_falls_back_to_the_bucket_id()
    {
        var s = AntigravityProvider.Parse("""
        {"groups":[{"displayName":"Gemini 4 Argon","buckets":[
          {"bucketId":"argon-weekly","resetTime":"2026-10-03T09:00:00Z","remainingFraction":0.75}]}]}
        """, Now);
        Assert.Equal(SnapshotStatus.Ok, s.Status);
        var w = s.Windows.Single();
        Assert.Equal((WindowKind.Weekly, "Argon"), (w.Kind, w.Group));
        Assert.Equal(25, w.UsedPercent, 1);
    }

    [Fact]
    public void Antigravity_without_the_gemini_group_never_passes_another_pool_off_as_gemini()
    {
        // daily-cloudcode-pa's answer to a client it doesn't recognise, trimmed from a live response.
        var s = AntigravityProvider.Parse("""
        {"groups":[{"buckets":[
            {"bucketId":"3p-weekly","displayName":"Weekly Limit Remaining","window":"weekly","resetTime":"2026-10-24T14:49:32Z","remainingFraction":0.7},
            {"bucketId":"3p-5h","displayName":"Five Hour Limit Remaining","window":"5h","resetTime":"2026-10-17T19:49:32Z","remainingFraction":1}],
          "displayName":"Claude and GPT models","description":"Models within this group: Claude Opus, Claude Sonnet, GPT-OSS"}]}
        """, Now);
        Assert.Equal(SnapshotStatus.Ok, s.Status);
        Assert.Equal(2, s.Windows.Count(w => w.Group == "Claude and GPT"));
        Assert.Empty(s.Windows.Where(w => w.Group is null));
        Assert.Equal(30, s.Binding!.UsedPercent, 1);
    }

    // The running app's language server, trimmed from a live RetrieveUserQuotaSummary answer.
    const string AntigravityLocalJson = """
    {"response":{"groups":[
      {"displayName":"Gemini Models","description":"Models within this group: Gemini Flash, Gemini Pro","buckets":[
        {"bucketId":"gemini-weekly","displayName":"Weekly Limit Remaining","window":"weekly","remainingFraction":0.9,"resetTime":"2026-10-24T15:17:42Z"},
        {"bucketId":"gemini-5h","displayName":"Five Hour Limit Remaining","window":"5h","remainingFraction":1,"resetTime":"2026-10-17T20:17:42Z"}]},
      {"displayName":"Claude and GPT models","description":"Models within this group: Claude Opus, Claude Sonnet, GPT-OSS","buckets":[
        {"bucketId":"3p-weekly","displayName":"Weekly Limit Remaining","window":"weekly","remainingFraction":1,"resetTime":"2026-10-24T15:17:42Z"},
        {"bucketId":"3p-5h","displayName":"Five Hour Limit Remaining","window":"5h","remainingFraction":1,"resetTime":"2026-10-17T20:17:42Z"}]},
      {"displayName":"Gemini 4 Argon","description":"For paid Pro plans, Argon quota is separate.","buckets":[
        {"bucketId":"gemini-argon-wtu-weekly","displayName":"Gemini 4 Argon Weekly Limit","window":"weekly","remainingFraction":0.35,"resetTime":"2026-10-24T15:17:42Z"}]}]}}
    """;

    [Fact]
    public void Antigravity_local_answer_is_unwrapped_and_shows_argon()
    {
        var s = AntigravityProvider.Parse(AntigravityLocalJson, Now);
        Assert.Equal(10, s.Get(WindowKind.Weekly)!.UsedPercent, 1);
        Assert.Null(s.Get(WindowKind.Weekly)!.Group);
        Assert.Equal(65, s.Windows.Single(w => w.Group == "Argon").UsedPercent, 1);
        Assert.Equal(2, s.Windows.Count(w => w.Group == "Claude and GPT"));
    }

    static string FakeAntigravityCredential(string email)
    {
        static string B64(string json) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var idToken = $"{B64("{}")}.{B64($$"""{"email":"{{email}}"}""")}.sig";
        return $$"""{"token":{"access_token":"x","expiry":"2099-01-01T00:00:00Z"},"id_token":"{{idToken}}"}""";
    }

    [Fact]
    public async Task Antigravity_uses_the_running_app_for_the_same_account()
    {
        var p = new AntigravityProvider(() => FakeAntigravityCredential("me@example.com"),
            _ => Task.FromResult<AntigravityLocal.Answer?>(new(AntigravityLocalJson, "Me@Example.com", "Google AI Pro")));
        var s = await p.FetchAsync(CancellationToken.None);
        Assert.Equal(SnapshotStatus.Ok, s.Status);
        Assert.Equal("Google AI Pro", s.Plan);
        Assert.Equal("Me@Example.com", s.Account);
        Assert.Single(s.Windows.Where(w => w.Group == "Argon"));
    }

    [Fact]
    public void Antigravity_without_buckets_is_an_error()
    {
        Assert.Equal(SnapshotStatus.Error, AntigravityProvider.Parse("""{"groups":[]}""", Now).Status);
    }

    [Fact]
    public void Grok_weekly_and_monthly_periods()
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
    public void Grok_picks_the_newest_xai_session_and_ignores_other_issuers()
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
