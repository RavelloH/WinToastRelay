using System.Collections.Concurrent;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using WinToastRelay.Models;
using Microsoft.UI.Xaml.Media.Imaging;

namespace WinToastRelay.Services;

/// <summary>
/// Event-driven bridge from Windows notifications to a delivery destination. It enumerates the
/// notification center only at startup and in response to NotificationChanged events.
/// </summary>
public sealed class NotificationRelayService
{
    // Resolve WinRT lazily so initialization failures can be reported instead of
    // escaping App's static initialization before the window exists.
    private UserNotificationListener? _listener;
    private readonly WebhookClient _webhookClient = new();
    private readonly DeliveryQueue _deliveryQueue = new();
    private readonly ConcurrentDictionary<uint, string> _knownNotifications = new();
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);
    private readonly RelayLifecycleCoordinator _lifecycle = new();
    private volatile bool _acceptNotifications;
    private bool _isSubscribed;
    private RelayDeliveryTarget _target = new(
        RelayDeliveryTarget.BarkMode, string.Empty, string.Empty, "https://api.day.app", string.Empty, "{app}: {title}", "{body}", "level=active");
    private HashSet<string> _allowedApps = new(StringComparer.OrdinalIgnoreCase);
    private bool _applicationFilterEnabled;

    public bool IsRunning => _lifecycle.IsRunning;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<ActivityEntry>? ActivityReceived;
    public event EventHandler<string>? ApplicationObserved;

    public NotificationRelayService()
    {
        _deliveryQueue.OutcomeReceived += (_, outcome) =>
        {
            var channel = _target.IsBark ? "Bark"
                : _target.IsWxPusher ? "WxPusher"
                : _target.IsFeishu ? "Feishu"
                : _target.IsTelegram ? "Telegram"
                : _target.IsDiscord ? "Discord"
                : "JSON Webhook";
            var parameters = _target.IsBark
                ? _target.BarkParameters.Replace("\r", " ").Replace("\n", "; ")
                : _target.IsWxPusher
                    ? $"UIDs: {(string.IsNullOrWhiteSpace(_target.WxPusherUids) ? "none" : "configured")}; topics: {(string.IsNullOrWhiteSpace(_target.WxPusherTopicIds) ? "none" : "configured")}; app token: configured"
                    : _target.IsFeishu
                        ? $"Webhook: configured; secret: {(string.IsNullOrWhiteSpace(_target.FeishuSecret) ? "none" : "configured")}"
                    : _target.IsTelegram
                        ? $"Chat ID: configured; bot token: configured; parse mode: {(string.IsNullOrWhiteSpace(_target.TelegramParseMode) ? "plain text" : _target.TelegramParseMode)}"
                    : _target.IsDiscord
                        ? $"Webhook: configured; username: {(string.IsNullOrWhiteSpace(_target.DiscordUsername) ? "default" : _target.DiscordUsername)}"
                    : $"Payload: {(string.IsNullOrWhiteSpace(_target.WebhookJsonTemplate) ? "default JSON" : "custom JSON")}; custom headers: {(string.IsNullOrWhiteSpace(_target.WebhookHeaders) ? "none" : "configured")}; bearer token: {(string.IsNullOrWhiteSpace(_target.BearerToken) ? "none" : "configured")}";
            ActivityReceived?.Invoke(this, new ActivityEntry(
                DateTimeOffset.Now,
                outcome.Notification.App,
                string.IsNullOrWhiteSpace(outcome.Notification.Title) ? outcome.Notification.Body : outcome.Notification.Title,
                outcome.Result.Succeeded,
                $"{(outcome.DeadLettered ? "Dead letter" : "Delivered")}: {outcome.Result.Detail} · Channel: {channel} · Attempts: {outcome.Attempts} · Queued at: {outcome.QueuedAt:O} · Completed at: {outcome.CompletedAt:O} · Notification time: {outcome.Notification.CreatedAt:O} · Delivery ID: {outcome.DeliveryId} · Parameters: {parameters}")
                { Body = outcome.Notification.Body });
        };
    }

    public void Configure(RelayDeliveryTarget target, string allowedApplications, bool applicationFilterEnabled = false)
    {
        _target = target;
        _deliveryQueue.Configure(_target);
        _allowedApps = (allowedApplications ?? string.Empty)
            .Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _applicationFilterEnabled = applicationFilterEnabled;
    }

    public Task<DeliveryResult> StartAsync() => _lifecycle.StartAsync(() => StartWithAccessAsync(requestAccess: true));

    public async Task<bool> TryStartIfAllowedAsync()
    {
        var result = await _lifecycle.StartAsync(() => StartWithAccessAsync(requestAccess: false));
        return result.Succeeded;
    }

    private UserNotificationListener GetListener() =>
        _listener ??= UserNotificationListener.Current ?? throw new InvalidOperationException("Notification listener is unavailable");

    private async Task<DeliveryResult> StartWithAccessAsync(bool requestAccess)
    {
        var stage = RelayFailureStage.RelayStart;
        try
        {
            var configurationError = WebhookClient.GetConfigurationError(_target);
            if (!string.IsNullOrEmpty(configurationError))
                return new DeliveryResult(false, configurationError);

            stage = RelayFailureStage.ListenerInitialization;
            var listener = GetListener();
            stage = RelayFailureStage.PermissionCheck;
            var access = listener.GetAccessStatus();
            if (requestAccess && access == UserNotificationListenerAccessStatus.Unspecified)
            {
                stage = RelayFailureStage.PermissionRequest;
                // Do not move this call to Task.Run: Windows requires the UI thread.
                access = await listener.RequestAccessAsync();
            }
            if (access != UserNotificationListenerAccessStatus.Allowed)
                return new DeliveryResult(false, $"Notification access: {access}");

            // Configuration/HTTP consent can change while the permission prompt is open.
            configurationError = WebhookClient.GetConfigurationError(_target);
            if (!string.IsNullOrEmpty(configurationError))
                return new DeliveryResult(false, configurationError);

            return await StartCoreAsync(listener);
        }
        catch (Exception ex)
        {
            return new DeliveryResult(false, RelayDiagnostics.Failure(stage, ex));
        }
    }

    public async Task<IReadOnlyList<(string Name, BitmapImage? Icon)>> GetAvailableApplicationsAsync()
    {
        var result = new List<(string, BitmapImage?)>();
        var stage = RelayFailureStage.ListenerInitialization;
        try
        {
            var listener = GetListener();
            stage = RelayFailureStage.PermissionCheck;
            if (listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed) return [];
            stage = RelayFailureStage.ApplicationEnumeration;
            var notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            if (notifications is null) throw new InvalidOperationException("Notification enumeration returned no collection");
            foreach (var notification in notifications)
            {
                // Metadata is optional, and one broken sender must not hide other apps.
                try
                {
                    var display = notification?.AppInfo?.DisplayInfo;
                    var name = display?.DisplayName;
                    if (string.IsNullOrWhiteSpace(name) || result.Any(x => string.Equals(x.Item1, name, StringComparison.OrdinalIgnoreCase))) continue;
                    BitmapImage? icon = null;
                    try
                    {
                        using var stream = await display!.GetLogo(new Windows.Foundation.Size(96, 96)).OpenReadAsync();
                        icon = new BitmapImage();
                        await icon.SetSourceAsync(stream);
                    }
                    catch { /* An unavailable logo uses the existing fallback icon. */ }
                    result.Add((name, icon));
                }
                catch (Exception ex) { PublishStatus(RelayDiagnostics.Failure(RelayFailureStage.ApplicationMetadata, ex)); }
            }
        }
        catch (Exception ex) { PublishStatus(RelayDiagnostics.Failure(stage, ex)); }
        return result.OrderBy(x => x.Item1, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public Task StopAsync() => _lifecycle.StopAsync(StopCoreAsync);

    private async Task StopCoreAsync()
    {
        var wasActive = _acceptNotifications || _isSubscribed || IsRunning;
        _acceptNotifications = false;
        string? failure = null;
        if (_isSubscribed && _listener is not null)
        {
            try
            {
                _listener.NotificationChanged -= ListenerOnNotificationChanged;
                _isSubscribed = false;
            }
            catch (Exception ex) { failure = RelayDiagnostics.Failure(RelayFailureStage.ListenerUnsubscription, ex); }
        }
        // Let the in-flight callback finish persisting before stopping the queue.
        // Stale callbacks check _acceptNotifications after taking this same lock.
        await _snapshotLock.WaitAsync();
        try
        {
            try { await _deliveryQueue.StopAsync(); }
            catch (Exception ex) { failure ??= RelayDiagnostics.Failure(RelayFailureStage.QueueShutdown, ex); }
        }
        finally { _snapshotLock.Release(); }
        if (failure is not null) throw new RelayDiagnosticException(failure);
        else if (wasActive) PublishStatus("Relay paused");
    }

    public Task<DeliveryResult> SendTestAsync()
    {
        var payload = new WebhookPayload(
            "relay.test",
            Guid.NewGuid().ToString("N"),
            new RelayNotification(0, "WinToastRelay", "Delivery test", "Your notification destination is working.", DateTimeOffset.UtcNow));
        return DeliverAsync(payload);
    }

    private async void ListenerOnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        try { await ProcessNotificationChangeAsync(sender, args); }
        catch (Exception ex) { PublishStatus(RelayDiagnostics.Failure(RelayFailureStage.NotificationEvent, ex)); }
    }

    private async Task<(int Skipped, string FirstFailure)> PrimeSnapshotAsync(UserNotificationListener listener)
    {
        await _snapshotLock.WaitAsync();
        try
        {
            var notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            if (notifications is null) throw new InvalidOperationException("Notification enumeration returned no collection");
            _knownNotifications.Clear();
            var observedApplications = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return NotificationContentParser.ReadBatch(notifications,
                notification => ToRelayNotification(notification, includePackageName: false), value =>
            {
                _knownNotifications[value.Id] = Fingerprint(value);
                if (observedApplications.Add(value.App)) ObserveApplication(value.App);
            });
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    private async Task ProcessNotificationChangeAsync(UserNotificationListener listener, UserNotificationChangedEventArgs args)
    {
        await _snapshotLock.WaitAsync();
        try
        {
            if (!_acceptNotifications || args is null) return;
            if (args.ChangeKind == UserNotificationChangedKind.Removed)
            {
                _knownNotifications.TryRemove(args.UserNotificationId, out _);
                return;
            }

            // GetNotification uses the ID supplied by the event. This is not polling.
            var notification = listener.GetNotification(args.UserNotificationId);
            if (notification is null) return;

            RelayNotification? relayNotification;
            try
            {
                relayNotification = ToRelayNotification(notification);
                if (relayNotification is null) throw new InvalidDataException("Notification has no readable text");
            }
            catch (Exception ex)
            {
                PublishStatus(RelayDiagnostics.Failure(RelayFailureStage.NotificationParsing, ex, args.UserNotificationId));
                return;
            }
            var fingerprint = Fingerprint(relayNotification);
            if (_knownNotifications.TryGetValue(relayNotification.Id, out var known) && known == fingerprint) return;

            _knownNotifications[relayNotification.Id] = fingerprint;
            ObserveApplication(relayNotification.App);
            if (_applicationFilterEnabled && !_allowedApps.Contains(relayNotification.App)) return;

            var eventType = args.ChangeKind == UserNotificationChangedKind.Added
                ? "notification.added"
                : "notification.changed";
            try { await _deliveryQueue.EnqueueAsync(new WebhookPayload(eventType, Guid.NewGuid().ToString("N"), relayNotification)); }
            catch (Exception ex) { PublishStatus(RelayDiagnostics.Failure(RelayFailureStage.QueueEnqueue, ex, relayNotification.Id)); }
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    private Task<DeliveryResult> DeliverAsync(WebhookPayload payload) => _webhookClient.DeliverAsync(_target, payload);

    private async Task<DeliveryResult> StartCoreAsync(UserNotificationListener listener)
    {
        var stage = RelayFailureStage.QueueStartup;
        try
        {
            await _deliveryQueue.StartAsync();
            stage = RelayFailureStage.ListenerSubscription;
            _acceptNotifications = true;
            if (!_isSubscribed)
            {
                listener.NotificationChanged += ListenerOnNotificationChanged;
                _isSubscribed = true;
            }
            // Subscribe before establishing the baseline so a notification arriving during
            // startup is still observed by the event-driven path.
            stage = RelayFailureStage.SnapshotEnumeration;
            var snapshot = await PrimeSnapshotAsync(listener);
            var detail = snapshot.Skipped > 0
                ? RelayDiagnostics.Warning(snapshot.Skipped, snapshot.FirstFailure)
                : "Notification access granted";
            PublishStatus(snapshot.Skipped > 0 ? detail : "Listening for Windows notifications");
            return new DeliveryResult(true, detail);
        }
        catch (Exception ex)
        {
            var failure = RelayDiagnostics.Failure(stage, ex);
            try { await StopCoreAsync(); }
            catch (Exception cleanupError) { PublishStatus(RelayDiagnostics.Failure(RelayFailureStage.StartupCleanup, cleanupError)); }
            // Cleanup failures must never hide the original startup failure.
            return new DeliveryResult(false, failure);
        }
    }

    private static RelayNotification? ToRelayNotification(UserNotification? notification, bool includePackageName = true)
    {
        if (notification is null) return null;
        var appInfo = notification.AppInfo;
        var app = appInfo?.DisplayInfo?.DisplayName;
        var binding = notification.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
        var text = binding?.GetTextElements()?.Select(element => element?.Text);
        var packageName = string.Empty;
        // AppInfo.Package was added in Windows 10 2004. Older Windows and
        // unpackaged notification senders may not expose a package at all.
        if (includePackageName && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) &&
            Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent("Windows.ApplicationModel.AppInfo", "Package"))
        {
            try { packageName = appInfo?.Package?.Id?.Name ?? string.Empty; }
            catch (Exception) { /* Optional metadata must never block notification delivery. */ }
        }
        return NotificationContentParser.Create(notification.Id, app, text, notification.CreationTime, packageName);
    }

    private static string Fingerprint(RelayNotification value)
    {
        return $"{value.App}\u001f{value.Title}\u001f{value.Body}\u001f{value.CreatedAt.UtcTicks}";
    }

    private void ObserveApplication(string application)
    {
        if (!string.IsNullOrWhiteSpace(application))
            ApplicationObserved?.Invoke(this, application);
    }

    private void PublishStatus(string message) => StatusChanged?.Invoke(this, message);
}
