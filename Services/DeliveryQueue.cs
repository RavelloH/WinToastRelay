using System.Text.Json;
using System.Threading.Channels;
using WinToastRelay.Models;

namespace WinToastRelay.Services;

/// <summary>
/// Durable, single-reader delivery queue. NotificationChanged is never held open
/// while an HTTP request is running; failed requests are retried with backoff and
/// remain as dead-letter records when a receiver rejects them permanently.
/// </summary>
public sealed class DeliveryQueue : IAsyncDisposable
{
    private const string QueueFileName = "delivery-queue.json";
    private const string DeadLetterFileName = "delivery-dead-letter.json";
    private const int MaxAttempts = 8;

    private readonly WebhookClient _client = new();
    private readonly Channel<bool> _wake = Channel.CreateUnbounded<bool>();
    private readonly object _gate = new();
    private CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly List<PendingDelivery> _items = new();
    private readonly List<DeadLetterDelivery> _deadLetters = new();
    private Task? _worker;
    private RelayDeliveryTarget _target = new(
        RelayDeliveryTarget.BarkMode, string.Empty, string.Empty, "https://api.day.app", string.Empty, "{app}: {title}", "{body}", "level=active");
    private bool _started;

    public event EventHandler<DeliveryOutcome>? OutcomeReceived;

    public void Configure(RelayDeliveryTarget target)
    {
        _target = target;
    }

    public async Task StartAsync()
    {
        if (_started) return;
        if (_shutdown.IsCancellationRequested)
        {
            _shutdown.Dispose();
            _shutdown = new CancellationTokenSource();
        }
        await LoadAsync();
        _started = true;
        _worker = Task.Run(() => WorkerAsync(_shutdown.Token));
        Signal();
    }

    public async Task EnqueueAsync(WebhookPayload payload)
    {
        lock (_gate)
        {
            _items.Add(new PendingDelivery
            {
                DeliveryId = payload.DeliveryId,
                Payload = payload,
                EnqueuedAt = DateTimeOffset.UtcNow,
                NextAttemptAt = DateTimeOffset.UtcNow
            });
        }

        await SaveAsync();
        Signal();
    }

    public async Task StopAsync()
    {
        if (!_started) return;
        _shutdown.Cancel();
        Signal();
        if (_worker is not null)
        {
            try { await _worker; }
            catch (OperationCanceledException) { }
        }
        await SaveAsync();
        _started = false;
        _worker = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _shutdown.Dispose();
        _fileLock.Dispose();
    }

    private async Task WorkerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var due = GetDueItems(DateTimeOffset.UtcNow);
            if (due.Count == 0)
            {
                await WaitForSignalAsync(GetNextDelay(), cancellationToken);
                continue;
            }

            foreach (var item in due)
            {
                if (cancellationToken.IsCancellationRequested) break;
                var result = await _client.DeliverAsync(_target, item.Payload, cancellationToken);
                var outcome = ApplyResult(item.DeliveryId, result);
                await SaveAsync();
                OutcomeReceived?.Invoke(this, outcome);
            }
        }
    }

    private DeliveryOutcome ApplyResult(string deliveryId, DeliveryResult result)
    {
        lock (_gate)
        {
            var item = _items.First(x => x.DeliveryId == deliveryId);
            var queuedAt = item.EnqueuedAt;
            var completedAt = DateTimeOffset.UtcNow;
            item.Attempts++;
            var deadLettered = !result.Succeeded && (!result.Retryable || item.Attempts >= MaxAttempts);

            if (result.Succeeded || deadLettered)
            {
                _items.Remove(item);
                if (deadLettered)
                {
                    _deadLetters.Insert(0, new DeadLetterDelivery
                    {
                        DeliveryId = item.DeliveryId,
                        Payload = item.Payload,
                        Attempts = item.Attempts,
                        FailedAt = DateTimeOffset.UtcNow,
                        Error = result.Detail
                    });
                    while (_deadLetters.Count > 100) _deadLetters.RemoveAt(_deadLetters.Count - 1);
                }
            }
            else
            {
                var seconds = Math.Min(900, Math.Pow(2, Math.Max(0, item.Attempts - 1)) * 5);
                item.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
                item.LastError = result.Detail;
            }

            return new DeliveryOutcome(deliveryId, item.Payload.Notification, result, item.Attempts, deadLettered)
            {
                QueuedAt = queuedAt,
                CompletedAt = completedAt
            };
        }
    }

    private List<PendingDelivery> GetDueItems(DateTimeOffset now)
    {
        lock (_gate) return _items.Where(item => item.NextAttemptAt <= now).Select(item => item.Clone()).ToList();
    }

    private TimeSpan? GetNextDelay()
    {
        lock (_gate)
        {
            if (_items.Count == 0) return null;
            var next = _items.Min(item => item.NextAttemptAt) - DateTimeOffset.UtcNow;
            return next < TimeSpan.Zero ? TimeSpan.Zero : next;
        }
    }

    private async Task WaitForSignalAsync(TimeSpan? delay, CancellationToken cancellationToken)
    {
        if (delay is null)
        {
            await _wake.Reader.WaitToReadAsync(cancellationToken);
            while (_wake.Reader.TryRead(out _)) { }
            return;
        }

        var signalTask = _wake.Reader.WaitToReadAsync(cancellationToken).AsTask();
        var delayTask = Task.Delay(delay.Value, cancellationToken);
        await Task.WhenAny(signalTask, delayTask);
        while (_wake.Reader.TryRead(out _)) { }
    }

    private void Signal() => _wake.Writer.TryWrite(true);

    private async Task LoadAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
            var file = await folder.TryGetItemAsync(QueueFileName) as Windows.Storage.StorageFile;
            var deadLetterFile = await folder.TryGetItemAsync(DeadLetterFileName) as Windows.Storage.StorageFile;
            var loaded = file is null
                ? []
                : JsonSerializer.Deserialize(await Windows.Storage.FileIO.ReadTextAsync(file), AppJsonContext.Default.ListPendingDelivery) ?? [];
            var deadLetters = deadLetterFile is null
                ? []
                : JsonSerializer.Deserialize(await Windows.Storage.FileIO.ReadTextAsync(deadLetterFile), AppJsonContext.Default.ListDeadLetterDelivery) ?? [];
            lock (_gate)
            {
                _items.Clear();
                _items.AddRange(loaded);
                _deadLetters.Clear();
                _deadLetters.AddRange(deadLetters.Take(100));
            }
        }
        catch (JsonException)
        {
            // A corrupt queue must not prevent the app from starting.
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task SaveAsync()
    {
        List<PendingDelivery> snapshot;
        List<DeadLetterDelivery> deadLetters;
        lock (_gate)
        {
            snapshot = _items.Select(item => item.Clone()).ToList();
            deadLetters = _deadLetters.Select(item => item.Clone()).ToList();
        }
        await _fileLock.WaitAsync();
        try
        {
            var file = await Windows.Storage.ApplicationData.Current.LocalFolder.CreateFileAsync(QueueFileName, Windows.Storage.CreationCollisionOption.ReplaceExisting);
            await Windows.Storage.FileIO.WriteTextAsync(file, JsonSerializer.Serialize(snapshot, AppJsonContext.Default.ListPendingDelivery));
            var deadLetterFile = await Windows.Storage.ApplicationData.Current.LocalFolder.CreateFileAsync(DeadLetterFileName, Windows.Storage.CreationCollisionOption.ReplaceExisting);
            await Windows.Storage.FileIO.WriteTextAsync(deadLetterFile, JsonSerializer.Serialize(deadLetters, AppJsonContext.Default.ListDeadLetterDelivery));
        }
        finally
        {
            _fileLock.Release();
        }
    }

}
