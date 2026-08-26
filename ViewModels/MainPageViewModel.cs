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
    [ObservableProperty] public partial string AllowedApplications { get; set; } = string.Empty;
    [ObservableProperty] public partial string StatusDetail { get; set; } = "尚未启动监听";
    [ObservableProperty] public partial string CurrentSection { get; set; } = "overview";
    [ObservableProperty] public partial bool IsRelayRunning { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsChinese { get; set; } = true;
    [ObservableProperty] public partial bool StartWithWindows { get; set; }
    [ObservableProperty] public partial bool IsDestinationConfigured { get; set; }
    [ObservableProperty] public partial bool RelayManuallyStopped { get; set; }

    public ObservableCollection<ActivityEntry> Activity { get; } = new();
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
    public string SetupCardDescription => IsChinese ? "默认使用 Bark，也支持 WxPusher 标准推送和通用 JSON Webhook。" : "Bark is the default; WxPusher standard push and generic JSON webhooks are also supported.";
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
    public string BarkServerUrlLabel => IsChinese ? "Bark 服务地址" : "Bark server URL";
    public string BarkServerUrlDescription => IsChinese ? "官方服务为 https://api.day.app，也支持自托管 Bark。" : "Use https://api.day.app or your self-hosted Bark server.";
    public string BarkDeviceKeyLabel => IsChinese ? "设备密钥" : "Device key";
    public string BarkTemplateTitleLabel => IsChinese ? "标题模板" : "Title template";
    public string BarkTemplateBodyLabel => IsChinese ? "正文模板" : "Body template";
    public string BarkTemplateDescription => IsChinese ? "可用变量：{app}、{title}、{body}、{id}、{eventType}、{createdAt}" : "Variables: {app}, {title}, {body}, {id}, {eventType}, {createdAt}";
    public string BarkParametersLabel => IsChinese ? "附加 Bark 参数" : "Additional Bark parameters";
    public string BarkParametersDescription => IsChinese ? "每行 key=value，例如 sound=bell、group=work、level=timeSensitive、url=https://example.com；这些参数会作为 JSON 字段发送。" : "One key=value per line, e.g. sound=bell, group=work, level=timeSensitive, url=https://example.com. They are sent as JSON fields.";
    public string BarkRouteDescription => IsChinese ? "请求使用 Bark /push JSON POST。设备密钥、标题、正文和附加参数都放在请求体中，不受 URL 长度限制；正文过长时会自动截断并标记。" : "Requests use Bark's /push JSON POST. The device key, title, body, and additional parameters stay in the request body, avoiding URL length limits; oversized text is truncated and marked automatically.";
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
    public string AboutDescription => IsChinese ? "使用 Windows 原生通知事件，将通知实时转发到 Bark、WxPusher 或 JSON Webhook。" : "Uses native Windows notification events to relay notifications to Bark, WxPusher, or a JSON webhook.";
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

    partial void OnIsChineseChanged(bool value) => OnPropertyChanged(string.Empty);

    public async Task InitializeAsync()
    {
        _settings = await _settingsStore.LoadAsync();
        DeliveryMode = NormalizeDeliveryMode(_settings.DeliveryMode);
        WebhookUrl = _settings.WebhookUrl;
        BarkServerUrl = _settings.BarkServerUrl;
        BarkDeviceKey = _settings.BarkDeviceKey;
        BarkTitleTemplate = _settings.BarkTitleTemplate;
        BarkBodyTemplate = _settings.BarkBodyTemplate;
        BarkParameters = _settings.BarkParameters;
        WxPusherUids = _settings.WxPusherUids;
        WxPusherTopicIds = _settings.WxPusherTopicIds;
        WxPusherSummaryTemplate = _settings.WxPusherSummaryTemplate;
        WxPusherContentTemplate = _settings.WxPusherContentTemplate;
        if (!BarkParameters.Contains("icon=", StringComparison.OrdinalIgnoreCase))
            BarkParameters = string.IsNullOrWhiteSpace(BarkParameters)
                ? "level=active\nicon=https://raw.ravelloh.com/icon/WinToastRelay.png"
                : BarkParameters.TrimEnd() + "\nicon=https://raw.ravelloh.com/icon/WinToastRelay.png";
        _settings.BarkParameters = BarkParameters;
        AllowedApplications = _settings.AllowedApplications;
        BearerToken = _secretStore.Get();
        WxPusherAppToken = _secretStore.GetWxPusherAppToken();
        IsChinese = !string.Equals(_settings.Language, "en-US", StringComparison.OrdinalIgnoreCase);
        RelayManuallyStopped = _settings.RelayManuallyStopped;
        StartWithWindows = await _startupTaskService.IsEnabledAsync();
        _settings.ApplicationFilterEnabled |= ParseAllowedApplications().Count > 0;
        _relayService.Configure(CreateTarget(), AllowedApplications, _settings.ApplicationFilterEnabled);
        IsDestinationConfigured = WebhookClient.IsValidConfiguration(CreateTarget());
        await LoadActivityAsync();
        await LoadApplicationsAsync();
        await StartRelayAutomaticallyAsync();
    }

    private async Task StartRelayAutomaticallyAsync()
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
        finally { IsBusy = false; }
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
        _settings.BarkDeviceKey = BarkDeviceKey.Trim();
        _settings.BarkTitleTemplate = BarkTitleTemplate;
        _settings.BarkBodyTemplate = BarkBodyTemplate;
        _settings.BarkParameters = BarkParameters;
        _settings.WxPusherUids = WxPusherUids;
        _settings.WxPusherTopicIds = WxPusherTopicIds;
        _settings.WxPusherSummaryTemplate = WxPusherSummaryTemplate;
        _settings.WxPusherContentTemplate = WxPusherContentTemplate;
        _settings.AllowedApplications = AllowedApplications;
        _settings.Language = IsChinese ? "zh-CN" : "en-US";
        _settings.RelayEnabled = IsRelayRunning;
        _settings.StartWithWindows = StartWithWindows;
        _secretStore.Save(BearerToken.Trim());
        _secretStore.SaveWxPusherAppToken(WxPusherAppToken.Trim());
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
        WxPusherContentTemplate: WxPusherContentTemplate);

    private static string NormalizeDeliveryMode(string deliveryMode)
    {
        if (string.Equals(deliveryMode, RelayDeliveryTarget.JsonWebhookMode, StringComparison.OrdinalIgnoreCase))
            return RelayDeliveryTarget.JsonWebhookMode;
        if (string.Equals(deliveryMode, RelayDeliveryTarget.WxPusherMode, StringComparison.OrdinalIgnoreCase))
            return RelayDeliveryTarget.WxPusherMode;
        return RelayDeliveryTarget.BarkMode;
    }

    private void SetStatus(string status)
    {
        var localizedStatus = LocalizeStatus(status);
        if (App.DispatcherQueue is null) { StatusDetail = localizedStatus; return; }
        App.DispatcherQueue.TryEnqueue(() => StatusDetail = localizedStatus);
    }

    private void AddActivity(ActivityEntry entry)
    {
        void Add()
        {
            Activity.Insert(0, entry);
            while (Activity.Count > 500) Activity.RemoveAt(Activity.Count - 1);
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
    private async Task RefreshApplicationsAsync() => await LoadApplicationsAsync();

    private async Task LoadApplicationsAsync()
    {
        var enabled = ParseAllowedApplications();
        foreach (var app in await _relayService.GetAvailableApplicationsAsync())
            AddApplicationCore(app.Name, !_settings.ApplicationFilterEnabled || enabled.Contains(app.Name), app.Icon);
    }

    private void AddApplication(string application)
    {
        void Add() => AddApplicationCore(application, !_settings.ApplicationFilterEnabled || ParseAllowedApplications().Contains(application));
        if (App.DispatcherQueue is null) Add();
        else App.DispatcherQueue.TryEnqueue(Add);
    }

    private void AddApplicationCore(string application, bool isEnabled, Microsoft.UI.Xaml.Media.ImageSource? icon = null)
    {
        if (Applications.Any(item => string.Equals(item.Name, application, StringComparison.OrdinalIgnoreCase))) return;
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
        "Listening for Windows notifications" => IsChinese ? "正在监听 Windows 通知" : status,
        "Relay paused" => IsChinese ? "转发已暂停" : status,
        _ => status
    };

    private async Task LoadActivityAsync()
    {
        try
        {
            var file = await Windows.Storage.ApplicationData.Current.LocalFolder.TryGetItemAsync(ActivityFileName) as Windows.Storage.StorageFile;
            if (file is null) return;
            var loaded = JsonSerializer.Deserialize(await Windows.Storage.FileIO.ReadTextAsync(file), AppJsonContext.Default.ListActivityEntry) ?? [];
            var cutoff = DateTimeOffset.Now.AddDays(-14);
            foreach (var item in loaded.Where(x => x.Time >= cutoff).OrderByDescending(x => x.Time)) Activity.Add(item);
            OnPropertyChanged(nameof(ActivityEmptyVisibility));
            OnPropertyChanged(nameof(ActivityListVisibility));
            OnPropertyChanged(nameof(StatsDeliveriesValue));
            OnPropertyChanged(nameof(StatsSuccessValue));
            OnPropertyChanged(nameof(StatsApplicationsValue));
        }
        catch (JsonException) { }
    }

    private async Task SaveActivityAsync()
    {
        try
        {
            var snapshot = Activity.Where(x => x.Time >= DateTimeOffset.Now.AddDays(-14)).ToList();
            var file = await Windows.Storage.ApplicationData.Current.LocalFolder.CreateFileAsync(ActivityFileName, Windows.Storage.CreationCollisionOption.ReplaceExisting);
            await Windows.Storage.FileIO.WriteTextAsync(file, JsonSerializer.Serialize(snapshot, AppJsonContext.Default.ListActivityEntry));
        }
        catch { }
    }
}
