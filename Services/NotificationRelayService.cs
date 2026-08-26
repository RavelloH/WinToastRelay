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
    private readonly UserNotificationListener _listener = UserNotificationListener.Current;
    private readonly WebhookClient _webhookClient = new();
    private readonly DeliveryQueue _deliveryQueue = new();
    private readonly ConcurrentDictionary<uint, string> _knownNotifications = new();
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);
    private bool _isRunning;
    private RelayDeliveryTarget _target = new(
        RelayDeliveryTarget.BarkMode, string.Empty, string.Empty, "https://api.day.app", string.Empty, "{app}: {title}", "{body}", "level=active");
    private HashSet<string> _allowedApps = new(StringComparer.OrdinalIgnoreCase);
    private bool _applicationFilterEnabled;

    public bool IsRunning => _isRunning;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<ActivityEntry>? ActivityReceived;
    public event EventHandler<string>? ApplicationObserved;

    public NotificationRelayService()
    {
        _deliveryQueue.OutcomeReceived += (_, outcome) =>
        {
            var channel = _target.IsBark ? "Bark" : _target.IsWxPusher ? "WxPusher" : "JSON Webhook";
            var parameters = _target.IsBark
                ? _target.BarkParameters.Replace("\r", " ").Replace("\n", "; ")
                : _target.IsWxPusher
                    ? $"UIDs: {(string.IsNullOrWhiteSpace(_target.WxPusherUids) ? "none" : "configured")}; topics: {(string.IsNullOrWhiteSpace(_target.WxPusherTopicIds) ? "none" : "configured")}; app token: configured"
                    : string.IsNullOrWhiteSpace(_target.BearerToken) ? "Bearer token: none" : "Bearer token: configured";
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
        _allowedApps = allowedApplications
            .Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _applicationFilterEnabled = applicationFilterEnabled;
    }

    public async Task<DeliveryResult> StartAsync()
    {
        if (_isRunning) return new DeliveryResult(true, "Already running");

        if (!WebhookClient.IsValidConfiguration(_target))
            return new DeliveryResult(false, _target.IsBark
                ? "Invalid Bark configuration"
                : _target.IsWxPusher ? "Invalid WxPusher configuration" : "Invalid webhook URL");

        var access = await _listener.RequestAccessAsync();
        if (access != UserNotificationListenerAccessStatus.Allowed)
            return new DeliveryResult(false, $"Notification access: {access}");

        return await StartCoreAsync();
    }

    public async Task<bool> TryStartIfAllowedAsync()
    {
        if (_isRunning || _listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
            return _isRunning;
        if (!WebhookClient.IsValidConfiguration(_target)) return false;
        var result = await StartCoreAsync();
        return result.Succeeded;
    }

    public async Task<IReadOnlyList<(string Name, BitmapImage? Icon)>> GetAvailableApplicationsAsync()
    {
        if (_listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
            return [];

        var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
        var result = new List<(string, BitmapImage?)>();
        foreach (var notification in notifications)
        {
            var display = notification.AppInfo.DisplayInfo;
            var name = display.DisplayName;
            if (string.IsNullOrWhiteSpace(name) || result.Any(x => string.Equals(x.Item1, name, StringComparison.OrdinalIgnoreCase))) continue;
            BitmapImage? icon = null;
            try
            {
                var stream = await display.GetLogo(new Windows.Foundation.Size(96, 96)).OpenReadAsync();
                icon = new BitmapImage();
                await icon.SetSourceAsync(stream);
            }
            catch { }
            result.Add((name, icon));
        }
        return result.OrderBy(x => x.Item1, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task StopAsync()
    {
        if (_isRunning)
        {
            _listener.NotificationChanged -= ListenerOnNotificationChanged;
            _isRunning = false;
            PublishStatus("Relay paused");
        }
        await _deliveryQueue.StopAsync();
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
        try { await ProcessNotificationChangeAsync(args); }
        catch (Exception ex) { PublishStatus($"Listener error: {ex.Message}"); }
    }

    private async Task PrimeSnapshotAsync()
    {
        await _snapshotLock.WaitAsync();
        try
        {
            var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            _knownNotifications.Clear();
            foreach (var notification in notifications)
            {
                _knownNotifications[notification.Id] = Fingerprint(notification);
                ObserveApplication(notification.AppInfo.DisplayInfo.DisplayName);
            }
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    private async Task ProcessNotificationChangeAsync(UserNotificationChangedEventArgs args)
    {
        await _snapshotLock.WaitAsync();
        try
        {
            if (args.ChangeKind == UserNotificationChangedKind.Removed)
            {
                _knownNotifications.TryRemove(args.UserNotificationId, out _);
                return;
            }

            // GetNotification uses the ID supplied by the event. This is not polling.
            var notification = _listener.GetNotification(args.UserNotificationId);
            if (notification is null) return;

            var fingerprint = Fingerprint(notification);
            if (_knownNotifications.TryGetValue(notification.Id, out var known) && known == fingerprint) return;

            _knownNotifications[notification.Id] = fingerprint;
            var relayNotification = ToRelayNotification(notification);
            ObserveApplication(relayNotification.App);
            if (_applicationFilterEnabled && !_allowedApps.Contains(relayNotification.App)) return;

            var eventType = args.ChangeKind == UserNotificationChangedKind.Added
                ? "notification.added"
                : "notification.changed";
            await _deliveryQueue.EnqueueAsync(new WebhookPayload(eventType, Guid.NewGuid().ToString("N"), relayNotification));
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    private Task<DeliveryResult> DeliverAsync(WebhookPayload payload) => _webhookClient.DeliverAsync(_target, payload);

    private async Task<DeliveryResult> StartCoreAsync()
    {
        try
        {
            await _deliveryQueue.StartAsync();
            _listener.NotificationChanged += ListenerOnNotificationChanged;
            // Subscribe before establishing the baseline so a notification arriving during
            // startup is still observed by the event-driven path.
            await PrimeSnapshotAsync();
            _isRunning = true;
            PublishStatus("Listening for Windows notifications");
            return new DeliveryResult(true, "Notification access granted");
        }
        catch (Exception ex)
        {
            _listener.NotificationChanged -= ListenerOnNotificationChanged;
            await _deliveryQueue.StopAsync();
            return new DeliveryResult(false, ex.Message);
        }
    }

    private static RelayNotification ToRelayNotification(UserNotification notification)
    {
        var app = notification.AppInfo.DisplayInfo.DisplayName;
        var binding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
        var parts = binding?.GetTextElements().Select(element => element.Text.Trim()).Where(text => text.Length > 0).ToArray() ?? [];
        return new RelayNotification(notification.Id, app, parts.ElementAtOrDefault(0) ?? string.Empty,
            string.Join(Environment.NewLine, parts.Skip(1)), notification.CreationTime);
    }

    private static string Fingerprint(UserNotification notification)
    {
        var value = ToRelayNotification(notification);
        return $"{value.App}\u001f{value.Title}\u001f{value.Body}\u001f{value.CreatedAt.UtcTicks}";
    }

    private void ObserveApplication(string application)
    {
        if (!string.IsNullOrWhiteSpace(application))
            ApplicationObserved?.Invoke(this, application);
    }

    private void PublishStatus(string message) => StatusChanged?.Invoke(this, message);
}
