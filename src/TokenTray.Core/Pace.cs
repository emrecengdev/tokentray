namespace TokenTray.Core;

public enum PaceState { Unknown, Comfortable, OnPace, Hot, Exhausted }

/// <summary>
/// Compares how much of a window has been used against how much of it has elapsed.
/// Using 60% of the allowance 30% of the way through the week is running hot.
/// </summary>
public readonly record struct Pace(PaceState State, double ElapsedPercent, DateTimeOffset? ProjectedEmptyAt)
{
    public static Pace Of(UsageWindow window, DateTimeOffset now)
    {
        if (window.UsedPercent >= 100) return new(PaceState.Exhausted, Elapsed(window, now) ?? 100, now);
        if (Elapsed(window, now) is not { } elapsed) return new(PaceState.Unknown, 0, null);

        DateTimeOffset? emptyAt = null;
        // A few minutes into a window, one burst would extrapolate wildly; wait for a real trend.
        if (window.UsedPercent >= 10 && elapsed >= 10 && window.StartsAt is { } start)
        {
            var spent = now - start;
            var ratePerTick = window.UsedPercent / Math.Max(spent.Ticks, 1);
            var at = now + TimeSpan.FromTicks((long)((100 - window.UsedPercent) / ratePerTick));
            if (at < window.ResetsAt) emptyAt = at;
        }

        var delta = window.UsedPercent - elapsed;
        var state = delta switch
        {
            > 10 => PaceState.Hot,
            < -15 => PaceState.Comfortable,
            _ => PaceState.OnPace,
        };
        // Early in a window, a small burst reads as "hot" without meaning much.
        if (state == PaceState.Hot && emptyAt is null && window.UsedPercent < 50) state = PaceState.OnPace;
        return new(state, elapsed, emptyAt);
    }

    static double? Elapsed(UsageWindow w, DateTimeOffset now)
    {
        if (w.StartsAt is not { } start || w.Length is not { } length || length <= TimeSpan.Zero) return null;
        return Math.Clamp((now - start) / length * 100, 0, 100);
    }
}

public enum VerdictKind { Unknown, Relaxed, Tight, High, RunsOut, Locked }

/// <summary>
/// One sentence per provider: will the allowance last until it refills?
/// Based only on what the service reports, projected linearly from the start of each window.
/// </summary>
public readonly record struct Verdict(VerdictKind Kind, UsageWindow? Window, DateTimeOffset? At)
{
    public static Verdict Of(ProviderSnapshot snap, DateTimeOffset now)
    {
        var live = snap.Windows.Where(w => w.ResetsAt is not { } r || r > now).ToList();
        if (live.Count == 0) return new(VerdictKind.Unknown, null, null);

        // A full window blocks everything until it reopens; the latest reopening is what matters.
        var locked = live.Where(w => w.UsedPercent >= 100).OrderByDescending(w => w.ResetsAt ?? DateTimeOffset.MaxValue).FirstOrDefault();
        if (locked != null) return new(VerdictKind.Locked, locked, locked.ResetsAt);

        var runsOut = live.Select(w => (w, p: Pace.Of(w, now)))
                          .Where(x => x.p.ProjectedEmptyAt != null)
                          .OrderBy(x => x.p.ProjectedEmptyAt)
                          .FirstOrDefault();
        if (runsOut.w != null) return new(VerdictKind.RunsOut, runsOut.w, runsOut.p.ProjectedEmptyAt);

        var tight = live.Where(w => w.UsedPercent >= 80).OrderByDescending(w => w.UsedPercent).FirstOrDefault();
        if (tight != null)
        {
            // "It'll last" needs a measured pace. Without one (no window length, or too early to tell), just say how full it is.
            var pace = Pace.Of(tight, now);
            var measured = pace.State != PaceState.Unknown && pace.ElapsedPercent >= 10;
            return new(measured ? VerdictKind.Tight : VerdictKind.High, tight, tight.ResetsAt);
        }

        var soonest = live.Where(w => w.ResetsAt != null).OrderBy(w => w.ResetsAt).FirstOrDefault();
        return new(VerdictKind.Relaxed, soonest, soonest?.ResetsAt);
    }
}
