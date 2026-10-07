using System.Globalization;

namespace WinToastRelay.Services;

public enum RelayFailureStage
{
    Initialization,
    SettingsSave,
    RelayStart,
    RelayStop,
    ListenerInitialization,
    PermissionCheck,
    PermissionRequest,
    QueueStartup,
    ListenerSubscription,
    SnapshotEnumeration,
    NotificationParsing,
    ApplicationEnumeration,
    ApplicationMetadata,
    NotificationEvent,
    QueueEnqueue,
    ListenerUnsubscription,
    QueueShutdown,
    StartupCleanup
}

public static class RelayDiagnostics
{
    private const string Prefix = "WTRD1|";
    private const string PermissionDenied = "Notification access: Denied";
    private const string PermissionUnspecified = "Notification access: Unspecified";
    private const string FailureMarker = "failure|";
    private const string WarningMarker = "warning|";
    private const int MaximumTypeNameLength = 64;

    public static string Failure(RelayFailureStage stage, Exception exception, uint? notificationId = null)
    {
        if (exception is RelayDiagnosticException diagnosticException
            && TryParseFailure(diagnosticException.Diagnostic, out _))
            return diagnosticException.Diagnostic;

        var safeStage = Enum.IsDefined(stage) ? stage : RelayFailureStage.Initialization;
        var typeName = GetSafeExceptionType(exception);
        var hresult = exception?.HResult ?? 0;
        var record = string.Create(CultureInfo.InvariantCulture,
            $"{Prefix}{FailureMarker}stage={safeStage}|type={typeName}|hr={hresult:X8}");

        return notificationId.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"{record}|id={notificationId.Value}")
            : record;
    }

    public static string Localize(string? status, bool isChinese)
    {
        if (string.Equals(status, PermissionDenied, StringComparison.Ordinal))
            return isChinese
                ? "Windows 已拒绝通知访问。请检查 Windows 隐私设置中的通知访问权限；如果访问受组织策略限制，请联系管理员。"
                : "Windows is denying notification access. Check notification access in Windows privacy settings; if access is blocked by policy, contact your administrator.";

        if (string.Equals(status, PermissionUnspecified, StringComparison.Ordinal))
            return isChinese
                ? "通知访问请求已关闭或尚未确认。请重新打开主窗口并再次启用中继，以重新请求访问权限。"
                : "The notification access request was closed or not confirmed. Reopen the main window and enable the relay again to request access.";

        if (TryParseFailure(status, out var failure))
            return LocalizeFailure(failure, isChinese);

        if (TryParseWarning(status, out var warning))
            return LocalizeWarning(warning, isChinese);

        return isChinese
            ? "中继状态不可用。请检查通知访问权限并重新启动中继。"
            : "Relay status is unavailable. Check notification access and restart the relay.";
    }

    public static bool IsDiagnostic(string? status)
    {
        return status is not null && (status.StartsWith(Prefix, StringComparison.Ordinal)
            || string.Equals(status, PermissionDenied, StringComparison.Ordinal)
            || string.Equals(status, PermissionUnspecified, StringComparison.Ordinal)
            || TryParseFailure(status, out _)
            || TryParseWarning(status, out _));
    }

    public static string Warning(int skipped, string? firstFailure)
    {
        var safeSkipped = Math.Max(0, skipped);
        if (!TryParseFailure(firstFailure, out var failure))
            failure = new ParsedFailure(RelayFailureStage.Initialization, "Exception", "00000000", null);

        return string.Create(CultureInfo.InvariantCulture,
            $"{Prefix}{WarningMarker}skipped={safeSkipped}|stage={failure.Stage}|type={failure.TypeName}|hr={failure.HResult}");
    }

    private static string LocalizeFailure(ParsedFailure failure, bool isChinese)
    {
        var stage = GetStageLabel(failure.Stage, isChinese);
        var notification = failure.NotificationId.HasValue
            ? isChinese
                ? $" 通知 ID：{failure.NotificationId.Value.ToString(CultureInfo.InvariantCulture)}。"
                : $" Notification ID: {failure.NotificationId.Value.ToString(CultureInfo.InvariantCulture)}."
            : string.Empty;
        var code = $"{failure.TypeName}, HRESULT 0x{failure.HResult}";
        var guidance = GetNextAction(failure.Stage, isChinese);

        return isChinese
            ? $"中继在“{stage}”阶段失败（{code}）。{notification}{guidance}"
            : $"Relay failed during {stage} ({code}).{notification} {guidance}";
    }

    private static string LocalizeWarning(ParsedWarning warning, bool isChinese)
    {
        var stage = GetStageLabel(warning.Stage, isChinese);
        var code = $"{warning.TypeName}, HRESULT 0x{warning.HResult}";
        var guidance = GetNextAction(warning.Stage, isChinese);

        return isChinese
            ? $"中继已启动，但有警告：在“{stage}”阶段发生故障，跳过了 {warning.Skipped.ToString(CultureInfo.InvariantCulture)} 条通知（{code}）。{guidance}"
            : $"Relay started with a warning: {warning.Skipped.ToString(CultureInfo.InvariantCulture)} notifications were skipped after {stage} failed ({code}). {guidance}";
    }

    private static string GetStageLabel(RelayFailureStage stage, bool isChinese) => (stage, isChinese) switch
    {
        (RelayFailureStage.Initialization, false) => "initialization",
        (RelayFailureStage.SettingsSave, false) => "settings save",
        (RelayFailureStage.RelayStart, false) => "relay start",
        (RelayFailureStage.RelayStop, false) => "relay stop",
        (RelayFailureStage.ListenerInitialization, false) => "listener initialization",
        (RelayFailureStage.PermissionCheck, false) => "permission check",
        (RelayFailureStage.PermissionRequest, false) => "permission request",
        (RelayFailureStage.QueueStartup, false) => "queue startup",
        (RelayFailureStage.ListenerSubscription, false) => "listener subscription",
        (RelayFailureStage.SnapshotEnumeration, false) => "notification snapshot enumeration",
        (RelayFailureStage.NotificationParsing, false) => "notification parsing",
        (RelayFailureStage.ApplicationEnumeration, false) => "application enumeration",
        (RelayFailureStage.ApplicationMetadata, false) => "application metadata lookup",
        (RelayFailureStage.NotificationEvent, false) => "notification event handling",
        (RelayFailureStage.QueueEnqueue, false) => "notification queueing",
        (RelayFailureStage.ListenerUnsubscription, false) => "listener unsubscription",
        (RelayFailureStage.QueueShutdown, false) => "queue shutdown",
        (RelayFailureStage.StartupCleanup, false) => "startup cleanup",
        (RelayFailureStage.Initialization, true) => "初始化",
        (RelayFailureStage.SettingsSave, true) => "保存设置",
        (RelayFailureStage.RelayStart, true) => "启动中继",
        (RelayFailureStage.RelayStop, true) => "停止中继",
        (RelayFailureStage.ListenerInitialization, true) => "初始化侦听器",
        (RelayFailureStage.PermissionCheck, true) => "检查权限",
        (RelayFailureStage.PermissionRequest, true) => "请求权限",
        (RelayFailureStage.QueueStartup, true) => "启动队列",
        (RelayFailureStage.ListenerSubscription, true) => "订阅侦听器",
        (RelayFailureStage.SnapshotEnumeration, true) => "枚举通知快照",
        (RelayFailureStage.NotificationParsing, true) => "解析通知",
        (RelayFailureStage.ApplicationEnumeration, true) => "枚举应用",
        (RelayFailureStage.ApplicationMetadata, true) => "读取应用信息",
        (RelayFailureStage.NotificationEvent, true) => "处理通知事件",
        (RelayFailureStage.QueueEnqueue, true) => "将通知加入队列",
        (RelayFailureStage.ListenerUnsubscription, true) => "取消侦听器订阅",
        (RelayFailureStage.QueueShutdown, true) => "关闭队列",
        (RelayFailureStage.StartupCleanup, true) => "清理启动状态",
        _ => isChinese ? "未知阶段" : "unknown stage"
    };

    private static string GetNextAction(RelayFailureStage stage, bool isChinese)
    {
        var settingsStage = stage == RelayFailureStage.SettingsSave;
        var permissionStage = stage is RelayFailureStage.PermissionCheck or RelayFailureStage.PermissionRequest;
        var cleanupStage = stage is RelayFailureStage.RelayStop
            or RelayFailureStage.ListenerUnsubscription
            or RelayFailureStage.QueueShutdown
            or RelayFailureStage.StartupCleanup;

        if (isChinese)
        {
            if (settingsStage)
                return "请检查可用存储空间和设置权限，然后重试保存。";
            if (permissionStage)
                return "请检查 Windows 隐私设置中的通知访问权限，然后重新启动中继。";
            if (cleanupStage)
                return "请重新打开应用；如果问题再次出现，请检查通知访问权限。";
            return "请检查 Windows 通知访问设置并重新启动中继；如果问题仍然存在，报告问题时请附上此诊断信息、Windows 版本和应用版本。";
        }

        if (settingsStage)
            return "Check available storage and settings permissions, then try saving again.";
        if (permissionStage)
            return "Check notification access in Windows privacy settings, then restart the relay.";
        if (cleanupStage)
            return "Reopen the app; if the problem returns, check notification access.";
        return "Check Windows notification access settings and restart the relay; if the problem persists, include this diagnostic information, your Windows version, and your app version when reporting the issue.";
    }

    private static string GetSafeExceptionType(Exception? exception)
    {
        var rawName = exception?.GetType().Name;
        if (string.IsNullOrEmpty(rawName))
            return "Exception";

        Span<char> safeName = stackalloc char[Math.Min(rawName.Length, MaximumTypeNameLength)];
        var count = 0;
        foreach (var character in rawName)
        {
            if (count == safeName.Length)
                break;

            safeName[count++] = IsSafeTypeCharacter(character) ? character : '_';
        }

        return count == 0 ? "Exception" : new string(safeName[..count]);
    }

    private static bool IsSafeTypeCharacter(char character) =>
        character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '_' or '.' or '+' or '`';

    private static bool TryParseFailure(string? status, out ParsedFailure failure)
    {
        failure = default;
        if (status is null || !status.StartsWith(Prefix + FailureMarker, StringComparison.Ordinal))
            return false;

        var fields = status[(Prefix.Length + FailureMarker.Length)..].Split('|');
        if ((fields.Length != 3 && fields.Length != 4)
            || !TryReadStage(fields[0], out var stage)
            || !TryReadType(fields[1], out var typeName)
            || !TryReadHResult(fields[2], out var hresult))
            return false;

        uint? notificationId = null;
        if (fields.Length == 4)
        {
            if (!fields[3].StartsWith("id=", StringComparison.Ordinal)
                || !uint.TryParse(fields[3].AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedId)
                || !IsCanonicalUnsigned(fields[3].AsSpan(3)))
                return false;

            notificationId = parsedId;
        }

        failure = new ParsedFailure(stage, typeName, hresult, notificationId);
        return true;
    }

    private static bool TryParseWarning(string? status, out ParsedWarning warning)
    {
        warning = default;
        if (status is null || !status.StartsWith(Prefix + WarningMarker, StringComparison.Ordinal))
            return false;

        var fields = status[(Prefix.Length + WarningMarker.Length)..].Split('|');
        if (fields.Length != 4
            || !fields[0].StartsWith("skipped=", StringComparison.Ordinal)
            || !int.TryParse(fields[0].AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture, out var skipped)
            || skipped < 0
            || !IsCanonicalUnsigned(fields[0].AsSpan(8))
            || !TryReadStage(fields[1], out var stage)
            || !TryReadType(fields[2], out var typeName)
            || !TryReadHResult(fields[3], out var hresult))
            return false;

        warning = new ParsedWarning(skipped, stage, typeName, hresult);
        return true;
    }

    private static bool TryReadStage(string field, out RelayFailureStage stage)
    {
        stage = default;
        if (!field.StartsWith("stage=", StringComparison.Ordinal))
            return false;

        var name = field.AsSpan(6);
        if (!Enum.TryParse(name, ignoreCase: false, out stage) || !Enum.IsDefined(stage))
            return false;

        return string.Equals(stage.ToString(), name.ToString(), StringComparison.Ordinal);
    }

    private static bool TryReadType(string field, out string typeName)
    {
        typeName = string.Empty;
        if (!field.StartsWith("type=", StringComparison.Ordinal))
            return false;

        var name = field.AsSpan(5);
        if (name.Length is < 1 or > MaximumTypeNameLength)
            return false;

        foreach (var character in name)
        {
            if (!IsSafeTypeCharacter(character))
                return false;
        }

        typeName = name.ToString();
        return true;
    }

    private static bool TryReadHResult(string field, out string hresult)
    {
        hresult = string.Empty;
        if (!field.StartsWith("hr=", StringComparison.Ordinal))
            return false;

        var value = field.AsSpan(3);
        if (value.Length != 8)
            return false;

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9' or >= 'A' and <= 'F'))
                return false;
        }

        hresult = value.ToString();
        return true;
    }

    private static bool IsCanonicalUnsigned(ReadOnlySpan<char> value) =>
        value.Length > 0 && (value.Length == 1 || value[0] != '0');

    private readonly record struct ParsedFailure(
        RelayFailureStage Stage,
        string TypeName,
        string HResult,
        uint? NotificationId);

    private readonly record struct ParsedWarning(
        int Skipped,
        RelayFailureStage Stage,
        string TypeName,
        string HResult);
}

internal sealed class RelayDiagnosticException : Exception
{
    public RelayDiagnosticException(string diagnostic)
        : base("A relay diagnostic was raised during cleanup.")
    {
        Diagnostic = diagnostic;
    }

    public string Diagnostic { get; }
}
