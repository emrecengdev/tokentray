namespace TokenTray.Core;

public enum ProviderId { Claude, Codex, Antigravity, Grok, Cursor, OpenCode }

/// <summary>
/// Session: a short rolling window (five hours). Weekly: a seven-day allowance.
/// WeeklyModel: a weekly limit for one model. Period: a billing period or monthly pool.
/// </summary>
public enum WindowKind { Session, Weekly, WeeklyModel, Period }

public enum SnapshotStatus { Ok, Loading, NotSignedIn, TokenExpired, RateLimited, Offline, Error }

/// <summary>One rate-limit window, e.g. Claude's five-hour session or Codex's weekly pool.</summary>
/// <param name="Group">
/// A secondary pool the window belongs to (Antigravity's "Claude and GPT models", Cursor's API usage).
/// Null for the provider's main allowance, which is what the taskbar shows.
/// </param>
public sealed record UsageWindow(
    WindowKind Kind,
    string Label,
    double UsedPercent,
    DateTimeOffset? ResetsAt,
    TimeSpan? Length,
    string? Group = null)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);

    /// <summary>Start of the window, when both reset time and length are known.</summary>
    public DateTimeOffset? StartsAt => ResetsAt is { } r && Length is { } l ? r - l : null;
}

/// <param name="Key">Identifies the source when one provider has several accounts; defaults to the provider name.</param>
/// <param name="Account">Stable id of who the reading belongs to, so switching accounts never mixes data.</param>
/// <param name="Email">The account's email, for display only.</param>
/// <param name="Name">Display name for an extra account ("Claude · work").</param>
public sealed record ProviderSnapshot(
    ProviderId Provider,
    SnapshotStatus Status,
    IReadOnlyList<UsageWindow> Windows,
    DateTimeOffset FetchedAt,
    string? Plan = null,
    string? Message = null,
    bool Stale = false,
    DateTimeOffset? RetryAt = null,
    string? Key = null,
    string? Account = null,
    string? Name = null,
    string? Email = null)
{
    public string Source => Key ?? Provider.ToString();

    public static ProviderSnapshot Failed(ProviderId id, SnapshotStatus status, string message) =>
        new(id, status, [], DateTimeOffset.Now, Message: message);

    /// <summary>The window that runs out first — what the taskbar shows.</summary>
    public UsageWindow? Binding
    {
        get
        {
            // Unused model-specific limits and secondary pools never bind, unless they're all there is.
            var candidates = Windows.Where(w => w.Group is null && (w.Kind != WindowKind.WeeklyModel || w.UsedPercent > 0)).ToList();
            if (candidates.Count == 0) candidates = [.. Windows];
            return candidates.OrderByDescending(w => w.UsedPercent)
                             .ThenBy(w => w.ResetsAt ?? DateTimeOffset.MaxValue)
                             .FirstOrDefault();
        }
    }

    /// <summary>The soonest known reset still ahead, among the given windows.</summary>
    public static DateTimeOffset? NextReset(IEnumerable<UsageWindow?> windows, DateTimeOffset now) =>
        windows.Select(w => w?.ResetsAt).Where(r => r > now).Min();

    /// <summary>The main-pool window of this kind, falling back to any pool.</summary>
    public UsageWindow? Get(WindowKind kind) =>
        Windows.FirstOrDefault(w => w.Kind == kind && w.Group is null) ?? Windows.FirstOrDefault(w => w.Kind == kind);

    /// <summary>The long window the taskbar shows beside the session: weekly, or the billing period.</summary>
    public UsageWindow? Long => Get(WindowKind.Weekly) ?? Get(WindowKind.Period);
}
