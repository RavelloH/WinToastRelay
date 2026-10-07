using System.Text.Json;
using Windows.Storage;
using WinToastRelay.Models;

namespace WinToastRelay.Services;

public sealed class SettingsStore
{
    private const string FileName = "relay-settings.json";
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    public async Task<RelaySettings> LoadAsync()
    {
        try
        {
            var file = await ApplicationData.Current.LocalFolder.TryGetItemAsync(FileName) as StorageFile;
            if (file is null) return new RelaySettings();
            var json = await FileIO.ReadTextAsync(file);
            return JsonSerializer.Deserialize(json, AppJsonContext.Default.RelaySettings) ?? new RelaySettings();
        }
        catch (JsonException)
        {
            return new RelaySettings();
        }
    }

    public async Task SaveAsync(RelaySettings settings)
    {
        // Consent changes may be saved while another settings operation is awaiting
        // storage. Serialize saves so an older write cannot restore a revoked grant.
        await _saveLock.WaitAsync();
        try
        {
            var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(FileName, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, JsonSerializer.Serialize(settings, AppJsonContext.Default.RelaySettings));
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
