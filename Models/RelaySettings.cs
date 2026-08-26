namespace WinToastRelay.Models;

public sealed class RelaySettings
{
    public string DeliveryMode { get; set; } = RelayDeliveryTarget.BarkMode;
    public string WebhookUrl { get; set; } = string.Empty;
    public string BarkServerUrl { get; set; } = "https://api.day.app";
    public string BarkDeviceKey { get; set; } = string.Empty;
    public string BarkTitleTemplate { get; set; } = "{app}: {title}";
    public string BarkBodyTemplate { get; set; } = "{body}";
    public string BarkParameters { get; set; } = "level=active\nicon=https://raw.ravelloh.com/icon/WinToastRelay.png";
    public string WxPusherUids { get; set; } = string.Empty;
    public string WxPusherTopicIds { get; set; } = string.Empty;
    public string WxPusherSummaryTemplate { get; set; } = "{app}: {title}";
    public string WxPusherContentTemplate { get; set; } = "{title}\n{body}";
    public string AllowedApplications { get; set; } = string.Empty;
    public bool ApplicationFilterEnabled { get; set; }
    public string Language { get; set; } = "zh-CN";
    public bool RelayEnabled { get; set; }
    public bool RelayManuallyStopped { get; set; }

    public bool StartWithWindows { get; set; }
}
