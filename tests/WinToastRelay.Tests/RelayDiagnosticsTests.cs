using System.Globalization;
using WinToastRelay.Services;

namespace WinToastRelay.Tests;

public sealed class RelayDiagnosticsTests
{
    [Fact]
    public void FailureIsCanonicalAndContainsOnlySafeStructuredFields()
    {
        const string secret = "notification-body https://example.test/?token=private";
        var exception = new InvalidOperationException(secret);
        exception.HResult = unchecked((int)0x81234567);

        var status = RelayDiagnostics.Failure(RelayFailureStage.NotificationParsing, exception, 42);

        Assert.Equal("WTRD1|failure|stage=NotificationParsing|type=InvalidOperationException|hr=81234567|id=42", status);
        Assert.DoesNotContain(secret, status, StringComparison.Ordinal);
        Assert.DoesNotContain("example.test", status, StringComparison.Ordinal);
        Assert.True(RelayDiagnostics.IsDiagnostic(status));
    }

    [Fact]
    public void FailureOmitsAbsentNotificationIdAndUsesInvariantFormatting()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            var status = RelayDiagnostics.Failure(RelayFailureStage.SettingsSave,
                new ArgumentException("sensitive detail"));

            Assert.Equal("WTRD1|failure|stage=SettingsSave|type=ArgumentException|hr=80070057", status);
            Assert.DoesNotContain("id=", status, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void LocalizeIncludesStageTypeCodeOptionalIdAndNextActionInBothLanguages()
    {
        var status = RelayDiagnostics.Failure(RelayFailureStage.PermissionRequest,
            new InvalidOperationException("must not appear"), 9001);

        var english = RelayDiagnostics.Localize(status, isChinese: false);
        var chinese = RelayDiagnostics.Localize(status, isChinese: true);

        Assert.Contains("permission request", english, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", english, StringComparison.Ordinal);
        Assert.Contains("HRESULT 0x", english, StringComparison.Ordinal);
        Assert.Contains("Notification ID: 9001", english, StringComparison.Ordinal);
        Assert.Contains("Windows privacy settings", english, StringComparison.Ordinal);
        Assert.Contains("请求权限", chinese, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", chinese, StringComparison.Ordinal);
        Assert.Contains("HRESULT 0x", chinese, StringComparison.Ordinal);
        Assert.Contains("通知 ID：9001", chinese, StringComparison.Ordinal);
        Assert.Contains("Windows 隐私设置", chinese, StringComparison.Ordinal);
        Assert.DoesNotContain("must not appear", english, StringComparison.Ordinal);
        Assert.DoesNotContain("must not appear", chinese, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalizeUsesSafeFallbackForMalformedOrUnrecognizedData()
    {
        var malformed = new[]
        {
            "WTRD1|failure|stage=MadeUp|type=Exception|hr=00000000",
            "WTRD1|failure|stage=RelayStart|type=Exception|hr=0000000a",
            "WTRD1|failure|stage=RelayStart|type=Exception|hr=00000000|id=01",
            "WTRD1|failure|stage=RelayStart|type=Exception|hr=00000000|unexpected=leak",
            "WTRD1|warning|skipped=-1|stage=RelayStart|type=Exception|hr=00000000",
            "WTRD1|warning|skipped=1|stage=RelayStart|type=Exception|hr=00000000|token=private",
            "raw exception text with https://example.test/?token=private"
        };

        foreach (var status in malformed)
        {
            var english = RelayDiagnostics.Localize(status, isChinese: false);
            var chinese = RelayDiagnostics.Localize(status, isChinese: true);

            Assert.Equal(status.StartsWith("WTRD1|", StringComparison.Ordinal), RelayDiagnostics.IsDiagnostic(status));
            Assert.DoesNotContain("example.test", english, StringComparison.Ordinal);
            Assert.DoesNotContain("token=private", english, StringComparison.Ordinal);
            Assert.DoesNotContain("example.test", chinese, StringComparison.Ordinal);
            Assert.DoesNotContain("token=private", chinese, StringComparison.Ordinal);
            Assert.Contains("status is unavailable", english, StringComparison.Ordinal);
            Assert.Contains("状态不可用", chinese, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DeniedPermissionStatusIsRecognizedAndExplainsPolicyHandling()
    {
        const string status = "Notification access: Denied";
        Assert.True(RelayDiagnostics.IsDiagnostic(status));
        Assert.Contains("Windows privacy settings", RelayDiagnostics.Localize(status, false), StringComparison.Ordinal);
        Assert.Contains("Windows 隐私设置", RelayDiagnostics.Localize(status, true), StringComparison.Ordinal);
        Assert.Contains("policy", RelayDiagnostics.Localize(status, false), StringComparison.Ordinal);
    }

    [Fact]
    public void UnspecifiedPermissionStatusExplainsHowToRetryTheRequest()
    {
        const string status = "Notification access: Unspecified";
        Assert.True(RelayDiagnostics.IsDiagnostic(status));
        Assert.Contains("Reopen the main window", RelayDiagnostics.Localize(status, false), StringComparison.Ordinal);
        Assert.Contains("重新打开主窗口", RelayDiagnostics.Localize(status, true), StringComparison.Ordinal);
    }

    [Fact]
    public void WarningKeepsOnlyValidatedFirstFailureFields()
    {
        const string secret = "token=private notification body https://example.test";
        var firstFailure = RelayDiagnostics.Failure(RelayFailureStage.ApplicationMetadata,
            new IOException(secret), 700);

        var warning = RelayDiagnostics.Warning(3, firstFailure);
        var localized = RelayDiagnostics.Localize(warning, isChinese: false);
        var localizedChinese = RelayDiagnostics.Localize(warning, isChinese: true);

        Assert.Equal("WTRD1|warning|skipped=3|stage=ApplicationMetadata|type=IOException|hr=80131620", warning);
        Assert.True(RelayDiagnostics.IsDiagnostic(warning));
        Assert.Contains("3 notifications were skipped", localized, StringComparison.Ordinal);
        Assert.Contains("application metadata lookup", localized, StringComparison.Ordinal);
        Assert.Contains("IOException", localized, StringComparison.Ordinal);
        Assert.Contains("HRESULT 0x80131620", localized, StringComparison.Ordinal);
        Assert.Contains("跳过了 3 条通知", localizedChinese, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, warning, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, localized, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, localizedChinese, StringComparison.Ordinal);
    }

    [Fact]
    public void WarningFallsBackToSafeFieldsForArbitraryInputAndClampsNegativeCount()
    {
        var warning = RelayDiagnostics.Warning(-5, "private token=do-not-leak https://example.test");

        Assert.Equal("WTRD1|warning|skipped=0|stage=Initialization|type=Exception|hr=00000000", warning);
        Assert.DoesNotContain("private", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("example.test", warning, StringComparison.Ordinal);
        Assert.True(RelayDiagnostics.IsDiagnostic(warning));
    }

    [Fact]
    public void FailurePreservesOnlyValidatedDiagnosticExceptions()
    {
        const string canonical = "WTRD1|failure|stage=QueueShutdown|type=IOException|hr=80131620";
        var wrapped = new RelayDiagnosticException(canonical);
        Assert.Equal(canonical, RelayDiagnostics.Failure(RelayFailureStage.RelayStop, wrapped));

        const string injected = "WTRD1|failure|stage=QueueShutdown|type=IOException|hr=80131620|token=private";
        var malformed = new RelayDiagnosticException(injected);
        var safe = RelayDiagnostics.Failure(RelayFailureStage.RelayStop, malformed);

        Assert.Equal("WTRD1|failure|stage=RelayStop|type=RelayDiagnosticException|hr=80131500", safe);
        Assert.DoesNotContain("token=private", safe, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalParserRejectsDelimiterInjectionAndInvalidIds()
    {
        var malformed = new[]
        {
            "WTRD1|failure|stage=RelayStart|type=Exception|hr=00000000|id=4294967296",
            "WTRD1|failure|stage=RelayStart|type=Exception|hr=00000000|id=+2",
            "WTRD1|failure|stage=RelayStart|type=Exception|hr=00000000|id=2|id=3",
            "WTRD1|failure|stage=RelayStart|type=Exception|hr=00000000|type=Leaked",
            "WTRD1|warning|skipped=01|stage=RelayStart|type=Exception|hr=00000000",
            "WTRD1|warning|skipped=1|stage=RelayStart|type=Exception|hr=0000000z"
        };

        foreach (var status in malformed)
        {
            Assert.True(RelayDiagnostics.IsDiagnostic(status));
            Assert.Contains("status is unavailable", RelayDiagnostics.Localize(status, false), StringComparison.Ordinal);
        }
    }
}
