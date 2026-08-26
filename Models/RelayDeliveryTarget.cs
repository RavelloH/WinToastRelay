namespace WinToastRelay.Models;

public sealed record RelayDeliveryTarget(
    string Mode,
    string WebhookUrl,
    string BearerToken,
    string BarkServerUrl,
    string BarkDeviceKey,
    string BarkTitleTemplate,
    string BarkBodyTemplate,
    string BarkParameters,
    string WxPusherApiUrl = "https://wxpusher.zjiecode.com/api/send/message",
    string WxPusherAppToken = "",
    string WxPusherUids = "",
    string WxPusherTopicIds = "",
    string WxPusherSummaryTemplate = "{app}: {title}",
    string WxPusherContentTemplate = "{title}\n{body}")
{
    public const string BarkMode = "bark";
    public const string JsonWebhookMode = "json";
    public const string WxPusherMode = "wxpusher";

    public bool IsBark => string.Equals(Mode, BarkMode, StringComparison.OrdinalIgnoreCase);
    public bool IsJsonWebhook => string.Equals(Mode, JsonWebhookMode, StringComparison.OrdinalIgnoreCase);
    public bool IsWxPusher => string.Equals(Mode, WxPusherMode, StringComparison.OrdinalIgnoreCase);
}
