using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace TokenTray.Core;

/// <summary>
/// Google Antigravity. The IDE keeps its Google sign-in in Windows Credential Manager
/// ("gemini:antigravity") and refreshes it while it runs. We only read it: an expired
/// token means "open Antigravity", never a refresh from here.
/// </summary>
public sealed class AntigravityProvider(Func<string?>? readCredential = null) : IUsageProvider
{
    const string Target = "gemini:antigravity";
    static readonly string[] Endpoints = ["https://daily-cloudcode-pa.googleapis.com", "https://cloudcode-pa.googleapis.com"];
    public ProviderId Id => ProviderId.Antigravity;
    public string? LastAccount { get; private set; }

    public static bool IsInstalled() => CredentialStore.Read(Target) != null;

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        LastAccount = null;
        var raw = (readCredential ?? (() => CredentialStore.Read(Target)))();
        if (raw is null)
            return ProviderSnapshot.Failed(Id, SnapshotStatus.NotSignedIn, "No Antigravity sign-in found. Open Antigravity and sign in with Google.");

        string token; DateTimeOffset? expiry; string? account;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var t = doc.RootElement.GetProperty("token");
            token = Http.Str(t, "access_token") ?? "";
            expiry = Http.Time(t, "expiry");
            account = Jwt.Claim(Http.Str(doc.RootElement, "id_token"), "email");
            LastAccount = account;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return ProviderSnapshot.Failed(Id, SnapshotStatus.NotSignedIn, "Antigravity's stored sign-in is unreadable. Sign in again in Antigravity.");
        }

        if (token.Length == 0 || expiry is { } e && e <= DateTimeOffset.UtcNow.AddSeconds(60))
            return ProviderSnapshot.Failed(Id, SnapshotStatus.TokenExpired, "Antigravity's sign-in has expired. Open Antigravity once and it will refresh.") with { Account = account };

        var auth = false; var lastError = "Antigravity quota service didn't answer.";
        foreach (var baseUrl in Endpoints)
        {
            var (o1, s1, load) = await Post(baseUrl + "/v1internal:loadCodeAssist", """{"metadata":{"ideType":"ANTIGRAVITY"}}""", token, ct);
            if (o1 == Http.Outcome.Auth) { auth = true; continue; }
            if (o1 != Http.Outcome.Ok) { lastError = $"Antigravity returned {s1}."; continue; }

            string? project; string? plan;
            using (var doc = JsonDocument.Parse(load))
            {
                project = Http.Str(doc.RootElement, "cloudaicompanionProject");
                plan = TierName(doc.RootElement, "paidTier") ?? TierName(doc.RootElement, "currentTier");
            }
            if (project is null) { lastError = "Antigravity has no quota project for this account."; continue; }

            var (o2, s2, summary) = await Post(baseUrl + "/v1internal:retrieveUserQuotaSummary", JsonSerializer.Serialize(new { project }), token, ct);
            if (o2 == Http.Outcome.Auth) { auth = true; continue; }
            if (o2 != Http.Outcome.Ok) { lastError = $"Antigravity quota summary returned {s2}."; continue; }
            return Parse(summary, DateTimeOffset.Now) with { Plan = plan, Account = account, Email = account };
        }
        return auth
            ? ProviderSnapshot.Failed(Id, SnapshotStatus.TokenExpired, "Antigravity rejected the sign-in. Open Antigravity once to refresh it.") with { Account = account }
            : ProviderSnapshot.Failed(Id, SnapshotStatus.Error, lastError) with { Account = account };
    }

    static string? TierName(JsonElement root, string prop) =>
        root.TryGetProperty(prop, out var t) && t.ValueKind == JsonValueKind.Object ? Http.Str(t, "name") : null;

    static async Task<(Http.Outcome, int, string)> Post(string url, string json, string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.UserAgent.ParseAdd("antigravity");
        return await Http.Send(req, ct);
    }

    /// <summary>
    /// Groups share a five-hour and a weekly limit. The Gemini group is the main allowance;
    /// others (Claude and GPT models) become secondary pools.
    /// </summary>
    public static ProviderSnapshot Parse(string json, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        var windows = new List<UsageWindow>();
        if (doc.RootElement.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            var list = groups.EnumerateArray().ToList();
            var main = list.FindIndex(IsGemini);
            if (main < 0) main = 0;
            for (var i = 0; i < list.Count; i++)
            {
                var g = list[i];
                string? group = i == main ? null : ShortGroup(Http.Str(g, "displayName") ?? $"Group {i + 1}");
                if (!g.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array) continue;
                foreach (var b in buckets.EnumerateArray())
                {
                    if (Http.Number(b, "remainingFraction") is not { } rem) continue;
                    var used = Math.Clamp((1 - Math.Clamp(rem, 0, 1)) * 100, 0, 100);
                    var reset = Http.Time(b, "resetTime");
                    switch (Http.Str(b, "window")?.ToLowerInvariant())
                    {
                        case "5h": windows.Add(new UsageWindow(WindowKind.Session, "5-hour", used, reset, TimeSpan.FromHours(5), group)); break;
                        case "weekly": windows.Add(new UsageWindow(WindowKind.Weekly, "Weekly", used, reset, TimeSpan.FromDays(7), group)); break;
                    }
                }
            }
        }
        return windows.Count == 0
            ? ProviderSnapshot.Failed(ProviderId.Antigravity, SnapshotStatus.Error, "Antigravity returned no quota for this account.")
            : new ProviderSnapshot(ProviderId.Antigravity, SnapshotStatus.Ok, windows, now);
    }

    static bool IsGemini(JsonElement g) =>
        (Http.Str(g, "displayName") ?? "").Contains("gemini", StringComparison.OrdinalIgnoreCase) ||
        (Http.Str(g, "description") ?? "").Contains("gemini", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Claude and GPT models" → "Claude and GPT".</summary>
    static string ShortGroup(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, @"\s*models?$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
}

/// <summary>Grok Build: the CLI's own session and the billing endpoint behind its /usage panel.</summary>
public sealed class GrokProvider(Func<string?>? authPath = null) : IUsageProvider
{
    const string BillingUrl = "https://cli-chat-proxy.grok.com/v1/billing?format=credits";
    public ProviderId Id => ProviderId.Grok;
    public string? LastAccount { get; private set; }

    public static string DefaultAuthPath() => Path.Combine(Http.Env("GROK_HOME") ?? Path.Combine(Http.Home, ".grok"), "auth.json");
    public static bool IsInstalled() => File.Exists(DefaultAuthPath());

    static readonly Lazy<string> ClientVersion = new(() =>
    {
        if (Http.Env("GROK_CLIENT_VERSION") is { } v) return v;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c grok --version")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            if (p is null) return "1.0.0";
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return "1.0.0"; }
            var m = System.Text.RegularExpressions.Regex.Match(output.Result, @"\d+\.\d+\.\d+");
            return m.Success ? m.Value : "1.0.0";
        }
        catch { return "1.0.0"; }
    });

    public record Session(string Token, string? UserId, string? Email, DateTimeOffset? ExpiresAt);

    /// <summary>Only xAI sign-ins count; enterprise IdP tokens and API keys are skipped.</summary>
    public static Session? SelectSession(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        return doc.RootElement.EnumerateObject()
            .Where(p => p.Name == "https://accounts.x.ai/sign-in" ||
                        (p.Name.StartsWith("https://auth.x.ai::", StringComparison.Ordinal) && p.Name.Length > "https://auth.x.ai::".Length))
            .Select(p => p.Value)
            .Where(v => v.ValueKind == JsonValueKind.Object && !string.IsNullOrWhiteSpace(Http.Str(v, "key")))
            .Select(v => new Session(Http.Str(v, "key")!.Trim(), Http.Str(v, "user_id")?.Trim(), Http.Str(v, "email"), Http.Time(v, "expires_at")))
            .OrderByDescending(s => s.ExpiresAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
    }

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        var path = (authPath ?? DefaultAuthPath)();
        if (path is null || !File.Exists(path))
            return ProviderSnapshot.Failed(Id, SnapshotStatus.NotSignedIn, "No Grok sign-in found. Run `grok login`.");

        LastAccount = null;
        Session? s;
        try { s = SelectSession(await Http.ReadShared(path, ct)); }
        catch (JsonException) { s = null; }
        LastAccount = s?.Email;
        if (s is null)
            return ProviderSnapshot.Failed(Id, SnapshotStatus.NotSignedIn, "No xAI sign-in in Grok's auth file. Run `grok login`.");
        if (s.ExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow)
            return ProviderSnapshot.Failed(Id, SnapshotStatus.TokenExpired, "Grok sign-in expired. Run `grok` once and it will refresh.") with { Account = s.Email };

        using var req = new HttpRequestMessage(HttpMethod.Get, BillingUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Token);
        req.Headers.Add("X-XAI-Token-Auth", "xai-grok-cli");
        req.Headers.Add("x-grok-client-mode", "cli");
        req.Headers.Add("x-grok-client-version", ClientVersion.Value);
        if (!string.IsNullOrEmpty(s.UserId)) req.Headers.Add("x-userid", s.UserId);
        var (outcome, status, body) = await Http.Send(req, ct);
        if (outcome == Http.Outcome.Auth)
            return ProviderSnapshot.Failed(Id, SnapshotStatus.TokenExpired, "Grok rejected the sign-in. Run `grok` once to refresh it.") with { Account = s.Email };
        if (outcome == Http.Outcome.Failed)
            return ProviderSnapshot.Failed(Id, SnapshotStatus.Error, $"Grok billing returned {status}.") with { Account = s.Email };
        return Parse(body, DateTimeOffset.Now) with { Account = s.Email, Email = s.Email };
    }

    /// <summary>One shared pool per billing period, weekly or monthly.</summary>
    public static ProviderSnapshot Parse(string json, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var plan = Http.Str(root, "subscriptionTier") is { Length: > 0 } tier
            ? Http.Title(tier.Replace("SUBSCRIPTION_TIER_", "").Replace('_', ' ')) : null;
        if (!root.TryGetProperty("config", out var config) || Http.Number(config, "creditUsagePercent") is not { } pct)
            return ProviderSnapshot.Failed(ProviderId.Grok, SnapshotStatus.Error, "Grok returned no usage for this account.") with { Plan = plan };

        DateTimeOffset? end = null; var type = "";
        if (config.TryGetProperty("currentPeriod", out var period) && period.ValueKind == JsonValueKind.Object)
        {
            end = Http.Time(period, "end");
            type = (Http.Str(period, "type") ?? Http.Str(period, "periodType") ?? "").ToLowerInvariant();
        }
        var window = type.Contains("week")
            ? new UsageWindow(WindowKind.Weekly, "Weekly", Math.Clamp(pct, 0, 100), end, TimeSpan.FromDays(7))
            : new UsageWindow(WindowKind.Period, type.Contains("month") ? "Monthly" : "Billing period", Math.Clamp(pct, 0, 100), end,
                type.Contains("month") ? TimeSpan.FromDays(30) : null);
        return new ProviderSnapshot(ProviderId.Grok, SnapshotStatus.Ok, [window], now, plan);
    }
}

/// <summary>Cursor: the IDE's stored session, read from its state database, and the dashboard's usage summary.</summary>
public sealed class CursorProvider(Func<string?>? readToken = null) : IUsageProvider
{
    const string SummaryUrl = "https://cursor.com/api/usage-summary";
    public ProviderId Id => ProviderId.Cursor;
    public string? LastAccount { get; private set; }

    static string DbPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor", "User", "globalStorage", "state.vscdb");
    public static bool IsInstalled() => Http.Env("CURSOR_SESSION_TOKEN") != null || File.Exists(DbPath);

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        var raw = (readToken ?? ReadToken)();
        var cookie = raw is null ? null : SessionCookie(raw);
        LastAccount = cookie?.Split("%3A%3A")[0];
        if (cookie is null)
            return ProviderSnapshot.Failed(Id, SnapshotStatus.NotSignedIn, "No Cursor sign-in found. Open Cursor and sign in.");

        using var req = new HttpRequestMessage(HttpMethod.Get, SummaryUrl);
        req.Headers.Add("Cookie", $"WorkosCursorSessionToken={cookie}");
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        var (outcome, status, body) = await Http.Send(req, ct);
        var account = cookie.Split("%3A%3A")[0];
        if (outcome == Http.Outcome.Auth)
            return ProviderSnapshot.Failed(Id, SnapshotStatus.TokenExpired, "Cursor rejected the sign-in. Open Cursor once to refresh it.") with { Account = account };
        if (outcome == Http.Outcome.Failed)
            return ProviderSnapshot.Failed(Id, SnapshotStatus.Error, $"Cursor usage returned {status}.") with { Account = account };
        return Parse(body, DateTimeOffset.Now) with { Account = account };
    }

    static string? ReadToken() =>
        Http.Env("CURSOR_SESSION_TOKEN") ?? (File.Exists(DbPath) ? WinSqlite.QueryText(DbPath, "SELECT value FROM ItemTable WHERE key = 'cursorAuth/accessToken'") : null);

    /// <summary>"userId%3A%3AaccessToken", from a raw JWT, "id::jwt", or an already-encoded cookie.</summary>
    public static string? SessionCookie(string token)
    {
        if (token.Contains('\r') || token.Contains('\n')) return null;
        token = token.Trim();
        if (token.StartsWith("WorkosCursorSessionToken=", StringComparison.Ordinal)) token = token["WorkosCursorSessionToken=".Length..].Trim();
        if (token.Length == 0) return null;
        if (token.Contains("%3A%3A")) return token;
        if (token.Contains("::")) return token.Replace("::", "%3A%3A");
        var sub = Jwt.Claim(token, "sub");
        if (sub is null) return null;
        var user = sub.Contains('|') ? sub[(sub.LastIndexOf('|') + 1)..] : sub;
        return $"{user}%3A%3A{token}";
    }

    /// <summary>Included usage for the billing cycle, and API (on-demand) usage as a secondary pool.</summary>
    public static ProviderSnapshot Parse(string json, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var end = Http.Time(root, "billingCycleEnd");
        var windows = new List<UsageWindow>();
        if (root.TryGetProperty("individualUsage", out var iu) && iu.ValueKind == JsonValueKind.Object &&
            iu.TryGetProperty("plan", out var plan) && plan.ValueKind == JsonValueKind.Object)
        {
            if ((Http.Number(plan, "totalPercentUsed") ?? Http.Number(plan, "autoPercentUsed")) is { } total)
                windows.Add(new UsageWindow(WindowKind.Period, "This month", Math.Clamp(total, 0, 100), end, null));
            if (Http.Number(plan, "apiPercentUsed") is { } api)
                windows.Add(new UsageWindow(WindowKind.Period, "This month", Math.Clamp(api, 0, 100), end, null, "API"));
        }
        var planName = Http.Str(root, "membershipType") is { Length: > 0 } m ? Http.Title(m) : null;
        return windows.Count == 0
            ? ProviderSnapshot.Failed(ProviderId.Cursor, SnapshotStatus.Error, "Cursor returned no usage for this account.")
            : new ProviderSnapshot(ProviderId.Cursor, SnapshotStatus.Ok, windows, now, planName);
    }
}

/// <summary>
/// OpenCode Go, read from the console with a workspace id and session cookie the user
/// provides (env vars or %APPDATA%\opencode-go\config.json).
/// </summary>
public sealed class OpenCodeProvider(Func<(string workspace, string cookie)?>? readCredentials = null) : IUsageProvider
{
    const string StatusUrl = "https://opencode.ai/console/api/go/status";
    public ProviderId Id => ProviderId.OpenCode;
    public string? LastAccount { get; private set; }

    static IEnumerable<string> ConfigPaths()
    {
        if (Http.Env("OPENCODE_GO_CONFIG_FILE") is { } f) yield return f;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "opencode-go", "config.json");
        yield return Path.Combine(Http.Home, ".config", "opencode-bar", "opencode-go.json");
        yield return Path.Combine(Http.Home, ".config", "opencode-quota", "opencode-go.json");
    }

    public static bool IsInstalled() => ReadCredentials() != null;

    static (string, string)? ReadCredentials()
    {
        if (Http.Env("OPENCODE_GO_WORKSPACE_ID") is { } w && Http.Env("OPENCODE_GO_AUTH_COOKIE") is { } c) return Valid(w, c);
        foreach (var path in ConfigPaths().Where(File.Exists))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var r = doc.RootElement;
                var ws = Http.Str(r, "workspaceId") ?? Http.Str(r, "workspaceID") ?? Http.Str(r, "workspace_id");
                var ck = Http.Str(r, "authCookie") ?? Http.Str(r, "cookie") ?? Http.Str(r, "auth_cookie");
                if (ws != null && ck != null && Valid(ws, ck) is { } ok) return ok;
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        }
        return null;
    }

    static (string, string)? Valid(string workspace, string cookie) =>
        workspace.Length > 0 && workspace.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-') &&
        cookie.Length > 0 && !cookie.Contains('\r') && !cookie.Contains('\n') ? (workspace, cookie) : null;

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        var creds = (readCredentials ?? (() => ReadCredentials()))();
        LastAccount = creds?.workspace;
        if (creds is not var (workspace, cookie))
            return ProviderSnapshot.Failed(Id, SnapshotStatus.NotSignedIn,
                "OpenCode Go needs a workspace id and session cookie in %APPDATA%\\opencode-go\\config.json.");

        var header = cookie.Split(';').Any(p => p.TrimStart().StartsWith("auth=") || p.TrimStart().StartsWith("__Host-console_session="))
            ? cookie : $"auth={cookie}";
        using var req = new HttpRequestMessage(HttpMethod.Get, StatusUrl);
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Add("x-org-id", workspace);
        req.Headers.Add("Cookie", header);
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36");
        var (outcome, status, body) = await Http.Send(req, ct);
        if (outcome == Http.Outcome.Auth)
            return ProviderSnapshot.Failed(Id, SnapshotStatus.TokenExpired, "OpenCode rejected the session cookie. Copy a fresh one from opencode.ai.") with { Account = workspace };
        if (outcome == Http.Outcome.Failed)
            return ProviderSnapshot.Failed(Id, SnapshotStatus.Error, $"OpenCode returned {status}.") with { Account = workspace };
        return Parse(body, DateTimeOffset.Now) with { Account = workspace };
    }

    public static ProviderSnapshot Parse(string json, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("access", out var access) || access.ValueKind != JsonValueKind.Object)
            return ProviderSnapshot.Failed(ProviderId.OpenCode, SnapshotStatus.Error, "No active OpenCode Go subscription on this workspace.");
        var windows = new List<UsageWindow>();
        if (access.TryGetProperty("meters", out var meters) && meters.ValueKind == JsonValueKind.Object)
        {
            void Add(string name, WindowKind kind, string label, TimeSpan? length, DateTimeOffset? fallbackReset)
            {
                if (!meters.TryGetProperty(name, out var m) || m.ValueKind != JsonValueKind.Object) return;
                if (Http.Number(m, "limitMicroCents") is not { } limit || Http.Number(m, "usedMicroCents") is not { } used) return;
                var pct = limit <= 0 ? 0 : Math.Clamp(used / limit * 100, 0, 100);
                windows.Add(new UsageWindow(kind, label, pct, Http.Time(m, "resetsAt") ?? fallbackReset, length));
            }
            Add("fiveHour", WindowKind.Session, "5-hour", TimeSpan.FromHours(5), null);
            Add("week", WindowKind.Weekly, "Weekly", TimeSpan.FromDays(7), null);
            var start = Http.Time(access, "startsAt"); var end = Http.Time(access, "endsAt");
            Add("month", WindowKind.Period, "Monthly", start is { } s && end is { } e2 ? e2 - s : TimeSpan.FromDays(30), end);
        }
        return windows.Count == 0
            ? ProviderSnapshot.Failed(ProviderId.OpenCode, SnapshotStatus.Error, "OpenCode returned no usage meters.")
            : new ProviderSnapshot(ProviderId.OpenCode, SnapshotStatus.Ok, windows, now, "Go");
    }
}

public static class Jwt
{
    /// <summary>A string claim from a JWT's payload, without verifying it (we only label things with it).</summary>
    public static string? Claim(string? jwt, string claim)
    {
        var parts = jwt?.Split('.');
        if (parts is not { Length: >= 2 }) return null;
        try
        {
            var p = parts[1].Replace('-', '+').Replace('_', '/');
            p += new string('=', (4 - p.Length % 4) % 4);
            using var doc = JsonDocument.Parse(Convert.FromBase64String(p));
            return Http.Str(doc.RootElement, claim);
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }
}

/// <summary>Windows Credential Manager, read-only.</summary>
public static class CredentialStore
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public uint Flags, Type; public IntPtr TargetName, Comment; public long LastWritten;
        public uint CredentialBlobSize; public IntPtr CredentialBlob; public uint Persist, AttributeCount;
        public IntPtr Attributes, TargetAlias, UserName;
    }

    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredReadW(string target, uint type, uint flags, out IntPtr cred);
    [DllImport("advapi32")] static extern void CredFree(IntPtr cred);

    public static string? Read(string target)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!CredReadW(target, 1 /* GENERIC */, 0, out var ptr) || ptr == IntPtr.Zero) return null;
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (c.CredentialBlobSize == 0 || c.CredentialBlob == IntPtr.Zero) return null;
            var bytes = new byte[c.CredentialBlobSize];
            Marshal.Copy(c.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally { CredFree(ptr); }
    }
}

/// <summary>The SQLite that ships with Windows (winsqlite3.dll), for one read-only lookup.</summary>
public static class WinSqlite
{
    [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_step(IntPtr stmt);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_busy_timeout(IntPtr db, int ms);

    const int OPEN_READONLY = 1, ROW = 100;

    /// <summary>First column of the first row, or null. Falls back to a temp copy if the IDE holds a lock.</summary>
    public static string? QueryText(string path, string sql)
    {
        try { return Query(path, sql); }
        catch (IOException) { }
        var copy = Path.Combine(Path.GetTempPath(), $"tokentray-{Guid.NewGuid():N}.vscdb");
        try
        {
            File.Copy(path, copy);
            return Query(copy, sql);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        finally { try { File.Delete(copy); } catch { } }
    }

    static string? Query(string path, string sql)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (sqlite3_open_v2(Utf8(path), out var db, OPEN_READONLY, IntPtr.Zero) != 0) { sqlite3_close(db); throw new IOException("open failed"); }
        try
        {
            sqlite3_busy_timeout(db, 1000);
            if (sqlite3_prepare_v2(db, Utf8(sql), -1, out var stmt, IntPtr.Zero) != 0) throw new IOException("prepare failed");
            try
            {
                var rc = sqlite3_step(stmt);
                if (rc == ROW) return Marshal.PtrToStringUTF8(sqlite3_column_text(stmt, 0));
                if (rc == 5 /* BUSY */ || rc == 6 /* LOCKED */) throw new IOException("locked");
                return null;
            }
            finally { sqlite3_finalize(stmt); }
        }
        finally { sqlite3_close(db); }
    }

    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");
}
