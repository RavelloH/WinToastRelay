using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinToastRelay.Models;

namespace WinToastRelay.Services;

public static class GenericWebhookTemplate
{
    public const string InvalidTemplate = "Invalid webhook JSON template";
    public const string InvalidHeaders = "Invalid webhook headers";
    public const string MissingSecret = "Missing webhook secret";
    public const string UnknownVariable = "Unknown webhook template variable";
    public const string AuthorizationConflict = "Conflicting webhook authorization";
    public const string PayloadTooLarge = "Webhook payload too large";

    private const int MaximumTemplateCharacters = 65_536;
    private const int MaximumBodyBytes = 1_048_576;
    private const int MaximumHeaderCount = 32;
    private const int MaximumHeaderValueLength = 8_192;
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static bool TryBuild(
        RelayDeliveryTarget target,
        WebhookPayload payload,
        bool redactSecrets,
        out string json,
        out IReadOnlyDictionary<string, string> headers,
        out string error)
    {
        json = string.Empty;
        headers = EmptyHeaders();
        error = string.Empty;

        var template = target.WebhookJsonTemplate ?? string.Empty;
        if (template.Length > MaximumTemplateCharacters)
            return Fail(InvalidTemplate, out json, out headers, out error);

        string builtJson;
        if (string.IsNullOrWhiteSpace(template))
        {
            try
            {
                var legacy = JsonNode.Parse(JsonSerializer.Serialize(payload, AppJsonContext.Default.WebhookPayload));
                if (legacy is not JsonObject root || root["notification"] is not JsonObject notification)
                    return Fail(InvalidTemplate, out json, out headers, out error);

                // PackageName is retained in queue persistence but is deliberately
                // absent from the established generic webhook wire shape.
                notification.Remove("packageName");
                builtJson = legacy.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
            {
                return Fail(InvalidTemplate, out json, out headers, out error);
            }
        }
        else
        {
            try
            {
                var parsed = JsonNode.Parse(template);
                if (parsed is not JsonObject)
                    return Fail(InvalidTemplate, out json, out headers, out error);

                var replaceError = ReplaceStringValues(parsed, target, payload, redactSecrets);
                if (replaceError.Length != 0)
                    return Fail(replaceError, out json, out headers, out error);

                builtJson = parsed.ToJsonString();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                return Fail(InvalidTemplate, out json, out headers, out error);
            }
        }

        if (Utf8.GetByteCount(builtJson) > MaximumBodyBytes)
            return Fail(PayloadTooLarge, out json, out headers, out error);

        if (!TryBuildHeaders(target, payload, redactSecrets, out var builtHeaders, out error))
        {
            var headerError = error;
            return Fail(headerError, out json, out headers, out error);
        }

        json = builtJson;
        headers = builtHeaders;
        return true;
    }

    public static string GetConfigurationError(RelayDeliveryTarget target)
    {
        if (!target.IsJsonWebhook) return string.Empty;

        var samplePayload = new WebhookPayload(
            "relay.test",
            "delivery-test",
            new RelayNotification(1, "Sample app", "Sample title", "Sample body", DateTimeOffset.UnixEpoch)
            {
                PackageName = "sample.package"
            });
        return TryBuild(target, samplePayload, redactSecrets: false, out _, out _, out var error)
            ? string.Empty
            : error;
    }

    public static bool IsValidSecretName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64 || !IsAsciiLetter(name[0]))
            return false;

        for (var index = 1; index < name.Length; index++)
        {
            var character = name[index];
            if (!IsAsciiLetter(character) && !IsAsciiDigit(character) && character is not ('_' or '-'))
                return false;
        }

        return true;
    }

    private static string ReplaceStringValues(JsonNode? node, RelayDeliveryTarget target, WebhookPayload payload, bool redactSecrets)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject.ToArray())
            {
                var child = property.Value;
                string error;
                if (child is JsonValue value && value.TryGetValue<string>(out var text) && text is not null)
                {
                    var resolved = ResolveTokens(text, target, payload, redactSecrets, out error);
                    if (error.Length == 0 && !string.Equals(text, resolved, StringComparison.Ordinal))
                        jsonObject[property.Key] = JsonValue.Create(resolved);
                }
                else
                {
                    error = ReplaceStringValues(child, target, payload, redactSecrets);
                }
                if (error.Length != 0) return error;
            }
            return string.Empty;
        }

        if (node is JsonArray jsonArray)
        {
            for (var index = 0; index < jsonArray.Count; index++)
            {
                var item = jsonArray[index];
                string error;
                if (item is JsonValue value && value.TryGetValue<string>(out var text) && text is not null)
                {
                    var resolved = ResolveTokens(text, target, payload, redactSecrets, out error);
                    if (error.Length == 0 && !string.Equals(text, resolved, StringComparison.Ordinal))
                        jsonArray[index] = JsonValue.Create(resolved);
                }
                else
                {
                    error = ReplaceStringValues(item, target, payload, redactSecrets);
                }
                if (error.Length != 0) return error;
            }
            return string.Empty;
        }

        return string.Empty;
    }

    private static string ResolveTokens(string value, RelayDeliveryTarget target, WebhookPayload payload, bool redactSecrets, out string error)
    {
        var builder = new StringBuilder(value.Length);
        error = string.Empty;
        for (var index = 0; index < value.Length;)
        {
            if (value[index] == '}')
            {
                error = UnknownVariable;
                return string.Empty;
            }

            if (value[index] != '{')
            {
                builder.Append(value[index]);
                index++;
                continue;
            }

            var closingBrace = value.IndexOf('}', index + 1);
            var nestedBrace = value.IndexOf('{', index + 1);
            if (closingBrace < 0 || (nestedBrace >= 0 && nestedBrace < closingBrace))
            {
                error = UnknownVariable;
                return string.Empty;
            }

            var token = value[(index + 1)..closingBrace];
            if (!TryResolveToken(token, target, payload, redactSecrets, out var replacement, out error))
                return string.Empty;
            builder.Append(replacement);
            index = closingBrace + 1;
        }

        return builder.ToString();
    }

    private static bool TryResolveToken(
        string token,
        RelayDeliveryTarget target,
        WebhookPayload payload,
        bool redactSecrets,
        out string replacement,
        out string error)
    {
        error = string.Empty;
        var notification = payload.Notification;
        if (token.Equals("app", StringComparison.OrdinalIgnoreCase))
        {
            replacement = notification.App ?? string.Empty;
            return true;
        }
        if (token.Equals("title", StringComparison.OrdinalIgnoreCase))
        {
            replacement = notification.Title ?? string.Empty;
            return true;
        }
        if (token.Equals("body", StringComparison.OrdinalIgnoreCase) || token.Equals("content", StringComparison.OrdinalIgnoreCase))
        {
            replacement = notification.Body ?? string.Empty;
            return true;
        }
        if (token.Equals("PackageName", StringComparison.OrdinalIgnoreCase))
        {
            replacement = notification.PackageName ?? string.Empty;
            return true;
        }
        if (token.Equals("id", StringComparison.OrdinalIgnoreCase))
        {
            replacement = notification.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        if (token.Equals("eventType", StringComparison.OrdinalIgnoreCase))
        {
            replacement = payload.EventType ?? string.Empty;
            return true;
        }
        if (token.Equals("createdAt", StringComparison.OrdinalIgnoreCase))
        {
            replacement = notification.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        if (token.Equals("deliveryId", StringComparison.OrdinalIgnoreCase))
        {
            replacement = payload.DeliveryId ?? string.Empty;
            return true;
        }

        if (token.StartsWith("secret:", StringComparison.OrdinalIgnoreCase))
        {
            var secretName = token[7..];
            if (!IsValidSecretName(secretName))
            {
                replacement = string.Empty;
                error = UnknownVariable;
                return false;
            }

            if (target.WebhookSecrets is not null)
            {
                foreach (var secret in target.WebhookSecrets)
                {
                    if (!string.Equals(secret.Key, secretName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrEmpty(secret.Value))
                    {
                        replacement = string.Empty;
                        error = MissingSecret;
                        return false;
                    }
                    replacement = redactSecrets ? "***" : secret.Value;
                    return true;
                }
            }

            replacement = string.Empty;
            error = MissingSecret;
            return false;
        }

        replacement = string.Empty;
        error = UnknownVariable;
        return false;
    }

    private static bool TryBuildHeaders(
        RelayDeliveryTarget target,
        WebhookPayload payload,
        bool redactSecrets,
        out IReadOnlyDictionary<string, string> headers,
        out string error)
    {
        headers = EmptyHeaders();
        error = string.Empty;
        var parsedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rawHeaders = target.WebhookHeaders ?? string.Empty;

        // The built-in authorization field is part of the same request. Invalid
        // values must fail before request construction, not crash the queue worker.
        if (!string.IsNullOrWhiteSpace(target.BearerToken))
        {
            if (target.BearerToken.Length > MaximumHeaderValueLength || !IsPrintableAscii(target.BearerToken))
                return Fail(InvalidHeaders, out headers, out error);
            try { _ = new AuthenticationHeaderValue("Bearer", target.BearerToken); }
            catch (FormatException) { return Fail(InvalidHeaders, out headers, out error); }
        }

        var lines = rawHeaders.Split('\n');
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (lineIndex < lines.Length - 1 && line.EndsWith('\r'))
                line = line[..^1];
            if (line.Length == 0 || line.All(character => character == ' ')) continue;

            var separator = line.IndexOf(':');
            if (separator <= 0) return Fail(InvalidHeaders, out headers, out error);
            var name = line[..separator];
            if (!IsValidHeaderName(name) || IsForbiddenHeader(name) || parsedHeaders.ContainsKey(name))
                return Fail(InvalidHeaders, out headers, out error);

            var rawValue = line[(separator + 1)..].Trim(' ');
            var actualValue = ResolveTokens(rawValue, target, payload, redactSecrets: false, out error);
            if (error.Length != 0)
            {
                var resolutionError = error;
                return Fail(resolutionError, out headers, out error);
            }
            if (actualValue.Length > MaximumHeaderValueLength || !IsPrintableAscii(actualValue))
                return Fail(InvalidHeaders, out headers, out error);

            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(target.BearerToken))
                return Fail(AuthorizationConflict, out headers, out error);

            try
            {
                using var validationRequest = new HttpRequestMessage();
                validationRequest.Headers.Add(name, actualValue);
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
            {
                return Fail(InvalidHeaders, out headers, out error);
            }

            parsedHeaders.Add(name, redactSecrets ? "***" : actualValue);
            if (parsedHeaders.Count > MaximumHeaderCount)
                return Fail(InvalidHeaders, out headers, out error);
        }

        headers = new ReadOnlyDictionary<string, string>(parsedHeaders);
        return true;
    }

    private static bool IsValidHeaderName(string name)
    {
        if (name.Length == 0) return false;
        foreach (var character in name)
        {
            if (!IsAsciiLetter(character) && !IsAsciiDigit(character) && character is not ('!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~'))
                return false;
        }
        return true;
    }

    private static bool IsForbiddenHeader(string name) =>
        name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Expect", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Trailer", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("X-WinToastRelay-Delivery", StringComparison.OrdinalIgnoreCase);

    private static bool IsPrintableAscii(string value)
    {
        foreach (var character in value)
        {
            if (character is < ' ' or > '~') return false;
        }
        return true;
    }

    private static bool IsAsciiLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';

    private static IReadOnlyDictionary<string, string> EmptyHeaders() =>
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static bool Fail(string message, out string json, out IReadOnlyDictionary<string, string> headers, out string error)
    {
        json = string.Empty;
        headers = EmptyHeaders();
        error = message;
        return false;
    }

    private static bool Fail(string message, out IReadOnlyDictionary<string, string> headers, out string error)
    {
        headers = EmptyHeaders();
        error = message;
        return false;
    }
}
