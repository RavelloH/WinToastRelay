namespace WinToastRelay.Models;

public sealed class RelaySettings
{
    public string DeliveryMode { get; set; } = RelayDeliveryTarget.BarkMode;
    // Consent is scoped to one canonical endpoint for each channel, not a global HTTP bypass.
    public Dictionary<string, string> HttpEndpointApprovals { get; set; } = new();
    public string WebhookUrl { get; set; } = string.Empty;
    public string WebhookJsonTemplate { get; set; } = string.Empty;
    public string WebhookHeaders { get; set; } = string.Empty;
    public string BarkServerUrl { get; set; } = "https://api.day.app";
    // Legacy JSON field retained only so older settings files can be migrated to
    // Windows Credential Manager during startup. New writes always keep it empty.
    public string BarkDeviceKey { get; set; } = string.Empty;
    public string BarkTitleTemplate { get; set; } = "{app}: {title}";
    public string BarkBodyTemplate { get; set; } = "{body}";
    public string BarkParameters { get; set; } = "level=active\nicon=https://raw.ravelloh.com/icon/WinToastRelay.png";
    public string WxPusherUids { get; set; } = string.Empty;
    public string WxPusherTopicIds { get; set; } = string.Empty;
    public string WxPusherSummaryTemplate { get; set; } = "{app}: {title}";
    public string WxPusherContentTemplate { get; set; } = "{title}\n{body}";
    public string FeishuWebhookUrl { get; set; } = string.Empty;
    public string FeishuTitleTemplate { get; set; } = "{app}: {title}";
    public string FeishuBodyTemplate { get; set; } = "{body}";
    public string TelegramApiUrl { get; set; } = "https://api.telegram.org";
    public string TelegramChatId { get; set; } = string.Empty;
    public string TelegramParseMode { get; set; } = string.Empty;
    public string TelegramTitleTemplate { get; set; } = "{app}: {title}";
    public string TelegramBodyTemplate { get; set; } = "{body}";
    public string DiscordWebhookUrl { get; set; } = string.Empty;
    public string DiscordUsername { get; set; } = "WinToastRelay";
    public string DiscordTitleTemplate { get; set; } = "{app}: {title}";
    public string DiscordBodyTemplate { get; set; } = "{body}";
    public string AllowedApplications { get; set; } = string.Empty;
    public bool ApplicationFilterEnabled { get; set; }
    public string Language { get; set; } = "zh-CN";
    public bool RelayEnabled { get; set; }
    public bool RelayManuallyStopped { get; set; }

    public bool StartWithWindows { get; set; }

    public string GetHttpEndpointApproval(string mode, string endpoint)
    {
        HttpEndpointApprovals ??= new();
        return HttpEndpointApprovals.TryGetValue(mode.ToLowerInvariant(), out var approved) &&
               EndpointTransportPolicy.IsApproved(endpoint, approved) ? approved : string.Empty;
    }

    public void SetHttpEndpointApproval(string mode, string endpoint, bool allow)
    {
        HttpEndpointApprovals ??= new();
        var key = mode.ToLowerInvariant();
        if (allow && EndpointTransportPolicy.RequiresHttpApproval(endpoint))
            HttpEndpointApprovals[key] = EndpointTransportPolicy.CanonicalizeEndpoint(endpoint);
        else
            HttpEndpointApprovals.Remove(key);
    }

    public void RevokeHttpEndpointApproval(string mode) => SetHttpEndpointApproval(mode, string.Empty, false);
}
