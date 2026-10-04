using System.Globalization;
using TokenTray.Core;

namespace TokenTray;

/// <summary>Turkish or Chinese when Windows is, English otherwise. A stored choice wins over the OS.</summary>
internal static class S
{
    static UiLang? chosen;

    /// <summary>Apply the stored language before any string is read.</summary>
    internal static void Configure(UiLang lang) => chosen = lang;

    static string Two => (chosen ?? UiLang.Auto) switch
    {
        UiLang.English => "en",
        UiLang.Turkish => "tr",
        UiLang.Chinese => "zh",
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
    };

    public static bool Tr => Two == "tr";
    public static bool Zh => Two is "zh";

    /// <summary>Culture for dates, day names and numbers. English follows the OS; Chinese is always zh-CN.</summary>
    public static CultureInfo Culture => Zh ? CultureInfo.GetCultureInfo("zh-CN") : CultureInfo.CurrentCulture;

    internal static string T(string en, string tr, string zh) => Zh ? zh : Tr ? tr : en;

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
            WindowKind.Session => w.Length is { } l
                ? T($"Session ({l.TotalHours:0}h)", $"Oturum ({l.TotalHours:0} sa)", $"时段（{l.TotalHours:0} 小时）")
                : T("Session", "Oturum", "时段"),
            WindowKind.Weekly => T("This week", "Bu hafta", "本周"),
            WindowKind.Period => w.Label switch
            {
                "Monthly" or "This month" => T("This month", "Bu ay", "本月"),
                _ => T("Billing period", "Fatura dönemi", "账期"),
            },
            _ => T($"{w.Label.Replace("Weekly · ", "")} this week", $"{w.Label.Replace("Weekly · ", "")} bu hafta", $"{w.Label.Replace("Weekly · ", "")}本周"),
        };
        return w.Group is { } g ? $"{g} · {name}" : name;
    }

    public static string Left => T("left", "kaldı", "剩余");
    public static string Used => T("used", "kullanıldı", "已用");
    /// <summary>"Refills in 4h 20m, Tue 03:20".</summary>
    public static string ResetsAt(DateTimeOffset at, DateTimeOffset now)
    {
        var local = Format.Minute(at);
        var when = local.Date == now.ToLocalTime().Date ? local.ToString("HH:mm", Culture)
            : local.ToString(Zh ? "M月d日 HH:mm" : "ddd HH:mm", Culture);
        return T($"Refills in {Format.Duration(at - now)}, {when}", $"{Duration(at - now)} sonra yenilenir, {when}",
            $"{ZhDuration(at - now)}后补充，{when}");
    }

    static string When(DateTimeOffset at, DateTimeOffset now)
    {
        var local = Format.Minute(at); var today = now.ToLocalTime().Date;
        var time = local.ToString("HH:mm", Culture);
        if (local.Date == today) return T($"today at {time}", $"bugün {time}", $"今天 {time}");
        if (local.Date == today.AddDays(1)) return T($"tomorrow at {time}", $"yarın {time}", $"明天 {time}");
        return Zh
            ? $"{local.ToString("M月d日", Culture)} {time}"
            : T($"{local:dddd} at {time}", $"{local.ToString("dddd", Culture)} {time}", $"{local.ToString("dddd", Culture)} {time}");
    }

    public static string Verdict(Verdict v, DateTimeOffset now) => (v.Kind, v.Window) switch
    {
        (VerdictKind.Locked, { } w) when v.At is { } at =>
            T($"{Window(w)} limit reached. It reopens in {Format.Duration(at - now)}, {When(at, now)}.",
              $"{Window(w)} limiti doldu. {Duration(at - now)} sonra, {When(at, now)} açılır.",
              $"{Window(w)}额度已用尽，{ZhDuration(at - now)}后恢复，即{When(at, now)}。"),
        (VerdictKind.Locked, { } w) => T($"{Window(w)} limit reached.", $"{Window(w)} limiti doldu.", $"{Window(w)}额度已用尽。"),
        (VerdictKind.RunsOut, { } w) when v.At is { } at && w.ResetsAt is { } r =>
            T($"At this pace, {Window(w).ToLowerInvariant()} runs out in {Format.Duration(at - now)}, {Format.Duration(r - at)} before it refills.",
              $"Bu tempoyla {Window(w).ToLowerInvariant()} hakkı {Duration(at - now)} içinde biter, yenilenmeden {Duration(r - at)} önce.",
              $"按当前速度，{Window(w)}将在{ZhDuration(at - now)}后耗尽，比补充时间早{ZhDuration(r - at)}。"),
        (VerdictKind.Tight, { } w) when w.ResetsAt is { } r =>
            T($"{Window(w)} is {w.UsedPercent:0}% used, but this pace lasts until it refills {When(r, now)}.",
              $"{Window(w)} %{w.UsedPercent:0} dolu, ama bu tempo {When(r, now)} yenilenene kadar yetiyor.",
              $"{Window(w)}已用 {w.UsedPercent:0}%，但按此速度可撑到{When(r, now)}补充。"),
        (VerdictKind.High, { } w) when w.ResetsAt is { } r =>
            T($"{Window(w)} is {w.UsedPercent:0}% used. It refills {When(r, now)}.",
              $"{Window(w)} %{w.UsedPercent:0} dolu. {When(r, now)} yenilenir.",
              $"{Window(w)}已用 {w.UsedPercent:0}%，{When(r, now)}补充。"),
        (VerdictKind.High, { } w) => T($"{Window(w)} is {w.UsedPercent:0}% used.", $"{Window(w)} %{w.UsedPercent:0} dolu.", $"{Window(w)}已用 {w.UsedPercent:0}%。"),
        (VerdictKind.Relaxed, _) => T("Plenty left. You'll reach the next reset with room to spare.", "Rahatsın. Yenilenmeye kadar bol bol yeter.", "额度充足，下次补充前完全够用。"),
        _ => "",
    };

    public static string NotchHint(bool remaining) => remaining
        ? T("The notch marks how much time is left in this window. Keep the bar ahead of it.", "Çentik bu pencerede kalan süreyi gösterir. Dolgu çentiğin önünde kalırsa hakkın yeter.", "刻度线表示本时段剩余时间。让进度条保持在它前面就够用。")
        : T("The notch marks how much of this window has passed. Stay behind it and you'll make it to the reset.", "Çentik bu pencerenin ne kadarının geçtiğini gösterir. Dolgu çentiğin gerisinde kalırsa hakkın yeter.", "刻度线表示本时段已过去多久。进度条留在它后面就能撑到补充。");
    public static string Refilling => T("Refilling…", "Yenileniyor…", "补充中…");
    public static string NoReset => T("No reset time reported", "Yenilenme zamanı bildirilmedi", "未提供补充时间");

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
        }, s.Provider switch
        {
            ProviderId.Claude => "未找到 Claude Code 登录。在终端运行 `claude` 并用你的订阅登录。",
            ProviderId.Codex => "未找到 Codex 登录。在终端运行 `codex login`。",
            ProviderId.Antigravity => "未找到 Antigravity 登录。打开 Antigravity 并用 Google 账号登录。",
            ProviderId.Grok => "未找到 Grok 登录。在终端运行 `grok login`。",
            ProviderId.Cursor => "未找到 Cursor 登录。打开 Cursor 并登录。",
            _ => @"OpenCode Go 需要在 %APPDATA%\opencode-go\config.json 中配置 workspaceId 和 authCookie。",
        }),
        SnapshotStatus.TokenExpired => T(s.Message ?? "Sign-in expired", s.Provider switch
        {
            ProviderId.Claude => "Claude oturumunun süresi doldu. Claude Code'u bir kez aç, kendisi yeniler.",
            ProviderId.Codex => "Codex oturumunun süresi doldu. Herhangi bir `codex` komutu çalıştır.",
            ProviderId.Antigravity => "Antigravity oturumunun süresi doldu. Antigravity'yi bir kez aç, kendisi yeniler.",
            ProviderId.Grok => "Grok oturumunun süresi doldu. Terminalde `grok` komutunu bir kez çalıştır.",
            ProviderId.Cursor => "Cursor oturumunun süresi doldu. Cursor'ı bir kez aç.",
            _ => "OpenCode oturum çerezi geçersiz. opencode.ai'den yenisini kopyala.",
        }, s.Provider switch
        {
            ProviderId.Claude => "Claude 登录已过期。打开一次 Claude Code 即会自动续期。",
            ProviderId.Codex => "Codex 登录已过期。运行任意一条 `codex` 命令即可刷新。",
            ProviderId.Antigravity => "Antigravity 登录已过期。打开一次 Antigravity 即会自动续期。",
            ProviderId.Grok => "Grok 登录已过期。在终端运行一次 `grok` 即可续期。",
            ProviderId.Cursor => "Cursor 登录已过期。打开一次 Cursor 即可续期。",
            _ => "OpenCode 会话 cookie 无效。请从 opencode.ai 重新复制一份。",
        }),
        SnapshotStatus.Error when Zh => "无法读取用量：" + ZhMsg(s.Message ?? "未知响应"),
        SnapshotStatus.Error when Tr => "Kullanım okunamadı: " + (s.Message ?? "bilinmeyen yanıt"),
        SnapshotStatus.Offline => T("Can't reach the usage service. Showing the last reading.", "Kullanım servisine ulaşılamıyor. Son okuma gösteriliyor.", "无法连接用量服务，正在显示上次读取结果。"),
        SnapshotStatus.RateLimited when s.RetryAt is { } at && at > DateTimeOffset.Now =>
            s.Windows.Count > 0
                ? T($"Showing the last reading. The usage service asked us to slow down; next check in {Format.Duration(at - DateTimeOffset.Now)}.",
                    $"Son okuma gösteriliyor. Servis çok sık sorduğumuzu söyledi; {Duration(at - DateTimeOffset.Now)} içinde tekrar bakılacak.",
                    $"正在显示上次读取结果。用量服务要求降低频率，{ZhDuration(at - DateTimeOffset.Now)}后再查。")
                : T($"Usage not read yet. The usage service asked us to slow down; trying again in {Format.Duration(at - DateTimeOffset.Now)}.",
                    $"Kullanım henüz okunamadı. Servis çok sık sorduğumuzu söyledi; {Duration(at - DateTimeOffset.Now)} içinde tekrar denenecek.",
                    $"尚未读到用量。用量服务要求降低频率，{ZhDuration(at - DateTimeOffset.Now)}后重试。"),
        SnapshotStatus.RateLimited => T("The usage service asked us to slow down. Trying again shortly.", "Kullanım servisi çok sık sorduğumuzu söyledi. Birazdan tekrar denenecek.", "用量服务要求降低请求频率，稍后重试。"),
        SnapshotStatus.Loading => T("Reading usage…", "Kullanım okunuyor…", "正在读取用量…"),
        _ => Zh ? ZhMsg(s.Message ?? "") : s.Message ?? "",
    };

    /// <summary>
    /// The providers report failures in English because TokenTray.Core stays UI-agnostic. Chinese
    /// needs those mapped here, longest prefix first so "quota summary returned" wins over "returned".
    /// </summary>
    static readonly (string En, string Zh)[] ZhPrefix =
    [
        ("Claude usage service returned ", "Claude 用量服务返回 "),
        ("Codex usage service returned ", "Codex 用量服务返回 "),
        ("Antigravity quota summary returned ", "Antigravity 配额汇总返回 "),
        ("Antigravity returned ", "Antigravity 返回 "),
        ("Grok billing returned ", "Grok 账单接口返回 "),
        ("Cursor usage returned ", "Cursor 用量接口返回 "),
        ("OpenCode returned ", "OpenCode 返回 "),
    ];

    static readonly Dictionary<string, string> ZhExact = new()
    {
        ["No Claude Code sign-in found. Run `claude` and sign in with your subscription."] = "未找到 Claude Code 登录。在终端运行 `claude` 并用你的订阅登录。",
        ["Claude sign-in expired. Open Claude Code once and it will refresh."] = "Claude 登录已过期。打开一次 Claude Code 即会自动续期。",
        ["Claude rejected the sign-in. Open Claude Code once to refresh it."] = "Claude 拒绝了该登录。打开一次 Claude Code 即可刷新。",
        ["Claude returned no usage windows for this account."] = "Claude 未返回该账号的用量时段。",
        ["No Codex sign-in file found. Run `codex login`. Keyring-only sign-ins aren't supported yet."] = "未找到 Codex 登录文件。运行 `codex login`。暂不支持仅保存在系统凭据库中的登录。",
        ["Codex is using an API key, which has no plan limits to show. Sign in with ChatGPT to track them."] = "Codex 正在使用 API Key，没有可显示的套餐额度。改用 ChatGPT 登录后才能跟踪。",
        ["Codex credentials are unreadable. Run `codex login`."] = "无法读取 Codex 凭据。运行 `codex login`。",
        ["Codex sign-in expired. Run any `codex` command to refresh it."] = "Codex 登录已过期。运行任意一条 `codex` 命令即可刷新。",
        ["Codex rejected the sign-in. Run any `codex` command to refresh it."] = "Codex 拒绝了该登录。运行任意一条 `codex` 命令即可刷新。",
        ["Codex returned no usage windows for this account."] = "Codex 未返回该账号的用量时段。",
        ["No Antigravity sign-in found. Open Antigravity and sign in with Google."] = "未找到 Antigravity 登录。打开 Antigravity 并用 Google 账号登录。",
        ["Antigravity's stored sign-in is unreadable. Sign in again in Antigravity."] = "无法读取 Antigravity 保存的登录信息。请在 Antigravity 中重新登录。",
        ["Antigravity's sign-in has expired. Open Antigravity once and it will refresh."] = "Antigravity 登录已过期。打开一次即会自动续期。",
        ["Antigravity rejected the sign-in. Open Antigravity once to refresh it."] = "Antigravity 拒绝了该登录。打开一次即可刷新。",
        ["Antigravity quota service didn't answer."] = "Antigravity 配额服务没有响应。",
        ["Antigravity has no quota project for this account."] = "该账号在 Antigravity 中没有配额项目。",
        ["Antigravity returned no quota for this account."] = "Antigravity 未返回该账号的配额。",
        ["No Grok sign-in found. Run `grok login`."] = "未找到 Grok 登录。运行 `grok login`。",
        ["No xAI sign-in in Grok's auth file. Run `grok login`."] = "Grok 的 auth 文件里没有 xAI 登录信息。运行 `grok login`。",
        ["Grok sign-in expired. Run `grok` once and it will refresh."] = "Grok 登录已过期。运行一次 `grok` 即可续期。",
        ["Grok rejected the sign-in. Run `grok` once to refresh it."] = "Grok 拒绝了该登录。运行一次 `grok` 即可刷新。",
        ["Grok returned no usage for this account."] = "Grok 未返回该账号的用量。",
        ["No Cursor sign-in found. Open Cursor and sign in."] = "未找到 Cursor 登录。打开 Cursor 并登录。",
        ["Cursor rejected the sign-in. Open Cursor once to refresh it."] = "Cursor 拒绝了该登录。打开一次即可刷新。",
        ["Cursor returned no usage for this account."] = "Cursor 未返回该账号的用量。",
        ["OpenCode Go needs a workspace id and session cookie in %APPDATA%\\opencode-go\\config.json."] = @"OpenCode Go 需要在 %APPDATA%\opencode-go\config.json 里配置 workspace id 和会话 cookie。",
        ["OpenCode rejected the session cookie. Copy a fresh one from opencode.ai."] = "OpenCode 拒绝了该会话 cookie。请从 opencode.ai 重新复制一份。",
        ["No active OpenCode Go subscription on this workspace."] = "该 workspace 上没有生效的 OpenCode Go 订阅。",
        ["OpenCode returned no usage meters."] = "OpenCode 没有返回任何用量计量。",
        ["The usage service asked us to slow down."] = "用量服务要求降低请求频率。",
    };

    static string ZhMsg(string en)
    {
        if (en.Length == 0) return "";
        foreach (var (e, z) in ZhPrefix)
            if (en.StartsWith(e, StringComparison.Ordinal)) return z + en[e.Length..];
        return ZhExact.GetValueOrDefault(en, en);
    }

    public static string Updated(DateTimeOffset at, DateTimeOffset now)
    {
        var ago = now - at;
        if (ago < TimeSpan.FromMinutes(1)) return T("Updated just now", "Az önce güncellendi", "刚刚更新");
        return T($"Updated {Format.Duration(ago)} ago", $"{Duration(ago)} önce güncellendi", $"{ZhDuration(ago)}前更新");
    }

    /// <summary>"2天4小时" / "3小时12分" / "8分钟" / "刚刚".</summary>
    static string ZhDuration(TimeSpan t)
    {
        if (t <= TimeSpan.Zero) return "刚刚";
        if (t.TotalDays >= 1) return t.Hours > 0 ? $"{(int)t.TotalDays}天{t.Hours}小时" : $"{(int)t.TotalDays}天";
        if (t.TotalHours >= 1) return t.Minutes > 0 ? $"{(int)t.TotalHours}小时{t.Minutes}分" : $"{(int)t.TotalHours}小时";
        return $"{Math.Max(1, (int)Math.Ceiling(t.TotalMinutes))}分钟";
    }

    public static string Duration(TimeSpan t) => Zh ? ZhDuration(t) : !Tr ? Format.Duration(t)
        : Format.Duration(t).Replace("d", " gün").Replace("h", " sa").Replace("m", " dk").Replace("now", "şimdi").Replace("  ", " ");

    public static string ShortDuration(TimeSpan t) => Zh ? ZhDuration(t) : !Tr ? Format.Short(t)
        : Format.Short(t).Replace("d", "g").Replace("h", "sa").Replace("m", "dk").Replace("now", "şimdi");

    // Menu and settings
    public static string Refresh => T("Refresh now", "Şimdi yenile", "立即刷新");
    public static string Settings => T("Settings", "Ayarlar", "设置");
    public static string Quit => T("Quit TokenTray", "TokenTray'den çık", "退出 TokenTray");
    public static string DisplayLabel => T("Numbers show", "Sayılar neyi göstersin", "数字显示");
    public static string UsedOption => T("Used", "Kullanılan", "已用");
    public static string LeftOption => T("Left", "Kalan", "剩余");
    public static string EmbedLabel => T("Show on the taskbar", "Görev çubuğunda göster", "在任务栏显示");
    public static string EmbedHint => T("Next to the tray icons. Off keeps only the tray icon", "Tepsi simgelerinin yanında. Kapalıyken yalnız tepsi simgesi kalır", "显示在托盘图标旁边。关闭后只保留托盘图标");
    public static string StartupLabel => T("Start with Windows", "Windows ile başlat", "开机自启动");
    public static string NotifyLabel => T("Warn me when running low", "Azalınca haber ver", "额度不足时提醒我");
    public static string NotifyHint => T("At 20% and 5% left, and when it refills", "%20 ve %5 kalınca ve yenilenince", "剩余 20% 和 5% 时，以及补充完成时");
    public static string IntervalLabel => T("Check every", "Kontrol sıklığı", "检查频率");
    public static string OffsetLabel => T("Gap from tray icons", "Tepsi simgelerine uzaklık", "与托盘图标的间距");
    public static string Back => T("Back", "Geri", "返回");
    public static string WelcomeTitle => T("TokenTray is running", "TokenTray çalışıyor", "TokenTray 已在运行");
    public static string WelcomeBody(bool taskbar) => taskbar
        ? T("Your limits are next to the clock. Hover for details, click for more. If the tray icon hides under ^, drag it onto the taskbar.",
            "Limitlerin saatin yanında. Detay için üzerine gel, fazlası için tıkla. Tepsi simgesi ^ altında kalırsa görev çubuğuna sürükle.",
            "你的额度就在时钟旁边。悬停查看详情，点击查看更多。如果托盘图标藏在 ^ 后面，把它拖到任务栏上。")
        : T("Click the tray icon for your limits. If it hides under ^, drag it onto the taskbar.",
            "Limitlerin için tepsi simgesine tıkla. Simge ^ altında kalırsa görev çubuğuna sürükle.",
            "点击托盘图标查看额度。如果图标藏在 ^ 后面，把它拖到任务栏上。");
    public static string BannerTagline => T("Your AI coding limits, right next to the clock. Session and weekly usage, exact reset times, and a warning before you run out.",
        "AI kodlama limitlerin, saatin hemen yanında. Oturum ve haftalık kullanım, tam yenilenme saatleri ve bitmeden önce uyarı.",
        "AI 编程额度就在时钟旁边。时段与每周用量、精确的补充时间，以及额度耗尽前的提醒。");
    public static (string, string)[] PaceGuideRows => Zh
        ? [
            ("节奏很宽松", "你的消耗比时间走得慢。额度非常充足。"),
            ("节奏正常", "消耗与时间同步，能撑到下次补充。"),
            ("用得偏快", "进度条已越过刻度线，按这个速度会先耗尽。"),
        ]
        : Tr
        ? [
            ("Tempo rahat", "Hakkın zamandan yavaş gidiyor; bol bol yeter."),
            ("Tempoda", "Harcaman geçen zamana denk; yenilenmeye yetişirsin."),
            ("Hızlı gidiyorsun", "Dolgu çentiği geçti; bu tempoyla yenilenmeden biter."),
        ]
        : [
            ("Well under pace", "You're spending slower than time passes. Plenty left."),
            ("On pace", "Spending matches the clock. You'll make it to the reset."),
            ("Running hot", "The fill passed the notch. At this rate it runs out first."),
        ];
    public static string PaceGuideCaption => T("The notch marks how much of the window has passed. Keep the fill behind it.",
        "Çentik pencerenin ne kadarının geçtiğini gösterir. Dolguyu onun gerisinde tut.",
        "刻度线表示本时段已经过去多久，让进度条保持在它后面。");
    public static string VariantName => T("Name", "İsimli", "显示名称");
    public static string VariantIcon => T("Icon", "İkonlu", "仅图标");
    public static string EmailLabel => T("Show account emails", "Hesap e-postalarını göster", "显示账号邮箱");
    public static string EmailHint => T("Under each tool's name in the panel", "Paneldeki her aracın adının altında", "显示在面板中每个工具名称下方");
    public static string ThemeLabel => T("Theme", "Tema", "主题");
    public static string ThemeName(AppTheme t) => t switch
    {
        AppTheme.Light => T("Light", "Açık", "浅色"),
        AppTheme.Dark => T("Dark", "Koyu", "深色"),
        _ => T("System", "Sistem", "跟随系统"),
    };
    public static string ThemeHint => T("The taskbar widget always matches the taskbar.", "Görev çubuğundaki widget her zaman görev çubuğunun rengini izler.", "任务栏挂件始终跟随任务栏配色。");
    public static string LanguageLabel => T("Language", "Dil", "语言");
    public static string LanguageHint => T("Interface language. Auto follows Windows.", "Arayüz dili. Otomatik, Windows'u izler.", "界面语言。“自动”跟随 Windows 设置。");
    public static string LanguageName(UiLang l) => l switch
    {
        UiLang.Chinese => "简体中文",
        UiLang.Turkish => "Türkçe",
        UiLang.English => "English",
        _ => T("Auto", "Otomatik", "自动"),
    };
    public static string ProvidersLabel => T("Tools", "Araçlar", "工具");
    public static string FoundHere => T("Found on this PC", "Bu bilgisayarda bulundu", "本机已检测到");
    public static string NotFoundHere => T("Not found on this PC", "Bu bilgisayarda bulunamadı", "本机未检测到");
    public static string AccountsLabel => T("More accounts", "Ek hesaplar", "更多账号");
    public static string AccountsHint => T("Signed in to Claude Code or Codex in another config folder (CLAUDE_CONFIG_DIR, CODEX_HOME)? Add that folder.",
        "Claude Code ya da Codex'e başka bir ayar klasöründe (CLAUDE_CONFIG_DIR, CODEX_HOME) giriş yaptıysan o klasörü ekle.",
        "在另一个配置目录（CLAUDE_CONFIG_DIR、CODEX_HOME）里登录了 Claude Code 或 Codex？把那个目录加进来。");
    public static string AddAccount(ProviderId id) => T($"Add {ShortName(id)} account", $"{ShortName(id)} hesabı ekle", $"添加 {ShortName(id)} 账号");
    public static string RemoveAccount => T("Remove account", "Hesabı kaldır", "移除账号");
    public static string PickFolder(ProviderId id) => T($"Choose the {Name(id)} config folder", $"{Name(id)} ayar klasörünü seç", $"选择 {Name(id)} 的配置目录");
    public static string NoSignInInFolder(ProviderId id, string file) =>
        T($"There's no {file} in that folder, so {Name(id)} isn't signed in there.", $"Bu klasörde {file} yok; {Name(id)} orada oturum açmamış.", $"该目录里没有 {file}，说明 {Name(id)} 未在那里登录。");
    public static string TaskbarTab => T("Taskbar", "Görev çubuğu", "任务栏");
    public static string GeneralTab => T("General", "Genel", "通用");
    public static string StyleLabel => T("Style", "Stil", "样式");
    public static string DetailsLabel => T("Show", "Gösterilecekler", "显示内容");
    public static string DetailName => T("Show name", "İsim göster", "显示名称");
    public static string DetailNameHint => T("With the name off, each provider shows its icon.", "İsim kapalıyken sağlayıcının ikonu gösterilir.", "关闭名称后，每个工具只显示图标。");
    public static string DetailSession => T("5-hour session", "5 saatlik oturum", "5 小时时段");
    public static string DetailWeekly => T("Weekly", "Haftalık", "每周");
    public static string DetailTime => T("When it refills", "Yenilenme zamanı", "补充时间");
    public static string StyleName(WidgetStyle s) => s switch
    {
        WidgetStyle.Rings => T("Rings", "Halka", "圆环"),
        WidgetStyle.Dots => T("Dots", "Nokta", "圆点"),
        WidgetStyle.Cells => T("Cells", "Hücre", "方块"),
        WidgetStyle.Bars => T("Bars", "Çubuk", "条形"),
        _ => T("Simple", "Sade", "简洁"),
    };
    public static string Minutes(int m) => T($"{m} min", $"{m} dk", $"{m} 分钟");

    public static string LowTitle(string who, double left) => T($"{who}: {left:0}% left", $"{who}: %{left:0} kaldı", $"{who}：剩余 {left:0}%");
    public static string LowBody(UsageWindow w, DateTimeOffset now) => w.ResetsAt is { } r
        ? T($"{Window(w)}: refills in {Format.Duration(r - now)}.", $"{Window(w)}: {Duration(r - now)} sonra yenilenir.", $"{Window(w)}：{ZhDuration(r - now)}后补充。")
        : T($"{Window(w)}: running low.", $"{Window(w)}: azalıyor.", $"{Window(w)}：额度偏低。");
    public static string RefilledTitle(string who) => T($"{who} refilled", $"{who} yenilendi", $"{who} 已补充");
    public static string RefilledBody(UsageWindow w) => T($"{Window(w)}: back to full.", $"{Window(w)}: hakkın yeniden dolu.", $"{Window(w)}：已恢复满额。");

    public static string Percent(double v) => Tr ? $"%{v:0}" : $"{v:0}%";
}
