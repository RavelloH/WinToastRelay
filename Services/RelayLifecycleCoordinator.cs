using WinToastRelay.Models;

namespace WinToastRelay.Services;

/// <summary>
/// Serializes both directions of the lifecycle, including permission requests and cleanup.
/// Awaiting this gate preserves the caller's UI context for WinRT consent APIs.
/// </summary>
internal sealed class RelayLifecycleCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _isRunning;

    internal bool IsRunning => _isRunning;

    internal async Task<DeliveryResult> StartAsync(Func<Task<DeliveryResult>> start)
    {
        await _gate.WaitAsync();
        try
        {
            if (_isRunning) return new DeliveryResult(true, "Already running");
            var result = await start();
            _isRunning = result.Succeeded;
            return result;
        }
        finally { _gate.Release(); }
    }

    internal async Task StopAsync(Func<Task> stop)
    {
        await _gate.WaitAsync();
        try { await stop(); }
        finally
        {
            _isRunning = false;
            _gate.Release();
        }
    }
}
