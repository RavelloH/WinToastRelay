using System.Net;
using System.Text;
using System.Text.Json;
using WinToastRelay.Models;
using WinToastRelay.Services;

namespace WinToastRelay.Tests;

public sealed class WebhookTransportTests
{
    [Fact]
    public async Task DeliverAsync_PreCanceledRequestIsNotDispatched()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new WebhookClient(httpClient);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DeliverAsync(
            CreateJsonTarget("https://relay.example/hooks"), CreatePayload(), cancellation.Token));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task DeliverAsync_CancellationOfInFlightRequestIsNotReportedAsDeliveryFailure()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            requestStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var httpClient = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var client = new WebhookClient(httpClient);
        var delivery = client.DeliverAsync(
            CreateJsonTarget("https://relay.example/hooks"), CreatePayload(), cancellation.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public void ConfigurationErrorsDistinguishMissingApprovalFromInvalidEndpoint()
    {
        var unapproved = CreateJsonTarget("http://10.0.0.8/hooks");
        var invalid = CreateJsonTarget("ftp://10.0.0.8/hooks");

        Assert.Equal(EndpointTransportPolicy.HttpApprovalRequired, WebhookClient.GetConfigurationError(unapproved));
        Assert.Equal("Invalid webhook URL", WebhookClient.GetConfigurationError(invalid));
        Assert.NotEqual(WebhookClient.GetConfigurationError(unapproved), WebhookClient.GetConfigurationError(invalid));
    }

    [Fact]
    public async Task DeliverAsync_DoesNotDispatchAnUnapprovedHttpRequest()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var client = new WebhookClient(httpClient);
        var target = CreateJsonTarget("http://10.0.0.8/hooks");

        var result = await client.DeliverAsync(target, CreatePayload());

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Equal(EndpointTransportPolicy.HttpApprovalRequired, result.Detail);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task DeliverAsync_DoesNotDispatchWhenApprovalNoLongerMatchesChangedEndpoint()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var client = new WebhookClient(httpClient);
        var target = CreateJsonTarget(
            "http://10.0.0.8/changed?key=two",
            approvedEndpoint: "http://10.0.0.8/original?key=one");

        var result = await client.DeliverAsync(target, CreatePayload());

        Assert.False(result.Succeeded);
        Assert.Equal(EndpointTransportPolicy.HttpApprovalRequired, result.Detail);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task DeliverAsync_ApprovedPrivateHttpPostsJsonWithBearerToken()
    {
        const string endpoint = "http://192.168.1.20:8080/relay?tenant=west";
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)));
        using var httpClient = new HttpClient(handler);
        var client = new WebhookClient(httpClient);
        var target = CreateJsonTarget(endpoint, approvedEndpoint: endpoint, bearerToken: "secret-token");

        var result = await client.DeliverAsync(target, CreatePayload());

        Assert.True(result.Succeeded);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(endpoint, handler.RequestUri?.AbsoluteUri);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("secret-token", handler.AuthorizationParameter);
        using var json = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("relay.test", json.RootElement.GetProperty("eventType").GetString());
        Assert.Equal("delivery-test", json.RootElement.GetProperty("deliveryId").GetString());
    }

    [Fact]
    public void ConfigurationValidationApprovesNonLoopbackDnsEndpointWithoutResolvingIt()
    {
        const string endpoint = "http://relay-device.local:8123/hooks?site=garage";
        var target = CreateJsonTarget(endpoint, approvedEndpoint: endpoint);

        Assert.Equal(string.Empty, WebhookClient.GetConfigurationError(target));
    }

    [Fact]
    public async Task DeliverAsync_BarkApprovalAppliesToConfiguredBaseBeforeAddingPushRoute()
    {
        const string configuredEndpoint = "http://10.20.0.8:8090/bark?tenant=home";
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var httpClient = new HttpClient(handler);
        var client = new WebhookClient(httpClient);
        var target = new RelayDeliveryTarget(
            RelayDeliveryTarget.BarkMode,
            string.Empty,
            string.Empty,
            configuredEndpoint,
            "device-key",
            "{title}",
            "{body}",
            string.Empty,
            ApprovedHttpEndpoint: configuredEndpoint);

        var result = await client.DeliverAsync(target, CreatePayload());

        Assert.True(result.Succeeded);
        Assert.Equal("http://10.20.0.8:8090/bark/push", handler.RequestUri?.AbsoluteUri);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task DeliverAsync_TelegramApprovalAppliesToConfiguredBaseBeforeAddingBotRoute()
    {
        const string configuredEndpoint = "http://10.20.0.9:8091/api?version=1";
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
        }));
        using var httpClient = new HttpClient(handler);
        var client = new WebhookClient(httpClient);
        var target = new RelayDeliveryTarget(
            RelayDeliveryTarget.TelegramMode,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            TelegramApiUrl: configuredEndpoint,
            TelegramBotToken: "123:ABC",
            TelegramChatId: "-100123",
            ApprovedHttpEndpoint: configuredEndpoint);

        var result = await client.DeliverAsync(target, CreatePayload());

        Assert.True(result.Succeeded);
        Assert.Equal("http://10.20.0.9:8091/api/bot123:ABC/sendMessage?version=1", handler.RequestUri?.AbsoluteUri);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task DefaultClientDoesNotFollowRedirectToSecondListener(int redirectStatusCode)
    {
        using var redirectListener = new HttpListener();
        redirectListener.Prefixes.Add("http://127.0.0.1:18775/");
        redirectListener.Start();
        using var destinationListener = new HttpListener();
        destinationListener.Prefixes.Add("http://127.0.0.1:18776/");
        destinationListener.Start();

        var destinationObservation = ObserveAndAnswerOneRequestAsync(destinationListener, TimeSpan.FromSeconds(1));
        var redirectAccept = redirectListener.GetContextAsync();
        var deliveryTask = new WebhookClient().DeliverAsync(
            "http://127.0.0.1:18775/redirect", "redirect-test-token", CreatePayload());
        var redirectContext = await redirectAccept.WaitAsync(TimeSpan.FromSeconds(5));
        using (var reader = new StreamReader(redirectContext.Request.InputStream))
            _ = await reader.ReadToEndAsync();
        redirectContext.Response.StatusCode = redirectStatusCode;
        redirectContext.Response.RedirectLocation = "http://127.0.0.1:18776/should-not-receive-post";
        redirectContext.Response.Close();

        var result = await deliveryTask.WaitAsync(TimeSpan.FromSeconds(5));
        var destinationRequest = await destinationObservation;

        // In particular, a 307 or 308 must not replay the original POST body to the destination.
        Assert.Null(destinationRequest);
        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains($"HTTP {redirectStatusCode}", result.Detail);
        Assert.Contains("Redirect blocked", result.Detail);
    }

    private static RelayDeliveryTarget CreateJsonTarget(string endpoint, string approvedEndpoint = "", string bearerToken = "") => new(
        RelayDeliveryTarget.JsonWebhookMode,
        endpoint,
        bearerToken,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        ApprovedHttpEndpoint: approvedEndpoint);

    private static WebhookPayload CreatePayload() => new(
        "relay.test",
        "delivery-test",
        new RelayNotification(7, "Calendar", "Reminder", "Meeting soon", DateTimeOffset.Parse("2026-08-20T00:00:00Z")));

    private static async Task<ObservedRequest?> ObserveAndAnswerOneRequestAsync(HttpListener listener, TimeSpan timeout)
    {
        var acceptTask = listener.GetContextAsync();
        var completed = await Task.WhenAny(acceptTask, Task.Delay(timeout));
        if (completed != acceptTask)
        {
            listener.Close();
            try
            {
                _ = await acceptTask;
            }
            catch (HttpListenerException)
            {
                // Closing the listener cancels the pending accept.
            }
            catch (ObjectDisposedException)
            {
                // Closing the listener cancels the pending accept.
            }
            return null;
        }

        var context = await acceptTask;
        var observedRequest = new ObservedRequest(context.Request.HttpMethod);
        using (var reader = new StreamReader(context.Request.InputStream))
            _ = await reader.ReadToEndAsync();
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.Close();
        return observedRequest;
    }

    private sealed record ObservedRequest(string Method);

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? responseFactory = null)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return responseFactory is null
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : await responseFactory(request, cancellationToken);
        }
    }
}
