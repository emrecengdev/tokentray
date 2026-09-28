using System.Windows.Media;
using Microsoft.Win32;
using TokenTray.Core;

namespace TokenTray;

/// <summary>Color tokens. Provider hues identify the service; status hues only appear when something needs attention.</summary>
internal sealed record Palette(bool Dark)
{
    public Color Text => Dark ? Color.FromRgb(0xF3, 0xF1, 0xEE) : Color.FromRgb(0x1B, 0x1A, 0x19);
    public Color TextMuted => Dark ? Color.FromArgb(0xC4, 0xF3, 0xF1, 0xEE) : Color.FromArgb(0xB8, 0x1B, 0x1A, 0x19);
    public Color Track => Dark ? Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x24, 0x00, 0x00, 0x00);
    public Color Tick => Dark ? Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xD0, 0x1B, 0x1A, 0x19);
    public Color Hairline => Dark ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x16, 0x00, 0x00, 0x00);
    /// <summary>Fallback surface for Windows 10, where there's no system backdrop.</summary>
    public Color Surface => Dark ? Color.FromRgb(0x24, 0x23, 0x22) : Color.FromRgb(0xF6, 0xF5, 0xF3);
    public Color HoverFill => Dark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0E, 0x00, 0x00, 0x00);

    public Color Amber => Dark ? Color.FromRgb(0xF5, 0xB0, 0x3F) : Color.FromRgb(0xC7, 0x7A, 0x00);
    /// <summary>Amber and red for text: darker on light backgrounds so small type keeps its contrast.</summary>
    public Color AmberText => Dark ? Amber : Color.FromRgb(0x96, 0x55, 0x00);
    public Color RedText => Dark ? Red : Color.FromRgb(0xB0, 0x1E, 0x24);
    public Color Red => Dark ? Color.FromRgb(0xFF, 0x6B, 0x5E) : Color.FromRgb(0xD1, 0x34, 0x38);

    public Color Hue(ProviderId id) => id switch
    {
        ProviderId.Claude => Dark ? Color.FromRgb(0xE8, 0x8C, 0x66) : Color.FromRgb(0xC2, 0x5E, 0x3A),
        ProviderId.Codex => Dark ? Color.FromRgb(0x9A, 0xA8, 0xFF) : Color.FromRgb(0x4B, 0x5B, 0xE0),
        ProviderId.Antigravity => Dark ? Color.FromRgb(0x4F, 0xC9, 0xA8) : Color.FromRgb(0x14, 0x8A, 0x6C),
        ProviderId.Grok => Dark ? Color.FromRgb(0xD4, 0xD4, 0xD8) : Color.FromRgb(0x3F, 0x3F, 0x46),
        ProviderId.Cursor => Dark ? Color.FromRgb(0xC0, 0x96, 0xF5) : Color.FromRgb(0x7C, 0x4D, 0xC9),
        _ => Dark ? Color.FromRgb(0x6C, 0xC7, 0xE6) : Color.FromRgb(0x1F, 0x86, 0xA8),
    };

    /// <summary>Bar color: the provider's own hue until the allowance is actually at risk.</summary>
    public Color Fill(ProviderId id, UsageWindow w, Pace p)
    {
        if (w.RemainingPercent <= 5 || p.State == PaceState.Exhausted) return Red;
        if (w.RemainingPercent <= 20 || p.State == PaceState.Hot) return Amber;
        return Hue(id);
    }

    public static SolidColorBrush Brush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>The taskbar follows the Windows (system) mode. The widget and tray icon sit on it, so they always use this.</summary>
    public static bool TaskbarDark => Environment.GetEnvironmentVariable("TOKENTRAY_THEME") switch
    {
        "dark" => true,
        "light" => false,
        _ => ReadDword("SystemUsesLightTheme") == 0,
    };

    /// <summary>The user's theme choice for the flyout, hover card and menu; null follows the system.</summary>
    public static bool? UiOverride { get; set; }
    public static bool UiDark => UiOverride ?? TaskbarDark;
    public static bool AppsDark => ReadDword("AppsUseLightTheme") == 0;

    static int? ReadDword(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Personalize);
        return key?.GetValue(name) as int?;
    }
}
