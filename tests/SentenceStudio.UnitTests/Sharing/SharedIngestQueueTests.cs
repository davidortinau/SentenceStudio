using FluentAssertions;
using SentenceStudio.Sharing;

namespace SentenceStudio.UnitTests.Sharing;

public sealed class SharedIngestQueueTests : IDisposable
{
    private readonly string _testDir;
    private readonly FileSystemSharedIngestQueue _queue;

    public SharedIngestQueueTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
        _queue = new FileSystemSharedIngestQueue(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    [Fact]
    public void Enqueue_Then_List_RoundTrips_TextItem_WithAllFields()
    {
        var item = new SharedIngestItem
        {
            Kind = SharedIngestKind.Text,
            Payload = "Hello, world",
            SourceAppBundleId = "com.example.app",
            CapturedAtUtc = new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc),
            SchemaVersion = 1
        };

        _queue.Enqueue(item);
        var results = _queue.List();

        results.Should().HaveCount(1);
        var roundTripped = results[0];
        roundTripped.Id.Should().Be(item.Id);
        roundTripped.Kind.Should().Be(SharedIngestKind.Text);
        roundTripped.Payload.Should().Be("Hello, world");
        roundTripped.SourceAppBundleId.Should().Be("com.example.app");
        roundTripped.CapturedAtUtc.Should().Be(item.CapturedAtUtc);
        roundTripped.SchemaVersion.Should().Be(1);
    }

    [Fact]
    public void Enqueue_Then_List_RoundTrips_UrlItem_WithNullBundleId()
    {
        var item = new SharedIngestItem
        {
            Kind = SharedIngestKind.Url,
            Payload = "https://example.com/article",
            SourceAppBundleId = null,
            CapturedAtUtc = new DateTime(2026, 6, 25, 9, 0, 0, DateTimeKind.Utc)
        };

        _queue.Enqueue(item);
        var results = _queue.List();

        results.Should().HaveCount(1);
        results[0].Kind.Should().Be(SharedIngestKind.Url);
        results[0].SourceAppBundleId.Should().BeNull();
    }

    [Fact]
    public void List_ReturnsItems_OrderedByCapturedAtUtc_Ascending()
    {
        var early = new SharedIngestItem
        {
            Payload = "first",
            CapturedAtUtc = new DateTime(2026, 6, 25, 8, 0, 0, DateTimeKind.Utc)
        };
        var middle = new SharedIngestItem
        {
            Payload = "second",
            CapturedAtUtc = new DateTime(2026, 6, 25, 9, 0, 0, DateTimeKind.Utc)
        };
        var late = new SharedIngestItem
        {
            Payload = "third",
            CapturedAtUtc = new DateTime(2026, 6, 25, 10, 0, 0, DateTimeKind.Utc)
        };

        // Enqueue out of order
        _queue.Enqueue(late);
        _queue.Enqueue(early);
        _queue.Enqueue(middle);

        var results = _queue.List();

        results.Should().HaveCount(3);
        results[0].Payload.Should().Be("first");
        results[1].Payload.Should().Be("second");
        results[2].Payload.Should().Be("third");
    }

    [Fact]
    public void List_SkipsMalformedFile_AndReturnsValidOnes()
    {
        var good = new SharedIngestItem
        {
            Payload = "valid item",
            CapturedAtUtc = new DateTime(2026, 6, 25, 9, 0, 0, DateTimeKind.Utc)
        };
        _queue.Enqueue(good);

        // Drop a garbage file that matches the *.json glob
        File.WriteAllText(Path.Combine(_testDir, "corrupt.json"), "{ not valid json ][");

        var results = _queue.List();

        results.Should().HaveCount(1);
        results[0].Id.Should().Be(good.Id);
    }

    [Fact]
    public void Remove_DeletesItem_AndSubsequentList_OmitsIt()
    {
        var item = new SharedIngestItem
        {
            Payload = "to be removed",
            CapturedAtUtc = DateTime.UtcNow
        };
        _queue.Enqueue(item);
        _queue.List().Should().HaveCount(1);

        var removed = _queue.Remove(item.Id);

        removed.Should().BeTrue();
        _queue.List().Should().BeEmpty();
    }

    [Fact]
    public void Remove_ReturnsFalse_ForUnknownId()
    {
        var result = _queue.Remove("nonexistent-id-00000000");

        result.Should().BeFalse();
    }

    [Fact]
    public void Enqueue_WritesOnlyFinalFile_NoLeftoverTempFile()
    {
        var item = new SharedIngestItem
        {
            Payload = "atomic write test",
            CapturedAtUtc = DateTime.UtcNow
        };

        _queue.Enqueue(item);

        var allFiles = Directory.GetFiles(_testDir);
        allFiles.Should().HaveCount(1);
        allFiles[0].Should().EndWith($"{item.Id}.json");
    }

    [Fact]
    public void DirectoryAutoCreated_WhenItDoesNotExist()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"), "deeply", "nested");

        try
        {
            var queue = new FileSystemSharedIngestQueue(nonExistent);
            var item = new SharedIngestItem { Payload = "test", CapturedAtUtc = DateTime.UtcNow };

            queue.Enqueue(item);
            queue.List().Should().HaveCount(1);
        }
        finally
        {
            // Clean up the parent of the 3-level path we created
            var root = Path.Combine(Path.GetTempPath(), nonExistent.Split(Path.DirectorySeparatorChar)[^3]);
            if (Directory.Exists(nonExistent))
                Directory.Delete(nonExistent, recursive: true);
        }
    }

    [Fact]
    public void SchemaVersion_DefaultsToOne_AndRoundTrips()
    {
        var item = new SharedIngestItem
        {
            Payload = "schema version test",
            CapturedAtUtc = DateTime.UtcNow
        };

        item.SchemaVersion.Should().Be(SharingConstants.CurrentSchemaVersion);

        _queue.Enqueue(item);
        var roundTripped = _queue.List()[0];

        roundTripped.SchemaVersion.Should().Be(1);
    }
}
