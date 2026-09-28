using System.Globalization;
using TokenTray.Core;

namespace TokenTray;

/// <summary>Turkish when Windows is Turkish, English otherwise.</summary>
internal static class S
{
    public static readonly bool Tr = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr";
    static string T(string en, string tr) => Tr ? tr : en;

    public static string Name(ProviderId id) => id switch
    {
        ProviderId.Claude => "Claude Code",
        ProviderId.Codex => "Codex",
        ProviderId.Antigravity => "Antigravity",
        ProviderId.Grok => "Grok Build",
        ProviderId.Cursor => "Cursor",
        _ => "OpenCode Go",
    };

    public static string ShortName(ProviderId id) => id switch
    {
        ProviderId.Claude => "Claude",
        ProviderId.Grok => "Grok",
        ProviderId.OpenCode => "OpenCode",
        _ => Name(id),
    };

    /// <summary>Short label for a source: the extra account's own name, else the provider's.</summary>
    public static string Label(ProviderSnapshot s) => s.Name ?? ShortName(s.Provider);
    public static string FullLabel(ProviderSnapshot s) => s.Name ?? Name(s.Provider);

    public static string Window(UsageWindow w)
    {
        var name = w.Kind switch
        {
            WindowKind.Session => w.Length is { } l ? T($"Session ({l.TotalHours:0}h)", $"Oturum ({l.TotalHours:0} sa)") : T("Session", "Oturum"),
            WindowKind.Weekly => T("This week", "Bu hafta"),
            WindowKind.Period => w.Label switch
            {
                "Monthly" or "This month" => T("This month", "Bu ay"),
                _ => T("Billing period", "Fatura dönemi"),
            },
            _ => T($"{w.Label.Replace("Weekly · ", "")} this week", $"{w.Label.Replace("Weekly · ", "")} bu hafta"),
        };
        return w.Group is { } g ? $"{g} · {name}" : name;
    }

    public static string Left => T("left", "kaldı");
    public static string Used => T("used", "kullanıldı");
    /// <summary>"Refills in 4h 20m, Tue 03:20".</summary>
    public static string ResetsAt(DateTimeOffset at, DateTimeOffset now)
    {
        var local = Format.Minute(at);
        var when = local.Date == now.ToLocalTime().Date ? local.ToString("HH:mm", CultureInfo.CurrentCulture)
            : local.ToString("ddd HH:mm", CultureInfo.CurrentCulture);
        return T($"Refills in {Format.Duration(at - now)}, {when}", $"{Duration(at - now)} sonra yenilenir, {when}");
    }

    static string When(DateTimeOffset at, DateTimeOffset now)
    {
        var local = Format.Minute(at); var today = now.ToLocalTime().Date;
        var time = local.ToString("HH:mm", CultureInfo.CurrentCulture);
        if (local.Date == today) return T($"today at {time}", $"bugün {time}");
        if (local.Date == today.AddDays(1)) return T($"tomorrow at {time}", $"yarın {time}");
        return T($"{local:dddd} at {time}", $"{local.ToString("dddd", CultureInfo.CurrentCulture)} {time}");
    }

    public static string Verdict(Verdict v, DateTimeOffset now) => (v.Kind, v.Window) switch
    {
        (VerdictKind.Locked, { } w) when v.At is { } at =>
            T($"{Window(w)} limit reached. It reopens in {Format.Duration(at - now)}, {When(at, now)}.",
              $"{Window(w)} limiti doldu. {Duration(at - now)} sonra, {When(at, now)} açılır."),
        (VerdictKind.Locked, { } w) => T($"{Window(w)} limit reached.", $"{Window(w)} limiti doldu."),
        (VerdictKind.RunsOut, { } w) when v.At is { } at && w.ResetsAt is { } r =>
            T($"At this pace, {Window(w).ToLowerInvariant()} runs out in {Format.Duration(at - now)}, {Format.Duration(r - at)} before it refills.",
              $"Bu tempoyla {Window(w).ToLowerInvariant()} hakkı {Duration(at - now)} içinde biter, yenilenmeden {Duration(r - at)} önce."),
        (VerdictKind.Tight, { } w) when w.ResetsAt is { } r =>
            T($"{Window(w)} is {w.UsedPercent:0}% used, but this pace lasts until it refills {When(r, now)}.",
              $"{Window(w)} %{w.UsedPercent:0} dolu, ama bu tempo {When(r, now)} yenilenene kadar yetiyor."),
        (VerdictKind.High, { } w) when w.ResetsAt is { } r =>
            T($"{Window(w)} is {w.UsedPercent:0}% used. It refills {When(r, now)}.",
              $"{Window(w)} %{w.UsedPercent:0} dolu. {When(r, now)} yenilenir."),
        (VerdictKind.High, { } w) => T($"{Window(w)} is {w.UsedPercent:0}% used.", $"{Window(w)} %{w.UsedPercent:0} dolu."),
        (VerdictKind.Relaxed, _) => T("Plenty left. You'll reach the next reset with room to spare.", "Rahatsın. Yenilenmeye kadar bol bol yeter."),
        _ => "",
    };

    public static string NotchHint(bool remaining) => remaining
        ? T("The notch marks how much time is left in this window. Keep the bar ahead of it.", "Çentik bu pencerede kalan süreyi gösterir. Dolgu çentiğin önünde kalırsa hakkın yeter.")
        : T("The notch marks how much of this window has passed. Stay behind it and you'll make it to the reset.", "Çentik bu pencerenin ne kadarının geçtiğini gösterir. Dolgu çentiğin gerisinde kalırsa hakkın yeter.");
    public static string Refilling => T("Refilling…", "Yenileniyor…");
    public static string NoReset => T("No reset time reported", "Yenilenme zamanı bildirilmedi");

    public static string Status(ProviderSnapshot s) => s.Status switch
    {
        SnapshotStatus.NotSignedIn => T(s.Message ?? "Not signed in", s.Provider switch
        {
            ProviderId.Claude => "Claude Code girişi bulunamadı. Terminalde `claude` çalıştırıp aboneliğinle giriş yap.",
            ProviderId.Codex => "Codex girişi bulunamadı. Terminalde `codex login` çalıştır.",
            ProviderId.Antigravity => "Antigravity girişi bulunamadı. Antigravity'yi açıp Google hesabınla giriş yap.",
            ProviderId.Grok => "Grok girişi bulunamadı. Terminalde `grok login` çalıştır.",
            ProviderId.Cursor => "Cursor girişi bulunamadı. Cursor'ı açıp giriş yap.",
            _ => @"OpenCode Go için %APPDATA%\opencode-go\config.json dosyasında workspaceId ve authCookie gerekli.",
        }),
        SnapshotStatus.TokenExpired => T(s.Message ?? "Sign-in expired", s.Provider switch
        {
            ProviderId.Claude => "Claude oturumunun süresi doldu. Claude Code'u bir kez aç, kendisi yeniler.",
            ProviderId.Codex => "Codex oturumunun süresi doldu. Herhangi bir `codex` komutu çalıştır.",
            ProviderId.Antigravity => "Antigravity oturumunun süresi doldu. Antigravity'yi bir kez aç, kendisi yeniler.",
            ProviderId.Grok => "Grok oturumunun süresi doldu. Terminalde `grok` komutunu bir kez çalıştır.",
            ProviderId.Cursor => "Cursor oturumunun süresi doldu. Cursor'ı bir kez aç.",
            _ => "OpenCode oturum çerezi geçersiz. opencode.ai'den yenisini kopyala.",
        }),
        SnapshotStatus.Error when Tr => "Kullanım okunamadı: " + (s.Message ?? "bilinmeyen yanıt"),
        SnapshotStatus.Offline => T("Can't reach the usage service. Showing the last reading.", "Kullanım servisine ulaşılamıyor. Son okuma gösteriliyor."),
        SnapshotStatus.RateLimited when s.RetryAt is { } at && at > DateTimeOffset.Now =>
            s.Windows.Count > 0
                ? T($"Showing the last reading. The usage service asked us to slow down; next check in {Format.Duration(at - DateTimeOffset.Now)}.",
                    $"Son okuma gösteriliyor. Servis çok sık sorduğumuzu söyledi; {Duration(at - DateTimeOffset.Now)} içinde tekrar bakılacak.")
                : T($"Usage not read yet. The usage service asked us to slow down; trying again in {Format.Duration(at - DateTimeOffset.Now)}.",
                    $"Kullanım henüz okunamadı. Servis çok sık sorduğumuzu söyledi; {Duration(at - DateTimeOffset.Now)} içinde tekrar denenecek."),
        SnapshotStatus.RateLimited => T("The usage service asked us to slow down. Trying again shortly.", "Kullanım servisi çok sık sorduğumuzu söyledi. Birazdan tekrar denenecek."),
        SnapshotStatus.Loading => T("Reading usage…", "Kullanım okunuyor…"),
        _ => s.Message ?? "",
    };

    public static string Updated(DateTimeOffset at, DateTimeOffset now)
    {
        var ago = now - at;
        if (ago < TimeSpan.FromMinutes(1)) return T("Updated just now", "Az önce güncellendi");
        return T($"Updated {Format.Duration(ago)} ago", $"{Duration(ago)} önce güncellendi");
    }

    public static string Duration(TimeSpan t) => !Tr ? Format.Duration(t)
        : Format.Duration(t).Replace("d", " gün").Replace("h", " sa").Replace("m", " dk").Replace("now", "şimdi").Replace("  ", " ");

    public static string ShortDuration(TimeSpan t) => !Tr ? Format.Short(t)
        : Format.Short(t).Replace("d", "g").Replace("h", "sa").Replace("m", "dk").Replace("now", "şimdi");

    // Menu and settings
    public static string Refresh => T("Refresh now", "Şimdi yenile");
    public static string Settings => T("Settings", "Ayarlar");
    public static string Quit => T("Quit TokenTray", "TokenTray'den çık");
    public static string DisplayLabel => T("Numbers show", "Sayılar neyi göstersin");
    public static string UsedOption => T("Used", "Kullanılan");
    public static string LeftOption => T("Left", "Kalan");
    public static string EmbedLabel => T("Show on the taskbar", "Görev çubuğunda göster");
    public static string EmbedHint => T("Next to the tray icons. Off keeps only the tray icon", "Tepsi simgelerinin yanında. Kapalıyken yalnız tepsi simgesi kalır");
    public static string StartupLabel => T("Start with Windows", "Windows ile başlat");
    public static string NotifyLabel => T("Warn me when running low", "Azalınca haber ver");
    public static string NotifyHint => T("At 20% and 5% left, and when it refills", "%20 ve %5 kalınca ve yenilenince");
    public static string IntervalLabel => T("Check every", "Kontrol sıklığı");
    public static string OffsetLabel => T("Gap from tray icons", "Tepsi simgelerine uzaklık");
    public static string Back => T("Back", "Geri");
    public static string WelcomeTitle => T("TokenTray is running", "TokenTray çalışıyor");
    public static string WelcomeBody(bool taskbar) => taskbar
        ? T("Your limits are next to the clock. Hover for details, click for more. If the tray icon hides under ^, drag it onto the taskbar.",
            "Limitlerin saatin yanında. Detay için üzerine gel, fazlası için tıkla. Tepsi simgesi ^ altında kalırsa görev çubuğuna sürükle.")
        : T("Click the tray icon for your limits. If it hides under ^, drag it onto the taskbar.",
            "Limitlerin için tepsi simgesine tıkla. Simge ^ altında kalırsa görev çubuğuna sürükle.");
    public static string VariantName => T("Name", "İsimli");
    public static string VariantIcon => T("Icon", "İkonlu");
    public static string EmailLabel => T("Show account emails", "Hesap e-postalarını göster");
    public static string EmailHint => T("Under each tool's name in the panel", "Paneldeki her aracın adının altında");
    public static string ThemeLabel => T("Theme", "Tema");
    public static string ThemeName(AppTheme t) => t switch
    {
        AppTheme.Light => T("Light", "Açık"),
        AppTheme.Dark => T("Dark", "Koyu"),
        _ => T("System", "Sistem"),
    };
    public static string ThemeHint => T("The taskbar widget always matches the taskbar.", "Görev çubuğundaki widget her zaman görev çubuğunun rengini izler.");
    public static string ProvidersLabel => T("Tools", "Araçlar");
    public static string FoundHere => T("Found on this PC", "Bu bilgisayarda bulundu");
    public static string NotFoundHere => T("Not found on this PC", "Bu bilgisayarda bulunamadı");
    public static string AccountsLabel => T("More accounts", "Ek hesaplar");
    public static string AccountsHint => T("Signed in to Claude Code or Codex in another config folder (CLAUDE_CONFIG_DIR, CODEX_HOME)? Add that folder.",
        "Claude Code ya da Codex'e başka bir ayar klasöründe (CLAUDE_CONFIG_DIR, CODEX_HOME) giriş yaptıysan o klasörü ekle.");
    public static string AddAccount(ProviderId id) => T($"Add {ShortName(id)} account", $"{ShortName(id)} hesabı ekle");
    public static string RemoveAccount => T("Remove account", "Hesabı kaldır");
    public static string PickFolder(ProviderId id) => T($"Choose the {Name(id)} config folder", $"{Name(id)} ayar klasörünü seç");
    public static string NoSignInInFolder(ProviderId id, string file) =>
        T($"There's no {file} in that folder, so {Name(id)} isn't signed in there.", $"Bu klasörde {file} yok; {Name(id)} orada oturum açmamış.");
    public static string TaskbarTab => T("Taskbar", "Görev çubuğu");
    public static string GeneralTab => T("General", "Genel");
    public static string StyleLabel => T("Style", "Stil");
    public static string DetailsLabel => T("Show", "Gösterilecekler");
    public static string DetailName => T("Show name", "İsim göster");
    public static string DetailNameHint => T("With the name off, each provider shows its icon.", "İsim kapalıyken sağlayıcının ikonu gösterilir.");
    public static string DetailSession => T("5-hour session", "5 saatlik oturum");
    public static string DetailWeekly => T("Weekly", "Haftalık");
    public static string DetailTime => T("When it refills", "Yenilenme zamanı");
    public static string StyleName(WidgetStyle s) => s switch
    {
        WidgetStyle.Rings => T("Rings", "Halka"),
        WidgetStyle.Dots => T("Dots", "Nokta"),
        WidgetStyle.Cells => T("Cells", "Hücre"),
        WidgetStyle.Bars => T("Bars", "Çubuk"),
        _ => T("Simple", "Sade"),
    };
    public static string Minutes(int m) => T($"{m} min", $"{m} dk");

    public static string LowTitle(string who, double left) => T($"{who}: {left:0}% left", $"{who}: %{left:0} kaldı");
    public static string LowBody(UsageWindow w, DateTimeOffset now) => w.ResetsAt is { } r
        ? T($"{Window(w)}: refills in {Format.Duration(r - now)}.", $"{Window(w)}: {Duration(r - now)} sonra yenilenir.")
        : T($"{Window(w)}: running low.", $"{Window(w)}: azalıyor.");
    public static string RefilledTitle(string who) => T($"{who} refilled", $"{who} yenilendi");
    public static string RefilledBody(UsageWindow w) => T($"{Window(w)}: back to full.", $"{Window(w)}: hakkın yeniden dolu.");

    public static string Percent(double v) => Tr ? $"%{v:0}" : $"{v:0}%";
}

