using System.IO;
using System.Text.Json;
using TokenTray.Core;

namespace TokenTray;

/// <summary>Low-allowance and refill notices, each shown once per window cycle — across restarts too.</summary>
internal sealed class Notifier(TrayIcon tray)
{
    static readonly double[] Thresholds = [20, 5];
    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TokenTray", "notified.json");

    readonly Dictionary<string, long> fired = Load();
    readonly Dictionary<string, UsageWindow> previous = [];

    public void Observe(ProviderSnapshot snap)
    {
        if (snap.Stale || snap.Status != SnapshotStatus.Ok) return;
        var now = DateTimeOffset.Now;
        var changed = false;

        foreach (var w in snap.Windows)
        {
            // Keyed by source, account and pool so switching accounts never suppresses or repeats a notice.
            var id = $"{snap.Source}|{snap.Account}|{w.Group}|{w.Label}";
            // Reset times jitter by milliseconds between responses; the hour identifies the cycle.
            var cycle = w.ResetsAt is { } r ? r.ToUnixTimeSeconds() / 3600 : 0;
            bool Fire(string what) { if (!fired.TryAdd($"{id}|{cycle}|{what}", cycle)) return false; changed = true; return true; }

            if (previous.TryGetValue(id, out var before) && before.UsedPercent >= 80 && w.UsedPercent < 10 && Fire("refill"))
                tray.Notify(S.RefilledTitle(S.Label(snap)), S.RefilledBody(w));
            previous[id] = w;

            if ((w.Kind == WindowKind.WeeklyModel || w.Group != null) && w.UsedPercent == 0) continue;
            // Mark every threshold actually crossed, but show one notice for them together.
            var crossed = Thresholds.Where(t => w.RemainingPercent <= t).Select(t => Fire(t.ToString())).ToList();
            if (crossed.Contains(true))
                tray.Notify(S.LowTitle(S.Label(snap), w.RemainingPercent), S.LowBody(w, now));
        }
        if (changed) Save();
    }

    static Dictionary<string, long> Load()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(FilePath)) ?? []; }
        catch { return []; }
    }

    void Save()
    {
        try
        {
            // Forget cycles that ended more than a day ago.
            var cutoff = DateTimeOffset.Now.AddDays(-1).ToUnixTimeSeconds() / 3600;
            foreach (var key in fired.Where(kv => kv.Value != 0 && kv.Value < cutoff).Select(kv => kv.Key).ToList()) fired.Remove(key);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(fired));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
