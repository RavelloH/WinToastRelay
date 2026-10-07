# WinToastRelay Privacy Policy

**Effective date:** October 7, 2026

WinToastRelay is an open-source Windows application maintained by RavelloH. It listens for Windows notifications on the local device and forwards selected notifications to a destination configured by the user.

## Information processed

When notification access is granted, WinToastRelay can process the information exposed by a Windows notification, including:

- the source application name and icon, and its Windows package name when available;
- the notification title and body;
- the Windows notification identifier and event type; and
- the notification creation time.

Notification content may contain personal or sensitive information placed there by another application or by the user. WinToastRelay does not infer additional personal information from the notification.

## Where information is sent

WinToastRelay sends notification data only to the Bark server, WxPusher service, Feishu bot, Telegram Bot API, Discord webhook, or JSON Webhook endpoint selected and configured by the user. The destination may be operated by the user or by a third party. The destination's own privacy policy and retention practices apply to data it receives.

WinToastRelay does not send notification data to RavelloH, Microsoft, or an official WinToastRelay cloud service. There is no WinToastRelay-hosted relay server, analytics service, advertising SDK, or telemetry service.

## Credentials and local data

The Bark device key, WxPusher AppToken, Feishu signing secret, Telegram Bot token, optional webhook bearer token, and named webhook secret variables are stored using Windows Credential Manager. Other application settings, including destination URLs, recipient identifiers, custom JSON templates, custom header definitions, delivery queue data, and the local delivery history are stored in the app's Windows local application data folder. These files remain on the device and are not uploaded by WinToastRelay except when their configured delivery operation requires it.

Custom webhook templates and header values may reference notification fields and named secrets. Referenced values are included only in delivery requests to the configured destination; secret values are not stored in the delivery queue or history. The preview uses synthetic notification data and masks referenced secrets and custom header values. Text entered literally in a template or header definition is stored as an ordinary setting, so sensitive values should be entered as secret variables rather than pasted into templates or headers.

The local delivery history is retained for the recent-history period shown by the application. Pending deliveries may remain in the local queue until they are delivered or marked as failed. You can remove the application or its local data using Windows settings.

## Permissions and control

WinToastRelay requests notification access through Windows' `UserNotificationListener` API. You can deny or revoke this access in Windows settings. You can also stop forwarding, change the destination, configure application filters, or close the application at any time.

The application does not execute user-provided code or access user-selected files, arbitrary filesystem locations, cameras, microphones, contacts, precise location, or other unrelated device data. It does use its Windows local application data folder for settings, delivery queue data, dead-letter data, and local delivery history.

## Security

Delivery requests use the URL and transport configured by the user. HTTPS is required by default, except for HTTP loopback endpoints used for local development and testing. You may explicitly enable unencrypted HTTP for the current delivery channel and exact destination URL, including a local network or self-hosted service. Changing that channel's destination URL revokes its HTTP approval; other channels do not inherit it. HTTP does not encrypt notification content or credentials in transit and should only be used with trusted endpoints and networks. HTTPS certificate validation is not bypassed.

Delivery POST requests do not automatically follow redirects. If an endpoint responds with a redirect, configure its final URL and, if applicable, approve HTTP for that URL before retrying. A configured Bark server, WxPusher service, Feishu bot, Telegram bot, Discord webhook, or JSON webhook endpoint should be treated as a trusted recipient because notification content is sent to it directly.

## Children's privacy

WinToastRelay is a general-purpose utility and is not directed to children. We do not knowingly collect personal information from children through a WinToastRelay service.

## Changes to this policy

This policy may be updated when WinToastRelay's data handling changes. The effective date at the top of this document will be updated with each revision.

## Contact

For privacy questions or requests, open an issue in the public repository:

<https://github.com/RavelloH/WinToastRelay/issues>
