# WinToastRelay

<!-- auto-readme-i18n-switcher start -->
| English | [中文](/.github/readme/README.zh.md) |
<!-- auto-readme-i18n-switcher end -->

> A native Windows notification bridge that forwards toast notifications to Bark or JSON webhooks in real time.

WinToastRelay uses `Windows.UI.Notifications.Management.UserNotificationListener` and its `NotificationChanged` event. It does not use timer-based polling. The notification center is enumerated once at startup to establish a baseline; later changes are handled by notification ID.

## Features

- WinUI 3 / Windows App SDK visual language with Mica and native Windows controls.
- Chinese and English interface, switchable from Settings.
- Bark JSON POST delivery with customizable title, body, and arbitrary Bark parameters.
- Generic HTTPS JSON webhook delivery with an optional Bearer token.
- Application allow-list filters.
- Durable local delivery queue with exponential backoff for transient failures.
- Recent delivery history with status and HTTP response details.
- System-tray operation; closing the window keeps the relay running.
- Optional Windows sign-in startup through the packaged startup-task API.
- Automatic listener startup after a valid destination is configured.
- No official WinToastRelay relay server, analytics, advertising SDK, or AI-model service.

## Requirements

- Windows 10 version 1809 or later; Windows 11 is recommended.
- .NET 9 SDK for building from source.
- A Windows App SDK-compatible development environment.

## Build and test

```powershell
dotnet restore .\WinToastRelay.csproj -r win-x64
dotnet build .\WinToastRelay.csproj -r win-x64 -p:Platform=x64
dotnet test .\tests\WinToastRelay.Tests\WinToastRelay.Tests.csproj
```

The app is intentionally packaged because notification access requires an interactive package identity. On first use, grant notification access when Windows asks.

## Microsoft Store submission

The reserved Store package identity is:

```text
Name:      RavelloH.WinToastRelay
Publisher: CN=184C7048-0661-4259-8EE3-39EFE462DFBE
Store ID:  9MV8SL6JLV2D
```

Microsoft Store re-signs the submitted package. The local upload package only needs a temporary certificate whose subject matches the reserved Publisher:

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

Upload the resulting `.msixbundle` from `AppPackages` to the Partner Center submission. Do not upload the `.cer`; Microsoft Store replaces the package signature. The package requests `runFullTrust`, which is required by the packaged WinUI 3 desktop executable and startup-task integration and may require Partner Center approval.

## Local sideloading

For local testing, use the `.msix` and matching `.cer` from the same package output directory. Import the certificate into **Local Computer → Trusted People** before installing. If Windows reports `0x800B0109` or `0x87e80034`, verify that the certificate and package are from the same build.

Never publish the temporary PFX as a public signing identity.

## Delivery modes

### Bark (default)

Enter a Bark server URL and device key. WinToastRelay sends a JSON POST request to the server's `/push` endpoint:

```json
{
  "device_key": "your-device-key",
  "title": "Mail: Build passed",
  "body": "Notification body",
  "sound": "bell",
  "group": "work"
}
```

Title and body templates support `{app}`, `{title}`, `{body}`, `{id}`, `{eventType}`, and `{createdAt}`. Add arbitrary Bark parameters as `key=value`, one per line, in Settings; they are sent as JSON fields. Long titles and bodies are kept out of the URL.

### Generic JSON webhook

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

The `X-WinToastRelay-Delivery` header contains the same delivery ID for receiver-side deduplication. Timeouts, 429 responses, and 5xx responses are persisted and retried with exponential backoff. Other failed responses are recorded as dead letters.

## Privacy

WinToastRelay processes the visible notification application name, icon, title, body, identifier, event type, and creation time. It sends this data only to the Bark or JSON Webhook endpoint configured by the user. It does not send notification data to RavelloH or an official WinToastRelay cloud service.

The optional Bearer token is stored in Windows Credential Manager. Other settings, the delivery queue, and recent history remain in the Windows local application data folder.

Read the complete [Privacy Policy](/PRIVACY.md).

## Project status

WinToastRelay is an open-source Windows application prepared for Microsoft Store distribution. Contributions and issue reports are welcome.

## License

[MIT](/LICENSE) Copyright 2026 RavelloH
