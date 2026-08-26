using Windows.Security.Credentials;

namespace WinToastRelay.Services;

/// <summary>Stores delivery credentials in Windows Credential Manager, never in the JSON settings file.</summary>
public sealed class SecretStore
{
    private const string ResourceName = "WinToastRelay.Webhook";
    private const string WebhookUserName = "Authorization";
    private const string WxPusherUserName = "WxPusherAppToken";

    public string Get() => Get(WebhookUserName);

    public string GetWxPusherAppToken() => Get(WxPusherUserName);

    public void Save(string secret) => Save(WebhookUserName, secret);

    public void SaveWxPusherAppToken(string secret) => Save(WxPusherUserName, secret);

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
