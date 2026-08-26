using System.Net;
using System.Text;
using System.Text.Json;
using WinToastRelay.Models;
using WinToastRelay.Services;

namespace WinToastRelay.Tests;

public sealed class WebhookClientTests
{
    [Theory]
    [InlineData("https://hooks.example.com/relay", true)]
    [InlineData("http://127.0.0.1:8080/relay", true)]
    [InlineData("http://localhost:8080/relay", true)]
    [InlineData("http://hooks.example.com/relay", false)]
    [InlineData("ftp://hooks.example.com/relay", false)]
    [InlineData("not a URL", false)]
    public void EndpointValidation_OnlyAllowsHttpsOrLoopbackHttp(string endpoint, bool expected) =>
        Assert.Equal(expected, WebhookClient.IsValidEndpoint(endpoint));

    [Fact]
    public async Task DeliverAsync_PostsJsonAndDeliveryHeader()
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:18765/");
        listener.Start();

        var requestTask = listener.GetContextAsync();
        var payload = new WebhookPayload(
            "relay.test",
            "delivery-123",
            new RelayNotification(7, "Calendar", "Reminder", "Meeting in 10 minutes", DateTimeOffset.Parse("2026-08-20T00:00:00Z")));

        var deliveryTask = new WebhookClient().DeliverAsync("http://127.0.0.1:18765/hooks", "token-abc", payload);
        var context = await requestTask;
        using var reader = new StreamReader(context.Request.InputStream);
        var body = await reader.ReadToEndAsync();
        context.Response.StatusCode = (int)HttpStatusCode.Accepted;
        context.Response.Close();

        var result = await deliveryTask;

        Assert.True(result.Succeeded);
        Assert.Equal("delivery-123", context.Request.Headers["X-WinToastRelay-Delivery"]);
        Assert.Equal("Bearer", context.Request.Headers["Authorization"]?.Split(' ')[0]);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("relay.test", json.RootElement.GetProperty("eventType").GetString());
        Assert.Equal("Calendar", json.RootElement.GetProperty("notification").GetProperty("app").GetString());
    }

    [Fact]
    public async Task DeliverAsync_BarkMode_PostsJsonToPushEndpointWithTemplatesAndCustomParameters()
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:18766/");
        listener.Start();

        var requestTask = listener.GetContextAsync();
        var payload = new WebhookPayload(
            "notification.added",
            "delivery-bark",
            new RelayNotification(9, "Mail", "Build passed", new string('长', 3000), DateTimeOffset.Parse("2026-08-20T00:00:00Z")));
        var target = new RelayDeliveryTarget(
            RelayDeliveryTarget.BarkMode,
            string.Empty,
            string.Empty,
            "http://127.0.0.1:18766",
            "device-key",
            "{app}: {title}",
            "{body}",
            "sound=bell\ngroup=builds\nbadge=3");

        var deliveryTask = new WebhookClient().DeliverAsync(target, payload);
        var context = await requestTask;
        var method = context.Request.HttpMethod;
        var path = context.Request.RawUrl?.Split('?')[0];
        var deliveryId = context.Request.Headers["X-WinToastRelay-Delivery"];
        using var reader = new StreamReader(context.Request.InputStream);
        var body = await reader.ReadToEndAsync();
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.Close();

        var result = await deliveryTask;

        Assert.True(result.Succeeded);
        Assert.Equal("POST", method);
        Assert.Equal("/push", path);
        Assert.Equal("delivery-bark", deliveryId);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("device-key", json.RootElement.GetProperty("device_key").GetString());
        Assert.Equal("Mail: Build passed", json.RootElement.GetProperty("title").GetString());
        var barkBody = json.RootElement.GetProperty("body").GetString();
        Assert.NotNull(barkBody);
        Assert.StartsWith(new string('长', 100), barkBody);
        Assert.EndsWith("[truncated by WinToastRelay]", barkBody);
        Assert.True(Encoding.UTF8.GetByteCount(body) <= 3500);
        Assert.Equal("bell", json.RootElement.GetProperty("sound").GetString());
        Assert.Equal("builds", json.RootElement.GetProperty("group").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("badge").GetInt32());
    }

    [Fact]
    public async Task DeliverAsync_WxPusherMode_PostsStandardPushPayloadAndRequiresBusinessSuccess()
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:18767/");
        listener.Start();

        var requestTask = listener.GetContextAsync();
        var payload = new WebhookPayload(
            "notification.added",
            "delivery-wxpusher",
            new RelayNotification(11, "Calendar", new string('T', 105), "Meeting in 10 minutes", DateTimeOffset.Parse("2026-08-20T00:00:00Z")));
        var target = new RelayDeliveryTarget(
            RelayDeliveryTarget.WxPusherMode,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            WxPusherApiUrl: "http://127.0.0.1:18767/api/send/message",
            WxPusherAppToken: "AT_test",
            WxPusherUids: "UID_alpha\nUID_beta,UID_alpha",
            WxPusherTopicIds: "123, 456;123",
            WxPusherSummaryTemplate: "{app}: {title}",
            WxPusherContentTemplate: "{title}\n{body}");

        var deliveryTask = new WebhookClient().DeliverAsync(target, payload);
        var context = await requestTask;
        var method = context.Request.HttpMethod;
        var path = context.Request.RawUrl;
        var deliveryId = context.Request.Headers["X-WinToastRelay-Delivery"];
        var authorization = context.Request.Headers["Authorization"];
        using var reader = new StreamReader(context.Request.InputStream);
        var body = await reader.ReadToEndAsync();
        var responseBytes = Encoding.UTF8.GetBytes("{\"code\":1000,\"msg\":\"处理成功\",\"data\":[{\"code\":1000},{\"code\":1000},{\"code\":1000},{\"code\":1000}]}");
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = responseBytes.Length;
        await context.Response.OutputStream.WriteAsync(responseBytes);
        context.Response.Close();

        var result = await deliveryTask;

        Assert.True(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Equal("WxPusher 1000 处理成功", result.Detail);
        Assert.Equal("POST", method);
        Assert.Equal("/api/send/message", path);
        Assert.Equal("delivery-wxpusher", deliveryId);
        Assert.Null(authorization);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("AT_test", json.RootElement.GetProperty("appToken").GetString());
        Assert.Equal(new string('T', 105) + "\nMeeting in 10 minutes", json.RootElement.GetProperty("content").GetString());
        Assert.Equal(100, json.RootElement.GetProperty("summary").GetString()!.EnumerateRunes().Count());
        Assert.Equal(1, json.RootElement.GetProperty("contentType").GetInt32());
        Assert.Equal(["UID_alpha", "UID_beta"], json.RootElement.GetProperty("uids").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal([123L, 456L], json.RootElement.GetProperty("topicIds").EnumerateArray().Select(item => item.GetInt64()).ToArray());
    }

    [Fact]
    public async Task DeliverAsync_WxPusherMode_RejectsFailedBusinessResponse()
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:18768/");
        listener.Start();

        var requestTask = listener.GetContextAsync();
        var payload = new WebhookPayload(
            "relay.test",
            "delivery-wxpusher-failed",
            new RelayNotification(0, "WinToastRelay", "Delivery test", "Test body", DateTimeOffset.Parse("2026-08-20T00:00:00Z")));
        var target = new RelayDeliveryTarget(
            RelayDeliveryTarget.WxPusherMode,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            WxPusherApiUrl: "http://127.0.0.1:18768/api/send/message",
            WxPusherAppToken: "AT_invalid",
            WxPusherUids: "UID_alpha");

        var deliveryTask = new WebhookClient().DeliverAsync(target, payload);
        var context = await requestTask;
        var responseBytes = Encoding.UTF8.GetBytes("{\"code\":1001,\"msg\":\"业务异常\",\"data\":null}");
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = responseBytes.Length;
        await context.Response.OutputStream.WriteAsync(responseBytes);
        context.Response.Close();

        var result = await deliveryTask;

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Equal("WxPusher 1001 业务异常", result.Detail);
    }

    [Fact]
    public async Task DeliverAsync_WxPusherMode_RejectsPartialRecipientFailure()
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:18769/");
        listener.Start();

        var requestTask = listener.GetContextAsync();
        var payload = new WebhookPayload(
            "relay.test",
            "delivery-wxpusher-partial",
            new RelayNotification(0, "WinToastRelay", "Delivery test", "Test body", DateTimeOffset.Parse("2026-08-20T00:00:00Z")));
        var target = new RelayDeliveryTarget(
            RelayDeliveryTarget.WxPusherMode,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            WxPusherApiUrl: "http://127.0.0.1:18769/api/send/message",
            WxPusherAppToken: "AT_test",
            WxPusherUids: "UID_alpha\nUID_beta");

        var deliveryTask = new WebhookClient().DeliverAsync(target, payload);
        var context = await requestTask;
        var responseBytes = Encoding.UTF8.GetBytes("{\"code\":1000,\"msg\":\"处理成功\",\"data\":[{\"uid\":\"UID_alpha\",\"code\":1000},{\"uid\":\"UID_beta\",\"code\":1001}]}");
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = responseBytes.Length;
        await context.Response.OutputStream.WriteAsync(responseBytes);
        context.Response.Close();

        var result = await deliveryTask;

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains("Recipients failed: 1/2", result.Detail);
        Assert.Contains("Codes: 1001", result.Detail);
    }

    [Fact]
    public async Task DeliverAsync_WxPusherMode_RetriesTransientBusinessFailure()
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:18770/");
        listener.Start();

        var requestTask = listener.GetContextAsync();
        var payload = new WebhookPayload(
            "relay.test",
            "delivery-wxpusher-transient",
            new RelayNotification(0, "WinToastRelay", "Delivery test", "Test body", DateTimeOffset.Parse("2026-08-20T00:00:00Z")));
        var target = new RelayDeliveryTarget(
            RelayDeliveryTarget.WxPusherMode,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            WxPusherApiUrl: "http://127.0.0.1:18770/api/send/message",
            WxPusherAppToken: "AT_test",
            WxPusherUids: "UID_alpha");

        var deliveryTask = new WebhookClient().DeliverAsync(target, payload);
        var context = await requestTask;
        var responseBytes = Encoding.UTF8.GetBytes("{\"code\":1005,\"msg\":\"服务器内部错误\",\"data\":null}");
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = responseBytes.Length;
        await context.Response.OutputStream.WriteAsync(responseBytes);
        context.Response.Close();

        var result = await deliveryTask;

        Assert.False(result.Succeeded);
        Assert.True(result.Retryable);
        Assert.Equal("WxPusher 1005 服务器内部错误", result.Detail);
    }

    [Fact]
    public async Task DeliverAsync_WxPusherMode_RejectsMissingRecipientResults()
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:18771/");
        listener.Start();

        var requestTask = listener.GetContextAsync();
        var payload = new WebhookPayload(
            "relay.test",
            "delivery-wxpusher-empty-results",
            new RelayNotification(0, "WinToastRelay", "Delivery test", "Test body", DateTimeOffset.Parse("2026-08-20T00:00:00Z")));
        var target = new RelayDeliveryTarget(
            RelayDeliveryTarget.WxPusherMode,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            WxPusherApiUrl: "http://127.0.0.1:18771/api/send/message",
            WxPusherAppToken: "AT_test",
            WxPusherUids: "UID_alpha");

        var deliveryTask = new WebhookClient().DeliverAsync(target, payload);
        var context = await requestTask;
        var responseBytes = Encoding.UTF8.GetBytes("{\"code\":1000,\"msg\":\"处理成功\",\"data\":[]}");
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = responseBytes.Length;
        await context.Response.OutputStream.WriteAsync(responseBytes);
        context.Response.Close();

        var result = await deliveryTask;

        Assert.False(result.Succeeded);
        Assert.True(result.Retryable);
        Assert.Contains("expected 1 recipient results, received 0", result.Detail);
    }
}
