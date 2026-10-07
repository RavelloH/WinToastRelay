using Windows.Security.Credentials;
using System.Text.Json;
using WinToastRelay.Models;

namespace WinToastRelay.Services;

/// <summary>Stores delivery credentials in Windows Credential Manager, never in the JSON settings file.</summary>
public sealed class SecretStore
{
    private const string ResourceName = "WinToastRelay.Webhook";
    private const string WebhookUserName = "Authorization";
    private const string BarkDeviceKeyUserName = "BarkDeviceKey";
    private const string WxPusherUserName = "WxPusherAppToken";
    private const string FeishuUserName = "FeishuSecret";
    private const string TelegramUserName = "TelegramBotToken";
    private const string WebhookSecretsUserName = "WebhookSecrets";

    public Dictionary<string, string> GetWebhookSecrets()
    {
        var json = Get(WebhookSecretsUserName);
        if (string.IsNullOrEmpty(json)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var values = JsonSerializer.Deserialize(json, AppJsonContext.Default.DictionaryStringString);
            return new(values ?? new(), StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException) { return new(StringComparer.OrdinalIgnoreCase); }
        catch (ArgumentException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    public void SaveWebhookSecrets(IReadOnlyDictionary<string, string> secrets)
    {
        // One vault entry keeps arbitrary template credentials out of ordinary
        // settings, delivery queues, and history, without creating an entry per key.
        var values = new Dictionary<string, string>(secrets, StringComparer.OrdinalIgnoreCase);
        if (values.Count > 32 || values.Any(value => !GenericWebhookTemplate.IsValidSecretName(value.Key) || string.IsNullOrEmpty(value.Value)))
            throw new ArgumentException("Invalid webhook secret collection.", nameof(secrets));
        var previous = Get(WebhookSecretsUserName);
        var replacement = values.Count == 0 ? string.Empty : JsonSerializer.Serialize(values, AppJsonContext.Default.DictionaryStringString);
        if (string.Equals(previous, replacement, StringComparison.Ordinal)) return;
        try { Save(WebhookSecretsUserName, replacement); }
        catch
        {
            // Vault writes can fail (for example because of storage limits).
            // Best-effort restoration avoids discarding a previously working collection.
            if (!string.IsNullOrEmpty(previous))
            {
                try { Save(WebhookSecretsUserName, previous); }
                catch { /* Preserve the original write error without exposing secret data. */ }
            }
            throw;
        }
    }

    public string Get() => Get(WebhookUserName);

    public string GetBarkDeviceKey() => Get(BarkDeviceKeyUserName);

    public string GetWxPusherAppToken() => Get(WxPusherUserName);

    public void Save(string secret) => Save(WebhookUserName, secret);

    public void SaveBarkDeviceKey(string secret) => Save(BarkDeviceKeyUserName, secret);

    public void SaveWxPusherAppToken(string secret) => Save(WxPusherUserName, secret);

    public string GetFeishuSecret() => Get(FeishuUserName);

    public void SaveFeishuSecret(string secret) => Save(FeishuUserName, secret);

    public string GetTelegramBotToken() => Get(TelegramUserName);

    public void SaveTelegramBotToken(string secret) => Save(TelegramUserName, secret);

    private static string Get(string userName)
    {
        try
        {
            var credential = new PasswordVault().Retrieve(ResourceName, userName);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static void Save(string userName, string secret)
    {
        var vault = new PasswordVault();
        try { vault.Remove(vault.Retrieve(ResourceName, userName)); }
        catch (Exception) { }

        if (!string.IsNullOrWhiteSpace(secret)) vault.Add(new PasswordCredential(ResourceName, userName, secret));
    }
}
