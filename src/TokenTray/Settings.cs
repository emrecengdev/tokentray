using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using TokenTray.Core;

namespace TokenTray;

public sealed class Settings
{
    public int Version { get; set; } = 1;
    /// <summary>Off by default, matching how Claude and Codex report usage themselves.</summary>
    public bool ShowRemaining { get; set; }
    public bool Claude { get; set; } = true;
    public bool Codex { get; set; } = true;
    // Null means "on when the tool is found on this PC", so new users see what they have.
    public bool? Antigravity { get; set; }
    public bool? Grok { get; set; }
    public bool? Cursor { get; set; }
    public bool? OpenCode { get; set; }
    /// <summary>Additional Claude Code or Codex sign-ins, each in its own config folder.</summary>
    public List<ExtraAccount> Accounts { get; set; } = [];
    public bool Welcomed { get; set; }

    public bool IsOn(ProviderId id) => id switch
    {
        ProviderId.Claude => Claude,
        ProviderId.Codex => Codex,
        ProviderId.Antigravity => Antigravity ?? AntigravityProvider.IsInstalled(),
        ProviderId.Grok => Grok ?? GrokProvider.IsInstalled(),
        ProviderId.Cursor => Cursor ?? CursorProvider.IsInstalled(),
        _ => OpenCode ?? OpenCodeProvider.IsInstalled(),
    };

    public void Set(ProviderId id, bool on)
    {
        switch (id)
        {
            case ProviderId.Claude: Claude = on; break;
            case ProviderId.Codex: Codex = on; break;
            case ProviderId.Antigravity: Antigravity = on; break;
            case ProviderId.Grok: Grok = on; break;
            case ProviderId.Cursor: Cursor = on; break;
            default: OpenCode = on; break;
        }
    }

    public static bool IsInstalled(ProviderId id) => id switch
    {
        ProviderId.Claude => ClaudeProvider.IsInstalled(),
        ProviderId.Codex => CodexProvider.IsInstalled(),
        ProviderId.Antigravity => AntigravityProvider.IsInstalled(),
        ProviderId.Grok => GrokProvider.IsInstalled(),
        ProviderId.Cursor => CursorProvider.IsInstalled(),
        _ => OpenCodeProvider.IsInstalled(),
    };

    /// <summary>Every source to poll: enabled providers, then extra accounts.</summary>
    public IEnumerable<IUsageProvider> BuildProviders()
    {
        if (Claude) yield return new ClaudeProvider();
        if (Codex) yield return new CodexProvider();
        if (IsOn(ProviderId.Antigravity)) yield return new AntigravityProvider();
        if (IsOn(ProviderId.Grok)) yield return new GrokProvider();
        if (IsOn(ProviderId.Cursor)) yield return new CursorProvider();
        if (IsOn(ProviderId.OpenCode)) yield return new OpenCodeProvider();
        foreach (var a in Accounts)
        {
            var key = $"{a.Provider}:{a.Path}";
            if (a.Provider == ProviderId.Claude) yield return new ClaudeProvider(a.Path, key, a.Name);
            else if (a.Provider == ProviderId.Codex) yield return new CodexProvider(a.Path, key, a.Name);
        }
    }
    public int IntervalSeconds { get; set; } = 120;
    public bool EmbedInTaskbar { get; set; } = true;
    /// <summary>Extra gap, in pixels at 100% scale, between the widget and the tray icons.</summary>
    public int TaskbarOffset { get; set; }
    public bool Notifications { get; set; } = true;
    public AppTheme Theme { get; set; } = AppTheme.System;
    /// <summary>Show each account's email under the tool's name in the flyout.</summary>
    public bool ShowEmail { get; set; } = true;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool? UiDark => Theme switch { AppTheme.Light => false, AppTheme.Dark => true, _ => null };

    // Taskbar widget look
    public WidgetStyle WidgetStyle { get; set; } = WidgetStyle.Rings;
    public bool WidgetName { get; set; } = true;
    public bool WidgetSession { get; set; } = true;
    public bool WidgetWeekly { get; set; } = true;
    public bool WidgetTime { get; set; } = true;

    [System.Text.Json.Serialization.JsonIgnore]
    public WidgetOptions Widget => new(WidgetStyle, WidgetName, WidgetSession, WidgetWeekly, WidgetTime);

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenTray");
    static string FilePath => Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("TokenTray") is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value && Environment.ProcessPath is { } exe) key.SetValue("TokenTray", $"\"{exe}\"");
            else key.DeleteValue("TokenTray", throwOnMissingValue: false);
        }
    }
}

public sealed record ExtraAccount(ProviderId Provider, string Path, string Name);

public enum AppTheme { System, Light, Dark }
