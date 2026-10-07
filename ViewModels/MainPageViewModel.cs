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
    private RelayDeliveryTarget? _savedTarget;
    private const string ActivityFileName = "delivery-activity.json";
    private readonly SemaphoreSlim _applicationsLoadLock = new(1, 1);
    private readonly SemaphoreSlim _relayStartLock = new(1, 1);
    private bool _initialized;
    private bool _activityLoaded;
    private bool _applicationsLoaded;
    private const int WebhookSecretLimit = 32;
    private string _statusSource = "尚未启动监听";
    private bool _isRefreshingHttpApprovalToggle;
    private string _webhookValidationErrorSource = string.Empty;

    [ObservableProperty] public partial string WebhookUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial string WebhookJsonTemplate { get; set; } = string.Empty;
    [ObservableProperty] public partial string WebhookHeaders { get; set; } = string.Empty;
    [ObservableProperty] public partial string WebhookPreviewText { get; set; } = string.Empty;
    [ObservableProperty] public partial string WebhookValidationError { get; set; } = string.Empty;
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
    [ObservableProperty] public partial bool AllowUnencryptedHttp { get; set; }

    private ObservableCollection<ActivityEntry> _activity = new();
    public ObservableCollection<ActivityEntry> Activity
    {
        get => _activity;
        private set => SetProperty(ref _activity, value);
    }
    public ObservableCollection<NotificationApplicationOption> Applications { get; } = new();
    public ObservableCollection<WebhookSecretOption> WebhookSecrets { get; } = new();
    public Visibility WebhookPreviewVisibility => string.IsNullOrEmpty(WebhookPreviewText) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility WebhookValidationErrorVisibility => string.IsNullOrEmpty(WebhookValidationError) ? Visibility.Collapsed : Visibility.Visible;
    public bool CanAddWebhookSecret => WebhookSecrets.Count < WebhookSecretLimit;

    public bool HasApplications => Applications.Count > 0;
    public Visibility ApplicationsVisibility => HasApplications ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyApplicationsVisibility => HasApplications ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ActivityEmptyVisibility => Activity.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActivityListVisibility => Activity.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public MainPageViewModel(NotificationRelayService relayService)
    {
        _relayService = relayService;
        WebhookSecrets.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanAddWebhookSecret));
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
    public string RunningLabel => !IsDestinationConfigured
        ? (IsChinese ? "等待配置和权限" : "Waiting for setup and permission")
        : IsRelayRunning
            ? (IsChinese ? "正常转发中" : "Relaying normally")
            : RelayManuallyStopped
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
    public string WebhookJsonTemplateLabel => IsChinese ? "JSON 正文模板" : "JSON body template";
    public string WebhookJsonTemplatePlaceholder => IsChinese ? "示例：{\"title\": \"{title}\", \"body\": \"{body}\"}" : "Example: {\"title\": \"{title}\", \"body\": \"{body}\"}";
    public string WebhookJsonTemplateDescription => IsChinese
        ? "留空时沿用原有通知 JSON 格式。自定义模板必须是 JSON 对象，只替换字符串值；可用变量：{app}、{title}、{body}、{Content}、{PackageName}、{id}、{eventType}、{createdAt}、{deliveryId}、{secret:NAME}。"
        : "Leave empty to keep the existing notification JSON format. Custom templates must be JSON objects; variables replace string values only. Available: {app}, {title}, {body}, {Content}, {PackageName}, {id}, {eventType}, {createdAt}, {deliveryId}, {secret:NAME}.";
    public string WebhookHeadersLabel => IsChinese ? "自定义请求头" : "Custom headers";
    public string WebhookHeadersDescription => IsChinese
        ? "每行填写 Name: Value，最多 32 项，值须为 ASCII 字符且不能含换行。变量可用于值中；敏感内容请引用下方密钥，例如 X-Api-Key: {secret:API_KEY}。不要填写 Host、Content-Type、Content-Length、连接类等传输头。"
        : "Enter one Name: Value per line, up to 32 entries. Values must use ASCII without line breaks. Variables are supported in values; use a secret for sensitive values, for example X-Api-Key: {secret:API_KEY}. Do not set Host, Content-Type, Content-Length, or connection headers.";
    public string WebhookSecretsLabel => IsChinese ? "密钥变量" : "Secret variables";
    public string WebhookSecretsDescription => IsChinese
        ? "密钥保存在 Windows 凭据管理器，不写入普通设置。名称以英文字母开头，后续可用字母、数字、下划线或连字符，最多 64 个字符。模板和请求头中的字面内容会保存在普通设置中；敏感值请放在这里。最多 32 项。"
        : "Secrets are stored in Windows Credential Manager, not ordinary settings. Names start with an ASCII letter, then use letters, digits, underscores, or hyphens, up to 64 characters. Literal template and header text is saved in ordinary settings; put sensitive values here. Up to 32 entries.";
    public string WebhookSecretNamePlaceholder => IsChinese ? "名称（例如 API_KEY）" : "Name (for example API_KEY)";
    public string WebhookSecretValuePlaceholder => IsChinese ? "密钥值" : "Secret value";
    public string AddWebhookSecretLabel => IsChinese ? "添加密钥" : "Add secret";
    public string RemoveWebhookSecretLabel => IsChinese ? "移除" : "Remove";
    public string PreviewWebhookLabel => IsChinese ? "预览示例" : "Preview sample";
    public string WebhookPreviewLabel => IsChinese ? "示例预览（不发送）" : "Sample preview (not sent)";
    public string WebhookBearerConflictHint => IsChinese
        ? "自定义 Authorization 请求头不能与上方 Bearer Token 同时使用。预览会隐藏所有请求头值。"
        : "A custom Authorization header cannot be combined with the Bearer token above. Header values are hidden in the preview.";
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
    public string HttpApprovalLabel => IsChinese ? "允许未加密 HTTP" : "Allow unencrypted HTTP";
    public string HttpApprovalDescription => IsChinese
        ? $"启用后，通知内容和凭据会以明文传输。仅对可信地址启用；授权仅适用于当前通道和地址：{GetCurrentChannelLabel()} · {GetCurrentEndpoint()}"
        : $"When enabled, notification content and credentials are sent in plaintext. Enable only for trusted destinations; this permission applies only to the current channel and address: {GetCurrentChannelLabel()} · {GetCurrentEndpoint()}";
    public string HttpApprovalTransportHint => IsChinese
        ? "仅对有效的非回环 HTTP 地址生效。HTTPS 或本机回环地址无需此权限。"
        : "Enabled only for valid non-loopback HTTP URLs. HTTPS and loopback destinations do not need this permission.";
    public bool IsHttpApprovalToggleEnabled => EndpointTransportPolicy.RequiresHttpApproval(GetCurrentEndpoint());
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

    partial void OnIsDestinationConfiguredChanged(bool value)
    {
        OnPropertyChanged(nameof(SetupCardVisibility));
        OnPropertyChanged(nameof(RunningLabel));
    }

    partial void OnWebhookPreviewTextChanged(string value) => OnPropertyChanged(nameof(WebhookPreviewVisibility));
    partial void OnWebhookValidationErrorChanged(string value) => OnPropertyChanged(nameof(WebhookValidationErrorVisibility));
    partial void OnWebhookJsonTemplateChanged(string value) => InvalidateWebhookPreview();
    partial void OnWebhookHeadersChanged(string value) => InvalidateWebhookPreview();

    partial void OnDeliveryModeChanged(string value)
    {
        OnPropertyChanged(nameof(BarkVisibility));
        OnPropertyChanged(nameof(WxPusherVisibility));
        OnPropertyChanged(nameof(JsonWebhookVisibility));
        OnPropertyChanged(nameof(FeishuVisibility));
        OnPropertyChanged(nameof(TelegramVisibility));
        OnPropertyChanged(nameof(DiscordVisibility));
        UpdateHttpApprovalUi();
        if (_initialized) RefreshHttpApprovalToggle();
    }

    partial void OnAllowUnencryptedHttpChanged(bool value)
    {
        if (_isRefreshingHttpApprovalToggle || !_initialized) return;

        var mode = NormalizeDeliveryMode(DeliveryMode);
        var endpoint = GetCurrentEndpoint();
        if (value && !EndpointTransportPolicy.RequiresHttpApproval(endpoint))
        {
            RefreshHttpApprovalToggle();
            return;
        }

        _settings.SetHttpEndpointApproval(mode, endpoint, value);
        // Update the target synchronously so queued deliveries use the new policy on future attempts.
        // Requests that have already been sent cannot be recalled.
        RefreshDestinationAfterPolicyChange();
        _ = PersistHttpApprovalChangeAsync(value);
    }

    partial void OnWebhookUrlChanged(string value) => HandleEndpointChanged(RelayDeliveryTarget.JsonWebhookMode);
    partial void OnBarkServerUrlChanged(string value) => HandleEndpointChanged(RelayDeliveryTarget.BarkMode);
    partial void OnFeishuWebhookUrlChanged(string value) => HandleEndpointChanged(RelayDeliveryTarget.FeishuMode);
    partial void OnTelegramApiUrlChanged(string value) => HandleEndpointChanged(RelayDeliveryTarget.TelegramMode);
    partial void OnDiscordWebhookUrlChanged(string value) => HandleEndpointChanged(RelayDeliveryTarget.DiscordMode);

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
        foreach (var option in WebhookSecrets) UpdateWebhookSecretLabels(option);
        if (!string.IsNullOrEmpty(_webhookValidationErrorSource))
            WebhookValidationError = LocalizeWebhookValidationError(_webhookValidationErrorSource);
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
        WebhookJsonTemplate = _settings.WebhookJsonTemplate;
        WebhookHeaders = _settings.WebhookHeaders;
        WebhookSecrets.Clear();
        foreach (var secret in _secretStore.GetWebhookSecrets())
            WebhookSecrets.Add(CreateWebhookSecretOption(secret.Key, secret.Value));
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
        _savedTarget = CreateTarget();
        _relayService.Configure(GetSavedTarget(), AllowedApplications, _settings.ApplicationFilterEnabled);
        IsDestinationConfigured = WebhookClient.IsValidConfiguration(GetSavedTarget());

        _initialized = true;
        RefreshHttpApprovalToggle();
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
                var target = GetSavedTarget();
                if (!WebhookClient.IsValidConfiguration(target))
                {
                    if (_relayService.IsRunning) await _relayService.StopAsync();
                    IsRelayRunning = false;
                    IsDestinationConfigured = false;
                    _settings.RelayEnabled = false;
                    var configurationError = WebhookClient.GetConfigurationError(target);
                    SetStatus(configurationError == EndpointTransportPolicy.HttpApprovalRequired
                        ? configurationError
                        : IsChinese ? "请先完成通知通道配置，保存后将自动开始监听" : "Complete the destination configuration; listening starts automatically after saving");
                    return;
                }

                IsDestinationConfigured = true;
                if (RelayManuallyStopped)
                {
                    IsRelayRunning = false;
                    SetStatus(IsChinese ? "转发已停止" : "Relay stopped");
                    return;
                }

                _relayService.Configure(target, AllowedApplications, _settings.ApplicationFilterEnabled);
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
        if (!TryValidateWebhookSettings()) return;
        try
        {
            if (!await SaveConfigurationAsync()) return;
            await StartRelayAutomaticallyAsync();
            if (WebhookClient.GetConfigurationError(GetSavedTarget()) == EndpointTransportPolicy.HttpApprovalRequired) return;
            SetStatus(IsChinese ? "设置已保存" : "Settings saved");
        }
        catch (Exception)
        {
            if (!IsJsonWebhookMode) throw;
            SetWebhookValidationError("Webhook save failed");
        }
    }

    [RelayCommand]
    private async Task TestWebhookAsync()
    {
        if (!TryValidateWebhookSettings()) return;
        try
        {
            if (!await SaveConfigurationAsync()) return;
            _relayService.Configure(GetSavedTarget(), AllowedApplications, _settings.ApplicationFilterEnabled);
            IsDestinationConfigured = WebhookClient.IsValidConfiguration(GetSavedTarget());
            var result = await _relayService.SendTestAsync();
            var resultDetail = LocalizeStatus(result.Detail);
            SetStatus(result.Succeeded ? (IsChinese ? "测试发送成功" : "Test delivered") : result.Detail);
            AddActivity(new ActivityEntry(
                DateTimeOffset.Now,
                "WinToastRelay",
                IsChinese ? "测试发送" : "Test delivery",
                result.Succeeded,
                resultDetail) { Body = result.Succeeded
                    ? (IsChinese ? "你的通知通道连接正常。" : "Your notification destination is working.")
                    : (IsChinese ? "测试请求未能送达。" : "The test request was not delivered.") });
        }
        catch (Exception)
        {
            if (!IsJsonWebhookMode) throw;
            SetWebhookValidationError("Webhook test failed");
        }
    }

    [RelayCommand]
    private void AddWebhookSecret()
    {
        if (!CanAddWebhookSecret) return;
        WebhookSecrets.Add(CreateWebhookSecretOption());
    }

    public void RemoveWebhookSecret(WebhookSecretOption? option)
    {
        if (option is null || !WebhookSecrets.Remove(option)) return;
        InvalidateWebhookPreview();
        OnPropertyChanged(nameof(CanAddWebhookSecret));
    }

    [RelayCommand]
    private void PreviewWebhook()
    {
        WebhookPreviewText = string.Empty;
        if (!TryValidateWebhookSettings()) return;

        try
        {
            var sample = new WebhookPayload(
                "sample.event",
                "sample-delivery-id",
                new RelayNotification(123, "Example app", "Sample title", "This is a sample notification preview.", new DateTimeOffset(2025, 1, 2, 3, 4, 0, TimeSpan.Zero))
                { PackageName = "example.package" });
            if (!GenericWebhookTemplate.TryBuild(CreateTarget(), sample, redactSecrets: true, out var json, out var headers, out var error))
            {
                SetWebhookValidationError(error);
                return;
            }

            var headerPreview = headers.Count == 0
                ? (IsChinese ? "（无）" : "(none)")
                : string.Join(Environment.NewLine, headers.Keys.Select(name => $"{name}: ••••••••"));
            WebhookPreviewText = $"JSON{Environment.NewLine}{json}{Environment.NewLine}{Environment.NewLine}{(IsChinese ? "请求头（值已隐藏）" : "Headers (values hidden)")}{Environment.NewLine}{headerPreview}";
        }
        catch (Exception)
        {
            SetWebhookValidationError("Webhook preview failed");
        }
    }

    private bool TryValidateWebhookSettings()
    {
        ClearWebhookValidationError();
        if (!string.Equals(NormalizeDeliveryMode(DeliveryMode), RelayDeliveryTarget.JsonWebhookMode, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!TryValidateWebhookSecretRows()) return false;

        string error;
        try { error = GenericWebhookTemplate.GetConfigurationError(CreateTarget()); }
        catch (Exception) { error = "Invalid webhook JSON template"; }
        if (IsWebhookTemplateError(error))
        {
            SetWebhookValidationError(error);
            return false;
        }
        return true;
    }

    private bool TryValidateWebhookSecretRows(bool showError = true)
    {
        string? error = null;
        if (WebhookSecrets.Count > WebhookSecretLimit)
            error = "Too many webhook secrets";
        else
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var option in WebhookSecrets)
            {
                var name = option.Name.Trim();
                if (!GenericWebhookTemplate.IsValidSecretName(name))
                {
                    error = "Invalid webhook secret name";
                    break;
                }
                if (!names.Add(name))
                {
                    error = "Duplicate webhook secret name";
                    break;
                }
                if (string.IsNullOrEmpty(option.Value))
                {
                    error = "Missing webhook secret value";
                    break;
                }
            }
        }

        if (error is null) return true;
        if (showError) SetWebhookValidationError(error);
        return false;
    }

    private static bool IsWebhookTemplateError(string error) => error is
        "Invalid webhook JSON template" or
        "Invalid webhook headers" or
        "Missing webhook secret" or
        "Unknown webhook template variable" or
        "Conflicting webhook authorization" or
        "Webhook payload too large";

    private string LocalizeWebhookValidationError(string error) => error switch
    {
        "Invalid webhook JSON template" => IsChinese ? "JSON 模板无效。请使用 JSON 对象，并检查其中的变量。" : "The JSON template is invalid. Use a JSON object and check its variables.",
        "Invalid webhook headers" => IsChinese ? "请求头格式无效。请按每行 Name: Value 填写。" : "The headers are invalid. Enter one Name: Value pair per line.",
        "Missing webhook secret" => IsChinese ? "模板引用的密钥不存在或没有值。" : "A referenced secret is missing or has no value.",
        "Unknown webhook template variable" => IsChinese ? "模板包含未知变量。请检查可用变量列表。" : "The template contains an unknown variable. Check the available variables.",
        "Conflicting webhook authorization" => IsChinese ? "Authorization 请求头不能与 Bearer Token 同时使用。" : "A custom Authorization header cannot be combined with the Bearer token.",
        "Webhook payload too large" => IsChinese ? "生成的 Webhook 正文超过大小限制。" : "The generated webhook body exceeds the size limit.",
        "Invalid webhook secret name" => IsChinese ? "密钥名称无效。名称须以英文字母开头，并且最多 64 个字符。" : "A secret name is invalid. Names must start with an ASCII letter and be at most 64 characters.",
        "Duplicate webhook secret name" => IsChinese ? "密钥名称不能重复（不区分大小写）。" : "Secret names must be unique, ignoring case.",
        "Missing webhook secret value" => IsChinese ? "每个密钥变量都需要填写值，或移除空白行。" : "Every secret variable needs a value, or remove the empty row.",
        "Too many webhook secrets" => IsChinese ? "密钥变量最多只能有 32 项。" : "You can configure at most 32 secret variables.",
        "Webhook save failed" => IsChinese ? "设置保存失败。请检查 Windows 凭据管理器访问权限后重试。" : "Settings could not be saved. Check access to Windows Credential Manager and try again.",
        "Webhook test failed" => IsChinese ? "无法保存或发送测试请求。请检查配置和凭据管理器访问权限。" : "The settings could not be saved or the test request could not be sent. Check the configuration and Credential Manager access.",
        "Webhook preview failed" => IsChinese ? "无法生成预览。请检查模板和请求头格式。" : "The preview could not be generated. Check the template and header format.",
        _ => IsChinese ? "Webhook 配置无效。请检查模板、请求头和密钥变量。" : "The webhook configuration is invalid. Check the template, headers, and secret variables."
    };

    private void SetWebhookValidationError(string error)
    {
        _webhookValidationErrorSource = error;
        WebhookValidationError = LocalizeWebhookValidationError(error);
        SetStatus("Webhook validation error");
    }

    private void ClearWebhookValidationError()
    {
        _webhookValidationErrorSource = string.Empty;
        WebhookValidationError = string.Empty;
        if (string.Equals(_statusSource, "Webhook validation error", StringComparison.Ordinal))
            SetStatus(IsRelayRunning ? "Listening for Windows notifications" : "Not listening yet");
    }

    private void InvalidateWebhookPreview()
    {
        WebhookPreviewText = string.Empty;
        ClearWebhookValidationError();
    }

    [RelayCommand]
    private async Task ToggleLanguageAsync()
    {
        IsChinese = !IsChinese;
        _settings.Language = IsChinese ? "zh-CN" : "en-US";
        try { await _settingsStore.SaveAsync(_settings); }
        catch (Exception)
        {
            SetStatus("Language preference save failed");
        }
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

    private async Task<bool> SaveConfigurationAsync()
    {
        if (IsJsonWebhookMode && !TryValidateWebhookSettings()) return false;
        var canSaveWebhookSecrets = TryValidateWebhookSecretRows(showError: IsJsonWebhookMode);
        // Capture the validated draft before any asynchronous writes. Later
        // keystrokes must not silently become the live transport configuration.
        var target = CreateTarget();

        // Finish credential writes before mutating the ordinary settings cache,
        // so a failed vault write cannot be persisted by an incidental filter save.
        if (canSaveWebhookSecrets)
            _secretStore.SaveWebhookSecrets(target.WebhookSecrets!);
        _secretStore.Save(target.BearerToken);
        _secretStore.SaveBarkDeviceKey(target.BarkDeviceKey);
        _secretStore.SaveWxPusherAppToken(target.WxPusherAppToken);
        _secretStore.SaveFeishuSecret(target.FeishuSecret);
        _secretStore.SaveTelegramBotToken(target.TelegramBotToken);

        _settings.WebhookUrl = WebhookUrl.Trim();
        _settings.WebhookJsonTemplate = WebhookJsonTemplate;
        _settings.WebhookHeaders = WebhookHeaders;
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
        await _settingsStore.SaveAsync(_settings);
        _savedTarget = target;
        _relayService.Configure(GetSavedTarget(), AllowedApplications, _settings.ApplicationFilterEnabled);
        return true;
    }

    private RelayDeliveryTarget GetSavedTarget()
    {
        var target = _savedTarget ?? CreateTarget();
        // HTTP revocation is an immediate security operation, independent of
        // whether unrelated template/header/secret drafts are valid or saved.
        return target with
        {
            ApprovedHttpEndpoint = _settings.GetHttpEndpointApproval(
                target.Mode, EndpointTransportPolicy.GetConfiguredEndpoint(target))
        };
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
        DiscordBodyTemplate: DiscordBodyTemplate,
        ApprovedHttpEndpoint: _settings.GetHttpEndpointApproval(DeliveryMode, GetCurrentEndpoint()),
        WebhookJsonTemplate: WebhookJsonTemplate,
        WebhookHeaders: WebhookHeaders,
        WebhookSecrets: CreateWebhookSecrets());

    private Dictionary<string, string> CreateWebhookSecrets()
    {
        var secrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in WebhookSecrets)
        {
            var name = option.Name.Trim();
            if (name.Length > 0 && !secrets.ContainsKey(name)) secrets.Add(name, option.Value);
        }
        return secrets;
    }

    private WebhookSecretOption CreateWebhookSecretOption(string name = "", string value = "")
    {
        var option = new WebhookSecretOption { Name = name, Value = value };
        UpdateWebhookSecretLabels(option);
        option.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(WebhookSecretOption.Name) or nameof(WebhookSecretOption.Value))
                InvalidateWebhookPreview();
        };
        return option;
    }

    private void UpdateWebhookSecretLabels(WebhookSecretOption option)
    {
        option.NamePlaceholder = WebhookSecretNamePlaceholder;
        option.ValuePlaceholder = WebhookSecretValuePlaceholder;
        option.RemoveLabel = RemoveWebhookSecretLabel;
    }

    private string GetCurrentEndpoint() => NormalizeDeliveryMode(DeliveryMode) switch
    {
        RelayDeliveryTarget.JsonWebhookMode => WebhookUrl,
        RelayDeliveryTarget.WxPusherMode => "https://wxpusher.zjiecode.com/api/send/message",
        RelayDeliveryTarget.FeishuMode => FeishuWebhookUrl,
        RelayDeliveryTarget.TelegramMode => TelegramApiUrl,
        RelayDeliveryTarget.DiscordMode => DiscordWebhookUrl,
        _ => BarkServerUrl
    };

    private bool IsJsonWebhookMode => string.Equals(NormalizeDeliveryMode(DeliveryMode), RelayDeliveryTarget.JsonWebhookMode, StringComparison.OrdinalIgnoreCase);

    private string GetCurrentChannelLabel() => NormalizeDeliveryMode(DeliveryMode) switch
    {
        RelayDeliveryTarget.JsonWebhookMode => JsonWebhookModeLabel,
        RelayDeliveryTarget.WxPusherMode => WxPusherModeLabel,
        RelayDeliveryTarget.FeishuMode => FeishuModeLabel,
        RelayDeliveryTarget.TelegramMode => TelegramModeLabel,
        RelayDeliveryTarget.DiscordMode => DiscordModeLabel,
        _ => BarkModeLabel
    };

    private void UpdateHttpApprovalUi()
    {
        OnPropertyChanged(nameof(IsHttpApprovalToggleEnabled));
        OnPropertyChanged(nameof(HttpApprovalDescription));
        OnPropertyChanged(nameof(HttpApprovalTransportHint));
    }

    private void RefreshHttpApprovalToggle()
    {
        _isRefreshingHttpApprovalToggle = true;
        try
        {
            AllowUnencryptedHttp = !string.IsNullOrEmpty(_settings.GetHttpEndpointApproval(DeliveryMode, GetCurrentEndpoint()));
        }
        finally
        {
            _isRefreshingHttpApprovalToggle = false;
        }
        UpdateHttpApprovalUi();
    }

    private void HandleEndpointChanged(string mode)
    {
        UpdateHttpApprovalUi();
        if (!_initialized) return;

        var hadApproval = _settings.HttpEndpointApprovals?.ContainsKey(mode) == true;
        _settings.RevokeHttpEndpointApproval(mode);
        if (string.Equals(NormalizeDeliveryMode(DeliveryMode), mode, StringComparison.OrdinalIgnoreCase))
            RefreshHttpApprovalToggle();

        if (!hadApproval) return;

        RefreshDestinationAfterPolicyChange();
        _ = PersistRevokedHttpApprovalAsync();
    }

    private void RefreshDestinationAfterPolicyChange()
    {
        var target = GetSavedTarget();
        _relayService.Configure(target, AllowedApplications, _settings.ApplicationFilterEnabled);
        IsDestinationConfigured = WebhookClient.IsValidConfiguration(target);
        if (WebhookClient.GetConfigurationError(target) == EndpointTransportPolicy.HttpApprovalRequired)
            SetStatus(EndpointTransportPolicy.HttpApprovalRequired);
    }

    private async Task PersistHttpApprovalChangeAsync(bool isAllowed)
    {
        try
        {
            if (!await SaveConfigurationAsync())
            {
                // Revocation must stop an unapproved live target even when an
                // unrelated unfinished editor prevents saving the full draft.
                await RecheckRelayAfterHttpPolicyChangeAsync();
                return;
            }
        }
        catch (Exception)
        {
            SetStatus(IsChinese ? "保存 HTTP 授权失败，请检查凭据管理器和应用数据访问权限。" : "Failed to save HTTP approval. Check access to Credential Manager and app data.");
            if (isAllowed) return;
        }
        await RecheckRelayAfterHttpPolicyChangeAsync();
    }

    private async Task PersistRevokedHttpApprovalAsync()
    {
        try
        {
            await _settingsStore.SaveAsync(_settings);
        }
        catch (Exception ex)
        {
            SetStatus(IsChinese ? $"保存 HTTP 授权撤销失败：{ex.Message}" : $"Failed to save HTTP approval revocation: {ex.Message}");
        }
        await RecheckRelayAfterHttpPolicyChangeAsync();
    }

    private async Task RecheckRelayAfterHttpPolicyChangeAsync()
    {
        try { await StartRelayAutomaticallyAsync(); }
        catch (Exception ex)
        {
            SetStatus(IsChinese ? $"更新 HTTP 转发状态失败：{ex.Message}" : $"Failed to update HTTP relay state: {ex.Message}");
        }
    }

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
        if (IsJsonWebhookMode && !entry.Succeeded)
        {
            entry = entry with
            {
                Detail = IsChinese ? "通用 Webhook 传递失败。请检查目标服务状态。" : "Generic webhook delivery failed. Check the destination service status."
            };
        }

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
        _settings.AllowedApplications = AllowedApplications;
        _relayService.Configure(GetSavedTarget(), AllowedApplications, _settings.ApplicationFilterEnabled);
        _ = SaveApplicationFiltersAsync();
    }

    private async Task SaveApplicationFiltersAsync()
    {
        try { await _settingsStore.SaveAsync(_settings); }
        catch (Exception)
        {
            SetStatus(IsChinese ? "应用筛选设置保存失败，请重试。" : "Application filters could not be saved. Please try again.");
        }
    }

    private string LocalizeStatus(string status) => status switch
    {
        _ when IsWebhookTemplateError(status) => LocalizeWebhookValidationError(status),
        "Webhook request timed out" => IsChinese ? "Webhook 请求超时，将按重试策略处理。" : "The webhook request timed out and is eligible for retry.",
        "Webhook network request failed" => IsChinese ? "Webhook 网络请求失败，请检查网络和目标服务。" : "The webhook network request failed. Check the network and destination service.",
        EndpointTransportPolicy.HttpApprovalRequired => IsChinese ? "请为此地址启用“允许未加密 HTTP”" : "Enable Allow unencrypted HTTP for this destination",
        "请为此地址启用“允许未加密 HTTP”" or "Enable Allow unencrypted HTTP for this destination" =>
            IsChinese ? "请为此地址启用“允许未加密 HTTP”" : "Enable Allow unencrypted HTTP for this destination",
        "目标服务器重定向已阻止，请将地址改为最终目的地。" or "The server redirect was blocked. Set the URL to the final destination." =>
            IsChinese ? "目标服务器重定向已阻止，请将地址改为最终目的地。" : "The server redirect was blocked. Set the URL to the final destination.",
        _ when status.StartsWith("HTTP ", StringComparison.Ordinal) && status.EndsWith(": Redirect blocked; configure the final destination URL", StringComparison.Ordinal) =>
            IsChinese ? "目标服务器重定向已阻止，请将地址改为最终目的地。" : "The server redirect was blocked. Set the URL to the final destination.",
        "尚未启动监听" or "Not listening yet" => IsChinese ? "尚未启动监听" : "Not listening yet",
        "Listening for Windows notifications" or "正在监听 Windows 通知" => IsChinese ? "正在监听 Windows 通知" : "Listening for Windows notifications",
        "Relay paused" or "转发已暂停" => IsChinese ? "转发已暂停" : "Relay paused",
        "转发已停止" or "Relay stopped" => IsChinese ? "转发已停止" : "Relay stopped",
        "通知监听已自动启动" or "Notification listening started automatically" => IsChinese ? "通知监听已自动启动" : "Notification listening started automatically",
        "Webhook validation error" => string.IsNullOrEmpty(_webhookValidationErrorSource)
            ? (IsChinese ? "Webhook 配置无效。请检查通知通道设置。" : "Webhook configuration is invalid. Check the destination settings.")
            : LocalizeWebhookValidationError(_webhookValidationErrorSource),
        "Language preference save failed" => IsChinese ? "语言设置保存失败。" : "The language preference could not be saved.",
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
