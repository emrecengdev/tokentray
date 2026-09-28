using TokenTray.Core;

namespace TokenTray;

/// <summary>A usage window resolved for display: what the bar shows, where the notch sits, and in which color.</summary>
internal readonly record struct Reading(UsageWindow Window, Pace Pace, double Percent, double Fraction, double Notch, bool PastReset)
{
    public static Reading Of(UsageWindow w, bool remaining, DateTimeOffset now)
    {
        var pace = Pace.Of(w, now);
        var pastReset = w.ResetsAt is { } r && r <= now;
        var percent = remaining ? w.RemainingPercent : Math.Clamp(w.UsedPercent, 0, 100);
        double notch = pace.State == PaceState.Unknown || pastReset ? double.NaN
            : remaining ? 1 - pace.ElapsedPercent / 100 : pace.ElapsedPercent / 100;
        return new(w, pace, percent, percent / 100, notch, pastReset);
    }

    public TimeSpan? Until(DateTimeOffset now) => Window.ResetsAt is { } r ? r - now : null;
}
