using System.Text.Json;
using WinToastRelay.Models;

namespace WinToastRelay.Tests;

public sealed class EndpointTransportPolicyTests
{
    [Theory]
    [InlineData("https://hooks.example.com/relay")]
    [InlineData("http://127.0.0.1:8080/relay")]
    [InlineData("http://[::1]:8080/relay")]
    [InlineData("http://localhost:8080/relay")]
    public void HttpsAndLoopbackEndpointsAreAllowedWithoutApproval(string endpoint)
    {
        Assert.True(EndpointTransportPolicy.TryParseEndpoint(endpoint, out _));
        Assert.False(EndpointTransportPolicy.RequiresHttpApproval(endpoint));
        Assert.True(EndpointTransportPolicy.IsEndpointAllowed(endpoint));
    }

    [Theory]
    [InlineData("http://10.0.0.8/relay")]
    [InlineData("http://172.16.12.3/relay")]
    [InlineData("http://192.168.1.20/relay")]
    [InlineData("http://8.8.8.8/relay")]
    [InlineData("http://printer.local/relay")]
    [InlineData("http://[fd12:3456:789a::1]/relay")]
    public void EveryNonLoopbackHttpEndpointRequiresExactApproval(string endpoint)
    {
        Assert.True(EndpointTransportPolicy.TryParseEndpoint(endpoint, out _));
        Assert.True(EndpointTransportPolicy.RequiresHttpApproval(endpoint));
        Assert.False(EndpointTransportPolicy.IsEndpointAllowed(endpoint));
        Assert.True(EndpointTransportPolicy.IsApproved(endpoint, endpoint));
        Assert.True(EndpointTransportPolicy.IsEndpointAllowed(endpoint, endpoint));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a URL")]
    [InlineData("ftp://hooks.example.com/relay")]
    [InlineData("file:///tmp/relay")]
    [InlineData("https://user:password@hooks.example.com/relay")]
    [InlineData("http://user@10.0.0.8/relay")]
    [InlineData("https://hooks.example.com/relay#fragment")]
    [InlineData("http://10.0.0.8/relay#fragment")]
    public void InvalidSchemesAndUnsafeUrlComponentsAreRejected(string endpoint)
    {
        Assert.False(EndpointTransportPolicy.TryParseEndpoint(endpoint, out _));
        Assert.False(EndpointTransportPolicy.IsEndpointAllowed(endpoint, endpoint));
    }

    [Fact]
    public void ApprovalMatchesCanonicalEndpointIncludingPortPathAndQuery()
    {
        const string approved = "http://10.0.0.8:8080/hooks/relay?key=one&mode=fast";

        Assert.True(EndpointTransportPolicy.IsApproved(
            "HTTP://10.0.0.8:8080/hooks/relay?key=one&mode=fast", approved));
        Assert.False(EndpointTransportPolicy.IsApproved(
            "http://10.0.0.8:8081/hooks/relay?key=one&mode=fast", approved));
        Assert.False(EndpointTransportPolicy.IsApproved(
            "http://10.0.0.9:8080/hooks/relay?key=one&mode=fast", approved));
        Assert.False(EndpointTransportPolicy.IsApproved(
            "http://10.0.0.8:8080/hooks/other?key=one&mode=fast", approved));
        Assert.False(EndpointTransportPolicy.IsApproved(
            "http://10.0.0.8:8080/hooks/relay?mode=fast&key=one", approved));
    }

    [Fact]
    public void SettingsApprovalsAreChannelScopedAndOnlyMatchTheConfiguredEndpoint()
    {
        var settings = new RelaySettings();
        const string endpoint = "http://192.168.1.20:8080/hooks?secret=abc";

        settings.SetHttpEndpointApproval(RelayDeliveryTarget.JsonWebhookMode, endpoint, allow: true);

        Assert.Equal(endpoint, settings.GetHttpEndpointApproval(RelayDeliveryTarget.JsonWebhookMode, endpoint));
        Assert.Empty(settings.GetHttpEndpointApproval(RelayDeliveryTarget.BarkMode, endpoint));
        Assert.Empty(settings.GetHttpEndpointApproval(RelayDeliveryTarget.JsonWebhookMode, "http://192.168.1.20:8080/other?secret=abc"));

        settings.SetHttpEndpointApproval(RelayDeliveryTarget.JsonWebhookMode, "http://192.168.1.20:8081/hooks?secret=abc", allow: true);
        Assert.Empty(settings.GetHttpEndpointApproval(RelayDeliveryTarget.JsonWebhookMode, endpoint));

        settings.RevokeHttpEndpointApproval(RelayDeliveryTarget.JsonWebhookMode);
        Assert.Empty(settings.GetHttpEndpointApproval(RelayDeliveryTarget.JsonWebhookMode,
            "http://192.168.1.20:8081/hooks?secret=abc"));
    }

    [Fact]
    public void OldSettingsJsonHasNoHttpGrantAndSourceGeneratedJsonPersistsGrants()
    {
        var oldSettings = JsonSerializer.Deserialize(
            "{\"deliveryMode\":\"json\",\"webhookUrl\":\"http://192.168.1.20/hook\"}",
            AppJsonContext.Default.RelaySettings);

        Assert.NotNull(oldSettings);
        Assert.Empty(oldSettings.HttpEndpointApprovals);
        Assert.Empty(oldSettings.GetHttpEndpointApproval(RelayDeliveryTarget.JsonWebhookMode, oldSettings.WebhookUrl));

        oldSettings.SetHttpEndpointApproval(RelayDeliveryTarget.JsonWebhookMode, oldSettings.WebhookUrl, allow: true);
        var json = JsonSerializer.Serialize(oldSettings, AppJsonContext.Default.RelaySettings);
        var roundTripped = JsonSerializer.Deserialize(json, AppJsonContext.Default.RelaySettings);

        Assert.NotNull(roundTripped);
        Assert.Equal(oldSettings.GetHttpEndpointApproval(RelayDeliveryTarget.JsonWebhookMode, oldSettings.WebhookUrl),
            roundTripped.GetHttpEndpointApproval(RelayDeliveryTarget.JsonWebhookMode, oldSettings.WebhookUrl));
    }

    [Fact]
    public void ChangingEndpointMakesStoredApprovalStale()
    {
        var settings = new RelaySettings();
        const string originalEndpoint = "http://10.0.0.8:8080/hooks?key=one";
        const string changedEndpoint = "http://10.0.0.8:8080/hooks?key=two";
        settings.SetHttpEndpointApproval(RelayDeliveryTarget.FeishuMode, originalEndpoint, allow: true);

        Assert.NotEmpty(settings.GetHttpEndpointApproval(RelayDeliveryTarget.FeishuMode, originalEndpoint));
        Assert.Empty(settings.GetHttpEndpointApproval(RelayDeliveryTarget.FeishuMode, changedEndpoint));
    }

    [Fact]
    public void RevokedApprovalDoesNotReturnWhenVisitingTheOriginalEndpointAgain()
    {
        var settings = new RelaySettings();
        const string endpoint = "http://10.0.0.8:8080/hooks?key=one";
        settings.SetHttpEndpointApproval(RelayDeliveryTarget.JsonWebhookMode, endpoint, allow: true);

        Assert.NotEmpty(settings.GetHttpEndpointApproval(RelayDeliveryTarget.JsonWebhookMode, endpoint));
        settings.RevokeHttpEndpointApproval(RelayDeliveryTarget.JsonWebhookMode);

        var approvalAfterRevoke = settings.GetHttpEndpointApproval(RelayDeliveryTarget.JsonWebhookMode, endpoint);
        Assert.Empty(approvalAfterRevoke);
        Assert.False(EndpointTransportPolicy.IsEndpointAllowed(endpoint, approvalAfterRevoke));
    }

    [Fact]
    public void NullApprovalDictionaryFromJsonIsTreatedAsNoGrantAndCanBeSafelyUpdated()
    {
        var settings = JsonSerializer.Deserialize(
            "{\"httpEndpointApprovals\":null}", AppJsonContext.Default.RelaySettings);
        const string endpoint = "http://192.168.1.30:8080/relay";

        Assert.NotNull(settings);
        Assert.Empty(settings.GetHttpEndpointApproval(RelayDeliveryTarget.JsonWebhookMode, endpoint));
        Assert.False(EndpointTransportPolicy.IsEndpointAllowed(endpoint,
            settings.GetHttpEndpointApproval(RelayDeliveryTarget.JsonWebhookMode, endpoint)));

        settings.SetHttpEndpointApproval(RelayDeliveryTarget.JsonWebhookMode, endpoint, allow: true);
        Assert.NotEmpty(settings.GetHttpEndpointApproval(RelayDeliveryTarget.JsonWebhookMode, endpoint));
        settings.RevokeHttpEndpointApproval(RelayDeliveryTarget.JsonWebhookMode);
        Assert.Empty(settings.GetHttpEndpointApproval(RelayDeliveryTarget.JsonWebhookMode, endpoint));
    }
}
