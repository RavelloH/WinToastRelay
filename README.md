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

WinToastRelay uses `Windows.UI.Notifications.Management.UserNotificationListener` and its `NotificationChanged` event. It does not use a timer or polling loop and only subscribes to notification events to ensure high performance. The notification center is enumerated once at startup to establish a baseline; each subsequent event is then handled individually by its notification ID.

## Features

* WinUI 3 / Windows App SDK visual language with Mica and native Windows controls.
* Chinese and English UI, switchable from Settings.
* Bark delivery is the default, using JSON POST with configurable templates and arbitrary Bark parameters.
* WxPusher standard push supports UID and Topic recipients with configurable summary and content templates.
* HTTPS JSON webhook delivery remains available with an optional Bearer token (loopback HTTP is allowed for local development).
* WxPusher AppToken and webhook Bearer token stored in Windows Credential Manager, not in the JSON settings file.
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

The project is intentionally packaged because the Windows notification listener requires an interactive packaged identity. On first use, click **Start listening** and approve notification access when Windows prompts you.

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

### Bark (default)

Enter a Bark server URL and device key. WinToastRelay sends a JSON POST request to the server's `/push` endpoint:

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

The AppToken is stored in Windows Credential Manager. A request is considered delivered only when the HTTP response succeeds and the WxPusher business response code is `1000`. Configuration follows the documented limits of 2,000 UIDs or five Topic IDs per request. See the [WxPusher standard push API documentation](https://wxpusher.zjiecode.com/docs/api-reference.html) for application and recipient setup.

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

The `X-WinToastRelay-Delivery` header contains the same delivery ID, making receiver-side deduplication straightforward. Transient failures (timeouts, 429 responses, 5xx responses, and temporary WxPusher business errors) are persisted locally and retried with exponential backoff. Other failed responses are persisted as dead letters and reported in the activity view for the active session.

## Privacy

Delivery sends the visible notification text, source application display name, and creation time to the configured Bark, WxPusher, or JSON webhook destination. Review the destination's data retention and access policies before relaying sensitive notifications. The WxPusher AppToken and optional webhook Bearer token are stored only in Windows Credential Manager.

## License

MIT
