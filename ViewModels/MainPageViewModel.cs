using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using WinToastRelay.Models;
using WinToastRelay.Services;

namespace WinToastRelay.ViewModels;

public partial class MainPageViewModel : ObservableObject
{
    private readonly SettingsStore _settingsStore = new();
    private readonly SecretStore _secretStore = new();
    private readonly NotificationRelayService _relayService;
    private readonly StartupTaskService _startupTaskService = new();
    private RelaySettings _settings = new();
    private const string ActivityFileName = "delivery-activity.json";
    private readonly SemaphoreSlim _applicationsLoadLock = new(1, 1);
    private readonly SemaphoreSlim _relayStartLock = new(1, 1);
    private bool _initialized;
    private bool _activityLoaded;
    private bool _applicationsLoaded;
    private string _statusSource = "尚未启动监听";

    [ObservableProperty] public partial string WebhookUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string BearerToken { get; set; } = string.Empty;
    [ObservableProperty] public partial string DeliveryMode { get; set; } = RelayDeliveryTarget.BarkMode;
    [ObservableProperty] public partial string BarkServerUrl { get; set; } = "https://api.day.app";
    [ObservableProperty] public partial string BarkDeviceKey { get; set; } = string.Empty;
    [ObservableProperty] public partial string BarkTitleTemplate { get; set; } = "{app}: {title}";
    [ObservableProperty] public partial string BarkBodyTemplate { get; set; } = "{body}";
    [ObservableProperty] public partial string BarkParameters { get; set; } = "level=active\nicon=https://raw.ravelloh.com/icon/WinToastRelay.png";
    [ObservableProperty] public partial string WxPusherAppToken { get; set; } = string.Empty;
    [ObservableProperty] public partial string WxPusherUids { get; set; } = string.Empty;
    [ObservableProperty] public partial string WxPusherTopicIds { get; set; } = string.Empty;
    [ObservableProperty] public partial string WxPusherSummaryTemplate { get; set; } = "{app}: {title}";
    [ObservableProperty] public partial string WxPusherContentTemplate { get; set; } = "{title}\n{body}";
    [ObservableProperty] public partial string FeishuWebhookUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string FeishuSecret { get; set; } = string.Empty;
    [ObservableProperty] public partial string FeishuTitleTemplate { get; set; } = "{app}: {title}";
    [ObservableProperty] public partial string FeishuBodyTemplate { get; set; } = "{body}";
    [ObservableProperty] public partial string TelegramApiUrl { get; set; } = "https://api.telegram.org";
    [ObservableProperty] public partial string TelegramBotToken { get; set; } = string.Empty;
    [ObservableProperty] public partial string TelegramChatId { get; set; } = string.Empty;
    [ObservableProperty] public partial string TelegramParseMode { get; set; } = string.Empty;
    [ObservableProperty] public partial string TelegramTitleTemplate { get; set; } = "{app}: {title}";
    [ObservableProperty] public partial string TelegramBodyTemplate { get; set; } = "{body}";
    [ObservableProperty] public partial string DiscordWebhookUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string DiscordUsername { get; set; } = "WinToastRelay";
    [ObservableProperty] public partial string DiscordTitleTemplate { get; set; } = "{app}: {title}";
    [ObservableProperty] public partial string DiscordBodyTemplate { get; set; } = "{body}";
    [ObservableProperty] public partial string AllowedApplications { get; set; } = string.Empty;
    [ObservableProperty] public partial string StatusDetail { get; set; } = "尚未启动监听";
    [ObservableProperty] public partial string CurrentSection { get; set; } = "overview";
    [ObservableProperty] public partial bool IsRelayRunning { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsChinese { get; set; } = true;
    [ObservableProperty] public partial bool StartWithWindows { get; set; }
    [ObservableProperty] public partial bool IsDestinationConfigured { get; set; }
    [ObservableProperty] public partial bool RelayManuallyStopped { get; set; }

    private ObservableCollection<ActivityEntry> _activity = new();
    public ObservableCollection<ActivityEntry> Activity
    {
        get => _activity;
        private set => SetProperty(ref _activity, value);
    }
    public ObservableCollection<NotificationApplicationOption> Applications { get; } = new();

    public bool HasApplications => Applications.Count > 0;
    public Visibility ApplicationsVisibility => HasApplications ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyApplicationsVisibility => HasApplications ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ActivityEmptyVisibility => Activity.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActivityListVisibility => Activity.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public MainPageViewModel(NotificationRelayService relayService)
    {
        _relayService = relayService;
        _relayService.StatusChanged += (_, status) => SetStatus(status);
        _relayService.ActivityReceived += (_, entry) => AddActivity(entry);
        _relayService.ApplicationObserved += (_, app) => AddApplication(app);
    }

    public Visibility OverviewVisibility => CurrentSection == "overview" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WebhookVisibility => CurrentSection == "webhook" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FiltersVisibility => CurrentSection == "filters" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActivityVisibility => CurrentSection == "activity" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SettingsVisibility => CurrentSection == "settings" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BarkVisibility => string.Equals(DeliveryMode, RelayDeliveryTarget.BarkMode, StringComparison.OrdinalIgnoreCase)
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WxPusherVisibility => string.Equals(DeliveryMode, RelayDeliveryTarget.WxPusherMode, StringComparison.OrdinalIgnoreCase)
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility JsonWebhookVisibility => string.Equals(DeliveryMode, RelayDeliveryTarget.JsonWebhookMode, StringComparison.OrdinalIgnoreCase)
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FeishuVisibility => string.Equals(DeliveryMode, RelayDeliveryTarget.FeishuMode, StringComparison.OrdinalIgnoreCase)
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TelegramVisibility => string.Equals(DeliveryMode, RelayDeliveryTarget.TelegramMode, StringComparison.OrdinalIgnoreCase)
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DiscordVisibility => string.Equals(DeliveryMode, RelayDeliveryTarget.DiscordMode, StringComparison.OrdinalIgnoreCase)
        ? Visibility.Visible : Visibility.Collapsed;

    public string AppSubtitle => IsChinese ? "原生 Windows 通知的实时推送桥接" : "A real-time delivery bridge for native Windows notifications";
    public string OverviewLabel => IsChinese ? "主页" : "Home";
    public string WebhookLabel => IsChinese ? "通知通道" : "Destination";
    public string FiltersLabel => IsChinese ? "筛选规则" : "Filters";
    public string ActivityLabel => IsChinese ? "传递记录" : "Activity";
    public string SettingsLabel => IsChinese ? "设置" : "Settings";
    public string OverviewTitle => IsChinese ? "把通知送到你需要的地方" : "Send notifications where they belong";
    public string OverviewDescription => string.Empty;
    public string RunningLabel => IsRelayRunning
        ? (IsChinese ? "正常转发中" : "Relaying normally")
        : IsDestinationConfigured && RelayManuallyStopped
            ? (IsChinese ? "已停止" : "Stopped")
            : (IsChinese ? "等待配置和权限" : "Waiting for setup and permission");
    public string StartRelayLabel => IsChinese ? "自动启动" : "Starts automatically";
    public string SetupCardTitle => IsChinese ? "先连接你的通知通道" : "Connect a notification destination";
    public string SetupCardDescription => IsChinese ? "默认使用 Bark，也支持 WxPusher、飞书、Telegram、Discord 和通用 JSON Webhook。" : "Bark is the default; WxPusher, Feishu, Telegram, Discord, and generic JSON webhooks are also supported.";
    public Visibility SetupCardVisibility => IsDestinationConfigured ? Visibility.Collapsed : Visibility.Visible;
    public string StatsTitle => IsChinese ? "概览" : "Overview";
    public string StatsDeliveriesLabel => IsChinese ? "14 天传递" : "14-day deliveries";
    public string StatsApplicationsLabel => IsChinese ? "通知应用" : "Notification apps";
    public string StatsSuccessLabel => IsChinese ? "成功率" : "Success rate";
    public string StatsDeliveriesValue => Activity.Count.ToString();
    public string StatsApplicationsValue => Applications
        .Select(item => item.Name)
        .Concat(Activity.Where(item => !string.Equals(item.App, "WinToastRelay", StringComparison.OrdinalIgnoreCase)).Select(item => item.App))
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count()
        .ToString();
    public string StatsSuccessValue => Activity.Count == 0 ? "—" : $"{Activity.Count(x => x.Succeeded) * 100 / Activity.Count}%";
    public string ConfigureDestinationLabel => IsChinese ? "配置通知通道" : "Configure destination";
    public string WebhookUrlLabel => IsChinese ? "Webhook 地址" : "Webhook URL";
    public string WebhookUrlPlaceholder => "https://example.com/hooks/wintoast";
    public string DeliveryModeLabel => IsChinese ? "传递方式" : "Delivery mode";
    public string BarkModeLabel => IsChinese ? "Bark（推荐）" : "Bark (recommended)";
    public string WxPusherModeLabel => "WxPusher";
    public string JsonWebhookModeLabel => IsChinese ? "通用 JSON Webhook" : "Generic JSON webhook";
    public string FeishuModeLabel => IsChinese ? "飞书自定义机器人" : "Feishu custom bot";
    public string TelegramModeLabel => "Telegram Bot";
    public string DiscordModeLabel => "Discord Webhook";
    public string BarkServerUrlLabel => IsChinese ? "Bark 服务地址" : "Bark server URL";
    public string BarkServerUrlDescription => IsChinese ? "官方服务为 https://api.day.app，也支持自托管 Bark。" : "Use https://api.day.app or your self-hosted Bark server.";
    public string BarkDeviceKeyLabel => IsChinese ? "设备密钥" : "Device key";
    public string BarkTemplateTitleLabel => IsChinese ? "标题模板" : "Title template";
    public string BarkTemplateBodyLabel => IsChinese ? "正文模板" : "Body template";
    public string BarkTemplateDescription => IsChinese ? "可用变量：{app}、{title}、{body}、{id}、{eventType}、{createdAt}" : "Variables: {app}, {title}, {body}, {id}, {eventType}, {createdAt}";
    public string BarkParametersLabel => IsChinese ? "附加 Bark 参数" : "Additional Bark parameters";
    public string BarkParametersDescription => IsChinese ? "每行 key=value，例如 sound=bell、group=work、level=timeSensitive、url=https://example.com；这些参数会作为 JSON 字段发送。" : "One key=value per line, e.g. sound=bell, group=work, level=timeSensitive, url=https://example.com. They are sent as JSON fields.";
    public string BarkRouteDescription => IsChinese ? "请求使用 Bark /push JSON POST。设备密钥保存在 Windows 凭据管理器中；标题、正文和附加参数都放在请求体中，不受 URL 长度限制；正文过长时会自动截断并标记。" : "Requests use Bark's /push JSON POST. The device key is stored in Windows Credential Manager; title, body, and additional parameters stay in the request body, avoiding URL length limits. Oversized text is truncated and marked automatically.";
    public string WxPusherRouteDescription => IsChinese ? "请求使用 WxPusher 标准推送 API，并校验响应业务码 1000。AppToken 仅保存在 Windows 凭据管理器中。" : "Requests use the WxPusher standard push API and require business code 1000 in the response. The app token is stored only in Windows Credential Manager.";
    public string WxPusherAppTokenLabel => "AppToken";
    public string WxPusherAppTokenDescription => IsChinese ? "在 WxPusher 管理后台创建应用后获取，以密钥方式保存。" : "Create an app in the WxPusher console to obtain it; it is stored as a credential.";
    public string WxPusherUidsLabel => IsChinese ? "接收用户 UID" : "Recipient UIDs";
    public string WxPusherUidsDescription => IsChinese ? "每行或用逗号分隔一个 UID；UID 与 Topic ID 至少配置一种，单次最多 2000 个 UID。" : "Enter one UID per line or separate them with commas. Configure UIDs or topic IDs; up to 2,000 UIDs are allowed per request.";
    public string WxPusherTopicIdsLabel => IsChinese ? "接收主题 Topic ID" : "Recipient topic IDs";
    public string WxPusherTopicIdsDescription => IsChinese ? "每行或用逗号分隔一个数字 Topic ID；单次最多 5 个。" : "Enter one numeric topic ID per line or separate them with commas; up to five are allowed per request.";
    public string WxPusherSummaryTemplateLabel => IsChinese ? "摘要模板" : "Summary template";
    public string WxPusherContentTemplateLabel => IsChinese ? "正文模板" : "Content template";
    public string WxPusherTemplateDescription => IsChinese ? "以纯文本发送。可用变量：{app}、{title}、{body}、{id}、{eventType}、{createdAt}；摘要最长 100 个字符。" : "Sent as plain text. Variables: {app}, {title}, {body}, {id}, {eventType}, {createdAt}. Summaries are limited to 100 characters.";
    public string FeishuRouteDescription => IsChinese ? "使用飞书自定义机器人 Webhook 发送文本消息。可选签名密钥会按飞书规范生成 timestamp 和 sign。" : "Sends text messages through a Feishu custom bot webhook. An optional signing secret generates timestamp and sign fields using Feishu's format.";
    public string FeishuWebhookUrlLabel => IsChinese ? "飞书 Webhook 地址" : "Feishu webhook URL";
    public string FeishuWebhookUrlDescription => IsChinese ? "从飞书群聊的自定义机器人设置中复制 Webhook 地址。" : "Copy the webhook URL from the Feishu custom bot settings.";
    public string FeishuSecretLabel => IsChinese ? "签名密钥（可选）" : "Signing secret (optional)";
    public string FeishuSecretDescription => IsChinese ? "如果机器人启用了签名校验，请填写安全设置中的密钥。" : "Fill this in when signature verification is enabled for the bot.";
    public string FeishuTemplateTitleLabel => IsChinese ? "消息标题模板" : "Message title template";
    public string FeishuTemplateBodyLabel => IsChinese ? "消息正文模板" : "Message body template";
    public string TelegramRouteDescription => IsChinese ? "使用 Telegram Bot API 的 sendMessage 接口发送纯文本消息。" : "Sends text messages through the Telegram Bot API sendMessage method.";
    public string TelegramApiUrlLabel => IsChinese ? "Telegram API 地址" : "Telegram API URL";
    public string TelegramApiUrlDescription => IsChinese ? "默认使用 https://api.telegram.org，也支持自建 Bot API 服务。" : "Uses https://api.telegram.org by default; self-hosted Bot API servers are supported.";
    public string TelegramBotTokenLabel => IsChinese ? "Bot Token" : "Bot token";
    public string TelegramBotTokenDescription => IsChinese ? "从 BotFather 获取，保存在 Windows 凭据管理器中。" : "Obtain it from BotFather; it is stored in Windows Credential Manager.";
    public string TelegramChatIdLabel => IsChinese ? "Chat ID" : "Chat ID";
    public string TelegramChatIdDescription => IsChinese ? "填写目标私聊、群组或频道的 Chat ID。" : "Enter the target private chat, group, or channel ID.";
    public string TelegramParseModeLabel => IsChinese ? "解析模式（可选）" : "Parse mode (optional)";
    public string TelegramParseModeDescription => IsChinese ? "可填写 MarkdownV2 或 HTML；留空则按纯文本发送。" : "Use MarkdownV2 or HTML; leave empty for plain text.";
    public string TelegramTemplateTitleLabel => IsChinese ? "消息标题模板" : "Message title template";
    public string TelegramTemplateBodyLabel => IsChinese ? "消息正文模板" : "Message body template";
    public string DiscordRouteDescription => IsChinese ? "使用 Discord Webhook 发送文本消息，并禁用未显式提及的通知。" : "Sends text messages through a Discord webhook with unsolicited mentions disabled.";
    public string DiscordWebhookUrlLabel => IsChinese ? "Discord Webhook 地址" : "Discord webhook URL";
    public string DiscordWebhookUrlDescription => IsChinese ? "从 Discord 频道的集成设置中复制 Webhook 地址。" : "Copy the webhook URL from the channel integration settings.";
    public string DiscordUsernameLabel => IsChinese ? "显示名称（可选）" : "Display name (optional)";
    public string DiscordUsernameDescription => IsChinese ? "留空则使用 Discord Webhook 的默认名称。" : "Leave empty to use the webhook's default name.";
    public string DiscordTemplateTitleLabel => IsChinese ? "消息标题模板" : "Message title template";
    public string DiscordTemplateBodyLabel => IsChinese ? "消息正文模板" : "Message body template";
    public string BearerTokenLabel => IsChinese ? "Bearer Token（可选）" : "Bearer token (optional)";
    public string BearerTokenDescription => IsChinese ? "令牌保存于 Windows 凭据管理器，不写入配置文件。" : "Stored in Windows Credential Manager, never in the settings file.";
    public string SaveLabel => IsChinese ? "保存设置" : "Save settings";
    public string TestLabel => IsChinese ? "发送测试" : "Send test";
    public string FiltersTitle => IsChinese ? "应用筛选" : "Application filters";
    public string FiltersDescription => IsChinese ? "通知中心中出现过的应用会列在这里。默认转发，关闭开关即可排除该应用。" : "Apps that have appeared in Notification Center show up here. They are relayed by default; turn one off to exclude it.";
    public string RefreshApplicationsLabel => IsChinese ? "刷新应用列表" : "Refresh app list";
    public string EmptyApplicationsLabel => IsChinese ? "尚未发现通知应用" : "No notification apps found yet";
    public string EmptyApplicationsDescription => IsChinese ? "授予通知权限后，收到一条通知或打开通知中心，再回到此页刷新即可。" : "After granting notification access, receive a notification or open Notification Center, then refresh this page.";
    public string ActivityTitle => IsChinese ? "最近传递" : "Recent deliveries";
    public string EmptyActivityLabel => IsChinese ? "还没有传递记录" : "No deliveries yet";
    public string EmptyActivityDescription => IsChinese ? "新的通知及测试发送结果会显示在这里。" : "New notification and test-delivery results will appear here.";
    public string SettingsTitle => IsChinese ? "偏好设置" : "Preferences";
    public string AboutTitle => IsChinese ? "关于 WinToastRelay" : "About WinToastRelay";
    public string AboutDescription => IsChinese ? "使用 Windows 原生通知事件，将通知实时转发到 Bark、WxPusher、飞书、Telegram、Discord 或 JSON Webhook。" : "Uses native Windows notification events to relay notifications to Bark, WxPusher, Feishu, Telegram, Discord, or a JSON webhook.";
    public string MadeByLabel => "Made by RavelloH";
    public string GithubLabel => IsChinese ? "GitHub 仓库" : "GitHub repository";
    public string GithubUrl => "https://github.com/RavelloH/WinToastRelay";
    public string VersionLabel => IsChinese ? $"版本 {CurrentVersion}" : $"Version {CurrentVersion}";
    public string LanguageLabel => IsChinese ? "界面语言" : "Interface language";
    public string LanguageDescription => IsChinese ? "选择应用界面语言" : "Choose the application language";
    public string ChineseLanguageOption => IsChinese ? "简体中文" : "Chinese (Simplified)";
    public string EnglishLanguageOption => "English";
    public string PermissionHint => IsChinese ? "配置有效后会自动启动；首次运行时 Windows 会请求通知访问权限。" : "Listening starts automatically once configured; Windows asks for notification access on first run.";
    public string PermissionInfoTitle => IsChinese ? "隐私与权限" : "Privacy and permission";
    public string StatusPrefix => IsChinese ? "状态" : "Status";
    public string StartWithWindowsLabel => IsChinese ? "登录 Windows 时启动" : "Start with Windows";
    public string StartWithWindowsDescription => IsChinese ? "启动后保持在系统托盘中，自动恢复已授权的通知监听。" : "Start minimized to the tray and resume authorized notification listening.";
    public string StartWithWindowsButtonLabel => StartWithWindows ? (IsChinese ? "已启用" : "Enabled") : (IsChinese ? "未启用" : "Disabled");

    private static string CurrentVersion
    {
        get
        {
            try
            {
                var version = Windows.ApplicationModel.Package.Current.Id.Version;
                return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            }
            catch
            {
                return typeof(MainPageViewModel).Assembly.GetName().Version?.ToString(4) ?? "开发版";
            }
        }
    }

    partial void OnCurrentSectionChanged(string value)
    {
        OnPropertyChanged(nameof(OverviewVisibility));
        OnPropertyChanged(nameof(WebhookVisibility));
        OnPropertyChanged(nameof(FiltersVisibility));
        OnPropertyChanged(nameof(ActivityVisibility));
        OnPropertyChanged(nameof(SettingsVisibility));
    }

    partial void OnIsDestinationConfiguredChanged(bool value) => OnPropertyChanged(nameof(SetupCardVisibility));

    partial void OnDeliveryModeChanged(string value)
    {
        OnPropertyChanged(nameof(BarkVisibility));
        OnPropertyChanged(nameof(WxPusherVisibility));
        OnPropertyChanged(nameof(JsonWebhookVisibility));
        OnPropertyChanged(nameof(FeishuVisibility));
        OnPropertyChanged(nameof(TelegramVisibility));
        OnPropertyChanged(nameof(DiscordVisibility));
    }

    partial void OnIsRelayRunningChanged(bool value) => OnPropertyChanged(nameof(RunningLabel));
    partial void OnRelayManuallyStoppedChanged(bool value) => OnPropertyChanged(nameof(RunningLabel));

    [RelayCommand]
    private async Task ToggleRelayAsync()
    {
        if (IsRelayRunning)
        {
            await _relayService.StopAsync();
            IsRelayRunning = false;
            _settings.RelayEnabled = false;
            RelayManuallyStopped = true;
            _settings.RelayManuallyStopped = true;
            await _settingsStore.SaveAsync(_settings);
            SetStatus(IsChinese ? "转发已暂停" : "Relay paused");
            return;
        }

        RelayManuallyStopped = false;
        _settings.RelayManuallyStopped = false;
        await StartRelayAutomaticallyAsync();
    }

    partial void OnIsChineseChanged(bool value)
    {
        OnPropertyChanged(string.Empty);
        // StatusDetail stores the last message in its source language. Re-localize it when
        // the user switches languages so a previously shown Chinese status cannot remain on
        // the English home page (and vice versa).
        SetStatus(_statusSource);
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;

        // Settings and startup-task state are independent. Read them together so the
        // first frame does not wait for two unrelated storage calls in sequence.
        var settingsTask = _settingsStore.LoadAsync();
        var startupTask = _startupTaskService.IsEnabledAsync();
        _settings = await settingsTask;
        DeliveryMode = NormalizeDeliveryMode(_settings.DeliveryMode);
        WebhookUrl = _settings.WebhookUrl;
        BarkServerUrl = _settings.BarkServerUrl;
        var storedBarkDeviceKey = _secretStore.GetBarkDeviceKey();
        var legacyBarkDeviceKey = _settings.BarkDeviceKey;
        if (string.IsNullOrWhiteSpace(storedBarkDeviceKey) && !string.IsNullOrWhiteSpace(legacyBarkDeviceKey))
        {
            // Migrate the legacy plaintext value once, then remove it from JSON on disk.
            storedBarkDeviceKey = legacyBarkDeviceKey;
            _secretStore.SaveBarkDeviceKey(storedBarkDeviceKey);
            _settings.BarkDeviceKey = string.Empty;
            await _settingsStore.SaveAsync(_settings);
        }
        BarkDeviceKey = storedBarkDeviceKey;
        BarkTitleTemplate = _settings.BarkTitleTemplate;
        BarkBodyTemplate = _settings.BarkBodyTemplate;
        BarkParameters = _settings.BarkParameters;
        WxPusherUids = _settings.WxPusherUids;
        WxPusherTopicIds = _settings.WxPusherTopicIds;
        WxPusherSummaryTemplate = _settings.WxPusherSummaryTemplate;
        WxPusherContentTemplate = _settings.WxPusherContentTemplate;
        FeishuWebhookUrl = _settings.FeishuWebhookUrl;
        FeishuTitleTemplate = _settings.FeishuTitleTemplate;
        FeishuBodyTemplate = _settings.FeishuBodyTemplate;
        TelegramApiUrl = string.IsNullOrWhiteSpace(_settings.TelegramApiUrl) ? "https://api.telegram.org" : _settings.TelegramApiUrl;
        TelegramChatId = _settings.TelegramChatId;
        TelegramParseMode = _settings.TelegramParseMode;
        TelegramTitleTemplate = _settings.TelegramTitleTemplate;
        TelegramBodyTemplate = _settings.TelegramBodyTemplate;
        DiscordWebhookUrl = _settings.DiscordWebhookUrl;
        DiscordUsername = _settings.DiscordUsername;
        DiscordTitleTemplate = _settings.DiscordTitleTemplate;
        DiscordBodyTemplate = _settings.DiscordBodyTemplate;
        if (!BarkParameters.Contains("icon=", StringComparison.OrdinalIgnoreCase))
            BarkParameters = string.IsNullOrWhiteSpace(BarkParameters)
                ? "level=active\nicon=https://raw.ravelloh.com/icon/WinToastRelay.png"
                : BarkParameters.TrimEnd() + "\nicon=https://raw.ravelloh.com/icon/WinToastRelay.png";
        _settings.BarkParameters = BarkParameters;
        AllowedApplications = _settings.AllowedApplications;
        BearerToken = _secretStore.Get();
        WxPusherAppToken = _secretStore.GetWxPusherAppToken();
        FeishuSecret = _secretStore.GetFeishuSecret();
        TelegramBotToken = _secretStore.GetTelegramBotToken();
        IsChinese = !string.Equals(_settings.Language, "en-US", StringComparison.OrdinalIgnoreCase);
        RelayManuallyStopped = _settings.RelayManuallyStopped;
        StartWithWindows = await startupTask;
        _settings.ApplicationFilterEnabled |= ParseAllowedApplications().Count > 0;
        _relayService.Configure(CreateTarget(), AllowedApplications, _settings.ApplicationFilterEnabled);
        IsDestinationConfigured = WebhookClient.IsValidConfiguration(CreateTarget());

        _initialized = true;
        // History, notification enumeration, permission checks, and icon loading can all
        // involve storage or WinRT calls. Defer them until the first frame is rendered so
        // launching the app remains responsive.
        _ = InitializeDeferredAsync();
    }

    private async Task InitializeDeferredAsync()
    {
        // Yield once to let WinUI paint the initial page before doing any deferred work.
        await Task.Yield();
        try
        {
            // Load the persisted history before subscribing to delivery events. This lets us
            // replace the bound collection in one operation without losing a new entry.
            await LoadActivityAsync();
            await StartRelayAutomaticallyAsync();

            // Application names are discovered by the relay's initial snapshot. Loading
            // logos is intentionally deferred until the Filters page is opened.
        }
        catch (Exception ex)
        {
            SetStatus($"Initialization error: {ex.Message}");
        }
    }

    private async Task StartRelayAutomaticallyAsync()
    {
        await _relayStartLock.WaitAsync();
        try
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                if (!WebhookClient.IsValidConfiguration(CreateTarget()))
                {
                    IsRelayRunning = false;
                    IsDestinationConfigured = false;
                    SetStatus(IsChinese ? "请先完成通知通道配置，保存后将自动开始监听" : "Complete the destination configuration; listening starts automatically after saving");
                    return;
                }

                IsDestinationConfigured = true;
                if (RelayManuallyStopped)
                {
                    IsRelayRunning = false;
                    SetStatus(IsChinese ? "转发已停止" : "Relay stopped");
                    return;
                }

                _relayService.Configure(CreateTarget(), AllowedApplications, _settings.ApplicationFilterEnabled);
                var result = await _relayService.StartAsync();
                IsRelayRunning = result.Succeeded;
                if (result.Succeeded) RelayManuallyStopped = false;
                SetStatus(result.Succeeded ? (IsChinese ? "通知监听已自动启动" : "Notification listening started automatically") : result.Detail);
                _settings.RelayEnabled = IsRelayRunning;
                _settings.RelayManuallyStopped = RelayManuallyStopped;
                await _settingsStore.SaveAsync(_settings);
            }
            finally
            {
                IsBusy = false;
            }
        }
        finally
        {
            _relayStartLock.Release();
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        await SaveConfigurationAsync();
        await StartRelayAutomaticallyAsync();
        SetStatus(IsChinese ? "设置已保存" : "Settings saved");
    }

    [RelayCommand]
    private async Task TestWebhookAsync()
    {
        await SaveConfigurationAsync();
        _relayService.Configure(CreateTarget(), AllowedApplications, _settings.ApplicationFilterEnabled);
        IsDestinationConfigured = WebhookClient.IsValidConfiguration(CreateTarget());
        var result = await _relayService.SendTestAsync();
        SetStatus(result.Succeeded ? (IsChinese ? "测试发送成功" : "Test delivered") : result.Detail);
        AddActivity(new ActivityEntry(
            DateTimeOffset.Now,
            "WinToastRelay",
            IsChinese ? "测试发送" : "Test delivery",
            result.Succeeded,
            result.Detail) { Body = IsChinese ? "你的通知通道连接正常。" : "Your notification destination is working." });
    }

    [RelayCommand]
    private async Task ToggleLanguageAsync()
    {
        IsChinese = !IsChinese;
        await SaveConfigurationAsync();
    }

    [RelayCommand]
    private void ConfigureDestination() => CurrentSection = "webhook";

    [RelayCommand]
    private async Task ToggleStartupAsync()
    {
        var enabled = await _startupTaskService.SetEnabledAsync(!StartWithWindows);
        StartWithWindows = enabled;
        _settings.StartWithWindows = enabled;
        await _settingsStore.SaveAsync(_settings);
        SetStatus(enabled
            ? (IsChinese ? "已启用登录启动" : "Start with Windows enabled")
            : (IsChinese ? "已关闭登录启动" : "Start with Windows disabled"));
    }

    private async Task SaveConfigurationAsync()
    {
        _settings.WebhookUrl = WebhookUrl.Trim();
        _settings.DeliveryMode = NormalizeDeliveryMode(DeliveryMode);
        _settings.BarkServerUrl = BarkServerUrl.Trim();
        // Bark's device key is a credential, not a regular application setting.
        _settings.BarkDeviceKey = string.Empty;
        _settings.BarkTitleTemplate = BarkTitleTemplate;
        _settings.BarkBodyTemplate = BarkBodyTemplate;
        _settings.BarkParameters = BarkParameters;
        _settings.WxPusherUids = WxPusherUids;
        _settings.WxPusherTopicIds = WxPusherTopicIds;
        _settings.WxPusherSummaryTemplate = WxPusherSummaryTemplate;
        _settings.WxPusherContentTemplate = WxPusherContentTemplate;
        _settings.FeishuWebhookUrl = FeishuWebhookUrl.Trim();
        _settings.FeishuTitleTemplate = FeishuTitleTemplate;
        _settings.FeishuBodyTemplate = FeishuBodyTemplate;
        _settings.TelegramApiUrl = TelegramApiUrl.Trim();
        _settings.TelegramChatId = TelegramChatId.Trim();
        _settings.TelegramParseMode = TelegramParseMode.Trim();
        _settings.TelegramTitleTemplate = TelegramTitleTemplate;
        _settings.TelegramBodyTemplate = TelegramBodyTemplate;
        _settings.DiscordWebhookUrl = DiscordWebhookUrl.Trim();
        _settings.DiscordUsername = DiscordUsername.Trim();
        _settings.DiscordTitleTemplate = DiscordTitleTemplate;
        _settings.DiscordBodyTemplate = DiscordBodyTemplate;
        _settings.AllowedApplications = AllowedApplications;
        _settings.Language = IsChinese ? "zh-CN" : "en-US";
        _settings.RelayEnabled = IsRelayRunning;
        _settings.StartWithWindows = StartWithWindows;
        _secretStore.Save(BearerToken.Trim());
        _secretStore.SaveBarkDeviceKey(BarkDeviceKey.Trim());
        _secretStore.SaveWxPusherAppToken(WxPusherAppToken.Trim());
        _secretStore.SaveFeishuSecret(FeishuSecret.Trim());
        _secretStore.SaveTelegramBotToken(TelegramBotToken.Trim());
        await _settingsStore.SaveAsync(_settings);
        _relayService.Configure(CreateTarget(), AllowedApplications, _settings.ApplicationFilterEnabled);
    }

    private RelayDeliveryTarget CreateTarget() => new(
        DeliveryMode,
        WebhookUrl.Trim(),
        BearerToken.Trim(),
        BarkServerUrl.Trim(),
        BarkDeviceKey.Trim(),
        BarkTitleTemplate,
        BarkBodyTemplate,
        BarkParameters,
        WxPusherAppToken: WxPusherAppToken.Trim(),
        WxPusherUids: WxPusherUids,
        WxPusherTopicIds: WxPusherTopicIds,
        WxPusherSummaryTemplate: WxPusherSummaryTemplate,
        WxPusherContentTemplate: WxPusherContentTemplate,
        FeishuWebhookUrl: FeishuWebhookUrl.Trim(),
        FeishuSecret: FeishuSecret.Trim(),
        FeishuTitleTemplate: FeishuTitleTemplate,
        FeishuBodyTemplate: FeishuBodyTemplate,
        TelegramApiUrl: string.IsNullOrWhiteSpace(TelegramApiUrl) ? "https://api.telegram.org" : TelegramApiUrl.Trim(),
        TelegramBotToken: TelegramBotToken.Trim(),
        TelegramChatId: TelegramChatId.Trim(),
        TelegramParseMode: TelegramParseMode.Trim(),
        TelegramTitleTemplate: TelegramTitleTemplate,
        TelegramBodyTemplate: TelegramBodyTemplate,
        DiscordWebhookUrl: DiscordWebhookUrl.Trim(),
        DiscordUsername: DiscordUsername.Trim(),
        DiscordTitleTemplate: DiscordTitleTemplate,
        DiscordBodyTemplate: DiscordBodyTemplate);

    private static string NormalizeDeliveryMode(string deliveryMode)
    {
        if (string.Equals(deliveryMode, RelayDeliveryTarget.JsonWebhookMode, StringComparison.OrdinalIgnoreCase))
            return RelayDeliveryTarget.JsonWebhookMode;
        if (string.Equals(deliveryMode, RelayDeliveryTarget.WxPusherMode, StringComparison.OrdinalIgnoreCase))
            return RelayDeliveryTarget.WxPusherMode;
        if (string.Equals(deliveryMode, RelayDeliveryTarget.FeishuMode, StringComparison.OrdinalIgnoreCase))
            return RelayDeliveryTarget.FeishuMode;
        if (string.Equals(deliveryMode, RelayDeliveryTarget.TelegramMode, StringComparison.OrdinalIgnoreCase))
            return RelayDeliveryTarget.TelegramMode;
        if (string.Equals(deliveryMode, RelayDeliveryTarget.DiscordMode, StringComparison.OrdinalIgnoreCase))
            return RelayDeliveryTarget.DiscordMode;
        return RelayDeliveryTarget.BarkMode;
    }

    private void SetStatus(string status)
    {
        _statusSource = status;
        var localizedStatus = LocalizeStatus(status);
        if (App.DispatcherQueue is null) { StatusDetail = localizedStatus; return; }
        App.DispatcherQueue.TryEnqueue(() => StatusDetail = localizedStatus);
    }

    private void AddActivity(ActivityEntry entry)
    {
        void Add()
        {
            Activity.Insert(0, entry);
            var cutoff = DateTimeOffset.Now.AddDays(-14);
            for (var i = Activity.Count - 1; i >= 0; i--)
                if (Activity[i].Time < cutoff) Activity.RemoveAt(i);
            OnPropertyChanged(nameof(ActivityEmptyVisibility));
            OnPropertyChanged(nameof(ActivityListVisibility));
            OnPropertyChanged(nameof(StatsDeliveriesValue));
            OnPropertyChanged(nameof(StatsApplicationsValue));
            OnPropertyChanged(nameof(StatsSuccessValue));
            _ = SaveActivityAsync();
        }

        if (App.DispatcherQueue is null) Add();
        else App.DispatcherQueue.TryEnqueue(Add);
    }

    [RelayCommand]
    private async Task RefreshApplicationsAsync() => await EnsureApplicationsLoadedAsync(force: true);

    public async Task EnsureApplicationsLoadedAsync(bool force = false)
    {
        if (_applicationsLoaded && !force) return;

        await _applicationsLoadLock.WaitAsync();
        try
        {
            if (_applicationsLoaded && !force) return;

            var enabled = ParseAllowedApplications();
            foreach (var app in await _relayService.GetAvailableApplicationsAsync())
                AddApplicationCore(app.Name, !_settings.ApplicationFilterEnabled || enabled.Contains(app.Name), app.Icon);
            _applicationsLoaded = true;
        }
        catch (Exception ex)
        {
            SetStatus($"Application list error: {ex.Message}");
        }
        finally
        {
            _applicationsLoadLock.Release();
        }
    }

    private void AddApplication(string application)
    {
        void Add() => AddApplicationCore(application, !_settings.ApplicationFilterEnabled || ParseAllowedApplications().Contains(application));
        if (App.DispatcherQueue is null) Add();
        else App.DispatcherQueue.TryEnqueue(Add);
    }

    private void AddApplicationCore(string application, bool isEnabled, Microsoft.UI.Xaml.Media.ImageSource? icon = null)
    {
        var existing = Applications.FirstOrDefault(item => string.Equals(item.Name, application, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            // Names are discovered quickly from the listener snapshot; logo retrieval is
            // deferred. Update the placeholder when the real icon arrives later.
            if (icon is not null && existing.IconSource is null)
                existing.IconSource = icon;
            return;
        }
        var item = new NotificationApplicationOption(application, isEnabled, icon);
        item.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(NotificationApplicationOption.IsEnabled))
                UpdateAllowedApplications();
        };
        Applications.Add(item);
        OnPropertyChanged(nameof(HasApplications));
        OnPropertyChanged(nameof(ApplicationsVisibility));
        OnPropertyChanged(nameof(EmptyApplicationsVisibility));
        OnPropertyChanged(nameof(StatsApplicationsValue));
    }

    private HashSet<string> ParseAllowedApplications() => AllowedApplications
        .Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void UpdateAllowedApplications()
    {
        AllowedApplications = string.Join(Environment.NewLine, Applications.Where(item => item.IsEnabled).Select(item => item.Name));
        _settings.ApplicationFilterEnabled = Applications.Any(item => !item.IsEnabled);
        _relayService.Configure(CreateTarget(), AllowedApplications, _settings.ApplicationFilterEnabled);
        _ = SaveConfigurationAsync();
    }

    private string LocalizeStatus(string status) => status switch
    {
        "尚未启动监听" or "Not listening yet" => IsChinese ? "尚未启动监听" : "Not listening yet",
        "Listening for Windows notifications" or "正在监听 Windows 通知" => IsChinese ? "正在监听 Windows 通知" : "Listening for Windows notifications",
        "Relay paused" or "转发已暂停" => IsChinese ? "转发已暂停" : "Relay paused",
        "转发已停止" or "Relay stopped" => IsChinese ? "转发已停止" : "Relay stopped",
        "通知监听已自动启动" or "Notification listening started automatically" => IsChinese ? "通知监听已自动启动" : "Notification listening started automatically",
        "请先完成通知通道配置，保存后将自动开始监听" or "Complete the destination configuration; listening starts automatically after saving" => IsChinese
            ? "请先完成通知通道配置，保存后将自动开始监听"
            : "Complete the destination configuration; listening starts automatically after saving",
        "设置已保存" or "Settings saved" => IsChinese ? "设置已保存" : "Settings saved",
        "测试发送成功" or "Test delivered" => IsChinese ? "测试发送成功" : "Test delivered",
        "已启用登录启动" or "Start with Windows enabled" => IsChinese ? "已启用登录启动" : "Start with Windows enabled",
        "已关闭登录启动" or "Start with Windows disabled" => IsChinese ? "已关闭登录启动" : "Start with Windows disabled",
        _ => status
    };

    private async Task LoadActivityAsync()
    {
        if (_activityLoaded) return;
        _activityLoaded = true;
        try
        {
            var file = await Windows.Storage.ApplicationData.Current.LocalFolder.TryGetItemAsync(ActivityFileName) as Windows.Storage.StorageFile;
            if (file is null) return;
            var json = await Windows.Storage.FileIO.ReadTextAsync(file);
            // JSON parsing and filtering can be noticeable with a large activity file;
            // keep that CPU work off the UI thread.
            var cutoff = DateTimeOffset.Now.AddDays(-14);
            var recent = await Task.Run(() =>
            {
                var loaded = JsonSerializer.Deserialize(json, AppJsonContext.Default.ListActivityEntry) ?? [];
                return loaded
                    .Where(x => x.Time >= cutoff)
                    .OrderByDescending(x => x.Time)
                    .ToList();
            });

            void ApplyLoadedActivity()
            {
                // Replacing the collection emits one property change instead of hundreds of
                // individual Add notifications, which avoids repeated layout passes.
                Activity = new ObservableCollection<ActivityEntry>(recent);
                OnPropertyChanged(nameof(ActivityEmptyVisibility));
                OnPropertyChanged(nameof(ActivityListVisibility));
                OnPropertyChanged(nameof(StatsDeliveriesValue));
                OnPropertyChanged(nameof(StatsSuccessValue));
                OnPropertyChanged(nameof(StatsApplicationsValue));
            }

            if (App.DispatcherQueue is null || App.DispatcherQueue.HasThreadAccess)
            {
                ApplyLoadedActivity();
            }
            else
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                App.DispatcherQueue.TryEnqueue(() =>
                {
                    try { ApplyLoadedActivity(); completion.SetResult(); }
                    catch (Exception ex) { completion.SetException(ex); }
                });
                await completion.Task;
            }
        }
        catch (JsonException) { }
        catch (Exception) { }
    }

    private async Task SaveActivityAsync()
    {
        try
        {
            // Copy the UI-bound collection first, then filter and serialize the snapshot off
            // the UI thread so a large 14-day history does not stall navigation or rendering.
            var snapshot = Activity.ToList();
            var cutoff = DateTimeOffset.Now.AddDays(-14);
            var json = await Task.Run(() => JsonSerializer.Serialize(
                snapshot.Where(x => x.Time >= cutoff).ToList(),
                AppJsonContext.Default.ListActivityEntry));
            var file = await Windows.Storage.ApplicationData.Current.LocalFolder.CreateFileAsync(ActivityFileName, Windows.Storage.CreationCollisionOption.ReplaceExisting);
            await Windows.Storage.FileIO.WriteTextAsync(file, json);
        }
        catch { }
    }
}
