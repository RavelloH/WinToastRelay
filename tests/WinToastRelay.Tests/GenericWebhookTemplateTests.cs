using System.Net;
using System.Text.Json;
using WinToastRelay.Models;
using WinToastRelay.Services;

namespace WinToastRelay.Tests;

public sealed class GenericWebhookTemplateTests
{
    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(500)]
    public async Task GenericResponseDetailsDoNotPersistServerEchoedSecrets(int statusCode)
    {
        const string secret = "credential-echo-must-not-reach-history";
        var handler = new RecordingHandler(() => Task.FromResult(new HttpResponseMessage((HttpStatusCode)statusCode)
        {
            ReasonPhrase = secret,
            Content = new StringContent(secret)
        }));
        using var httpClient = new HttpClient(handler);
        var result = await new WebhookClient(httpClient).DeliverAsync(CreateTarget(), CreatePayload());

        Assert.Equal($"HTTP {statusCode}", result.Detail);
        Assert.Equal(statusCode == 200, result.Succeeded);
        Assert.Equal(statusCode == 500, result.Retryable);
        Assert.DoesNotContain(secret, result.Detail);
    }

    [Fact]
    public async Task GenericNetworkFailureDoesNotPersistExceptionData()
    {
        const string secret = "credential-in-exception-must-not-reach-history";
        var handler = new RecordingHandler(() => Task.FromException<HttpResponseMessage>(new HttpRequestException(secret)));
        using var httpClient = new HttpClient(handler);
        var result = await new WebhookClient(httpClient).DeliverAsync(CreateTarget(), CreatePayload());

        Assert.False(result.Succeeded);
        Assert.True(result.Retryable);
        Assert.Equal("Webhook network request failed", result.Detail);
        Assert.DoesNotContain(secret, result.Detail);
    }

    [Fact]
    public void EmptyTemplateKeepsLegacyPayloadShapeAndOmitsPackageMetadata()
    {
        var payload = CreatePayload() with
        {
            Notification = CreatePayload().Notification with { PackageName = "com.example.calendar" }
        };

        AssertBuild(CreateTarget(), payload, out var json, out var headers);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(["eventType", "deliveryId", "notification"], root.EnumerateObject().Select(property => property.Name));
        var notification = root.GetProperty("notification");
        Assert.Equal(["id", "app", "title", "body", "createdAt"],
            notification.EnumerateObject().Select(property => property.Name));
        Assert.Equal(7u, notification.GetProperty("id").GetUInt32());
        Assert.Equal("Calendar", notification.GetProperty("app").GetString());
        Assert.Empty(headers);
    }

    [Fact]
    public void TemplateReplacesOnlyStringValuesRecursivelyAndPreservesJsonTypesAndKeys()
    {
        var notification = CreatePayload().Notification with
        {
            Title = "Meeting \"review\"\n🗓️",
            Body = "First line\nsecond line 😀",
            PackageName = "com.example.calendar"
        };
        var payload = CreatePayload() with { Notification = notification };
        var target = CreateTarget(template: """
            {
              "literal {title}": "{APP} — {Title}",
              "notification": {
                "body": "{body}",
                "contentAlias": "{cOnTeNt}",
                "package": "{PackageName}",
                "idText": "{id}",
                "event": "{eventType}",
                "created": "{createdAt}",
                "delivery": "{deliveryId}"
              },
              "array": ["{app}", 23, false, null, {"value": "{body}"}],
              "number": 42,
              "boolean": true,
              "nothing": null
            }
            """);

        AssertBuild(target, payload, out var json, out _);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("Calendar — Meeting \"review\"\n🗓️", root.GetProperty("literal {title}").GetString());
        var rendered = root.GetProperty("notification");
        Assert.Equal("First line\nsecond line 😀", rendered.GetProperty("body").GetString());
        Assert.Equal("First line\nsecond line 😀", rendered.GetProperty("contentAlias").GetString());
        Assert.Equal("com.example.calendar", rendered.GetProperty("package").GetString());
        Assert.Equal("7", rendered.GetProperty("idText").GetString());
        Assert.Equal("relay.test", rendered.GetProperty("event").GetString());
        Assert.Equal("delivery-test", rendered.GetProperty("delivery").GetString());
        Assert.Equal(payload.Notification.CreatedAt, DateTimeOffset.Parse(rendered.GetProperty("created").GetString()!));
        var array = root.GetProperty("array");
        Assert.Equal("Calendar", array[0].GetString());
        Assert.Equal(JsonValueKind.Number, array[1].ValueKind);
        Assert.Equal(23, array[1].GetInt32());
        Assert.Equal(JsonValueKind.False, array[2].ValueKind);
        Assert.Equal(JsonValueKind.Null, array[3].ValueKind);
        Assert.Equal("First line\nsecond line 😀", array[4].GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("number").ValueKind);
        Assert.Equal(JsonValueKind.True, root.GetProperty("boolean").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("nothing").ValueKind);
    }

    [Fact]
    public void ExpandedNotificationTextCannotIntroduceASecretReference()
    {
        const string marker = "unintended-secret-value";
        var payload = CreatePayload() with
        {
            Notification = CreatePayload().Notification with { Body = "literal {secret:HOOK}" }
        };
        var target = CreateTarget(
            template: "{\"body\":\"{body}\"}",
            secrets: new Dictionary<string, string> { ["HOOK"] = marker });

        AssertBuild(target, payload, out var json, out _);

        using var document = JsonDocument.Parse(json);
        Assert.Equal("literal {secret:HOOK}", document.RootElement.GetProperty("body").GetString());
        Assert.DoesNotContain(marker, json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"body\":\"{mystery}\"}", "Unknown webhook template variable")]
    [InlineData("{\"body\":\"{secret:bad name}\"}", "Unknown webhook template variable")]
    [InlineData("{\"body\":\"{secret:HOOK}\"}", "Missing webhook secret")]
    [InlineData("{\"body\":\"x\"} trailing", "Invalid webhook JSON template")]
    [InlineData("[]", "Invalid webhook JSON template")]
    public void InvalidTemplatesReturnStableNonSensitiveErrors(string template, string expectedError)
    {
        var target = CreateTarget(template: template);

        Assert.False(GenericWebhookTemplate.TryBuild(target, CreatePayload(), false,
            out var json, out var headers, out var error));

        Assert.Empty(json);
        Assert.Empty(headers);
        Assert.Equal(expectedError, error);
    }

    [Theory]
    [InlineData("{\"body\":\"one\",\"body\":\"two\"}")]
    [InlineData("{\"outer\":{\"value\":\"one\",\"value\":\"two\"}}")]
    public void DuplicateJsonObjectKeysAreRejectedWithoutLeakingParserDetails(string template)
    {
        Assert.False(GenericWebhookTemplate.TryBuild(CreateTarget(template: template), CreatePayload(), false,
            out _, out _, out var error));

        Assert.Equal("Invalid webhook JSON template", error);
    }

    [Fact]
    public void TemplateSizeLimitHasStableError()
    {
        var target = CreateTarget(template: new string(' ', 65_537));

        Assert.False(GenericWebhookTemplate.TryBuild(target, CreatePayload(), false,
            out _, out _, out var error));

        Assert.Equal("Invalid webhook JSON template", error);
    }

    [Fact]
    public void SecretValuesAreExpandedButMaskedInPreviewAndHeaderValuesAreAlwaysMasked()
    {
        const string secret = "never-show-this-secret";
        var target = CreateTarget(
            template: "{\"auth\":\"{secret:API_KEY}\",\"visible\":\"hello\"}",
            headers: "X-Token: {secret:API_KEY}\nX-Visible: hello",
            secrets: new Dictionary<string, string> { ["API_KEY"] = secret });

        AssertBuild(target, CreatePayload(), out var json, out var headers, redactSecrets: false);
        Assert.Contains(secret, json, StringComparison.Ordinal);
        Assert.Equal(secret, headers["X-Token"]);
        Assert.Equal("hello", headers["X-Visible"]);

        AssertBuild(target, CreatePayload(), out var previewJson, out var previewHeaders, redactSecrets: true);
        Assert.DoesNotContain(secret, previewJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, string.Join("\n", previewHeaders.Values), StringComparison.Ordinal);
        using var preview = JsonDocument.Parse(previewJson);
        Assert.Equal("***", preview.RootElement.GetProperty("auth").GetString());
        Assert.All(previewHeaders.Values, value => Assert.Equal("***", value));
    }

    [Fact]
    public void SecretNamesFollowTheDocumentedAsciiGrammar()
    {
        Assert.True(GenericWebhookTemplate.IsValidSecretName("A"));
        Assert.True(GenericWebhookTemplate.IsValidSecretName("a9_B-c"));
        Assert.True(GenericWebhookTemplate.IsValidSecretName(new string('x', 64)));
        Assert.False(GenericWebhookTemplate.IsValidSecretName(""));
        Assert.False(GenericWebhookTemplate.IsValidSecretName("9startsWithDigit"));
        Assert.False(GenericWebhookTemplate.IsValidSecretName("contains space"));
        Assert.False(GenericWebhookTemplate.IsValidSecretName("has.dot"));
        Assert.False(GenericWebhookTemplate.IsValidSecretName(new string('x', 65)));
    }

    [Theory]
    [InlineData("X-Test: first\nX-test: second")]
    [InlineData("Host: hooks.example.com")]
    [InlineData("Content-Length: 20")]
    [InlineData("Connection: keep-alive")]
    [InlineData("X-Test: {body}", "First line\nsecond line 😀")]
    [InlineData("X-Test: non-ascii-值")]
    public void InvalidOrUnsafeHeadersAreRejected(string headerText, string? body = null)
    {
        var payload = body is null
            ? CreatePayload()
            : CreatePayload() with { Notification = CreatePayload().Notification with { Body = body } };

        Assert.False(GenericWebhookTemplate.TryBuild(CreateTarget(headers: headerText), payload, false,
            out _, out _, out var error));

        Assert.Equal("Invalid webhook headers", error);
    }

    [Fact]
    public void AuthorizationHeaderCannotConflictWithConfiguredBearerToken()
    {
        var target = CreateTarget(bearerToken: "configured-bearer", headers: "Authorization: Basic custom");

        Assert.False(GenericWebhookTemplate.TryBuild(target, CreatePayload(), false,
            out _, out _, out var error));

        Assert.Equal("Conflicting webhook authorization", error);
    }

    [Theory]
    [InlineData("bad\r\nInjected: value")]
    [InlineData("bad\tvalue")]
    [InlineData("秘密")]
    public void InvalidBuiltinBearerValuesAreRejectedBeforeRequestConstruction(string bearerToken)
    {
        Assert.False(GenericWebhookTemplate.TryBuild(CreateTarget(bearerToken: bearerToken), CreatePayload(), false,
            out _, out _, out var error));

        Assert.Equal("Invalid webhook headers", error);
    }

    [Fact]
    public void OversizedBuiltinBearerValueIsRejected()
    {
        Assert.False(GenericWebhookTemplate.TryBuild(CreateTarget(bearerToken: new string('x', 8_193)), CreatePayload(), false,
            out _, out _, out var error));

        Assert.Equal("Invalid webhook headers", error);
    }

    [Fact]
    public void HeaderValuesExpandedFromSecretsAreValidatedBeforePreviewMasking()
    {
        var target = CreateTarget(
            headers: "X-Value: {secret:VALUE}",
            secrets: new Dictionary<string, string> { ["VALUE"] = "line one\r\nX-Injected: yes" });

        Assert.False(GenericWebhookTemplate.TryBuild(target, CreatePayload(), true,
            out _, out _, out var error));

        Assert.Equal("Invalid webhook headers", error);
    }

    [Theory]
    [InlineData("Bad Name: value", "Invalid webhook headers")]
    [InlineData("Proxy-Authorization: value", "Invalid webhook headers")]
    [InlineData("User-Agent: value", "Invalid webhook headers")]
    [InlineData("X-One: a\nmissing-colon", "Invalid webhook headers")]
    public void RestrictedHeaderNamesAndMalformedLinesAreRejected(string headerText, string expectedError)
    {
        Assert.False(GenericWebhookTemplate.TryBuild(CreateTarget(headers: headerText), CreatePayload(), false,
            out _, out _, out var error));

        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void MoreThanThirtyTwoCustomHeaderLinesAreRejected()
    {
        var headerText = string.Join("\n", Enumerable.Range(1, 33).Select(index => $"X-Test-{index}: value"));

        Assert.False(GenericWebhookTemplate.TryBuild(CreateTarget(headers: headerText), CreatePayload(), false,
            out _, out _, out var error));

        Assert.Equal("Invalid webhook headers", error);
    }

    [Fact]
    public void CustomHeaderValueLengthIsBounded()
    {
        var target = CreateTarget(headers: $"X-Test: {new string('x', 8_193)}");

        Assert.False(GenericWebhookTemplate.TryBuild(target, CreatePayload(), false,
            out _, out _, out var error));

        Assert.Equal("Invalid webhook headers", error);
    }

    [Fact]
    public void OutputByteLimitReturnsPayloadTooLargeWithoutReturningPartialJson()
    {
        var payload = CreatePayload() with
        {
            Notification = CreatePayload().Notification with { Body = string.Concat(Enumerable.Repeat("😀", 262_145)) }
        };
        var target = CreateTarget(template: "{\"body\":\"{body}\"}");

        Assert.False(GenericWebhookTemplate.TryBuild(target, payload, false,
            out var json, out _, out var error));

        Assert.Empty(json);
        Assert.Equal("Webhook payload too large", error);
    }

    [Fact]
    public void SourceGeneratedQueueJsonRetainsPackageMetadataButSettingsNeverPersistSecrets()
    {
        var payload = CreatePayload() with
        {
            Notification = CreatePayload().Notification with { PackageName = "com.example.calendar" }
        };

        var queuedJson = JsonSerializer.Serialize(payload, AppJsonContext.Default.WebhookPayload);
        var roundTrip = JsonSerializer.Deserialize(queuedJson, AppJsonContext.Default.WebhookPayload);
        Assert.NotNull(roundTrip);
        Assert.Equal("com.example.calendar", roundTrip.Notification.PackageName);

        const string credential = "must-not-be-persisted";
        var settingsJson = JsonSerializer.Serialize(new RelaySettings());
        Assert.DoesNotContain("WebhookSecrets", settingsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(credential, settingsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationComparisonTreatsSecretSnapshotsStructurally()
    {
        var noSecrets = CreateTarget();
        var emptySecrets = CreateTarget(secrets: new Dictionary<string, string>());
        var sameValuesNewReference = CreateTarget(secrets: new Dictionary<string, string>
        {
            ["API_KEY"] = "one",
            ["SIGNATURE"] = "two"
        });
        var sameValuesDifferentReference = CreateTarget(secrets: new Dictionary<string, string>
        {
            ["API_KEY"] = "one",
            ["SIGNATURE"] = "two"
        });

        Assert.True(noSecrets.HasSameConfiguration(emptySecrets));
        Assert.True(sameValuesNewReference.HasSameConfiguration(sameValuesDifferentReference));
        Assert.True(CreateTarget(secrets: new Dictionary<string, string> { ["api_key"] = "one" })
            .HasSameConfiguration(CreateTarget(secrets: new Dictionary<string, string> { ["API_KEY"] = "one" })));
        Assert.False(CreateTarget(secrets: new Dictionary<string, string> { ["API_KEY"] = "one" })
            .HasSameConfiguration(CreateTarget(secrets: new Dictionary<string, string> { ["API_KEY"] = "changed" })));
        Assert.False(CreateTarget(secrets: new Dictionary<string, string> { ["API_KEY"] = "one" })
            .HasSameConfiguration(CreateTarget(secrets: new Dictionary<string, string> { ["OTHER_KEY"] = "one" })));
    }

    [Fact]
    public void OtherDeliveryModesIgnoreGenericJsonConfiguration()
    {
        var target = CreateTarget(mode: RelayDeliveryTarget.BarkMode,
            template: "not-json", headers: "Host: forbidden", secrets: new Dictionary<string, string>());

        Assert.Equal(string.Empty, GenericWebhookTemplate.GetConfigurationError(target));
    }

    [Fact]
    public async Task ClientPostsExpandedCustomJsonAndHeaders()
    {
        const string secret = "custom-header-secret";
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var client = new WebhookClient(httpClient);
        var target = CreateTarget(
            template: "{\"message\":\"{body}\",\"credential\":\"{secret:HOOK}\"}",
            headers: "X-Relay-App: {app}\nX-Custom-Secret: {secret:HOOK}",
            secrets: new Dictionary<string, string> { ["HOOK"] = secret });

        var result = await client.DeliverAsync(target, CreatePayload());

        Assert.True(result.Succeeded);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://example.test/hook", handler.RequestUri?.AbsoluteUri);
        Assert.Equal("Calendar", handler.Headers["X-Relay-App"]);
        Assert.Equal(secret, handler.Headers["X-Custom-Secret"]);
        using var document = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("Meeting soon", document.RootElement.GetProperty("message").GetString());
        Assert.Equal(secret, document.RootElement.GetProperty("credential").GetString());
    }

    [Fact]
    public async Task ClientDoesNotDispatchInvalidTemplateOrHeaderConfiguration()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var client = new WebhookClient(httpClient);
        var invalidTargets = new[]
        {
            CreateTarget(template: "{\"body\":"),
            CreateTarget(template: "{\"body\":\"{missing}\"}"),
            CreateTarget(template: "{\"body\":\"{secret:API_KEY}\"}"),
            CreateTarget(headers: "Host: forged.example"),
            CreateTarget(bearerToken: "bad\r\nInjected: value"),
            CreateTarget(bearerToken: "bad\tvalue"),
            CreateTarget(bearerToken: "秘密"),
            CreateTarget(bearerToken: new string('x', 8_193))
        };

        foreach (var target in invalidTargets)
        {
            var result = await client.DeliverAsync(target, CreatePayload());

            Assert.False(result.Succeeded);
            Assert.False(result.Retryable);
            Assert.NotEmpty(result.Detail);
            Assert.Equal(0, handler.RequestCount);
        }
    }

    private static void AssertBuild(
        RelayDeliveryTarget target,
        WebhookPayload payload,
        out string json,
        out IReadOnlyDictionary<string, string> headers,
        bool redactSecrets = false)
    {
        Assert.True(GenericWebhookTemplate.TryBuild(target, payload, redactSecrets,
            out json, out headers, out var error), error);
        Assert.Empty(error);
    }

    private static RelayDeliveryTarget CreateTarget(
        string mode = RelayDeliveryTarget.JsonWebhookMode,
        string template = "",
        string headers = "",
        string bearerToken = "",
        IReadOnlyDictionary<string, string>? secrets = null) => new(
            mode,
            "https://example.test/hook",
            bearerToken,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            WebhookJsonTemplate: template,
            WebhookHeaders: headers,
            WebhookSecrets: secrets);

    private static WebhookPayload CreatePayload() => new(
        "relay.test",
        "delivery-test",
        new RelayNotification(7, "Calendar", "Reminder", "Meeting soon", DateTimeOffset.Parse("2026-08-20T00:00:00Z")));

    private sealed class RecordingHandler(Func<Task<HttpResponseMessage>>? responseFactory = null) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? RequestBody { get; private set; }
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            foreach (var header in request.Headers)
                Headers[header.Key] = string.Join(",", header.Value);
            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                    Headers[header.Key] = string.Join(",", header.Value);
            }
            return responseFactory is null
                ? new HttpResponseMessage(HttpStatusCode.Accepted)
                : await responseFactory();
        }
    }
}
