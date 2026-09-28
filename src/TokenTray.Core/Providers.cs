using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TokenTray.Core;

public interface IUsageProvider
{
    ProviderId Id { get; }
    /// <summary>Unique per source; the provider name unless this is an extra account.</summary>
    string Key => Id.ToString();
    /// <summary>
    /// The account the last attempt read from local credentials, before any request. Failures the
    /// provider can't report itself (network errors, 429) carry it, so they never inherit another
    /// account's numbers.
    /// </summary>
    string? LastAccount => null;
    Task<ProviderSnapshot> FetchAsync(CancellationToken ct);
}

/// <summary>Thrown when the service asks us to back off.</summary>
public sealed class RateLimitedException(TimeSpan? retryAfter) : Exception("Rate limited")
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public static class Http
{
    public static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
    })
    { Timeout = TimeSpan.FromSeconds(20) };

    public static TimeSpan? RetryAfter(HttpResponseMessage r)
    {
        var ra = r.Headers.RetryAfter;
        TimeSpan? value = ra?.Delta ?? (ra?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
        return value is { } v ? TimeSpan.FromSeconds(Math.Clamp(v.TotalSeconds, 30, 24 * 3600)) : null;
    }

    /// <summary>What a non-success response means for a usage poll.</summary>
    public enum Outcome { Ok, Auth, Failed }

    /// <summary>Sends, throws on 429, and classifies everything else.</summary>
    public static async Task<(Outcome outcome, int status, string body)> Send(HttpRequestMessage req, CancellationToken ct)
    {
        using var res = await Client.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.TooManyRequests) throw new RateLimitedException(RetryAfter(res));
        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return (Outcome.Auth, (int)res.StatusCode, body);
        return (res.IsSuccessStatusCode ? Outcome.Ok : Outcome.Failed, (int)res.StatusCode, body);
    }

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v.Trim() : null;

    /// <summary>Reads a file another process may be rewriting: shared access, brief retries.</summary>
    public static async Task<string> ReadShared(string path, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                return await sr.ReadToEndAsync(ct);
            }
            catch (IOException) when (attempt < 3) { await Task.Delay(150, ct); }
        }
    }

    public static DateTimeOffset? Time(JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)) return t;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n)) return Unix(n);
        return null;
    }

    /// <summary>Seconds or milliseconds since 1970, or null when outside what a date can hold.</summary>
    public static DateTimeOffset? Unix(double n)
    {
        var ms = n > 100_000_000_000 ? n : n * 1000;
        return ms > 0 && ms < 253_402_300_799_000 ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms) : null;
    }

    public static double? Number(JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n)) return n;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && double.IsFinite(s)) return s;
        return null;
    }

    public static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static string Title(string s) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
}

/// <summary>
/// Claude Code subscription usage. Reads the CLI's own OAuth credentials without
/// modifying them, and never refreshes the token itself: the CLI owns that file.
/// </summary>
public sealed class ClaudeProvider(string? configDir = null, string? key = null, string? name = null, Func<Func<ClaudeDesktop.Token, bool>, ClaudeDesktop.Token?>? desktopToken = null) : IUsageProvider
{
    readonly Func<Func<ClaudeDesktop.Token, bool>, ClaudeDesktop.Token?> desktop = desktopToken ?? ClaudeDesktop.Read;
    const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    public ProviderId Id => ProviderId.Claude;
    public string Key => key ?? Id.ToString();
    public string? LastAccount { get; private set; }

    public static string DefaultConfigDir() =>
        Http.Env("CLAUDE_CONFIG_DIR") ?? Path.Combine(Http.Home, ".claude");

    public static bool IsInstalled() => File.Exists(Path.Combine(DefaultConfigDir(), ".credentials.json")) || ClaudeDesktop.ConfigPaths().Any(File.Exists);

    ProviderSnapshot Tag(ProviderSnapshot s, string? account, string? email) =>
        s with { Key = key, Name = name, Account = account ?? s.Account, Email = email ?? s.Email };

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        var dir = configDir ?? DefaultConfigDir();
        LastAccount = null;
        var (uuid, email) = await AccountOf(dir, ct);
        var account = uuid ?? email;
        LastAccount = account;

        // Candidate 1: the CLI's credentials file.
        string? token = null; DateTimeOffset? expires = null; string? plan = null; var cliFound = false;
        var path = Path.Combine(dir, ".credentials.json");
        if (File.Exists(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(await Http.ReadShared(path, ct));
                var o = doc.RootElement.GetProperty("claudeAiOauth");
                token = o.GetProperty("accessToken").GetString();
                expires = o.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeMilliseconds(e.GetInt64()) : null;
                plan = PlanName(Http.Str(o, "subscriptionType"), Http.Str(o, "rateLimitTier"));
                cliFound = token != null;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
        }

        // Candidate 2: the desktop app's sign-in, which stays fresh while the app runs. Only for the
        // default config. With a CLI sign-in present, the desktop token must be the same account,
        // verified by uuid; without one, the desktop sign-in stands on its own (its own id and plan).
        var cliUsable = token != null && (expires is not { } x || x > DateTimeOffset.UtcNow.AddSeconds(30));
        ClaudeDesktop.Token? Desktop(string? except) =>
            configDir is not null || (cliFound && uuid is null) ? null :
            desktop(t => (!cliFound || string.Equals(t.AccountId, uuid, StringComparison.OrdinalIgnoreCase))
                         && (t.ExpiresAt is not { } dx || dx > DateTimeOffset.UtcNow.AddSeconds(30))
                         && t.AccessToken != except);
        void Use(ClaudeDesktop.Token d)
        {
            token = d.AccessToken; expires = d.ExpiresAt;
            if (!cliFound) { plan = d.Plan; account = d.AccountId; email = null; }
            else plan ??= d.Plan;
            LastAccount = account;
        }

        if (!cliUsable)
        {
            if (Desktop(null) is { } d) Use(d);
            else if (!cliFound && !ClaudeDesktop.ConfigPaths().Any(File.Exists))
                return Tag(ProviderSnapshot.Failed(Id, SnapshotStatus.NotSignedIn,
                    "No Claude Code sign-in found. Run `claude` and sign in with your subscription."), account, email);
            else
                return Tag(ProviderSnapshot.Failed(Id, SnapshotStatus.TokenExpired,
                    "Claude sign-in expired. Open Claude Code once and it will refresh.") with { Plan = plan }, account, email);
        }

        var (outcome, status, body) = await Request(token!, ct);
        // The server has the last word on a token. If it refuses the CLI's, try the desktop's once.
        if (outcome == Http.Outcome.Auth && Desktop(token) is { } retry)
        {
            Use(retry);
            (outcome, status, body) = await Request(token!, ct);
        }
        if (outcome == Http.Outcome.Auth)
            return Tag(ProviderSnapshot.Failed(Id, SnapshotStatus.TokenExpired,
                "Claude rejected the sign-in. Open Claude Code once to refresh it.") with { Plan = plan }, account, email);
        if (outcome == Http.Outcome.Failed)
            return Tag(ProviderSnapshot.Failed(Id, SnapshotStatus.Error, $"Claude usage service returned {status}."), account, email);

        return Tag(Parse(body, DateTimeOffset.Now) with { Plan = plan }, account, email);
    }

    static async Task<(Http.Outcome, int, string)> Request(string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        req.Headers.UserAgent.ParseAdd("TokenTray/1.0");
        return await Http.Send(req, ct);
    }

    /// <summary>The signed-in account (uuid, email), from the CLI's global config beside the credentials.</summary>
    static async Task<(string? uuid, string? email)> AccountOf(string configDir, CancellationToken ct)
    {
        // Default layout keeps ~/.claude.json next to ~/.claude; a custom CLAUDE_CONFIG_DIR keeps it inside.
        var candidates = new[] { Path.Combine(configDir, ".claude.json"), Path.Combine(Path.GetDirectoryName(configDir.TrimEnd('\\', '/')) ?? configDir, ".claude.json") };
        foreach (var file in candidates.Where(File.Exists))
        {
            try
            {
                using var doc = JsonDocument.Parse(await Http.ReadShared(file, ct));
                if (doc.RootElement.TryGetProperty("oauthAccount", out var a))
                    return (Http.Str(a, "accountUuid"), Http.Str(a, "emailAddress"));
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        }
        return (null, null);
    }

    public static ProviderSnapshot Parse(string json, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var windows = new List<UsageWindow>();

        void Add(string prop, WindowKind kind, string label, TimeSpan length)
        {
            if (!root.TryGetProperty(prop, out var w) || w.ValueKind != JsonValueKind.Object) return;
            if (Http.Number(w, "utilization") is not { } u) return;
            windows.Add(new UsageWindow(kind, label, Math.Clamp(u, 0, 100), Http.Time(w, "resets_at"), length));
        }

        Add("five_hour", WindowKind.Session, "5-hour", TimeSpan.FromHours(5));
        Add("seven_day", WindowKind.Weekly, "Weekly", TimeSpan.FromDays(7));
        Add("seven_day_opus", WindowKind.WeeklyModel, "Weekly · Opus", TimeSpan.FromDays(7));
        Add("seven_day_sonnet", WindowKind.WeeklyModel, "Weekly · Sonnet", TimeSpan.FromDays(7));

        // Newer responses list model-scoped weekly limits under "limits".
        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in limits.EnumerateArray())
            {
                if (Http.Str(l, "kind") == "weekly_scoped"
                    && Http.Number(l, "percent") is { } p
                    && l.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.Object
                    && sc.TryGetProperty("model", out var m) && Http.Str(m, "display_name") is { Length: > 0 } modelName)
                {
                    var label = $"Weekly · {modelName}";
                    if (windows.Any(w => w.Label == label)) continue;
                    windows.Add(new UsageWindow(WindowKind.WeeklyModel, label, p, Http.Time(l, "resets_at"), TimeSpan.FromDays(7)));
                }
            }
        }

        return windows.Count == 0
            ? ProviderSnapshot.Failed(ProviderId.Claude, SnapshotStatus.Error, "Claude returned no usage windows for this account.")
            : new ProviderSnapshot(ProviderId.Claude, SnapshotStatus.Ok, windows, now);
    }

    /// <summary>"max" + "default_claude_max_20x" → "Max 20x".</summary>
    public static string? PlanName(string? raw, string? tier)
    {
        var name = PlanName(raw);
        var m = tier is null ? null : System.Text.RegularExpressions.Regex.Match(tier, @"max_(\d+)x");
        return m is { Success: true } ? $"Max {m.Groups[1].Value}x" : name;
    }

    static string? PlanName(string? raw) => raw?.ToLowerInvariant() switch
    {
        null or "" => null,
        "max" => "Max",
        "pro" => "Pro",
        "team" => "Team",
        "enterprise" => "Enterprise",
        var s => Http.Title(s),
    };
}

/// <summary>Codex (ChatGPT sign-in) usage, from the CLI's auth.json. Read-only.</summary>
public sealed class CodexProvider(string? home = null, string? key = null, string? name = null) : IUsageProvider
{
    const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    public ProviderId Id => ProviderId.Codex;
    public string Key => key ?? Id.ToString();
    public string? LastAccount { get; private set; }

    public static string DefaultHome() => Http.Env("CODEX_HOME") ?? Path.Combine(Http.Home, ".codex");
    public static bool IsInstalled() => File.Exists(Path.Combine(DefaultHome(), "auth.json"));

    ProviderSnapshot Tag(ProviderSnapshot s, string? account = null) => s with { Key = key, Name = name, Account = account ?? s.Account };

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        var path = Path.Combine(home ?? DefaultHome(), "auth.json");
        LastAccount = null;
        if (!File.Exists(path))
            return Tag(ProviderSnapshot.Failed(Id, SnapshotStatus.NotSignedIn,
                "No Codex sign-in file found. Run `codex login`. Keyring-only sign-ins aren't supported yet."));

        string token; string? account;
        try
        {
            using var doc = JsonDocument.Parse(await Http.ReadShared(path, ct));
            if (!doc.RootElement.TryGetProperty("tokens", out var t) || t.ValueKind != JsonValueKind.Object)
                return Tag(ProviderSnapshot.Failed(Id, SnapshotStatus.NotSignedIn,
                    "Codex is using an API key, which has no plan limits to show. Sign in with ChatGPT to track them."));
            token = t.GetProperty("access_token").GetString() ?? throw new FormatException();
            account = Http.Str(t, "account_id");
            LastAccount = account;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            return Tag(ProviderSnapshot.Failed(Id, SnapshotStatus.NotSignedIn, "Codex credentials are unreadable. Run `codex login`."));
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.UserAgent.ParseAdd("codex-cli");
        if (!string.IsNullOrEmpty(account)) req.Headers.Add("ChatGPT-Account-Id", account);
        var (outcome, status, body) = await Http.Send(req, ct);
        if (outcome == Http.Outcome.Auth)
            return Tag(ProviderSnapshot.Failed(Id, SnapshotStatus.TokenExpired, "Codex sign-in expired. Run any `codex` command to refresh it."), account);
        if (outcome == Http.Outcome.Failed)
            return Tag(ProviderSnapshot.Failed(Id, SnapshotStatus.Error, $"Codex usage service returned {status}."), account);

        // Isolate by the account id from auth.json, the same id failures carry; the email is display-only.
        var snap = Parse(body, DateTimeOffset.Now);
        return Tag(snap, account ?? snap.Account);
    }

    public static ProviderSnapshot Parse(string json, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var windows = new List<UsageWindow>();
        var plan = Http.Str(root, "plan_type") is { } pt ? Http.Title(pt) : null;
        var email = Http.Str(root, "email");
        var account = Http.Str(root, "account_id") ?? email;

        if (root.TryGetProperty("rate_limit", out var rl) && rl.ValueKind == JsonValueKind.Object)
        {
            foreach (var slot in new[] { "primary_window", "secondary_window" })
            {
                if (!rl.TryGetProperty(slot, out var w) || w.ValueKind != JsonValueKind.Object) continue;
                if (Http.Number(w, "used_percent") is not { } u) continue;
                TimeSpan? length = Http.Number(w, "limit_window_seconds") is { } lw ? TimeSpan.FromSeconds(lw) : null;
                var resets = Http.Time(w, "reset_at") ?? (Http.Number(w, "reset_after_seconds") is { } rs ? now + TimeSpan.FromSeconds(rs) : null);

                // Slots aren't stable: assign by window length, not by position.
                var weekly = length is { } l ? l >= TimeSpan.FromDays(1) : slot == "secondary_window";
                windows.Add(new UsageWindow(
                    weekly ? WindowKind.Weekly : WindowKind.Session,
                    weekly ? "Weekly" : length is { } s ? $"{s.TotalHours:0}-hour" : "Session",
                    Math.Clamp(u, 0, 100), resets, length));
            }
        }

        return windows.Count == 0
            ? ProviderSnapshot.Failed(ProviderId.Codex, SnapshotStatus.Error, "Codex returned no usage windows for this account.") with { Plan = plan, Account = account, Email = email }
            : new ProviderSnapshot(ProviderId.Codex, SnapshotStatus.Ok, windows.OrderBy(w => w.Length).ToList(), now, plan, Account: account, Email: email);
    }
}
