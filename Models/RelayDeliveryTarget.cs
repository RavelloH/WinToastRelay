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
    string WxPusherContentTemplate = "{title}\n{body}",
    string FeishuWebhookUrl = "",
    string FeishuSecret = "",
    string FeishuTitleTemplate = "{app}: {title}",
    string FeishuBodyTemplate = "{body}",
    string TelegramApiUrl = "https://api.telegram.org",
    string TelegramBotToken = "",
    string TelegramChatId = "",
    string TelegramParseMode = "",
    string TelegramTitleTemplate = "{app}: {title}",
    string TelegramBodyTemplate = "{body}",
    string DiscordWebhookUrl = "",
    string DiscordUsername = "WinToastRelay",
    string DiscordTitleTemplate = "{app}: {title}",
    string DiscordBodyTemplate = "{body}",
    string ApprovedHttpEndpoint = "",
    string WebhookJsonTemplate = "",
    string WebhookHeaders = "",
    IReadOnlyDictionary<string, string>? WebhookSecrets = null)
{
    public const string BarkMode = "bark";
    public const string JsonWebhookMode = "json";
    public const string WxPusherMode = "wxpusher";
    public const string FeishuMode = "feishu";
    public const string TelegramMode = "telegram";
    public const string DiscordMode = "discord";

    public bool IsBark => string.Equals(Mode, BarkMode, StringComparison.OrdinalIgnoreCase);
    public bool IsJsonWebhook => string.Equals(Mode, JsonWebhookMode, StringComparison.OrdinalIgnoreCase);
    public bool IsWxPusher => string.Equals(Mode, WxPusherMode, StringComparison.OrdinalIgnoreCase);
    public bool IsFeishu => string.Equals(Mode, FeishuMode, StringComparison.OrdinalIgnoreCase);
    public bool IsTelegram => string.Equals(Mode, TelegramMode, StringComparison.OrdinalIgnoreCase);
    public bool IsDiscord => string.Equals(Mode, DiscordMode, StringComparison.OrdinalIgnoreCase);

    public bool HasSameConfiguration(RelayDeliveryTarget other)
    {
        if ((this with { WebhookSecrets = null }) != (other with { WebhookSecrets = null })) return false;
        if ((WebhookSecrets?.Count ?? 0) != (other.WebhookSecrets?.Count ?? 0)) return false;
        return WebhookSecrets is null || WebhookSecrets.All(secret =>
            other.WebhookSecrets is not null && other.WebhookSecrets.Any(candidate =>
                string.Equals(secret.Key, candidate.Key, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(secret.Value, candidate.Value, StringComparison.Ordinal)));
    }
}
