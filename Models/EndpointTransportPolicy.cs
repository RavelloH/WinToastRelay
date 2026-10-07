using System.Diagnostics.CodeAnalysis;

namespace WinToastRelay.Models;

/// <summary>Transport consent for an exact user-configured endpoint; no DNS probing is needed.</summary>
public static class EndpointTransportPolicy
{
    public const string HttpApprovalRequired = "HTTP approval required";

    public static bool TryParseEndpoint(string endpoint, [NotNullWhen(true)] out Uri? uri)
    {
        return Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out uri) &&
               (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
               !string.IsNullOrWhiteSpace(uri.Host) &&
               string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);
    }

    public static string CanonicalizeEndpoint(string endpoint) =>
        TryParseEndpoint(endpoint, out var uri) ? uri.AbsoluteUri : string.Empty;

    public static bool RequiresHttpApproval(string endpoint) =>
        TryParseEndpoint(endpoint, out var uri) && uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback;

    public static bool IsApproved(string endpoint, string approvedEndpoint) =>
        RequiresHttpApproval(endpoint) &&
        !string.IsNullOrEmpty(CanonicalizeEndpoint(approvedEndpoint)) &&
        string.Equals(CanonicalizeEndpoint(endpoint), CanonicalizeEndpoint(approvedEndpoint), StringComparison.Ordinal);

    public static bool IsEndpointAllowed(string endpoint, string approvedEndpoint = "") =>
        TryParseEndpoint(endpoint, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback || IsApproved(endpoint, approvedEndpoint));

    public static string GetConfiguredEndpoint(RelayDeliveryTarget target) =>
        target.IsBark ? target.BarkServerUrl :
        target.IsWxPusher ? target.WxPusherApiUrl :
        target.IsFeishu ? target.FeishuWebhookUrl :
        target.IsTelegram ? target.TelegramApiUrl :
        target.IsDiscord ? target.DiscordWebhookUrl : target.WebhookUrl;
}
