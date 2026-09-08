using System.Net.Http.Headers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinToastRelay.Models;

namespace WinToastRelay.Services;

public sealed class WebhookClient
{
    // Keep a conservative margin below the payload size accepted by Bark/APNs.
    // This limit applies to the serialized JSON body, not the notification text
    // alone, so the device key and configured Bark fields are included as well.
    private const int BarkPayloadByteLimit = 3500;
    private const string BarkTruncationSuffix = "\n[truncated by WinToastRelay]";
    private const int WxPusherSuccessCode = 1000;
    private const int WxPusherBusinessFailureCode = 1001;
    private const int WxPusherUnauthorizedCode = 1002;
    private const int WxPusherSignatureFailureCode = 1003;
    private const int WxPusherNotFoundCode = 1004;
    private const int WxPusherSummaryCharacterLimit = 100;
    private const int WxPusherTopicLimit = 5;
    private const int WxPusherUidLimit = 2000;
    private const int FeishuTextCharacterLimit = 30000;
    private const int TelegramTextCharacterLimit = 4096;
    private const int DiscordTextCharacterLimit = 2000;
    private const string ChannelTruncationSuffix = "\n[truncated by WinToastRelay]";
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static bool IsValidEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps ||
         (uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))));

    public static bool IsValidConfiguration(RelayDeliveryTarget target)
    {
        if (target.IsBark)
            return !string.IsNullOrWhiteSpace(target.BarkDeviceKey) && IsValidEndpoint(target.BarkServerUrl);

        if (target.IsWxPusher)
        {
            var uids = ParseWxPusherValues(target.WxPusherUids);
            if (!TryParseWxPusherTopicIds(target.WxPusherTopicIds, out var topicIds)) return false;
            return !string.IsNullOrWhiteSpace(target.WxPusherAppToken) &&
                   IsValidEndpoint(target.WxPusherApiUrl) &&
                   uids.Length <= WxPusherUidLimit &&
                   topicIds.Length <= WxPusherTopicLimit &&
                   (uids.Length > 0 || topicIds.Length > 0);
        }

        if (target.IsFeishu)
            return IsValidEndpoint(target.FeishuWebhookUrl);

        if (target.IsTelegram)
            return !string.IsNullOrWhiteSpace(target.TelegramBotToken) &&
                   !string.IsNullOrWhiteSpace(target.TelegramChatId) &&
                   IsValidEndpoint(target.TelegramApiUrl);

        if (target.IsDiscord)
            return IsValidEndpoint(target.DiscordWebhookUrl);

        return target.IsJsonWebhook && IsValidEndpoint(target.WebhookUrl);
    }

    public async Task<DeliveryResult> DeliverAsync(string endpoint, string bearerToken, WebhookPayload payload)
        => await DeliverAsync(new RelayDeliveryTarget(
            RelayDeliveryTarget.JsonWebhookMode,
            endpoint,
            bearerToken,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty), payload);

    public Task<DeliveryResult> DeliverAsync(RelayDeliveryTarget target, WebhookPayload payload)
        => DeliverAsync(target, payload, CancellationToken.None);

    internal async Task<DeliveryResult> DeliverAsync(RelayDeliveryTarget target, WebhookPayload payload, CancellationToken cancellationToken)
    {
        if (!IsValidConfiguration(target))
            return new DeliveryResult(false, target.IsBark
                ? "Invalid Bark configuration"
                : target.IsWxPusher ? "Invalid WxPusher configuration"
                : target.IsFeishu ? "Invalid Feishu configuration"
                : target.IsTelegram ? "Invalid Telegram configuration"
                : target.IsDiscord ? "Invalid Discord configuration"
                : "Invalid webhook URL", false);

        var uri = target.IsBark
            ? BuildBarkPushUri(target.BarkServerUrl)
            : target.IsWxPusher
                ? new Uri(target.WxPusherApiUrl, UriKind.Absolute)
                : target.IsFeishu
                    ? new Uri(target.FeishuWebhookUrl, UriKind.Absolute)
                    : target.IsTelegram
                        ? BuildTelegramSendMessageUri(target)
                        : new Uri(target.IsDiscord ? target.DiscordWebhookUrl : target.WebhookUrl, UriKind.Absolute);

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = target.IsBark
                ? new StringContent(CreateBarkJson(target, payload), Encoding.UTF8, "application/json")
                : target.IsWxPusher
                    ? new StringContent(CreateWxPusherJson(target, payload), Encoding.UTF8, "application/json")
                    : target.IsFeishu
                        ? new StringContent(CreateFeishuJson(target, payload), Encoding.UTF8, "application/json")
                        : target.IsTelegram
                            ? new StringContent(CreateTelegramJson(target, payload), Encoding.UTF8, "application/json")
                            : target.IsDiscord
                                ? new StringContent(CreateDiscordJson(target, payload), Encoding.UTF8, "application/json")
                                : new StringContent(JsonSerializer.Serialize(payload, AppJsonContext.Default.WebhookPayload), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-WinToastRelay-Delivery", payload.DeliveryId);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("WinToastRelay", "0.1"));
        if (target.IsJsonWebhook && !string.IsNullOrWhiteSpace(target.BearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.BearerToken);

        try
        {
            using var response = await HttpClient.SendAsync(request, cancellationToken);
            var retryable = response.StatusCode == System.Net.HttpStatusCode.RequestTimeout ||
                            response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                            (int)response.StatusCode >= 500;
            if (!response.IsSuccessStatusCode)
                return new DeliveryResult(false, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", retryable);

            if (target.IsWxPusher)
                return CreateWxPusherResult(await response.Content.ReadAsStringAsync(), CountWxPusherRecipients(target));
            if (target.IsFeishu)
                return CreateFeishuResult(await response.Content.ReadAsStringAsync());
            if (target.IsTelegram)
                return CreateTelegramResult(await response.Content.ReadAsStringAsync());
            return new DeliveryResult(true, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new DeliveryResult(false, ex.Message, true);
        }
    }

    private static Uri BuildBarkPushUri(string serverUrl)
    {
        var builder = new UriBuilder(new Uri(serverUrl, UriKind.Absolute))
        {
            Query = string.Empty,
        };
        var path = builder.Path.TrimEnd('/');
        if (!path.EndsWith("/push", StringComparison.OrdinalIgnoreCase))
            path += "/push";
        builder.Path = path;
        return builder.Uri;
    }

    private static Uri BuildTelegramSendMessageUri(RelayDeliveryTarget target)
    {
        var builder = new UriBuilder(new Uri(target.TelegramApiUrl, UriKind.Absolute));
        var path = builder.Path.TrimEnd('/');
        builder.Path = $"{path}/bot{target.TelegramBotToken.Trim()}/sendMessage";
        return builder.Uri;
    }

    private static string CreateBarkJson(RelayDeliveryTarget target, WebhookPayload payload)
    {
        var json = new JsonObject();
        foreach (var (key, value) in ParseBarkParameters(target.BarkParameters))
        {
            // These fields are controlled by the existing dedicated settings and
            // templates. Do not let an old query-style parameter silently replace them.
            if (key.Equals("device_key", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("title", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("body", StringComparison.OrdinalIgnoreCase))
                continue;

            json[key] = CreateBarkValue(key, value);
        }

        // Bark's JSON endpoint accepts the device key in the request body, which
        // keeps long titles and bodies out of the request URL entirely.
        json["device_key"] = target.BarkDeviceKey.Trim();
        json["title"] = ApplyTemplate(target.BarkTitleTemplate, payload);
        json["body"] = ApplyTemplate(target.BarkBodyTemplate, payload);

        if (Utf8.GetByteCount(json.ToJsonString()) > BarkPayloadByteLimit)
        {
            // Body is the usual source of oversized payloads. If a custom title
            // template also contains {body}, fit it separately after the body.
            FitBarkTextProperty(json, "body", json["body"]?.GetValue<string>() ?? string.Empty);
            if (Utf8.GetByteCount(json.ToJsonString()) > BarkPayloadByteLimit)
                FitBarkTextProperty(json, "title", json["title"]?.GetValue<string>() ?? string.Empty);
        }

        return json.ToJsonString();
    }

    private static string CreateWxPusherJson(RelayDeliveryTarget target, WebhookPayload payload)
    {
        var uids = ParseWxPusherValues(target.WxPusherUids);
        TryParseWxPusherTopicIds(target.WxPusherTopicIds, out var topicIds);
        var json = new JsonObject
        {
            ["appToken"] = target.WxPusherAppToken.Trim(),
            ["content"] = ApplyTemplate(target.WxPusherContentTemplate, payload, "{title}\n{body}"),
            ["summary"] = TruncateCharacters(ApplyTemplate(target.WxPusherSummaryTemplate, payload, "{app}: {title}"), WxPusherSummaryCharacterLimit),
            ["contentType"] = 1,
        };

        if (uids.Length > 0)
        {
            var uidNodes = new JsonArray();
            foreach (var uid in uids) uidNodes.Add(uid);
            json["uids"] = uidNodes;
        }

        if (topicIds.Length > 0)
        {
            var topicNodes = new JsonArray();
            foreach (var topicId in topicIds) topicNodes.Add(topicId);
            json["topicIds"] = topicNodes;
        }

        return json.ToJsonString();
    }

    private static string CreateFeishuJson(RelayDeliveryTarget target, WebhookPayload payload)
    {
        var text = ComposeText(target.FeishuTitleTemplate, target.FeishuBodyTemplate, payload, FeishuTextCharacterLimit);
        var json = new JsonObject
        {
            ["msg_type"] = "text",
            ["content"] = new JsonObject { ["text"] = text }
        };
        if (!string.IsNullOrWhiteSpace(target.FeishuSecret))
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var message = Encoding.UTF8.GetBytes($"{timestamp}\n{target.FeishuSecret}");
            var key = Encoding.UTF8.GetBytes(target.FeishuSecret);
            json["timestamp"] = timestamp;
            json["sign"] = Convert.ToBase64String(HMACSHA256.HashData(key, message));
        }
        return json.ToJsonString();
    }

    private static string CreateTelegramJson(RelayDeliveryTarget target, WebhookPayload payload)
    {
        var json = new JsonObject
        {
            ["chat_id"] = target.TelegramChatId.Trim(),
            ["text"] = ComposeText(target.TelegramTitleTemplate, target.TelegramBodyTemplate, payload, TelegramTextCharacterLimit),
            ["disable_web_page_preview"] = true
        };
        if (!string.IsNullOrWhiteSpace(target.TelegramParseMode))
            json["parse_mode"] = target.TelegramParseMode.Trim();
        return json.ToJsonString();
    }

    private static string CreateDiscordJson(RelayDeliveryTarget target, WebhookPayload payload)
    {
        var json = new JsonObject
        {
            ["content"] = ComposeText(target.DiscordTitleTemplate, target.DiscordBodyTemplate, payload, DiscordTextCharacterLimit),
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() }
        };
        if (!string.IsNullOrWhiteSpace(target.DiscordUsername))
            json["username"] = target.DiscordUsername.Trim();
        return json.ToJsonString();
    }

    private static string ComposeText(string titleTemplate, string bodyTemplate, WebhookPayload payload, int maxCharacters)
    {
        var title = ApplyTemplate(titleTemplate, payload, "{app}: {title}");
        var body = ApplyTemplate(bodyTemplate, payload, "{body}");
        var text = string.IsNullOrWhiteSpace(title) ? body : string.IsNullOrWhiteSpace(body) ? title : $"{title}\n{body}";
        return TruncateCharacters(text, maxCharacters, ChannelTruncationSuffix);
    }

    private static DeliveryResult CreateFeishuResult(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return new DeliveryResult(true, "Feishu delivered", false);
        try
        {
            using var json = JsonDocument.Parse(responseBody);
            if (json.RootElement.TryGetProperty("code", out var code) && code.TryGetInt32(out var codeValue) && codeValue != 0)
            {
                var message = json.RootElement.TryGetProperty("msg", out var msg) ? msg.GetString() : null;
                return new DeliveryResult(false, $"Feishu {codeValue}{(string.IsNullOrWhiteSpace(message) ? string.Empty : $" {message}")}", false);
            }
            return new DeliveryResult(true, "Feishu delivered", false);
        }
        catch (JsonException)
        {
            return new DeliveryResult(false, "Invalid Feishu response", true);
        }
    }

    private static DeliveryResult CreateTelegramResult(string responseBody)
    {
        try
        {
            using var json = JsonDocument.Parse(responseBody);
            if (json.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                return new DeliveryResult(true, "Telegram delivered", false);
            var description = json.RootElement.TryGetProperty("description", out var descriptionElement) ? descriptionElement.GetString() : null;
            return new DeliveryResult(false, $"Telegram failed{(string.IsNullOrWhiteSpace(description) ? string.Empty : $": {description}")}", false);
        }
        catch (JsonException)
        {
            return new DeliveryResult(false, "Invalid Telegram response", true);
        }
    }

    private static DeliveryResult CreateWxPusherResult(string responseBody, int expectedRecipientCount)
    {
        try
        {
            using var json = JsonDocument.Parse(responseBody);
            if (!json.RootElement.TryGetProperty("code", out var codeElement) || !codeElement.TryGetInt32(out var code))
                return new DeliveryResult(false, "Invalid WxPusher response", true);

            string? message = null;
            if (json.RootElement.TryGetProperty("msg", out var messageElement) && messageElement.ValueKind == JsonValueKind.String)
                message = messageElement.GetString();
            else if (json.RootElement.TryGetProperty("message", out messageElement) && messageElement.ValueKind == JsonValueKind.String)
                message = messageElement.GetString();

            var detail = string.IsNullOrWhiteSpace(message) ? $"WxPusher {code}" : $"WxPusher {code} {message}";
            if (code != WxPusherSuccessCode)
                return new DeliveryResult(false, detail, IsWxPusherBusinessErrorRetryable(code));

            if (!json.RootElement.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
                return new DeliveryResult(false, "Invalid WxPusher response: missing recipient results", true);

            var recipientCount = dataElement.GetArrayLength();
            if (recipientCount != expectedRecipientCount)
                return new DeliveryResult(false,
                    $"Invalid WxPusher response: expected {expectedRecipientCount} recipient results, received {recipientCount}", true);

            var failedCodes = new List<int>();
            foreach (var recipient in dataElement.EnumerateArray())
            {
                if (recipient.ValueKind != JsonValueKind.Object ||
                    !recipient.TryGetProperty("code", out var recipientCodeElement) ||
                    !recipientCodeElement.TryGetInt32(out var recipientCode))
                    return new DeliveryResult(false, "Invalid WxPusher response: malformed recipient result", true);
                if (recipientCode != WxPusherSuccessCode) failedCodes.Add(recipientCode);
            }

            if (failedCodes.Count == 0) return new DeliveryResult(true, detail, false);

            var failedCodeList = string.Join(", ", failedCodes.Distinct().Order());
            return new DeliveryResult(false,
                $"{detail} · Recipients failed: {failedCodes.Count}/{recipientCount} · Codes: {failedCodeList}",
                failedCodes.Any(IsWxPusherBusinessErrorRetryable));
        }
        catch (JsonException)
        {
            return new DeliveryResult(false, "Invalid WxPusher response", true);
        }
    }

    private static bool IsWxPusherBusinessErrorRetryable(int code) => code switch
    {
        WxPusherBusinessFailureCode or
        WxPusherUnauthorizedCode or
        WxPusherSignatureFailureCode or
        WxPusherNotFoundCode => false,
        _ => true,
    };

    private static int CountWxPusherRecipients(RelayDeliveryTarget target)
    {
        var count = ParseWxPusherValues(target.WxPusherUids).Length;
        if (TryParseWxPusherTopicIds(target.WxPusherTopicIds, out var topicIds)) count += topicIds.Length;
        return count;
    }

    private static void FitBarkTextProperty(JsonObject json, string propertyName, string originalValue)
    {
        var low = 0;
        var high = Utf8.GetByteCount(originalValue);
        var best = string.Empty;

        while (low <= high)
        {
            var candidateBudget = low + ((high - low) / 2);
            var candidate = TruncateUtf8(originalValue, candidateBudget);
            json[propertyName] = candidate;
            if (Utf8.GetByteCount(json.ToJsonString()) <= BarkPayloadByteLimit)
            {
                best = candidate;
                low = candidateBudget + 1;
            }
            else
            {
                high = candidateBudget - 1;
            }
        }

        json[propertyName] = best;
    }

    private static string TruncateUtf8(string value, int maxBytes)
    {
        if (maxBytes <= 0) return string.Empty;
        if (Utf8.GetByteCount(value) <= maxBytes) return value;

        var suffixBytes = Utf8.GetByteCount(BarkTruncationSuffix);
        if (suffixBytes >= maxBytes) return string.Empty;

        var contentBudget = maxBytes - suffixBytes;
        var builder = new StringBuilder();
        var usedBytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var runeText = rune.ToString();
            var runeBytes = Utf8.GetByteCount(runeText);
            if (usedBytes + runeBytes > contentBudget) break;
            builder.Append(runeText);
            usedBytes += runeBytes;
        }

        return builder.Append(BarkTruncationSuffix).ToString();
    }

    private static JsonNode CreateBarkValue(string key, string value)
    {
        // Bark documents badge and ttl as integer JSON values. Keep the other
        // legacy key=value options as strings so existing configurations retain
        // their exact meaning (for example call=1 and isArchive=1).
        if ((key.Equals("badge", StringComparison.OrdinalIgnoreCase) ||
             key.Equals("ttl", StringComparison.OrdinalIgnoreCase)) &&
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return JsonValue.Create(number)!;

        return JsonValue.Create(value)!;
    }

    private static string ApplyTemplate(string template, WebhookPayload payload, string defaultTemplate = "{body}")
    {
        var notification = payload.Notification;
        var value = string.IsNullOrWhiteSpace(template) ? defaultTemplate : template;
        return value
            .Replace("{app}", notification.App, StringComparison.OrdinalIgnoreCase)
            .Replace("{title}", notification.Title, StringComparison.OrdinalIgnoreCase)
            .Replace("{body}", notification.Body, StringComparison.OrdinalIgnoreCase)
            .Replace("{id}", notification.Id.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{eventType}", payload.EventType, StringComparison.OrdinalIgnoreCase)
            .Replace("{createdAt}", notification.CreatedAt.ToString("O"), StringComparison.OrdinalIgnoreCase);
    }

    private static string TruncateCharacters(string value, int maxCharacters, string suffix = "")
    {
        if (value.EnumerateRunes().Count() <= maxCharacters) return value;
        if (string.IsNullOrEmpty(suffix)) suffix = string.Empty;
        var suffixLength = suffix.EnumerateRunes().Count();
        if (suffixLength >= maxCharacters)
            return string.Concat(suffix.EnumerateRunes().Take(maxCharacters).Select(rune => rune.ToString()));

        var builder = new StringBuilder();
        var characterCount = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (characterCount == maxCharacters - suffixLength) break;
            builder.Append(rune);
            characterCount++;
        }
        return builder.Append(suffix).ToString();
    }

    private static string[] ParseWxPusherValues(string values) => values
        .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private static bool TryParseWxPusherTopicIds(string values, out long[] topicIds)
    {
        var rawValues = ParseWxPusherValues(values);
        var parsedValues = new List<long>(rawValues.Length);
        foreach (var value in rawValues)
        {
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var topicId) || topicId <= 0)
            {
                topicIds = [];
                return false;
            }
            if (!parsedValues.Contains(topicId)) parsedValues.Add(topicId);
        }
        topicIds = [.. parsedValues];
        return true;
    }

    private static IEnumerable<(string Key, string Value)> ParseBarkParameters(string parameters)
    {
        foreach (var line in parameters.Split(['\r', '\n', '&'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith('#')) continue;
            var separator = line.IndexOf('=');
            var key = (separator < 0 ? line : line[..separator]).Trim();
            var value = (separator < 0 ? string.Empty : line[(separator + 1)..]).Trim();
            if (key.Length == 0) continue;
            yield return (key, value);
        }
    }
}
