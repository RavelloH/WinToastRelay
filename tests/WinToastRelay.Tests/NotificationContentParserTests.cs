using WinToastRelay.Services;

namespace WinToastRelay.Tests;

public sealed class NotificationContentParserTests
{
    [Fact]
    public void CreateTrimsTextAndUsesFirstNonEmptyLineAsTitle()
    {
        var createdAt = DateTimeOffset.Parse("2026-10-07T12:34:56Z");

        var notification = NotificationContentParser.Create(23, " Calendar ",
            [null, "  ", " Meeting  ", " Starts soon ", "   ", "Bring notes"], createdAt,
            " com.example.calendar ");

        Assert.NotNull(notification);
        Assert.Equal((uint)23, notification.Id);
        Assert.Equal("Calendar", notification.App);
        Assert.Equal("Meeting", notification.Title);
        Assert.Equal($"Starts soon{Environment.NewLine}Bring notes", notification.Body);
        Assert.Equal(createdAt, notification.CreatedAt);
        Assert.Equal("com.example.calendar", notification.PackageName);
    }

    [Theory]
    [InlineData(null, null, "Unknown application", "")]
    [InlineData(" ", " com.example.reader ", "com.example.reader", "com.example.reader")]
    [InlineData(" News ", " com.example.news ", "News", "com.example.news")]
    public void CreateNormalizesApplicationAndPackageFallback(
        string? app, string? packageName, string expectedApp, string expectedPackage)
    {
        var notification = NotificationContentParser.Create(1, app, ["Title"], DateTimeOffset.UnixEpoch, packageName);

        Assert.NotNull(notification);
        Assert.Equal(expectedApp, notification.App);
        Assert.Equal(expectedPackage, notification.PackageName);
    }

    [Fact]
    public void CreateReturnsNullWhenNoReadableTextExists()
    {
        Assert.Null(NotificationContentParser.Create(1, "App", null, DateTimeOffset.UnixEpoch));
        Assert.Null(NotificationContentParser.Create(1, "App", [null, " ", "\t"], DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void ReadBatchContinuesAfterNullAndThrowingRowsAndKeepsFirstSafeFailure()
    {
        const string secret = "private notification https://example.test/?token=secret";
        var accepted = new List<uint>();
        var batch = NotificationContentParser.ReadBatch(
            ["first", "empty", "throws", "last"],
            row => row switch
            {
                "empty" => null,
                "throws" => throw new InvalidOperationException(secret),
                _ => NotificationContentParser.Create((uint)(row == "first" ? 1 : 4), "App", [row], DateTimeOffset.UnixEpoch)
            },
            notification => accepted.Add(notification.Id));

        Assert.Equal([1u, 4u], accepted);
        Assert.Equal(2, batch.Skipped);
        Assert.Equal("WTRD1|failure|stage=NotificationParsing|type=InvalidDataException|hr=80131501", batch.FirstFailure);
        Assert.True(RelayDiagnostics.IsDiagnostic(batch.FirstFailure));
        Assert.DoesNotContain(secret, batch.FirstFailure, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadBatchCountsAcceptCallbackFailuresAndContinues()
    {
        const string secret = "callback secret payload";
        var accepted = new List<uint>();

        var batch = NotificationContentParser.ReadBatch(
            [1, 2, 3],
            id => NotificationContentParser.Create((uint)id, "App", [$"Title {id}"], DateTimeOffset.UnixEpoch),
            notification =>
            {
                if (notification.Id == 2)
                    throw new InvalidOperationException(secret);
                accepted.Add(notification.Id);
            });

        Assert.Equal([1u, 3u], accepted);
        Assert.Equal(1, batch.Skipped);
        Assert.Equal("WTRD1|failure|stage=NotificationParsing|type=InvalidOperationException|hr=80131509", batch.FirstFailure);
        Assert.DoesNotContain(secret, batch.FirstFailure, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadBatchLetsWholeEnumerationFailuresBubbleForSnapshotHandling()
    {
        const string secret = "enumeration failure private detail";
        var accepted = new List<uint>();

        var error = Assert.Throws<InvalidOperationException>(() => NotificationContentParser.ReadBatch(
            ThrowDuringEnumeration(),
            id => NotificationContentParser.Create(id, "App", ["Title"], DateTimeOffset.UnixEpoch),
            notification => accepted.Add(notification.Id)));

        Assert.Equal((uint)1, Assert.Single(accepted));
        Assert.Equal(secret, error.Message);
    }

    private static IEnumerable<uint> ThrowDuringEnumeration()
    {
        yield return 1;
        throw new InvalidOperationException("enumeration failure private detail");
    }
}
