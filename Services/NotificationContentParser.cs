using WinToastRelay.Models;

namespace WinToastRelay.Services;

/// <summary>Normalizes optional notification metadata without depending on WinRT.</summary>
internal static class NotificationContentParser
{
    internal static (int Skipped, string FirstFailure) ReadBatch<T>(IEnumerable<T> notifications,
        Func<T, RelayNotification?> read, Action<RelayNotification> accept)
    {
        var skipped = 0;
        var firstFailure = string.Empty;
        foreach (var notification in notifications)
        {
            try
            {
                var value = read(notification);
                if (value is null) throw new InvalidDataException("Notification has no readable text");
                accept(value);
            }
            catch (Exception ex)
            {
                skipped++;
                if (string.IsNullOrEmpty(firstFailure))
                    firstFailure = RelayDiagnostics.Failure(RelayFailureStage.NotificationParsing, ex);
            }
        }
        return (skipped, firstFailure);
    }

    internal static RelayNotification? Create(uint id, string? app, IEnumerable<string?>? text,
        DateTimeOffset createdAt, string? packageName = null)
    {
        var parts = text?.Select(value => value?.Trim())
            .Where(value => !string.IsNullOrEmpty(value)).ToArray() ?? [];
        if (parts.Length == 0) return null;

        var package = packageName?.Trim() ?? string.Empty;
        var application = app?.Trim();
        if (string.IsNullOrEmpty(application))
            application = string.IsNullOrEmpty(package) ? "Unknown application" : package;

        return new RelayNotification(id, application, parts[0]!,
            string.Join(Environment.NewLine, parts.Skip(1)), createdAt) { PackageName = package };
    }
}
