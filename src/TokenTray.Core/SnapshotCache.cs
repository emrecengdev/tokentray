using System.Text.Json;

namespace TokenTray.Core;

/// <summary>
/// The last good reading per provider, kept on disk so a restart shows real numbers
/// immediately instead of waiting — or, if the service is throttling us, showing nothing.
/// </summary>
public sealed class SnapshotCache(string path)
{
    static readonly JsonSerializerOptions Json = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    string HoldsPath => Path.Combine(Path.GetDirectoryName(path)!, "holds.json");

    /// <summary>Retry-After deadlines that outlive a restart.</summary>
    public Dictionary<string, DateTimeOffset> LoadHolds()
    {
        try
        {
            var all = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(HoldsPath), Json) ?? [];
            return all.Where(kv => kv.Value > DateTimeOffset.Now).ToDictionary();
        }
        catch { return []; }
    }

    public void SaveHold(string id, DateTimeOffset? until)
    {
        try
        {
            var all = LoadHolds();
            if (until is { } u && u > DateTimeOffset.Now) all[id] = u; else if (!all.Remove(id)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(HoldsPath, JsonSerializer.Serialize(all, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public IReadOnlyList<ProviderSnapshot> Load()
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<ProviderSnapshot>>(File.ReadAllText(path), Json) ?? [];
            return list.Where(s => s.Status == SnapshotStatus.Ok && s.Windows.Count > 0)
                       .Select(s => s with { Stale = true })
                       .ToList();
        }
        catch { return []; }
    }

    public void Save(IEnumerable<ProviderSnapshot> snapshots)
    {
        try
        {
            var good = snapshots.Where(s => s.Status == SnapshotStatus.Ok && !s.Stale && s.Windows.Count > 0).ToList();
            if (good.Count == 0) return;
            // Keep entries for providers that didn't just update.
            var merged = Load().Where(old => good.All(g => g.Source != old.Source)).Select(o => o with { Stale = false }).Concat(good).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(merged, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
