# WinToastRelay

WinToastRelay is a native Windows 10/11 app that relays Windows toast notifications to a configured destination in real time.

<a href="https://apps.microsoft.com/detail/9MV8SL6JLV2D">
  <img
    src="https://developer.microsoft.com/store/badges/images/English_get-it-from-MS.png"
    alt="Get it from Microsoft"
    width="180"
  />
</a>

-----

<img width="930" height="624" alt="Snipaste_2026-08-20_23-44-58" src="https://github.com/user-attachments/assets/df4edc40-4873-437d-89de-3ea87fabdd25" />
<img width="930" height="624" alt="Snipaste_2026-08-20_23-45-28" src="https://github.com/user-attachments/assets/730931f2-1d0f-4a5a-a59f-2e8f8b017a74" />
<img width="930" height="624" alt="Snipaste_2026-08-20_23-46-02" src="https://github.com/user-attachments/assets/a4be89b6-a134-492b-aeb4-3a7c51e1e28e" />
<img width="930" height="624" alt="Snipaste_2026-08-20_23-48-11" src="https://github.com/user-attachments/assets/4ddfe7ec-cc4f-44c0-9182-7ab0aea3c0a3" />
<img width="930" height="624" alt="Snipaste_2026-08-20_23-48-22" src="https://github.com/user-attachments/assets/5f3c7860-663e-4720-b29e-f2f62ad2b0d3" />

WinToastRelay uses `Windows.UI.Notifications.Management.UserNotificationListener` and its `NotificationChanged` event. It does not use a timer or polling loop. When the listener starts, the notification center is enumerated once to establish a baseline; each subsequent event is handled individually by its notification ID.

## Features

* WinUI 3 / Windows App SDK visual language with Mica and native Windows controls.
* Chinese and English UI, switchable from Settings.
* Bark delivery is the default, using JSON POST with configurable templates and arbitrary Bark parameters.
* WxPusher standard push supports UID and Topic recipients with configurable summary and content templates.
* Feishu custom bot, Telegram Bot API, and Discord Webhook delivery are supported with configurable templates.
* JSON webhook delivery supports an optional Bearer token, custom JSON payload templates, custom HTTP headers, and credential-backed secret variables. HTTPS is the default; loopback HTTP remains allowed, and other HTTP endpoints require explicit approval for the current channel and URL.
* Delivery credentials, including the Bark device key, WxPusher AppToken, Feishu signing secret, Telegram Bot token, and webhook Bearer token, are stored in Windows Credential Manager, not in the JSON settings file.
* Application allow-list filtering.
* Delivery activity history with status and HTTP response details.
* Durable local delivery queue with exponential backoff for transient HTTP failures.
* System-tray operation: closing the window keeps the relay running; the tray menu can reopen the window or exit the app.
* Optional startup at Windows sign-in using the packaged startup-task API.
* Automatic event listener startup after a valid delivery destination is configured; no manual start button is required.
* Application filters presented as per-app switches for notification sources already observed by Windows.
* Single-project MSIX packaging with package identity; notification access is explicitly requested through `RequestAccessAsync`.

## Build

Requirements:

* Windows 10 1809 or later (Windows 11 recommended).
* .NET 9 SDK (`global.json` pins SDK 9.0.205).
* A Windows App SDK-compatible development environment.

```powershell
dotnet restore .\WinToastRelay.csproj -r win-x64
dotnet build .\WinToastRelay.csproj -r win-x64 -p:Platform=x64
dotnet test .\tests\WinToastRelay.Tests\WinToastRelay.Tests.csproj
```

The project is intentionally packaged because the Windows notification listener requires an interactive packaged identity. On first use, configure a valid delivery destination and approve notification access when Windows prompts you. Listening starts automatically after configuration.

## Development MSIX

Create a local code-signing certificate once. The generated files are ignored by Git and will not be committed to the repository.

The package requests one capability: `runFullTrust`. This capability is required for the packaged WinUI 3 desktop executable and its optional Windows startup task; it does not grant notification-listener access. Access to Windows notifications is requested separately at runtime through `UserNotificationListener.RequestAccessAsync`, and the user may deny or later revoke that permission.

Create a temporary certificate whose Subject matches the reserved Store Publisher and use it only during packaging:

```powershell
.\scripts\New-DevCertificate.ps1 `
  -Subject "CN=184C7048-0661-4259-8EE3-39EFE462DFBE" `
  -OutputName "WinToastRelay-store-upload" `
  -Password "choose-a-temporary-password" `
  -ValidYears 1
dotnet publish .\WinToastRelay.csproj -r win-x64 -p:Platform=x64 -p:Configuration=Release `
  -p:GenerateAppxPackageOnBuild=true -p:AppxBundle=Always -p:AppxBundlePlatforms=x64 `
  -p:PackageCertificateKeyFile="$PWD\certs\WinToastRelay-store-upload.pfx" `
  -p:PackageCertificatePassword="choose-a-temporary-password"
```

Upload the resulting `.msixbundle` from the `AppPackages` directory under **Manage packages** in Partner Center. Do not upload the `.cer`; Microsoft Store replaces the package signature after the submission is accepted.

For local sideloading, run the generated `Add-AppDevPackage.ps1` from the package output directory and allow it to install the matching `.cer` into the **Local Computer → Trusted People** certificate store. You can also import the `.cer` manually with administrator approval. The temporary certificate includes the non-CA Basic Constraints extension required for MSIX sideloading. Do not publish the temporary PFX or CER as a public signing identity; Microsoft Store replaces the signature for Store distribution.

### Certificate troubleshooting

Use the `.msix` and `.cer` from the same output directory. If Windows reports `0x800B0109` or `0x87e80034` during local sideloading, import the matching `.cer` into **Local Computer → Trusted People**, then install the corresponding `.msix`.

## Delivery modes

### HTTP and self-hosted destinations

For a trusted HTTP service such as `http://192.168.1.50:8123/api/webhook/test_relay` or `http://homeassistant.local:8123/api/webhook/test_relay`, enter the endpoint in **Destination**, enable **Allow unencrypted HTTP**, then save or send a test. IPv4, IPv6, hostnames, and custom ports are supported without a private-address allow-list or DNS probing. The switch grants permission only for that channel's exact configured URL; changing the URL revokes approval, and switching channels does not transfer permission. HTTPS and loopback HTTP do not require this switch.

HTTP sends notification content and credentials without encryption. Use it only with trusted endpoints and networks; HTTPS certificate validation remains enabled. Test delivery and queued background delivery enforce the same policy. Delivery POST requests do not follow redirects automatically: configure the final destination URL instead, and approve it separately if it uses HTTP.

### Bark (default)

Enter a Bark server URL and device key. The device key is stored in Windows Credential Manager rather than the JSON settings file. WinToastRelay sends a JSON POST request to the server's `/push` endpoint:

```json
{
  "device_key": "your-device-key",
  "title": "Example app: A title",
  "body": "Notification body",
  "sound": "bell",
  "group": "work"
}
```

Title and body templates support `{app}`, `{title}`, `{body}`, `{id}`, `{eventType}`, and `{createdAt}`. You can add arbitrary Bark parameters in the app settings, one `key=value` entry per line. Oversized payload text is truncated before delivery to stay within the configured payload limit.

### WxPusher

Create an application in the WxPusher console, then enter its AppToken and at least one recipient UID or numeric Topic ID. UIDs and Topic IDs can be separated by new lines, commas, or semicolons. WinToastRelay sends plain-text messages through `POST https://wxpusher.zjiecode.com/api/send/message`; summary and content templates support the same variables as Bark.

The AppToken is stored in Windows Credential Manager. A request is considered delivered only when the HTTP response succeeds, the top-level WxPusher business response code is `1000`, and every recipient result is successful. Configuration supports up to 2,000 UIDs and 5 Topic IDs per request; both recipient types may be used together. See the [WxPusher standard push API documentation](https://wxpusher.zjiecode.com/docs/api-reference.html) for application and recipient setup.

### Feishu

Create a custom bot in a Feishu group and paste its webhook URL. WinToastRelay sends a text message through the Feishu bot API. If signature verification is enabled, enter the bot secret; the request includes the standard `timestamp` and `sign` fields. The title and body templates support `{app}`, `{title}`, `{body}`, `{id}`, `{eventType}`, and `{createdAt}`.

### Telegram

Create a bot with BotFather, then enter its Bot token and destination Chat ID. WinToastRelay calls the Telegram Bot API `sendMessage` method and supports optional `HTML` or `MarkdownV2` parse modes, as well as a custom Bot API base URL for self-hosted deployments.

### Discord

Create a Discord channel webhook and paste its URL. WinToastRelay sends the rendered title and body as a webhook message, with unsolicited mentions disabled. An optional display name can be configured for the webhook message.

### Generic JSON webhook

Leave the JSON template blank to keep the original payload below. Existing configurations continue to use this format:

```json
{
  "eventType": "notification.added",
  "deliveryId": "a-generated-id",
  "notification": {
    "id": 123,
    "app": "Example app",
    "title": "A title",
    "body": "Notification body",
    "createdAt": "2026-08-20T00:00:00Z"
  }
}
```

The `X-WinToastRelay-Delivery` header contains the same delivery ID, making receiver-side deduplication straightforward. Transient failures (timeouts, 429 responses, 5xx responses, and temporary WxPusher business errors) are persisted locally and retried with exponential backoff. Other failed responses are persisted as dead letters and reported in the activity view for the active session.

#### Custom payloads and headers

On **Destination → Generic JSON webhook**, expand the JSON template card and enter a valid JSON object. Placeholders are replaced in string values only, including strings nested in objects or arrays. Object keys and JSON numbers, booleans, and null values are not changed. Notification text is serialized safely: quotes, backslashes, newlines, and Unicode do not break the JSON structure. Substituted text is not interpreted as another template or executable code.

Supported placeholders are case-insensitive:

| Placeholder | Value |
| --- | --- |
| `{app}` | Source application's display name |
| `{title}` | Notification title |
| `{body}` or `{Content}` | Notification body |
| `{PackageName}` | Windows package name, when available; otherwise empty |
| `{id}` | Windows notification ID, as a string |
| `{eventType}` | Event type, such as `notification.added` or `relay.test` |
| `{createdAt}` | Notification creation time in ISO 8601 format |
| `{deliveryId}` | Delivery ID |
| `{secret:name}` | A named secret from Windows Credential Manager |

Unrecognized placeholders and references to missing secrets are configuration errors. The template must be a JSON object, not a raw text body, array, or script. Placeholders must be inside JSON strings; write numeric values as JSON numbers when required by the receiving service.

Add sensitive values under **Secret variables**, then reference them as `{secret:name}` in the JSON template or a header value. Secret names start with an ASCII letter and contain only letters, digits, underscores, or hyphens (up to 64 characters). Names are case-insensitive. Secret values are stored only in Windows Credential Manager; removing a secret and saving removes it from the saved secret collection. Ordinary template text and header definitions are stored in the settings file, so do not paste credentials directly into those editors. Up to 32 secret variables are supported.

Custom headers use one `Name: Value` entry per line. For example:

```text
X-API-Key: {secret:api_key}
Authorization: Bearer {secret:access_token}
```

You may use the built-in Bearer token field **or** a custom `Authorization` header, but not both. Duplicate headers, newline/control characters, non-ASCII header values, and transport-managed headers (including `Host`, `Content-*`, `Connection`, `Transfer-Encoding`, `User-Agent`, and `X-WinToastRelay-Delivery`) are rejected. The body is always sent as UTF-8 `application/json` with POST. Up to 32 custom headers are supported, with at most 8,192 characters per rendered value.

**Preview** uses synthetic notification data and masks referenced secrets and custom header values. It does not send a request. **Send test** validates and sends the same configuration used by queued notifications. Generic webhook delivery considers HTTP 2xx successful; service-specific business response fields are not interpreted. JSON templates are limited to 65,536 characters and rendered bodies to 1 MiB; oversized generic bodies are rejected rather than silently truncating arbitrary fields.

For [Pushover's JSON API](https://pushover.net/api), set the endpoint to `https://api.pushover.net/1/messages.json`, create secrets named `pushover_token` and `pushover_user`, and use:

```json
{
  "token": "{secret:pushover_token}",
  "user": "{secret:pushover_user}",
  "title": "{title}",
  "message": "{body}\n\nSource: {app}"
}
```

For [ntfy JSON publishing](https://docs.ntfy.sh/publish/#publish-as-json), use the server's root URL (for example, `https://ntfy.sh/`, not a topic URL):

```json
{
  "topic": "your-topic",
  "title": "{title}",
  "message": "{body}"
}
```

If the service requires authentication, use its documented header format and a secret variable. The existing HTTPS/HTTP approval policy and redirect protection apply to both default and custom webhooks.

## Privacy

Delivery sends the visible notification text, source application display name, and creation time to the configured Bark, WxPusher, Feishu, Telegram, Discord, or JSON webhook destination. Custom JSON templates can also include the Windows package name when available. Review the destination's data retention and access policies before relaying sensitive notifications. Delivery credentials, including the Bark device key, WxPusher AppToken, Feishu signing secret, Telegram Bot token, optional webhook Bearer token, and named webhook secrets, are stored only in Windows Credential Manager. Literal template text and header definitions are ordinary settings; use secret variables for sensitive values.

## License

MIT
